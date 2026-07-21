# SignPath Foundation readiness checklist

Status: **public source repository and CI active; unsigned testing previews approved; no SignPath application or signing transfer performed**
Assessment date: 2026-07-21

The maintainer selected SignPath Foundation as the preferred zero-monetary-cost
public-trust path and selected a reviewed clean source snapshot in the same
public repository that will host releases. The existing local history will not
be pushed. On 2026-07-19 the user explicitly approved creating and publishing
that source repository. On 2026-07-19 the user also approved publishing the
unsigned Basic Portable evaluation/testing prerelease. Sending a SignPath
inquiry, submitting identity or account data, and requesting a signature remain
separate approval gates.

On 2026-07-21 the user additionally approved one unsigned WiX Setup and
single-file Portable prerelease for personal evaluation and testing. This is
not a production release or evidence that SignPath accepted the signing form.

## Eligibility matrix

| Requirement | Current evidence | Status |
|---|---|---|
| Project license | Root `LICENSE` is MIT | Ready for project-owned source |
| No proprietary component | Several Windows App SDK runtime packages use Microsoft Software License Terms; the SignPath System Library exception has not been confirmed | **Blocked** |
| Active maintenance | More than 40 private source commits plus current build/test/release work | Public source evidence available |
| Already released in the form to sign | Public source plus unsigned Portable and Burn Setup testing previews exist; the Setup is not production signed or clean-VM qualified | **Blocked; SignPath interpretation required** |
| Download-page documentation | Public root README and user guide separate the Portable testing preview from future production installers | Ready for Portable publication |
| Code signing policy | `docs/code-signing-policy.md` is published and linked from the README | Ready for source publication |
| Privacy disclosure | `docs/privacy.md` publishes the required no-transfer statement and current local-data behavior | Ready for source publication |
| Repository ownership and roles | Public repository owner and single-maintainer model are documented; protected `main` requires pull requests | SignPath roles not configured |
| MFA and review controls | `main` enforces administrators, linear history, conversation resolution, and the strict `build-test` check | Source-host MFA not verified; SignPath controls not configured |
| Verifiable automated build | Pinned GitHub Actions workflow passed restore, build, 827 tests, package verifiers, unsigned WiX/MSIX generation, checksums, and SBOM on public `main` | Public CI evidence available; SignPath integration pending |
| Manual signing approval | Planned in the code signing policy | SignPath project does not exist |

The phrase “already be released in the form that should be signed” does not
explicitly say that an unsigned prerelease is sufficient. The Portable preview
is being published because the user chose an evaluation/testing channel, not as
a claim that SignPath's existing-release requirement has been satisfied.

## Publication and privacy audit

The audit intentionally did not print author names, email values, credentials,
or token-like content.

- Private source commit `212afcea011e6c44d28fb5c14844f87d71730bd6`
  contains 359 tracked files and is the 46th commit before this status update.
- History has one author identity and one author email. The email is not a
  GitHub noreply address and therefore requires explicit exposure approval or a
  non-destructive publication strategy before any public push.
- No tracked or historical file name matched private-key, PFX/P12, keystore, or
  credential-file patterns. No tracked certificate file exists at HEAD or in
  history.
- A strong-pattern scan across the then-current 40 commits found no private-key header,
  GitHub token, AWS access-key ID, Google API key, or Slack token match.
- Two path/email pattern matches are synthetic negative-test data, not user
  identity or credentials.
- Gitleaks 8.30.1 scanned the 353-file clean source snapshot with archive depth
  1 and found no leaks. Manual checks found no local user/project path, Git
  metadata, collaboration file, or internal audit directory in the snapshot.
