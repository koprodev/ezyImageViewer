#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$MainMsix,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostMsix,

    [string]$AppInstallerFile,

    [Parameter(Mandatory = $true)]
    [string]$MainDepsJson,

    [Parameter(Mandatory = $true)]
    [string]$CodecHostDepsJson,

    [string]$MainProjectAssetsJson,

    [string]$CodecHostProjectAssetsJson,

    [string]$NuGetPackageRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-FourPartVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
        throw "$ParameterName must be a canonical four-part numeric version: '$Value'."
    }

    foreach ($part in $Value.Split('.')) {
        $number = [uint32]::Parse($part, [Globalization.CultureInfo]::InvariantCulture)
        if ($number -gt 65535) {
            throw "$ParameterName contains a component greater than 65535: '$Value'."
        }
    }
}

function Get-ExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "$ParameterName must identify a file: '$Path'."
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$ParameterName must not be a reparse point: '$Path'."
    }
    return $item
}

function Get-MsixEntrySha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$EntryName,

        [Parameter(Mandatory = $true)]
        [string]$PackageLabel
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            [string]::Equals($_.FullName, $EntryName, [StringComparison]::Ordinal)
        })
        if ($entries.Count -ne 1) {
            throw "$PackageLabel must contain exactly one '$EntryName' entry; found $($entries.Count)."
        }

        $stream = $entries[0].Open()
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-MsixEntryMatchesFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$EntryName,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$PackageLabel
    )

    $file = Get-ExistingFile -Path $FilePath -ParameterName "$PackageLabel source '$EntryName'"
    $entryHash = Get-MsixEntrySha256 -PackagePath $PackagePath `
        -EntryName $EntryName -PackageLabel $PackageLabel
    $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not [string]::Equals($entryHash, $fileHash, [StringComparison]::Ordinal)) {
        throw "$PackageLabel '$EntryName' does not match the supplied source file."
    }
    return $entryHash
}

function Get-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "$ParameterName must identify a directory: '$Path'."
    }
    return $item
}

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    $directoryPrefix = $Directory.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($directoryPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-SortedKeys {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Dictionary
    )

    [string[]]$keys = @($Dictionary.Keys | ForEach-Object { [string]$_ })
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    return $keys
}

function Write-DeterministicText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd("`n") + "`n"
    $encoding = New-Object Text.UTF8Encoding($false)
    $bytes = $encoding.GetBytes($normalized)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Get-ExistingDirectory `
        -Path (Split-Path $fullPath -Parent) -ParameterName 'metadata output parent'
    if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Metadata output parent must not be a reparse point: '$($parent.FullName)'."
    }
    if (Test-Path -LiteralPath $fullPath) {
        $existing = Get-ExistingFile -Path $fullPath -ParameterName 'metadata output'
        if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Metadata output must not be a reparse point: '$fullPath'."
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
            $stream.Write($bytes, 0, $bytes.Length)
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
}

function Convert-Base64Sha512ToHex {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $base64 = $Value.Trim()
    if ($base64.StartsWith('sha512-', [StringComparison]::OrdinalIgnoreCase)) {
        $base64 = $base64.Substring(7)
    }

    try {
        [byte[]]$bytes = [Convert]::FromBase64String($base64)
    }
    catch {
        throw "Invalid SHA-512 base64 value: '$Value'."
    }
    if ($bytes.Length -ne 64) {
        throw "SHA-512 value must decode to 64 bytes, not $($bytes.Length)."
    }
    return ([BitConverter]::ToString($bytes)).Replace('-', '')
}

function Get-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    $file = Get-ExistingFile -Path $Path -ParameterName $ParameterName
    try {
        return (Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        throw "$ParameterName is not valid JSON: '$($file.FullName)'. $($_.Exception.Message)"
    }
}

function Add-UniqueDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$Directories,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Seen,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Source
    )

    $directory = Get-ExistingDirectory -Path $Path -ParameterName $Source
    if (-not $Seen.Contains($directory.FullName)) {
        $Seen[$directory.FullName] = $true
        [void]$Directories.Add($directory.FullName)
    }
}

function Get-NuGetPackageRoots {
    $directories = New-Object 'Collections.Generic.List[string]'
    $seen = @{}

    if (-not [string]::IsNullOrWhiteSpace($NuGetPackageRoot)) {
        Add-UniqueDirectory -Directories $directories -Seen $seen `
            -Path $NuGetPackageRoot -Source 'NuGetPackageRoot'
    }

    $assetsInputs = @(
        [ordered]@{ Name = 'MainProjectAssetsJson'; Path = $MainProjectAssetsJson },
        [ordered]@{ Name = 'CodecHostProjectAssetsJson'; Path = $CodecHostProjectAssetsJson }
    )
    foreach ($assetsInput in $assetsInputs) {
        if ([string]::IsNullOrWhiteSpace([string]$assetsInput.Path)) {
            continue
        }

        $assets = Get-JsonFile -Path $assetsInput.Path -ParameterName $assetsInput.Name
        if ($null -eq $assets.packageFolders) {
            throw "$($assetsInput.Name) does not contain packageFolders."
        }

        [string[]]$packageFolders = @(
            $assets.packageFolders.PSObject.Properties | ForEach-Object { [string]$_.Name }
        )
        [Array]::Sort($packageFolders, [StringComparer]::OrdinalIgnoreCase)
        foreach ($packageFolder in $packageFolders) {
            Add-UniqueDirectory -Directories $directories -Seen $seen `
                -Path $packageFolder -Source "$($assetsInput.Name).packageFolders"
        }
    }

    if ($directories.Count -eq 0) {
        throw 'Provide NuGetPackageRoot or a project.assets.json file with packageFolders.'
    }
    return $directories.ToArray()
}

