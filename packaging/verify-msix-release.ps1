# Verifies a main/CodecHost MSIX pair without installing it or changing certificate stores.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainPackage,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostPackage,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostVersion,

    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    [string]$HashesFile,
    [string]$AppInstallerFile,
    [string]$BuildToolsRoot,
    [switch]$RequireSignature,
    [switch]$RequireBuildOutputMatch
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-helpers.ps1')

function Assert-MsixVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
        throw "$Label must be a canonical four-part numeric version: '$Value'."
    }

    foreach ($part in $Value.Split('.')) {
        if ([uint64]::Parse($part, [Globalization.CultureInfo]::InvariantCulture) -gt 65535) {
            throw "$Label contains a part outside the MSIX range 0..65535: '$Value'."
        }
    }
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "$Label must be a file: '$Path'."
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: '$Path'."
    }
    return $item.FullName
}

function Get-MakeAppxPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $binRoot = Join-Path $Root 'bin'
    $candidates = @(Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction Stop |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ })
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one x64 makeappx.exe under '$binRoot'; found $($candidates.Count)."
    }
    return $candidates[0]
}

function Get-SignToolPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $binRoot = Join-Path $Root 'bin'
    $candidates = @(Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction Stop |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ })
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one x64 signtool.exe under '$binRoot'; found $($candidates.Count)."
    }
    return $candidates[0]
}

function Expand-Msix {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MakeAppx,

        [Parameter(Mandatory = $true)]
        [string]$Package,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & $MakeAppx unpack /o /p $Package /d $Destination | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx unpack failed for '$Package' ($LASTEXITCODE)."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Destination 'AppxManifest.xml'))) {
        throw "Unpacked package has no AppxManifest.xml: '$Package'."
    }
}

function Get-ManifestNode {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$XPath,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $node = $Manifest.SelectSingleNode($XPath)
    if ($null -eq $node) {
        throw "Manifest node is missing: $Label."
    }
    return $node
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not [string]::Equals($Actual, $Expected, [StringComparison]::Ordinal)) {
        throw "$Label mismatch: expected '$Expected', actual '$Actual'."
    }
}

function Get-RelativeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $prefix = $Root.TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File |
        ForEach-Object { $_.FullName.Substring($prefix.Length).Replace('\', '/') } |
        Sort-Object)
}

function Assert-ContainsFile {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Files,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$PackageLabel
    )

    if (-not ($Files -contains $Expected)) {
        throw "$PackageLabel is missing required file '$Expected'."
    }
}

function Test-Hashes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [Collections.Generic.IDictionary[string, string]]$ArtifactsByName
    )

    $hashPath = Resolve-ExistingFile -Path $Path -Label 'HashesFile'
    $names = New-Object 'System.Collections.Generic.List[string]'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($line in Get-Content -LiteralPath $hashPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9A-Fa-f]{64})  ([^\\/]+)$') {
            throw "Invalid SHA256SUMS line: '$line'."
        }

        $expected = $Matches[1].ToUpperInvariant()
        $name = $Matches[2]
        if (-not $seen.Add($name)) {
            throw "Duplicate SHA256SUMS entry: '$name'."
        }
        $names.Add($name)

        if (-not $ArtifactsByName.ContainsKey($name)) {
            throw "SHA256SUMS contains an unspecified artifact: '$name'."
        }
        $artifact = [string]$ArtifactsByName[$name]
        $actual = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
            throw "SHA-256 mismatch for '$name'."
        }
    }

    $sorted = $names.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    if ([string]::Join("`n", $names) -cne [string]::Join("`n", $sorted)) {
        throw 'SHA256SUMS entries are not sorted by artifact name.'
    }
    if ($seen.Count -ne $ArtifactsByName.Count) {
        throw "SHA256SUMS entry count mismatch: expected $($ArtifactsByName.Count), actual $($seen.Count)."
    }
    foreach ($name in $ArtifactsByName.Keys) {
        if (-not $seen.Contains($name)) {
            throw "SHA256SUMS does not contain required package '$name'."
        }
    }
}

Assert-MsixVersion -Value $Version -Label 'Version'
Assert-MsixVersion -Value $CodecHostVersion -Label 'CodecHostVersion'
if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher.Contains('{{')) {
    throw "Publisher is empty or unresolved: '$Publisher'."
}
if (-not $RequireSignature -and -not $RequireBuildOutputMatch) {
    throw 'Verification requires either -RequireSignature or -RequireBuildOutputMatch.'
}
try {
    [void][Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher)
}
catch {
    throw "Publisher is not a valid X.500 distinguished name: '$Publisher'."
}

