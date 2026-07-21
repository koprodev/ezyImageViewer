[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ContractPath,
    [switch]$SkipPortableRuntimeSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    $ContractPath = Join-Path $scriptRoot 'preview-release.json'
}
$contract = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ContractPath)) | ConvertFrom-Json
$output = Get-Item -LiteralPath ([IO.Path]::GetFullPath($OutputDirectory)) -Force
if (-not $output.PSIsContainer -or
    ($output.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Preview release output must be a physical directory.'
}

$portableName = 'ezyImageViewer.exe'
$setupName = "ezyImageViewerSetup-$($contract.productVersion)-x64-dev-unsigned.exe"
$manifestName = 'preview-release-manifest.json'
$expectedNames = @(
    $portableName, $setupName, 'EzyRtfLargeTheme.xml', 'LICENSE-MRL.txt',
    $manifestName, 'SHA256SUMS.txt')
$actualFiles = @(Get-ChildItem -LiteralPath $output.FullName -Force)
if (@($actualFiles | Where-Object { $_.PSIsContainer }).Count -ne 0 -or
    $actualFiles.Count -ne $expectedNames.Count) {
    throw 'Preview release output file count is invalid.'
}
foreach ($name in $expectedNames) {
    if (-not [IO.File]::Exists((Join-Path $output.FullName $name))) {
        throw "Preview release output is missing '$name'."
    }
}

$hashedNames = @($expectedNames | Where-Object { $_ -cne 'SHA256SUMS.txt' })
[Array]::Sort($hashedNames, [Comparison[object]]{
        param($left, $right)
        [StringComparer]::Ordinal.Compare($left, $right)
    })
$hashLines = [IO.File]::ReadAllLines((Join-Path $output.FullName 'SHA256SUMS.txt'))
if ($hashLines.Count -ne $hashedNames.Count) {
    throw 'Preview release checksum entry count is invalid.'
}
for ($index = 0; $index -lt $hashedNames.Count; $index++) {
    if ($hashLines[$index] -cnotmatch '^([A-F0-9]{64})  ([A-Za-z0-9_.-]+)$' -or
        $Matches[2] -cne $hashedNames[$index]) {
        throw "Invalid or unsorted preview checksum line: '$($hashLines[$index])'."
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $output.FullName $Matches[2]) `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($Matches[1] -cne $actualHash) {
        throw "Preview release checksum mismatch for '$($Matches[2])'."
    }
}

$manifest = [IO.File]::ReadAllText((Join-Path $output.FullName $manifestName)) |
    ConvertFrom-Json
$head = @(& git -C $repositoryRoot rev-parse --verify 'HEAD^{commit}')
if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or
    [int]$manifest.schemaVersion -ne 1 -or
    [string]$manifest.tag -cne [string]$contract.tag -or
    [string]$manifest.releaseVersion -cne [string]$contract.releaseVersion -or
    [string]$manifest.sourceCommit -cne $head[0].Trim().ToLowerInvariant() -or
    -not [bool]$manifest.prerelease -or [bool]$manifest.signed -or
    [string]$manifest.supportedUse -cne 'personal-evaluation-and-testing-preview' -or
    [string]$manifest.applicationVersion -cne [string]$contract.applicationVersion -or
    [string]$manifest.codecHostVersion -cne [string]$contract.codecHostVersion -or
    [string]$manifest.portableVersion -cne [string]$contract.portableVersion) {
    throw 'Preview release manifest contract mismatch.'
}

$artifactMap = @{}
foreach ($artifact in @($manifest.artifacts)) {
    $name = [string]$artifact.fileName
    if ($artifactMap.ContainsKey($name) -or $hashedNames -cnotcontains $name -or
        $name -ceq $manifestName) {
        throw "Unexpected or duplicate manifest artifact '$name'."
    }
    $file = Get-Item -LiteralPath (Join-Path $output.FullName $name) -Force
    if ([long]$artifact.byteCount -ne $file.Length -or
        [string]$artifact.sha256 -cne (
            Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()) {
        throw "Preview manifest metadata mismatch for '$name'."
    }
    $artifactMap[$name] = $true
}
if ($artifactMap.Count -ne 4) {
    throw 'Preview manifest artifact count is invalid.'
}

$portablePath = Join-Path $output.FullName $portableName
$portableVerifyArguments = @{
    Version = [string]$contract.portableVersion
    ExecutablePath = $portablePath
}
if ($SkipPortableRuntimeSmoke) {
    $portableVerifyArguments.SkipRuntimeSmoke = $true
}
& (Join-Path $scriptRoot 'verify-single-file-portable.ps1') @portableVerifyArguments
if ($LASTEXITCODE -ne 0) { throw 'Single-file portable verification failed.' }

$setupPath = Join-Path $output.FullName $setupName
& (Join-Path $scriptRoot 'verify-wix-bundle.ps1') `
    -BundlePath $setupPath -ProductVersion ([string]$contract.productVersion)
if ($LASTEXITCODE -ne 0) { throw 'WiX setup verification failed.' }
foreach ($path in @($portablePath, $setupPath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ([string]$signature.Status -cne 'NotSigned') {
        throw "Preview executable must be NotSigned: '$path'."
    }
}

$sourcePairs = @(
    @('EzyRtfLargeTheme.xml', 'installer\bundle\EzyRtfLargeTheme.xml'),
    @('LICENSE-MRL.txt', 'installer\bundle\LICENSE-MRL.txt'))
foreach ($pair in $sourcePairs) {
    $releasedHash = (Get-FileHash -LiteralPath (Join-Path $output.FullName $pair[0]) `
        -Algorithm SHA256).Hash
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $pair[1]) `
        -Algorithm SHA256).Hash
    if ($releasedHash -cne $sourceHash) {
        throw "Released '$($pair[0])' differs from its source."
    }
}

Write-Output 'Preview release verification passed.'
Write-Output "Tag: $($contract.tag)"
Write-Output "Assets: $($expectedNames.Count)"
Write-Output 'Executables: NotSigned'
