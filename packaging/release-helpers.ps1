# 릴리스 패키징·검증용 공용 닫힘 우선 도우미.

function Get-EzyPinnedBuildToolsRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [string[]]$ProjectAssetsPaths = @(),

        [string]$ExplicitRoot
    )

    [xml]$packageVersions = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'Directory.Packages.props')
    $versions = @($packageVersions.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq 'Microsoft.Windows.SDK.BuildTools' } |
        ForEach-Object { [string]$_.Version })
    if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0])) {
        throw "Microsoft.Windows.SDK.BuildTools must have exactly one pinned version."
    }
    $version = $versions[0]

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        $item = Get-Item -LiteralPath $ExplicitRoot -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "BuildToolsRoot must be a physical directory: '$ExplicitRoot'."
        }
        if (-not [string]::Equals(
                $item.Name,
                $version,
                [StringComparison]::Ordinal)) {
            throw "BuildToolsRoot must point to pinned version $($version): '$($item.FullName)'."
        }
        return $item.FullName
    }

    $packageRoots = New-Object 'Collections.Generic.List[string]'
    foreach ($assetsPath in $ProjectAssetsPaths) {
        if ([string]::IsNullOrWhiteSpace($assetsPath) -or
            -not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
            continue
        }
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        if ($null -eq $assets.packageFolders) {
            continue
        }
        foreach ($property in $assets.packageFolders.PSObject.Properties) {
            if (-not [string]::IsNullOrWhiteSpace([string]$property.Name)) {
                [void]$packageRoots.Add([string]$property.Name)
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        [void]$packageRoots.Add($env:NUGET_PACKAGES)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        [void]$packageRoots.Add((Join-Path $env:USERPROFILE '.nuget\packages'))
    }

    $candidateSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($packageRoot in $packageRoots) {
        try {
            $candidate = Join-Path ([IO.Path]::GetFullPath($packageRoot)) (
                "microsoft.windows.sdk.buildtools\$version")
            if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
                continue
            }
            $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "BuildTools package root must not be a reparse point: '$($item.FullName)'."
            }
            [void]$candidateSet.Add($item.FullName)
        }
        catch [ArgumentException] {
            throw "Invalid NuGet package root '$packageRoot'."
        }
        catch [NotSupportedException] {
            throw "Invalid NuGet package root '$packageRoot'."
        }
    }

    $candidates = @($candidateSet)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one restored BuildTools $version root; found $($candidates.Count)."
    }
    return $candidates[0]
}

function Assert-EzyProductionTimestampUrl {
    param(
        [Parameter(Mandatory = $true)]
        [uri]$TimestampUrl
    )

    if (-not $TimestampUrl.IsAbsoluteUri -or
        -not [string]::Equals(
            $TimestampUrl.Scheme,
            [Uri]::UriSchemeHttps,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace($TimestampUrl.Host) -or
        -not [string]::IsNullOrEmpty($TimestampUrl.UserInfo) -or
        -not [string]::IsNullOrEmpty($TimestampUrl.Fragment)) {
        throw 'TimestampUrl must be an absolute HTTPS URL without credentials or a fragment.'
    }
}

function Test-EzyCodeSigningCertificate {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -isnot
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            continue
        }
        foreach ($usage in $extension.EnhancedKeyUsages) {
            if ($usage.Value -ceq '1.3.6.1.5.5.7.3.3') {
                return $true
            }
        }
    }
    return $false
}

function Test-EzyDistinguishedNameComponent {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X500DistinguishedName]$DistinguishedName,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedComponent
    )

    $formatted = $DistinguishedName.Name
    $componentStart = 0
    $insideQuotes = $false
    for ($index = 0; $index -lt $formatted.Length; $index++) {
        $character = $formatted[$index]
        if ($character -eq '\' -and $index + 1 -lt $formatted.Length) {
            $index++
            continue
        }
        if ($character -eq '"') {
            if ($insideQuotes -and $index + 1 -lt $formatted.Length -and
                $formatted[$index + 1] -eq '"') {
                $index++
                continue
            }
            $insideQuotes = -not $insideQuotes
            continue
        }
        if (-not $insideQuotes -and ($character -eq ',' -or $character -eq '+')) {
            $component = $formatted.Substring(
                $componentStart,
                $index - $componentStart).Trim()
            if ($component -ceq $ExpectedComponent) {
                return $true
            }
            $componentStart = $index + 1
        }
    }
    if ($insideQuotes) {
        return $false
    }
    return $formatted.Substring($componentStart).Trim() -ceq $ExpectedComponent
}

