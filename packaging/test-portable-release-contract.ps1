[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assertions = 0

function Get-ContractText {
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Portable release contract file is missing: '$RelativePath'."
    }
    return [IO.File]::ReadAllText($path)
}

function Assert-ContractContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Label is missing '$Expected'."
    }
    $script:assertions++
}

function Assert-ContractDoesNotContain {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Forbidden,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Text.IndexOf($Forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "$Label contains forbidden text '$Forbidden'."
    }
    $script:assertions++
}

$build = Get-ContractText 'packaging/build-portable-release.ps1'
$verify = Get-ContractText 'packaging/verify-portable-release.ps1'
$helpers = Get-ContractText 'packaging/portable-release-helpers.ps1'
$workflow = Get-ContractText '.github/workflows/release-portable.yml'
$portableReadme = Get-ContractText 'docs/portable-readme.txt'
$releaseNotes = Get-ContractText 'docs/portable-release-notes.md'
$contractText = Get-ContractText 'packaging/portable-release.json'
$contract = $contractText | ConvertFrom-Json

if ([int]$contract.schemaVersion -ne 1 -or
    [string]$contract.version -cnotmatch '^\d+\.\d+\.\d+-portable\.\d+$' -or
    [string]$contract.tag -cne "v$($contract.version)" -or
    -not [bool]$contract.prerelease) {
    throw 'packaging/portable-release.json is invalid.'
}
$assertions++

foreach ($expected in @(
        "'-p:Packaged=false'",
        "'-p:ExternalIdentity=false'",
        "'-p:Portable=true'",
        "'-p:WarningsAsErrors=NU1903%3BNU1904'",
        "'--self-contained', 'true'",
        "'-p:DebugSymbols=false'",
        'Assert-EzyPortableSourceState',
        'Copy-EzyPortableThirdPartyFiles',
        'Write-EzyPackageContentsManifest',
        "'NotSigned'")) {
    Assert-ContractContains $build $expected 'Portable builder'
}
foreach ($expected in @(
        'Assert-EzyPackageContentsManifest',
        'Microsoft.WindowsAppSDK.WinUI',
        'MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW',
        "'NotSigned'",
        'AppxManifest',
        'Magick\.')) {
    Assert-ContractContains $verify $expected 'Portable verifier'
}
foreach ($expected in @(
        'THIRD-PARTY-LICENSES',
        'ZipArchiveMode',
        'CompressionLevel',
        '2020, 1, 1',
        'git -C $RepositoryRoot status')) {
    Assert-ContractContains $helpers $expected 'Portable helpers'
}
foreach ($expected in @(
        'workflow_run:',
        'head_branch ==',
        'permissions:',
        'contents: write',
        'Invoke-RestMethod',
        'StatusCode -eq 404',
        'build-portable-release.ps1',
        'verify-portable-release.ps1',
        'gh release create',
        '--prerelease')) {
    Assert-ContractContains $workflow $expected 'Portable release workflow'
}
Assert-ContractDoesNotContain $workflow 'gh release view' 'Portable release workflow'
foreach ($text in @($portableReadme, $releaseNotes)) {
    Assert-ContractContains $text 'Windows SmartScreen' 'Portable disclosure'
    Assert-ContractContains $text 'evaluation and testing' 'Portable disclosure'
    Assert-ContractContains $text 'Engineering Preview' 'Portable disclosure'
}

Write-Output "Portable release contract: $assertions assertions passed."
