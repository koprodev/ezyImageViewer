Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-helpers.ps1')

$script:EzyMsiPayloadExtensions = @(
    '.dll', '.exe', '.html', '.ico', '.json', '.md', '.mui', '.png', '.pri', '.ttf',
    '.txt', '.winmd'
)
$script:EzyMsiRequiredPayloadPaths = @(
    'ezyImageViewer.exe',
    'ezyImageViewer.dll',
    'ezyImageViewer.deps.json',
    'ezyImageViewer.runtimeconfig.json',
    'ezyImageViewer.pri',
    'Assets/ezyImageViewer.ico',
    'Assets/Fonts/MaterialSymbolsOutlined.ttf',
    'Assets/Fonts/LICENSE-MaterialSymbols.txt',
    'LICENSE.txt',
    'THIRD-PARTY-NOTICES.md'
)

function Get-EzyMsiPayloadFiles {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadDirectory
    )

    $root = Get-Item -LiteralPath ([IO.Path]::GetFullPath($PayloadDirectory)) -Force `
        -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "MSI payload must be a physical directory: '$PayloadDirectory'."
    }

    $prefix = $root.FullName.TrimEnd('\') + '\'
    $files = New-Object 'Collections.Generic.List[object]'
    foreach ($item in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force `
            -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "MSI payload must not contain a reparse point: '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            continue
        }
        $relative = $item.FullName.Substring($prefix.Length).Replace('\', '/')
        [void]$files.Add([PSCustomObject]@{
                File = $item
                RelativePath = $relative
            })
    }
    return $files.ToArray()
}

function Get-EzyMsiPublishOutputListPath {
    param(
        [Parameter(Mandatory)]
        [string]$IntermediateRoot,

        [Parameter(Mandatory)]
        [string]$PayloadDirectory
    )

    $payloadFiles = @(Get-EzyMsiPayloadFiles $PayloadDirectory)
    $actual = @($payloadFiles | ForEach-Object { $_.File.FullName } | Sort-Object)
    $root = Get-Item -LiteralPath ([IO.Path]::GetFullPath($IntermediateRoot)) -Force `
        -ErrorAction Stop
    if (-not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Intermediate output must be a physical directory: '$IntermediateRoot'."
    }

    $matches = New-Object 'Collections.Generic.List[string]'
    foreach ($candidate in @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force `
            -Filter 'PublishOutputs.*.txt' -File -ErrorAction Stop)) {
        if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish output list must not be a reparse point: '$($candidate.FullName)'."
        }
        $declared = @([IO.File]::ReadAllLines($candidate.FullName) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object)
        if ($declared.Count -ne $actual.Count) {
            continue
        }
        if (@(Compare-Object -ReferenceObject $actual -DifferenceObject $declared `
                -CaseSensitive).Count -eq 0) {
            [void]$matches.Add($candidate.FullName)
        }
    }

    if ($matches.Count -ne 1) {
        throw "Expected exactly one publish output list matching the MSI payload; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-EzyMsiPayload {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadDirectory,

        [string]$PublishOutputListPath,

        [switch]$InventoryPresent
    )

    $files = @(Get-EzyMsiPayloadFiles $PayloadDirectory)
    $root = (Get-Item -LiteralPath ([IO.Path]::GetFullPath($PayloadDirectory)) -Force).FullName
    $prefix = $root.TrimEnd('\') + '\'
    $inventoryName = 'PACKAGE-CONTENTS.sha256'
    $inventoryFiles = @($files | Where-Object {
            [string]::Equals($_.RelativePath, $inventoryName,
                [StringComparison]::OrdinalIgnoreCase)
        })
    if ($InventoryPresent) {
        if ($inventoryFiles.Count -ne 1 -or
            $inventoryFiles[0].RelativePath -cne $inventoryName) {
            throw 'MSI payload must contain exactly one canonical package inventory.'
        }
    }
    elseif ($inventoryFiles.Count -ne 0) {
        throw 'MSI payload contains a reserved package inventory before generation.'
    }

    $productFiles = @($files | Where-Object {
            -not [string]::Equals($_.RelativePath, $inventoryName,
                [StringComparison]::OrdinalIgnoreCase)
        })
    if ($productFiles.Count -eq 0) {
        throw 'MSI payload is empty.'
    }

    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $productFiles) {
        if (-not $actual.Add($entry.RelativePath)) {
            throw "MSI payload contains a duplicate relative path: '$($entry.RelativePath)'."
        }
        $extension = [IO.Path]::GetExtension($entry.RelativePath)
        if ($extension -cnotin $script:EzyMsiPayloadExtensions) {
            throw "MSI payload extension is not allowlisted: '$($entry.RelativePath)'."
        }
        if ($extension -ceq '.pdb' -or
            $entry.File.Name -in @(
                'makeappx.exe',
                'signtool.exe',
                'mt.exe')) {
            throw "MSI payload contains a forbidden build, SDK, or isolated codec file: '$($entry.RelativePath)'."
        }
    }

    foreach ($required in $script:EzyMsiRequiredPayloadPaths) {
        if (-not $actual.Contains($required)) {
            throw "MSI payload is missing required file '$required'."
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($PublishOutputListPath)) {
        $list = Get-Item -LiteralPath ([IO.Path]::GetFullPath($PublishOutputListPath)) `
            -Force -ErrorAction Stop
        if ($list.PSIsContainer -or
            ($list.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish output list must be a physical file: '$PublishOutputListPath'."
        }
        $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in [IO.File]::ReadAllLines($list.FullName)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            $fullPath = [IO.Path]::GetFullPath($line)
            if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Publish output list escapes the MSI payload: '$fullPath'."
            }
            $relative = $fullPath.Substring($prefix.Length).Replace('\', '/')
            if (-not $declared.Add($relative)) {
                throw "Publish output list contains duplicate path '$relative'."
            }
        }

        if ($declared.Count -ne $actual.Count) {
            throw "MSI payload inventory mismatch: declared $($declared.Count), actual $($actual.Count)."
        }
        foreach ($relative in $actual) {
            if (-not $declared.Contains($relative)) {
                throw "MSI payload contains an undeclared or stale file: '$relative'."
            }
        }
        foreach ($relative in $declared) {
            if (-not $actual.Contains($relative)) {
                throw "MSI payload is missing declared file: '$relative'."
            }
        }
    }
    elseif (-not $InventoryPresent) {
        throw 'MSI payload requires MSBuild publish provenance before inventory generation.'
    }

    if ($InventoryPresent) {
        Assert-EzyPackageContentsManifest -UnpackedRoot $root -PackageLabel 'MSI payload'
    }
}

function Write-EzyMsiPayloadInventory {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadDirectory,

        [Parameter(Mandatory)]
        [string]$PublishOutputListPath
    )

    Assert-EzyMsiPayload $PayloadDirectory $PublishOutputListPath
    $path = Write-EzyPackageContentsManifest -Layout $PayloadDirectory
    Assert-EzyMsiPayload $PayloadDirectory $PublishOutputListPath -InventoryPresent
    return $path
}
