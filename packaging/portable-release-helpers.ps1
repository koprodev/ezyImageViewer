Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-EzyPortableVersion {
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-portable\.(0|[1-9][0-9]*)$') {
        throw "Portable Version must use '<major>.<minor>.<patch>-portable.<number>': '$Version'."
    }
}

function Get-EzyPortableNumericVersion {
    param([Parameter(Mandatory)][string]$Version)

    Assert-EzyPortableVersion $Version
    $core = $Version.Split('-')[0].Split('.')
    return "$($core[0]).$($core[1]).$($core[2]).0"
}

function Assert-EzyPortableSourceState {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $allowedDirtyPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($relativePath in @(
            'AGENTS.md',
            'CLAUDE.md',
            'GEMINI.md',
            'PingPong.md',
            'PingPong_Checklist.md')) {
        [void]$allowedDirtyPaths.Add($relativePath)
    }

    $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'git status failed while validating portable release provenance.'
    }
    $unexpected = New-Object 'Collections.Generic.List[string]'
    foreach ($line in $status) {
        if ($line.Length -lt 4) {
            throw "Unexpected git status line: '$line'."
        }
        $relativePath = $line.Substring(3).Replace('\', '/')
        if (-not $allowedDirtyPaths.Contains($relativePath)) {
            [void]$unexpected.Add($relativePath)
        }
    }
    if ($unexpected.Count -ne 0) {
        throw "Portable release source contains uncommitted public files: $($unexpected -join ', ')."
    }

    $commit = @(& git -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $commit.Count -ne 1 -or
        $commit[0] -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the portable release source commit.'
    }
    return $commit[0]
}

function Test-EzyPathWithinDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Directory
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    return $fullPath.StartsWith($fullDirectory, [StringComparison]::OrdinalIgnoreCase)
}

function Get-EzyXmlChildText {
    param(
        [Parameter(Mandatory)][Xml.XmlNode]$Parent,
        [Parameter(Mandatory)][string]$Name
    )

    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        return $null
    }
    return $node.InnerText.Trim()
}

