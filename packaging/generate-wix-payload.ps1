#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][ValidateSet('PerUser', 'PerMachine')][string]$Scope,
    [Parameter(Mandatory)][string]$CodecHostPackage,
    [Parameter(Mandatory)][string]$ExternalIdentityPackage,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'msi-payload-helpers.ps1')

function Get-WixHash([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-WixGuid([string]$Value) {
    $hash = Get-WixHash $Value
    $bytes = [byte[]]::new(16)
    for ($index = 0; $index -lt 16; $index++) {
        $bytes[$index] = [Convert]::ToByte($hash.Substring($index * 2, 2), 16)
    }
    $bytes[7] = ($bytes[7] -band 0x0F) -bor 0x40
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($bytes)).ToString('B').ToUpperInvariant()
}

function Escape-Wix([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

$payloadRoot = Get-Item -LiteralPath ([IO.Path]::GetFullPath($PayloadDirectory)) -Force `
    -ErrorAction Stop
if (-not $payloadRoot.PSIsContainer -or
    ($payloadRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PayloadDirectory must be a physical directory.'
}
Assert-EzyMsiPayload $payloadRoot.FullName -InventoryPresent

$codecPath = Get-Item -LiteralPath ([IO.Path]::GetFullPath($CodecHostPackage)) -Force `
    -ErrorAction Stop
$identityPath = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExternalIdentityPackage)) `
    -Force -ErrorAction Stop
foreach ($package in @($codecPath, $identityPath)) {
    if ($package.PSIsContainer -or
        ($package.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [IO.Path]::GetExtension($package.Name) -cne '.msix') {
        throw "Installer package input is invalid: '$($package.FullName)'."
    }
}

$additionalFiles = [ordered]@{
    'InstallerResources\identity-registration-contract.ps1' =
        (Join-Path $PSScriptRoot 'identity-registration-contract.ps1')
    'InstallerResources\identity-registration-backend.ps1' =
        (Join-Path $PSScriptRoot 'identity-registration-backend.ps1')
    'InstallerResources\invoke-identity-registration.ps1' =
        (Join-Path $PSScriptRoot 'invoke-identity-registration.ps1')
    'InstallerResources\ezyImageViewer.CodecHost.msix' = $codecPath.FullName
    'InstallerResources\ezyImageViewer.ExternalIdentity.msix' = $identityPath.FullName
}

$entries = [Collections.Generic.List[object]]::new()
$prefix = $payloadRoot.FullName.TrimEnd('\') + '\'
foreach ($file in @(Get-EzyMsiPayloadFiles $payloadRoot.FullName)) {
    $relative = $file.RelativePath.Replace('/', '\')
    if ($relative -ceq 'PACKAGE-CONTENTS.sha256') { continue }
    $entries.Add([PSCustomObject]@{ RelativePath = $relative; SourcePath = $file.File.FullName })
}
foreach ($relative in $additionalFiles.Keys) {
    $source = Get-Item -LiteralPath $additionalFiles[$relative] -Force -ErrorAction Stop
    if ($source.PSIsContainer -or
        ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Additional installer input is invalid: '$($source.FullName)'."
    }
    $entries.Add([PSCustomObject]@{ RelativePath = $relative; SourcePath = $source.FullName })
}
$entries = @($entries | Sort-Object RelativePath)
if (@($entries | Group-Object { $_.RelativePath.ToUpperInvariant() } |
        Where-Object Count -ne 1).Count -ne 0) {
    throw 'Installer payload contains duplicate case-insensitive relative paths.'
}

$directoryMap = @{}
foreach ($entry in $entries) {
    $relativeDirectory = [IO.Path]::GetDirectoryName($entry.RelativePath)
    while (-not [string]::IsNullOrEmpty($relativeDirectory)) {
        $directoryKey = $relativeDirectory.ToUpperInvariant()
        if (-not $directoryMap.ContainsKey($directoryKey)) {
            $directoryHash = Get-WixHash $relativeDirectory.ToLowerInvariant()
            $directoryMap[$directoryKey] = [PSCustomObject]@{
                RelativePath = $relativeDirectory
                Id = 'Dir_' + $directoryHash.Substring(0, 24)
            }
        }
        $relativeDirectory = [IO.Path]::GetDirectoryName($relativeDirectory)
    }
}
$directories = @($directoryMap.Values | Sort-Object `
        @{ Expression = { @($_.RelativePath -split '\\').Count } }, RelativePath)

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add('  <Fragment>')
foreach ($directory in $directories) {
    $parentPath = [IO.Path]::GetDirectoryName($directory.RelativePath)
    $parentId = if ([string]::IsNullOrEmpty($parentPath)) { 'APPLICATIONFOLDER' }
        else { $directoryMap[$parentPath.ToUpperInvariant()].Id }
    $lines.Add('    <DirectoryRef Id="' + $parentId + '">')
    $lines.Add('      <Directory Id="' + $directory.Id + '" Name="' +
        (Escape-Wix ([IO.Path]::GetFileName($directory.RelativePath))) + '" />')
    $lines.Add('    </DirectoryRef>')
}
$lines.Add('  </Fragment>')
$lines.Add('  <Fragment>')
$lines.Add('    <ComponentGroup Id="ApplicationPayload">')
foreach ($entry in $entries) {
    $key = "$Scope|$($entry.RelativePath.ToLowerInvariant())"
    $hash = Get-WixHash $key
    $componentId = 'Cmp_' + $hash.Substring(0, 24)
    $fileId = if ($entry.RelativePath -ceq 'ezyImageViewer.exe') {
        'ApplicationExecutable'
    }
    elseif ($entry.RelativePath -ceq 'InstallerResources\invoke-identity-registration.ps1') {
        'IdentityRegistrationInvoker'
    }
    elseif ($entry.RelativePath -ceq 'InstallerResources\ezyImageViewer.CodecHost.msix') {
        'CodecHostIdentityPackage'
    }
    elseif ($entry.RelativePath -ceq 'InstallerResources\ezyImageViewer.ExternalIdentity.msix') {
        'ExternalIdentityPackage'
    }
    else { 'Fil_' + $hash.Substring(0, 24) }
    $directory = [IO.Path]::GetDirectoryName($entry.RelativePath)
    $directoryId = if ([string]::IsNullOrEmpty($directory)) { 'APPLICATIONFOLDER' }
        else { $directoryMap[$directory.ToUpperInvariant()].Id }
    $guid = Get-WixGuid $key
    $lines.Add('      <Component Id="' + $componentId + '" Guid="' + $guid +
        '" Directory="' + $directoryId + '">')
    $keyPath = if ($Scope -ceq 'PerMachine') { ' KeyPath="yes"' } else { '' }
    $lines.Add('        <File Id="' + $fileId + '" Source="' +
        (Escape-Wix $entry.SourcePath) + '"' + $keyPath + ' />')
    if ($Scope -ceq 'PerUser') {
        $lines.Add('        <RegistryValue Root="HKCU" Key="Software\koprodev\ezy Image Viewer\Installer\Components" Name="' +
            $hash.Substring(0, 32) + '" Type="integer" Value="1" KeyPath="yes" />')
    }
    $lines.Add('      </Component>')
}
$cleanupKey = "$Scope|directory-cleanup"
$cleanupHash = Get-WixHash $cleanupKey
$lines.Add('      <Component Id="DirectoryCleanupComponent" Guid="' +
    (Get-WixGuid $cleanupKey) + '" Directory="APPLICATIONFOLDER">')
