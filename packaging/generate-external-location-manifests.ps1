[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$Publisher,

    [Parameter(Mandatory)]
    [ValidateSet('10.0.19041.0', '10.0.26100.0')]
    [string]$MinVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'external-location-helpers.ps1')

$result = New-EzyExternalLocationManifests `
    -PackageTemplatePath (Join-Path $scriptRoot 'ExternalLocation.AppxManifest.template.xml') `
    -ApplicationTemplatePath (Join-Path $scriptRoot 'ExternalLocation.App.manifest.template.xml') `
    -OutputDirectory $OutputDirectory `
    -Version $Version `
    -Publisher $Publisher `
    -MinVersion $MinVersion

Write-Output "external package manifest: $($result.PackageManifestPath)"
Write-Output "application identity manifest: $($result.ApplicationManifestPath)"
