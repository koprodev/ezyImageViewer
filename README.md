# ezy Image Viewer

ezy Image Viewer is an open-source image viewer and editor for Windows 10 build
19041 or later on x64 PCs. The application is implemented with C# and WinUI 3.

> **Pre-release status:** the public downloads are unsigned evaluation and
> testing previews. There is no production-signed public installer yet, and the
> preview artifacts must not be redistributed as trusted production builds.

## Features

- Opens common raster images, animated GIF/WebP, multi-page TIFF, ICO, and
  security-restricted SVG/SVGZ files.
- Provides non-destructive project files (`.ezyimg`), image export, layers,
  annotations, crop/resize/rotate tools, and privacy-oriented metadata export.
- Integrates with the Windows Snipping Tool and clipboard without uploading
  captured or opened images.
- Stores settings, recent-file history, local logs, and crash recovery data on
  the local PC.

AVIF and HEIC/HEIF require a compatible Windows WIC codec. PDF and PSD are
intentionally unsupported; their signatures are recognized and rejected with a
clear unsupported-format error. See the [user guide](docs/user-guide.md) for
the exact supported formats and limits.

## Privacy

The application has no telemetry, analytics, or automatic upload path. At
startup it checks the repository's public GitHub Releases metadata no more than
once every 24 hours, including preview releases. It never uploads image or file
data and does not download or install an update automatically.

Read the [privacy policy](docs/privacy.md) and the
[local data operations guide](docs/operations.md) before handling sensitive
images.

## Downloads and updates

The current [Installer + Portable preview](https://github.com/koprodev/ezyImageViewer/releases/latest)
provides an unsigned scope-selecting Setup EXE and an unsigned compressed
single-file Portable EXE. Verify either file against `SHA256SUMS.txt` before
running it. Setup registers supported image types as Windows Open With
candidates by default; it never forces a default handler. Portable installs no
registry entries or shortcuts. Windows SmartScreen may warn for both files.

The earlier folder-based
[Basic Portable preview](https://github.com/koprodev/ezyImageViewer/releases/tag/v0.1.0-portable.1)
remains available for comparison. Neither release is production signed. The
included Windows App SDK WinUI component carries Engineering Preview terms that
restrict live operating use, and clean-VM lifecycle qualification is not
complete. The application does not download or install updates automatically.

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
