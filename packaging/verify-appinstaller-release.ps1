#Requires -Version 5.1

# Verifies an App Installer file against the byte-backed identities in an actual MSIX pair.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppInstallerFile,

    [Parameter(Mandatory = $true)]
    [string]$MainPackage,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostPackage,

    [Parameter(Mandatory = $true)]
    [string]$AppInstallerUri,

    [Parameter(Mandatory = $true)]
    [string]$MainPackageUri,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostPackageUri,

    [ValidateSet('None', 'OnLaunch')]
    [string]$ExpectedUpdateMode = 'None',

    [ValidateRange(0, 255)]
    [int]$HoursBetweenUpdateChecks = 24
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'appinstaller-helpers.ps1')

if ($ExpectedUpdateMode -eq 'None' -and
    $PSBoundParameters.ContainsKey('HoursBetweenUpdateChecks')) {
    throw '-HoursBetweenUpdateChecks requires -ExpectedUpdateMode OnLaunch.'
}

$appInstallerItem = Get-EzyAppInstallerPhysicalFile `
    -Path $AppInstallerFile -Label 'AppInstallerFile'
if (-not [string]::Equals(
        $appInstallerItem.Extension,
        '.appinstaller',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "AppInstallerFile must have the .appinstaller extension: '$AppInstallerFile'."
}
Assert-EzyAsciiText -Value $appInstallerItem.Name -Label 'AppInstallerFile basename'

$pair = Get-EzyReleasePairContract `
    -MainPackage $MainPackage -CodecHostPackage $CodecHostPackage
$appInstallerUriValue = Resolve-EzyAppInstallerHttpsUri `
    -Value $AppInstallerUri -ExpectedFileName $appInstallerItem.Name -Label 'AppInstallerUri'
$mainPackageUriValue = Resolve-EzyAppInstallerHttpsUri `
    -Value $MainPackageUri -ExpectedFileName $pair.Main.File.Name -Label 'MainPackageUri'
$codecHostPackageUriValue = Resolve-EzyAppInstallerHttpsUri `
    -Value $CodecHostPackageUri -ExpectedFileName $pair.CodecHost.File.Name `
    -Label 'CodecHostPackageUri'
$uriValues = @($appInstallerUriValue, $mainPackageUriValue, $codecHostPackageUriValue)
if (@($uriValues | Select-Object -Unique).Count -ne $uriValues.Count) {
    throw 'AppInstallerUri, MainPackageUri, and CodecHostPackageUri must be unique.'
}

$stream = [IO.FileStream]::new(
    $appInstallerItem.FullName,
    [IO.FileMode]::Open,
    [IO.FileAccess]::Read,
    [IO.FileShare]::Read)
try {
    if ($stream.Length -le 0 -or $stream.Length -gt $script:EzyMaximumXmlBytes) {
        throw "AppInstallerFile size must be between 1 and $script:EzyMaximumXmlBytes bytes."
    }
    $bytes = [byte[]]::new([int]$stream.Length)
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
        if ($read -eq 0) {
            throw 'AppInstallerFile ended before its declared length.'
        }
        $offset += $read
    }
    if ($stream.ReadByte() -ne -1) {
        throw 'AppInstallerFile changed length while it was being read.'
    }
}
finally {
    $stream.Dispose()
}
$document = Read-EzySecureXmlBytes `
    -Bytes $bytes -Label 'AppInstallerFile' -RequireAsciiWithoutBom
Assert-EzyAppInstallerDocument `
    -Document $document `
    -Pair $pair `
    -AppInstallerUri $appInstallerUriValue `
    -MainPackageUri $mainPackageUriValue `
    -CodecHostPackageUri $codecHostPackageUriValue `
    -ExpectedUpdateMode $ExpectedUpdateMode `
    -HoursBetweenUpdateChecks $HoursBetweenUpdateChecks

Write-Output "verified appinstaller: $($appInstallerItem.FullName)"
Write-Output "identity: $($pair.Main.Name) $($pair.Main.Version) $($pair.Main.Architecture)"
Write-Output "dependency: $($pair.CodecHost.Name) $($pair.CodecHost.Version) $($pair.CodecHost.Architecture)"
Write-Output "update mode: $ExpectedUpdateMode"
