#Requires -Version 5.1

# 실제 MSIX 안의 ID로 결정적 App Installer 파일 생성.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainPackage,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$AppInstallerUri,

    [Parameter(Mandatory = $true)]
    [string]$MainPackageUri,

    [ValidateSet('None', 'OnLaunch')]
    [string]$UpdateMode = 'None',

    [ValidateRange(0, 255)]
    [int]$HoursBetweenUpdateChecks = 24
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'appinstaller-helpers.ps1')

if ($UpdateMode -eq 'None' -and
    $PSBoundParameters.ContainsKey('HoursBetweenUpdateChecks')) {
    throw '-HoursBetweenUpdateChecks requires -UpdateMode OnLaunch.'
}

$pair = Get-EzyReleasePackageContract -MainPackage $MainPackage
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputFileName = [IO.Path]::GetFileName($outputFullPath)
if (-not [string]::Equals(
        [IO.Path]::GetExtension($outputFileName),
        '.appinstaller',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must have the .appinstaller extension: '$OutputPath'."
}
Assert-EzyAsciiText -Value $outputFileName -Label 'OutputPath basename'

$appInstallerUriValue = Resolve-EzyAppInstallerHttpsUri `
    -Value $AppInstallerUri -ExpectedFileName $outputFileName -Label 'AppInstallerUri'
$mainPackageUriValue = Resolve-EzyAppInstallerHttpsUri `
    -Value $MainPackageUri -ExpectedFileName $pair.Main.File.Name -Label 'MainPackageUri'
$uriValues = @($appInstallerUriValue, $mainPackageUriValue)
if (@($uriValues | Select-Object -Unique).Count -ne $uriValues.Count) {
    throw 'AppInstallerUri and MainPackageUri must be unique.'
}

$memory = [IO.MemoryStream]::new()
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.NewLineChars = "`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace
$settings.OmitXmlDeclaration = $false
$settings.CloseOutput = $false
$writer = $null
try {
    $writer = [Xml.XmlWriter]::Create($memory, $settings)
    $writer.WriteStartDocument()
    $writer.WriteStartElement('AppInstaller', $script:EzyAppInstallerNamespace)
    $writer.WriteAttributeString('Version', $pair.Main.Version)
    $writer.WriteAttributeString('Uri', $appInstallerUriValue)

    $writer.WriteStartElement('MainPackage', $script:EzyAppInstallerNamespace)
    $writer.WriteAttributeString('Name', $pair.Main.Name)
    $writer.WriteAttributeString('Publisher', $pair.Main.Publisher)
    $writer.WriteAttributeString('Version', $pair.Main.Version)
    $writer.WriteAttributeString('ProcessorArchitecture', $pair.Main.Architecture)
    $writer.WriteAttributeString('Uri', $mainPackageUriValue)
    $writer.WriteEndElement()

    if ($UpdateMode -eq 'OnLaunch') {
        $writer.WriteStartElement('UpdateSettings', $script:EzyAppInstallerNamespace)
        $writer.WriteStartElement('OnLaunch', $script:EzyAppInstallerNamespace)
        $writer.WriteAttributeString(
            'HoursBetweenUpdateChecks',
            $HoursBetweenUpdateChecks.ToString(
                [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndDocument()
    $writer.Flush()
    $bytes = $memory.ToArray()
}
finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }
    $memory.Dispose()
}

$generated = Read-EzySecureXmlBytes `
    -Bytes $bytes -Label 'Generated AppInstaller' -RequireAsciiWithoutBom
Assert-EzyAppInstallerDocument `
    -Document $generated `
    -Pair $pair `
    -AppInstallerUri $appInstallerUriValue `
    -MainPackageUri $mainPackageUriValue `
    -ExpectedUpdateMode $UpdateMode `
    -HoursBetweenUpdateChecks $HoursBetweenUpdateChecks
$writtenPath = Write-EzyAppInstallerFile -Path $outputFullPath -Bytes $bytes

Write-Output "appinstaller: $writtenPath"
Write-Output "identity: $($pair.Main.Name) $($pair.Main.Version) $($pair.Main.Architecture)"
Write-Output "update mode: $UpdateMode"
