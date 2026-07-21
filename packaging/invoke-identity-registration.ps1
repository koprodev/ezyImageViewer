#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Register', 'Unregister', 'Rollback')]
    [string]$Action,

    [Parameter(Mandatory)]
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$Scope,

    [string]$InstallDirectory,

    [string]$CodecHostPackagePath,

    [string]$ExternalPackagePath,

    [string]$StatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'identity-registration-backend.ps1')

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = [IO.Directory]::GetParent($PSScriptRoot).FullName
}
if ([string]::IsNullOrWhiteSpace($CodecHostPackagePath)) {
    $CodecHostPackagePath = Join-Path $PSScriptRoot 'ezyImageViewer.CodecHost.msix'
}
if ([string]::IsNullOrWhiteSpace($ExternalPackagePath)) {
    $ExternalPackagePath = Join-Path $PSScriptRoot 'ezyImageViewer.ExternalIdentity.msix'
}
if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path $PSScriptRoot 'identity-state.json'
}

try {
    if (-not [Environment]::Is64BitOperatingSystem -or
        [Environment]::OSVersion.Version.Build -lt 19041) {
        exit (Get-EzyIdentityExitCodes).PrerequisiteFailure
    }
    if ($Action -ceq 'Register') {
        if ([string]::IsNullOrWhiteSpace($CodecHostPackagePath) -or
            [string]::IsNullOrWhiteSpace($ExternalPackagePath)) {
            exit (Get-EzyIdentityExitCodes).InvalidInput
        }
        [void](Invoke-EzyIdentityRegister $Scope $InstallDirectory `
                $CodecHostPackagePath $ExternalPackagePath $StatePath)
    }
    else {
        Invoke-EzyIdentityUnregister $Scope $InstallDirectory $StatePath `
            -AllowOwnershipStateFailure:($Action -ceq 'Unregister')
    }
    exit (Get-EzyIdentityExitCodes).Success
}
catch {
    Write-Error $_
    if ($Action -ceq 'Register') { exit (Get-EzyIdentityExitCodes).MainIdentityFailure }
    exit (Get-EzyIdentityExitCodes).RemovalFailure
}
