[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ContractPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'external-location-helpers.ps1')
. (Join-Path $scriptRoot 'portable-release-helpers.ps1')

if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    $ContractPath = Join-Path $scriptRoot 'preview-release.json'
}

$contractFile = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ContractPath)) -Force
$contract = [IO.File]::ReadAllText($contractFile.FullName) | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1 -or
    [string]$contract.releaseVersion -cnotmatch '^\d+\.\d+\.\d+-preview\.\d+$' -or
    [string]$contract.tag -cne "v$($contract.releaseVersion)" -or
    -not [bool]$contract.prerelease) {
    throw 'Preview release contract is invalid.'
}
Assert-EzyExternalFourPartVersion ([string]$contract.applicationVersion) 'applicationVersion'
Assert-EzyExternalPublisher ([string]$contract.publisher)
Assert-EzyPortableVersion ([string]$contract.portableVersion)
$applicationParts = ([string]$contract.applicationVersion).Split('.')
if ([string]$contract.productVersion -cne ($applicationParts[0..2] -join '.') -or
    -not ([string]$contract.portableVersion).StartsWith(
        ([string]$contract.productVersion + '-'), [StringComparison]::Ordinal)) {
    throw 'Preview release version fields are inconsistent.'
}
$sourceCommit = Assert-EzyPortableSourceState $repositoryRoot

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
$working = Join-Path $staging 'working'
$release = Join-Path $staging 'release'

try {
    [void][IO.Directory]::CreateDirectory($working)
    [void][IO.Directory]::CreateDirectory($release)

    $portableOutput = Join-Path $working 'portable'
    & (Join-Path $scriptRoot 'build-single-file-portable.ps1') `
        -Version ([string]$contract.portableVersion) `
        -ReleaseVersion ([string]$contract.releaseVersion) `
        -OutputDirectory $portableOutput
    if ($LASTEXITCODE -ne 0) { throw 'Single-file portable build failed.' }

    & (Join-Path $scriptRoot 'pack-msix.ps1') `
        -Version ([string]$contract.applicationVersion) `
        -ReleaseVersion ([string]$contract.releaseVersion) `
        -Publisher ([string]$contract.publisher) -SkipSign
    if ($LASTEXITCODE -ne 0) { throw 'Unsigned identity package build failed.' }

    $installerOutput = Join-Path $working 'installer'
    & (Join-Path $scriptRoot 'build-wix-installer.ps1') `
        -Version ([string]$contract.applicationVersion) `
        -ReleaseVersion ([string]$contract.releaseVersion) `
        -Publisher ([string]$contract.publisher) `
        -EulaRtf (Join-Path $repositoryRoot 'installer\assets\EULA.rtf') `
        -OutputDirectory $installerOutput `
        -MinVersion '10.0.19041.0' -DevelopmentUnsigned
    if ($LASTEXITCODE -ne 0) { throw 'Unsigned WiX installer build failed.' }

    $portableName = 'ezyImageViewer.exe'
    $setupName = "ezyImageViewerSetup-$($contract.productVersion)-x64-dev-unsigned.exe"
    $releaseInputs = @(
        [PSCustomObject]@{
            Role = 'portable-single-file'
            Source = Join-Path $portableOutput $portableName
            Name = $portableName
        },
        [PSCustomObject]@{
            Role = 'installer-setup'
            Source = Join-Path $installerOutput $setupName
            Name = $setupName
        },
        [PSCustomObject]@{
            Role = 'wix-theme-source'
            Source = Join-Path $installerOutput 'EzyRtfLargeTheme.xml'
            Name = 'EzyRtfLargeTheme.xml'
        },
        [PSCustomObject]@{
            Role = 'wix-theme-license'
            Source = Join-Path $installerOutput 'LICENSE-MRL.txt'
            Name = 'LICENSE-MRL.txt'
        }
    )

    $artifactRecords = [Collections.Generic.List[object]]::new()
    foreach ($input in $releaseInputs) {
        $source = Get-Item -LiteralPath $input.Source -Force
        if ($source.PSIsContainer -or
            ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release input must be a physical file: '$($input.Source)'."
        }
        $destination = Join-Path $release $input.Name
        [IO.File]::Copy($source.FullName, $destination, $false)
        $file = Get-Item -LiteralPath $destination -Force
        [void]$artifactRecords.Add([ordered]@{
                role = $input.Role
                fileName = $file.Name
                byteCount = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            })
    }

    $manifestName = 'preview-release-manifest.json'
    $manifestPath = Join-Path $release $manifestName
    $manifest = [ordered]@{
        schemaVersion = 1
        tag = [string]$contract.tag
        releaseVersion = [string]$contract.releaseVersion
        sourceCommit = $sourceCommit
        prerelease = $true
        signed = $false
        supportedUse = 'personal-evaluation-and-testing-preview'
        platform = [ordered]@{
            operatingSystem = 'Windows 10 build 19041 or later'
            architecture = 'x64'
        }
        applicationVersion = [string]$contract.applicationVersion
        portableVersion = [string]$contract.portableVersion
        artifacts = $artifactRecords.ToArray()
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))

    $checksumFiles = @($releaseInputs | ForEach-Object {
            Get-Item -LiteralPath (Join-Path $release $_.Name) -Force
        }) + @(Get-Item -LiteralPath $manifestPath -Force)
    [Array]::Sort($checksumFiles, [Comparison[object]]{
            param($left, $right)
            [StringComparer]::Ordinal.Compare($left.Name, $right.Name)
        })
    $checksumLines = @($checksumFiles | ForEach-Object {
            '{0}  {1}' -f (
                Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant(),
                $_.Name
        })
    [IO.File]::WriteAllText(
        (Join-Path $release 'SHA256SUMS.txt'),
        ($checksumLines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    [IO.Directory]::Delete($working, $true)
    [IO.Directory]::Move($release, $target)
    Write-Output "Preview release staged: $target"
    Write-Output "Tag: $($contract.tag)"
    Write-Output 'Signature: NotSigned (personal evaluation/testing prerelease)'
}
finally {
    if ([IO.Directory]::Exists($staging)) {
        [IO.Directory]::Delete($staging, $true)
    }
}
