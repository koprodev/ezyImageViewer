[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'release-helpers.ps1')
. (Join-Path $scriptRoot 'portable-release-helpers.ps1')

Assert-EzyPortableVersion $Version
$numericVersion = Get-EzyPortableNumericVersion $Version
$output = Get-Item -LiteralPath ([IO.Path]::GetFullPath($OutputDirectory)) -Force
if (-not $output.PSIsContainer -or
    ($output.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Portable release output must be a physical directory.'
}

$packageStem = "ezyImageViewer-$Version-win-x64"
$archiveName = "$packageStem.zip"
$manifestName = 'portable-release-manifest.json'
$hashesName = 'SHA256SUMS.txt'
$expectedFiles = @($archiveName, $hashesName, $manifestName)
$actualFiles = @(Get-ChildItem -LiteralPath $output.FullName -Force)
if (@($actualFiles | Where-Object { $_.PSIsContainer }).Count -ne 0 -or
    $actualFiles.Count -ne $expectedFiles.Count) {
    throw 'Portable release output must contain exactly three top-level files.'
}
foreach ($expected in $expectedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $output.FullName $expected) -PathType Leaf)) {
        throw "Portable release output is missing '$expected'."
    }
}

$artifactPaths = [ordered]@{}
$artifactPaths[$archiveName] = Join-Path $output.FullName $archiveName
$artifactPaths[$manifestName] = Join-Path $output.FullName $manifestName
$hashLines = [IO.File]::ReadAllLines((Join-Path $output.FullName $hashesName))
if ($hashLines.Count -ne $artifactPaths.Count) {
    throw 'SHA256SUMS.txt entry count is invalid.'
}
$seenHashes = @{}
$orderedNames = New-Object 'Collections.Generic.List[string]'
foreach ($line in $hashLines) {
    if ($line -cnotmatch '^([A-F0-9]{64})  ([A-Za-z0-9_.-]+)$') {
        throw "Invalid SHA256SUMS.txt line: '$line'."
    }
    $hash = $Matches[1]
    $name = $Matches[2]
    if (-not $artifactPaths.Contains($name) -or $seenHashes.ContainsKey($name)) {
        throw "Unexpected or duplicate SHA256SUMS.txt entry: '$name'."
    }
    $actualHash = (Get-FileHash -LiteralPath $artifactPaths[$name] -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $hash) {
        throw "SHA-256 mismatch for '$name'."
    }
    $seenHashes[$name] = $true
    [void]$orderedNames.Add($name)
}
$sortedNames = $orderedNames.ToArray()
[Array]::Sort($sortedNames, [StringComparer]::Ordinal)
if (($orderedNames.ToArray() -join "`n") -cne ($sortedNames -join "`n")) {
    throw 'SHA256SUMS.txt entries are not ordinally sorted.'
}

$manifestPath = $artifactPaths[$manifestName]
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1 -or
    [string]$manifest.tag -cne "v$Version" -or
    [string]$manifest.version -cne $Version -or
    [string]$manifest.numericVersion -cne $numericVersion -or
    [string]$manifest.channel -cne 'basic-portable-preview' -or
    [string]$manifest.platform.operatingSystem -cne 'Windows 10 build 19041 or later' -or
    [string]$manifest.platform.architecture -cne 'x64' -or
    [bool]$manifest.packageIdentity -or
    [bool]$manifest.installer -or
    [bool]$manifest.signed -or
    [string]$manifest.supportedUse -cne 'evaluation-and-testing-preview') {
    throw 'Portable release manifest contract mismatch.'
}
if ([string]$manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
    [string]$manifest.archive.fileName -cne $archiveName -or
    [long]$manifest.archive.byteCount -ne (Get-Item -LiteralPath $artifactPaths[$archiveName]).Length -or
    [string]$manifest.archive.sha256 -cne (
        Get-FileHash -LiteralPath $artifactPaths[$archiveName] -Algorithm SHA256).Hash.ToUpperInvariant()) {
    throw 'Portable release archive metadata mismatch.'
}

$extractRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ezy-portable-verify-' + [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($extractRoot)
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::OpenRead($artifactPaths[$archiveName])
    try {
        $zip = [IO.Compression.ZipArchive]::new(
            $archiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $entryNames = New-Object 'Collections.Generic.List[string]'
            $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($entry in $zip.Entries) {
                $entryName = $entry.FullName
                if (-not $entryName.StartsWith("$packageStem/", [StringComparison]::Ordinal) -or
                    $entryName.EndsWith('/', [StringComparison]::Ordinal) -or
                    $entryName.Contains('\') -or
                    $entryName.Contains(':') -or
                    $entryName.Split('/') -contains '..' -or
                    -not $seenEntries.Add($entryName)) {
                    throw "Unsafe or duplicate portable ZIP entry: '$entryName'."
                }
                [void]$entryNames.Add($entryName)
                $relative = $entryName.Substring($packageStem.Length + 1)
                $destination = [IO.Path]::GetFullPath((Join-Path $extractRoot $relative.Replace('/', '\')))
                if (-not (Test-EzyPathWithinDirectory $destination $extractRoot)) {
                    throw "Portable ZIP entry escaped extraction root: '$entryName'."
                }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
                $source = $entry.Open()
                $target = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try {
                    $source.CopyTo($target)
                }
                finally {
                    $target.Dispose()
                    $source.Dispose()
                }
            }
            $sortedEntries = $entryNames.ToArray()
            [Array]::Sort($sortedEntries, [StringComparer]::Ordinal)
            if (($entryNames.ToArray() -join "`n") -cne ($sortedEntries -join "`n")) {
                throw 'Portable ZIP entries are not ordinally sorted.'
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }

    Assert-EzyPackageContentsManifest `
        -UnpackedRoot $extractRoot `
        -PackageLabel 'Portable preview'
    foreach ($required in @(
            'ezyImageViewer.exe',
            'ezyImageViewer.dll',
            'ezyImageViewer.deps.json',
            'ezyImageViewer.runtimeconfig.json',
            'ezyImageViewer.pri',
            'LICENSE.txt',
            'THIRD-PARTY-NOTICES.md',
            'PORTABLE-README.txt',
            'THIRD-PARTY-LICENSES\INDEX.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extractRoot $required) -PathType Leaf)) {
            throw "Portable preview is missing '$required'."
        }
    }

    $forbidden = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Force | Where-Object {
            $_.Extension -ieq '.pdb' -or
            $_.Name -match '(?i)^(AppxManifest\.xml|AppxSignature\.p7x|Magick\.)'
        })
    if ($forbidden.Count -ne 0) {
        throw "Portable preview contains forbidden package or test-only content: '$($forbidden[0].FullName)'."
    }

    $portableReadme = [IO.File]::ReadAllText((Join-Path $extractRoot 'PORTABLE-README.txt'))
    foreach ($requiredText in @(
            'evaluation and testing preview',
            'Windows SmartScreen',
            '%LOCALAPPDATA%\ezyImageViewer',
            'No installer or package identity')) {
        if ($portableReadme.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "PORTABLE-README.txt is missing required disclosure: '$requiredText'."
        }
    }

    $licenseIndexPath = Join-Path $extractRoot 'THIRD-PARTY-LICENSES\INDEX.json'
    $licenseIndex = [IO.File]::ReadAllText($licenseIndexPath) | ConvertFrom-Json
    $winui = @($licenseIndex.packages | Where-Object {
            [string]$_.id -ceq 'Microsoft.WindowsAppSDK.WinUI'
        })
    if ($winui.Count -ne 1 -or [string]$winui[0].version -cne '2.2.1') {
        throw 'Portable third-party index is missing Microsoft.WindowsAppSDK.WinUI 2.2.1.'
    }
    $winuiLicenseRecords = @($winui[0].copiedFiles | Where-Object {
            [string]$_.path -match '(?i)/license\.txt$'
        })
    if ($winuiLicenseRecords.Count -eq 0) {
        throw 'Portable preview is missing the Windows App SDK WinUI license text.'
    }
    $winuiLicensePath = Join-Path $extractRoot (
        'THIRD-PARTY-LICENSES\' + $winuiLicenseRecords[0].path.Replace('/', '\'))
    $winuiLicense = [IO.File]::ReadAllText($winuiLicensePath)
    if ($winuiLicense.IndexOf('MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW',
            [StringComparison]::Ordinal) -lt 0 -or
        $winuiLicense.IndexOf('live operating environment',
            [StringComparison]::Ordinal) -lt 0) {
        throw 'Portable preview does not preserve the current WinUI license disclosure.'
    }

    $executablePath = Join-Path $extractRoot 'ezyImageViewer.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $executablePath
    if ([string]$signature.Status -cne 'NotSigned') {
        throw "Portable executable signature status must be NotSigned; actual '$($signature.Status)'."
    }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
    if ([string]$versionInfo.FileVersion -cne $numericVersion) {
        throw "Portable executable file version mismatch: '$($versionInfo.FileVersion)'."
    }

    $actualInventoryHash = (Get-FileHash `
        -LiteralPath (Join-Path $extractRoot 'PACKAGE-CONTENTS.sha256') `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $actualLicenseIndexHash = (Get-FileHash `
        -LiteralPath $licenseIndexPath `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $actualPayloadFiles = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Force)
    [long]$actualPayloadBytes = 0
    foreach ($file in $actualPayloadFiles) {
        $actualPayloadBytes += $file.Length
    }
    if ([int]$manifest.payload.fileCount -ne $actualPayloadFiles.Count -or
        [long]$manifest.payload.byteCount -ne $actualPayloadBytes -or
        [string]$manifest.payload.inventorySha256 -cne $actualInventoryHash -or
        [string]$manifest.payload.thirdPartyIndexSha256 -cne $actualLicenseIndexHash) {
        throw 'Portable payload metadata mismatch.'
    }

    Write-Output 'Portable release verification passed.'
    Write-Output "Version: $Version"
    Write-Output "Payload files: $($actualPayloadFiles.Count)"
    Write-Output "Archive SHA-256: $($manifest.archive.sha256)"
    Write-Output 'Signature: NotSigned (testing preview)'
}
finally {
    if ([IO.Directory]::Exists($extractRoot)) {
        [IO.Directory]::Delete($extractRoot, $true)
    }
}
