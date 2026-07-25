[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    # 로컬 테스트만 현재 작업 트리 그대로 게시. 공개 릴리스는 이 옵션 없이 commit에 결박.
    [switch]$AllowUncommittedSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'msi-payload-helpers.ps1')
. (Join-Path $scriptRoot 'portable-release-helpers.ps1')

Assert-EzyPortableVersion $Version
if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $ReleaseVersion = $Version
}
if ($ReleaseVersion -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "ReleaseVersion has an invalid format: '$ReleaseVersion'."
}
$productVersion = $Version.Split('-')[0]
if ($ReleaseVersion -cne $productVersion -and
    -not $ReleaseVersion.StartsWith(
        "$productVersion-", [StringComparison]::Ordinal)) {
    throw "ReleaseVersion must match Version: '$ReleaseVersion'."
}
$numericVersion = Get-EzyPortableNumericVersion $Version
if (-not $AllowUncommittedSource) {
    [void](Assert-EzyPortableSourceState $repositoryRoot)
}

$target = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw "OutputDirectory already exists: '$target'."
}
$parentPath = [IO.Path]::GetDirectoryName($target)
if ([string]::IsNullOrWhiteSpace($parentPath)) {
    throw 'OutputDirectory must have a parent directory.'
}
[void][IO.Directory]::CreateDirectory($parentPath)
$parent = Get-Item -LiteralPath $parentPath -Force
if (-not $parent.PSIsContainer -or
    ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'OutputDirectory parent must be a physical directory.'
}

$targetName = [IO.Path]::GetFileName($target)
$staging = Join-Path $parent.FullName (
    ".$targetName.$([Guid]::NewGuid().ToString('N')).staging")
$folderPayload = Join-Path $staging 'folder-payload'
$singleOutput = Join-Path $staging 'single-output'
$applicationProject = Join-Path $repositoryRoot 'EzyImageViewer.App\EzyImageViewer.App.csproj'
$portableReadme = Join-Path $repositoryRoot 'docs\portable-readme.txt'
$singleFileTargets = Join-Path $scriptRoot 'SingleFilePublish.targets'
$fileName = 'ezyImageViewer.exe'

try {
    [void][IO.Directory]::CreateDirectory($staging)

    $restoreArguments = @(
        'restore', $applicationProject, '--locked-mode', '--runtime', 'win-x64',
        '-p:Platform=x64', '-p:Packaged=false', '-p:ExternalIdentity=false',
        '-p:Portable=true', '-p:NuGetAuditMode=all',
        '-p:WarningsAsErrors=NU1903%3BNU1904')
    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for the single-file portable flavor ($LASTEXITCODE)."
    }

    $commonPublishArguments = @(
        $applicationProject, '-c', 'Release', '--no-restore', '--self-contained', 'true',
        '-p:Platform=x64', '-p:Packaged=false', '-p:ExternalIdentity=false',
        '-p:Portable=true', '-p:DebugSymbols=false', '-p:DebugType=None',
        '-p:CopyOutputSymbolsToPublishDirectory=false', "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion", "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$ReleaseVersion")

    & dotnet publish @commonPublishArguments `
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $scriptRoot 'MsiPublish.targets')" `
        -o $folderPayload
    if ($LASTEXITCODE -ne 0) {
        throw "folder publish failed for the single-file portable flavor ($LASTEXITCODE)."
    }

    $intermediateRoot = Join-Path $repositoryRoot 'EzyImageViewer.App\obj\portable\x64\Release'
    $publishOutputList = Get-EzyMsiPublishOutputListPath $intermediateRoot $folderPayload
    Assert-EzyMsiPayload $folderPayload $publishOutputList
    $depsJson = Join-Path $folderPayload 'ezyImageViewer.deps.json'
    $projectAssets = Join-Path $repositoryRoot 'EzyImageViewer.App\obj\portable\project.assets.json'
    $licenseIndex = Copy-EzyPortableThirdPartyFiles `
        -PayloadDirectory $folderPayload -DepsJson $depsJson `
        -ProjectAssetsJson $projectAssets
    $licenseRoot = Split-Path -Parent $licenseIndex

    # 폴더 게시가 만든 비리디렉션 RegFree WinRT manifest만 지워 단일 파일 게시가 loadFrom으로 재생성.
    $manifestDirectory = Join-Path (Split-Path -Parent $publishOutputList) 'Manifests'
    foreach ($generatedName in @('WindowsAppSDK.manifest', 'app.manifest')) {
        $generatedManifest = Join-Path $manifestDirectory $generatedName
        if ([IO.File]::Exists($generatedManifest)) {
            [IO.File]::Delete($generatedManifest)
        }
    }

    & dotnet publish @commonPublishArguments -t:Rebuild `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:EnableMsixTooling=true `
        "-p:EzyPortableLicenseRoot=$licenseRoot" `
        "-p:EzyPortableReadme=$portableReadme" `
        "-p:CustomBeforeMicrosoftCommonProps=$singleFileTargets" `
        -o $singleOutput
    if ($LASTEXITCODE -ne 0) {
        throw "single-file publish failed ($LASTEXITCODE)."
    }

    $sourceExecutable = Join-Path $singleOutput 'ezyImageViewer.exe'
    if (-not [IO.File]::Exists($sourceExecutable)) {
        throw 'Single-file publish did not produce ezyImageViewer.exe.'
    }
    $unexpectedRuntimeFiles = @(Get-ChildItem -LiteralPath $singleOutput -File -Force |
        Where-Object { $_.Name -cne 'ezyImageViewer.exe' -and $_.Extension -cne '.pdb' })
    if ($unexpectedRuntimeFiles.Count -ne 0) {
        throw "Single-file publish produced unexpected runtime files: $($unexpectedRuntimeFiles.Name -join ', ')."
    }

    [void][IO.Directory]::CreateDirectory($target)
    $destination = Join-Path $target $fileName
    [IO.File]::Copy($sourceExecutable, $destination, $false)
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if ([string]$signature.Status -cne 'NotSigned') {
        throw "Portable executable must be unsigned; actual status is '$($signature.Status)'."
    }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination)
    if ([string]$versionInfo.FileVersion -cne $numericVersion) {
        throw "Portable executable file version mismatch: '$($versionInfo.FileVersion)'."
    }

    $file = Get-Item -LiteralPath $destination -Force
    Write-Output "Single-file portable staged: $($file.FullName)"
    Write-Output "Bytes: $($file.Length)"
    Write-Output "SHA-256: $((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)"
    Write-Output 'Signature: NotSigned (testing preview)'
}
finally {
    if ([IO.Directory]::Exists($staging)) {
        [IO.Directory]::Delete($staging, $true)
    }
}
