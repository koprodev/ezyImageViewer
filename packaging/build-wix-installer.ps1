#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Development')]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$EulaRtf,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('10.0.19041.0')][string]$MinVersion = '10.0.19041.0',
    [Parameter(ParameterSetName = 'Development', Mandatory)][switch]$DevelopmentUnsigned,
    [Parameter(ParameterSetName = 'Production', Mandatory)][string]$CertificateThumbprint,
    [Parameter(ParameterSetName = 'Production', Mandatory)][uri]$TimestampUrl,
    [Parameter(ParameterSetName = 'Production', Mandatory)][string]$BuildToolsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'external-location-helpers.ps1')
. (Join-Path $scriptRoot 'identity-registration-backend.ps1')
. (Join-Path $scriptRoot 'release-helpers.ps1')

function Invoke-Checked([string]$Label, [scriptblock]$Operation) {
    & $Operation
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Get-PhysicalFile([string]$Path, [string]$Label, [string]$Extension) {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Extension -cne $Extension) {
        throw "$Label must be a physical $Extension file: '$Path'."
    }
    return $item
}

function Get-GeneratedPayloadFileCount([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path), $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }

    $files = @($document.SelectNodes(
            "/*[local-name()='Wix']/*[local-name()='Fragment']" +
            "/*[local-name()='ComponentGroup' and @Id='ApplicationPayload']" +
            "/*[local-name()='Component']/*[local-name()='File']"))
    if ($files.Count -eq 0) {
        throw "Generated WiX payload contains no files: '$Path'."
    }
    return $files.Count
}

function ConvertTo-Rtf([string]$SourcePath, [string]$DestinationPath) {
    $text = [IO.File]::ReadAllText($SourcePath, [Text.Encoding]::UTF8)
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append("{\rtf1\ansi\deff0\uc1`r`n{\fonttbl{\f0 Segoe UI;}}`r`n\fs20`r`n")
    foreach ($character in $text.ToCharArray()) {
        switch ([int]$character) {
            13 { continue }
            10 { [void]$builder.Append("\par`r`n"); continue }
            92 { [void]$builder.Append('\\'); continue }
            123 { [void]$builder.Append('\{'); continue }
            125 { [void]$builder.Append('\}'); continue }
        }
        $value = [int]$character
        if ($value -ge 32 -and $value -le 126) {
            [void]$builder.Append($character)
        }
        else {
            $signed = if ($value -gt 32767) { $value - 65536 } else { $value }
            [void]$builder.Append("\u${signed}?")
        }
    }
    [void]$builder.Append("`r`n}`r`n")
    [IO.File]::WriteAllText($DestinationPath, $builder.ToString(),
        [Text.ASCIIEncoding]::new())
}

function Get-WixTool([string]$ProjectPath) {
    $project = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ProjectPath)) -Force `
        -ErrorAction Stop
    $assetsPath = Join-Path $project.Directory.FullName 'obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw -ErrorAction Stop | ConvertFrom-Json
    $candidates = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            Join-Path $_ 'wixtoolset.sdk\7.0.0\tools\net472\x64\wix.exe'
        } | Where-Object { [IO.File]::Exists($_) } | Sort-Object -Unique)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one restored WiX 7.0.0 x64 tool; found $($candidates.Count)."
    }
    return (Get-PhysicalFile $candidates[0] 'WiX tool' '.exe').FullName
}

function Get-SigningCertificate([string]$Thumbprint, [string]$Subject) {
    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    $now = Get-Date
    $certificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
            $_.Thumbprint -ceq $normalized -and $_.Subject -ceq $Subject -and
            $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now -and
            (Test-EzyCodeSigningCertificate $_)
        })
    if ($certificates.Count -ne 1) {
        throw 'CertificateThumbprint must identify one valid current-user code-signing certificate whose subject equals Publisher.'
    }
    return $certificates[0]
}

function Sign-Artifact(
    [string]$SignTool,
    [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [uri]$Timestamp,
    [string]$Path
) {
    & $SignTool sign /fd SHA256 /sha1 $Certificate.Thumbprint /tr $Timestamp.AbsoluteUri `
        /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for '$Path'." }
    & $SignTool verify /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for '$Path'." }
    Assert-EzyArtifactSignature -Path $Path -ExpectedThumbprint $Certificate.Thumbprint
}

