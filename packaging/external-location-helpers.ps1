Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExternalManifestNamespace =
    'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$script:ExternalUapNamespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10'
$script:ExternalUap10Namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
$script:ExternalRestrictedCapabilityNamespace =
    'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
$script:AssemblyManifestNamespace = 'urn:schemas-microsoft-com:asm.v1'
$script:MsixManifestNamespace = 'urn:schemas-microsoft-com:msix.v1'
$script:CompatibilityManifestNamespace = 'urn:schemas-microsoft-com:compatibility.v1'
$script:AssemblyV3ManifestNamespace = 'urn:schemas-microsoft-com:asm.v3'
$script:DpiAwarenessManifestNamespace =
    'http://schemas.microsoft.com/SMI/2016/WindowsSettings'
$script:XmlnsNamespace = 'http://www.w3.org/2000/xmlns/'

function Assert-EzyExternalFourPartVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Label
    )

    if ($Value -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "$Label must be a canonical four-part version: '$Value'."
    }

    $parts = $Value.Split('.')
    foreach ($part in $parts) {
        $component = 0
        if (-not [int]::TryParse($part, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$component) -or
            $component -lt 0 -or $component -gt 65535 -or
            $component.ToString([Globalization.CultureInfo]::InvariantCulture) -ne $part) {
            throw "$Label contains a non-canonical or out-of-range component: '$Value'."
        }
    }
}

function Assert-EzyExternalPublisher {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -ne $Value.Trim() -or
        $Value.Contains('{{') -or -not $Value.Contains('=')) {
        throw "Publisher must be a resolved X.500 distinguished name: '$Value'."
    }

    try {
        [void][Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Value)
    }
    catch {
        throw "Publisher is not a valid X.500 distinguished name: '$Value'."
    }
}

function Assert-EzyExternalMinVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value -notin @('10.0.19041.0', '10.0.26100.0')) {
        throw "MinVersion must be an explicit supported baseline: '$Value'."
    }
}

function Read-EzyExternalXml {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($resolvedPath)) {
        throw "XML input does not exist: '$resolvedPath'."
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1MB
    $settings.IgnoreComments = $false

    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    $document.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($resolvedPath, $settings)
    try {
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    return ,$document
}

function New-EzyExternalNamespaceManager {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument]$Document
    )

    $namespaces = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespaces.AddNamespace('f', $script:ExternalManifestNamespace)
    $namespaces.AddNamespace('uap', $script:ExternalUapNamespace)
    $namespaces.AddNamespace('uap10', $script:ExternalUap10Namespace)
    $namespaces.AddNamespace('rescap', $script:ExternalRestrictedCapabilityNamespace)
    return ,$namespaces
}

function Assert-EzyExternalAttribute {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Expected,

        [string]$Namespace = ''
    )

    $actual = if ([string]::IsNullOrEmpty($Namespace)) {
        $Element.GetAttribute($Name)
    }
    else {
        $Element.GetAttribute($Name, $Namespace)
    }

    if ($actual -cne $Expected) {
        throw "$($Element.LocalName).$Name mismatch: expected '$Expected', actual '$actual'."
    }
}

function Assert-EzyExternalNodeCount {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [Xml.XmlNamespaceManager]$Namespaces,

        [Parameter(Mandatory)]
        [string]$XPath,

        [Parameter(Mandatory)]
        [int]$Expected
    )

    $actual = @($Document.SelectNodes($XPath, $Namespaces)).Count
    if ($actual -ne $Expected) {
        throw "Node count mismatch for '$XPath': expected $Expected, actual $actual."
    }
}

function Assert-EzyExternalExactAttributes {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Expected
    )

    $actual = @($Element.Attributes |
        Where-Object { $_.NamespaceURI -cne $script:XmlnsNamespace } |
        ForEach-Object { $_.NamespaceURI + '|' + $_.LocalName })
    $expectedCopy = @($Expected)
    [Array]::Sort($actual, [StringComparer]::Ordinal)
    [Array]::Sort($expectedCopy, [StringComparer]::Ordinal)
    if (($actual -join ',') -cne ($expectedCopy -join ',')) {
        throw "$($Element.LocalName) attribute allowlist mismatch: expected " +
            "'$($expectedCopy -join ',')', actual '$($actual -join ',')'."
    }
}

