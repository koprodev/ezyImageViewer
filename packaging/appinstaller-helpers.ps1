# 결정적 App Installer 생성·검증용 공용 닫힘 우선 도우미.

Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:EzyAppInstallerNamespace = 'http://schemas.microsoft.com/appx/appinstaller/2017/2'
$script:EzyPackageManifestNamespace = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$script:EzyXmlNamespaceNamespace = 'http://www.w3.org/2000/xmlns/'
$script:EzyMaximumXmlBytes = 1MB
$script:EzyMaximumUriLength = 2084

function Assert-EzyAppInstallerFourPartVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Label,

        [switch]$RequirePositiveMajor
    )

    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
        throw "$Label must be a canonical four-part numeric version: '$Value'."
    }

    $parts = @($Value.Split('.') | ForEach-Object {
        [uint32]::Parse($_, [Globalization.CultureInfo]::InvariantCulture)
    })
    foreach ($part in $parts) {
        if ($part -gt 65535) {
            throw "$Label contains a component outside the MSIX range 0..65535: '$Value'."
        }
    }
    if ($RequirePositiveMajor -and $parts[0] -eq 0) {
        throw "$Label major version must be greater than zero for the 2017/2 App Installer schema: '$Value'."
    }
}

function Get-EzyAppInstallerPhysicalFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "$Label must be a file: '$Path'."
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: '$Path'."
    }
    return $item
}

function Get-EzyAppInstallerPhysicalDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "$Label must be a directory: '$Path'."
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: '$Path'."
    }
    return $item
}

function Assert-EzyAsciiText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label must not be empty."
    }
    if ($Value.Contains('{{') -or $Value.Contains('}}')) {
        throw "$Label contains an unresolved placeholder."
    }
    foreach ($character in $Value.ToCharArray()) {
        if ([int]$character -gt 127) {
            throw "$Label must contain ASCII characters only."
        }
    }
}

function Read-EzySecureXmlBytes {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [Parameter(Mandatory = $true)]
        [string]$Label,

        [switch]$RequireAsciiWithoutBom
    )

    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt $script:EzyMaximumXmlBytes) {
        throw "$Label size must be between 1 and $script:EzyMaximumXmlBytes bytes."
    }
    if ($RequireAsciiWithoutBom) {
        if ($Bytes.Length -ge 3 -and
            $Bytes[0] -eq 0xEF -and
            $Bytes[1] -eq 0xBB -and
            $Bytes[2] -eq 0xBF) {
            throw "$Label must use UTF-8 without a byte-order mark."
        }
        foreach ($value in $Bytes) {
            if ($value -gt 127) {
                throw "$Label must contain ASCII bytes only."
            }
            if ($value -eq 0x26) {
                throw "$Label must not contain XML entity or character references."
            }
        }
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $script:EzyMaximumXmlBytes
    $settings.MaxCharactersFromEntities = 0
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    catch {
        throw "$Label is not safe, well-formed XML: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $stream.Dispose()
    }
}

