# ezy Image Viewer privacy policy

Status: **public source policy; no public production binary release exists**
Last reviewed: 2026-07-25

This policy describes the behavior implemented in the public source tree. It
does not claim that a production binary release or support service is already
available.

## Network and telemetry policy

The application does not implement telemetry, analytics, advertising,
automatic crash reporting, background downloads, or automatic update
installation.

At startup, the application may send one unauthenticated HTTPS GET request to
the public GitHub Releases API. Automatic checks are limited to once every 24
hours and include preview releases. A manual **Check for updates** action
bypasses that local interval. The request contains the normal HTTP connection
metadata, a fixed application User-Agent, and no image bytes, image metadata,
file paths, settings, logs, or recovery data. GitHub may process the originating
IP address and request metadata under its own policies.

The following user-requested operating-system integrations may cause another
application to use the network:

- **Open release page** asks Windows to open the validated GitHub release page
  in the default browser. The application does not download a release itself.
- A user may download a release, submit an issue, or share diagnostic material
  through a browser or another tool. Those transfers are controlled by the
  user and the selected third-party service, not by ezy Image Viewer.

The Windows Snipping Tool protocol and clipboard integration are local Windows
operations. Opened images, captured images, clipboard contents, and project
files are not uploaded by the application.

## Local data

The application may store the following under
`%LOCALAPPDATA%\ezyImageViewer`:

- settings and privacy preferences;
- up to 20 recent-file paths when recent-file history is enabled;
- bounded structured logs that exclude original document paths, exception
  messages, stack traces, image bytes, and clipboard contents;
- recovery checkpoints that can contain the original image and edit state;
- quarantine and crash-marker data used for recovery and safe mode;
- the last update-check attempt time in `update-check-state.txt`.

Recovery checkpoints are integrity checked but are not separately encrypted.
Users handling sensitive images should rely on Windows account and storage
protection. Retention limits, deletion behavior, and exact fields are documented
in the [user guide](user-guide.md#개인정보와-로컬-데이터) and
[operations guide](operations.md).

## Metadata and user-controlled sharing

Metadata preservation is off by default. When enabled for a file-backed image,
the exporter rebuilds only an allowlisted subset and removes GPS, serial
numbers, unique identifiers, free-text author fields, and the original
thumbnail. Clipboard and capture documents do not expose the preservation
option.

Logs and recovery files are never sent automatically. A user who chooses to
share support material should review it first; recovery files and recent-file
records can contain sensitive image data or full local paths.

## Platform and third-party components

The source does not intentionally enable product telemetry. Windows and
Microsoft runtime components remain subject to the user's Windows diagnostic
settings and the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).
Other bundled components and their notices are listed in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Deletion and policy changes

Uninstalling the application keeps the local data directory by default so that
user work is not silently destroyed. Users may delete that directory after
closing the application and preserving any recovery work they still need.

Changes to this policy are versioned with the source. GitHub Issues in the
public source repository may be used for project questions, but they are not a
private support channel or a service-level commitment.