function Split-LibraryKey {
    param([Parameter(Mandatory = $true)][string]$Key)

    $separator = $Key.LastIndexOf('/')
    if ($separator -le 0 -or $separator -eq ($Key.Length - 1)) {
        throw "Runtime library key must use name/version form: '$Key'."
    }
    return [pscustomobject]@{
        Id = $Key.Substring(0, $separator)
        Version = $Key.Substring($separator + 1)
    }
}

function Get-PackageDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$LibraryType,

        [string]$LibraryPath,

        [Parameter(Mandatory = $true)]
        [string[]]$PackageRoots
    )

    $relativeCandidates = New-Object 'Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($LibraryPath)) {
        [void]$relativeCandidates.Add($LibraryPath)
    }

    $packageId = $Id
    if ([string]::Equals($LibraryType, 'runtimepack', [StringComparison]::OrdinalIgnoreCase)) {
        $packageId = $packageId -replace '^runtimepack\.', ''
    }
    $derivedPath = ($packageId + '/' + $Version).ToLowerInvariant()
    if (-not $relativeCandidates.Contains($derivedPath)) {
        [void]$relativeCandidates.Add($derivedPath)
    }

    foreach ($root in $PackageRoots) {
        $rootPrefix = $root.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($relativeCandidate in $relativeCandidates) {
            if ([IO.Path]::IsPathRooted($relativeCandidate)) {
                throw "NuGet library path must be relative: '$relativeCandidate'."
            }

            $candidate = [IO.Path]::GetFullPath((Join-Path $root $relativeCandidate))
            if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "NuGet library path escapes package root: '$relativeCandidate'."
            }
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                return (Get-Item -LiteralPath $candidate -Force).FullName
            }
        }
    }

    throw "NuGet package directory was not found for $Id/$Version."
}

function Get-XmlChildText {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlNode]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) {
        return $null
    }
    return $node.InnerText.Trim()
}

function Get-XmlAttributeValue {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlNode]$Node,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $attribute = $Node.Attributes[$Name]
    if ($null -eq $attribute -or [string]::IsNullOrWhiteSpace($attribute.Value)) {
        return $null
    }
    return $attribute.Value.Trim()
}

