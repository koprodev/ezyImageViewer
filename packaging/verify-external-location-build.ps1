[CmdletBinding()]
param(
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
. (Join-Path $scriptRoot 'external-location-helpers.ps1')

Assert-EzyExternalFourPartVersion $Version 'Version'
Assert-EzyExternalPublisher $Publisher
Assert-EzyExternalMinVersion $MinVersion

$stageScript = Join-Path $scriptRoot 'stage-msi-foundation.ps1'
$verifyScript = Join-Path $scriptRoot 'verify-msi-foundation.ps1'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempPrefix = $tempBase.TrimEnd('\') + '\'
$verificationRoot = Join-Path $tempBase (
    'ezy-external-build-verify-' + [Guid]::NewGuid().ToString('N'))

function Remove-EzyOwnedExternalBuildVerificationRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not [IO.Directory]::Exists($Path)) {
        return
    }

    $resolved = [IO.Path]::GetFullPath($Path)
    $name = [IO.Path]::GetFileName($resolved)
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $name -cnotmatch '^ezy-external-build-verify-[0-9a-f]{32}$' -or
        $resolved -ceq $tempBase) {
        throw "Refusing to remove unsafe verification path: '$resolved'."
    }

    $root = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Verification root became a reparse point: '$resolved'."
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $resolved -Recurse -Force `
            -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Verification output contains a reparse point: '$($item.FullName)'."
        }
    }
    [IO.Directory]::Delete($resolved, $true)
}

$operationError = $null
try {
    [void][IO.Directory]::CreateDirectory($verificationRoot)
    $root = Get-Item -LiteralPath $verificationRoot -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Verification root must be a physical directory: '$verificationRoot'."
    }

    $common = @{
        Version = $Version
        Publisher = $Publisher
        MinVersion = $MinVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($BuildToolsRoot)) {
        $common.BuildToolsRoot = $BuildToolsRoot
    }

    $staging = Join-Path $root.FullName 'foundation'
    & $stageScript -OutputDirectory $staging @common
    & $verifyScript -StagingDirectory $staging @common
}
catch {
    $operationError = $_
}

$cleanupError = $null
try {
    Remove-EzyOwnedExternalBuildVerificationRoot $verificationRoot
}
catch {
    $cleanupError = $_
}

if ($null -ne $operationError -and $null -ne $cleanupError) {
    throw ("External-location verification failed: " +
        "$($operationError.Exception.Message) Cleanup also failed: " +
        $cleanupError.Exception.Message)
}
if ($null -ne $operationError) {
    throw $operationError
}
if ($null -ne $cleanupError) {
    throw $cleanupError
}

Write-Output 'External-location source-to-artifact verification passed.'
