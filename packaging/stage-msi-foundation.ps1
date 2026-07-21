[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$CodecHostVersion,

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
Assert-EzyExternalFourPartVersion $CodecHostVersion 'CodecHostVersion'
Assert-EzyExternalPublisher $Publisher
Assert-EzyExternalMinVersion $MinVersion

$target = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw "OutputDirectory already exists: '$target'."
}
$parentPath = [IO.Path]::GetDirectoryName($target)
$parent = Get-Item -LiteralPath $parentPath -Force -ErrorAction Stop
if (-not $parent.PSIsContainer -or
    ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputDirectory parent must be a physical directory: '$parentPath'."
}
$targetName = [IO.Path]::GetFileName($target)
if ([string]::IsNullOrWhiteSpace($targetName)) {
    throw "OutputDirectory must identify a child directory: '$target'."
}
$staging = Join-Path $parent.FullName ('.' + $targetName + '.' +
    [Guid]::NewGuid().ToString('N') + '.staging')

try {
    [void][IO.Directory]::CreateDirectory($staging)
    $contractsRoot = Join-Path $staging 'contracts'
    $manifestResult = New-EzyExternalLocationManifests `
        -PackageTemplatePath (Join-Path $scriptRoot 'ExternalLocation.AppxManifest.template.xml') `
        -ApplicationTemplatePath (Join-Path $scriptRoot 'ExternalLocation.App.manifest.template.xml') `
        -OutputDirectory $contractsRoot `
        -Version $Version `
        -CodecHostVersion $CodecHostVersion `
        -Publisher $Publisher `
        -MinVersion $MinVersion

    $payload = Join-Path $staging 'payload'
    $applicationProject = Join-Path $repositoryRoot 'EzyImageViewer.App\EzyImageViewer.App.csproj'
    $externalManifestProperty =
        "-p:ExternalApplicationManifest=$($manifestResult.ApplicationManifestPath)"
    $restoreArguments = @(
        'restore',
        $applicationProject,
        '--locked-mode',
        '--runtime', 'win-x64',
        '-p:Platform=x64',
        '-p:Packaged=false',
        '-p:ExternalIdentity=true',
        $externalManifestProperty
    )
    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for the external identity flavor ($LASTEXITCODE)."
    }

    $publishArguments = @(
        'publish',
        $applicationProject,
        '-c', 'Release',
        '--no-restore',
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:Packaged=false',
        '-p:ExternalIdentity=true',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $scriptRoot 'MsiPublish.targets')",
        $externalManifestProperty,
        '-o', $payload
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed ($LASTEXITCODE)."
    }

    $intermediateRoot = Join-Path $repositoryRoot 'EzyImageViewer.App\obj\external\x64\Release'
    $publishOutputList = Get-EzyMsiPublishOutputListPath $intermediateRoot $payload
    Assert-EzyMsiPayload $payload $publishOutputList
    $inventoryPath = Write-EzyMsiPayloadInventory $payload $publishOutputList

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

    $embeddedManifest = Join-Path $contractsRoot 'embedded-ezyImageViewer.exe.manifest'
    & $manifestTool "-inputresource:$(Join-Path $payload 'ezyImageViewer.exe');#1" `
        "-out:$embeddedManifest"
    if ($LASTEXITCODE -ne 0) {
        throw "mt.exe could not extract the application manifest ($LASTEXITCODE)."
    }
    $embeddedDocument = Read-EzyExternalXml $embeddedManifest
    Assert-EzyExternalApplicationManifest $embeddedDocument $Publisher -Embedded

    $packageLayout = Split-Path -Parent $manifestResult.PackageManifestPath
    $assetOutput = Join-Path $packageLayout 'Assets'
    [void][IO.Directory]::CreateDirectory($assetOutput)
    foreach ($assetName in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
        [IO.File]::Copy(
            (Join-Path $scriptRoot "Assets\$assetName"),
            (Join-Path $assetOutput $assetName),
            $false)
    }
    $identityDirectory = Join-Path $staging 'identity'
    [void][IO.Directory]::CreateDirectory($identityDirectory)
    $identityPackage = Join-Path $identityDirectory 'ezyImageViewer.ExternalIdentity.msix'
    & $makeAppx pack /o /nv /d $packageLayout /p $identityPackage
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx failed for external identity package ($LASTEXITCODE)."
    }

    $payloadFiles = @(Get-EzyMsiPayloadFiles $payload)
    $payloadBytes = [long]0
    foreach ($entry in $payloadFiles) {
        $payloadBytes += $entry.File.Length
    }
    $metadata = [PSCustomObject][ordered]@{
        schemaVersion = 1
        version = $Version
        codecHostVersion = $CodecHostVersion
        publisher = $Publisher
        minVersion = $MinVersion
        architecture = 'x64'
        packageIdentityArchitecture = 'neutral'
        signed = $false
        payload = [PSCustomObject][ordered]@{
            directory = 'payload'
            fileCount = $payloadFiles.Count
            byteCount = $payloadBytes
            inventory = 'payload/PACKAGE-CONTENTS.sha256'
            inventorySha256 = (Get-FileHash $inventoryPath -Algorithm SHA256).Hash.ToUpperInvariant()
        }
        externalIdentity = [PSCustomObject][ordered]@{
            path = 'identity/ezyImageViewer.ExternalIdentity.msix'
            sha256 = (Get-FileHash $identityPackage -Algorithm SHA256).Hash.ToUpperInvariant()
            applicationManifestSha256 = (Get-FileHash $manifestResult.ApplicationManifestPath `
                -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    }
    $metadataJson = $metadata | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        (Join-Path $staging 'STAGING-METADATA.json'),
        $metadataJson.Replace("`r`n", "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    [IO.Directory]::Move($staging, $target)
    Write-Output "MSI foundation staged: $target"
    Write-Output "payload files: $($payloadFiles.Count)"
    Write-Output "payload bytes: $payloadBytes"
    Write-Output 'external identity: unsigned'
}
finally {
    if ([IO.Directory]::Exists($staging)) {
        $stagingFull = [IO.Path]::GetFullPath($staging)
        $parentPrefix = $parent.FullName.TrimEnd('\') + '\'
        if (-not $stagingFull.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($stagingFull).EndsWith('.staging',
                [StringComparison]::Ordinal)) {
            throw "Refusing to remove unsafe staging path: '$stagingFull'."
        }
        [IO.Directory]::Delete($stagingFull, $true)
    }
}
