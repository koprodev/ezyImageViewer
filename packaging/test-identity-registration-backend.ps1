#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'identity-registration-backend.ps1')

$script:PassCount = 0
function Assert-Backend([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:PassCount++
}
function Assert-BackendThrows([string]$Label, [scriptblock]$Action) {
    try { & $Action } catch { $script:PassCount++; return }
    throw "Expected backend rejection: $Label."
}

function New-TestPackage {
    param([string]$Path, [string]$Name, [string]$Publisher, [bool]$Framework)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $directory = [IO.Path]::GetDirectoryName($Path)
    [void][IO.Directory]::CreateDirectory($directory)
    $layout = Join-Path $directory ([Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($layout)
    try {
        $properties = if ($Framework) { '<Properties><Framework>true</Framework></Properties>' } else { '' }
        $dependency = if ($Name -ceq 'GRTech.ezyImageViewer') {
            '<Dependencies><PackageDependency Name="GRTech.ezyImageViewer.CodecHost" Publisher="' +
                $Publisher + '" MinVersion="1.0.0.0" /></Dependencies>'
        }
        else { '<Dependencies />' }
        $xml = '<?xml version="1.0" encoding="utf-8"?>' +
            '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">' +
            '<Identity Name="' + $Name + '" Publisher="' + $Publisher +
            '" Version="1.0.0.0" ProcessorArchitecture="neutral" />' + $properties +
            $dependency + '</Package>'
        [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $xml,
            [Text.UTF8Encoding]::new($false))
        [IO.Compression.ZipFile]::CreateFromDirectory($layout, $Path)
    }
    finally { [IO.Directory]::Delete($layout, $true) }
}

function New-FakeAdapter {
    param([hashtable]$State, [string]$FailStep)

    return @{
        GetCurrentUserPackages = {
            param($name)
            return @($State.Current[$name] | ForEach-Object {
                    [PSCustomObject]@{ PackageFullName = $_ }
                })
        }.GetNewClosure()
        GetAllUserPackages = {
            param($name)
            return @($State.All[$name] | ForEach-Object {
                    [PSCustomObject]@{ PackageFullName = $_ }
                })
        }.GetNewClosure()
        GetProvisionedPackages = {
            param($name)
            return @($State.Provisioned[$name] | ForEach-Object {
                    [PSCustomObject]@{ PackageName = $_ }
                })
        }.GetNewClosure()
        AddCurrentUserPackage = {
            param($path, $external)
            $name = if ([string]::IsNullOrEmpty($external)) {
                'GRTech.ezyImageViewer.CodecHost'
            } else { 'GRTech.ezyImageViewer' }
            $State.Calls.Add("add-current:$name")
            if ($FailStep -ceq $name) { throw 'injected add failure' }
            $State.Current[$name] = @($name + '_1.0.0.0_x64_test')
        }.GetNewClosure()
        StageAndProvisionPackage = {
            param($path, $external)
            $name = if ([string]::IsNullOrEmpty($external)) {
                'GRTech.ezyImageViewer.CodecHost'
            } else { 'GRTech.ezyImageViewer' }
            $State.Calls.Add("add-all:$name")
            $State.StageRequests.Add([PSCustomObject]@{
                    Path = $path
                    ExternalLocation = $external
                })
            if ($FailStep -ceq $name) { throw 'injected provision failure' }
            $State.All[$name] = @($name + '_1.0.0.0_x64_test')
            $State.Provisioned[$name] = @($name + '_1.0.0.0_neutral_test')
        }.GetNewClosure()
        RemoveCurrentUserPackage = {
            param($fullName)
            $name = if ($fullName.StartsWith('GRTech.ezyImageViewer.CodecHost')) {
                'GRTech.ezyImageViewer.CodecHost'
            } else { 'GRTech.ezyImageViewer' }
            $State.Calls.Add("remove-current:$name")
            $State.Current[$name] = @()
        }.GetNewClosure()
        RemoveAllUsersPackage = {
            param($fullName)
            $name = if ($fullName.StartsWith('GRTech.ezyImageViewer.CodecHost')) {
                'GRTech.ezyImageViewer.CodecHost'
            } else { 'GRTech.ezyImageViewer' }
            $State.Calls.Add("remove-all:$name")
            $State.All[$name] = @()
        }.GetNewClosure()
        RemoveProvisionedPackage = {
            param($packageName)
            $name = if ($packageName.StartsWith('GRTech.ezyImageViewer.CodecHost')) {
                'GRTech.ezyImageViewer.CodecHost'
            } else { 'GRTech.ezyImageViewer' }
            $State.Calls.Add("deprovision:$name")
            $State.Provisioned[$name] = @()
        }.GetNewClosure()
        HasInstalledDependents = { param($name, $scope) return $false }
    }
}

function New-FakeState {
    return @{
        Current = @{}
        All = @{}
        Provisioned = @{}
        Calls = [Collections.Generic.List[string]]::new()
        StageRequests = [Collections.Generic.List[object]]::new()
    }
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $tempBase ('ezy-identity-backend-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $localizedDirectoryName = -join [char[]](0xC124, 0xCE58)
    $install = Join-Path $root "$localizedDirectoryName folder's"
    [void][IO.Directory]::CreateDirectory($install)
    [IO.File]::WriteAllText((Join-Path $install 'ezyImageViewer.exe'), 'app')
    $codec = Join-Path $root 'codec host.msix'
    $external = Join-Path $root "external identity's.msix"
    New-TestPackage $codec 'GRTech.ezyImageViewer.CodecHost' 'CN=Test Publisher' $true
    New-TestPackage $external 'GRTech.ezyImageViewer' 'CN=Test Publisher' $false

    $defaultStageSource = (New-EzyDefaultIdentityAdapter).StageAndProvisionPackage.Ast.Extent.Text
    Assert-Backend (([regex]::Matches(
                $defaultStageSource,
                'Add-AppxProvisionedPackage\s+-Online\s+-PackagePath\s+\$path\s+-SkipLicense')).Count -eq 2) `
        'Every all-users provisioning branch must pass PackagePath and SkipLicense.'
    Assert-Backend ($defaultStageSource -match
        'Add-AppxPackage\s+-Stage\s+-Path\s+\$path\s+-ExternalLocation\s+\$externalLocation') `
        'The external-location package must be staged against the install directory.'
    Assert-Backend ($defaultStageSource -notmatch 'ExternalLocationPath') `
        'Provisioning must follow the Windows 10 external-location stage contract.'

    $statePath = Join-Path $install 'InstallerResources\identity-state.json'
    $fake = New-FakeState
    $adapter = New-FakeAdapter $fake ''
    $state = Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external `
        $statePath $adapter
    Assert-Backend ([IO.File]::Exists($statePath)) 'Registration state was not persisted.'
    Assert-Backend (($fake.Calls -join '|') -ceq
        'add-current:GRTech.ezyImageViewer.CodecHost|add-current:GRTech.ezyImageViewer') `
        'Current-user registration order is invalid.'
    Assert-Backend (@($state.Steps | Where-Object { $_.Introduced }).Count -eq 2) `
        'Introduced package ownership was not recorded.'
    $repair = Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external `
        $statePath $adapter
    Assert-Backend (@($repair.Steps | Where-Object { $_.Introduced }).Count -eq 2) `
        'Repair lost the original package ownership state.'
    Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath $adapter
    Assert-Backend (($fake.Calls[-2..-1] -join '|') -ceq
        'remove-current:GRTech.ezyImageViewer|remove-current:GRTech.ezyImageViewer.CodecHost') `
        'Current-user removal order is invalid.'
    Assert-Backend (-not [IO.File]::Exists($statePath)) 'Identity state survived uninstall.'

    $preexisting = New-FakeState
    $preexisting.Current['GRTech.ezyImageViewer.CodecHost'] = @('host_preexisting')
    $preAdapter = New-FakeAdapter $preexisting ''
    [void](Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external `
            $statePath $preAdapter)
    Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath $preAdapter
    Assert-Backend ($preexisting.Current['GRTech.ezyImageViewer.CodecHost'][0] -ceq
        'host_preexisting') 'Pre-existing CodecHost was removed.'

    $collision = New-FakeState
    $collision.Current['GRTech.ezyImageViewer'] = @('main_preexisting')
    Assert-BackendThrows 'unowned main identity' {
        Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external $statePath `
            (New-FakeAdapter $collision '')
    }

    $failed = New-FakeState
    Assert-BackendThrows 'main registration failure' {
        Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external $statePath `
            (New-FakeAdapter $failed 'GRTech.ezyImageViewer')
    }
    Assert-Backend (($failed.Calls -join '|') -ceq
        'add-current:GRTech.ezyImageViewer.CodecHost|add-current:GRTech.ezyImageViewer|remove-current:GRTech.ezyImageViewer.CodecHost') `
        'Failed registration did not roll back Host in reverse order.'

    $all = New-FakeState
    $allAdapter = New-FakeAdapter $all ''
    [void](Invoke-EzyIdentityRegister 'AllUsers' $install $codec $external $statePath $allAdapter)
    Assert-Backend (($all.Calls[0..1] -join '|') -ceq
        'add-all:GRTech.ezyImageViewer.CodecHost|add-all:GRTech.ezyImageViewer') `
        'All-users registration order is invalid.'
    Assert-Backend ($all.StageRequests[0].Path -ceq $codec -and
        [string]::IsNullOrEmpty($all.StageRequests[0].ExternalLocation)) `
        'CodecHost all-users stage arguments are invalid.'
    Assert-Backend ($all.StageRequests[1].Path -ceq $external -and
        $all.StageRequests[1].ExternalLocation -ceq $install) `
        'Main identity all-users stage arguments are invalid.'
    Invoke-EzyIdentityUnregister 'AllUsers' $install $statePath $allAdapter
    Assert-Backend (($all.Calls[2..5] -join '|') -ceq
        'deprovision:GRTech.ezyImageViewer|remove-all:GRTech.ezyImageViewer|deprovision:GRTech.ezyImageViewer.CodecHost|remove-all:GRTech.ezyImageViewer.CodecHost') `
        'All-users removal order is invalid.'

    $missingExecutable = New-FakeState
    $missingExecutableAdapter = New-FakeAdapter $missingExecutable ''
    [void](Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external `
            $statePath $missingExecutableAdapter)
    [IO.File]::Delete((Join-Path $install 'ezyImageViewer.exe'))
    Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath $missingExecutableAdapter
    Assert-Backend (-not [IO.File]::Exists($statePath)) `
        'A missing application executable blocked identity removal.'
    [IO.File]::WriteAllText((Join-Path $install 'ezyImageViewer.exe'), 'app')

    [IO.File]::WriteAllText($statePath, '{broken', [Text.UTF8Encoding]::new($false))
    Assert-BackendThrows 'strict corrupt ownership state' {
        Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath `
            (New-FakeAdapter (New-FakeState) '')
    }
    Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath `
        -AllowOwnershipStateFailure -Adapter (New-FakeAdapter (New-FakeState) '')
    Assert-Backend ([IO.File]::Exists($statePath)) `
        'A recoverable corrupt ownership state was modified.'
    [IO.File]::Delete($statePath)

    $scopeMismatch = New-FakeState
    $scopeMismatchAdapter = New-FakeAdapter $scopeMismatch ''
    [void](Invoke-EzyIdentityRegister 'CurrentUser' $install $codec $external `
            $statePath $scopeMismatchAdapter)
    Invoke-EzyIdentityUnregister 'AllUsers' $install $statePath $scopeMismatchAdapter `
        -AllowOwnershipStateFailure
    Assert-Backend ([IO.File]::Exists($statePath) -and
        @($scopeMismatch.Current['GRTech.ezyImageViewer']).Count -eq 1) `
        'A scope-mismatched ownership state removed the current-user identity.'
    Invoke-EzyIdentityUnregister 'CurrentUser' $install $statePath $scopeMismatchAdapter

    Write-Output "Identity registration backend tests passed: $script:PassCount"
}
finally {
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
}
