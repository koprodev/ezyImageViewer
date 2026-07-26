# ezy Image Viewer

ezy Image Viewer is an open-source image viewer and editor for Windows 10
build 19041 or later on x64 PCs. It is built with C# and WinUI 3.

> Distribution status: Microsoft Store certification is pending. GitHub is the
> source, documentation, and issue tracker; this repository does not publish
> application binaries.

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
intentionally unsupported. See the [user guide](docs/user-guide.md) for the
exact supported formats and limits.

## Install and updates

The planned binary distribution channel is the
[Microsoft Store](https://apps.microsoft.com/detail/9P82BRPVKC5N). Installation
is not considered generally available until Store certification succeeds.
After publication, the Store manages application installation and updates.

GitHub Releases, Portable archives, MSI/Setup packages, and App Installer feeds
are not current distribution channels.

## Privacy

The application has no telemetry, analytics, advertising, automatic upload, or
application-initiated update request. Opened images and local app data stay on
the device. Read the [privacy policy](docs/privacy.md) and
[local data operations guide](docs/operations.md) before handling sensitive
images.

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
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/test-public-source-snapshot-contract.ps1
```

Store package validation is documented in the
[release process](docs/release-process.md).

## Documentation

- [User guide](docs/user-guide.md)
- [Local data and recovery operations](docs/operations.md)
- [Release process](docs/release-process.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
- [Privacy policy](docs/privacy.md)

## License

Project-owned source code is licensed under the [MIT License](LICENSE).
Distributed packages also contain third-party components with their own terms.
Review [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistribution.
