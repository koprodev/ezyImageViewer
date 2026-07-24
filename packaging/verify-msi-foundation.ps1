[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StagingDirectory,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$Publisher,

    [Parameter(Mandatory)]
    [ValidateSet('10.0.19041.0', '10.0.26100.0')]
    [string]$MinVersion,

    [string]$BuildToolsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'external-location-helpers.ps1')
. (Join-Path $scriptRoot 'msi-payload-helpers.ps1')

Assert-EzyExternalFourPartVersion $Version 'Version'
Assert-EzyExternalPublisher $Publisher
Assert-EzyExternalMinVersion $MinVersion

$root = Get-Item -LiteralPath ([IO.Path]::GetFullPath($StagingDirectory)) -Force `
    -ErrorAction Stop
if (-not $root.PSIsContainer -or
    ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "StagingDirectory must be a physical directory: '$StagingDirectory'."
}
foreach ($item in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force `
        -ErrorAction Stop)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "StagingDirectory contains a reparse point: '$($item.FullName)'."
    }
}

$metadataPath = Join-Path $root.FullName 'STAGING-METADATA.json'
$metadataFile = Get-Item -LiteralPath $metadataPath -Force -ErrorAction Stop
if ($metadataFile.PSIsContainer -or $metadataFile.Length -le 0 -or $metadataFile.Length -gt 1MB) {
    throw 'STAGING-METADATA.json must be a non-empty bounded file.'
}
$metadata = [IO.File]::ReadAllText($metadataFile.FullName) | ConvertFrom-Json
if ($metadata.schemaVersion -ne 1 -or
    $metadata.version -cne $Version -or
    $metadata.publisher -cne $Publisher -or
    $metadata.minVersion -cne $MinVersion -or
    $metadata.architecture -cne 'x64' -or
    $metadata.packageIdentityArchitecture -cne 'neutral' -or
    $metadata.signed -ne $false -or
    $metadata.payload.directory -cne 'payload' -or
    $metadata.payload.inventory -cne 'payload/PACKAGE-CONTENTS.sha256' -or
    $metadata.externalIdentity.path -cne 'identity/ezyImageViewer.ExternalIdentity.msix') {
    throw 'MSI foundation metadata contract mismatch.'
}

