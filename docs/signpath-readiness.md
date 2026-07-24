# SignPath Foundation readiness checklist

Status: **public source repository and CI active; unsigned testing previews approved; no SignPath application or signing transfer performed**
Assessment date: 2026-07-24

The preferred zero-cost public-trust route is SignPath Foundation. The project
has not sent an inquiry, submitted identity or account data, created a SignPath
project, or requested a signature. Each external contact remains a separate
user approval gate.

## Current assessment

| Requirement | Evidence | Status |
|---|---|---|
| Project license | Root `LICENSE` is MIT | Ready |
| Public source | Reviewed allowlisted snapshot at `koprodev/ezyImageViewer` | Ready |
| Privacy disclosure | `docs/privacy.md` contains the required no-transfer statement | Ready |
| Signing policy | Published and linked from the root README | Ready |
| CI | Locked restore, build, tests, packaging contracts, checksums | Available; SignPath integration absent |
| Existing release | Unsigned Portable and Setup testing previews | SignPath interpretation required |
| Runtime licensing | Windows App SDK WinUI carries Engineering Preview terms | **Blocked** |
| Proprietary components | Self-contained Microsoft runtime treatment is unconfirmed | **Blocked** |
| MFA and roles | Single-maintainer model documented | Not verified with SignPath |
| Manual approval | Policy requires it | SignPath project does not exist |

An unsigned prerelease is published because the user approved an evaluation
channel. It is not evidence that SignPath's existing-release requirement has
been satisfied.

## Public-source boundary

The public snapshot is created from
`packaging/public-source-allowlist.txt`. It excludes local Git history,
collaboration state, internal instructions, local audits, private design
material, and credentials. `PUBLIC-SOURCE-MANIFEST.json` records the source
commit and exact hashes.

The public repository is `https://github.com/koprodev/ezyImageViewer`.
Unsigned release workflows may publish only the explicitly approved testing
artifacts. Test-result artifacts remain separate from release binaries.

## Blocking license questions

1. `Microsoft.WindowsAppSDK` currently brings in a WinUI package whose license
   identifies it as **Microsoft Windows App SDK Engineering Preview** and limits
   live operating use. A production release remains blocked until the terms are
   clarified or a reviewed release-safe dependency replaces it.
2. The self-contained build redistributes Microsoft components under Microsoft
   Software License Terms. SignPath must confirm whether they qualify under its
   System Library exception.
3. WiX Toolset source and the modified theme use MS-RL. Its current maintenance
   terms and the project's revenue status must be rechecked before a public
   production release.

PDF and PSD are permanently unsupported. Their former native decoder packages,
framework identity, notice bundle, and signing operation are no longer part of
the product or application.

## Questions for a future inquiry

Do not contact SignPath until the user explicitly approves it.

- Does an unsigned testing prerelease satisfy the existing-release requirement?
- Does the self-contained Windows App SDK runtime qualify as a System Library?
- Can the free OSS workflow sign a detached Burn engine and then the final bundle?
- Can one approval cover the main identity MSIX, two MSI files, engine, and bundle?
- May one maintainer hold Author, Reviewer, and Approver roles?
- Are there eligibility restrictions for an individual maintainer in South Korea?

## Checklist

- [x] Publish a reviewed allowlisted source snapshot.
- [x] Publish code-signing and privacy policies.
- [x] Keep unsigned testing artifacts separate from production claims.
- [x] Remove the retired PDF/PSD decoder and framework identity from signing scope.
- [ ] Resolve the Windows App SDK WinUI production-license blocker.
- [ ] Confirm SignPath treatment of Microsoft redistributables.
- [ ] Verify source-host MFA and configure accepted SignPath roles.
- [ ] Obtain explicit approval before contacting SignPath.
- [ ] Obtain explicit approval before the first signing request.
- [ ] Complete production signing and clean-VM lifecycle evidence.
