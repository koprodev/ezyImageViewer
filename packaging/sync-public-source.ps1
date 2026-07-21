[CmdletBinding()]
param(
    [string]$PublicDirectory = (Join-Path (Split-Path -Parent (
            [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))) 'ezyImageViewer-public'),

    [ValidateNotNullOrEmpty()]
    [string]$Revision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$publicPath = [IO.Path]::GetFullPath($PublicDirectory).TrimEnd('\', '/')
$sourcePrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
$publicPrefix = $publicPath + [IO.Path]::DirectorySeparatorChar

if ($publicPath.Equals([IO.Path]::GetPathRoot($publicPath), [StringComparison]::OrdinalIgnoreCase) -or
    $publicPath.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $publicPath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($publicPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PublicDirectory must be a separate non-root directory outside the development repository.'
}
if (-not [IO.Directory]::Exists($publicPath)) {
    throw "PublicDirectory does not exist: $publicPath"
}

$publicItem = Get-Item -LiteralPath $publicPath -Force
if (($publicItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PublicDirectory must be a physical directory.'
}

$publicGitRoot = @(& git -C $publicPath rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or $publicGitRoot.Count -ne 1 -or
    -not [IO.Path]::GetFullPath($publicGitRoot[0]).TrimEnd('\', '/').Equals(
        $publicPath,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PublicDirectory must be the root of a separate Git repository.'
}

$publicStatus = @(& git -C $publicPath status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the public working tree.'
}
if ($publicStatus.Count -ne 0) {
    throw 'PublicDirectory contains uncommitted changes; commit or discard them before syncing.'
}

$operationId = [Guid]::NewGuid().ToString('N')
$snapshotPath = Join-Path ([IO.Path]::GetDirectoryName($publicPath)) `
    ".ezy-public-sync-$operationId"

try {
    & (Join-Path $PSScriptRoot 'new-public-source-snapshot.ps1') `
        -OutputDirectory $snapshotPath `
        -Revision $Revision
    if ($LASTEXITCODE -ne 0) {
        throw 'Public source snapshot generation failed.'
    }

    & robocopy $snapshotPath $publicPath /MIR /XD (Join-Path $publicPath '.git') `
        /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Public working tree synchronization failed with robocopy exit code $LASTEXITCODE."
    }

    $manifestPath = Join-Path $publicPath 'PUBLIC-SOURCE-MANIFEST.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    foreach ($file in @($manifest.files)) {
        $candidate = Join-Path $publicPath $file.path.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar)
        if (-not [IO.File]::Exists($candidate) -or
            (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne $file.sha256) {
            throw "Synchronized public file does not match its manifest: $($file.path)"
        }
    }

    Write-Output "Public working tree synchronized from $($manifest.sourceCommit): $publicPath"
    & git -C $publicPath status --short
}
finally {
    if ([IO.Directory]::Exists($snapshotPath)) {
        Remove-Item -LiteralPath $snapshotPath -Recurse -Force
    }
}
