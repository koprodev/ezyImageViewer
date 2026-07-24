# ezy Image Viewer Installer + Portable Preview 1.0.42

This unsigned prerelease is provided for personal evaluation and testing on
Windows 10 build 19041 or later, x64. It is not a production-signed release.
Windows SmartScreen may warn when either executable is launched.

## Downloads

- `ezyImageViewerSetup-1.0.42-x64-dev-unsigned.exe`: installer with current-user
  or all-users scope. Start menu and supported image file association (Open With)
  registration are enabled by default. The desktop shortcut remains an explicit
  opt-in choice.
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

Preview 1.0.42 adds, since 1.0.12: a speech bubble annotation with a draggable
tail, a 4K whiteboard document (white or black, baked grid), right-drag
panning, box pixel selection with transparent cut and lift-to-object editing,
crop-region clipboard copy, toolbar dropdown groups (open, select split,
rotate/flip, crop/size, zoom, and privacy tools) with per-group on/off toggles
on a dedicated settings page, a paged settings hub with bulk Open With file
association management, a bottom-right docked layer panel, and a title bar
that shows the exact build version. It also fixes the installer's post-setup
Launch button and applies the file-association selection on Save.

New in this build, opening the file association settings page and saving also
makes ezy Image Viewer the double-click default for the ticked image
extensions. The separate "Set as default app" button 1.0.36 introduced is gone;
Save and Apply now do both the Open With candidate registration and the default
switch, so ticking extensions and saving is enough. Untick an extension and
save to give it back. Saving without opening that page never touches your
defaults, so changing an unrelated setting cannot hand them over. This uses an
unsupported Windows mechanism, so Windows policy or a future update can block
it; when that happens the app reports the result per extension and points to
the Windows default-apps page. The installer itself still only registers Open
With candidates and never changes your default.

This build also fixes a startup failure that could disable settings, recent
files, and recovery for a session. Starting a second window while the first one
was saving could make the app-data protection check fail on the half-written
temporary file and report "로컬 데이터 보호 실패". Files written by the app now
also carry an explicit owner-only permission set rather than inheriting one.

Because this preview is unsigned, the installer skips package-identity
registration; signed production builds keep that registration path.

The bundled Microsoft Windows App SDK WinUI component carries Engineering Preview
terms. Use these artifacts only for evaluation and testing, not as a
production or live operating deployment. PDF and PSD remain disabled in the
normal product UI.