function Get-EzyMicrosoftSignTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BuildToolsRoot
    )

    $root = Get-Item -LiteralPath ([IO.Path]::GetFullPath($BuildToolsRoot)) `
        -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'BuildToolsRoot must be a physical directory.'
    }
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $root.FullName 'bin') `
            -Recurse -File -Filter signtool.exe -ErrorAction Stop | Where-Object {
            $_.Directory.Name -ceq 'x64'
        })
    if ($candidates.Count -ne 1 -or
        ($candidates[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Expected exactly one physical x64 signtool.exe; found $($candidates.Count)."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $candidates[0].FullName
    if ([string]$signature.Status -cne 'Valid' -or
        $null -eq $signature.SignerCertificate) {
        throw 'The selected x64 signtool.exe does not have a valid Windows-trusted signature.'
    }
    if (-not (Test-EzyDistinguishedNameComponent `
            -DistinguishedName $signature.SignerCertificate.SubjectName `
            -ExpectedComponent 'O=Microsoft Corporation') -or
        -not (Test-EzyCodeSigningCertificate $signature.SignerCertificate)) {
        throw 'The selected x64 signtool.exe is not signed by Microsoft for code signing.'
    }
    return $candidates[0].FullName
}

function Assert-EzySignatureEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Signature,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedThumbprint,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $normalizedThumbprint = $ExpectedThumbprint.Replace(' ', '').ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($normalizedThumbprint) -or
        [string]$Signature.Status -cne 'Valid' -or
        $null -eq $Signature.SignerCertificate -or
        $Signature.SignerCertificate.Thumbprint -cne $normalizedThumbprint -or
        $null -eq $Signature.TimeStamperCertificate) {
        throw "$Label must have a valid signature from the selected certificate and an RFC 3161 timestamp."
    }
}

function Assert-EzyArtifactSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedThumbprint
    )

    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Signed artifact must be a physical file: '$Path'."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
    Assert-EzySignatureEvidence -Signature $signature `
        -ExpectedThumbprint $ExpectedThumbprint -Label $item.Name
}

function Get-EzyFileListPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$IntermediateRoot
    )

    $root = Get-Item -LiteralPath $IntermediateRoot -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Intermediate output must be a physical directory: '$IntermediateRoot'."
    }
    $candidates = @(Get-ChildItem -LiteralPath $root.FullName `
        -Filter '*.FileListAbsolute.txt' -File -Force -ErrorAction Stop)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one MSBuild FileListAbsolute under '$($root.FullName)'; found $($candidates.Count)."
    }
    if (($candidates[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "MSBuild FileListAbsolute must not be a reparse point: '$($candidates[0].FullName)'."
    }
    return $candidates[0].FullName
}

function Test-EzyExcludedBuildFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    return $RelativePath.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) -or
        $RelativePath -match '(?i)(^|/)(ref|NativeAotProbe)(/|$)'
}

function Test-EzyPackageEnvelopeFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    return [string]::Equals(
            $RelativePath,
            'AppxBlockMap.xml',
            [StringComparison]::Ordinal) -or
        [string]::Equals(
            $RelativePath,
            '[Content_Types].xml',
            [StringComparison]::Ordinal) -or
        [string]::Equals(
            $RelativePath,
            'AppxSignature.p7x',
            [StringComparison]::Ordinal)
}

function Assert-EzyBuildOutputInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BuildOutput,

        [Parameter(Mandatory = $true)]
        [string]$FileListPath
    )

    $root = Get-Item -LiteralPath $BuildOutput -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Build output must be a physical directory: '$BuildOutput'."
    }
    $rootPrefix = $root.FullName.TrimEnd('\') + '\'
    $declared = @{}
    foreach ($line in [IO.File]::ReadAllLines($FileListPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $fullPath = [IO.Path]::GetFullPath($line)
        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $relative = $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
        if (Test-EzyExcludedBuildFile -RelativePath $relative) {
            continue
        }
        if ($declared.ContainsKey($relative)) {
            throw "MSBuild FileListAbsolute contains duplicate output '$relative'."
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "MSBuild-declared release output is missing: '$relative'."
        }
        $declared[$relative] = $true
    }

    $actual = @{}
    $items = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Build output must not contain a reparse point: '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        if (Test-EzyExcludedBuildFile -RelativePath $relative) {
            continue
        }
        if ($actual.ContainsKey($relative)) {
            throw "Build output contains a duplicate relative path '$relative'."
        }
        $actual[$relative] = $true
    }

    if ($actual.Count -eq 0 -or $actual.Count -ne $declared.Count) {
        throw "Build output inventory mismatch: declared $($declared.Count), actual $($actual.Count)."
    }
    foreach ($relative in $actual.Keys) {
        if (-not $declared.ContainsKey($relative)) {
            throw "Build output contains an undeclared or stale file: '$relative'."
        }
    }
    foreach ($relative in $declared.Keys) {
        if (-not $actual.ContainsKey($relative)) {
            throw "Build output is missing a declared file: '$relative'."
        }
    }
}