function Sign-BurnBundle(
    [string]$WixTool,
    [string]$SignTool,
    [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [uri]$Timestamp,
    [string]$BundlePath,
    [string]$WorkingDirectory
) {
    $engine = Join-Path $WorkingDirectory 'burn-engine.exe'
    $reattachedBundle = Join-Path $WorkingDirectory 'burn-reattached.exe'
    & $WixTool -acceptEula wix7 burn detach $BundlePath -engine $engine
    if ($LASTEXITCODE -ne 0) { throw 'Detaching the Burn engine failed.' }
    Sign-Artifact $SignTool $Certificate $Timestamp $engine
    & $WixTool -acceptEula wix7 burn reattach $BundlePath -engine $engine -o $reattachedBundle
    if ($LASTEXITCODE -ne 0) { throw 'Reattaching the signed Burn engine failed.' }
    [void](Get-PhysicalFile $reattachedBundle 'Reattached Burn bundle' '.exe')
    [IO.File]::Copy($reattachedBundle, $BundlePath, $true)
    Sign-Artifact $SignTool $Certificate $Timestamp $BundlePath
}

Assert-EzyExternalFourPartVersion $Version 'Version'
Assert-EzyExternalPublisher $Publisher
Assert-EzyExternalMinVersion $MinVersion
$versionParts = $Version.Split('.')
$productVersion = ($versionParts[0..2] -join '.')
$eula = Get-PhysicalFile $EulaRtf 'EulaRtf' '.rtf'
$themeSource = Get-PhysicalFile (Join-Path $repositoryRoot `
        'installer\bundle\EzyRtfLargeTheme.xml') 'Burn theme source' '.xml'
$bundleLocalization = Get-PhysicalFile (Join-Path $repositoryRoot `
        'installer\bundle\Bundle.en-US.wxl') 'Burn fallback localization' '.wxl'
$koreanLocalization = Get-PhysicalFile (Join-Path $repositoryRoot `
        'installer\bundle\Bundle.ko-KR.wxl') 'Burn Korean localization' '.wxl'
$koreanEulaText = Get-PhysicalFile (Join-Path $repositoryRoot `
        'installer\assets\EULA.ko-KR.txt') 'Burn Korean EULA source' '.txt'
$themeLicense = Get-PhysicalFile (Join-Path $repositoryRoot `
        'installer\bundle\LICENSE-MRL.txt') 'Burn theme license' '.txt'
$signTool = $null
$certificate = $null
if (-not $DevelopmentUnsigned) {
    Assert-EzyProductionTimestampUrl $TimestampUrl
    $pinnedBuildToolsRoot = Get-EzyPinnedBuildToolsRoot `
        -RepositoryRoot $repositoryRoot -ExplicitRoot $BuildToolsRoot
    $signTool = Get-EzyMicrosoftSignTool $pinnedBuildToolsRoot
    $certificate = Get-SigningCertificate $CertificateThumbprint $Publisher
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
    throw "OutputDirectory already exists: '$output'."
}
$outputParentPath = [IO.Path]::GetDirectoryName($output)
[void][IO.Directory]::CreateDirectory($outputParentPath)
$outputParent = Get-Item -LiteralPath $outputParentPath -Force -ErrorAction Stop
if (-not $outputParent.PSIsContainer -or
    ($outputParent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'OutputDirectory parent must be a physical directory.'
}

$generatedRoot = Join-Path $repositoryRoot 'installer\generated'
[void][IO.Directory]::CreateDirectory($generatedRoot)
$generatedItem = Get-Item -LiteralPath $generatedRoot -Force -ErrorAction Stop
if (-not $generatedItem.PSIsContainer -or
    ($generatedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'installer/generated must be a physical directory.'
}
$workingRoot = Join-Path $generatedItem.FullName `
    ('.wix-build.' + [Guid]::NewGuid().ToString('N'))
$foundation = Join-Path $workingRoot 'foundation'
$identityRoot = Join-Path $workingRoot 'identity'
$perUserFragment = Join-Path $workingRoot 'Payload.PerUser.wxs'
$perMachineFragment = Join-Path $workingRoot 'Payload.PerMachine.wxs'

try {
    [void][IO.Directory]::CreateDirectory($workingRoot)
    $koreanEula = Join-Path $workingRoot 'license.ko-KR.rtf'
    ConvertTo-Rtf $koreanEulaText.FullName $koreanEula
    [void](Get-PhysicalFile $koreanEula 'Generated Korean EULA' '.rtf')
    & (Join-Path $scriptRoot 'stage-msi-foundation.ps1') `
        -OutputDirectory $foundation -Version $Version `
        -Publisher $Publisher -MinVersion $MinVersion
    if ($LASTEXITCODE -ne 0) { throw 'MSI foundation staging failed.' }
    & (Join-Path $scriptRoot 'verify-msi-foundation.ps1') `
        -StagingDirectory $foundation -Version $Version `
        -Publisher $Publisher -MinVersion $MinVersion
    if ($LASTEXITCODE -ne 0) { throw 'MSI foundation verification failed.' }

    [void][IO.Directory]::CreateDirectory($identityRoot)
    $preparedExternal = Join-Path $identityRoot 'ezyImageViewer.ExternalIdentity.msix'
    [IO.File]::Copy((Join-Path $foundation 'identity\ezyImageViewer.ExternalIdentity.msix'),
        $preparedExternal, $false)
    [void](Assert-EzyIdentityPackage $preparedExternal)

    if (-not $DevelopmentUnsigned) {
        Sign-Artifact $signTool $certificate $TimestampUrl $preparedExternal
    }

    $generator = Join-Path $scriptRoot 'generate-wix-payload.ps1'
    foreach ($contract in @(
            [PSCustomObject]@{ Scope = 'PerUser'; Path = $perUserFragment },
            [PSCustomObject]@{ Scope = 'PerMachine'; Path = $perMachineFragment }
        )) {
        & $generator -PayloadDirectory (Join-Path $foundation 'payload') `
            -Scope $contract.Scope `
            -ExternalIdentityPackage $preparedExternal -OutputPath $contract.Path
        if ($LASTEXITCODE -ne 0) { throw "$($contract.Scope) payload generation failed." }
        $firstHash = (Get-FileHash -LiteralPath $contract.Path -Algorithm SHA256).Hash
        & $generator -PayloadDirectory (Join-Path $foundation 'payload') `
            -Scope $contract.Scope `
            -ExternalIdentityPackage $preparedExternal -OutputPath $contract.Path
        if ($LASTEXITCODE -ne 0) { throw "$($contract.Scope) repeat generation failed." }
        $secondHash = (Get-FileHash -LiteralPath $contract.Path -Algorithm SHA256).Hash
        if ($firstHash -cne $secondHash) {
            throw "$($contract.Scope) WiX payload generation is not deterministic."
        }
    }

    $projects = @(
        'installer\per-user\ezyImageViewer.PerUser.wixproj',
        'installer\per-machine\ezyImageViewer.PerMachine.wixproj',
        'installer\bundle\ezyImageViewer.Bundle.wixproj'
    )
    foreach ($project in $projects) {
        $path = Join-Path $repositoryRoot $project
        Invoke-Checked "Restore $project" {
            & dotnet restore $path --locked-mode -p:AcceptEula=wix7
        }
    }

    $perUserProject = Join-Path $repositoryRoot `
        'installer\per-user\ezyImageViewer.PerUser.wixproj'
    $perMachineProject = Join-Path $repositoryRoot `
        'installer\per-machine\ezyImageViewer.PerMachine.wixproj'
    $enableIdentityRegistration = if ($DevelopmentUnsigned) { '0' } else { '1' }
    Invoke-Checked 'Build per-user MSI' {
        & dotnet build $perUserProject -t:Rebuild -c Release --no-restore `
            "-p:ProductVersion=$productVersion" `
            "-p:GeneratedPayloadSource=$perUserFragment" `
            "-p:EnableIdentityRegistration=$enableIdentityRegistration" `
            "-p:EulaRtf=$($eula.FullName)" -p:AcceptEula=wix7
    }
    Invoke-Checked 'Build per-machine MSI' {
        & dotnet build $perMachineProject -t:Rebuild -c Release --no-restore `
            "-p:ProductVersion=$productVersion" `
            "-p:GeneratedPayloadSource=$perMachineFragment" `
            "-p:EnableIdentityRegistration=$enableIdentityRegistration" `
            "-p:EulaRtf=$($eula.FullName)" -p:AcceptEula=wix7
    }
    $perUserMsi = Join-Path $repositoryRoot `
        "installer\per-user\bin\Release\ezyImageViewer-$productVersion-x64-per-user.msi"
    $perMachineMsi = Join-Path $repositoryRoot `
        "installer\per-machine\bin\Release\ezyImageViewer-$productVersion-x64-per-machine.msi"
    if (-not $DevelopmentUnsigned) {
        Sign-Artifact $signTool $certificate $TimestampUrl $perUserMsi
        Sign-Artifact $signTool $certificate $TimestampUrl $perMachineMsi
    }

    $perUserExpectedFiles = Get-GeneratedPayloadFileCount $perUserFragment
    $perMachineExpectedFiles = Get-GeneratedPayloadFileCount $perMachineFragment
    if ($perUserExpectedFiles -ne $perMachineExpectedFiles) {
        throw "Generated WiX payload counts differ by scope."
    }
    $expectedFiles = $perUserExpectedFiles
    & (Join-Path $scriptRoot 'verify-wix-installer.ps1') `
        -PerUserMsi $perUserMsi -PerMachineMsi $perMachineMsi `
        -ProductVersion $productVersion -ExpectedPayloadFileCount $expectedFiles `
        -ExpectedIdentityRegistration $enableIdentityRegistration
    if ($LASTEXITCODE -ne 0) { throw 'WiX MSI verification failed.' }

    $bundleProject = Join-Path $repositoryRoot `
        'installer\bundle\ezyImageViewer.Bundle.wixproj'
    $bundleIcon = Join-Path $repositoryRoot `
        'EzyImageViewer.App\Assets\ezyImageViewer.ico'
    $bundleLogo = Join-Path $foundation `
        'contracts\package\Assets\Square150x150Logo.png'
    $themeFile = $themeSource.FullName
    Invoke-Checked 'Build Burn bundle' {
        & dotnet build $bundleProject -t:Rebuild -c Release --no-restore `
            "-p:ProductVersion=$productVersion" "-p:PerUserMsi=$perUserMsi" `
            "-p:PerMachineMsi=$perMachineMsi" `
            "-p:EulaRtf=$($eula.FullName)" "-p:BundleIcon=$bundleIcon" `
            "-p:BundleLogo=$bundleLogo" "-p:ThemeFile=$themeFile" `
            "-p:BundleLocalization=$($bundleLocalization.FullName)" `
            "-p:KoreanLocalization=$($koreanLocalization.FullName)" `
            "-p:KoreanEula=$koreanEula" `
            -p:AcceptEula=wix7
    }
    $bundle = Join-Path $repositoryRoot `
        "installer\bundle\bin\Release\ezyImageViewerSetup-$productVersion-x64.exe"
    if (-not $DevelopmentUnsigned) {
        $wixTool = Get-WixTool $bundleProject
        Sign-BurnBundle $wixTool $signTool $certificate $TimestampUrl $bundle $workingRoot
    }
    & (Join-Path $scriptRoot 'verify-wix-bundle.ps1') `
        -BundlePath $bundle -ProductVersion $productVersion
    if ($LASTEXITCODE -ne 0) { throw 'WiX Burn verification failed.' }

    [void][IO.Directory]::CreateDirectory($output)
    $suffix = if ($DevelopmentUnsigned) { '-dev-unsigned' } else { '' }
    $artifacts = @(
        [PSCustomObject]@{ Source = $perUserMsi; Name = "ezyImageViewer-$productVersion-x64-per-user$suffix.msi" },
        [PSCustomObject]@{ Source = $perMachineMsi; Name = "ezyImageViewer-$productVersion-x64-per-machine$suffix.msi" },
        [PSCustomObject]@{ Source = $bundle; Name = "ezyImageViewerSetup-$productVersion-x64$suffix.exe" },
        [PSCustomObject]@{ Source = $themeSource.FullName; Name = 'EzyRtfLargeTheme.xml' },
        [PSCustomObject]@{ Source = $themeLicense.FullName; Name = 'LICENSE-MRL.txt' }
    )
    $published = [Collections.Generic.List[object]]::new()
    foreach ($artifact in $artifacts) {
        $destination = Join-Path $output $artifact.Name
        [IO.File]::Copy($artifact.Source, $destination, $false)
        $file = Get-Item -LiteralPath $destination -Force
        $published.Add([PSCustomObject][ordered]@{
                file = $file.Name
                size = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            })
    }
    $releaseMetadata = [PSCustomObject][ordered]@{
        schemaVersion = 1
        version = $Version
        productVersion = $productVersion
        publisher = $Publisher
        minVersion = $MinVersion
        architecture = 'x64'
        developmentUnsigned = [bool]$DevelopmentUnsigned
        artifacts = @($published)
    }
    [IO.File]::WriteAllText((Join-Path $output 'installer-artifacts.json'),
        ($releaseMetadata | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $checksumLines = @($published | Sort-Object file | ForEach-Object {
            $_.sha256 + '  ' + $_.file
        })
    [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS'), $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Write-Output "WiX installer artifacts published: $output"
}
finally {
    $resolvedWorkingRoot = [IO.Path]::GetFullPath($workingRoot)
    $expectedPrefix = $generatedItem.FullName.TrimEnd('\') + '\'
    if ([IO.Directory]::Exists($resolvedWorkingRoot)) {
        if (-not $resolvedWorkingRoot.StartsWith($expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedWorkingRoot).StartsWith(
                '.wix-build.', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected working path '$resolvedWorkingRoot'."
        }
        Remove-Item -LiteralPath $resolvedWorkingRoot -Recurse -Force
    }
}
