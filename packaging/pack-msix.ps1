# dotnet CLI와 Windows SDK BuildTools만으로 앱 빌드·서명 MSIX 패키징(VS 불필요).
# 개발 identity·서명 전용. 배포 identity·채널·인증서는 별도 범위.
#
# 실행 예:
#   powershell -NoProfile -ExecutionPolicy Bypass -File packaging\pack-msix.ps1 `
#       -Version 1.0.9.0
#   ... -NoBuild       # 기존 PACKAGED x64 출력 재사용(bin\packaged\x64)
#   ... -SkipSign      # unsigned .msix 생성
#   ... -CreateDevCertificate # 없는 개발 인증서를 명시적으로 생성
# 패키지 설치:
#   Add-AppxPackage packaging\out\ezyImageViewer.msix
#
# 개발 서명 실행은 공개 인증서를 out\ezyImageViewer-dev.cer로 내보냄.
# 관리자에서 한 번 신뢰: certutil -addstore TrustedPeople packaging\out\ezyImageViewer-dev.cer
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Publisher = "CN=ezyImageViewer Dev",
    [string]$CertificateThumbprint,
    [switch]$CreateDevCertificate,
    [switch]$NoBuild,
    [switch]$SkipSign
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-helpers.ps1')

function Assert-MsixVersion {
    param([string]$Value, [string]$Label)
    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
        throw "$Label must be a canonical four-part numeric version: '$Value'."
    }
    foreach ($part in $Value.Split('.')) {
        if ([uint64]::Parse($part, [Globalization.CultureInfo]::InvariantCulture) -gt 65535) {
            throw "$Label contains a part outside the MSIX range 0..65535: '$Value'."
        }
    }
}

function Assert-Publisher {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Contains('{{')) {
        throw "Publisher is empty or unresolved: '$Value'."
    }
    try {
        [void][Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Value)
    }
    catch {
        throw "Publisher is not a valid X.500 distinguished name: '$Value'."
    }
}

function Save-Manifest {
    param([xml]$Document, [string]$Path)
    if ($Document.OuterXml.Contains('{{')) {
        throw 'Manifest contains an unresolved placeholder.'
    }
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Publish-ArtifactSet {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Artifacts,

        [Parameter(Mandatory = $true)]
        [string]$BackupDirectory
    )

    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $backups = New-Object 'Collections.Generic.List[object]'
    $published = New-Object 'Collections.Generic.List[string]'
    try {
        foreach ($artifact in $Artifacts) {
            if (Test-Path -LiteralPath $artifact.Final) {
                $backup = Join-Path $BackupDirectory ([IO.Path]::GetFileName($artifact.Final))
                Move-Item -LiteralPath $artifact.Final -Destination $backup -ErrorAction Stop
                [void]$backups.Add([pscustomobject]@{ Backup = $backup; Final = $artifact.Final })
            }
        }
        foreach ($artifact in $Artifacts) {
            if ([string]::IsNullOrWhiteSpace([string]$artifact.Staged)) {
                continue
            }
            Move-Item -LiteralPath $artifact.Staged -Destination $artifact.Final -ErrorAction Stop
            [void]$published.Add($artifact.Final)
        }
    }
    catch {
        $promotionFailure = $_
        $rollbackFailures = New-Object 'Collections.Generic.List[Exception]'
        foreach ($path in $published) {
            if (Test-Path -LiteralPath $path) {
                try {
                    Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                }
                catch {
                    [void]$rollbackFailures.Add($_.Exception)
                }
            }
        }
        foreach ($backup in $backups) {
            if (Test-Path -LiteralPath $backup.Backup) {
                try {
                    Move-Item -LiteralPath $backup.Backup -Destination $backup.Final -ErrorAction Stop
                }
                catch {
                    [void]$rollbackFailures.Add($_.Exception)
                }
            }
        }
        if ($rollbackFailures.Count -gt 0) {
            $allFailures = New-Object 'Collections.Generic.List[Exception]'
            [void]$allFailures.Add($promotionFailure.Exception)
            foreach ($rollbackFailure in $rollbackFailures) {
                [void]$allFailures.Add($rollbackFailure)
            }
            throw [AggregateException]::new(
                'Artifact promotion failed and rollback was incomplete.',
                $allFailures.ToArray())
        }
        throw $promotionFailure
    }
}

function Open-ExclusivePublishLock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        return [IO.File]::Open(
            $Path,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another release artifact publisher holds '$Path'."
    }
}

Assert-MsixVersion $Version 'Version'
Assert-Publisher $Publisher
if ($SkipSign -and ($CreateDevCertificate -or -not [string]::IsNullOrWhiteSpace($CertificateThumbprint))) {
    throw 'Certificate options cannot be combined with -SkipSign.'
}

