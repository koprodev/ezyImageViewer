#Requires -Version 5.1

# Exercises deterministic generation plus representative fail-closed App Installer mutations.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainPackage,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostPackage
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'appinstaller-helpers.ps1')

$mainItem = Get-EzyAppInstallerPhysicalFile -Path $MainPackage -Label 'MainPackage'
$codecHostItem = Get-EzyAppInstallerPhysicalFile `
    -Path $CodecHostPackage -Label 'CodecHostPackage'
$pair = Get-EzyReleasePairContract `
    -MainPackage $mainItem.FullName -CodecHostPackage $codecHostItem.FullName
$generator = Join-Path $PSScriptRoot 'generate-appinstaller.ps1'
$verifier = Join-Path $PSScriptRoot 'verify-appinstaller-release.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'ezyImageViewer-appinstaller-contract-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $scratch)

$baseUri = 'https://example.invalid/ezyimageviewer/'
$mainUri = $baseUri + $mainItem.Name
$codecHostUri = $baseUri + $codecHostItem.Name
$defaultPath = Join-Path $scratch 'ezyImageViewer.appinstaller'
$defaultUri = $baseUri + 'ezyImageViewer.appinstaller'
$onLaunchPath = Join-Path $scratch 'ezyImageViewer-onlaunch.appinstaller'
$onLaunchUri = $baseUri + 'ezyImageViewer-onlaunch.appinstaller'
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

function Invoke-AppInstallerVerifier {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [ValidateSet('None', 'OnLaunch')]
        [string]$Mode = 'None',

        [int]$Hours = 24,

        [string]$ExpectedAppInstallerUri = $defaultUri
    )

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $verifier,
        '-AppInstallerFile', $Path,
        '-MainPackage', $mainItem.FullName,
        '-CodecHostPackage', $codecHostItem.FullName,
        '-AppInstallerUri', $ExpectedAppInstallerUri,
        '-MainPackageUri', $mainUri,
        '-CodecHostPackageUri', $codecHostUri,
        '-ExpectedUpdateMode', $Mode)
    if ($Mode -eq 'OnLaunch') {
        $arguments += @('-HoursBetweenUpdateChecks', $Hours)
    }
    & powershell @arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "App Installer verifier exited with $LASTEXITCODE."
    }
}

function Write-Mutation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Content,

        [string]$LeafName = 'ezyImageViewer.appinstaller'
    )

    $directory = Join-Path $scratch $Name
    [void](New-Item -ItemType Directory -Path $directory)
    $path = Join-Path $directory $LeafName
    [IO.File]::WriteAllText($path, $Content, $utf8WithoutBom)
    return $path
}

function Assert-ExpectedFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
    }
    if (-not $failed) {
        throw "Negative App Installer case did not fail: $Label."
    }
    Write-Output "negative PASS: $Label"
}