- `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `PingPong.md`,
  `PingPong_Checklist.md`, and `docs/reviews/` are tracked locally but excluded
  from the selected clean public snapshot. The existing history and its author
  email will not be pushed. `packaging/new-public-source-snapshot.ps1` records
  the source commit and exact file hashes in `PUBLIC-SOURCE-MANIFEST.json`.
- Four snapshot assets are at least 1 MiB. The Material Symbols font has pinned
  commit, SHA-256, and Apache-2.0 provenance. The three user-provided design PNGs
  have no text/EXIF chunks and retain their C2PA Content Credentials. All four
  are intentionally included.
- The reviewed snapshot is public at
  `https://github.com/koprodev/ezy-image-viewer-releases`. Pull request #1 was
  squash-merged as public commit `3f56687b53da726a84ecc8586f52d4a92a5954cf`.
  Its protected-main GitHub Actions run
  [29683659478](https://github.com/koprodev/ezy-image-viewer-releases/actions/runs/29683659478)
  completed successfully. This is source and CI evidence, not a production
  binary release or SignPath acceptance.

The public CI does not upload unsigned MSIX or standalone MSI validation
outputs. Two explicitly approved workflows may publish unsigned testing
prereleases: the original identity-free Portable ZIP and the 2026-07-21 Burn
Setup plus single-file Portable set. Test-result artifacts remain separate from
release binaries.

## License and redistributable blockers

1. `Microsoft.WindowsAppSDK` 2.2.0 pulls
   `Microsoft.WindowsAppSDK.WinUI` 2.2.1 into the locked app graph and actual
   self-contained publish layout. The WinUI package's local `license.txt` says
   **Microsoft Windows App SDK Engineering Preview** and states that it may not
   be used in a live operating environment unless another agreement permits it.
   A production release is blocked until Microsoft clarifies the package terms
   or the dependency is replaced by a release-safe version and fully retested.
   As of this assessment, the official NuGet gallery lists
   [Windows App SDK 2.3.1](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1)
   with `Microsoft.WindowsAppSDK.WinUI >= 2.3.0`. A separate dependency,
   license, Windows 10 build 19041, package-identity, and full regression audit
   is the first technical resolution candidate; this is not approval for an
   unreviewed package update.
2. Windows App SDK component packages use Microsoft Software License Terms even
   though the upstream source repository is MIT. SignPath's no-proprietary-code
   rule allows System Libraries, but this project's self-contained redistributed
   runtime has not been accepted under that exception. SignPath must confirm it.
3. The restored `bblanchon.PDFium.Win32` package declares Apache-2.0 but does not
   ship the corresponding PDFium/Chromium third-party notice set. That notice set
   has now been acquired version-pinned from the upstream `chromium/7690` release
   (proven to match the redistributed `pdfium.dll` by identical SHA-256),
   committed under `EzyImageViewer.CodecHost/Notices/PDFium/`, packaged into the
   CodecHost MSIX, and hash-locked in `packaging/verify-msix-release.ps1`. See
   [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for provenance. The
   engineering packaging blocker is closed; final legal review stays part of the
   overall legal gate.
4. WiX Toolset 7 source is MS-RL and the local OSMF EULA exempts users below
   USD 10,000 annual gross revenue from its binary maintenance fee. The user has
   stated current revenue is zero, so the local threshold is not reached. Terms
   and revenue status must be rechecked before each public release; this audit is
   not a legal or accounting opinion.

See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for the complete runtime
inventory and remaining notice work.

## Draft SignPath inquiry

Do not send this text until the user separately approves external contact.

```text
Subject: OSS eligibility and WiX Burn workflow questions for ezy Image Viewer

Hello SignPath Foundation team,

I maintain ezy Image Viewer, an MIT-licensed Windows desktop application as an
individual in South Korea. The project has no revenue and publishes its source
at https://github.com/koprodev/ezy-image-viewer-releases. It has not published
a production binary yet.

Before applying for signing, could you clarify:

1. What existing-release evidence is acceptable for a new project? Does an
   unsigned prerelease satisfy “already released in the form that should be
   signed,” or should the project provide another form of evidence?
2. Can a self-contained Windows App SDK application that redistributes Microsoft
   runtime components under Microsoft Software License Terms qualify under your
   System Library exception?
3. Does the free OSS workflow support an ordered WiX Burn flow with two remote
   signing stages: detached engine signing, local reattachment, and final bundle
   signing?
4. Can the workflow sign and verify two MSIX packages, three MSI packages, the
   detached Burn engine, and the final bundle as one release approval?
5. For a single-maintainer project, may the same person hold the Author,
   Reviewer, and Approver roles? If not, what minimum role separation or
   compensating control is required?
6. Are there any eligibility restrictions for an individual maintainer residing
   in South Korea?

The project will not submit artifacts until the license issue identified in its
Windows App SDK WinUI dependency is resolved.
```

## Public source publication gates

- [x] Obtain explicit approval to create and publish the source repository.
- [x] Run Gitleaks 8.30.1 and manually review the exact 353-file staged tree.
- [x] Use a reviewed clean source snapshot; do not publish the existing author
      email, local Git history, collaboration state, or internal audit history.
- [x] Configure protected `main` with pull requests, administrator enforcement,
      linear history, conversation resolution, and strict `build-test` status.
- [ ] Verify source-host account MFA and configure SignPath project roles.

## Gates before a signing application or production binary

- [ ] Resolve the Windows App SDK WinUI production-license blocker.
- [ ] Confirm SignPath treatment of self-contained Microsoft redistributables.
- [x] Acquire and package the PDFium/Chromium notice set (version-pinned from
      upstream `chromium/7690`, hash-locked in the CodecHost release contract;
      final legal review still pending under the overall legal gate).
- [ ] Confirm SignPath's minimum role-separation requirement for a
      single-maintainer project.
- [ ] Obtain explicit approval before sending the SignPath inquiry.
- [ ] Confirm whether the published Portable testing prerelease is acceptable
      existing-release evidence.
- [x] Obtain explicit approval before the first Portable testing artifact upload.
- [ ] Obtain explicit approval before the first signing request.
