# Internal helper that builds the CodecHost framework MSIX inside a caller-owned staging directory.
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Publisher = "CN=ezyImageViewer Dev",
    [string]$CertificateThumbprint,
    [switch]$CreateDevCertificate,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
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
$hostProj = Join-Path $repo 'EzyImageViewer.CodecHost\EzyImageViewer.CodecHost.csproj'
$buildOut = Join-Path $repo 'EzyImageViewer.CodecHost\bin\Release\net10.0\win-x64'
$intermediateRoot = Join-Path $repo 'EzyImageViewer.CodecHost\obj\Release\net10.0\win-x64'
$packagingOut = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'out'))
$outDir = [IO.Path]::GetFullPath($OutputDirectory)
$packagingOutItem = Get-Item -LiteralPath $packagingOut -Force -ErrorAction Stop
if (-not $packagingOutItem.PSIsContainer -or
    ($packagingOutItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Packaging output must be a physical directory: '$packagingOut'."
}
$outParent = Split-Path $outDir -Parent
$outLeaf = Split-Path $outDir -Leaf
if (-not [string]::Equals($outParent, $packagingOut, [StringComparison]::OrdinalIgnoreCase) -or
    $outLeaf -cnotmatch '^\.staging-[0-9a-f]{32}$') {
    throw "OutputDirectory must be a main-package staging directory under '$packagingOut': '$outDir'."
}
$outDirectoryItem = Get-Item -LiteralPath $outDir -Force -ErrorAction Stop
if (-not $outDirectoryItem.PSIsContainer -or
    ($outDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputDirectory must be a physical directory: '$outDir'."
}
$workRoot = Join-Path $outDir ('.codec-host-staging-' + [Guid]::NewGuid().ToString('N'))
$layout = Join-Path $workRoot 'layout'
$stagedMsix = Join-Path $workRoot 'ezyImageViewer.CodecHost.msix'
$msix = Join-Path $outDir 'ezyImageViewer.CodecHost.msix'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

try {
$btRoot = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repo -ProjectAssetsPaths @(
    (Join-Path $repo 'EzyImageViewer.CodecHost\obj\project.assets.json'))
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
    & dotnet build $hostProj -c Release -p:EnableCodecHostDiagnostics=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $buildOut 'EzyImageViewer.CodecHost.exe'))) {
    throw "CodecHost build output is missing: $buildOut"
}
$fileList = Get-EzyFileListPath -IntermediateRoot $intermediateRoot
Assert-EzyBuildOutputInventory -BuildOutput $buildOut -FileListPath $fileList
$hostAssembly = Join-Path $buildOut 'EzyImageViewer.CodecHost.dll'
if (-not (Test-Path $hostAssembly)) {
    throw "CodecHost assembly is missing: $hostAssembly"
}
$hostAssemblyText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($hostAssembly))
if ($hostAssemblyText.IndexOf(
        'DiagnosticOperationProcessor',
        [StringComparison]::Ordinal) -ge 0) {
    throw 'Release CodecHost still contains DiagnosticOperationProcessor.'
}

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
& robocopy $buildOut $layout /E /NFL /NDL /NJH /NJS /XD ref NativeAotProbe /XF *.pdb | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Assets\StoreLogo.png') (Join-Path $layout 'Assets\StoreLogo.png') -Force

[xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'CodecHost.AppxManifest.template.xml')
$identity = $manifest.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identity) { throw 'CodecHost manifest Identity is missing.' }
$identity.SetAttribute('Version', $Version)
$identity.SetAttribute('Publisher', $Publisher)
Save-Manifest $manifest (Join-Path $layout 'AppxManifest.xml')
[void](Write-EzyPackageContentsManifest -Layout $layout)

& $makeappx pack /o /d $layout /p $stagedMsix
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
    if ($cert.Subject -ne $Publisher) {
        throw "certificate subject '$($cert.Subject)' != manifest publisher '$Publisher'"
    }
    $codeSigningEku = @($cert.EnhancedKeyUsageList | Where-Object {
        $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'
    })
    if (-not $cert.HasPrivateKey -or $codeSigningEku.Count -eq 0) {
        throw 'The selected certificate lacks a private key or the code-signing EKU.'
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $stagedMsix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
    Write-Output "signed: subject=$($cert.Subject) thumbprint=$($cert.Thumbprint)"
}

$publishLock = Open-ExclusivePublishLock -Path (Join-Path $outDir '.release-publish.lock')
try {
    if (Test-Path -LiteralPath $msix) {
        [IO.File]::Replace(
            $stagedMsix,
            $msix,
            (Join-Path $workRoot 'previous.msix'),
            $true)
    }
    else {
        [IO.File]::Move($stagedMsix, $msix)
    }
}
finally {
    $publishLock.Dispose()
}
Write-Output "packed: $msix"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
