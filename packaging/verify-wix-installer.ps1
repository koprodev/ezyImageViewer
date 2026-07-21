#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PerUserMsi,
    [Parameter(Mandatory)][string]$PerMachineMsi,
    [string]$ProductVersion = '1.0.9',
    [int]$ExpectedPayloadFileCount = 549,
    [ValidateSet('0', '1')][string]$ExpectedIdentityRegistration = '1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:AssertionCount = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:AssertionCount++
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
    $script:AssertionCount++
}

function Get-MsiRows($Database, [string]$Table, [string[]]$Columns) {
    $projection = ($Columns | ForEach-Object { '`' + $_ + '`' }) -join ','
    $query = "SELECT $projection FROM ``$Table``"
    $view = $null
    try {
        $view = $Database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $Database, @($query))
        [void]$view.GetType().InvokeMember(
            'Execute', 'InvokeMethod', $null, $view, $null)
        $rows = [Collections.Generic.List[object]]::new()
        while ($true) {
            $record = $view.GetType().InvokeMember(
                'Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }
            try {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Count; $index++) {
                    $row[$Columns[$index]] = $record.GetType().InvokeMember(
                        'StringData', 'GetProperty', $null, $record, @($index + 1))
                }
                $rows.Add([PSCustomObject]$row)
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
        return @($rows)
    }
    finally {
        if ($null -ne $view) {
            [void]$view.GetType().InvokeMember(
                'Close', 'InvokeMethod', $null, $view, $null)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
}

function Get-Map([object[]]$Rows, [string]$Key) {
    $result = @{}
    foreach ($row in $Rows) { $result[[string]$row.$Key] = $row }
    return $result
}

function Open-MsiReadOnly([string]$Path) {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force -ErrorAction Stop
    Assert-True (-not $item.PSIsContainer) "MSI path is not a file: '$Path'."
    Assert-True (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "MSI path must not be a reparse point: '$Path'."
    Assert-True ($item.Extension -ceq '.msi') "Installer must use the .msi extension: '$Path'."
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($item.FullName, 0))
        return [PSCustomObject]@{
            Installer = $installer
            Database = $database
            Path = $item.FullName
        }
    }
    catch {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        throw
    }
}

function Close-Msi($Handle) {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Handle.Database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Handle.Installer)
}

function Test-MsiContract(
    [string]$Path,
    [ValidateSet('PerUser', 'PerMachine')][string]$Scope,
    [string]$ExpectedUpgradeCode
) {
    $handle = Open-MsiReadOnly $Path
    try {
        $properties = Get-Map (Get-MsiRows $handle.Database 'Property' @('Property', 'Value')) 'Property'
        Assert-Equal $ProductVersion $properties.ProductVersion.Value "$Scope ProductVersion mismatch."
        Assert-Equal '0' $properties.EZY_DESKTOP_SHORTCUT.Value `
            "$Scope desktop shortcut must default off."
        Assert-Equal '0' $properties.EZY_FILE_ASSOCIATIONS.Value `
            "$Scope file associations must default off."
        Assert-Equal $ExpectedIdentityRegistration $properties.EZY_REGISTER_IDENTITY.Value `
            "$Scope identity registration mode mismatch."
        Assert-Equal $ExpectedUpgradeCode $properties.UpgradeCode.Value `
            "$Scope UpgradeCode mismatch."
        if ($Scope -ceq 'PerUser') {
            Assert-Equal '2' $properties.ALLUSERS.Value `
                'Per-user layout MSI must be dual-purpose.'
            Assert-Equal '1' $properties.MSIINSTALLPERUSER.Value `
                'Per-user layout MSI must default to per-user.'
            Assert-Equal 'WixPerUserFolder' $properties.WixAppFolder.Value `
                'Per-user MSI folder mode mismatch.'
        }
        else {
            Assert-Equal '2' $properties.ALLUSERS.Value `
                'Per-machine layout MSI must be dual-purpose.'
            Assert-True (-not $properties.ContainsKey('MSIINSTALLPERUSER')) `
                'Per-machine layout MSI must default to per-machine.'
            Assert-Equal 'WixPerMachineFolder' $properties.WixAppFolder.Value `
                'Per-machine MSI folder mode mismatch.'
        }

        $launchConditions = Get-MsiRows $handle.Database 'LaunchCondition' `
            @('Condition', 'Description')
        Assert-True (@($launchConditions | Where-Object {
                    $_.Condition -ceq 'VersionNT64 AND WINDOWSBUILD >= 19041'
                }).Count -eq 1) "$Scope Windows 19041 x64 launch condition is missing."
        Assert-True (@($launchConditions | Where-Object {
                    $_.Condition -ceq 'NOT WIX_DOWNGRADE_DETECTED'
                }).Count -eq 1) "$Scope downgrade block is missing."

        $featureMap = Get-Map (Get-MsiRows $handle.Database 'Feature' `
                @('Feature', 'Level', 'Attributes')) 'Feature'
        Assert-Equal '1' $featureMap.Core.Level "$Scope Core feature level mismatch."
        Assert-Equal '1' $featureMap.StartMenu.Level "$Scope StartMenu feature level mismatch."
        Assert-Equal '2' $featureMap.Desktop.Level "$Scope Desktop feature must default off."
        Assert-Equal '2' $featureMap.FileAssociations.Level `
            "$Scope file associations feature must default off."

        $featureConditions = Get-MsiRows $handle.Database 'Condition' `
            @('Feature_', 'Level', 'Condition')
        Assert-True (@($featureConditions | Where-Object {
                    $_.Feature_ -ceq 'Desktop' -and $_.Level -ceq '1' -and
                    $_.Condition -ceq 'EZY_DESKTOP_SHORTCUT = 1'
                }).Count -eq 1) "$Scope desktop opt-in condition mismatch."
        Assert-True (@($featureConditions | Where-Object {
                    $_.Feature_ -ceq 'FileAssociations' -and $_.Level -ceq '1' -and
                    $_.Condition -ceq 'EZY_FILE_ASSOCIATIONS = 1'
                }).Count -eq 1) "$Scope association opt-in condition mismatch."

        $files = Get-MsiRows $handle.Database 'File' `
            @('File', 'Component_', 'FileName', 'FileSize', 'Version', 'Language')
        Assert-Equal ([string]$ExpectedPayloadFileCount) ([string]$files.Count) `
            "$Scope payload file count mismatch."
        $fileMap = Get-Map $files 'File'
        foreach ($id in @('ApplicationExecutable', 'IdentityRegistrationInvoker',
                'CodecHostIdentityPackage', 'ExternalIdentityPackage')) {
            Assert-True $fileMap.ContainsKey($id) "$Scope required file '$id' is missing."
        }

        $components = Get-Map (Get-MsiRows $handle.Database 'Component' `
                @('Component', 'Directory_', 'Attributes', 'KeyPath')) 'Component'
        $registry = Get-MsiRows $handle.Database 'Registry' `
            @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_')
        foreach ($file in $files) {
            $component = $components[[string]$file.Component_]
            Assert-True ($null -ne $component) `
                "$Scope file '$($file.File)' has no component."
            if ($Scope -ceq 'PerUser') {
                Assert-True ($component.KeyPath -cne $file.File) `
                    "Per-user file '$($file.File)' must use a registry key path."
                Assert-True (@($registry | Where-Object {
                            $_.Component_ -ceq $file.Component_ -and $_.Root -ceq '1'
                        }).Count -ge 1) `
                    "Per-user file '$($file.File)' has no HKCU key path row."
            }
            else {
                Assert-Equal $file.File $component.KeyPath `
                    "Per-machine file '$($file.File)' must be its component key path."
            }
        }

        $directoryMap = Get-Map (Get-MsiRows $handle.Database 'Directory' `
                @('Directory', 'Directory_Parent', 'DefaultDir')) 'Directory'
        if ($Scope -ceq 'PerUser') {
            Assert-Equal 'LocalProgramsFolder' $directoryMap.APPLICATIONFOLDER.Directory_Parent `
                'Per-user application directory parent mismatch.'
            Assert-Equal 'LocalAppDataFolder' $directoryMap.LocalProgramsFolder.Directory_Parent `
                'Per-user Programs directory parent mismatch.'
        }
        else {
            Assert-Equal 'ProgramFiles64Folder' $directoryMap.APPLICATIONFOLDER.Directory_Parent `
                'Per-machine application directory parent mismatch.'
        }

        $customActions = Get-Map (Get-MsiRows $handle.Database 'CustomAction' `
                @('Action', 'Type', 'Source', 'Target')) 'Action'
        $scopeName = if ($Scope -ceq 'PerUser') { 'CurrentUser' } else { 'AllUsers' }
        foreach ($action in @('SetRollbackUnregisterIdentity', 'SetUnregisterIdentity',
                'SetRollbackIdentity', 'SetRegisterIdentity')) {
            Assert-True ($customActions.ContainsKey($action)) `
                "$Scope custom action '$action' is missing."
            Assert-True ($customActions[$action].Target.Length -le 255) `
                "$Scope custom action '$action' exceeds the MSI Target limit."
            Assert-True ($customActions[$action].Target.Contains(
                    '[#IdentityRegistrationInvoker]')) `
                "$Scope custom action '$action' does not use the installed invoker."
            Assert-True ($customActions[$action].Target.Contains("-Scope $scopeName")) `
                "$Scope custom action '$action' registration scope mismatch."
        }
        $expectedDeferredType = if ($Scope -ceq 'PerUser') { '1025' } else { '3073' }
        $expectedRollbackType = if ($Scope -ceq 'PerUser') { '1281' } else { '3329' }
        Assert-Equal $expectedDeferredType $customActions.RegisterIdentity.Type `
            "$Scope register impersonation mismatch."
        Assert-Equal $expectedDeferredType $customActions.UnregisterIdentity.Type `
            "$Scope unregister impersonation mismatch."
        Assert-Equal $expectedRollbackType $customActions.RollbackIdentity.Type `
            "$Scope install rollback impersonation mismatch."
        Assert-Equal $expectedRollbackType $customActions.RollbackUnregisterIdentity.Type `
            "$Scope uninstall rollback impersonation mismatch."

        $sequence = Get-Map (Get-MsiRows $handle.Database 'InstallExecuteSequence' `
                @('Action', 'Condition', 'Sequence')) 'Action'
        Assert-Equal '1502' $sequence.RollbackUnregisterIdentity.Sequence `
            "$Scope uninstall rollback sequence mismatch."
        Assert-Equal '1504' $sequence.UnregisterIdentity.Sequence `
            "$Scope unregister sequence mismatch."
        Assert-Equal '4002' $sequence.RollbackIdentity.Sequence `
            "$Scope install rollback sequence mismatch."
        Assert-Equal '4004' $sequence.RegisterIdentity.Sequence `
            "$Scope register sequence mismatch."
        Assert-Equal 'EZY_REGISTER_IDENTITY = 1 AND REMOVE~="ALL"' `
            $sequence.RollbackUnregisterIdentity.Condition `
            "$Scope uninstall rollback condition mismatch."
        Assert-Equal 'EZY_REGISTER_IDENTITY = 1 AND REMOVE~="ALL"' `
            $sequence.UnregisterIdentity.Condition `
            "$Scope uninstall condition mismatch."
        Assert-Equal 'EZY_REGISTER_IDENTITY = 1 AND NOT Installed' `
            $sequence.RollbackIdentity.Condition `
            "$Scope install rollback condition mismatch."
        Assert-Equal 'EZY_REGISTER_IDENTITY = 1 AND (NOT Installed OR REINSTALL)' `
            $sequence.RegisterIdentity.Condition `
            "$Scope repair registration condition mismatch."

        $associationRoot = if ($Scope -ceq 'PerUser') { '1' } else { '2' }
        $applicationPathRows = @($registry | Where-Object {
                $_.Component_ -ceq 'ApplicationPathComponent'
            })
        Assert-Equal '1' ([string]$applicationPathRows.Count) `
            "$Scope App Paths registration row count mismatch."
        Assert-Equal $associationRoot $applicationPathRows[0].Root `
            "$Scope App Paths registry root mismatch."
        Assert-Equal 'Software\Microsoft\Windows\CurrentVersion\App Paths\ezyImageViewer.exe' `
            $applicationPathRows[0].Key "$Scope App Paths key mismatch."
        Assert-Equal '[APPLICATIONFOLDER]ezyImageViewer.exe' $applicationPathRows[0].Value `
            "$Scope App Paths executable target mismatch."

        $associationRows = @($registry | Where-Object {
                $_.Component_ -ceq 'FileAssociationComponent'
            })
        Assert-True ($associationRows.Count -ge 22) `
            "$Scope file association registry contract is incomplete."
        Assert-True (@($associationRows | Where-Object {
                    $_.Root -cne $associationRoot
                }).Count -eq 0) "$Scope file association registry root mismatch."
        foreach ($extension in @('png', 'jpg', 'jpeg', 'bmp', 'gif', 'webp', 'tif', 'tiff')) {
            $openWithKey = "Software\Classes\.$extension\OpenWithProgids"
            Assert-True (@($associationRows | Where-Object {
                        $_.Key -ceq $openWithKey -and
                        $_.Name -ceq 'ezyImageViewer.Image'
                    }).Count -eq 1) "$Scope .$extension OpenWith registration mismatch."
            $defaultKey = "Software\Classes\.$extension"
            Assert-True (@($associationRows | Where-Object {
                        $_.Key -ceq $defaultKey
                    }).Count -eq 0) "$Scope .$extension must not become the default handler."
        }

        $shortcuts = Get-MsiRows $handle.Database 'Shortcut' `
            @('Shortcut', 'Directory_', 'Name', 'Component_', 'Target')
        Assert-True (@($shortcuts | Where-Object Shortcut -CEQ 'StartMenuShortcut').Count -eq 1) `
            "$Scope Start Menu shortcut row is missing."
        Assert-True (@($shortcuts | Where-Object Shortcut -CEQ 'DesktopShortcut').Count -eq 1) `
            "$Scope desktop shortcut row is missing."
    }
    finally { Close-Msi $handle }
}

Test-MsiContract $PerUserMsi 'PerUser' '{3397C80C-EF0F-4531-B152-860A092A6437}'
Test-MsiContract $PerMachineMsi 'PerMachine' '{63E0CE6E-1713-45D6-B994-0F4B5E7E194E}'

Write-Output "WiX MSI read-only verification passed: $script:AssertionCount assertions"