function Get-NuGetMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$LibraryType,

        [Parameter(Mandatory = $true)]
        [psobject]$Library,

        [Parameter(Mandatory = $true)]
        [string[]]$PackageRoots
    )

    $libraryPath = $null
    $pathProperty = $Library.PSObject.Properties['path']
    if ($null -ne $pathProperty) {
        $libraryPath = [string]$pathProperty.Value
    }
    $packageDirectory = Get-PackageDirectory -Id $Id -Version $Version `
        -LibraryType $LibraryType -LibraryPath $libraryPath -PackageRoots $PackageRoots

    $nuspecs = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)
    if ($nuspecs.Count -ne 1) {
        throw "Expected exactly one nuspec for $Id/$Version in '$packageDirectory'; found $($nuspecs.Count)."
    }

    try {
        [xml]$nuspec = Get-Content -LiteralPath $nuspecs[0].FullName -Raw -Encoding UTF8
    }
    catch {
        throw "Invalid nuspec for $Id/${Version}: '$($nuspecs[0].FullName)'. $($_.Exception.Message)"
    }
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "Nuspec metadata is missing for $Id/$Version."
    }

    $nuspecId = Get-XmlChildText -Parent $metadata -Name 'id'
    $nuspecVersion = Get-XmlChildText -Parent $metadata -Name 'version'
    $expectedId = $Id
    if ([string]::Equals($LibraryType, 'runtimepack', [StringComparison]::OrdinalIgnoreCase)) {
        $expectedId = $expectedId -replace '^runtimepack\.', ''
    }
    if (-not [string]::Equals($nuspecId, $expectedId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Nuspec id '$nuspecId' does not match runtime component '$expectedId'."
    }
    if (-not [string]::Equals($nuspecVersion, $Version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Nuspec version '$nuspecVersion' does not match runtime component '$Version'."
    }

    $license = $null
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    if ($null -ne $licenseNode -and -not [string]::IsNullOrWhiteSpace($licenseNode.InnerText)) {
        $licenseKind = Get-XmlAttributeValue -Node $licenseNode -Name 'type'
        if ([string]::IsNullOrWhiteSpace($licenseKind)) {
            $licenseKind = 'unspecified'
        }
        $license = [ordered]@{
            kind = $licenseKind.ToLowerInvariant()
            value = $licenseNode.InnerText.Trim()
        }
        if ([string]::Equals($license.kind, 'file', [StringComparison]::OrdinalIgnoreCase)) {
            $licensePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $license.value))
            if (-not (Test-PathWithinDirectory -Path $licensePath -Directory $packageDirectory) -or
                -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                throw "Nuspec license file is missing or escapes its package: '$($license.value)' for $Id/$Version."
            }
        }
    }
    else {
        $licenseUrl = Get-XmlChildText -Parent $metadata -Name 'licenseUrl'
        if (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
            $license = [ordered]@{
                kind = 'url'
                value = $licenseUrl
            }
        }
    }

    $repository = $null
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    if ($null -ne $repositoryNode) {
        $repositoryUrl = Get-XmlAttributeValue -Node $repositoryNode -Name 'url'
        if (-not [string]::IsNullOrWhiteSpace($repositoryUrl)) {
            $repository = [ordered]@{
                type = Get-XmlAttributeValue -Node $repositoryNode -Name 'type'
                url = $repositoryUrl
                branch = Get-XmlAttributeValue -Node $repositoryNode -Name 'branch'
                commit = Get-XmlAttributeValue -Node $repositoryNode -Name 'commit'
            }
        }
    }

    $nupkgs = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nupkg' -File)
    if ($nupkgs.Count -ne 1) {
        throw "Expected exactly one nupkg for $Id/$Version in '$packageDirectory'; found $($nupkgs.Count)."
    }
    if (($nupkgs[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "NuGet package archive must not be a reparse point for $Id/$Version."
    }

    $hashFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nupkg.sha512' -File)
    if ($hashFiles.Count -ne 1) {
        throw "Expected exactly one nupkg SHA-512 file for $Id/$Version in '$packageDirectory'; found $($hashFiles.Count)."
    }
    if (($hashFiles[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "NuGet SHA-512 sidecar must not be a reparse point for $Id/$Version."
    }

    $actualSha512 = (Get-FileHash -LiteralPath $nupkgs[0].FullName -Algorithm SHA512).Hash.ToUpperInvariant()
    $sidecarSha512 = Convert-Base64Sha512ToHex -Value (
        Get-Content -LiteralPath $hashFiles[0].FullName -Raw -Encoding ASCII)
    if (-not [string]::Equals($actualSha512, $sidecarSha512, [StringComparison]::Ordinal)) {
        throw "NuGet archive SHA-512 does not match its sidecar for $Id/$Version."
    }

    $shaProperty = $Library.PSObject.Properties['sha512']
    if ($null -ne $shaProperty -and -not [string]::IsNullOrWhiteSpace([string]$shaProperty.Value)) {
        # deps.json stores NuGet's normalized content hash, not the raw nupkg file hash.
        [void](Convert-Base64Sha512ToHex -Value ([string]$shaProperty.Value))
    }
    $sha512 = $actualSha512

    return [pscustomobject]@{
        Id = $nuspecId
        Version = $nuspecVersion
        PackageDirectory = $packageDirectory
        Sha512 = $sha512
        License = $license
        Repository = $repository
    }
}

function New-NuGetBomRef {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Version
    )

    return 'pkg:nuget/{0}@{1}' -f [Uri]::EscapeDataString($Id), [Uri]::EscapeDataString($Version)
}

function Add-Edge {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Edges,

        [Parameter(Mandatory = $true)]
        [string]$From,

        [string]$To
    )

    if (-not $Edges.Contains($From)) {
        $Edges[$From] = @{}
    }
    if (-not [string]::IsNullOrWhiteSpace($To)) {
        $Edges[$From][$To] = $true
    }
}

function Add-CollapsedExternalDependencies {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EntryKey,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Entries,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$NameToKey,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ExternalRefByEntryKey,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Results,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Visiting
    )

    if ($Visiting.Contains($EntryKey)) {
        return
    }
    $Visiting[$EntryKey] = $true

    $entry = $Entries[$EntryKey]
    $dependenciesProperty = $entry.Value.PSObject.Properties['dependencies']
    if ($null -ne $dependenciesProperty -and $null -ne $dependenciesProperty.Value) {
        foreach ($dependency in $dependenciesProperty.Value.PSObject.Properties) {
            $dependencyName = [string]$dependency.Name
            if (-not $NameToKey.Contains($dependencyName)) {
                continue
            }

            $dependencyKey = [string]$NameToKey[$dependencyName]
            if ($ExternalRefByEntryKey.Contains($dependencyKey)) {
                $Results[[string]$ExternalRefByEntryKey[$dependencyKey]] = $true
            }
            elseif ([string]::Equals(
                    [string]$Entries[$dependencyKey].Type,
                    'project',
                    [StringComparison]::OrdinalIgnoreCase)) {
                Add-CollapsedExternalDependencies -EntryKey $dependencyKey -Entries $Entries `
                    -NameToKey $NameToKey -ExternalRefByEntryKey $ExternalRefByEntryKey `
                    -Results $Results -Visiting $Visiting
            }
        }
    }

    [void]$Visiting.Remove($EntryKey)
}

function Assert-MatchingComponentMetadata {
    param(
        [Parameter(Mandatory = $true)][psobject]$Existing,
        [Parameter(Mandatory = $true)][psobject]$Candidate
    )

    $existingLicense = $Existing.License | ConvertTo-Json -Compress -Depth 10
    $candidateLicense = $Candidate.License | ConvertTo-Json -Compress -Depth 10
    $existingRepository = $Existing.Repository | ConvertTo-Json -Compress -Depth 10
    $candidateRepository = $Candidate.Repository | ConvertTo-Json -Compress -Depth 10
    if (-not [string]::Equals($Existing.Sha512, $Candidate.Sha512, [StringComparison]::Ordinal) -or
        -not [string]::Equals($Existing.Sha256, $Candidate.Sha256, [StringComparison]::Ordinal) -or
        -not [string]::Equals($existingLicense, $candidateLicense, [StringComparison]::Ordinal) -or
        -not [string]::Equals($existingRepository, $candidateRepository, [StringComparison]::Ordinal)) {
        throw "Conflicting NuGet metadata was found for $($Existing.BomRef)."
    }
}

function Add-RuntimeGraph {
    param(
        [Parameter(Mandatory = $true)][string]$Scope,
        [Parameter(Mandatory = $true)][string]$RootBomRef,
        [Parameter(Mandatory = $true)][string]$DepsJsonPath,
        [Parameter(Mandatory = $true)][string]$DepsSha256,
        [Parameter(Mandatory = $true)][string[]]$PackageRoots,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Components,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Edges
    )

    $deps = Get-JsonFile -Path $DepsJsonPath -ParameterName "$Scope deps.json"
    if ($null -eq $deps.runtimeTarget -or [string]::IsNullOrWhiteSpace([string]$deps.runtimeTarget.name)) {
        throw "$Scope deps.json does not declare runtimeTarget.name."
    }
    $runtimeTarget = [string]$deps.runtimeTarget.name
    $expectedRuntimeTarget = '.NETCoreApp,Version=v10.0/win-x64'
    if (-not [string]::Equals(
            $runtimeTarget,
            $expectedRuntimeTarget,
            [StringComparison]::Ordinal)) {
        throw "$Scope deps.json runtime target must be '$expectedRuntimeTarget'; found '$runtimeTarget'."
    }
    if ($DepsSha256 -cnotmatch '^[A-F0-9]{64}$') {
        throw "$Scope deps.json SHA-256 is invalid."
    }
    $targetProperty = $deps.targets.PSObject.Properties[$runtimeTarget]
    if ($null -eq $targetProperty) {
        throw "$Scope deps.json does not contain runtime target '$runtimeTarget'."
    }

    $entries = @{}
    $nameToKey = @{}
    foreach ($targetEntry in $targetProperty.Value.PSObject.Properties) {
        $key = [string]$targetEntry.Name
        $parts = Split-LibraryKey -Key $key
        $libraryProperty = $deps.libraries.PSObject.Properties[$key]
        if ($null -eq $libraryProperty) {
            throw "$Scope runtime target entry '$key' has no libraries metadata."
        }
        $type = [string]$libraryProperty.Value.type
        if ($nameToKey.Contains($parts.Id)) {
            throw "$Scope runtime target contains multiple versions of '$($parts.Id)'."
        }
        $entry = [pscustomobject]@{
            Key = $key
            Id = $parts.Id
            Version = $parts.Version
            Type = $type
            Value = $targetEntry.Value
            Library = $libraryProperty.Value
        }
        $entries[$key] = $entry
        $nameToKey[$parts.Id] = $key
    }

    $externalRefByEntryKey = @{}
    foreach ($entryKey in (Get-SortedKeys -Dictionary $entries)) {
        $entry = $entries[$entryKey]
        if (-not [string]::Equals($entry.Type, 'package', [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($entry.Type, 'runtimepack', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $metadata = Get-NuGetMetadata -Id $entry.Id -Version $entry.Version `
            -LibraryType $entry.Type -Library $entry.Library -PackageRoots $PackageRoots
        $bomRef = New-NuGetBomRef -Id $metadata.Id -Version $metadata.Version
        $candidate = [pscustomobject]@{
            BomRef = $bomRef
            Type = $entry.Type.ToLowerInvariant()
            Name = $metadata.Id
            Version = $metadata.Version
            Sha256 = $null
            Sha512 = $metadata.Sha512
            License = $metadata.License
            Repository = $metadata.Repository
            Scopes = @{}
        }

        if ($Components.Contains($bomRef)) {
            Assert-MatchingComponentMetadata -Existing $Components[$bomRef] -Candidate $candidate
        }
        else {
            $Components[$bomRef] = $candidate
        }
        $Components[$bomRef].Scopes[$Scope] = $true
        $externalRefByEntryKey[$entryKey] = $bomRef
        Add-Edge -Edges $Edges -From $bomRef
    }

    foreach ($entryKey in (Get-SortedKeys -Dictionary $externalRefByEntryKey)) {
        $dependencies = @{}
        Add-CollapsedExternalDependencies -EntryKey $entryKey -Entries $entries `
            -NameToKey $nameToKey -ExternalRefByEntryKey $externalRefByEntryKey `
            -Results $dependencies -Visiting @{}
        foreach ($dependencyRef in (Get-SortedKeys -Dictionary $dependencies)) {
            Add-Edge -Edges $Edges -From ([string]$externalRefByEntryKey[$entryKey]) -To $dependencyRef
        }
    }

    $depsFileName = [IO.Path]::GetFileName((Get-ExistingFile `
        -Path $DepsJsonPath -ParameterName "$Scope deps.json").FullName)
    $rootProjectName = $depsFileName -replace '(?i)\.deps\.json$', ''
    if (-not $nameToKey.Contains($rootProjectName)) {
        throw "$Scope deps.json root project '$rootProjectName' is absent from its runtime target."
    }
    $rootEntryKey = [string]$nameToKey[$rootProjectName]
    if (-not [string]::Equals($entries[$rootEntryKey].Type, 'project', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$scope deps.json root '$rootProjectName' is not a project runtime entry."
    }

    Add-Edge -Edges $Edges -From $RootBomRef
    $rootDependencies = @{}
    Add-CollapsedExternalDependencies -EntryKey $rootEntryKey -Entries $entries `
        -NameToKey $nameToKey -ExternalRefByEntryKey $externalRefByEntryKey `
        -Results $rootDependencies -Visiting @{}
    foreach ($dependencyRef in (Get-SortedKeys -Dictionary $rootDependencies)) {
        Add-Edge -Edges $Edges -From $RootBomRef -To $dependencyRef
    }

    $componentRefs = @{}
    foreach ($componentRef in $externalRefByEntryKey.Values) {
        $componentRefs[[string]$componentRef] = $true
    }
    return [pscustomobject]@{
        Name = $Scope
        RuntimeTarget = $runtimeTarget
        DepsFileName = $depsFileName
        DepsSha256 = $DepsSha256
        RootBomRef = $RootBomRef
        ComponentRefs = @(Get-SortedKeys -Dictionary $componentRefs)
    }
}

function New-ReleaseComponent {
    param([Parameter(Mandatory = $true)][psobject]$Component)

    return [ordered]@{
        bomRef = $Component.BomRef
        type = $Component.Type
        name = $Component.Name
        version = $Component.Version
        sha256 = $Component.Sha256
        sha512 = $Component.Sha512
        runtimeScopes = @(Get-SortedKeys -Dictionary $Component.Scopes)
        license = $Component.License
        repository = $Component.Repository
    }
}

function New-CycloneDxComponent {
    param([Parameter(Mandatory = $true)][psobject]$Component)

    $cycloneType = 'library'
    if ([string]::Equals($Component.Type, 'runtimepack', [StringComparison]::OrdinalIgnoreCase)) {
        $cycloneType = 'framework'
    }
    elseif ([string]::Equals($Component.Type, 'file', [StringComparison]::OrdinalIgnoreCase)) {
        $cycloneType = 'file'
    }
    $result = [ordered]@{
        type = $cycloneType
        'bom-ref' = $Component.BomRef
        name = $Component.Name
        version = $Component.Version
        scope = 'required'
        purl = $Component.BomRef
    }
    $hashes = New-Object 'Collections.Generic.List[object]'
    if (-not [string]::IsNullOrWhiteSpace($Component.Sha256)) {
        [void]$hashes.Add([ordered]@{
            alg = 'SHA-256'
            content = $Component.Sha256
        })
    }
    if (-not [string]::IsNullOrWhiteSpace($Component.Sha512)) {
        [void]$hashes.Add([ordered]@{
            alg = 'SHA-512'
            content = $Component.Sha512
        })
    }
    if ($hashes.Count -gt 0) {
        $result.hashes = $hashes.ToArray()
    }

    $properties = New-Object 'Collections.Generic.List[object]'
    [void]$properties.Add([ordered]@{
        name = 'ezyImageViewer:runtimeScopes'
        value = ((Get-SortedKeys -Dictionary $Component.Scopes) -join ',')
    })

    if ($null -ne $Component.License) {
        if ([string]::Equals($Component.License.kind, 'expression', [StringComparison]::OrdinalIgnoreCase)) {
            $result.licenses = @([ordered]@{ expression = $Component.License.value })
        }
        elseif ([string]::Equals($Component.License.kind, 'url', [StringComparison]::OrdinalIgnoreCase)) {
            $result.licenses = @([ordered]@{
                license = [ordered]@{
                    name = 'NuGet license URL'
                    url = $Component.License.value
                }
            })
        }
        else {
            $result.licenses = @([ordered]@{
                license = [ordered]@{ name = "NuGet license $($Component.License.kind)" }
            })
            [void]$properties.Add([ordered]@{
                name = 'ezyImageViewer:licenseFile'
                value = $Component.License.value
            })
        }
    }

    if ($null -ne $Component.Repository) {
        $externalReference = [ordered]@{
            type = 'vcs'
            url = $Component.Repository.url
        }
        if (-not [string]::IsNullOrWhiteSpace($Component.Repository.commit)) {
            $externalReference.comment = "commit: $($Component.Repository.commit)"
        }
        $result.externalReferences = @($externalReference)
        if (-not [string]::IsNullOrWhiteSpace($Component.Repository.branch)) {
            [void]$properties.Add([ordered]@{
                name = 'ezyImageViewer:repositoryBranch'
                value = $Component.Repository.branch
            })
        }
    }

    $result.properties = $properties.ToArray()
    return $result
}

Assert-FourPartVersion -Value $Version -ParameterName 'Version'
Assert-FourPartVersion -Value $CodecHostVersion -ParameterName 'CodecHostVersion'

$outputDirectoryItem = Get-ExistingDirectory -Path $OutputDirectory -ParameterName 'OutputDirectory'
if (($outputDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputDirectory must not be a reparse point: '$($outputDirectoryItem.FullName)'."
}
$mainArtifact = Get-ExistingFile -Path $MainMsix -ParameterName 'MainMsix'
$codecHostArtifact = Get-ExistingFile -Path $CodecHostMsix -ParameterName 'CodecHostMsix'
$releaseArtifacts = New-Object 'Collections.Generic.List[IO.FileInfo]'
[void]$releaseArtifacts.Add($mainArtifact)
[void]$releaseArtifacts.Add($codecHostArtifact)
$appInstallerArtifact = $null
if (-not [string]::IsNullOrWhiteSpace($AppInstallerFile)) {
    $appInstallerArtifact = Get-ExistingFile `
        -Path $AppInstallerFile -ParameterName 'AppInstallerFile'
    [void]$releaseArtifacts.Add($appInstallerArtifact)
}
foreach ($artifact in $releaseArtifacts) {
    $expectedExtension = if ($artifact -eq $appInstallerArtifact) {
        '.appinstaller'
    }
    else {
        '.msix'
    }
    if (-not [string]::Equals(
            $artifact.Extension,
            $expectedExtension,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release artifact must have $expectedExtension extension: '$($artifact.FullName)'."
    }
    if (-not (Test-PathWithinDirectory -Path $artifact.FullName -Directory $outputDirectoryItem.FullName)) {
        throw "Release artifact must be inside OutputDirectory: '$($artifact.FullName)'."
    }
    if (-not [string]::Equals(
            (Split-Path $artifact.FullName -Parent),
            $outputDirectoryItem.FullName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release artifact must be a direct child of OutputDirectory: '$($artifact.FullName)'."
    }
    if (($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release artifact must not be a reparse point: '$($artifact.FullName)'."
    }
}

$artifactNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($artifact in $releaseArtifacts) {
    if (-not $artifactNames.Add($artifact.Name)) {
        throw "Release artifact basenames must be unique: '$($artifact.Name)'."
    }
}

$repo = Split-Path $PSScriptRoot -Parent
$mainDepsItem = Get-ExistingFile -Path $MainDepsJson -ParameterName 'MainDepsJson'
$codecHostDepsItem = Get-ExistingFile -Path $CodecHostDepsJson -ParameterName 'CodecHostDepsJson'
if (-not [string]::Equals(
        $mainDepsItem.Name,
        'ezyImageViewer.deps.json',
        [StringComparison]::Ordinal)) {
    throw "MainDepsJson must be named 'ezyImageViewer.deps.json'."
}
if (-not [string]::Equals(
        $codecHostDepsItem.Name,
        'EzyImageViewer.CodecHost.deps.json',
        [StringComparison]::Ordinal)) {
    throw "CodecHostDepsJson must be named 'EzyImageViewer.CodecHost.deps.json'."
}
$mainDepsSha256 = Assert-MsixEntryMatchesFile -PackagePath $mainArtifact.FullName `
    -EntryName $mainDepsItem.Name -FilePath $mainDepsItem.FullName -PackageLabel 'Main MSIX'
$codecHostDepsSha256 = Assert-MsixEntryMatchesFile -PackagePath $codecHostArtifact.FullName `
    -EntryName $codecHostDepsItem.Name -FilePath $codecHostDepsItem.FullName `
    -PackageLabel 'CodecHost MSIX'

$fontSourcePath = Join-Path $repo 'EzyImageViewer.App\Assets\Fonts\MaterialSymbolsOutlined.ttf'
$fontLicenseSourcePath = Join-Path $repo 'EzyImageViewer.App\Assets\Fonts\LICENSE-MaterialSymbols.txt'
$fontSha256 = Assert-MsixEntryMatchesFile -PackagePath $mainArtifact.FullName `
    -EntryName 'Assets/Fonts/MaterialSymbolsOutlined.ttf' -FilePath $fontSourcePath `
    -PackageLabel 'Main MSIX'
$expectedFontSha256 = '6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F'
if (-not [string]::Equals($fontSha256, $expectedFontSha256, [StringComparison]::Ordinal)) {
    throw 'Main MSIX Material Symbols font does not match the pinned SHA-256.'
}
[void](Assert-MsixEntryMatchesFile -PackagePath $mainArtifact.FullName `
    -EntryName 'Assets/Fonts/LICENSE-MaterialSymbols.txt' `
    -FilePath $fontLicenseSourcePath -PackageLabel 'Main MSIX')

$packageRoots = @(Get-NuGetPackageRoots)
$components = @{}
$edges = @{}
$mainBomRef = 'pkg:generic/GRTech/ezyImageViewer@' + [Uri]::EscapeDataString($Version)
$codecHostBomRef = 'pkg:generic/GRTech/ezyImageViewer.CodecHost@' +
    [Uri]::EscapeDataString($CodecHostVersion)
$mainRuntime = Add-RuntimeGraph -Scope 'main' -RootBomRef $mainBomRef `
    -DepsJsonPath $mainDepsItem.FullName -DepsSha256 $mainDepsSha256 `
    -PackageRoots $packageRoots `
    -Components $components -Edges $edges
$codecHostRuntime = Add-RuntimeGraph -Scope 'codec-host' -RootBomRef $codecHostBomRef `
    -DepsJsonPath $codecHostDepsItem.FullName -DepsSha256 $codecHostDepsSha256 `
    -PackageRoots $packageRoots `
    -Components $components -Edges $edges
Add-Edge -Edges $edges -From $mainBomRef -To $codecHostBomRef

$fontCommit = 'abd7f5c0e179c83f068c770650bd14ebac5d5a09'
$fontBomRef = 'pkg:generic/google/MaterialSymbolsOutlined@' + $fontCommit
$components[$fontBomRef] = [pscustomobject]@{
    BomRef = $fontBomRef
    Type = 'file'
    Name = 'Material Symbols Outlined'
    Version = $fontCommit
    Sha256 = $fontSha256
    Sha512 = $null
    License = [ordered]@{
        kind = 'expression'
        value = 'Apache-2.0'
    }
    Repository = [ordered]@{
        url = 'https://github.com/google/material-design-icons'
        commit = $fontCommit
        branch = $null
    }
    Scopes = @{ main = $true }
}
Add-Edge -Edges $edges -From $fontBomRef
Add-Edge -Edges $edges -From $mainBomRef -To $fontBomRef
[string[]]$mainRuntimeComponentRefs = @($mainRuntime.ComponentRefs) + @($fontBomRef)
[Array]::Sort($mainRuntimeComponentRefs, [StringComparer]::Ordinal)

$artifactsByName = @{}
$artifactsByName[$mainArtifact.Name] = [pscustomobject]@{
    Role = 'main'
    Version = $Version
    File = $mainArtifact
}
$artifactsByName[$codecHostArtifact.Name] = [pscustomobject]@{
    Role = 'codec-host'
    Version = $CodecHostVersion
    File = $codecHostArtifact
}
if ($null -ne $appInstallerArtifact) {
    $artifactsByName[$appInstallerArtifact.Name] = [pscustomobject]@{
        Role = 'app-installer'
        Version = $Version
        File = $appInstallerArtifact
    }
}

$checksumLines = New-Object 'Collections.Generic.List[string]'
$artifactRecords = New-Object 'Collections.Generic.List[object]'
foreach ($fileName in (Get-SortedKeys -Dictionary $artifactsByName)) {
    $artifact = $artifactsByName[$fileName]
    $hash = (Get-FileHash -LiteralPath $artifact.File.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    [void]$checksumLines.Add("$hash  $fileName")
    [void]$artifactRecords.Add([ordered]@{
        role = $artifact.Role
        fileName = $fileName
        version = $artifact.Version
        size = [long]$artifact.File.Length
        sha256 = $hash
    })
}

$checksumsPath = Join-Path $outputDirectoryItem.FullName 'SHA256SUMS.txt'
Write-DeterministicText -Path $checksumsPath -Content ($checksumLines -join "`n")

$verifiedLines = [IO.File]::ReadAllLines($checksumsPath)
if ($verifiedLines.Length -ne $artifactsByName.Count) {
    throw "SHA256SUMS.txt entry count changed after writing."
}
for ($index = 0; $index -lt $verifiedLines.Length; $index++) {
    if ($verifiedLines[$index] -cnotmatch '^([A-F0-9]{64})  (.+)$') {
        throw "Invalid SHA256SUMS.txt line: '$($verifiedLines[$index])'."
    }
    $expectedHash = $Matches[1]
    $fileName = $Matches[2]
    if (-not $artifactsByName.Contains($fileName)) {
        throw "SHA256SUMS.txt contains an unspecified artifact: '$fileName'."
    }
    $actualHash = (Get-FileHash -LiteralPath $artifactsByName[$fileName].File.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::Ordinal)) {
        throw "SHA-256 verification failed for '$fileName'."
    }
    if (-not [string]::Equals($verifiedLines[$index], $checksumLines[$index], [StringComparison]::Ordinal)) {
        throw 'SHA256SUMS.txt ordering or content changed after writing.'
    }
}

$releaseComponents = New-Object 'Collections.Generic.List[object]'
$cycloneComponents = New-Object 'Collections.Generic.List[object]'
foreach ($componentRef in (Get-SortedKeys -Dictionary $components)) {
    [void]$releaseComponents.Add((New-ReleaseComponent -Component $components[$componentRef]))
    [void]$cycloneComponents.Add((New-CycloneDxComponent -Component $components[$componentRef]))
}

$dependencyRecords = New-Object 'Collections.Generic.List[object]'
foreach ($fromRef in (Get-SortedKeys -Dictionary $edges)) {
    [void]$dependencyRecords.Add([ordered]@{
        ref = $fromRef
        dependsOn = @(Get-SortedKeys -Dictionary $edges[$fromRef])
    })
}

$runtimeRecords = @(
    [ordered]@{
        name = $mainRuntime.Name
        runtimeTarget = $mainRuntime.RuntimeTarget
        depsFileName = $mainRuntime.DepsFileName
        depsSha256 = $mainRuntime.DepsSha256
        rootBomRef = $mainRuntime.RootBomRef
        components = $mainRuntimeComponentRefs
    },
    [ordered]@{
        name = $codecHostRuntime.Name
        runtimeTarget = $codecHostRuntime.RuntimeTarget
        depsFileName = $codecHostRuntime.DepsFileName
        depsSha256 = $codecHostRuntime.DepsSha256
        rootBomRef = $codecHostRuntime.RootBomRef
        components = @($codecHostRuntime.ComponentRefs)
    }
)

$releaseManifest = [ordered]@{
    schemaVersion = 1
    product = [ordered]@{
        name = 'ezyImageViewer'
        version = $Version
        codecHostVersion = $CodecHostVersion
        mainBomRef = $mainBomRef
        codecHostBomRef = $codecHostBomRef
    }
    artifacts = $artifactRecords.ToArray()
    runtimes = $runtimeRecords
    components = $releaseComponents.ToArray()
    dependencies = $dependencyRecords.ToArray()
}

$mainArtifactRecord = $artifactRecords | Where-Object { $_.role -eq 'main' }
$codecHostArtifactRecord = $artifactRecords | Where-Object { $_.role -eq 'codec-host' }
$mainCycloneComponent = [ordered]@{
    type = 'application'
    'bom-ref' = $mainBomRef
    group = 'GRTech'
    name = 'ezyImageViewer'
    version = $Version
    hashes = @([ordered]@{
        alg = 'SHA-256'
        content = $mainArtifactRecord.sha256
    })
    properties = @([ordered]@{
        name = 'ezyImageViewer:artifact'
        value = $mainArtifactRecord.fileName
    })
}
$codecHostCycloneComponent = [ordered]@{
    type = 'application'
    'bom-ref' = $codecHostBomRef
    group = 'GRTech'
    name = 'ezyImageViewer.CodecHost'
    version = $CodecHostVersion
    hashes = @([ordered]@{
        alg = 'SHA-256'
        content = $codecHostArtifactRecord.sha256
    })
    properties = @([ordered]@{
        name = 'ezyImageViewer:artifact'
        value = $codecHostArtifactRecord.fileName
    })
}

$allCycloneComponents = New-Object 'Collections.Generic.List[object]'
[void]$allCycloneComponents.Add($codecHostCycloneComponent)
foreach ($component in $cycloneComponents) {
    [void]$allCycloneComponents.Add($component)
}

$cycloneDx = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{
        component = $mainCycloneComponent
    }
    components = $allCycloneComponents.ToArray()
    dependencies = $dependencyRecords.ToArray()
}

$releaseManifestPath = Join-Path $outputDirectoryItem.FullName 'release-manifest.json'
$sbomPath = Join-Path $outputDirectoryItem.FullName 'sbom.cdx.json'
Write-DeterministicText -Path $releaseManifestPath `
    -Content ($releaseManifest | ConvertTo-Json -Depth 100)
Write-DeterministicText -Path $sbomPath `
    -Content ($cycloneDx | ConvertTo-Json -Depth 100)

[void](Get-Content -LiteralPath $releaseManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
$verifiedSbom = Get-Content -LiteralPath $sbomPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($verifiedSbom.bomFormat -ne 'CycloneDX' -or $verifiedSbom.specVersion -ne '1.6') {
    throw 'Generated SBOM does not declare CycloneDX 1.6.'
}

Write-Output "checksums: $checksumsPath"
Write-Output "release manifest: $releaseManifestPath"
Write-Output "CycloneDX SBOM: $sbomPath"