try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
        -MainPackage $mainItem.FullName `
        -CodecHostPackage $codecHostItem.FullName `
        -OutputPath $defaultPath `
        -AppInstallerUri $defaultUri `
        -MainPackageUri $mainUri `
        -CodecHostPackageUri $codecHostUri | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Default App Installer generation exited with $LASTEXITCODE."
    }
    Invoke-AppInstallerVerifier -Path $defaultPath
    $firstHash = (Get-FileHash -LiteralPath $defaultPath -Algorithm SHA256).Hash

    & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
        -MainPackage $mainItem.FullName `
        -CodecHostPackage $codecHostItem.FullName `
        -OutputPath $defaultPath `
        -AppInstallerUri $defaultUri `
        -MainPackageUri $mainUri `
        -CodecHostPackageUri $codecHostUri | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Repeated App Installer generation exited with $LASTEXITCODE."
    }
    $secondHash = (Get-FileHash -LiteralPath $defaultPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($firstHash, $secondHash, [StringComparison]::Ordinal)) {
        throw 'Repeated App Installer generation was not byte-identical.'
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
        -MainPackage $mainItem.FullName `
        -CodecHostPackage $codecHostItem.FullName `
        -OutputPath $onLaunchPath `
        -AppInstallerUri $onLaunchUri `
        -MainPackageUri $mainUri `
        -CodecHostPackageUri $codecHostUri `
        -UpdateMode OnLaunch `
        -HoursBetweenUpdateChecks 12 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "OnLaunch App Installer generation exited with $LASTEXITCODE."
    }
    Invoke-AppInstallerVerifier `
        -Path $onLaunchPath -Mode OnLaunch -Hours 12 `
        -ExpectedAppInstallerUri $onLaunchUri

    $defaultText = [IO.File]::ReadAllText($defaultPath, [Text.Encoding]::UTF8)
    $onLaunchText = [IO.File]::ReadAllText($onLaunchPath, [Text.Encoding]::UTF8)

    $wrongNamespace = Write-Mutation -Name 'wrong-namespace' -Content (
        $defaultText.Replace(
            'http://schemas.microsoft.com/appx/appinstaller/2017/2',
            'http://schemas.microsoft.com/appx/appinstaller/2021'))
    Assert-ExpectedFailure -Label 'wrong namespace' -Action {
        Invoke-AppInstallerVerifier -Path $wrongNamespace
    }

    $wrongIdentity = Write-Mutation -Name 'wrong-identity' -Content (
        $defaultText.Replace(
            'Name="GRTech.ezyImageViewer"',
            'Name="GRTech.ezyImageViewer.Bad"'))
    Assert-ExpectedFailure -Label 'main identity mismatch' -Action {
        Invoke-AppInstallerVerifier -Path $wrongIdentity
    }

    $numericEntity = Write-Mutation -Name 'numeric-entity' -Content (
        $defaultText.Replace(
            'Name="GRTech.ezyImageViewer"',
            'Name="GRTech.&#x65;zyImageViewer"'))
    Assert-ExpectedFailure -Label 'XML numeric character reference' -Action {
        Invoke-AppInstallerVerifier -Path $numericEntity
    }

    $duplicateDependencyText = [Text.RegularExpressions.Regex]::Replace(
        $defaultText,
        '(<Package [^>]+/>)',
        '$1' + "`n    " + '$1',
        1)
    $duplicateDependency = Write-Mutation `
        -Name 'duplicate-dependency' -Content $duplicateDependencyText
    Assert-ExpectedFailure -Label 'duplicate dependency' -Action {
        Invoke-AppInstallerVerifier -Path $duplicateDependency
    }

    $unexpectedUpdate = Write-Mutation -Name 'unexpected-update' -Content (
        $defaultText.Replace(
            '</AppInstaller>',
            "  <UpdateSettings>`n    <OnLaunch HoursBetweenUpdateChecks=`"12`" />`n" +
            "  </UpdateSettings>`n</AppInstaller>"))
    Assert-ExpectedFailure -Label 'unapproved OnLaunch update mode' -Action {
        Invoke-AppInstallerVerifier -Path $unexpectedUpdate
    }

    $downgrade = Write-Mutation -Name 'downgrade' `
        -LeafName 'ezyImageViewer-onlaunch.appinstaller' -Content (
        $onLaunchText.Replace(
            '</UpdateSettings>',
            "    <ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>`n" +
            '  </UpdateSettings>'))
    Assert-ExpectedFailure -Label 'downgrade element' -Action {
        Invoke-AppInstallerVerifier `
            -Path $downgrade -Mode OnLaunch -Hours 12 `
            -ExpectedAppInstallerUri $onLaunchUri
    }

    $background = Write-Mutation -Name 'background' `
        -LeafName 'ezyImageViewer-onlaunch.appinstaller' -Content (
        $onLaunchText.Replace(
            '</UpdateSettings>',
            "    <AutomaticBackgroundTask />`n  </UpdateSettings>"))
    Assert-ExpectedFailure -Label 'unapproved background update' -Action {
        Invoke-AppInstallerVerifier `
            -Path $background -Mode OnLaunch -Hours 12 `
            -ExpectedAppInstallerUri $onLaunchUri
    }

    $dtd = Write-Mutation -Name 'dtd' -Content (
        $defaultText.Replace(
            "?>`n",
            "?>`n<!DOCTYPE AppInstaller [<!ENTITY probe `"blocked`">]>`n"))
    Assert-ExpectedFailure -Label 'DTD' -Action {
        Invoke-AppInstallerVerifier -Path $dtd
    }

    $bomDirectory = Join-Path $scratch 'bom'
    [void](New-Item -ItemType Directory -Path $bomDirectory)
    $bomPath = Join-Path $bomDirectory 'ezyImageViewer.appinstaller'
    $originalBytes = [IO.File]::ReadAllBytes($defaultPath)
    $bomBytes = [byte[]]::new($originalBytes.Length + 3)
    $bomBytes[0] = 0xEF
    $bomBytes[1] = 0xBB
    $bomBytes[2] = 0xBF
    [Array]::Copy($originalBytes, 0, $bomBytes, 3, $originalBytes.Length)
    [IO.File]::WriteAllBytes($bomPath, $bomBytes)
    Assert-ExpectedFailure -Label 'UTF-8 BOM' -Action {
        Invoke-AppInstallerVerifier -Path $bomPath
    }

    $queryUri = $defaultUri + '?channel=test'
    Assert-ExpectedFailure -Label 'query URI' -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $verifier `
            -AppInstallerFile $defaultPath `
            -MainPackage $mainItem.FullName `
            -CodecHostPackage $codecHostItem.FullName `
            -AppInstallerUri $queryUri `
            -MainPackageUri $mainUri `
            -CodecHostPackageUri $codecHostUri 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Expected query rejection exit $LASTEXITCODE."
        }
    }

    $encodedSeparatorUri = $baseUri + 'redirect%2F' + $mainItem.Name
    Assert-ExpectedFailure -Label 'encoded path separator' -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
            -MainPackage $mainItem.FullName `
            -CodecHostPackage $codecHostItem.FullName `
            -OutputPath $defaultPath `
            -AppInstallerUri $defaultUri `
            -MainPackageUri $encodedSeparatorUri `
            -CodecHostPackageUri $codecHostUri 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Expected encoded-separator rejection exit $LASTEXITCODE."
        }
    }

    $encodedFileNameUri = $baseUri + '%65zyImageViewer.msix'
    Assert-ExpectedFailure -Label 'encoded package basename' -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
            -MainPackage $mainItem.FullName `
            -CodecHostPackage $codecHostItem.FullName `
            -OutputPath $defaultPath `
            -AppInstallerUri $defaultUri `
            -MainPackageUri $encodedFileNameUri `
            -CodecHostPackageUri $codecHostUri 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Expected encoded-basename rejection exit $LASTEXITCODE."
        }
    }

    $nestedEncodingUri = $baseUri + 'a/%252e%252e/' + $mainItem.Name
    Assert-ExpectedFailure -Label 'nested percent encoding' -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $generator `
            -MainPackage $mainItem.FullName `
            -CodecHostPackage $codecHostItem.FullName `
            -OutputPath $defaultPath `
            -AppInstallerUri $defaultUri `
            -MainPackageUri $nestedEncodingUri `
            -CodecHostPackageUri $codecHostUri 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Expected nested-encoding rejection exit $LASTEXITCODE."
        }
    }

    $oversizedDirectory = Join-Path $scratch 'oversized'
    [void](New-Item -ItemType Directory -Path $oversizedDirectory)
    $oversizedPath = Join-Path $oversizedDirectory 'ezyImageViewer.appinstaller'
    $oversizedBytes = [byte[]]::new(1MB + 1)
    [IO.File]::WriteAllBytes($oversizedPath, $oversizedBytes)
    Assert-ExpectedFailure -Label 'oversized AppInstaller' -Action {
        Invoke-AppInstallerVerifier -Path $oversizedPath
    }

    $caseVariantHashesPath = Join-Path $scratch 'SHA256SUMS-case-variant.txt'
    $caseVariantRecords = @(
        [pscustomobject]@{
            Name = $codecHostItem.Name
            Hash = (Get-FileHash -LiteralPath $codecHostItem.FullName -Algorithm SHA256).Hash
        },
        [pscustomobject]@{
            Name = [IO.Path]::GetFileName($defaultPath)
            Hash = (Get-FileHash -LiteralPath $defaultPath -Algorithm SHA256).Hash
        },
        [pscustomobject]@{
            Name = $mainItem.Name.ToLowerInvariant()
            Hash = (Get-FileHash -LiteralPath $mainItem.FullName -Algorithm SHA256).Hash
        })
    [string[]]$caseVariantLines = @($caseVariantRecords | ForEach-Object {
        "$($_.Hash)  $($_.Name)"
    })
    [Array]::Sort($caseVariantLines, [StringComparer]::Ordinal)
    [IO.File]::WriteAllLines(
        $caseVariantHashesPath,
        $caseVariantLines,
        $utf8WithoutBom)
    Assert-ExpectedFailure -Label 'case-variant checksum basename' -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'verify-msix-release.ps1') `
            -MainPackage $mainItem.FullName `
            -CodecHostPackage $codecHostItem.FullName `
            -Version $pair.Main.Version `
            -CodecHostVersion $pair.CodecHost.Version `
            -Publisher $pair.Main.Publisher `
            -HashesFile $caseVariantHashesPath `
            -AppInstallerFile $defaultPath `
            -RequireBuildOutputMatch 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Expected case-variant checksum rejection exit $LASTEXITCODE."
        }
    }

    Write-Output "positive PASS: default and OnLaunch"
    Write-Output "deterministic SHA-256: $firstHash"
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
