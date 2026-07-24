[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'msi-payload-helpers.ps1')
. (Join-Path $scriptRoot 'portable-release-helpers.ps1')

Assert-EzyPortableVersion $Version
$numericVersion = Get-EzyPortableNumericVersion $Version
$sourceCommit = Assert-EzyPortableSourceState $repositoryRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "packaging\out\portable-$Version"
}
$target = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw "OutputDirectory already exists: '$target'."
}
$parentPath = [IO.Path]::GetDirectoryName($target)
if ([string]::IsNullOrWhiteSpace($parentPath)) {
    throw 'OutputDirectory must have a parent directory.'
}
[void][IO.Directory]::CreateDirectory($parentPath)
$parent = Get-Item -LiteralPath $parentPath -Force
if (-not $parent.PSIsContainer -or
    ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'OutputDirectory parent must be a physical directory.'
}

$targetName = [IO.Path]::GetFileName($target)
$staging = Join-Path $parent.FullName (
    ".$targetName.$([Guid]::NewGuid().ToString('N')).staging")
$packageStem = "ezyImageViewer-$Version-win-x64"
$archiveName = "$packageStem.zip"

try {
    [void][IO.Directory]::CreateDirectory($staging)
    $payload = Join-Path $staging 'payload'
    $applicationProject = Join-Path $repositoryRoot 'EzyImageViewer.App\EzyImageViewer.App.csproj'

    $restoreArguments = @(
        'restore',
        $applicationProject,
        '--locked-mode',
        '--runtime', 'win-x64',
        '-p:Platform=x64',
        '-p:Packaged=false',
        '-p:ExternalIdentity=false',
        '-p:Portable=true',
        '-p:NuGetAuditMode=all',
        '-p:WarningsAsErrors=NU1903%3BNU1904'
    )
    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for the portable flavor ($LASTEXITCODE)."
    }

    $publishArguments = @(
        'publish',
        $applicationProject,
        '-c', 'Release',
        '--no-restore',
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:Packaged=false',
        '-p:ExternalIdentity=false',
        '-p:Portable=true',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion",
        "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$Version",
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $scriptRoot 'MsiPublish.targets')",
        '-o', $payload
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for the portable flavor ($LASTEXITCODE)."
    }

    $intermediateRoot = Join-Path $repositoryRoot 'EzyImageViewer.App\obj\portable\x64\Release'
    $publishOutputList = Get-EzyMsiPublishOutputListPath $intermediateRoot $payload
    Assert-EzyMsiPayload $payload $publishOutputList

    foreach ($forbidden in @(
            'AppxManifest.xml',
            'AppxSignature.p7x')) {
        if (Test-Path -LiteralPath (Join-Path $payload $forbidden)) {
            throw "Portable payload contains forbidden packaged content: '$forbidden'."
        }
    }

    [IO.File]::Copy(
        (Join-Path $repositoryRoot 'docs\portable-readme.txt'),
        (Join-Path $payload 'PORTABLE-README.txt'),
        $false)
    $depsJson = Join-Path $payload 'ezyImageViewer.deps.json'
    $projectAssets = Join-Path $repositoryRoot 'EzyImageViewer.App\obj\portable\project.assets.json'
    $thirdPartyIndex = Copy-EzyPortableThirdPartyFiles `
        -PayloadDirectory $payload `
        -DepsJson $depsJson `
        -ProjectAssetsJson $projectAssets
    $inventory = Write-EzyPackageContentsManifest -Layout $payload

    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $payload 'ezyImageViewer.exe')
    if ([string]$signature.Status -cne 'NotSigned') {
        throw "Portable executable must be unsigned; actual status is '$($signature.Status)'."
    }

    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)
    [long]$payloadBytes = 0
    foreach ($file in $payloadFiles) {
        $payloadBytes += $file.Length
    }

    $archivePath = Join-Path $staging $archiveName
    New-EzyPortableZip `
        -PayloadDirectory $payload `
        -ArchivePath $archivePath `
        -RootDirectoryName $packageStem

    $manifestName = 'portable-release-manifest.json'
    $manifestPath = Join-Path $staging $manifestName
    $manifest = [ordered]@{
        schemaVersion = 1
        tag = "v$Version"
        version = $Version
        numericVersion = $numericVersion
        channel = 'basic-portable-preview'
        sourceCommit = $sourceCommit
        platform = [ordered]@{
            operatingSystem = 'Windows 10 build 19041 or later'
            architecture = 'x64'
        }
        packageIdentity = $false
        installer = $false
        signed = $false
        supportedUse = 'evaluation-and-testing-preview'
        limitations = @(
            'Windows SmartScreen may warn because the executable is unsigned.',
            'Official package-identity capture callback is unavailable; clipboard fallback remains.',
            'PDF and PSD are disabled in the normal product UI.',
            'Updates are manual and this archive is not upgraded automatically by Microsoft Store.'
        )
        payload = [ordered]@{
            fileCount = $payloadFiles.Count
            byteCount = $payloadBytes
            inventorySha256 = (Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash.ToUpperInvariant()
            thirdPartyIndexSha256 = (Get-FileHash -LiteralPath $thirdPartyIndex -Algorithm SHA256).Hash.ToUpperInvariant()
        }
        archive = [ordered]@{
            fileName = $archiveName
            byteCount = (Get-Item -LiteralPath $archivePath).Length
            sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))

    $checksumEntries = @(
        (Get-Item -LiteralPath $archivePath)
        (Get-Item -LiteralPath $manifestPath)
    )
    [Array]::Sort($checksumEntries, [Comparison[object]]{
            param($left, $right)
            [StringComparer]::Ordinal.Compare($left.Name, $right.Name)
        })
    $checksumLines = @($checksumEntries | ForEach-Object {
            "{0}  {1}" -f (
                Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant(),
                $_.Name
        })
    [IO.File]::WriteAllText(
        (Join-Path $staging 'SHA256SUMS.txt'),
        ($checksumLines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    [IO.Directory]::Delete($payload, $true)
    [IO.Directory]::Move($staging, $target)
    Write-Output "Portable release staged: $target"
    Write-Output "Archive: $archiveName"
    Write-Output "Payload files: $($payloadFiles.Count)"
    Write-Output "Payload bytes: $payloadBytes"
    Write-Output 'Signature: NotSigned (testing preview)'
}
finally {
    if ([IO.Directory]::Exists($staging)) {
        [IO.Directory]::Delete($staging, $true)
    }
}
