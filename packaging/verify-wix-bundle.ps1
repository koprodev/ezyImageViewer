#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundlePath,
    [string]$WixToolPath,
    [string]$ProductVersion = '1.0.9'
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

$bundle = Get-Item -LiteralPath ([IO.Path]::GetFullPath($BundlePath)) -Force -ErrorAction Stop
Assert-True (-not $bundle.PSIsContainer) 'BundlePath must reference a file.'
Assert-True (($bundle.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
    'BundlePath must not reference a reparse point.'
Assert-True ($bundle.Extension -ceq '.exe') 'BundlePath must use the .exe extension.'

if ([string]::IsNullOrWhiteSpace($WixToolPath)) {
    $WixToolPath = Join-Path $env:USERPROFILE `
        '.nuget\packages\wixtoolset.sdk\7.0.0\tools\net8.0\wix.dll'
}
$wix = Get-Item -LiteralPath ([IO.Path]::GetFullPath($WixToolPath)) -Force -ErrorAction Stop
Assert-True (-not $wix.PSIsContainer) 'WixToolPath must reference a file.'

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$inspectionRoot = Join-Path $temporaryRoot ('ezy-wix-bundle-' + [Guid]::NewGuid().ToString('N'))
$payloadDirectory = Join-Path $inspectionRoot 'payload'
$baDirectory = Join-Path $inspectionRoot 'ba'
try {
    [void][IO.Directory]::CreateDirectory($inspectionRoot)
    & dotnet $wix.FullName burn extract -acceptEula wix7 $bundle.FullName `
        -o $payloadDirectory -oba $baDirectory
    if ($LASTEXITCODE -ne 0) { throw "WiX bundle extraction failed with exit code $LASTEXITCODE." }

    $manifestPath = Join-Path $baDirectory 'manifest.xml'
    $themePath = Join-Path $baDirectory 'thm.xml'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
    [xml]$theme = Get-Content -LiteralPath $themePath -Raw -ErrorAction Stop

    $manifestNamespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $manifestNamespace.AddNamespace('b', 'http://wixtoolset.org/schemas/v4/2008/Burn')
    $root = $manifest.SelectSingleNode('/b:BurnManifest', $manifestNamespace)
    Assert-Equal 'yes' $root.Win64 'Bundle engine must be x64.'
    Assert-Equal 'VersionNT64 AND WindowsBuildNumber >= 19041' `
        $root.SelectSingleNode('b:Condition', $manifestNamespace).InnerText `
        'Bundle prerequisite condition mismatch.'

    $variables = @{}
    foreach ($variable in $root.SelectNodes('b:Variable', $manifestNamespace)) {
        $variables[[string]$variable.Id] = $variable
    }
    Assert-Equal '[LocalAppDataFolder]Programs\ezy Image Viewer' `
        $variables.PerUserInstallFolder.Value 'Per-user default bundle path mismatch.'
    Assert-Equal '[ProgramFiles64Folder]ezy Image Viewer' `
        $variables.PerMachineInstallFolder.Value 'Per-machine default bundle path mismatch.'
    Assert-Equal '0' $variables.EzyDesktopShortcut.Value `
        'Bundle desktop shortcut must default off.'
    Assert-Equal '1' $variables.EzyFileAssociations.Value `
        'Bundle file associations must default on.'
    Assert-Equal 'ezyImageViewer.exe' $variables.LaunchTarget.Value `
        'Bundle launch target must resolve through the selected scope App Paths registration.'
    Assert-True (-not $variables.ContainsKey('LaunchArguments')) `
        'Bundle launch must not depend on package-identity protocol registration.'
    foreach ($name in @('PerUserInstallFolder', 'PerMachineInstallFolder',
            'EzyDesktopShortcut')) {
        Assert-Equal 'yes' $variables[$name].Persisted `
            "Bundle variable '$name' must be persisted."
    }
    Assert-Equal 'no' $variables.EzyFileAssociations.Persisted `
        'Bundle file association choice must not preserve a previous release default.'
    Assert-True (-not $variables.ContainsKey('WixStdBAScope')) `
        'WixStdBAScope is reserved for WixStdBA and must not be authored as a bundle variable.'

    $packages = @($root.SelectNodes('b:Chain/b:MsiPackage', $manifestNamespace))
    Assert-Equal '2' ([string]$packages.Count) 'Bundle package count mismatch.'
    Assert-Equal 'PerUserPackage' $packages[0].Id 'Per-user package chain order mismatch.'
    Assert-Equal 'perUserOrMachine' $packages[0].Scope `
        'Per-user layout MSI must make the bundle configurable and default to per-user.'
    Assert-Equal 'WixStdBAScope = "PerUser"' $packages[0].InstallCondition `
        'Per-user MSI plan condition mismatch.'
    Assert-Equal 'PerMachinePackage' $packages[1].Id 'Per-machine package chain order mismatch.'
    Assert-Equal 'perMachineOrUser' $packages[1].Scope `
        'Per-machine layout MSI must remain dual-purpose and default to per-machine.'
    Assert-Equal 'WixStdBAScope = "PerMachine"' $packages[1].InstallCondition `
        'Per-machine MSI plan condition mismatch.'

    foreach ($contract in @(
            [PSCustomObject]@{ Package = $packages[0]; Folder = '[PerUserInstallFolder]' },
            [PSCustomObject]@{ Package = $packages[1]; Folder = '[PerMachineInstallFolder]' }
        )) {
        $propertyMap = @{}
        foreach ($property in $contract.Package.SelectNodes('b:MsiProperty', $manifestNamespace)) {
            $propertyMap[[string]$property.Id] = [string]$property.Value
        }
        Assert-Equal $contract.Folder $propertyMap.APPLICATIONFOLDER `
            "$($contract.Package.Id) install folder binding mismatch."
        Assert-Equal '[EzyDesktopShortcut]' $propertyMap.EZY_DESKTOP_SHORTCUT `
            "$($contract.Package.Id) desktop option binding mismatch."
        Assert-Equal '[EzyFileAssociations]' $propertyMap.EZY_FILE_ASSOCIATIONS `
            "$($contract.Package.Id) association option binding mismatch."
    }

    foreach ($package in $packages) {
        $payload = $root.SelectSingleNode("b:Payload[@Id='$($package.Id)']", $manifestNamespace)
        Assert-True ($null -ne $payload) "Bundle payload '$($package.Id)' is missing."
        $containerDirectory = Join-Path $payloadDirectory $payload.Container
        $extracted = Get-Item -LiteralPath (Join-Path $containerDirectory $payload.FilePath) `
            -Force -ErrorAction Stop
        Assert-Equal $payload.FileSize ([string]$extracted.Length) `
            "Bundle payload '$($package.Id)' size mismatch."
        $hash = (Get-FileHash -LiteralPath $extracted.FullName -Algorithm SHA512).Hash
        Assert-Equal $payload.Hash $hash "Bundle payload '$($package.Id)' hash mismatch."
    }

    $themeNamespace = [Xml.XmlNamespaceManager]::new($theme.NameTable)
    $themeNamespace.AddNamespace('t', 'http://wixtoolset.org/schemas/v4/thmutil')
    $scopeRadios = @($theme.SelectNodes(
            '/t:Theme/t:Window/t:Page[@Name="Options"]/t:RadioButtons[@Name="WixStdBAScope"]/t:RadioButton',
            $themeNamespace))
    Assert-Equal '2' ([string]$scopeRadios.Count) 'Bundle scope selector must have two choices.'
    Assert-True (@($scopeRadios | Where-Object Value -CEQ 'PerUser').Count -eq 1) `
        'Bundle per-user scope choice is missing.'
    Assert-True (@($scopeRadios | Where-Object Value -CEQ 'PerMachine').Count -eq 1) `
        'Bundle per-machine scope choice is missing.'
    Assert-True (@($scopeRadios | Where-Object { $_.HasAttribute('EnableCondition') }).Count -eq 0) `
        'Bundle scope choices must not depend on the mixed-package authored scope.'
    foreach ($name in @('PerUserInstallFolder', 'PerMachineInstallFolder')) {
        Assert-True ($null -ne $theme.SelectSingleNode(
                "//t:Editbox[@Name='$name']", $themeNamespace)) `
            "Bundle path editor '$name' is missing."
        Assert-True ($null -ne $theme.SelectSingleNode(
                "//t:BrowseDirectoryAction[@VariableName='$name']", $themeNamespace)) `
            "Bundle path browser '$name' is missing."
    }
    foreach ($name in @('EzyDesktopShortcut', 'EzyFileAssociations')) {
        Assert-True ($null -ne $theme.SelectSingleNode(
                "//t:Checkbox[@Name='$name']", $themeNamespace)) `
            "Bundle option checkbox '$name' is missing."
    }
    Assert-True ($null -ne $theme.SelectSingleNode(
            '/t:Theme/t:Window/t:Page[@Name="Success"]/t:Button[@Name="LaunchButton"]',
            $themeNamespace)) 'Bundle success launch button is missing.'

    Add-Type -AssemblyName System.Drawing
    $logoPath = Join-Path $baDirectory 'logo.png'
    $logo = [Drawing.Bitmap]::FromFile($logoPath)
    try {
        Assert-Equal '150' ([string]$logo.Width) 'Bundle logo width mismatch.'
        Assert-Equal '150' ([string]$logo.Height) 'Bundle logo height mismatch.'
        foreach ($point in @(
                [Drawing.Point]::new(0, 0), [Drawing.Point]::new(149, 0),
                [Drawing.Point]::new(0, 149), [Drawing.Point]::new(149, 149))) {
            Assert-True ($logo.GetPixel($point.X, $point.Y).A -le 8) `
                "Bundle logo corner $($point.X),$($point.Y) is not transparent."
        }
    }
    finally { $logo.Dispose() }

    [xml]$fallbackLocalization = Get-Content -LiteralPath (Join-Path $baDirectory 'thm.wxl') `
        -Raw -ErrorAction Stop
    [xml]$koreanLocalization = Get-Content -LiteralPath (Join-Path $baDirectory '1042\thm.wxl') `
        -Raw -ErrorAction Stop
    Assert-Equal '1033' $fallbackLocalization.WixLocalization.Language `
        'Bundle fallback language mismatch.'
    Assert-Equal '1042' $koreanLocalization.WixLocalization.Language `
        'Bundle Korean language mismatch.'
    foreach ($localization in @($fallbackLocalization, $koreanLocalization)) {
        foreach ($id in @('OptionsDesktopShortcutText', 'OptionsFileAssociationsText',
                'SuccessLaunchButton')) {
            Assert-True ($null -ne @($localization.WixLocalization.String |
                    Where-Object Id -CEQ $id)[0]) `
                "Bundle localization string '$id' is missing."
        }
    }
    $koreanEula = Get-Content -LiteralPath (Join-Path $baDirectory '1042\license.rtf') `
        -Raw -ErrorAction Stop
    Assert-True ($koreanEula.StartsWith('{\rtf1\ansi', [StringComparison]::Ordinal)) `
        'Bundle Korean license is not RTF.'
    Assert-True ($koreanEula.Contains('\u')) `
        'Bundle Korean license does not contain Unicode text.'

    $version = $root.SelectSingleNode('b:Registration', $manifestNamespace).Version
    Assert-Equal $ProductVersion $version 'Bundle registration version mismatch.'
}
finally {
    $resolvedInspectionRoot = [IO.Path]::GetFullPath($inspectionRoot)
    if ([IO.Directory]::Exists($resolvedInspectionRoot)) {
        if (-not $resolvedInspectionRoot.StartsWith($temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedInspectionRoot).StartsWith(
                'ezy-wix-bundle-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected inspection path '$resolvedInspectionRoot'."
        }
        Remove-Item -LiteralPath $resolvedInspectionRoot -Recurse -Force
    }
}

Write-Output "WiX Burn read-only verification passed: $script:AssertionCount assertions"
