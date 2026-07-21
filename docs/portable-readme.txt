ezy Image Viewer - Basic Portable Preview

This unsigned build is an evaluation and testing preview for Windows 10 build
19041 or later on x64 PCs. It is not a production-signed installer.

HOW TO RUN

1. Extract the entire ZIP archive to a normal local folder.
2. Run ezyImageViewer.exe from that extracted folder.
3. Windows SmartScreen may warn because this preview is unsigned. Verify the
   archive against the published SHA256SUMS.txt before running it.

PORTABLE SCOPE

- No installer or package identity is included.
- No registry key, Start menu shortcut, file association, service, or automatic
  updater is installed.
- To remove the program, close it and delete the extracted folder.
- User settings, recent-file history, logs, and recovery data remain under
  %LOCALAPPDATA%\ezyImageViewer. Delete that data separately only if you no
  longer need the settings or recovery files.

KNOWN LIMITATIONS

- The official package-identity Snipping Tool callback is unavailable. The
  existing clipboard-based fallback remains available.
- PDF and PSD remain disabled in the normal product UI.
- AVIF and HEIC/HEIF require a compatible Windows WIC codec.
- Updates are manual. A future Microsoft Store installation is separate and
  does not automatically remove this extracted copy.

LICENSE STATUS

Project-owned source is MIT licensed. THIRD-PARTY-NOTICES.md and the
THIRD-PARTY-LICENSES directory contain the runtime license inventory and copied
license or notice files. The current Microsoft.WindowsAppSDK.WinUI dependency
ships Engineering Preview terms that restrict live operating use unless another
agreement permits it. Accordingly, this Portable build is published only for
evaluation and testing and must not be relied on as a production deployment.

Project page:
https://github.com/koprodev/ezy-image-viewer-releases