$payload = Join-Path $root.FullName 'payload'
Assert-EzyMsiPayload -PayloadDirectory $payload -InventoryPresent
$payloadFiles = @(Get-EzyMsiPayloadFiles $payload)
$payloadBytes = [long]0
foreach ($entry in $payloadFiles) { $payloadBytes += $entry.File.Length }
if ($payloadFiles.Count -ne $metadata.payload.fileCount -or
    $payloadBytes -ne $metadata.payload.byteCount) {
    throw 'MSI payload count or byte total does not match metadata.'
}
$inventoryPath = Join-Path $payload 'PACKAGE-CONTENTS.sha256'
$inventoryHash = (Get-FileHash $inventoryPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($inventoryHash -cne $metadata.payload.inventorySha256) {
    throw 'MSI payload inventory hash does not match metadata.'
}

$sourcePackageManifest = Join-Path $root.FullName 'contracts\package\AppxManifest.xml'
$sourceApplicationManifest = Join-Path $root.FullName `
    'contracts\application\ezyImageViewer.exe.manifest'
$packageDocument = Read-EzyExternalXml $sourcePackageManifest
Assert-EzyExternalPackageManifest $packageDocument $Version $Publisher $MinVersion
$applicationDocument = Read-EzyExternalXml $sourceApplicationManifest
Assert-EzyExternalApplicationManifest $applicationDocument $Publisher
$applicationHash = (Get-FileHash $sourceApplicationManifest -Algorithm SHA256).Hash.ToUpperInvariant()
if ($applicationHash -cne $metadata.externalIdentity.applicationManifestSha256) {
    throw 'Application identity manifest hash does not match metadata.'
}

$identityPackage = Join-Path $root.FullName 'identity\ezyImageViewer.ExternalIdentity.msix'
$identityHash = (Get-FileHash $identityPackage -Algorithm SHA256).Hash.ToUpperInvariant()
if ($identityHash -cne $metadata.externalIdentity.sha256) {
    throw 'External identity package hash does not match metadata.'
}

$projectAssets = @(
    (Join-Path $repositoryRoot 'EzyImageViewer.App\obj\external\project.assets.json'),
    (Join-Path $repositoryRoot 'EzyImageViewer.Imaging\obj\external\project.assets.json')
)
$buildTools = Get-EzyPinnedBuildToolsRoot -RepositoryRoot $repositoryRoot `
    -ProjectAssetsPaths $projectAssets -ExplicitRoot $BuildToolsRoot
$toolBins = @(Get-ChildItem -LiteralPath (Join-Path $buildTools 'bin') -Directory `
    -Recurse -Force -ErrorAction Stop | Where-Object {
        [IO.File]::Exists((Join-Path $_.FullName 'x64\makeappx.exe')) -and
        [IO.File]::Exists((Join-Path $_.FullName 'x64\mt.exe'))
    })
if ($toolBins.Count -ne 1) {
    throw "Expected exactly one x64 BuildTools directory; found $($toolBins.Count)."
}
$makeAppx = Join-Path $toolBins[0].FullName 'x64\makeappx.exe'
$manifestTool = Join-Path $toolBins[0].FullName 'x64\mt.exe'

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$verificationRoot = Join-Path $tempBase ('ezy-msi-foundation-verify-' +
    [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($verificationRoot)
    $unpacked = Join-Path $verificationRoot 'identity'
    & $makeAppx unpack /nv /p $identityPackage /d $unpacked
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx could not unpack the external identity package ($LASTEXITCODE)."
    }
    $unpackedDocument = Read-EzyExternalXml (Join-Path $unpacked 'AppxManifest.xml')
    Assert-EzyExternalPackageManifest $unpackedDocument $Version $Publisher $MinVersion
    $unpackedPrefix = $unpacked.TrimEnd('\') + '\'
    $unpackedPayload = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File |
        Where-Object { $_.Name -notin @('AppxBlockMap.xml', '[Content_Types].xml') } |
        ForEach-Object {
            $_.FullName.Substring($unpackedPrefix.Length).Replace('\', '/')
        } | Sort-Object)
    $expectedPayload = @(
        'AppxManifest.xml',
        'Assets/Square150x150Logo.png',
        'Assets/Square44x44Logo.png',
        'Assets/StoreLogo.png'
    ) | Sort-Object
    if (@(Compare-Object -ReferenceObject $expectedPayload -DifferenceObject $unpackedPayload `
            -CaseSensitive).Count -ne 0) {
        throw "External identity package payload set mismatch: $($unpackedPayload -join ', ')."
    }
    foreach ($assetName in @(
            'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
        $sourceAsset = Join-Path $scriptRoot "Assets\$assetName"
        $unpackedAsset = Join-Path $unpacked "Assets\$assetName"
        $sourceHash = (Get-FileHash $sourceAsset -Algorithm SHA256).Hash
        $unpackedHash = (Get-FileHash $unpackedAsset -Algorithm SHA256).Hash
        if ($sourceHash -cne $unpackedHash) {
            throw "External identity asset hash mismatch: '$assetName'."
        }
    }

    $embeddedManifest = Join-Path $verificationRoot 'embedded.manifest'
    & $manifestTool "-inputresource:$(Join-Path $payload 'ezyImageViewer.exe');#1" `
        "-out:$embeddedManifest"
    if ($LASTEXITCODE -ne 0) {
        throw "mt.exe could not extract the staged application manifest ($LASTEXITCODE)."
    }
    $embeddedDocument = Read-EzyExternalXml $embeddedManifest
    Assert-EzyExternalApplicationManifest $embeddedDocument $Publisher -Embedded
}
finally {
    if ([IO.Directory]::Exists($verificationRoot)) {
        $verificationFull = [IO.Path]::GetFullPath($verificationRoot)
        if (-not $verificationFull.StartsWith($tempBase,
                [StringComparison]::OrdinalIgnoreCase) -or
            $verificationFull -ceq $tempBase) {
            throw "Refusing to remove unsafe verification path: '$verificationFull'."
        }
        [IO.Directory]::Delete($verificationFull, $true)
    }
}

Write-Output 'MSI foundation verification passed.'
Write-Output "payload files: $($payloadFiles.Count)"
Write-Output "payload bytes: $payloadBytes"
Write-Output "external identity sha256: $identityHash"
