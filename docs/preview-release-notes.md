# ezy Image Viewer Installer + Portable Preview 1.0.11

This unsigned prerelease is provided for personal evaluation and testing on
Windows 10 build 19041 or later, x64. It is not a production-signed release.
Windows SmartScreen may warn when either executable is launched.

## Downloads

- `ezyImageViewerSetup-1.0.11-x64-dev-unsigned.exe`: installer with current-user
  or all-users scope. Start menu registration is enabled by default. Desktop
  shortcut and image file association registration are explicit opt-in choices.
  The setup UI and MIT license explanation follow the Windows UI language for
  Korean and English, with English as the fallback.
- `ezyImageViewer.exe`: compressed single-file Portable. Keep this filename so WinUI can resolve its embedded resources.
  It installs no shortcuts, registry entries, package identity, or file
  association. Keep the EXE at a stable path if Windows is configured manually
  to open image files with it.
- `SHA256SUMS.txt` and `preview-release-manifest.json`: integrity and provenance
  metadata for the exact release assets.
- `EzyRtfLargeTheme.xml` and `LICENSE-MRL.txt`: corresponding WiX theme source
  and Microsoft Reciprocal License text.

The single-file Portable extracts its .NET, WinUI, and native graphics runtime
to the current user's temporary directory while running. Deleting the outer EXE
does not remove settings, recent-file history, logs, or recovery data under
`%LOCALAPPDATA%\ezyImageViewer`.

The installer can register PNG, JPG, JPEG, BMP, GIF, WebP, TIF, and TIFF as
Open With candidates. It does not force ezy Image Viewer to become the Windows
default handler; that final choice remains with the user.

Preview 1.0.11 replaces placeholder noise assets with the product icon and fixes
the scope plan so exactly one application MSI is installed. The completion-page
Launch action resolves `ezyImageViewer.exe` through the App Paths registration
created by the selected installation scope. Because this preview is unsigned,
the installer skips package-identity registration; signed production builds keep
that registration path.

The bundled Microsoft Windows App SDK WinUI component carries Engineering Preview
terms. Use these artifacts only for evaluation and testing, not as a
production or live operating deployment. PDF and PSD remain disabled in the
normal product UI.
