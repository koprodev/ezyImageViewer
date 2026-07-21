Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'identity-registration-contract.ps1')

function Get-EzyIdentityPhysicalFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    $fullPath = Get-EzyRegistrationPhysicalPath $Path 'Leaf' $Label
    if ([IO.Path]::GetExtension($fullPath) -cne '.msix') {
        throw "$Label must use the exact .msix extension."
    }
    return $fullPath
}

function Read-EzyIdentityPackageManifest {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 1MB) {
            throw "$Label must contain exactly one bounded AppxManifest.xml."
        }
        $stream = $entries[0].Open()
        try {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.MaxCharactersInDocument = 1MB
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.PreserveWhitespace = $true
                $document.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }

    $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $document.SelectSingleNode('/f:Package/f:Identity', $manager)
    if ($null -eq $identity) { throw "$Label identity is missing." }
    $name = $identity.GetAttribute('Name')
    $publisher = $identity.GetAttribute('Publisher')
    $version = $identity.GetAttribute('Version')
    if ([string]::IsNullOrWhiteSpace($name) -or
        [string]::IsNullOrWhiteSpace($publisher) -or
        [string]::IsNullOrWhiteSpace($version)) {
        throw "$Label identity is incomplete."
    }

    $dependencies = @($document.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $manager) |
        ForEach-Object {
            [PSCustomObject][ordered]@{
                Name = $_.GetAttribute('Name')
                Publisher = $_.GetAttribute('Publisher')
                MinVersion = $_.GetAttribute('MinVersion')
            }
        })
    $frameworkNode = $document.SelectSingleNode('/f:Package/f:Properties/f:Framework', $manager)
    return [PSCustomObject][ordered]@{
        Name = $name
        Publisher = $publisher
        Version = $version
        IsFramework = $null -ne $frameworkNode -and $frameworkNode.InnerText -ceq 'true'
        Dependencies = $dependencies
    }
}