function Write-EzyPackageContentsManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Layout
    )

    $root = Get-Item -LiteralPath $Layout -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package layout must be a physical directory: '$Layout'."
    }
    $manifestName = 'PACKAGE-CONTENTS.sha256'
    $manifestPath = Join-Path $root.FullName $manifestName
    if (Test-Path -LiteralPath $manifestPath) {
        throw "Package layout already contains reserved inventory '$manifestName'."
    }
    $prefix = $root.FullName.TrimEnd('\') + '\'
    $paths = New-Object 'Collections.Generic.List[string]'
    foreach ($item in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Package layout must not contain a reparse point: '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($prefix.Length).Replace('\', '/')
        if ([string]::Equals($relative, $manifestName, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package layout contains a case-variant reserved inventory '$relative'."
        }
        [void]$paths.Add($relative)
    }
    $sorted = $paths.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $lines = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $sorted) {
        $fullPath = Join-Path $root.FullName $relative.Replace('/', '\')
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToUpperInvariant()
        [void]$lines.Add("$hash  $relative")
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        ($lines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
    return $manifestPath
}

function Assert-EzyPackageContentsManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UnpackedRoot,

        [Parameter(Mandatory = $true)]
        [string]$PackageLabel
    )

    $root = Get-Item -LiteralPath $UnpackedRoot -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$PackageLabel unpacked root must be a physical directory."
    }
    $manifestName = 'PACKAGE-CONTENTS.sha256'
    $manifestPath = Join-Path $root.FullName $manifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "$PackageLabel is missing $manifestName."
    }
    $prefix = $root.FullName.TrimEnd('\') + '\'
    $expected = @{}
    $orderedNames = New-Object 'Collections.Generic.List[string]'
    foreach ($line in [IO.File]::ReadAllLines($manifestPath)) {
        if ($line -cnotmatch '^([A-F0-9]{64})  ([^\\]+)$') {
            throw "$PackageLabel has an invalid $manifestName line: '$line'."
        }
        $hash = $Matches[1]
        $relative = $Matches[2]
        $segments = $relative.Split('/')
        if ($relative.StartsWith('/', [StringComparison]::Ordinal) -or
            $relative.Contains(':') -or
            $segments -contains '' -or
            $segments -contains '.' -or
            $segments -contains '..' -or
            [string]::Equals($relative, $manifestName, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$PackageLabel has an unsafe inventory path '$relative'."
        }
        if ($expected.ContainsKey($relative)) {
            throw "$PackageLabel has duplicate inventory path '$relative'."
        }
        $expected[$relative] = $hash
        [void]$orderedNames.Add($relative)
    }
    $sortedNames = $orderedNames.ToArray()
    [Array]::Sort($sortedNames, [StringComparer]::Ordinal)
    if ([string]::Join("`n", $orderedNames.ToArray()) -cne
        [string]::Join("`n", $sortedNames)) {
        throw "$PackageLabel $manifestName paths are not ordinally sorted."
    }

    $actual = @{}
    foreach ($item in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$PackageLabel contains a reparse point '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($prefix.Length).Replace('\', '/')
        if ([string]::Equals($relative, $manifestName, [StringComparison]::Ordinal)) {
            continue
        }
        if (Test-EzyPackageEnvelopeFile -RelativePath $relative) {
            continue
        }
        if ($actual.ContainsKey($relative)) {
            throw "$PackageLabel contains duplicate relative path '$relative'."
        }
        $actual[$relative] = $item.FullName
    }
    if ($expected.Count -eq 0 -or $expected.Count -ne $actual.Count) {
        throw "$PackageLabel content inventory count mismatch: expected $($expected.Count), actual $($actual.Count)."
    }
    foreach ($relative in $actual.Keys) {
        if (-not $expected.ContainsKey($relative)) {
            throw "$PackageLabel contains an unlisted file '$relative'."
        }
        $actualHash = (Get-FileHash -LiteralPath $actual[$relative] -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not [string]::Equals(
                $actualHash,
                [string]$expected[$relative],
                [StringComparison]::Ordinal)) {
            throw "$PackageLabel content hash mismatch for '$relative'."
        }
    }
    foreach ($relative in $expected.Keys) {
        if (-not $actual.ContainsKey($relative)) {
            throw "$PackageLabel is missing inventoried file '$relative'."
        }
    }
}

function Assert-EzyPackageMatchesBuildOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UnpackedRoot,

        [Parameter(Mandatory = $true)]
        [string]$BuildOutput,

        [Parameter(Mandatory = $true)]
        [string]$FileListPath,

        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$AdditionalSourceFiles,

        [Parameter(Mandatory = $true)]
        [string]$PackageLabel
    )

    Assert-EzyBuildOutputInventory -BuildOutput $BuildOutput -FileListPath $FileListPath
    $root = Get-Item -LiteralPath $UnpackedRoot -Force -ErrorAction Stop
    $buildRoot = Get-Item -LiteralPath $BuildOutput -Force -ErrorAction Stop
    $rootPrefix = $root.FullName.TrimEnd('\') + '\'
    $buildPrefix = $buildRoot.FullName.TrimEnd('\') + '\'
    $expected = @{}

    foreach ($item in @(Get-ChildItem -LiteralPath $buildRoot.FullName -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$PackageLabel build output contains a reparse point '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($buildPrefix.Length).Replace('\', '/')
        if (Test-EzyExcludedBuildFile -RelativePath $relative) {
            continue
        }
        if ($expected.ContainsKey($relative)) {
            throw "$PackageLabel build provenance contains duplicate path '$relative'."
        }
        $expected[$relative] = [pscustomobject]@{ Source = $item.FullName }
    }

    foreach ($relativeObject in $AdditionalSourceFiles.Keys) {
        $relative = [string]$relativeObject
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.Contains('\') -or
            $relative.StartsWith('/', [StringComparison]::Ordinal) -or
            $relative.Contains(':') -or
            $relative.Split('/') -contains '..') {
            throw "$PackageLabel has an unsafe additional provenance path '$relative'."
        }
        $source = Get-Item -LiteralPath ([string]$AdditionalSourceFiles[$relativeObject]) `
            -Force -ErrorAction Stop
        if ($source.PSIsContainer -or
            ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$PackageLabel additional provenance must be a physical file: '$($source.FullName)'."
        }
        $expected[$relative] = [pscustomobject]@{ Source = $source.FullName }
    }
    $expected['AppxManifest.xml'] = [pscustomobject]@{ Source = $null }
    $expected['PACKAGE-CONTENTS.sha256'] = [pscustomobject]@{ Source = $null }

    $actual = @{}
    foreach ($item in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$PackageLabel contains a reparse point '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        if (Test-EzyPackageEnvelopeFile -RelativePath $relative) {
            continue
        }
        if ($actual.ContainsKey($relative)) {
            throw "$PackageLabel contains duplicate relative path '$relative'."
        }
        $actual[$relative] = $item.FullName
    }

    if ($actual.Count -ne $expected.Count) {
        throw "$PackageLabel trusted content count mismatch: expected $($expected.Count), actual $($actual.Count)."
    }
    foreach ($relative in $actual.Keys) {
        if (-not $expected.ContainsKey($relative)) {
            throw "$PackageLabel contains content absent from trusted build provenance: '$relative'."
        }
        $source = [string]$expected[$relative].Source
        if ([string]::IsNullOrWhiteSpace($source)) {
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $actual[$relative] -Algorithm SHA256).Hash.ToUpperInvariant()
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not [string]::Equals($actualHash, $sourceHash, [StringComparison]::Ordinal)) {
            throw "$PackageLabel differs from trusted build provenance at '$relative'."
        }
    }
    foreach ($relative in $expected.Keys) {
        if (-not $actual.ContainsKey($relative)) {
            throw "$PackageLabel is missing trusted build content '$relative'."
        }
    }
}
