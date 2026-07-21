# ezy Image Viewer

ezy Image Viewer is an open-source image viewer and editor for Windows 10 build
19041 or later on x64 PCs. The application is implemented with C# and WinUI 3.

> **Pre-release status:** the Basic Portable ZIP is an unsigned evaluation and
> testing preview. There is no production-signed public installer yet, and the
> Portable preview must not be redistributed as a trusted production build.

## Features

- Opens common raster images, animated GIF/WebP, multi-page TIFF, ICO, and
  security-restricted SVG/SVGZ files.
- Provides non-destructive project files (`.ezyimg`), image export, layers,
  annotations, crop/resize/rotate tools, and privacy-oriented metadata export.
- Integrates with the Windows Snipping Tool and clipboard without uploading
  captured or opened images.
- Stores settings, recent-file history, local logs, and crash recovery data on
  the local PC.

AVIF and HEIC/HEIF require a compatible Windows WIC codec. PDF and PSD support
is deliberately disabled in the normal UI until the isolated codec path passes
the remaining package, security, corpus, and fidelity gates. See the
[user guide](docs/user-guide.md) for the exact supported formats and limits.

## Privacy

The application has no telemetry, analytics, automatic version check, or
automatic upload path. Selecting **Check for updates** only asks the operating
system to open the fixed GitHub Releases page in the user's default browser.

Read the [privacy policy](docs/privacy.md) and the
[local data operations guide](docs/operations.md) before handling sensitive
images.

## Downloads and updates

The first public download is the
[Basic Portable preview](https://github.com/koprodev/ezy-image-viewer-releases/releases/tag/v0.1.0-portable.1).
Download the ZIP and `SHA256SUMS.txt`, verify the hash, extract the entire ZIP,
and run `ezyImageViewer.exe`. The archive is unsigned, has no installer or
package identity, and Windows SmartScreen may warn. It is published only for
evaluation and testing because the included Windows App SDK WinUI component
carries Engineering Preview terms that restrict live operating use.

There is no public production-signed installer yet. The future production
channel remains a scope-selecting Burn setup executable plus fixed-scope
per-user and per-machine MSI packages. A Microsoft Store package is the next
free-distribution path to pursue. Production signing, timestamp verification,
checksum and SBOM generation, licensing clearance, and clean-VM lifecycle
testing remain mandatory for those packages. The application does not download
or install updates automatically.

This public repository contains a reviewed clean source snapshot. Local Git
history and internal collaboration records are not part of that snapshot. The
Portable preview is not a production binary release or SignPath acceptance
evidence by itself.

## Build and test

Prerequisites:

- Windows x64
- The .NET SDK selected by [`global.json`](global.json)
- Locked NuGet dependencies

From the repository root:

```powershell
dotnet restore EzyImageViewer.slnx --locked-mode
dotnet build EzyImageViewer.slnx -c Release --no-restore
dotnet test EzyImageViewer.Tests/EzyImageViewer.Tests.csproj -c Release --no-build
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File packaging/test-publication-readiness-contract.ps1
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File packaging/test-public-source-snapshot-contract.ps1
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File packaging/test-portable-release-contract.ps1
```

Installer generation has additional WiX, signing, and verification gates. See
the [release process](docs/release-process.md); do not treat its unsigned
examples as distributable releases.

## Code signing policy

The project is preparing an application for free open-source code signing, but
it has not been accepted by SignPath Foundation and has no production signing
identity. The [code signing policy](docs/code-signing-policy.md) records the
planned roles, artifact scope, approval flow, and fail-closed release rules.
The [SignPath readiness checklist](docs/signpath-readiness.md) lists every
unresolved eligibility and publication gate.

## Documentation

- [User guide](docs/user-guide.md)
- [Local data and recovery operations](docs/operations.md)
- [Release process](docs/release-process.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
- [Privacy policy](docs/privacy.md)
- [Code signing policy](docs/code-signing-policy.md)

## License

Project-owned source code is licensed under the [MIT License](LICENSE).
Distributed packages also contain third-party components with their own terms. Review
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistribution.