function Assert-EzyIdentityPackagePair {
    param(
        [Parameter(Mandatory)][string]$CodecHostPackagePath,
        [Parameter(Mandatory)][string]$ExternalPackagePath
    )

    $codecPath = Get-EzyIdentityPhysicalFile $CodecHostPackagePath 'CodecHostPackage'
    $externalPath = Get-EzyIdentityPhysicalFile $ExternalPackagePath 'ExternalPackage'
    if ([string]::Equals($codecPath, $externalPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CodecHost and external identity package paths must be distinct.'
    }
    $codec = Read-EzyIdentityPackageManifest $codecPath 'CodecHostPackage'
    $external = Read-EzyIdentityPackageManifest $externalPath 'ExternalPackage'
    if ($codec.Name -cne 'GRTech.ezyImageViewer.CodecHost' -or -not $codec.IsFramework) {
        throw 'CodecHost package identity or framework contract mismatch.'
    }
    if ($external.Name -cne 'GRTech.ezyImageViewer') {
        throw 'External package identity contract mismatch.'
    }
    if ($codec.Publisher -cne $external.Publisher) {
        throw 'CodecHost and external identity Publisher values must match exactly.'
    }
    $hostDependencies = @($external.Dependencies | Where-Object {
            $_.Name -ceq $codec.Name -and $_.Publisher -ceq $codec.Publisher
        })
    if ($hostDependencies.Count -ne 1) {
        throw 'External identity must contain exactly one matching CodecHost dependency.'
    }
    return [PSCustomObject][ordered]@{
        CodecHostPath = $codecPath
        ExternalPath = $externalPath
        CodecHost = $codec
        External = $external
    }
}

function New-EzyDefaultIdentityAdapter {
    return @{
        GetCurrentUserPackages = {
            param($name)
            return @(Get-AppxPackage -Name $name -ErrorAction Stop)
        }
        GetAllUserPackages = {
            param($name)
            return @(Get-AppxPackage -AllUsers -Name $name -ErrorAction Stop)
        }
        GetProvisionedPackages = {
            param($name)
            return @(Get-AppxProvisionedPackage -Online -ErrorAction Stop |
                Where-Object { $_.DisplayName -ceq $name })
        }
        AddCurrentUserPackage = {
            param($path, $externalLocation)
            if ([string]::IsNullOrEmpty($externalLocation)) {
                Add-AppxPackage -Path $path -ErrorAction Stop
            }
            else {
                Add-AppxPackage -Path $path -ExternalLocation $externalLocation -ErrorAction Stop
            }
        }
        StageAndProvisionPackage = {
            param($path, $externalLocation)
            if ([string]::IsNullOrEmpty($externalLocation)) {
                Add-AppxPackage -Stage -Path $path -ErrorAction Stop
                Add-AppxProvisionedPackage -Online -PackagePath $path -SkipLicense `
                    -ErrorAction Stop | Out-Null
            }
            else {
                Add-AppxPackage -Stage -Path $path -ExternalLocation $externalLocation `
                    -ErrorAction Stop
                Add-AppxProvisionedPackage -Online -PackagePath $path -SkipLicense `
                    -ErrorAction Stop | Out-Null
            }
        }
        RemoveCurrentUserPackage = {
            param($fullName)
            Remove-AppxPackage -Package $fullName -ErrorAction Stop
        }
        RemoveAllUsersPackage = {
            param($fullName)
            Remove-AppxPackage -Package $fullName -AllUsers -ErrorAction Stop
        }
        RemoveProvisionedPackage = {
            param($packageName)
            Remove-AppxProvisionedPackage -Online -PackageName $packageName `
                -ErrorAction Stop | Out-Null
        }
        HasInstalledDependents = {
            param($dependencyName, $scope)
            $packages = if ($scope -ceq 'CurrentUser') {
                @(Get-AppxPackage -PackageTypeFilter Main -ErrorAction Stop)
            }
            else {
                @(Get-AppxPackage -AllUsers -PackageTypeFilter Main -ErrorAction Stop)
            }
            foreach ($package in $packages) {
                foreach ($dependency in @($package.Dependencies)) {
                    if ($dependency.Id.Name -ceq $dependencyName) { return $true }
                }
            }
            return $false
        }
    }
}

function Get-EzyIdentitySnapshot {
    param(
        [Parameter(Mandatory)][ValidateSet('CurrentUser', 'AllUsers')][string]$Scope,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][hashtable]$Adapter
    )

    $installed = if ($Scope -ceq 'CurrentUser') {
        @(& $Adapter.GetCurrentUserPackages $PackageName)
    }
    else { @(& $Adapter.GetAllUserPackages $PackageName) }
    $provisioned = if ($Scope -ceq 'AllUsers') {
        @(& $Adapter.GetProvisionedPackages $PackageName)
    }
    else { @() }
    return [PSCustomObject][ordered]@{
        InstalledFullNames = @($installed | ForEach-Object { $_.PackageFullName } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        ProvisionedPackageNames = @($provisioned | ForEach-Object { $_.PackageName } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    }
}

function Test-EzyIdentitySnapshotPresent {
    param([Parameter(Mandatory)][PSCustomObject]$Snapshot)
    return @($Snapshot.InstalledFullNames).Count -gt 0 -or
        @($Snapshot.ProvisionedPackageNames).Count -gt 0
}

function Write-EzyIdentityState {
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][PSCustomObject]$State
    )

    $directory = [IO.Path]::GetDirectoryName($StatePath)
    [void][IO.Directory]::CreateDirectory($directory)
    $temporary = $StatePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $StatePath + '.' + [Guid]::NewGuid().ToString('N') + '.bak'
    try {
        $json = $State | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($temporary, $json.Replace("`r`n", "`n") + "`n",
            [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($StatePath)) {
            [IO.File]::Replace($temporary, $StatePath, $backup, $true)
        }
        else { [IO.File]::Move($temporary, $StatePath) }
    }
    finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }
}

function Read-EzyIdentityState {
    param([Parameter(Mandatory)][string]$StatePath)

    if (-not [IO.File]::Exists($StatePath)) { return $null }
    $file = Get-Item -LiteralPath $StatePath -Force -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt 1MB -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Identity state must be a non-empty bounded physical file.'
    }
    $state = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    if ($state.SchemaVersion -ne 1 -or $state.Action -cne 'Register' -or
        $state.Scope -notin @('CurrentUser', 'AllUsers') -or
        @($state.Steps).Count -ne 2) {
        throw 'Identity state contract mismatch.'
    }
    return $state
}

function Remove-EzyIntroducedIdentityStep {
    param(
        [Parameter(Mandatory)][PSCustomObject]$Step,
        [Parameter(Mandatory)][ValidateSet('CurrentUser', 'AllUsers')][string]$Scope,
        [Parameter(Mandatory)][hashtable]$Adapter
    )

    if (-not $Step.Introduced) { return }
    if ($Step.StepId -ceq 'codec-host' -and
        (& $Adapter.HasInstalledDependents 'GRTech.ezyImageViewer.CodecHost' $Scope)) {
        return
    }
    foreach ($packageName in @($Step.ProvisionedPackageNames)) {
        & $Adapter.RemoveProvisionedPackage $packageName
    }
    foreach ($fullName in @($Step.InstalledFullNames)) {
        if ($Scope -ceq 'CurrentUser') { & $Adapter.RemoveCurrentUserPackage $fullName }
        else { & $Adapter.RemoveAllUsersPackage $fullName }
    }
}