function Get-EzyMsixManifestDocument {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileInfo]$Package,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not [string]::Equals($Package.Extension, '.msix', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must have the .msix extension: '$($Package.FullName)'."
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($Package.FullName)
    try {
        $entries = @($archive.Entries | Where-Object {
            [string]::Equals(
                $_.FullName,
                'AppxManifest.xml',
                [StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1 -or
            -not [string]::Equals(
                $entries[0].FullName,
                'AppxManifest.xml',
                [StringComparison]::Ordinal)) {
            throw "$Label must contain exactly one canonical AppxManifest.xml entry."
        }
        if ($entries[0].Length -le 0 -or
            $entries[0].Length -gt $script:EzyMaximumXmlBytes) {
            throw "$Label AppxManifest.xml has an invalid uncompressed size."
        }

        $entryStream = $entries[0].Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(81920)
            while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $memory.Write($buffer, 0, $read)
                if ($memory.Length -gt $script:EzyMaximumXmlBytes) {
                    throw "$Label AppxManifest.xml exceeds the size limit while reading."
                }
            }
            return Read-EzySecureXmlBytes -Bytes $memory.ToArray() -Label "$Label AppxManifest.xml"
        }
        finally {
            $memory.Dispose()
            $entryStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-EzyRequiredAttribute {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not $Element.HasAttribute($Name)) {
        throw "$Label is missing required attribute '$Name'."
    }
    $value = $Element.GetAttribute($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Label attribute '$Name' must not be empty."
    }
    return $value
}

function Get-EzyPackageContract {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileInfo]$Package,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $manifest = Get-EzyMsixManifestDocument -Package $Package -Label $Label
    $root = $manifest.DocumentElement
    if ($null -eq $root -or
        -not [string]::Equals($root.LocalName, 'Package', [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $root.NamespaceURI,
            $script:EzyPackageManifestNamespace,
            [StringComparison]::Ordinal)) {
        throw "$Label has an unexpected package manifest root."
    }

    $identities = @($root.ChildNodes | Where-Object {
        $_.NodeType -eq [Xml.XmlNodeType]::Element -and
        $_.LocalName -eq 'Identity' -and
        $_.NamespaceURI -eq $script:EzyPackageManifestNamespace
    })
    if ($identities.Count -ne 1) {
        throw "$Label must contain exactly one Identity element."
    }
    $identity = [Xml.XmlElement]$identities[0]
    $name = Get-EzyRequiredAttribute -Element $identity -Name 'Name' -Label "$Label Identity"
    $publisher = Get-EzyRequiredAttribute -Element $identity -Name 'Publisher' -Label "$Label Identity"
    $version = Get-EzyRequiredAttribute -Element $identity -Name 'Version' -Label "$Label Identity"
    $architecture = Get-EzyRequiredAttribute -Element $identity `
        -Name 'ProcessorArchitecture' -Label "$Label Identity"

    Assert-EzyAsciiText -Value $name -Label "$Label identity name"
    Assert-EzyAsciiText -Value $publisher -Label "$Label publisher"
    Assert-EzyAppInstallerFourPartVersion -Value $version -Label "$Label version" -RequirePositiveMajor
    if (-not [string]::Equals($architecture, 'x64', [StringComparison]::Ordinal)) {
        throw "$Label architecture must be x64: '$architecture'."
    }
    try {
        [void][Security.Cryptography.X509Certificates.X500DistinguishedName]::new($publisher)
    }
    catch {
        throw "$Label publisher is not a valid X.500 distinguished name: '$publisher'."
    }

    return [pscustomobject]@{
        File = $Package
        Manifest = $manifest
        Name = $name
        Publisher = $publisher
        Version = $version
        Architecture = $architecture
    }
}

function Get-EzyReleasePackageContract {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MainPackage
    )

    $mainFile = Get-EzyAppInstallerPhysicalFile -Path $MainPackage -Label 'MainPackage'
    $main = Get-EzyPackageContract -Package $mainFile -Label 'MainPackage'
    if (-not [string]::Equals(
            $main.Name,
            'GRTech.ezyImageViewer',
            [StringComparison]::Ordinal)) {
        throw "Unexpected main package identity '$($main.Name)'."
    }

    $mainDependencies = @($main.Manifest.SelectNodes(
        "/*[local-name()='Package' and namespace-uri()='$script:EzyPackageManifestNamespace']" +
        "/*[local-name()='Dependencies' and namespace-uri()='$script:EzyPackageManifestNamespace']" +
        "/*[local-name()='PackageDependency' and namespace-uri()='$script:EzyPackageManifestNamespace']"))
    if ($mainDependencies.Count -ne 0) {
        throw "MainPackage must not declare a PackageDependency; found $($mainDependencies.Count)."
    }

    return [pscustomobject]@{
        Main = $main
    }
}

function Resolve-EzyAppInstallerHttpsUri {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedFileName,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    Assert-EzyAsciiText -Value $Value -Label $Label
    if ($Value.Length -gt $script:EzyMaximumUriLength) {
        throw "$Label exceeds the $script:EzyMaximumUriLength-character limit."
    }

    [Uri]$uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        -not [string]::Equals($uri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace($uri.Host)) {
        throw "$Label must be an absolute HTTPS URI."
    }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "$Label must not contain userinfo, a query, or a fragment."
    }

    $rawSegments = $uri.AbsolutePath.Split('/')
    if ($rawSegments.Length -lt 2 -or $rawSegments[0].Length -ne 0) {
        throw "$Label path must be absolute and hierarchical."
    }
    for ($index = 1; $index -lt $rawSegments.Length; $index++) {
        $rawSegment = $rawSegments[$index]
        if ([string]::IsNullOrEmpty($rawSegment)) {
            throw "$Label path must not contain empty segments."
        }
        if ($rawSegment -match '(?i)%25') {
            throw "$Label path must not contain nested percent encoding."
        }
        try {
            $decodedSegment = [Uri]::UnescapeDataString($rawSegment)
        }
        catch {
            throw "$Label contains an invalid escaped path segment."
        }
        if ($decodedSegment.Contains('/') -or
            $decodedSegment.Contains('\') -or
            $decodedSegment -match '(?i)%2f|%5c' -or
            $decodedSegment -in @('.', '..')) {
            throw "$Label path contains an ambiguous encoded separator or traversal segment."
        }
    }
    $fileName = $rawSegments[$rawSegments.Length - 1]
    if (-not [string]::Equals($fileName, $ExpectedFileName, [StringComparison]::Ordinal)) {
        throw "$Label path must end with '$ExpectedFileName'."
    }

    $canonical = $uri.AbsoluteUri
    Assert-EzyAsciiText -Value $canonical -Label "$Label canonical value"
    if ($canonical.Length -gt $script:EzyMaximumUriLength) {
        throw "$Label canonical value exceeds the $script:EzyMaximumUriLength-character limit."
    }
    return $canonical
}

function Assert-EzyExactAttributes {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Names,

        [Parameter(Mandatory = $true)]
        [string]$Label,

        [switch]$RequireDefaultNamespaceDeclaration
    )

    $seen = @{}
    $namespaceDeclarations = @($Element.Attributes | Where-Object {
        $_.NamespaceURI -eq $script:EzyXmlNamespaceNamespace
    })
    if ($RequireDefaultNamespaceDeclaration) {
        if ($namespaceDeclarations.Count -ne 1 -or
            -not [string]::Equals(
                $namespaceDeclarations[0].Name,
                'xmlns',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $namespaceDeclarations[0].Value,
                $script:EzyAppInstallerNamespace,
                [StringComparison]::Ordinal)) {
            throw "$Label must declare only the expected default namespace."
        }
    }
    elseif ($namespaceDeclarations.Count -ne 0) {
        throw "$Label must not redeclare namespaces."
    }

    foreach ($attribute in $Element.Attributes) {
        if ($attribute.NamespaceURI -eq $script:EzyXmlNamespaceNamespace) {
            continue
        }
        if (-not [string]::IsNullOrEmpty($attribute.NamespaceURI) -or
            -not ($Names -ccontains $attribute.LocalName) -or
            $seen.ContainsKey($attribute.LocalName)) {
            throw "$Label contains unexpected attribute '$($attribute.Name)'."
        }
        $seen[$attribute.LocalName] = $true
    }
    if ($seen.Count -ne $Names.Count) {
        throw "$Label attribute count mismatch: expected $($Names.Count), actual $($seen.Count)."
    }
    foreach ($name in $Names) {
        if (-not $seen.ContainsKey($name)) {
            throw "$Label is missing required attribute '$name'."
        }
    }
}

function Get-EzyExactElementChildren {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $elements = New-Object 'Collections.Generic.List[Xml.XmlElement]'
    foreach ($child in $Element.ChildNodes) {
        if ($child.NodeType -eq [Xml.XmlNodeType]::Element) {
            [void]$elements.Add([Xml.XmlElement]$child)
            continue
        }
        if (($child.NodeType -eq [Xml.XmlNodeType]::Whitespace -or
                $child.NodeType -eq [Xml.XmlNodeType]::SignificantWhitespace -or
                $child.NodeType -eq [Xml.XmlNodeType]::Text) -and
            [string]::IsNullOrWhiteSpace($child.Value)) {
            continue
        }
        throw "$Label contains unsupported node type '$($child.NodeType)'."
    }
    return $elements.ToArray()
}

function Assert-EzyElementName {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not [string]::Equals($Element.LocalName, $Name, [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $Element.NamespaceURI,
            $script:EzyAppInstallerNamespace,
            [StringComparison]::Ordinal) -or
        -not [string]::IsNullOrEmpty($Element.Prefix)) {
        throw "$Label must be an unprefixed '$Name' element in the 2017/2 namespace."
    }
}

function Assert-EzyAttributeValue {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $actual = $Element.GetAttribute($Name)
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "$Label $Name mismatch: expected '$Expected', actual '$actual'."
    }
}

function Assert-EzyAppInstallerDocument {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlDocument]$Document,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$Pair,

        [Parameter(Mandatory = $true)]
        [string]$AppInstallerUri,

        [Parameter(Mandatory = $true)]
        [string]$MainPackageUri,

        [Parameter(Mandatory = $true)]
        [ValidateSet('None', 'OnLaunch')]
        [string]$ExpectedUpdateMode,

        [ValidateRange(0, 255)]
        [int]$HoursBetweenUpdateChecks = 24
    )

    if ($null -ne $Document.DocumentType) {
        throw 'AppInstaller must not contain a document type declaration.'
    }
    $root = $Document.DocumentElement
    if ($null -eq $root) {
        throw 'AppInstaller has no document element.'
    }
    Assert-EzyElementName -Element $root -Name 'AppInstaller' -Label 'AppInstaller root'
    Assert-EzyExactAttributes -Element $root -Names @('Version', 'Uri') `
        -Label 'AppInstaller root' -RequireDefaultNamespaceDeclaration
    Assert-EzyAttributeValue -Element $root -Name 'Version' `
        -Expected $Pair.Main.Version -Label 'AppInstaller root'
    Assert-EzyAttributeValue -Element $root -Name 'Uri' `
        -Expected $AppInstallerUri -Label 'AppInstaller root'
    Assert-EzyAppInstallerFourPartVersion -Value $root.GetAttribute('Version') `
        -Label 'AppInstaller root version' -RequirePositiveMajor

    $children = @(Get-EzyExactElementChildren -Element $root -Label 'AppInstaller root')
    $expectedChildCount = if ($ExpectedUpdateMode -eq 'OnLaunch') { 2 } else { 1 }
    if ($children.Count -ne $expectedChildCount) {
        throw "AppInstaller child count mismatch: expected $expectedChildCount, actual $($children.Count)."
    }

    $main = $children[0]
    Assert-EzyElementName -Element $main -Name 'MainPackage' -Label 'MainPackage'
    Assert-EzyExactAttributes -Element $main `
        -Names @('Name', 'Publisher', 'Version', 'ProcessorArchitecture', 'Uri') `
        -Label 'MainPackage'
    Assert-EzyAttributeValue $main 'Name' $Pair.Main.Name 'MainPackage'
    Assert-EzyAttributeValue $main 'Publisher' $Pair.Main.Publisher 'MainPackage'
    Assert-EzyAttributeValue $main 'Version' $Pair.Main.Version 'MainPackage'
    Assert-EzyAttributeValue $main 'ProcessorArchitecture' $Pair.Main.Architecture 'MainPackage'
    Assert-EzyAttributeValue $main 'Uri' $MainPackageUri 'MainPackage'
    if (@(Get-EzyExactElementChildren -Element $main -Label 'MainPackage').Count -ne 0) {
        throw 'MainPackage must be empty.'
    }

    if ($ExpectedUpdateMode -eq 'OnLaunch') {
        $updateSettings = $children[1]
        Assert-EzyElementName -Element $updateSettings `
            -Name 'UpdateSettings' -Label 'UpdateSettings'
        Assert-EzyExactAttributes -Element $updateSettings -Names @() -Label 'UpdateSettings'
        $updateChildren = @(Get-EzyExactElementChildren `
            -Element $updateSettings -Label 'UpdateSettings')
        if ($updateChildren.Count -ne 1) {
            throw "UpdateSettings must contain exactly one OnLaunch; found $($updateChildren.Count)."
        }
        $onLaunch = $updateChildren[0]
        Assert-EzyElementName -Element $onLaunch -Name 'OnLaunch' -Label 'OnLaunch'
        Assert-EzyExactAttributes -Element $onLaunch `
            -Names @('HoursBetweenUpdateChecks') -Label 'OnLaunch'
        $expectedHours = $HoursBetweenUpdateChecks.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
        Assert-EzyAttributeValue $onLaunch 'HoursBetweenUpdateChecks' `
            $expectedHours 'OnLaunch'
        if (@(Get-EzyExactElementChildren -Element $onLaunch -Label 'OnLaunch').Count -ne 0) {
            throw 'OnLaunch must be empty.'
        }
    }
}

function Write-EzyAppInstallerFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
            [IO.Path]::GetExtension($fullPath),
            '.appinstaller',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputPath must have the .appinstaller extension: '$Path'."
    }
    $parent = Get-EzyAppInstallerPhysicalDirectory `
        -Path (Split-Path $fullPath -Parent) -Label 'OutputPath parent'
    if (Test-Path -LiteralPath $fullPath) {
        $existing = Get-EzyAppInstallerPhysicalFile -Path $fullPath -Label 'OutputPath'
        if (-not [string]::Equals(
                $existing.FullName,
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "OutputPath resolution changed unexpectedly: '$Path'."
        }
    }

    $temporary = Join-Path $parent.FullName (
        '.' + [IO.Path]::GetFileName($fullPath) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent.FullName (
        '.' + [IO.Path]::GetFileName($fullPath) + '.bak-' + [Guid]::NewGuid().ToString('N'))
    try {
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $stream.Write($Bytes, 0, $Bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if (Test-Path -LiteralPath $fullPath) {
            [IO.File]::Replace($temporary, $fullPath, $backup, $true)
        }
        else {
            [IO.File]::Move($temporary, $fullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
    return $fullPath
}
