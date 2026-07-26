# 설치나 인증서 저장소 변경 없이 주 MSIX 검증.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainPackage,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    # 기본값은 개발 서명 ID. Store 패키지를 검증할 땐 호출자가 Partner Center 값을 넘겨야 하고,
    # 안 넘기면 매니페스트와 어긋나 검증이 실패하므로 조용히 통과할 길은 없다.
    [string]$IdentityName = 'GRTech.ezyImageViewer',
    [string]$PublisherDisplayName = 'grtech-devpro',

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

Assert-MsixVersion -Value $Version -Label 'Version'
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
if ([IO.Path]::GetExtension($mainPath) -ine '.msix') {
    throw 'MainPackage must use the .msix extension.'
}
$projectAssetsPaths = @(
    (Join-Path $repo 'EzyImageViewer.App\obj\packaged\project.assets.json'))
$toolsRoot = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repo `
    -ProjectAssetsPaths $projectAssetsPaths -ExplicitRoot $BuildToolsRoot
$makeAppx = Get-MakeAppxPath -Root $toolsRoot
if ($RequireSignature) {
    $signTool = Get-SignToolPath -Root $toolsRoot
    foreach ($package in @($mainPath)) {
        & $signTool verify /pa /all $package | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed for '$package' ($LASTEXITCODE)."
        }
    }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'ezyImageViewer-msix-verify-' + [Guid]::NewGuid().ToString('N'))
$mainRoot = Join-Path $scratch 'main'

try {
    Expand-Msix -MakeAppx $makeAppx -Package $mainPath -Destination $mainRoot
    Assert-EzyPackageContentsManifest -UnpackedRoot $mainRoot -PackageLabel 'Main MSIX'
    if ($RequireBuildOutputMatch) {
        $mainBuildOutput = Join-Path $repo 'EzyImageViewer.App\bin\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
        $mainIntermediate = Join-Path $repo 'EzyImageViewer.App\obj\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
        $mainFileList = Get-EzyFileListPath -IntermediateRoot $mainIntermediate
        # 타일 로고는 이제 Content라 빌드 출력 목록에 들어 있다. 밖에서 끌어올 파일이 없음.
        Assert-EzyPackageMatchesBuildOutput -UnpackedRoot $mainRoot `
            -BuildOutput $mainBuildOutput -FileListPath $mainFileList `
            -AdditionalSourceFiles @{} -PackageLabel 'Main MSIX'
    }

    [xml]$mainManifest = Get-Content -LiteralPath (Join-Path $mainRoot 'AppxManifest.xml')
    $mainIdentity = Get-ManifestNode -Manifest $mainManifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Identity']" -Label 'main Identity'
    Assert-Equal $mainIdentity.GetAttribute('Name') $IdentityName 'main identity name'
    Assert-Equal $mainIdentity.GetAttribute('Version') $Version 'main identity version'
    Assert-Equal $mainIdentity.GetAttribute('Publisher') $Publisher 'main publisher'
    Assert-Equal $mainIdentity.GetAttribute('ProcessorArchitecture') 'x64' 'main architecture'
    # Partner Center 표시 이름과 한 글자라도 다르면 제출이 거절된다.
    $mainPublisherDisplay = Get-ManifestNode -Manifest $mainManifest `
        -XPath ("/*[local-name()='Package']/*[local-name()='Properties']" +
            "/*[local-name()='PublisherDisplayName']") -Label 'main PublisherDisplayName'
    Assert-Equal $mainPublisherDisplay.InnerText $PublisherDisplayName 'main publisher display name'

    $dependencies = @($mainManifest.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($dependencies.Count -ne 0) {
        throw "Main manifest must not declare a package dependency; found $($dependencies.Count)."
    }

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
    if ($extensions.Count -ne 2) {
        throw "Main manifest must contain exactly two extensions; found $($extensions.Count)."
    }
    foreach ($extension in $extensions) {
        Assert-Equal $extension.NamespaceURI `
            'http://schemas.microsoft.com/appx/manifest/uap/windows10' 'main extension namespace'
    }

    $protocolExtensions = @($extensions | Where-Object { $_.GetAttribute('Category') -ceq 'windows.protocol' })
    if ($protocolExtensions.Count -ne 1) {
        throw "Main manifest must contain exactly one protocol extension; found $($protocolExtensions.Count)."
    }
    $protocols = @($protocolExtensions[0].SelectNodes("*[local-name()='Protocol']"))
    if ($protocols.Count -ne 1) {
        throw "Main protocol extension must contain exactly one Protocol; found $($protocols.Count)."
    }
    Assert-Equal $protocols[0].GetAttribute('Name') 'ezyimageviewer' 'main protocol name'

    # 매니페스트 연결 목록은 FileAssociationPolicy와 계약 테스트로 함께 고정.
    $associationExtensions = @($extensions | Where-Object {
            $_.GetAttribute('Category') -ceq 'windows.fileTypeAssociation'
        })
    if ($associationExtensions.Count -ne 1) {
        throw ('Main manifest must contain exactly one file type association extension; ' +
            "found $($associationExtensions.Count).")
    }
    $associations = @($associationExtensions[0].SelectNodes("*[local-name()='FileTypeAssociation']"))
    if ($associations.Count -ne 1) {
        throw "Main association extension must contain exactly one FileTypeAssociation; found $($associations.Count)."
    }
    Assert-Equal $associations[0].GetAttribute('Name') 'ezyimageviewer.image' 'main association name'
    $fileTypes = @($associations[0].SelectNodes(
            "*[local-name()='SupportedFileTypes']/*[local-name()='FileType']") |
        ForEach-Object { $_.InnerText })
    $expectedFileTypes = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff')
    if (Compare-Object $fileTypes $expectedFileTypes -SyncWindow 0) {
        throw ("Main association file types must equal '$($expectedFileTypes -join ', ')'; " +
            "found '$($fileTypes -join ', ')'.")
    }

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

    $mainFiles = Get-RelativeFiles -Root $mainRoot
    foreach ($file in $mainFiles) {
# Magick.NET은 테스트 픽스처 생성 전용. 배포 패키지에 발 들이면 안 됨.
        if ($file -match '(?i)(PDFtoImage|Magick|PDFium)' -or
            $file -match '(?i)\.pdb$') {
            throw "Main MSIX contains a forbidden artifact: '$file'."
        }
    }

    $requiredMainFiles = @(
        'AppxManifest.xml',
        'PACKAGE-CONTENTS.sha256',
        'ezyImageViewer.exe',
        'ezyImageViewer.dll',
        'ezyImageViewer.deps.json',
        'ezyImageViewer.runtimeconfig.json',
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
    $fontPath = Join-Path $mainRoot 'Assets\Fonts\MaterialSymbolsOutlined.ttf'
    $expectedFontHash = '6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F'
    $actualFontHash = (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-Equal $actualFontHash $expectedFontHash 'Material Symbols font SHA-256'
    $packagedFontLicense = Join-Path $mainRoot 'Assets\Fonts\LICENSE-MaterialSymbols.txt'
    $sourceFontLicense = Join-Path $repo 'EzyImageViewer.App\Assets\Fonts\LICENSE-MaterialSymbols.txt'
    $packagedFontLicenseHash = (Get-FileHash -LiteralPath $packagedFontLicense -Algorithm SHA256).Hash.ToUpperInvariant()
    $sourceFontLicenseHash = (Get-FileHash -LiteralPath $sourceFontLicense -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-Equal $packagedFontLicenseHash $sourceFontLicenseHash 'Material Symbols license SHA-256'

    Write-Output "verified: $mainPath"
    Write-Output "identity: $IdentityName $Version x64"
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
