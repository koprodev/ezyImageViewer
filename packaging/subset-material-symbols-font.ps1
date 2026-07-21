[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceFont,

    [Parameter(Mandatory)]
    [string]$OutputFont,

    [string]$PythonExecutable = 'python'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$iconsPath = Join-Path $repositoryRoot 'EzyImageViewer.App\Resources\Icons.xaml'
$sourcePath = [IO.Path]::GetFullPath($SourceFont)
$outputPath = [IO.Path]::GetFullPath($OutputFont)

if (-not [IO.File]::Exists($sourcePath)) {
    throw "Source font does not exist: '$sourcePath'."
}
if ([string]::Equals($sourcePath, $outputPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SourceFont and OutputFont must be different files.'
}
if ([IO.File]::Exists($outputPath) -or [IO.Directory]::Exists($outputPath)) {
    throw "OutputFont already exists: '$outputPath'."
}

$outputDirectory = [IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputFont must have a parent directory.'
}
[void][IO.Directory]::CreateDirectory($outputDirectory)

$iconText = [IO.File]::ReadAllText($iconsPath)
$codePoints = @([regex]::Matches($iconText, '&#x([0-9A-Fa-f]+);') | ForEach-Object {
        'U+' + $_.Groups[1].Value.ToUpperInvariant()
    } | Sort-Object -Unique)
if ($codePoints.Count -eq 0) {
    throw 'Icons.xaml contains no numeric glyph references.'
}

$instancePath = Join-Path $outputDirectory (
    [IO.Path]::GetFileNameWithoutExtension($outputPath) + '.instance.tmp.ttf')
if ([IO.File]::Exists($instancePath) -or [IO.Directory]::Exists($instancePath)) {
    throw "Intermediate font path already exists: '$instancePath'."
}

try {
    & $PythonExecutable -m fontTools.varLib.instancer $sourcePath `
        'FILL=0' 'wght=400' 'GRAD=0' 'opsz=24' --no-recalc-timestamp --output $instancePath
    if ($LASTEXITCODE -ne 0) {
        throw "fontTools varLib instancer failed ($LASTEXITCODE)."
    }

    $subsetArguments = @(
        '-m', 'fontTools.subset',
        $instancePath,
        "--output-file=$outputPath",
        ("--unicodes={0}" -f ($codePoints -join ',')),
        '--layout-features=*',
        '--name-IDs=*',
        '--name-legacy',
        '--name-languages=*',
        '--glyph-names',
        '--symbol-cmap',
        '--legacy-cmap',
        '--notdef-glyph',
        '--notdef-outline',
        '--recommended-glyphs')
    & $PythonExecutable @subsetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "fontTools subset failed ($LASTEXITCODE)."
    }
}
finally {
    if ([IO.File]::Exists($instancePath)) {
        [IO.File]::Delete($instancePath)
    }
}

$output = Get-Item -LiteralPath $outputPath -Force
$hash = (Get-FileHash -LiteralPath $output.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Output "Material Symbols subset: $($codePoints.Count) code points, $($output.Length) bytes"
Write-Output "SHA-256: $hash"
