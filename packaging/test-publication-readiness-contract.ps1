[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pathSeparators = [char[]]@(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd($pathSeparators)
$repoRootPrefix = $repoRoot + [IO.Path]::DirectorySeparatorChar
$assertions = 0

function Get-RepoText {
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    $isRepositoryPath = $path.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $path.StartsWith($repoRootPrefix, [StringComparison]::OrdinalIgnoreCase)
    if (-not $isRepositoryPath -or
        -not [IO.File]::Exists($path)) {
        throw "Required publication file is missing: $RelativePath"
    }

    return [IO.File]::ReadAllText($path)
}

function Assert-ContainsLiteral {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Label is missing required text: $Expected"
    }
    $script:assertions++
}

function Assert-DoesNotContainLiteral {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Forbidden,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Text.IndexOf($Forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "$Label contains forbidden text: $Forbidden"
    }
    $script:assertions++
}

$readme = Get-RepoText 'README.md'
$privacy = Get-RepoText 'docs/privacy.md'
$signing = Get-RepoText 'docs/code-signing-policy.md'
$readiness = Get-RepoText 'docs/signpath-readiness.md'
$releaseProcess = Get-RepoText 'docs/release-process.md'
$publicSourceAllowlist = Get-RepoText 'packaging/public-source-allowlist.txt'
$publicSourceGenerator = Get-RepoText 'packaging/new-public-source-snapshot.ps1'
$publicSourceSync = Get-RepoText 'packaging/sync-public-source.ps1'
$workflow = Get-RepoText '.github/workflows/ci.yml'
$portableWorkflow = Get-RepoText '.github/workflows/release-portable.yml'
$previewWorkflow = Get-RepoText '.github/workflows/release-preview.yml'

Assert-ContainsLiteral $readme '(docs/privacy.md)' 'README'
Assert-ContainsLiteral $readme '(docs/code-signing-policy.md)' 'README'
Assert-ContainsLiteral $readme '(docs/signpath-readiness.md)' 'README'
Assert-ContainsLiteral $readme 'There is no production-signed public installer yet' 'README'

Assert-ContainsLiteral $privacy `
    'This program will not transfer any information to other networked systems' `
    'Privacy policy'
Assert-ContainsLiteral $privacy 'automatic version checks' 'Privacy policy'

Assert-ContainsLiteral $signing `
    'SignPath Foundation has not accepted this project' `
    'Code signing policy'
Assert-ContainsLiteral $signing `
    'Free code signing provided by SignPath.io, certificate by SignPath Foundation' `
    'Code signing policy'
Assert-ContainsLiteral $signing 'Unsigned or self-signed binaries are not public production releases.' `
    'Code signing policy'

Assert-ContainsLiteral $readiness 'no SignPath application or signing transfer performed' `
    'SignPath readiness checklist'
Assert-ContainsLiteral $readiness 'Microsoft Windows App SDK Engineering Preview' `
    'SignPath readiness checklist'

Assert-ContainsLiteral $readme 'Basic Portable preview' `
    'README'
Assert-ContainsLiteral $readme 'Installer + Portable preview' `
    'README'
Assert-ContainsLiteral $releaseProcess 'koprodev/ezyImageViewer' `
    'Release process'
Assert-ContainsLiteral $releaseProcess '0.1.0-portable.1' `
    'Release process'
Assert-ContainsLiteral $releaseProcess 'PUBLIC-SOURCE-MANIFEST.json' `
    'Release process'
Assert-ContainsLiteral $releaseProcess 'allowlist' 'Release process'
Assert-ContainsLiteral $publicSourceAllowlist 'EzyImageViewer.App/' `
    'Public source allowlist'
Assert-ContainsLiteral $publicSourceAllowlist 'installer/' `
    'Public source allowlist'
Assert-DoesNotContainLiteral $publicSourceAllowlist 'UI디자인.png' `
    'Public source allowlist'
Assert-DoesNotContainLiteral $publicSourceAllowlist '배포파일정의서_참고용.md' `
    'Public source allowlist'
Assert-ContainsLiteral $publicSourceGenerator 'git -C $repoRoot archive' `
    'Public source generator'
Assert-ContainsLiteral $publicSourceGenerator 'PUBLIC-SOURCE-MANIFEST.json' `
    'Public source generator'
Assert-ContainsLiteral $publicSourceSync 'ezyImageViewer-public' `
    'Public source synchronizer'
Assert-ContainsLiteral $publicSourceSync 'status --porcelain=v1' `
    'Public source synchronizer'

Assert-DoesNotContainLiteral $workflow 'name: unsigned-msix-release-gate' 'CI workflow'
Assert-DoesNotContainLiteral $workflow 'name: unsigned-wix-installer-gate' 'CI workflow'
Assert-DoesNotContainLiteral $workflow 'installer/out/ci-wix/*.msi' 'CI workflow'
Assert-DoesNotContainLiteral $workflow 'installer/out/ci-wix/*.exe' 'CI workflow'
Assert-ContainsLiteral $portableWorkflow 'workflow_run:' 'Portable release workflow'
Assert-ContainsLiteral $portableWorkflow 'contents: write' 'Portable release workflow'
Assert-ContainsLiteral $portableWorkflow 'StatusCode -eq 404' 'Portable release workflow'
Assert-ContainsLiteral $portableWorkflow 'verify-portable-release.ps1' 'Portable release workflow'
Assert-ContainsLiteral $portableWorkflow 'gh release create' 'Portable release workflow'
Assert-DoesNotContainLiteral $portableWorkflow 'gh release view' 'Portable release workflow'
Assert-ContainsLiteral $previewWorkflow 'workflow_dispatch:' 'Preview release workflow'
Assert-ContainsLiteral $previewWorkflow 'contents: write' 'Preview release workflow'
Assert-ContainsLiteral $previewWorkflow 'build-preview-release.ps1' 'Preview release workflow'
Assert-ContainsLiteral $previewWorkflow 'verify-preview-release.ps1' 'Preview release workflow'
Assert-ContainsLiteral $previewWorkflow 'gh release create' 'Preview release workflow'
Assert-ContainsLiteral $previewWorkflow '--prerelease' 'Preview release workflow'

$uploadArtifactUseCount = [regex]::Matches(
    $workflow,
    '(?m)^\s*-\s*uses:\s*actions/upload-artifact@'
).Count
if ($uploadArtifactUseCount -ne 1) {
    throw "CI workflow must contain exactly one test-results upload-artifact use; found $uploadArtifactUseCount."
}
$assertions++

$sensitiveNamePattern = '(?i)(^|/)(\.env($|\.)|id_(rsa|ecdsa|ed25519)$|' +
    '[^/]*\.(pfx|p12|p8|pem|key|snk|kdbx|jks|keystore|ppk|gpg|asc)$|' +
    'credentials?[^/]*|secrets?[^/]*)'
$trackedFiles = @(& git -C $repoRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files failed while checking publication inputs.'
}

$sensitiveFiles = @($trackedFiles | Where-Object { $_ -match $sensitiveNamePattern })
if ($sensitiveFiles.Count -ne 0) {
    throw "Tracked sensitive file names require review: $($sensitiveFiles -join ', ')"
}
$assertions++

Write-Output "Publication readiness contract: $assertions assertions passed."