function Invoke-EzyIdentityRegister {
    param(
        [Parameter(Mandatory)][ValidateSet('CurrentUser', 'AllUsers')][string]$Scope,
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$CodecHostPackagePath,
        [Parameter(Mandatory)][string]$ExternalPackagePath,
        [Parameter(Mandatory)][string]$StatePath,
        [hashtable]$Adapter = (New-EzyDefaultIdentityAdapter)
    )

    $plan = New-EzyIdentityRegistrationPlan 'Register' $Scope $InstallDirectory `
        $CodecHostPackagePath $ExternalPackagePath
    $pair = Assert-EzyIdentityPackagePair $CodecHostPackagePath $ExternalPackagePath
    $existingState = Read-EzyIdentityState $StatePath
    if ($null -ne $existingState -and
        ($existingState.Scope -cne $Scope -or
            $existingState.InstallDirectory -cne $plan.InstallDirectory)) {
        throw 'Existing identity state belongs to a different scope or install directory.'
    }

    $completed = [Collections.Generic.List[object]]::new()
    try {
        foreach ($step in $plan.Steps) {
            $packageName = if ($step.StepId -ceq 'codec-host') {
                $plan.Identity.CodecHostPackageName
            }
            else { $plan.Identity.MainPackageName }
            $before = Get-EzyIdentitySnapshot $Scope $packageName $Adapter
            $present = Test-EzyIdentitySnapshotPresent $before
            $previousStep = if ($null -ne $existingState) {
                @($existingState.Steps | Where-Object { $_.StepId -ceq $step.StepId }) |
                    Select-Object -First 1
            }
            else { $null }
            if ($step.StepId -ceq 'main-identity' -and $present -and $null -eq $existingState) {
                throw 'A pre-existing GRTech.ezyImageViewer package is not owned by this installer. Remove the previous packaged installation before retrying.'
            }
            if (-not $present) {
                $externalLocation = if ($step.StepId -ceq 'main-identity') {
                    $plan.InstallDirectory
                }
                else { $null }
                if ($Scope -ceq 'CurrentUser') {
                    & $Adapter.AddCurrentUserPackage $step.Arguments.PackagePath $externalLocation
                }
                else {
                    & $Adapter.StageAndProvisionPackage $step.Arguments.PackagePath `
                        $externalLocation
                }
            }
            $after = Get-EzyIdentitySnapshot $Scope $packageName $Adapter
            if (-not (Test-EzyIdentitySnapshotPresent $after)) {
                throw "Identity step '$($step.StepId)' did not produce a registered package."
            }
            $completed.Add([PSCustomObject][ordered]@{
                    StepId = $step.StepId
                    PackageName = $packageName
                    Introduced = if ($present -and $null -ne $previousStep) {
                        [bool]$previousStep.Introduced
                    }
                    else { -not $present }
                    InstalledFullNames = @($after.InstalledFullNames)
                    ProvisionedPackageNames = @($after.ProvisionedPackageNames)
                })
        }
        $state = [PSCustomObject][ordered]@{
            SchemaVersion = 1
            Action = 'Register'
            Scope = $Scope
            InstallDirectory = $plan.InstallDirectory
            Publisher = $pair.External.Publisher
            Steps = @($completed)
        }
        Write-EzyIdentityState $StatePath $state
        return $state
    }
    catch {
        $rollbackFailed = $false
        for ($index = $completed.Count - 1; $index -ge 0; $index--) {
            try { Remove-EzyIntroducedIdentityStep $completed[$index] $Scope $Adapter }
            catch { $rollbackFailed = $true }
        }
        if ($rollbackFailed) { throw 'Identity registration failed and rollback was incomplete.' }
        throw
    }
}

function Invoke-EzyIdentityUnregister {
    param(
        [Parameter(Mandatory)][ValidateSet('CurrentUser', 'AllUsers')][string]$Scope,
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$StatePath,
        [hashtable]$Adapter = (New-EzyDefaultIdentityAdapter),
        [switch]$AllowOwnershipStateFailure
    )

    $plan = New-EzyIdentityRegistrationPlan 'Unregister' $Scope $InstallDirectory
    try { $state = Read-EzyIdentityState $StatePath }
    catch {
        if (-not $AllowOwnershipStateFailure) { throw }
        Write-Warning 'Identity ownership state is unreadable. Package removal was skipped so MSI uninstall can continue safely.'
        return
    }
    if ($null -eq $state) { return }
    if ($state.Scope -cne $Scope -or $state.InstallDirectory -cne $plan.InstallDirectory) {
        $message = 'Identity state does not match the requested uninstall scope or directory.'
        if (-not $AllowOwnershipStateFailure) { throw $message }
        Write-Warning "$message Package removal was skipped so MSI uninstall can continue safely."
        return
    }
    $byId = @{}
    foreach ($step in @($state.Steps)) { $byId[$step.StepId] = $step }
    $missingStepIds = @(@('main-identity', 'codec-host') | Where-Object {
            -not $byId.ContainsKey($_)
        })
    if ($missingStepIds.Count -gt 0) {
        $message = "Identity state is missing '$($missingStepIds -join "', '")'."
        if (-not $AllowOwnershipStateFailure) { throw $message }
        Write-Warning "$message Package removal was skipped so MSI uninstall can continue safely."
        return
    }
    foreach ($stepId in @('main-identity', 'codec-host')) {
        Remove-EzyIntroducedIdentityStep $byId[$stepId] $Scope $Adapter
    }
    [IO.File]::Delete($StatePath)
}