function Copy-EzyPortableThirdPartyFiles {
    param(
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$DepsJson,
        [Parameter(Mandatory)][string]$ProjectAssetsJson
    )

    $payload = Get-Item -LiteralPath ([IO.Path]::GetFullPath($PayloadDirectory)) -Force
    if (-not $payload.PSIsContainer -or
        ($payload.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Portable payload must be a physical directory.'
    }
    $depsPath = Get-Item -LiteralPath ([IO.Path]::GetFullPath($DepsJson)) -Force
    $assetsPath = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ProjectAssetsJson)) -Force
    if ($depsPath.PSIsContainer -or $assetsPath.PSIsContainer) {
        throw 'Portable dependency metadata must be physical files.'
    }

    $deps = [IO.File]::ReadAllText($depsPath.FullName) | ConvertFrom-Json
    $assets = [IO.File]::ReadAllText($assetsPath.FullName) | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    if ($packageRoots.Count -eq 0) {
        throw 'Portable project assets contain no NuGet package roots.'
    }

    $licenseRoot = Join-Path $payload.FullName 'THIRD-PARTY-LICENSES'
    if ([IO.Directory]::Exists($licenseRoot) -or [IO.File]::Exists($licenseRoot)) {
        throw 'Portable payload already contains THIRD-PARTY-LICENSES.'
    }
    [void][IO.Directory]::CreateDirectory($licenseRoot)

    $records = New-Object 'Collections.Generic.List[object]'
    $libraries = @($deps.libraries.PSObject.Properties | Where-Object {
            [string]$_.Value.type -ceq 'package'
        })
    [Array]::Sort($libraries, [Comparison[object]]{
            param($left, $right)
            [StringComparer]::Ordinal.Compare($left.Name, $right.Name)
        })

    foreach ($library in $libraries) {
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -le 0 -or $separator -eq $library.Name.Length - 1) {
            throw "Invalid package identity in portable deps.json: '$($library.Name)'."
        }
        $id = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        if ($id -cnotmatch '^[A-Za-z0-9_.-]+$' -or
            $version -cnotmatch '^[A-Za-z0-9+_.-]+$') {
            throw "Unsafe package identity in portable deps.json: '$($library.Name)'."
        }

        $relativePackagePath = "$($id.ToLowerInvariant())\$version"
        $packageCandidates = @($packageRoots | ForEach-Object {
                Join-Path $_ $relativePackagePath
            } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if ($packageCandidates.Count -ne 1) {
            throw "Expected one restored package directory for '$($library.Name)'; found $($packageCandidates.Count)."
        }
        $packageDirectory = (Get-Item -LiteralPath $packageCandidates[0] -Force).FullName
        $nuspecs = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nuspec' -Force)
        if ($nuspecs.Count -ne 1) {
            throw "Expected one nuspec for '$($library.Name)'; found $($nuspecs.Count)."
        }
        [xml]$nuspec = [IO.File]::ReadAllText($nuspecs[0].FullName)
        $metadata = $nuspec.SelectSingleNode(
            "/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw "Package metadata is missing for '$($library.Name)'."
        }

        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $licenseKind = $null
        $licenseValue = $null
        if ($null -ne $licenseNode -and
            -not [string]::IsNullOrWhiteSpace($licenseNode.InnerText)) {
            $licenseTypeAttribute = $licenseNode.Attributes['type']
            if ($null -ne $licenseTypeAttribute) {
                $licenseKind = $licenseTypeAttribute.Value
            }
            if ([string]::IsNullOrWhiteSpace($licenseKind)) {
                $licenseKind = 'unspecified'
            }
            $licenseValue = $licenseNode.InnerText.Trim()
        }
        else {
            $licenseValue = Get-EzyXmlChildText $metadata 'licenseUrl'
            if (-not [string]::IsNullOrWhiteSpace($licenseValue)) {
                $licenseKind = 'url'
            }
        }
        if ([string]::IsNullOrWhiteSpace($licenseValue)) {
            throw "Package '$($library.Name)' does not declare license metadata."
        }

        $sourceFiles = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($candidate in @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Force)) {
            if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Package '$($library.Name)' contains a reparse point."
            }
            if ($candidate.Name -match '^(?i:LICENSE|NOTICE|THIRD[-_]?PARTY[-_]?NOTICES?)(?:\..+)?$') {
                $sourceFiles[$candidate.FullName] = $candidate
            }
        }
        if ([string]::Equals($licenseKind, 'file', [StringComparison]::OrdinalIgnoreCase)) {
            $declaredLicense = [IO.Path]::GetFullPath((Join-Path $packageDirectory $licenseValue))
            if (-not (Test-EzyPathWithinDirectory $declaredLicense $packageDirectory) -or
                -not [IO.File]::Exists($declaredLicense)) {
                throw "Declared license file is missing for '$($library.Name)': '$licenseValue'."
            }
            $sourceFiles[$declaredLicense] = Get-Item -LiteralPath $declaredLicense -Force
        }

        $copied = New-Object 'Collections.Generic.List[object]'
        $orderedSources = @($sourceFiles.Values)
        [Array]::Sort($orderedSources, [Comparison[object]]{
                param($left, $right)
                [StringComparer]::Ordinal.Compare($left.FullName, $right.FullName)
            })
        foreach ($source in $orderedSources) {
            $relativeSource = $source.FullName.Substring(
                $packageDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
            $destinationRelative = "$id/$version/$relativeSource"
            $destination = Join-Path $licenseRoot $destinationRelative.Replace('/', '\')
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($source.FullName, $destination, $false)
            [void]$copied.Add([ordered]@{
                    path = $destinationRelative
                    sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant()
                })
        }

        [void]$records.Add([ordered]@{
                id = $id
                version = $version
                license = [ordered]@{
                    kind = $licenseKind.ToLowerInvariant()
                    value = $licenseValue
                }
                projectUrl = Get-EzyXmlChildText $metadata 'projectUrl'
                copiedFiles = $copied.ToArray()
            })
    }

    $index = [ordered]@{
        schemaVersion = 1
        generatedFrom = [IO.Path]::GetFileName($depsPath.FullName)
        packages = $records.ToArray()
    }
    $indexPath = Join-Path $licenseRoot 'INDEX.json'
    [IO.File]::WriteAllText(
        $indexPath,
        (($index | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
    return $indexPath
}

function New-EzyPortableZip {
    param(
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$RootDirectoryName
    )

    if ($RootDirectoryName -cnotmatch '^[A-Za-z0-9_.-]+$') {
        throw "Unsafe portable archive root: '$RootDirectoryName'."
    }
    $root = Get-Item -LiteralPath ([IO.Path]::GetFullPath($PayloadDirectory)) -Force
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    if ([IO.File]::Exists($archive) -or [IO.Directory]::Exists($archive)) {
        throw "Portable archive target already exists: '$archive'."
    }
    $files = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -File -Force)
    $prefix = $root.FullName.TrimEnd('\') + '\'
    [Array]::Sort($files, [Comparison[object]]{
            param($left, $right)
            [StringComparer]::Ordinal.Compare($left.FullName, $right.FullName)
        })

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
    try {
        $zip = [IO.Compression.ZipArchive]::new(
            $stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Portable payload contains a reparse point: '$($file.FullName)'."
                }
                $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                $entry = $zip.CreateEntry(
                    "$RootDirectoryName/$relative",
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $source = $file.OpenRead()
                $destination = $entry.Open()
                try {
                    $source.CopyTo($destination)
                }
                finally {
                    $destination.Dispose()
                    $source.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