function Assert-EzyExternalExactElementChildren {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Expected
    )

    $actual = @($Element.ChildNodes |
        Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element } |
        ForEach-Object { $_.NamespaceURI + '|' + $_.LocalName })
    if ($actual.Count -ne $Expected.Count) {
        throw "$($Element.LocalName) child allowlist mismatch: expected " +
            "'$($Expected -join ',')', actual '$($actual -join ',')'."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -cne $Expected[$index]) {
            throw "$($Element.LocalName) child order mismatch at ${index}: expected " +
                "'$($Expected[$index])', actual '$($actual[$index])'."
        }
    }
}

function Assert-EzyExternalText {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Expected
    )

    if ($Element.InnerText -cne $Expected) {
        throw "$($Element.LocalName) text mismatch: expected '$Expected', actual " +
            "'$($Element.InnerText)'."
    }
}

function Initialize-EzyExternalPhysicalDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($fullPath)) {
        throw "External-location output path is a file: '$fullPath'."
    }
    [void][IO.Directory]::CreateDirectory($fullPath)
    $directory = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (-not $directory.PSIsContainer -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "External-location output must be a physical directory: '$fullPath'."
    }
    return $directory.FullName
}

function Assert-EzyExternalPackageManifest {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [Parameter(Mandatory)]
        [string]$MinVersion
    )

    Assert-EzyExternalFourPartVersion -Value $Version -Label 'Version'
    Assert-EzyExternalPublisher -Value $Publisher
    Assert-EzyExternalMinVersion -Value $MinVersion

    if ($Document.OuterXml.Contains('{{')) {
        throw 'External-location package manifest contains unresolved template tokens.'
    }

    $namespaces = New-EzyExternalNamespaceManager -Document $Document
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package' 1
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Identity' 1
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Dependencies/f:TargetDeviceFamily' 1
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Dependencies/f:PackageDependency' 0
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Properties/uap10:AllowExternalContent' 1
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Applications/f:Application' 1
    Assert-EzyExternalNodeCount $Document $namespaces `
        '/f:Package/f:Applications/f:Application/f:Extensions/uap:Extension/uap:Protocol' 1
    Assert-EzyExternalNodeCount $Document $namespaces '/f:Package/f:Capabilities/rescap:Capability' 2

    $package = [Xml.XmlElement]$Document.SelectSingleNode('/f:Package', $namespaces)
    $foundation = $script:ExternalManifestNamespace
    $uap = $script:ExternalUapNamespace
    $uap10 = $script:ExternalUap10Namespace
    $rescap = $script:ExternalRestrictedCapabilityNamespace
    Assert-EzyExternalExactElementChildren $package @(
        "$foundation|Identity",
        "$foundation|Properties",
        "$foundation|Resources",
        "$foundation|Dependencies",
        "$foundation|Capabilities",
        "$foundation|Applications")
    Assert-EzyExternalExactAttributes $package @('|IgnorableNamespaces')
    Assert-EzyExternalAttribute $package 'IgnorableNamespaces' 'uap uap10'

    $identity = [Xml.XmlElement]$Document.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    Assert-EzyExternalExactElementChildren $identity @()
    Assert-EzyExternalExactAttributes $identity @(
        '|Name', '|ProcessorArchitecture', '|Publisher', '|Version')
    Assert-EzyExternalAttribute $identity 'Name' 'GRTech.ezyImageViewer'
    Assert-EzyExternalAttribute $identity 'Publisher' $Publisher
    Assert-EzyExternalAttribute $identity 'Version' $Version
    Assert-EzyExternalAttribute $identity 'ProcessorArchitecture' 'neutral'

    $properties = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Properties', $namespaces)
    Assert-EzyExternalExactAttributes $properties @()
    Assert-EzyExternalExactElementChildren $properties @(
        "$foundation|DisplayName",
        "$foundation|PublisherDisplayName",
        "$foundation|Logo",
        "$uap10|AllowExternalContent")
    $displayName = [Xml.XmlElement]$properties.SelectSingleNode('f:DisplayName', $namespaces)
    $publisherDisplayName = [Xml.XmlElement]$properties.SelectSingleNode(
        'f:PublisherDisplayName', $namespaces)
    $logo = [Xml.XmlElement]$properties.SelectSingleNode('f:Logo', $namespaces)
    foreach ($textElement in @($displayName, $publisherDisplayName, $logo)) {
        Assert-EzyExternalExactAttributes $textElement @()
        Assert-EzyExternalExactElementChildren $textElement @()
    }
    Assert-EzyExternalText $displayName 'ezy Image Viewer'
    Assert-EzyExternalText $publisherDisplayName 'grtech-devpro'
    Assert-EzyExternalText $logo 'Assets\StoreLogo.png'

    $allowExternalContent = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Properties/uap10:AllowExternalContent', $namespaces)
    Assert-EzyExternalExactAttributes $allowExternalContent @()
    Assert-EzyExternalExactElementChildren $allowExternalContent @()
    Assert-EzyExternalText $allowExternalContent 'true'

    $resources = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Resources', $namespaces)
    Assert-EzyExternalExactAttributes $resources @()
    Assert-EzyExternalExactElementChildren $resources @("$foundation|Resource")
    $resource = [Xml.XmlElement]$resources.SelectSingleNode('f:Resource', $namespaces)
    Assert-EzyExternalExactAttributes $resource @('|Language')
    Assert-EzyExternalExactElementChildren $resource @()
    Assert-EzyExternalAttribute $resource 'Language' 'ko-KR'

    $dependencies = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Dependencies', $namespaces)
    Assert-EzyExternalExactAttributes $dependencies @()
    Assert-EzyExternalExactElementChildren $dependencies @("$foundation|TargetDeviceFamily")

    $target = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Dependencies/f:TargetDeviceFamily', $namespaces)
    Assert-EzyExternalExactAttributes $target @(
        '|MaxVersionTested', '|MinVersion', '|Name')
    Assert-EzyExternalExactElementChildren $target @()
    Assert-EzyExternalAttribute $target 'Name' 'Windows.Desktop'
    Assert-EzyExternalAttribute $target 'MinVersion' $MinVersion
    Assert-EzyExternalAttribute $target 'MaxVersionTested' '10.0.26100.0'

    $application = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Applications/f:Application', $namespaces)
    $applications = [Xml.XmlElement]$application.ParentNode
    Assert-EzyExternalExactAttributes $applications @()
    Assert-EzyExternalExactElementChildren $applications @("$foundation|Application")
    Assert-EzyExternalExactAttributes $application @(
        '|Executable', '|Id', "$uap10|RuntimeBehavior", "$uap10|TrustLevel")
    Assert-EzyExternalExactElementChildren $application @(
        "$uap|VisualElements", "$foundation|Extensions")
    Assert-EzyExternalAttribute $application 'Id' 'App'
    Assert-EzyExternalAttribute $application 'Executable' 'ezyImageViewer.exe'
    Assert-EzyExternalAttribute $application 'TrustLevel' 'mediumIL' $script:ExternalUap10Namespace
    Assert-EzyExternalAttribute $application 'RuntimeBehavior' 'win32App' $script:ExternalUap10Namespace

    $visualElements = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/uap:VisualElements', $namespaces)
    Assert-EzyExternalExactAttributes $visualElements @(
        '|AppListEntry', '|BackgroundColor', '|Description', '|DisplayName',
        '|Square150x150Logo', '|Square44x44Logo')
    Assert-EzyExternalExactElementChildren $visualElements @()
    Assert-EzyExternalAttribute $visualElements 'DisplayName' 'ezy Image Viewer'
    Assert-EzyExternalAttribute $visualElements 'Description' 'ezy Image Viewer'
    Assert-EzyExternalAttribute $visualElements 'BackgroundColor' 'transparent'
    Assert-EzyExternalAttribute $visualElements 'Square150x150Logo' `
        'Assets\Square150x150Logo.png'
    Assert-EzyExternalAttribute $visualElements 'Square44x44Logo' `
        'Assets\Square44x44Logo.png'
    Assert-EzyExternalAttribute $visualElements 'AppListEntry' 'none'

    $extensions = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions', $namespaces)
    Assert-EzyExternalExactAttributes $extensions @()
    Assert-EzyExternalExactElementChildren $extensions @("$uap|Extension")
    $extension = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions/uap:Extension', $namespaces)
    Assert-EzyExternalExactAttributes $extension @('|Category')
    Assert-EzyExternalExactElementChildren $extension @("$uap|Protocol")
    Assert-EzyExternalAttribute $extension 'Category' 'windows.protocol'
    $protocol = [Xml.XmlElement]$extension.SelectSingleNode('uap:Protocol', $namespaces)
    Assert-EzyExternalExactAttributes $protocol @('|DesiredView', '|Name')
    Assert-EzyExternalExactElementChildren $protocol @("$uap|DisplayName")
    Assert-EzyExternalAttribute $protocol 'Name' 'ezyimageviewer'
    Assert-EzyExternalAttribute $protocol 'DesiredView' 'default'
    $protocolDisplayName = [Xml.XmlElement]$protocol.SelectSingleNode(
        'uap:DisplayName', $namespaces)
    Assert-EzyExternalExactAttributes $protocolDisplayName @()
    Assert-EzyExternalExactElementChildren $protocolDisplayName @()
    Assert-EzyExternalText $protocolDisplayName 'ezy Image Viewer'

    $capabilities = [Xml.XmlElement]$Document.SelectSingleNode(
        '/f:Package/f:Capabilities', $namespaces)
    Assert-EzyExternalExactAttributes $capabilities @()
    Assert-EzyExternalExactElementChildren $capabilities @(
        "$rescap|Capability", "$rescap|Capability")
    $capabilityNodes = @($capabilities.SelectNodes('rescap:Capability', $namespaces))
    foreach ($capability in $capabilityNodes) {
        Assert-EzyExternalExactAttributes ([Xml.XmlElement]$capability) @('|Name')
        Assert-EzyExternalExactElementChildren ([Xml.XmlElement]$capability) @()
    }
    $capabilityNames = @($capabilityNodes |
        ForEach-Object { $_.GetAttribute('Name') } | Sort-Object)
    if (($capabilityNames -join ',') -cne 'runFullTrust,unvirtualizedResources') {
        throw "Capability allowlist mismatch: '$($capabilityNames -join ',')'."
    }
}

function Assert-EzyExternalApplicationManifest {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [switch]$Embedded
    )

    Assert-EzyExternalPublisher -Value $Publisher
    if ($Document.OuterXml.Contains('{{')) {
        throw 'Application manifest contains unresolved template tokens.'
    }

    $namespaces = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespaces.AddNamespace('asm', $script:AssemblyManifestNamespace)
    $namespaces.AddNamespace('msix', $script:MsixManifestNamespace)
    $namespaces.AddNamespace('compat', $script:CompatibilityManifestNamespace)
    $namespaces.AddNamespace('asmv3', $script:AssemblyV3ManifestNamespace)
    $namespaces.AddNamespace('dpi', $script:DpiAwarenessManifestNamespace)

    $assembly = [Xml.XmlElement]$Document.SelectSingleNode('/asm:assembly', $namespaces)
    if ($null -eq $assembly) {
        throw 'Application manifest must contain exactly one assembly root.'
    }
    Assert-EzyExternalExactAttributes $assembly @('|manifestVersion')
    Assert-EzyExternalAttribute $assembly 'manifestVersion' '1.0'
    if ($Embedded) {
        $allowedEmbeddedChildren = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($name in @(
                "$($script:AssemblyManifestNamespace)|assemblyIdentity",
                "$($script:AssemblyV3ManifestNamespace)|file",
                "$($script:MsixManifestNamespace)|msix",
                "$($script:CompatibilityManifestNamespace)|compatibility",
                "$($script:AssemblyV3ManifestNamespace)|application")) {
            [void]$allowedEmbeddedChildren.Add($name)
        }
        foreach ($child in @($assembly.ChildNodes |
                Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element })) {
            $qualifiedName = $child.NamespaceURI + '|' + $child.LocalName
            if (-not $allowedEmbeddedChildren.Contains($qualifiedName)) {
                throw "Embedded application manifest contains an unexpected root child " +
                    "'$qualifiedName'."
            }
        }
        foreach ($xpath in @(
                '/asm:assembly/asm:assemblyIdentity',
                '/asm:assembly/msix:msix',
                '/asm:assembly/compat:compatibility',
                '/asm:assembly/asmv3:application')) {
            if (@($Document.SelectNodes($xpath, $namespaces)).Count -ne 1) {
                throw "Embedded application manifest must contain exactly one '$xpath'."
            }
        }
    }
    else {
        Assert-EzyExternalExactElementChildren $assembly @(
            "$($script:AssemblyManifestNamespace)|assemblyIdentity",
            "$($script:MsixManifestNamespace)|msix",
            "$($script:CompatibilityManifestNamespace)|compatibility",
            "$($script:AssemblyV3ManifestNamespace)|application")
    }

    $assemblyIdentity = [Xml.XmlElement]$assembly.SelectSingleNode(
        'asm:assemblyIdentity', $namespaces)
    Assert-EzyExternalExactAttributes $assemblyIdentity @('|name', '|version')
    Assert-EzyExternalExactElementChildren $assemblyIdentity @()
    Assert-EzyExternalAttribute $assemblyIdentity 'version' '1.0.0.0'
    Assert-EzyExternalAttribute $assemblyIdentity 'name' 'EzyImageViewer.App'

    $nodes = @($Document.SelectNodes('/asm:assembly/msix:msix', $namespaces))
    if ($nodes.Count -ne 1) {
        throw "Application manifest must contain exactly one msix identity node; actual $($nodes.Count)."
    }

    $identity = [Xml.XmlElement]$nodes[0]
    Assert-EzyExternalExactAttributes $identity @(
        '|applicationId', '|packageName', '|publisher')
    Assert-EzyExternalExactElementChildren $identity @()
    Assert-EzyExternalAttribute $identity 'publisher' $Publisher
    Assert-EzyExternalAttribute $identity 'packageName' 'GRTech.ezyImageViewer'
    Assert-EzyExternalAttribute $identity 'applicationId' 'App'

    $compatibility = [Xml.XmlElement]$assembly.SelectSingleNode(
        'compat:compatibility', $namespaces)
    Assert-EzyExternalExactAttributes $compatibility @()
    Assert-EzyExternalExactElementChildren $compatibility @(
        "$($script:CompatibilityManifestNamespace)|application")
    $compatibilityApplication = [Xml.XmlElement]$compatibility.SelectSingleNode(
        'compat:application', $namespaces)
    Assert-EzyExternalExactAttributes $compatibilityApplication @()
    Assert-EzyExternalExactElementChildren $compatibilityApplication @(
        "$($script:CompatibilityManifestNamespace)|supportedOS")
    $supportedOs = [Xml.XmlElement]$compatibilityApplication.SelectSingleNode(
        'compat:supportedOS', $namespaces)
    Assert-EzyExternalExactAttributes $supportedOs @('|Id')
    Assert-EzyExternalExactElementChildren $supportedOs @()
    Assert-EzyExternalAttribute $supportedOs 'Id' `
        '{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}'

    $application = [Xml.XmlElement]$assembly.SelectSingleNode(
        'asmv3:application', $namespaces)
    Assert-EzyExternalExactAttributes $application @()
    Assert-EzyExternalExactElementChildren $application @(
        "$($script:AssemblyV3ManifestNamespace)|windowsSettings")
    $windowsSettings = [Xml.XmlElement]$application.SelectSingleNode(
        'asmv3:windowsSettings', $namespaces)
    Assert-EzyExternalExactAttributes $windowsSettings @()
    Assert-EzyExternalExactElementChildren $windowsSettings @(
        "$($script:DpiAwarenessManifestNamespace)|dpiAwareness")
    $dpiAwareness = [Xml.XmlElement]$windowsSettings.SelectSingleNode(
        'dpi:dpiAwareness', $namespaces)
    Assert-EzyExternalExactAttributes $dpiAwareness @()
    Assert-EzyExternalExactElementChildren $dpiAwareness @()
    Assert-EzyExternalText $dpiAwareness 'PerMonitorV2'
}

