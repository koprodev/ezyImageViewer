[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ExecutablePath,
    [switch]$SkipRuntimeSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptRoot 'portable-release-helpers.ps1')

Assert-EzyPortableVersion $Version
$numericVersion = Get-EzyPortableNumericVersion $Version
$executable = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExecutablePath)) -Force
$expectedName = 'ezyImageViewer.exe'
if ($executable.PSIsContainer -or
    ($executable.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $executable.Name -cne $expectedName) {
    throw "Single-file portable must be a physical '$expectedName'."
}
$signature = Get-AuthenticodeSignature -LiteralPath $executable.FullName
if ([string]$signature.Status -cne 'NotSigned') {
    throw "Single-file portable signature must be NotSigned; actual '$($signature.Status)'."
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable.FullName)
if ([string]$versionInfo.FileVersion -cne $numericVersion) {
    throw "Single-file portable version mismatch: '$($versionInfo.FileVersion)'."
}
if ($SkipRuntimeSmoke) {
    Write-Output 'Single-file portable static verification passed.'
    Write-Output "Version: $Version"
    Write-Output "Executable bytes: $($executable.Length)"
    Write-Output 'Runtime smoke: skipped for a non-interactive build agent'
    return
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ezy-single-file-verify-' + [Guid]::NewGuid().ToString('N'))
$extractRoot = Join-Path $workRoot 'extract'
$imagePath = Join-Path $workRoot 'pixel.png'
$resultPath = Join-Path $workRoot 'smoke.json'

try {
    [void][IO.Directory]::CreateDirectory($workRoot)
    [IO.File]::WriteAllBytes($imagePath, [Convert]::FromBase64String(
            'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII='))

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable.FullName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = '--smoke-open="' + $imagePath + '" --smoke-out="' + $resultPath + '"'
    $startInfo.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractRoot
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Unable to start the single-file portable smoke process.'
    }
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw 'Single-file portable smoke timed out.'
        }
        if ($process.ExitCode -ne 0) {
            throw "Single-file portable smoke failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }

    if (-not [IO.File]::Exists($resultPath)) {
        throw 'Single-file portable smoke result is missing.'
    }
    $smoke = [IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
    if ([string]$smoke.state -cne 'Ready' -or [bool]$smoke.packageIdentity) {
        throw 'Single-file portable smoke did not reach identity-free Ready state.'
    }

    $extractedFiles = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Force)
    if ($extractedFiles.Count -eq 0) {
        throw 'Single-file portable extracted no runtime payload.'
    }
    if (@($extractedFiles | Where-Object { $_.Extension -ieq '.pdb' }).Count -ne 0) {
        throw 'Single-file portable extracted debug symbols.'
    }
    foreach ($requiredName in @(
            'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'PORTABLE-README.txt', 'INDEX.json')) {
        if (@($extractedFiles | Where-Object { $_.Name -ceq $requiredName }).Count -eq 0) {
            throw "Single-file portable did not embed '$requiredName'."
        }
    }

    $readme = @($extractedFiles | Where-Object { $_.Name -ceq 'PORTABLE-README.txt' })
    if ($readme.Count -ne 1 -or
        [IO.File]::ReadAllText($readme[0].FullName).IndexOf(
            'Windows SmartScreen', [StringComparison]::Ordinal) -lt 0) {
        throw 'Single-file portable README disclosure is invalid.'
    }
    $licenseIndexes = @($extractedFiles | Where-Object {
            $_.Name -ceq 'INDEX.json' -and
            $_.FullName.IndexOf('THIRD-PARTY-LICENSES', [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    if ($licenseIndexes.Count -ne 1) {
        throw 'Single-file portable third-party license index is missing or ambiguous.'
    }
    $licenseIndex = [IO.File]::ReadAllText($licenseIndexes[0].FullName) | ConvertFrom-Json
    $winUi = @($licenseIndex.packages | Where-Object {
            [string]$_.id -ceq 'Microsoft.WindowsAppSDK.WinUI' -and
            [string]$_.version -ceq '2.2.1'
        })
    if ($winUi.Count -ne 1) {
        throw 'Single-file portable WinUI license inventory is invalid.'
    }

    [long]$extractedBytes = 0
    foreach ($file in $extractedFiles) { $extractedBytes += $file.Length }
    Write-Output 'Single-file portable verification passed.'
    Write-Output "Version: $Version"
    Write-Output "Executable bytes: $($executable.Length)"
    Write-Output "Extracted files: $($extractedFiles.Count)"
    Write-Output "Extracted bytes: $extractedBytes"
    Write-Output 'Smoke: Ready, packageIdentity=false'
}
finally {
    if ([IO.Directory]::Exists($workRoot)) {
        [IO.Directory]::Delete($workRoot, $true)
    }
}
