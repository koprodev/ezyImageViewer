# ezy Image Viewer Basic Portable Preview 0.1.0-portable.1

This is the first identity-free, installation-free testing preview for Windows
10 build 19041 or later on x64 PCs.

## Run

1. Download `ezyImageViewer-0.1.0-portable.1-win-x64.zip`.
2. Verify its SHA-256 value with `SHA256SUMS.txt`.
3. Extract the whole archive and run `ezyImageViewer.exe`.

## Important limitations

- This preview is unsigned, so Windows SmartScreen may warn.
- It does not install shortcuts, registry entries, file associations, package
  identity, or automatic updates.
- The official package-identity Snipping Tool callback is unavailable; the
  clipboard fallback remains available.
- PDF and PSD remain disabled in the normal UI.
- The included Windows App SDK WinUI component carries Engineering Preview
  terms. This release is for evaluation and testing, not production deployment.

Read `PORTABLE-README.txt`, `THIRD-PARTY-NOTICES.md`, and the bundled
`THIRD-PARTY-LICENSES` directory before use.