function Set-EzyExternalXmlAttribute {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Value
    )

    $Element.SetAttribute($Name, $Value)
}

function Save-EzyExternalXml {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = Initialize-EzyExternalPhysicalDirectory `
        ([IO.Path]::GetDirectoryName($fullPath))
    $temporaryPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($fullPath) +
        '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($fullPath) +
        '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $replaceCompleted = $false

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = "`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $settings.OmitXmlDeclaration = $false

    try {
        $writer = [Xml.XmlWriter]::Create($temporaryPath, $settings)
        try {
            $Document.Save($writer)
        }
        finally {
            $writer.Dispose()
        }

        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporaryPath, $fullPath, $backupPath)
            $replaceCompleted = $true
            [IO.File]::Delete($backupPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        if ($replaceCompleted -and [IO.File]::Exists($backupPath)) {
            [IO.File]::Delete($backupPath)
        }
    }
}

function New-EzyExternalLocationManifests {
    param(
        [Parameter(Mandatory)]
        [string]$PackageTemplatePath,

        [Parameter(Mandatory)]
        [string]$ApplicationTemplatePath,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [Parameter(Mandatory)]
        [string]$MinVersion
    )

    Assert-EzyExternalFourPartVersion -Value $Version -Label 'Version'
    Assert-EzyExternalPublisher -Value $Publisher
    Assert-EzyExternalMinVersion -Value $MinVersion

    $package = Read-EzyExternalXml -Path $PackageTemplatePath
    $namespaces = New-EzyExternalNamespaceManager -Document $package
    $identity = [Xml.XmlElement]$package.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    $target = [Xml.XmlElement]$package.SelectSingleNode(
        '/f:Package/f:Dependencies/f:TargetDeviceFamily', $namespaces)
    if ($null -eq $identity -or $null -eq $target) {
        throw 'External-location package template is missing required identity nodes.'
    }

    Set-EzyExternalXmlAttribute $identity 'Publisher' $Publisher
    Set-EzyExternalXmlAttribute $identity 'Version' $Version
    Set-EzyExternalXmlAttribute $target 'MinVersion' $MinVersion
    Assert-EzyExternalPackageManifest $package $Version $Publisher $MinVersion

    $application = Read-EzyExternalXml -Path $ApplicationTemplatePath
    $applicationNamespaces = [Xml.XmlNamespaceManager]::new($application.NameTable)
    $applicationNamespaces.AddNamespace('asm', $script:AssemblyManifestNamespace)
    $applicationNamespaces.AddNamespace('msix', $script:MsixManifestNamespace)
    $applicationIdentity = [Xml.XmlElement]$application.SelectSingleNode(
        '/asm:assembly/msix:msix', $applicationNamespaces)
    if ($null -eq $applicationIdentity) {
        throw 'Application manifest template is missing the msix identity node.'
    }

    Set-EzyExternalXmlAttribute $applicationIdentity 'publisher' $Publisher
    Assert-EzyExternalApplicationManifest $application $Publisher

    $outputRoot = Initialize-EzyExternalPhysicalDirectory $OutputDirectory
    $packageRoot = Initialize-EzyExternalPhysicalDirectory (Join-Path $outputRoot 'package')
    $applicationRoot = Initialize-EzyExternalPhysicalDirectory `
        (Join-Path $outputRoot 'application')
    $packagePath = Join-Path $packageRoot 'AppxManifest.xml'
    $applicationPath = Join-Path $applicationRoot 'ezyImageViewer.exe.manifest'
    Save-EzyExternalXml $package $packagePath
    Save-EzyExternalXml $application $applicationPath

    [PSCustomObject]@{
        PackageManifestPath = $packagePath
        ApplicationManifestPath = $applicationPath
    }
}
