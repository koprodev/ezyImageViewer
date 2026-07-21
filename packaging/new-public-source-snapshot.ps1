[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [ValidateNotNullOrEmpty()]
    [string]$Revision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pathSeparators = [char[]]@(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd($pathSeparators)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd($pathSeparators)
$allowlistFile = Join-Path $PSScriptRoot 'public-source-allowlist.txt'

if ($outputPath.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The public source snapshot cannot replace the repository root.'
}
if ([IO.File]::Exists($outputPath) -or [IO.Directory]::Exists($outputPath)) {
    throw "OutputDirectory must not already exist: $outputPath"
}
if (-not [IO.File]::Exists($allowlistFile)) {
    throw "Public source allowlist is missing: $allowlistFile"
}

$revisionOutput = @(& git -C $repoRoot rev-parse --verify "$Revision`^{commit}" 2>&1)
if ($LASTEXITCODE -ne 0 -or $revisionOutput.Count -ne 1) {
    throw "Unable to resolve source revision '$Revision': $($revisionOutput -join [Environment]::NewLine)"
}
$sourceCommit = $revisionOutput[0].Trim()
if ($sourceCommit -notmatch '\A[0-9a-fA-F]{40}\z') {
    throw "Resolved source revision is not a full commit ID: $sourceCommit"
}

$allowlistedPaths = @(
    [IO.File]::ReadAllLines($allowlistFile) |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('#', [StringComparison]::Ordinal) }
)
if ($allowlistedPaths.Count -eq 0) {
    throw 'The public source allowlist is empty.'
}

$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($relativePath in $allowlistedPaths) {
    $normalizedPath = $relativePath.Replace('\', '/')
    $treePath = $normalizedPath.TrimEnd('/')
    $segments = @($treePath.Split('/'))
    if ($relativePath -cne $normalizedPath -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $segments.Count -eq 0 -or
        $segments -contains '' -or
        $segments -contains '.' -or
        $segments -contains '..') {
        throw "Public source allowlist entry must be a normalized repository-relative path: $relativePath"
    }
    if (-not $seenPaths.Add($relativePath)) {
        throw "Public source allowlist contains a duplicate entry: $relativePath"
    }

    & git -C $repoRoot cat-file -e "$sourceCommit`:$treePath" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Public source allowlist entry does not exist at revision '$Revision': $relativePath"
    }
}

$expectedFiles = @(& git -C $repoRoot ls-tree -r --name-only $sourceCommit -- @allowlistedPaths)
if ($LASTEXITCODE -ne 0 -or $expectedFiles.Count -eq 0) {
    throw 'Unable to enumerate the allowlisted public source files.'
}
$expectedFiles = @($expectedFiles | Sort-Object -Unique)

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputParent)) {
    throw "OutputDirectory has no parent: $outputPath"
}
[IO.Directory]::CreateDirectory($outputParent) | Out-Null

$operationId = [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $outputParent ".public-source-staging-$operationId"
$archivePath = Join-Path ([IO.Path]::GetTempPath()) "ezy-image-viewer-source-$operationId.zip"

try {
    [IO.Directory]::CreateDirectory($stagingPath) | Out-Null

    $archiveOutput = @(& git -C $repoRoot archive --format=zip "--output=$archivePath" `
            $sourceCommit -- @allowlistedPaths 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($archivePath)) {
        throw "git archive failed: $($archiveOutput -join [Environment]::NewLine)"
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingPath
    $stagingPrefix = $stagingPath.TrimEnd($pathSeparators) + [IO.Path]::DirectorySeparatorChar
    $manifestFileName = 'PUBLIC-SOURCE-MANIFEST.json'
    $relativeFiles = @(
        Get-ChildItem -LiteralPath $stagingPath -Recurse -Force -File |
            ForEach-Object {
                $_.FullName.Substring($stagingPrefix.Length).Replace('\', '/')
            } |
            Where-Object { $_ -cne $manifestFileName } |
            Sort-Object -Unique
    )
    $unexpectedFiles = @($relativeFiles | Where-Object { $expectedFiles -cnotcontains $_ })
    $missingFiles = @($expectedFiles | Where-Object { $relativeFiles -cnotcontains $_ })
    if ($unexpectedFiles.Count -ne 0 -or $missingFiles.Count -ne 0) {
        throw "Public source archive differs from its allowlist (unexpected: $($unexpectedFiles -join ', '); missing: $($missingFiles -join ', '))."
    }

    $sensitiveNamePattern = '(?i)(^|/)(\.env($|\.)|id_(rsa|ecdsa|ed25519)$|' +
        '[^/]*\.(pfx|p12|p8|pem|key|snk|kdbx|jks|keystore|ppk|gpg|asc)$|' +
        'credentials?[^/]*|secrets?[^/]*)'
    $sensitiveFiles = @($relativeFiles | Where-Object { $_ -match $sensitiveNamePattern })
    if ($sensitiveFiles.Count -ne 0) {
        throw "Public source snapshot contains sensitive file names: $($sensitiveFiles -join ', ')"
    }

    [Array]::Sort($relativeFiles, [StringComparer]::Ordinal)
    $fileRecords = @(
        foreach ($relativePath in $relativeFiles) {
            $platformPath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $filePath = Join-Path $stagingPath $platformPath
            [ordered]@{
                path = $relativePath
                sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
            }
        }
    )

    $manifest = [ordered]@{
        schemaVersion = 2
        sourceCommit = $sourceCommit.ToLowerInvariant()
        allowlistedPaths = @($allowlistedPaths)
        payloadFileCount = $fileRecords.Count
        files = $fileRecords
    }
    $manifestPath = Join-Path $stagingPath $manifestFileName
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $stagingPath -Destination $outputPath
    Write-Output "Public source snapshot: $($fileRecords.Count) allowlisted payload files from $sourceCommit"
    Write-Output "Output: $outputPath"
}
finally {
    if ([IO.File]::Exists($archivePath)) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    if ([IO.Directory]::Exists($stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
