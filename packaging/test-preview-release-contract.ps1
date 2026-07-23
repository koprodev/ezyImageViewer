[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assertions = 0

function Get-ContractText([string]$RelativePath) {
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not [IO.File]::Exists($path)) { throw "Missing preview release file: '$RelativePath'." }
    return [IO.File]::ReadAllText($path)
}

function Assert-Contains([string]$Text, [string]$Expected, [string]$Label) {
    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Label is missing '$Expected'."
    }
    $script:assertions++
}

$contract = Get-ContractText 'packaging/preview-release.json' | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1 -or
    [string]$contract.releaseVersion -cnotmatch '^\d+\.\d+\.\d+-preview\.\d+$' -or
    [string]$contract.tag -cne "v$($contract.releaseVersion)" -or
    [string]$contract.applicationVersion -cnotmatch '^\d+\.\d+\.\d+\.\d+$' -or
    [string]$contract.portableVersion -cnotmatch '^\d+\.\d+\.\d+-portable\.\d+$' -or
    -not [bool]$contract.prerelease) {
    throw 'Preview release JSON contract is invalid.'
}
$assertions++

$singleBuild = Get-ContractText 'packaging/build-single-file-portable.ps1'
$singleVerify = Get-ContractText 'packaging/verify-single-file-portable.ps1'
$releaseBuild = Get-ContractText 'packaging/build-preview-release.ps1'
$releaseVerify = Get-ContractText 'packaging/verify-preview-release.ps1'
$wixBuild = Get-ContractText 'packaging/build-wix-installer.ps1'
$bundleSource = Get-ContractText 'installer/bundle/Bundle.wxs'
$productSource = Get-ContractText 'installer/common/Product.wxs'
$themeSource = Get-ContractText 'installer/bundle/EzyRtfLargeTheme.xml'
$bundleProject = Get-ContractText 'installer/bundle/ezyImageViewer.Bundle.wixproj'
$englishLocalization = Get-ContractText 'installer/bundle/Bundle.en-US.wxl'
$koreanLocalization = Get-ContractText 'installer/bundle/Bundle.ko-KR.wxl'
[void](Get-ContractText 'installer/assets/EULA.ko-KR.txt')
$targets = Get-ContractText 'packaging/SingleFilePublish.targets'
$workflow = Get-ContractText '.github/workflows/release-preview.yml'
$notes = Get-ContractText 'docs/preview-release-notes.md'

foreach ($expected in @(
        '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:EnableMsixTooling=true',
        '-t:Rebuild', '-p:CustomBeforeMicrosoftCommonProps=', 'Copy-EzyPortableThirdPartyFiles',
        'WindowsAppSDK.manifest', '[IO.File]::Delete', 'SingleFilePublish.targets',
        "'ezyImageViewer.exe'", "'NotSigned'")) {
    Assert-Contains $singleBuild $expected 'Single-file portable builder'
}
foreach ($expected in @(
        'DOTNET_BUNDLE_EXTRACT_BASE_DIR', "'Ready'", 'THIRD-PARTY-LICENSES',
        'Microsoft.WindowsAppSDK.WinUI', "'ezyImageViewer.exe'", "'NotSigned'", '.Arguments =',
        '.EnvironmentVariables[')) {
    Assert-Contains $singleVerify $expected 'Single-file portable verifier'
}
foreach ($expected in @(
        'build-single-file-portable.ps1', 'pack-msix.ps1', '-SkipSign',
        'build-wix-installer.ps1', '-DevelopmentUnsigned', 'EzyRtfLargeTheme.xml',
        'LICENSE-MRL.txt', 'preview-release-manifest.json', 'SHA256SUMS.txt',
        'Join-Path $scriptRoot ''preview-release.json''')) {
    Assert-Contains $releaseBuild $expected 'Preview release builder'
}
foreach ($expected in @('Bundle.en-US.wxl', 'Bundle.ko-KR.wxl', 'EULA.ko-KR.txt',
        'ConvertTo-Rtf')) {
    Assert-Contains $wixBuild $expected 'WiX installer builder'
}
foreach ($expected in @('WixStdBAScope = &quot;PerUser&quot;',
        'WixStdBAScope = &quot;PerMachine&quot;', 'LaunchTarget="[LaunchTargetPath]"',
        '1042\thm.wxl', '1042\license.rtf',
        'EzyFileAssociations" Type="numeric" Value="1"')) {
    Assert-Contains $bundleSource $expected 'WiX bundle source'
}
Assert-Contains $productSource 'Feature Id="ImageOpenWith"' 'WiX MSI product source'
foreach ($expected in @('#(loc.OptionsDesktopShortcutText)',
        '#(loc.OptionsFileAssociationsText)')) {
    Assert-Contains $themeSource $expected 'WiX bundle theme'
}
foreach ($localization in @($englishLocalization, $koreanLocalization)) {
    foreach ($expected in @('OptionsDesktopShortcutText', 'OptionsFileAssociationsText',
            'SuccessLaunchButton')) {
        Assert-Contains $localization $expected 'WiX bundle localization'
    }
}
Assert-Contains $bundleProject '<EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>' `
    'WiX bundle project'
foreach ($expected in @(
        'verify-single-file-portable.ps1', 'verify-wix-bundle.ps1',
        'personal-evaluation-and-testing-preview', "'NotSigned'",
        'Join-Path $scriptRoot ''preview-release.json''',
        '[StringComparer]::Ordinal.Compare')) {
    Assert-Contains $releaseVerify $expected 'Preview release verifier'
}
foreach ($expected in @(
        '<None Include=', 'THIRD-PARTY-LICENSES', 'PORTABLE-README.txt',
        'CopyToPublishDirectory')) {
    Assert-Contains $targets $expected 'Single-file publish targets'
}
foreach ($expected in @(
        'workflow_dispatch:', 'permissions:', 'contents: write',
        'build-preview-release.ps1', 'verify-preview-release.ps1',
        'gh release create', '--prerelease')) {
    Assert-Contains $workflow $expected 'Preview release workflow'
}
foreach ($expected in @(
        'Windows SmartScreen', 'unsigned', 'evaluation and testing',
        'file association', 'Engineering Preview')) {
    Assert-Contains $notes $expected 'Preview release notes'
}

Write-Output "Preview release contract: $assertions assertions passed."