$repo = Split-Path $PSScriptRoot -Parent
$mainPath = Resolve-ExistingFile -Path $MainPackage -Label 'MainPackage'
$codecPath = Resolve-ExistingFile -Path $CodecHostPackage -Label 'CodecHostPackage'
if ([IO.Path]::GetExtension($mainPath) -ine '.msix' -or
    [IO.Path]::GetExtension($codecPath) -ine '.msix') {
    throw 'MainPackage and CodecHostPackage must both use the .msix extension.'
}
if ([string]::Equals($mainPath, $codecPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MainPackage and CodecHostPackage must be different files.'
}
$mainName = Split-Path $mainPath -Leaf
$codecName = Split-Path $codecPath -Leaf
if ([string]::Equals($mainName, $codecName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MainPackage and CodecHostPackage must have different basenames.'
}

if (-not [string]::IsNullOrWhiteSpace($AppInstallerFile) -and
    [string]::IsNullOrWhiteSpace($HashesFile)) {
    throw 'AppInstallerFile requires HashesFile so the third artifact is actually verified.'
}
if (-not [string]::IsNullOrWhiteSpace($HashesFile)) {
    $artifactsByName = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $artifactsByName.Add($mainName, $mainPath)
    $artifactsByName.Add($codecName, $codecPath)
    if (-not [string]::IsNullOrWhiteSpace($AppInstallerFile)) {
        $appInstallerPath = Resolve-ExistingFile `
            -Path $AppInstallerFile -Label 'AppInstallerFile'
        if ([IO.Path]::GetExtension($appInstallerPath) -ine '.appinstaller') {
            throw 'AppInstallerFile must use the .appinstaller extension.'
        }
        $appInstallerName = Split-Path $appInstallerPath -Leaf
        if ($artifactsByName.ContainsKey($appInstallerName)) {
            throw "AppInstallerFile basename collides with another artifact: '$appInstallerName'."
        }
        $artifactsByName.Add($appInstallerName, $appInstallerPath)
    }
    Test-Hashes -Path $HashesFile -ArtifactsByName $artifactsByName
}

$projectAssetsPaths = @(
    (Join-Path $repo 'EzyImageViewer.App\obj\packaged\project.assets.json'),
    (Join-Path $repo 'EzyImageViewer.CodecHost\obj\project.assets.json'))
$toolsRoot = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repo `
    -ProjectAssetsPaths $projectAssetsPaths -ExplicitRoot $BuildToolsRoot
$makeAppx = Get-MakeAppxPath -Root $toolsRoot
if ($RequireSignature) {
    $signTool = Get-SignToolPath -Root $toolsRoot
    foreach ($package in @($mainPath, $codecPath)) {
        & $signTool verify /pa /all $package | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed for '$package' ($LASTEXITCODE)."
        }
    }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'ezyImageViewer-msix-verify-' + [Guid]::NewGuid().ToString('N'))
$mainRoot = Join-Path $scratch 'main'
$codecRoot = Join-Path $scratch 'codec-host'

try {
    Expand-Msix -MakeAppx $makeAppx -Package $mainPath -Destination $mainRoot
    Expand-Msix -MakeAppx $makeAppx -Package $codecPath -Destination $codecRoot
    Assert-EzyPackageContentsManifest -UnpackedRoot $mainRoot -PackageLabel 'Main MSIX'
    Assert-EzyPackageContentsManifest -UnpackedRoot $codecRoot -PackageLabel 'CodecHost MSIX'
    if ($RequireBuildOutputMatch) {
        $mainBuildOutput = Join-Path $repo 'EzyImageViewer.App\bin\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
        $mainIntermediate = Join-Path $repo 'EzyImageViewer.App\obj\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
        $codecBuildOutput = Join-Path $repo 'EzyImageViewer.CodecHost\bin\Release\net10.0\win-x64'
        $codecIntermediate = Join-Path $repo 'EzyImageViewer.CodecHost\obj\Release\net10.0\win-x64'
        $mainFileList = Get-EzyFileListPath -IntermediateRoot $mainIntermediate
        $codecFileList = Get-EzyFileListPath -IntermediateRoot $codecIntermediate
        $mainAdditionalFiles = @{
            'Assets/Square44x44Logo.png' = Join-Path $PSScriptRoot 'Assets\Square44x44Logo.png'
            'Assets/Square150x150Logo.png' = Join-Path $PSScriptRoot 'Assets\Square150x150Logo.png'
            'Assets/StoreLogo.png' = Join-Path $PSScriptRoot 'Assets\StoreLogo.png'
        }
        $codecAdditionalFiles = @{
            'Assets/StoreLogo.png' = Join-Path $PSScriptRoot 'Assets\StoreLogo.png'
        }
        Assert-EzyPackageMatchesBuildOutput -UnpackedRoot $mainRoot `
            -BuildOutput $mainBuildOutput -FileListPath $mainFileList `
            -AdditionalSourceFiles $mainAdditionalFiles -PackageLabel 'Main MSIX'
        Assert-EzyPackageMatchesBuildOutput -UnpackedRoot $codecRoot `
            -BuildOutput $codecBuildOutput -FileListPath $codecFileList `
            -AdditionalSourceFiles $codecAdditionalFiles -PackageLabel 'CodecHost MSIX'
    }

    [xml]$mainManifest = Get-Content -LiteralPath (Join-Path $mainRoot 'AppxManifest.xml')
    [xml]$codecManifest = Get-Content -LiteralPath (Join-Path $codecRoot 'AppxManifest.xml')
    $mainIdentity = Get-ManifestNode -Manifest $mainManifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Identity']" -Label 'main Identity'
    $codecIdentity = Get-ManifestNode -Manifest $codecManifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Identity']" -Label 'CodecHost Identity'

    Assert-Equal $mainIdentity.GetAttribute('Name') 'GRTech.ezyImageViewer' 'main identity name'
    Assert-Equal $mainIdentity.GetAttribute('Version') $Version 'main identity version'
    Assert-Equal $mainIdentity.GetAttribute('Publisher') $Publisher 'main publisher'
    Assert-Equal $mainIdentity.GetAttribute('ProcessorArchitecture') 'x64' 'main architecture'
    Assert-Equal $codecIdentity.GetAttribute('Name') 'GRTech.ezyImageViewer.CodecHost' 'CodecHost identity name'
    Assert-Equal $codecIdentity.GetAttribute('Version') $CodecHostVersion 'CodecHost identity version'
    Assert-Equal $codecIdentity.GetAttribute('Publisher') $Publisher 'CodecHost publisher'
    Assert-Equal $codecIdentity.GetAttribute('ProcessorArchitecture') 'x64' 'CodecHost architecture'

    $dependencies = @($mainManifest.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($dependencies.Count -ne 1) {
        throw "Main manifest must contain exactly one package dependency; found $($dependencies.Count)."
    }
    Assert-Equal $dependencies[0].GetAttribute('Name') `
        'GRTech.ezyImageViewer.CodecHost' 'CodecHost dependency name'
    Assert-Equal $dependencies[0].GetAttribute('Publisher') $Publisher 'CodecHost dependency publisher'
    Assert-Equal $dependencies[0].GetAttribute('MinVersion') `
        $CodecHostVersion 'CodecHost dependency minimum version'

    $applications = @($mainManifest.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applications.Count -ne 1) {
        throw "Main manifest must contain exactly one Application; found $($applications.Count)."
    }
    Assert-Equal $applications[0].GetAttribute('Id') 'App' 'main application id'
    Assert-Equal $applications[0].GetAttribute('Executable') `
        'ezyImageViewer.exe' 'main application executable'
    Assert-Equal $applications[0].GetAttribute('EntryPoint') `
        'Windows.FullTrustApplication' 'main application entry point'

    $extensions = @($mainManifest.SelectNodes("//*[local-name()='Extension']"))
    if ($extensions.Count -ne 1) {
        throw "Main manifest must contain exactly one extension; found $($extensions.Count)."
    }
    Assert-Equal $extensions[0].NamespaceURI `
        'http://schemas.microsoft.com/appx/manifest/uap/windows10' 'main extension namespace'
    Assert-Equal $extensions[0].GetAttribute('Category') `
        'windows.protocol' 'main extension category'
    $protocols = @($extensions[0].SelectNodes("*[local-name()='Protocol']"))
    if ($protocols.Count -ne 1) {
        throw "Main protocol extension must contain exactly one Protocol; found $($protocols.Count)."
    }
    Assert-Equal $protocols[0].GetAttribute('Name') 'ezyimageviewer' 'main protocol name'

    $capabilities = @($mainManifest.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Capabilities']/*"))
    if ($capabilities.Count -ne 1) {
        throw "Main manifest must contain exactly one capability; found $($capabilities.Count)."
    }
    Assert-Equal $capabilities[0].LocalName 'Capability' 'main capability element'
    Assert-Equal $capabilities[0].NamespaceURI `
        'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities' `
        'main capability namespace'
    Assert-Equal $capabilities[0].GetAttribute('Name') 'runFullTrust' 'main capability name'

    $framework = Get-ManifestNode -Manifest $codecManifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Framework']" `
        -Label 'CodecHost Framework'
    Assert-Equal $framework.InnerText.ToLowerInvariant() 'true' 'CodecHost framework flag'
    if ($null -ne $codecManifest.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Applications']")) {
        throw 'CodecHost framework package must not expose Applications.'
    }
    if ($null -ne $codecManifest.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Capabilities']")) {
        throw 'CodecHost framework package must not expose Capabilities.'
    }
    if (@($codecManifest.SelectNodes("//*[local-name()='Extension']")).Count -ne 0) {
        throw 'CodecHost framework package must not expose Extensions.'
    }

    $mainFiles = Get-RelativeFiles -Root $mainRoot
    $codecFiles = Get-RelativeFiles -Root $codecRoot
    foreach ($file in $mainFiles) {
        if ($file -match '(?i)(^|/)CodecHost(/|$)' -or
            $file -match '(?i)(^|/)EzyImageViewer\.CodecHost(?:\.|/|$)' -or
            $file -match '(?i)(PDFtoImage|Magick|PDFium)' -or
            $file -match '(?i)\.pdb$') {
            throw "Main MSIX contains a forbidden isolated-codec artifact: '$file'."
        }
    }
    foreach ($file in $codecFiles) {
        if ($file -match '(?i)\.pdb$' -or
            $file -match '(?i)(^|/)ezyImageViewer\.(exe|dll|deps\.json|runtimeconfig\.json)$') {
            throw "CodecHost MSIX contains a forbidden artifact: '$file'."
        }
    }

    $requiredMainFiles = @(
        'AppxManifest.xml',
        'PACKAGE-CONTENTS.sha256',
        'ezyImageViewer.exe',
        'ezyImageViewer.dll',
        'ezyImageViewer.deps.json',
        'ezyImageViewer.runtimeconfig.json',
        'EzyImageViewer.CodecProtocol.dll',
        'App.xbf',
        'Resources/Icons.xbf',
        'Views/ViewerWindow.xbf',
        'ezyImageViewer.pri',
        'Assets/Fonts/MaterialSymbolsOutlined.ttf',
        'Assets/Fonts/LICENSE-MaterialSymbols.txt',
        'LICENSE.txt',
        'THIRD-PARTY-NOTICES.md')
    foreach ($required in $requiredMainFiles) {
        Assert-ContainsFile -Files $mainFiles -Expected $required -PackageLabel 'Main MSIX'
    }
    $requiredCodecFiles = @(
        'AppxManifest.xml',
        'PACKAGE-CONTENTS.sha256',
        'EzyImageViewer.CodecHost.exe',
        'EzyImageViewer.CodecHost.dll',
        'EzyImageViewer.CodecHost.deps.json',
        'EzyImageViewer.CodecHost.runtimeconfig.json',
        'EzyImageViewer.CodecProtocol.dll',
        'PDFtoImage.dll',
        'pdfium.dll',
        'Magick.NET.Core.dll',
        'Magick.NET-Q8-AnyCPU.dll',
        'Magick.Native-Q8-x64.dll',
        'SkiaSharp.dll',
        'libSkiaSharp.dll',
        'LICENSE.txt',
        'THIRD-PARTY-NOTICES.md')
    foreach ($required in $requiredCodecFiles) {
        Assert-ContainsFile -Files $codecFiles -Expected $required -PackageLabel 'CodecHost MSIX'
    }

    $fontPath = Join-Path $mainRoot 'Assets\Fonts\MaterialSymbolsOutlined.ttf'
    $expectedFontHash = '6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F'
    $actualFontHash = (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-Equal $actualFontHash $expectedFontHash 'Material Symbols font SHA-256'
    $packagedFontLicense = Join-Path $mainRoot 'Assets\Fonts\LICENSE-MaterialSymbols.txt'
    $sourceFontLicense = Join-Path $repo 'EzyImageViewer.App\Assets\Fonts\LICENSE-MaterialSymbols.txt'
    $packagedFontLicenseHash = (Get-FileHash -LiteralPath $packagedFontLicense -Algorithm SHA256).Hash.ToUpperInvariant()
    $sourceFontLicenseHash = (Get-FileHash -LiteralPath $sourceFontLicense -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-Equal $packagedFontLicenseHash $sourceFontLicenseHash 'Material Symbols license SHA-256'

    # Version-pinned PDFium/Chromium notice set (bblanchon pdfium-binaries
    # chromium/7690, PDFium 147.0.7690.0). Bytes must match the notices upstream
    # ships with the redistributed pdfium.dll, so each file is hash-locked.
    $pdfiumNoticeHashes = [ordered]@{
        'Notices/PDFium/LICENSE.txt'             = '8854F4388F1CA13B3AD9BAA42E95F5546B4C0B17109C159256D3ECA7BE39B09B'
        'Notices/PDFium/licenses/abseil.txt'     = 'F54FFF0B905DF5B3464527C652A30E903B172D6DCAB4D89B5E6F105D5E4A4603'
        'Notices/PDFium/licenses/agg23.txt'      = 'C110D3EA2AD77467CE0DCFF7D3337E6C8BE8049A5103F4B9BD5FD911A77972E5'
        'Notices/PDFium/licenses/fast_float.txt' = 'BF1B57355FECA8FCE77EE95F48002F8D4789FB71B30EC7599C06CDA4901FBB2B'
        'Notices/PDFium/licenses/freetype.txt'   = 'F4B133E25DF1F86AD3FFEA453AA0E613F0474F34778DBBB3E437E7B2724937D8'
        'Notices/PDFium/licenses/icu.txt'        = 'DE8A5576714C2308536EB4F1CC7908ACF189BC46095EBC6DF134341A6FF21E24'
        'Notices/PDFium/licenses/lcms.txt'       = '5132B7530AE5ACAB7058634DA86470D3D38BB01D70C2B67BA10C96BC62CED1AF'
        'Notices/PDFium/licenses/libjpeg_turbo.ijg' = 'DB16A04128171879C60708D171B88D97345A2DD20F9BFC173680A4497C73F704'
        'Notices/PDFium/licenses/libjpeg_turbo.md'  = 'BE2B2B5AB168BCE87BC3E31F2A5C5ADBA4B7F6E9E51D618E958D1D46972EBD95'
        'Notices/PDFium/licenses/libopenjpeg.txt' = 'C5AB0890A737C2DFA7BA675036554F6D17741D98629B0C2A145354D00617E6B2'
        'Notices/PDFium/licenses/libpng.txt'     = '0AD3BFEE8BE10E5519949E7AF492E36BC349376B75FBEB412229A5967E3E9434'
        'Notices/PDFium/licenses/libtiff.txt'    = '92B72BA97E6C2749C2A94BC0EF646B47080217F1E772A482B33CF5A5F98A6506'
        'Notices/PDFium/licenses/llvm-libc.txt'  = '3B6226C32E168C83B891D8D6F0D3C29C2116DC3EF93DC93C307B54F279ECF383'
        'Notices/PDFium/licenses/pdfium.txt'     = '961EACD9633FFF6D051DB7208B755E9210E30EFAC7ADEC3E6A6D52798F0CCF0E'
        'Notices/PDFium/licenses/simdutf.txt'    = 'C172A0BA936FF31230FEBB5DAD869E25CB7C1A07480C7A381BE8CF011BB52719'
        'Notices/PDFium/licenses/zlib.txt'       = '02421FCFBFB1D656EF0B6FF4CD3C39F2946F08C3219FA42DB482ADD7CE1F53EA'
    }
    foreach ($noticeRel in $pdfiumNoticeHashes.Keys) {
        Assert-ContainsFile -Files $codecFiles -Expected $noticeRel -PackageLabel 'CodecHost MSIX'
        $noticePath = Join-Path $codecRoot ($noticeRel -replace '/', '\')
        $noticeHash = (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash.ToUpperInvariant()
        Assert-Equal $noticeHash $pdfiumNoticeHashes[$noticeRel] "PDFium notice '$noticeRel' SHA-256"
    }

    $hostAssembly = Join-Path $codecRoot 'EzyImageViewer.CodecHost.dll'
    if (-not (Test-Path -LiteralPath $hostAssembly)) {
        throw 'CodecHost MSIX is missing EzyImageViewer.CodecHost.dll.'
    }
    $hostText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($hostAssembly))
    if ($hostText.IndexOf('DiagnosticOperationProcessor', [StringComparison]::Ordinal) -ge 0) {
        throw 'CodecHost MSIX contains the diagnostic operation surface.'
    }

    Write-Output "verified: $mainPath"
    Write-Output "verified dependency: $codecPath"
    Write-Output "identity: GRTech.ezyImageViewer $Version x64"
    Write-Output "codec identity: GRTech.ezyImageViewer.CodecHost $CodecHostVersion x64"
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