foreach ($directory in @($directories | Sort-Object RelativePath -Descending)) {
    $lines.Add('        <RemoveFolder Id="Rfd_' +
        (Get-WixHash $directory.RelativePath.ToLowerInvariant()).Substring(0, 24) +
        '" Directory="' + $directory.Id + '" On="uninstall" />')
}
$lines.Add('        <RemoveFolder Id="RemoveApplicationFolder" Directory="APPLICATIONFOLDER" On="uninstall" />')
if ($Scope -ceq 'PerUser') {
    $lines.Add('        <RemoveFolder Id="RemoveLocalProgramsFolder" Directory="LocalProgramsFolder" On="uninstall" />')
}
$cleanupRoot = if ($Scope -ceq 'PerUser') { 'HKCU' } else { 'HKLM' }
$lines.Add('        <RegistryValue Root="' + $cleanupRoot +
    '" Key="Software\koprodev\ezy Image Viewer\Installer" Name="DirectoryCleanup"' +
    ' Type="integer" Value="1" KeyPath="yes" />')
$lines.Add('      </Component>')
$lines.Add('    </ComponentGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')

$outputFull = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull))
$temporary = $outputFull + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllLines($temporary, $lines, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($outputFull)) { [IO.File]::Delete($outputFull) }
    [IO.File]::Move($temporary, $outputFull)
}
finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }

Write-Output "WiX $Scope payload generated: $outputFull"
Write-Output "components: $($entries.Count)"
