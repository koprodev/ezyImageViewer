[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repoRoot ('obj\public-source-contract-' + [Guid]::NewGuid().ToString('N'))
$snapshotPath = Join-Path $testRoot 'snapshot'
$assertions = 0

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
    $script:assertions++
}

try {
    & (Join-Path $PSScriptRoot 'new-public-source-snapshot.ps1') -OutputDirectory $snapshotPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Public source snapshot generator failed.'
    }

    foreach ($requiredPath in @(
        'README.md',
        'LICENSE',
        'icon.png',
        'EzyImageViewer.slnx',
        'EzyImageViewer.App/EzyImageViewer.App.csproj',
        'installer/bundle/ezyImageViewer.Bundle.wixproj',
        '.github/workflows/ci.yml',
        '.github/workflows/release-portable.yml',
        '.github/workflows/release-preview.yml',
        'docs/portable-readme.txt',
        'docs/preview-release-notes.md',
        'docs/adr/ADR-0013-material-symbols-font-icons.md',
        'docs/adr/ADR-0019-external-location-identity-wix-transition.md',
        'packaging/build-portable-release.ps1',
        'packaging/public-source-allowlist.txt',
        'packaging/sync-public-source.ps1',
        'packaging/verify-portable-release.ps1',
        'packaging/portable-release.json'
    )) {
        $candidatePath = Join-Path $snapshotPath $requiredPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        Assert-True ([IO.File]::Exists($candidatePath)) "Required public source file is missing: $requiredPath"
    }

    foreach ($excludedPath in @(
        'AGENTS.md',
        'CLAUDE.md',
        'GEMINI.md',
        'PingPong.md',
        'PingPong_Checklist.md',
        'UI디자인.png',
        'UI디자인2.png',
        'ezyImageViewer_개발계획_요건정의서.md',
        'ezy_Image_Viewer_아이콘_시스템_명세.md',
        '배포파일정의서_참고용.md',
        'docs/RTM.md',
        'docs/reviews',
        'docs/spikes',
        'docs/adr/ADR-0001-toolchain-and-stack.md'
    )) {
        $candidatePath = Join-Path $snapshotPath $excludedPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        Assert-True (-not [IO.File]::Exists($candidatePath) -and -not [IO.Directory]::Exists($candidatePath)) `
            "Internal path leaked into the public source snapshot: $excludedPath"
    }

    Assert-True (-not [IO.Directory]::Exists((Join-Path $snapshotPath '.git'))) `
        'The public source snapshot must not contain Git metadata.'

    $manifestPath = Join-Path $snapshotPath 'PUBLIC-SOURCE-MANIFEST.json'
    Assert-True ([IO.File]::Exists($manifestPath)) 'The public source manifest is missing.'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 2) 'The public source manifest schema is not allowlist-based.'
    $head = (& git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
    Assert-True ($manifest.sourceCommit -ceq $head) 'The public source manifest does not identify HEAD.'

    $allowlist = @(
        [IO.File]::ReadAllLines((Join-Path $repoRoot 'packaging/public-source-allowlist.txt')) |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('#', [StringComparison]::Ordinal) }
    )
    Assert-True ((@($manifest.allowlistedPaths) -join "`n") -ceq ($allowlist -join "`n")) `
        'The public source manifest does not preserve the exact allowlist.'

    $manifestFiles = @($manifest.files)
    Assert-True ($manifest.payloadFileCount -eq $manifestFiles.Count) `
        'The public source manifest file count is inconsistent.'
    Assert-True (@($manifestFiles | Where-Object { $_.path -ceq 'PUBLIC-SOURCE-MANIFEST.json' }).Count -eq 0) `
        'The public source manifest must not include itself as payload.'
    foreach ($file in $manifestFiles) {
        $candidatePath = Join-Path $snapshotPath $file.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        Assert-True ([IO.File]::Exists($candidatePath)) "Manifest file is missing: $($file.path)"
        $actualHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
        Assert-True ($file.sha256 -ceq $actualHash) "Manifest hash mismatch: $($file.path)"
    }

    $actualPayloadCount = @(
        Get-ChildItem -LiteralPath $snapshotPath -Recurse -Force -File |
            Where-Object { $_.Name -cne 'PUBLIC-SOURCE-MANIFEST.json' }
    ).Count
    Assert-True ($actualPayloadCount -eq $manifest.payloadFileCount) `
        'The public source manifest does not cover the exact payload file set.'

    Write-Output "Public source snapshot contract: $assertions assertions passed."
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
