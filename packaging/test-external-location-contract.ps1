[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'external-location-helpers.ps1')

$script:PassCount = 0

function Assert-Contract {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }

    $script:PassCount++
}

function Assert-ContractThrows {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        $script:PassCount++
        return
    }

    throw "Expected contract rejection: $Label."
}

function Get-ContractHash {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hash)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$packageTemplate = Join-Path $scriptRoot 'ExternalLocation.AppxManifest.template.xml'
$applicationTemplate = Join-Path $scriptRoot 'ExternalLocation.App.manifest.template.xml'
$publisher = 'CN=ezyImageViewer Dev'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ('ezy-external-contract-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempRoot)

try {
    foreach ($minVersion in @('10.0.19041.0', '10.0.26100.0')) {
        $output = Join-Path $tempRoot $minVersion
        $result = New-EzyExternalLocationManifests `
            -PackageTemplatePath $packageTemplate `
            -ApplicationTemplatePath $applicationTemplate `
            -OutputDirectory $output `
            -Version '1.2.3.4' `
            -Publisher $publisher `
            -MinVersion $minVersion

        Assert-Contract ([IO.File]::Exists($result.PackageManifestPath)) `
            "Package manifest was not generated for $minVersion."
        Assert-Contract ([IO.File]::Exists($result.ApplicationManifestPath)) `
            "Application manifest was not generated for $minVersion."

        $package = Read-EzyExternalXml $result.PackageManifestPath
        Assert-EzyExternalPackageManifest $package '1.2.3.4' $publisher $minVersion
        $application = Read-EzyExternalXml $result.ApplicationManifestPath
        Assert-EzyExternalApplicationManifest $application $publisher
        $script:PassCount += 2
    }

    $first = New-EzyExternalLocationManifests `
        -PackageTemplatePath $packageTemplate `
        -ApplicationTemplatePath $applicationTemplate `
        -OutputDirectory (Join-Path $tempRoot 'deterministic-a') `
        -Version '2.0.0.0' `
        -Publisher $publisher `
        -MinVersion '10.0.19041.0'
    $second = New-EzyExternalLocationManifests `
        -PackageTemplatePath $packageTemplate `
        -ApplicationTemplatePath $applicationTemplate `
        -OutputDirectory (Join-Path $tempRoot 'deterministic-b') `
        -Version '2.0.0.0' `
        -Publisher $publisher `
        -MinVersion '10.0.19041.0'

    Assert-Contract `
        ((Get-ContractHash $first.PackageManifestPath) -ceq
            (Get-ContractHash $second.PackageManifestPath)) `
        'Package manifest generation is not deterministic.'
    Assert-Contract `
        ((Get-ContractHash $first.ApplicationManifestPath) -ceq
            (Get-ContractHash $second.ApplicationManifestPath)) `
        'Application manifest generation is not deterministic.'

    $overwriteRoot = Join-Path $tempRoot 'overwrite'
    [void](New-EzyExternalLocationManifests `
        -PackageTemplatePath $packageTemplate `
        -ApplicationTemplatePath $applicationTemplate `
        -OutputDirectory $overwriteRoot `
        -Version '2.0.0.0' `
        -Publisher $publisher `
        -MinVersion '10.0.19041.0')
    $overwrite = New-EzyExternalLocationManifests `
        -PackageTemplatePath $packageTemplate `
        -ApplicationTemplatePath $applicationTemplate `
        -OutputDirectory $overwriteRoot `
        -Version '2.0.0.1' `
        -Publisher $publisher `
        -MinVersion '10.0.19041.0'
    $overwrittenPackage = Read-EzyExternalXml $overwrite.PackageManifestPath
    Assert-EzyExternalPackageManifest $overwrittenPackage '2.0.0.1' `
        $publisher '10.0.19041.0'
    $script:PassCount++
    Assert-Contract `
        (@(Get-ChildItem -LiteralPath $overwriteRoot -Recurse -Force -File |
            Where-Object { $_.Name.EndsWith('.tmp', [StringComparison]::Ordinal) }).Count -eq 0) `
        'Atomic overwrite left a temporary file behind.'

    Assert-ContractThrows 'three-part version' {
        Assert-EzyExternalFourPartVersion '1.2.3' 'Version'
    }
    Assert-ContractThrows 'leading-zero version component' {
        Assert-EzyExternalFourPartVersion '01.2.3.4' 'Version'
    }
    Assert-ContractThrows 'out-of-range version component' {
        Assert-EzyExternalFourPartVersion '65536.0.0.0' 'Version'
    }
    Assert-ContractThrows 'unresolved publisher' {
        Assert-EzyExternalPublisher '{{PUBLISHER}}'
    }
    Assert-ContractThrows 'unsupported minimum OS version' {
        Assert-EzyExternalMinVersion '10.0.22000.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $namespaces = New-EzyExternalNamespaceManager $tamperedPackage
    $tamperedPackage.SelectSingleNode(
        '/f:Package/f:Properties/uap10:AllowExternalContent', $namespaces).InnerText = 'false'
    Assert-ContractThrows 'external content disabled' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $namespaces = New-EzyExternalNamespaceManager $tamperedPackage
    $tamperedProtocol = [Xml.XmlElement]$tamperedPackage.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions/uap:Extension/uap:Protocol',
        $namespaces)
    $tamperedProtocol.SetAttribute('Name', 'wrong-scheme')
    Assert-ContractThrows 'protocol mismatch' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $tamperedPackage.DocumentElement.SetAttribute(
        'IgnorableNamespaces', 'uap uap10 rescap')
    Assert-ContractThrows 'restricted capability namespace made ignorable' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $namespaces = New-EzyExternalNamespaceManager $tamperedPackage
    $tamperedApp = [Xml.XmlElement]$tamperedPackage.SelectSingleNode(
        '/f:Package/f:Applications/f:Application', $namespaces)
    $tamperedApp.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
    Assert-ContractThrows 'unexpected application attribute' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $namespaces = New-EzyExternalNamespaceManager $tamperedPackage
    $capabilities = $tamperedPackage.SelectSingleNode(
        '/f:Package/f:Capabilities', $namespaces)
    $extraCapability = $tamperedPackage.CreateElement(
        'rescap', 'Capability',
        'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $extraCapability.SetAttribute('Name', 'broadFileSystemAccess')
    [void]$capabilities.AppendChild($extraCapability)
    Assert-ContractThrows 'unexpected capability' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $tamperedPackage.DocumentElement.SetAttribute('Unexpected', 'value')
    Assert-ContractThrows 'unexpected package root attribute' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedPackage = Read-EzyExternalXml $first.PackageManifestPath
    $namespaces = New-EzyExternalNamespaceManager $tamperedPackage
    $extensions = $tamperedPackage.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions', $namespaces)
    $extension = $tamperedPackage.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions/uap:Extension',
        $namespaces)
    [void]$extensions.AppendChild($extension.CloneNode($true))
    Assert-ContractThrows 'duplicate protocol extension' {
        Assert-EzyExternalPackageManifest $tamperedPackage '2.0.0.0' `
            $publisher '10.0.19041.0'
    }

    $tamperedApplication = Read-EzyExternalXml $first.ApplicationManifestPath
    $applicationNamespaces = [Xml.XmlNamespaceManager]::new($tamperedApplication.NameTable)
    $applicationNamespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $applicationNamespaces.AddNamespace('msix', 'urn:schemas-microsoft-com:msix.v1')
    $tamperedApplication.SelectSingleNode(
        '/asm:assembly/msix:msix', $applicationNamespaces).SetAttribute(
        'applicationId', 'WrongApp')
    Assert-ContractThrows 'application identity mismatch' {
        Assert-EzyExternalApplicationManifest $tamperedApplication $publisher
    }

    $tamperedApplication = Read-EzyExternalXml $first.ApplicationManifestPath
    $applicationNamespaces = [Xml.XmlNamespaceManager]::new($tamperedApplication.NameTable)
    $applicationNamespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $applicationNamespaces.AddNamespace('msix', 'urn:schemas-microsoft-com:msix.v1')
    $tamperedApplication.SelectSingleNode(
        '/asm:assembly/msix:msix', $applicationNamespaces).SetAttribute(
        'unexpected', 'value')
    Assert-ContractThrows 'unexpected fusion identity attribute' {
        Assert-EzyExternalApplicationManifest $tamperedApplication $publisher
    }

    $embeddedApplication = Read-EzyExternalXml $first.ApplicationManifestPath
    $embeddedNamespaces = [Xml.XmlNamespaceManager]::new($embeddedApplication.NameTable)
    $embeddedNamespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $assembly = $embeddedApplication.SelectSingleNode('/asm:assembly', $embeddedNamespaces)
    $assemblyIdentity = $embeddedApplication.SelectSingleNode(
        '/asm:assembly/asm:assemblyIdentity', $embeddedNamespaces)
    $generatedFile = $embeddedApplication.CreateElement(
        'asmv3', 'file', 'urn:schemas-microsoft-com:asm.v3')
    $generatedFile.SetAttribute('name', 'Microsoft.WindowsAppRuntime.dll')
    [void]$assembly.InsertAfter($generatedFile, $assemblyIdentity)
    Assert-ContractThrows 'tool-generated file rejected for source template' {
        Assert-EzyExternalApplicationManifest $embeddedApplication $publisher
    }
    Assert-EzyExternalApplicationManifest $embeddedApplication $publisher -Embedded
    $script:PassCount++

    $unexpectedEmbeddedApplication = $embeddedApplication.CloneNode($true)
    $unexpectedRoot = $unexpectedEmbeddedApplication.DocumentElement
    [void]$unexpectedRoot.AppendChild($unexpectedEmbeddedApplication.CreateElement(
        'asmv3', 'trustInfo', 'urn:schemas-microsoft-com:asm.v3'))
    Assert-ContractThrows 'unexpected embedded root child' {
        Assert-EzyExternalApplicationManifest `
            $unexpectedEmbeddedApplication $publisher -Embedded
    }

    $dtdPath = Join-Path $tempRoot 'dtd.xml'
    [IO.File]::WriteAllText(
        $dtdPath,
        '<!DOCTYPE Package [<!ENTITY injected "value">]><Package>&injected;</Package>',
        [Text.UTF8Encoding]::new($false))
    Assert-ContractThrows 'DTD input' {
        [void](Read-EzyExternalXml $dtdPath)
    }

    $oversizedPath = Join-Path $tempRoot 'oversized.xml'
    [IO.File]::WriteAllText(
        $oversizedPath,
        '<Package>' + ('x' * (1MB + 1)) + '</Package>',
        [Text.UTF8Encoding]::new($false))
    Assert-ContractThrows 'oversized XML input' {
        [void](Read-EzyExternalXml $oversizedPath)
    }

    Write-Output "External-location contract tests passed: $script:PassCount"
}
finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if (-not $resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedTempRoot -ceq $tempBase) {
        throw "Refusing to remove unsafe contract-test path: '$resolvedTempRoot'."
    }

    if ([IO.Directory]::Exists($resolvedTempRoot)) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