$repo = Split-Path $PSScriptRoot -Parent
$appProj = Join-Path $repo 'EzyImageViewer.App\EzyImageViewer.App.csproj'
# 패키지 빌드는 전용 bin/obj 사용. -NoBuild가 개발 출력을 잘못 담지 못하게 함.
$buildOut = Join-Path $repo 'EzyImageViewer.App\bin\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
$intermediateRoot = Join-Path $repo 'EzyImageViewer.App\obj\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64'
$outDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'out'))
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$outDirectoryItem = Get-Item -LiteralPath $outDir -Force
if (-not $outDirectoryItem.PSIsContainer -or
    ($outDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Packaging output must be a physical directory: '$outDir'."
}
$staging = Join-Path $outDir ('.staging-' + [Guid]::NewGuid().ToString('N'))
$layout = Join-Path $staging 'layout'
$msix = Join-Path $staging 'ezyImageViewer.msix'
$stagedCertificate = Join-Path $staging 'ezyImageViewer-dev.cer'
$finalMsix = Join-Path $outDir 'ezyImageViewer.msix'
$finalCertificate = Join-Path $outDir 'ezyImageViewer-dev.cer'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$stagingItem = Get-Item -LiteralPath $staging -Force
if (-not $stagingItem.PSIsContainer -or
    ($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Packaging staging must be a physical directory: '$staging'."
}

try {
$btRoot = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repo -ProjectAssetsPaths @(
    (Join-Path $repo 'EzyImageViewer.App\obj\packaged\project.assets.json'))
$toolBins = @(Get-ChildItem (Join-Path $btRoot 'bin') -Directory |
    Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')
    })
if ($toolBins.Count -ne 1) {
    throw "Expected exactly one x64 BuildTools directory under '$btRoot'; found $($toolBins.Count)."
}
$makeappx = Join-Path $toolBins[0].FullName 'x64\makeappx.exe'
$signtool = Join-Path $toolBins[0].FullName 'x64\signtool.exe'
if (-not (Test-Path -LiteralPath $signtool)) {
    throw "signtool.exe is missing: '$signtool'."
}

if (-not $NoBuild) {
    # Packaged=true면 비패키지 부트스트랩을 빼고 모든 프로젝트를 packaged bin/obj로 보냄.
    & dotnet build $appProj -c Release -p:Packaged=true -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $buildOut 'ezyImageViewer.exe'))) { throw "packaged build output missing: $buildOut" }
$fileList = Get-EzyFileListPath -IntermediateRoot $intermediateRoot
Assert-EzyBuildOutputInventory -BuildOutput $buildOut -FileListPath $fileList

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
& robocopy $buildOut $layout /E /NFL /NDL /NJH /NJS /XD ref NativeAotProbe /XF *.pdb | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
foreach ($assetName in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
    Copy-Item (Join-Path $PSScriptRoot "Assets\$assetName") (Join-Path $layout 'Assets') -Force
}

[xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml')
$identity = $manifest.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identity) {
    throw 'Main manifest Identity is missing.'
}
$identity.SetAttribute('Version', $Version)
$identity.SetAttribute('Publisher', $Publisher)
Save-Manifest $manifest (Join-Path $layout 'AppxManifest.xml')
[void](Write-EzyPackageContentsManifest -Layout $layout)

if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /o /d $layout /p $msix
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }

if (-not $SkipSign) {
    $now = Get-Date
    $certificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $Publisher -and $_.HasPrivateKey -and
        $_.NotBefore -le $now -and $_.NotAfter -gt $now -and
        ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
            $_.Thumbprint -eq $CertificateThumbprint)
    })
    if ($certificates.Count -eq 0 -and $CreateDevCertificate) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName 'ezyImageViewer Dev' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    elseif ($certificates.Count -eq 1) {
        $cert = $certificates[0]
    }
    elseif ($certificates.Count -eq 0) {
        throw 'No valid code-signing certificate matched. Pass -CreateDevCertificate only for an explicit development-certificate creation.'
    }
    else {
        throw "Multiple valid certificates matched '$Publisher'; pass -CertificateThumbprint."
    }
    if ($cert.Subject -ne $Publisher) { throw "certificate subject '$($cert.Subject)' != manifest publisher '$Publisher'" }
    $codeSigningEku = @($cert.EnhancedKeyUsageList | Where-Object {
        $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'
    })
    if (-not $cert.HasPrivateKey -or $codeSigningEku.Count -eq 0) {
        throw 'The selected certificate lacks a private key or the code-signing EKU.'
    }
    # 깨끗한 PC에서 신뢰 등록할 수 있게 공개 키를 패키지 옆에 둠.
    Export-Certificate -Cert $cert -FilePath $stagedCertificate -Force | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
    Write-Output "signed: subject=$($cert.Subject) thumbprint=$($cert.Thumbprint)"
}

$verifyArgs = @{
    MainPackage = $msix
    Version = $Version
    Publisher = $Publisher
    BuildToolsRoot = $btRoot
    RequireBuildOutputMatch = $true
}
& (Join-Path $PSScriptRoot 'verify-msix-release.ps1') @verifyArgs

$artifacts = @(
    [pscustomobject]@{ Staged = $msix; Final = $finalMsix },
    [pscustomobject]@{
        Staged = if ($SkipSign) { $null } else { $stagedCertificate }
        Final = $finalCertificate
    }
)
$publishLock = Open-ExclusivePublishLock -Path (Join-Path $outDir '.release-publish.lock')
try {
    Publish-ArtifactSet -Artifacts $artifacts -BackupDirectory (Join-Path $staging 'backup')
}
finally {
    $publishLock.Dispose()
}

Write-Output "packed: $finalMsix"
if (-not $SkipSign) {
    Write-Output "exported: $finalCertificate"
}
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
