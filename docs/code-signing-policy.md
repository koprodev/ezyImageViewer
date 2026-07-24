# Code signing policy

Status: **preparation only — SignPath Foundation has not accepted this project**
Last reviewed: 2026-07-24

This policy becomes active only after the public-source, licensing, account
security, and SignPath approval gates pass. Until then, no artifact is a
production-signed release.

## Required disclosure

If the project is accepted, the project page and every signed download page
will include:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

The expected Authenticode publisher is SignPath Foundation. No SignPath
certificate, project, subscription, token, or production publisher is
configured today.

## Artifact scope

The current installer design needs these ordered Authenticode operations:

1. main application identity MSIX;
2. per-user application MSI;
3. per-machine application MSI;
4. detached WiX Burn engine;
5. reattached final Burn setup.

The identity MSIX is an installer input, not a separate public download. The
planned public set consists of the scope-selecting Setup, checksums, release
manifest, SBOM, and required license/source notices.

The SignPath service must explicitly support the WiX detach, remote engine
sign, reattach, and final bundle sign sequence. The build must not bypass or
weaken that sequence to fit a provider.

## Build and approval rules

- Build only from the reviewed public source commit with locked dependencies.
- Pin CI actions to full commit hashes and grant the minimum permissions.
- Review release workflow, dependency, signing configuration, and source changes.
- Require manual approval after build, test, package, checksum, SBOM, and static
  verification pass.
- Keep signing credentials and provider tokens out of source and logs.
- Verify exact signer, timestamp, Publisher, payload, and checksum after signing.
- Unsigned or self-signed binaries are not public production releases.

The current public CI validates unsigned artifacts. No SignPath provider adapter
or artifact configuration exists.

## Approved testing exceptions

Two unsigned prerelease channels are approved for personal evaluation and
testing:

- the folder-based Basic Portable ZIP;
- the WiX Setup plus compressed single-file Portable set.

They must remain `NotSigned`, carry SHA-256 metadata, identify SmartScreen and
Engineering Preview limits, and avoid production or trust claims. These
exceptions do not satisfy SignPath eligibility, production signing, clean-VM
lifecycle, timestamp, or legal-clearance gates.

## Roles

| Role | Member | Responsibility |
|---|---|---|
| Author | `koprodev` | Source and build maintenance |
| Reviewer | `koprodev` | Outside contribution and release-critical review |
| Approver | `koprodev` | Manual approval of each signing request |

This records the planned single-maintainer model only. SignPath must accept the
role arrangement, and source-host plus SignPath MFA must be verified first.

## Release verification

Every signed candidate must pass:

- Windows trust validation for the exact signer and RFC 3161 timestamp;
- exact Publisher agreement between the executable and main identity package;
- MSI database, Burn extraction, inventory, checksum, and SBOM verification;
- clean Windows VM install, launch, repair, upgrade, rollback, and removal;
- final source notice, license, and privacy review.

Missing signer, timestamp, approval, artifact, or evidence blocks the release.
SignPath rejection, suspension, or revocation also blocks publication.

## Incident response

A suspected signing compromise or unauthorized request stops releases. Preserve
the source commit and CI evidence, contact SignPath through an approved channel,
request revocation when warranted, and publish a clear notice. A replacement
requires a new reviewed build and approval; an unsigned binary is not a
production fallback.

## Related documents

- [Privacy policy](privacy.md)
- [SignPath readiness checklist](signpath-readiness.md)
- [Release process](release-process.md)
- [Third-party notices](../THIRD-PARTY-NOTICES.md)
