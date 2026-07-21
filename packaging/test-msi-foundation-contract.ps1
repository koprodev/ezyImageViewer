[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'identity-registration-contract.ps1')
. (Join-Path $scriptRoot 'msi-payload-helpers.ps1')
. (Join-Path $scriptRoot 'release-helpers.ps1')

$script:PassCount = 0

function Assert-Foundation {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:PassCount++
}

function Assert-FoundationThrows {
    param([string]$Label, [scriptblock]$Action)
    try { & $Action }
    catch {
        $script:PassCount++
        return
    }
    throw "Expected contract rejection: $Label."
}

function New-SyntheticPayload {
    param([string]$Root)

    $paths = @(
        'ezyImageViewer.exe',
        'ezyImageViewer.dll',
        'ezyImageViewer.deps.json',
        'ezyImageViewer.runtimeconfig.json',
        'ezyImageViewer.pri',
        'Assets/ezyImageViewer.ico',
        'Assets/Fonts/MaterialSymbolsOutlined.ttf',
        'Assets/Fonts/LICENSE-MaterialSymbols.txt',
        'LICENSE.txt',
        'THIRD-PARTY-NOTICES.md',
        'runtime/native.dll'
    )
    foreach ($relative in $paths) {
        $path = Join-Path $Root $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path, "synthetic:$relative", [Text.UTF8Encoding]::new($false))
    }

    $listPath = Join-Path ([IO.Path]::GetDirectoryName($Root)) `
        (([IO.Path]::GetFileName($Root)) + '.PublishOutputs.txt')
    $lines = @($paths | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $Root $_)) })
    [IO.File]::WriteAllLines($listPath, $lines, [Text.UTF8Encoding]::new($false))
    return $listPath
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ('ezy-msi-contract-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempRoot)

try {
    $payloadA = Join-Path $tempRoot 'payload-a'
    $payloadB = Join-Path $tempRoot 'payload-b'
    $listA = New-SyntheticPayload $payloadA
    $listB = New-SyntheticPayload $payloadB
    Assert-EzyMsiPayload $payloadA $listA
    Assert-EzyMsiPayload $payloadB $listB
    $script:PassCount += 2

    $inventoryA = Write-EzyMsiPayloadInventory $payloadA $listA
    $inventoryB = Write-EzyMsiPayloadInventory $payloadB $listB
    Assert-Foundation ([IO.File]::Exists($inventoryA)) 'First payload inventory is missing.'
    Assert-Foundation ([IO.File]::Exists($inventoryB)) 'Second payload inventory is missing.'
    Assert-Foundation `
        ((Get-FileHash $inventoryA -Algorithm SHA256).Hash -ceq
            (Get-FileHash $inventoryB -Algorithm SHA256).Hash) `
        'MSI payload inventory is not deterministic.'

    $extraPayload = Join-Path $tempRoot 'payload-extra'
    $extraList = New-SyntheticPayload $extraPayload
    [IO.File]::WriteAllText((Join-Path $extraPayload 'stale.dll'), 'stale')
    Assert-FoundationThrows 'undeclared stale file' {
        Assert-EzyMsiPayload $extraPayload $extraList
    }

    $pdbPayload = Join-Path $tempRoot 'payload-pdb'
    $pdbList = New-SyntheticPayload $pdbPayload
    $pdbPath = Join-Path $pdbPayload 'ezyImageViewer.pdb'
    [IO.File]::WriteAllText($pdbPath, 'symbols')
    [IO.File]::AppendAllText($pdbList, $pdbPath + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Assert-FoundationThrows 'PDB file' {
        Assert-EzyMsiPayload $pdbPayload $pdbList
    }

    $hostPayload = Join-Path $tempRoot 'payload-host'
    $hostList = New-SyntheticPayload $hostPayload
    $hostPath = Join-Path $hostPayload 'EzyImageViewer.CodecHost.dll'
    [IO.File]::WriteAllText($hostPath, 'host')
    [IO.File]::AppendAllText($hostList, $hostPath + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Assert-FoundationThrows 'CodecHost payload mixing' {
        Assert-EzyMsiPayload $hostPayload $hostList
    }

    $escapedPayload = Join-Path $tempRoot 'payload-escaped'
    $escapedList = New-SyntheticPayload $escapedPayload
    [IO.File]::AppendAllText($escapedList,
        (Join-Path $tempRoot 'outside.dll') + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Assert-FoundationThrows 'publish list path escape' {
        Assert-EzyMsiPayload $escapedPayload $escapedList
    }

    $localizedDirectoryName = -join [char[]](0xC124, 0xCE58, 0x20, 0xACBD, 0xB85C)
    $installRoot = Join-Path $tempRoot "$localizedDirectoryName with space's"
    [void][IO.Directory]::CreateDirectory($installRoot)
    [IO.File]::WriteAllText((Join-Path $installRoot 'ezyImageViewer.exe'), 'app')
    $codecPackage = Join-Path $tempRoot 'codec host.msix'
    $externalPackage = Join-Path $tempRoot "main identity's.msix"
    [IO.File]::WriteAllText($codecPackage, 'codec')
    [IO.File]::WriteAllText($externalPackage, 'main')

    foreach ($scope in @('CurrentUser', 'AllUsers')) {
        $register = New-EzyIdentityRegistrationPlan 'Register' $scope $installRoot `
            $codecPackage $externalPackage
        $unregister = New-EzyIdentityRegistrationPlan 'Unregister' $scope $installRoot
        Assert-Foundation ($register.Steps[0].StepId -ceq 'codec-host') `
            "$scope register order is invalid."
        Assert-Foundation ($unregister.Steps[0].StepId -ceq 'main-identity') `
            "$scope unregister order is invalid."
        Assert-Foundation ($register.Steps[1].Arguments.ExternalLocation -ceq $installRoot) `
            "$scope external location lost path characters."
        $firstJson = ConvertTo-EzyIdentityRegistrationPlanJson $register
        $secondJson = ConvertTo-EzyIdentityRegistrationPlanJson $register
        Assert-Foundation ($firstJson -ceq $secondJson) `
            "$scope registration plan JSON is not deterministic."
        Assert-Foundation (-not $firstJson.Contains('Add-AppxPackage')) `
            "$scope plan contains a shell command string."
    }

    $exitCodes = Get-EzyIdentityExitCodes
    $values = @($exitCodes.PSObject.Properties.Value)
    Assert-Foundation (@($values | Sort-Object -Unique).Count -eq $values.Count) `
        'Registration exit codes are not unique.'
    Assert-Foundation ($exitCodes.Success -eq 0) 'Success exit code is not zero.'

    Assert-EzyProductionTimestampUrl ([uri]'https://timestamp.example.test/rfc3161')
    $script:PassCount++
    Assert-FoundationThrows 'non-HTTPS timestamp URL' {
        Assert-EzyProductionTimestampUrl ([uri]'http://timestamp.example.test/rfc3161')
    }
    Assert-FoundationThrows 'relative timestamp URL' {
        Assert-EzyProductionTimestampUrl ([uri]'timestamp/rfc3161')
    }
    Assert-FoundationThrows 'timestamp URL credentials' {
        Assert-EzyProductionTimestampUrl ([uri]'https://token@timestamp.example.test/rfc3161')
    }
    Assert-FoundationThrows 'timestamp URL fragment' {
        Assert-EzyProductionTimestampUrl ([uri]'https://timestamp.example.test/rfc3161#fragment')
    }

    $syntheticRepository = Join-Path $tempRoot 'repository'
    $pinnedBuildTools = Join-Path $tempRoot 'packages\microsoft.windows.sdk.buildtools\1.2.3'
    [void][IO.Directory]::CreateDirectory($syntheticRepository)
    [void][IO.Directory]::CreateDirectory($pinnedBuildTools)
    [IO.File]::WriteAllText(
        (Join-Path $syntheticRepository 'Directory.Packages.props'),
        '<Project><ItemGroup><PackageVersion Include="Microsoft.Windows.SDK.BuildTools" Version="1.2.3" /></ItemGroup></Project>',
        [Text.UTF8Encoding]::new($false))
    $resolvedBuildTools = Get-EzyPinnedBuildToolsRoot `
        -RepositoryRoot $syntheticRepository -ExplicitRoot $pinnedBuildTools
    Assert-Foundation ($resolvedBuildTools -ceq $pinnedBuildTools) `
        'Explicit BuildTools root did not preserve the pinned physical path.'
    $wrongBuildTools = Join-Path $tempRoot 'packages\microsoft.windows.sdk.buildtools\9.9.9'
    [void][IO.Directory]::CreateDirectory($wrongBuildTools)
    Assert-FoundationThrows 'unpinned explicit BuildTools root' {
        Get-EzyPinnedBuildToolsRoot -RepositoryRoot $syntheticRepository `
            -ExplicitRoot $wrongBuildTools
    }

    $fakeToolDirectory = Join-Path $pinnedBuildTools 'bin\1.2.3\x64'
    [void][IO.Directory]::CreateDirectory($fakeToolDirectory)
    [IO.File]::WriteAllText((Join-Path $fakeToolDirectory 'signtool.exe'), 'not a tool')
    Assert-FoundationThrows 'untrusted x64 SignTool' {
        Get-EzyMicrosoftSignTool $pinnedBuildTools
    }

    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
    $actualBuildTools = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repositoryRoot `
        -ProjectAssetsPaths @(
            (Join-Path $repositoryRoot 'EzyImageViewer.App\obj\project.assets.json'))
    $actualSignTool = Get-EzyMicrosoftSignTool $actualBuildTools
    Assert-Foundation ([IO.File]::Exists($actualSignTool)) `
        'Trusted Microsoft x64 SignTool was not resolved.'
    $actualSignature = Get-AuthenticodeSignature -LiteralPath $actualSignTool
    Assert-Foundation `
        (Test-EzyCodeSigningCertificate $actualSignature.SignerCertificate) `
        'Trusted Microsoft SignTool certificate is missing the code-signing EKU.'
    $quotedOrganizationInjection = `
        [Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
            "CN=`"Evil, O=Microsoft Corporation,`nO=Microsoft Corporation`nX`", C=US")
    Assert-Foundation `
        (-not (Test-EzyDistinguishedNameComponent `
                -DistinguishedName $quotedOrganizationInjection `
                -ExpectedComponent 'O=Microsoft Corporation')) `
        'A quoted subject value was incorrectly accepted as the Microsoft organization.'
    Assert-Foundation `
        (-not (Test-EzyCodeSigningCertificate $actualSignature.TimeStamperCertificate)) `
        'Timestamp certificate was incorrectly accepted for code signing.'
    Assert-EzySignatureEvidence -Signature $actualSignature `
        -ExpectedThumbprint $actualSignature.SignerCertificate.Thumbprint `
        -Label 'trusted SignTool'
    $script:PassCount++
    Assert-EzyArtifactSignature -Path $actualSignTool `
        -ExpectedThumbprint $actualSignature.SignerCertificate.Thumbprint
    $script:PassCount++
    $invalidStatus = [PSCustomObject]@{
        Status = 'UnknownError'
        SignerCertificate = $actualSignature.SignerCertificate
        TimeStamperCertificate = $actualSignature.TimeStamperCertificate
    }
    Assert-FoundationThrows 'invalid artifact signature status' {
        Assert-EzySignatureEvidence -Signature $invalidStatus `
            -ExpectedThumbprint $actualSignature.SignerCertificate.Thumbprint `
            -Label 'invalid status'
    }
    Assert-FoundationThrows 'unexpected artifact signer' {
        Assert-EzySignatureEvidence -Signature $actualSignature `
            -ExpectedThumbprint $actualSignature.TimeStamperCertificate.Thumbprint `
            -Label 'wrong signer'
    }
    $missingTimestamp = [PSCustomObject]@{
        Status = 'Valid'
        SignerCertificate = $actualSignature.SignerCertificate
        TimeStamperCertificate = $null
    }
    Assert-FoundationThrows 'missing RFC 3161 timestamp' {
        Assert-EzySignatureEvidence -Signature $missingTimestamp `
            -ExpectedThumbprint $actualSignature.SignerCertificate.Thumbprint `
            -Label 'missing timestamp'
    }

    $stageScript = [IO.File]::ReadAllText((Join-Path $scriptRoot 'stage-msi-foundation.ps1'))
    Assert-Foundation ($stageScript.Contains("'-p:ExternalIdentity=true'")) `
        'MSI staging does not select the isolated external identity build flavor.'
    Assert-Foundation ($stageScript.Contains('-p:ExternalApplicationManifest=')) `
        'MSI staging does not inject the Publisher-bound external application manifest.'
    Assert-Foundation ($stageScript.Contains('obj\external\x64\Release')) `
        'MSI staging does not use external identity publish provenance.'
    Assert-Foundation ($stageScript.Contains("'--runtime', 'win-x64'")) `
        'MSI staging locked restore does not select the lock-file runtime graph.'
    Assert-Foundation (-not $stageScript.Contains('-p:ApplicationManifest=')) `
        'MSI staging bypasses the external identity project contract.'

    $wixBuildScript = [IO.File]::ReadAllText((Join-Path $scriptRoot 'build-wix-installer.ps1'))
    $detachIndex = $wixBuildScript.IndexOf(
        '& $WixTool -acceptEula wix7 burn detach',
        [StringComparison]::Ordinal)
    $engineSignIndex = $wixBuildScript.IndexOf(
        'Sign-Artifact $SignTool $Certificate $Timestamp $engine',
        [StringComparison]::Ordinal)
    $reattachIndex = $wixBuildScript.IndexOf(
        '& $WixTool -acceptEula wix7 burn reattach',
        [StringComparison]::Ordinal)
    $finalBundleSignIndex = $wixBuildScript.IndexOf(
        'Sign-Artifact $SignTool $Certificate $Timestamp $BundlePath',
        [StringComparison]::Ordinal)
    Assert-Foundation ($detachIndex -ge 0 -and $engineSignIndex -gt $detachIndex -and
        $reattachIndex -gt $engineSignIndex -and
        $finalBundleSignIndex -gt $reattachIndex) `
        'Production Burn signing does not detach, sign, reattach, then sign the full bundle.'
    Assert-Foundation ($wixBuildScript.Contains(
            'Assert-EzyProductionTimestampUrl $TimestampUrl')) `
        'Production WiX build does not enforce the timestamp URL policy.'
    Assert-Foundation ($wixBuildScript.Contains(
            '-ExplicitRoot $BuildToolsRoot')) `
        'Production WiX build does not enforce the pinned BuildTools root.'
    Assert-Foundation ($wixBuildScript.Contains(
            'Get-EzyMicrosoftSignTool $pinnedBuildToolsRoot')) `
        'Production WiX build does not verify the Microsoft SignTool trust.'
    Assert-Foundation ($wixBuildScript.Contains(
            '(Test-EzyCodeSigningCertificate $_)')) `
        'Production WiX build does not verify the selected certificate EKU.'
    Assert-Foundation ($wixBuildScript.Contains(
            'Assert-EzyArtifactSignature -Path $Path')) `
        'Production WiX build does not verify exact signer and timestamp evidence.'
    $preflightIndex = $wixBuildScript.IndexOf(
        'Assert-EzyProductionTimestampUrl $TimestampUrl',
        [StringComparison]::Ordinal)
    $outputMutationIndex = $wixBuildScript.IndexOf(
        '[void][IO.Directory]::CreateDirectory($outputParentPath)',
        [StringComparison]::Ordinal)
    $stagingIndex = $wixBuildScript.IndexOf(
        "& (Join-Path `$scriptRoot 'stage-msi-foundation.ps1')",
        [StringComparison]::Ordinal)
    Assert-Foundation ($preflightIndex -ge 0 -and
        $preflightIndex -lt $outputMutationIndex -and
        $preflightIndex -lt $stagingIndex) `
        'Production signing preflight does not run before output and staging mutations.'

    Assert-FoundationThrows 'same package path' {
        New-EzyIdentityRegistrationPlan 'Register' 'CurrentUser' $installRoot `
            $codecPackage $codecPackage
    }
    Assert-FoundationThrows 'unregister package arguments' {
        New-EzyIdentityRegistrationPlan 'Unregister' 'CurrentUser' $installRoot `
            $codecPackage $externalPackage
    }

    Write-Output "MSI foundation contract tests passed: $script:PassCount"
}
finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if (-not $resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedTempRoot -ceq $tempBase) {
        throw "Refusing to remove unsafe contract-test path: '$resolvedTempRoot'."
    }
    if ([IO.Directory]::Exists($resolvedTempRoot)) {
        [IO.Directory]::Delete($resolvedTempRoot, $true)
    }
}
