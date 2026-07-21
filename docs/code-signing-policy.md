# Code signing policy

Status: **preparation only — SignPath Foundation has not accepted this project**
Last reviewed: 2026-07-19

This policy becomes an active public release policy only after the project has
a public source repository, the required repository protections and MFA are
verified, and SignPath Foundation accepts the application. Until then, no
artifact may be described as production signed.

## Planned provider disclosure

If the application is accepted, the project home page and every signed download
page will include the required disclosure:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

The expected Authenticode publisher would therefore be **SignPath Foundation**,
not the project name or maintainer. No SignPath certificate, subscription, or
production publisher is currently configured.

## Signing scope

One approved release requires seven ordered Authenticode operations:

1. CodecHost framework identity MSIX;
2. main application identity MSIX;
3. fixed per-user application MSI;
4. fixed per-machine application MSI;
5. registry-only scope-anchor MSI;
6. detached WiX Burn engine executable;
7. reattached final Burn setup executable.

The MSIX identity packages are installer payloads rather than standalone public
downloads in the planned channel. The public release set is the two fixed-scope
MSI files, the Burn setup executable, checksums, the release manifest, the SBOM,
and required license/source notices.

SignPath documents Authenticode support for PE, MSI, and MSIX formats, but the
ordered WiX Burn detach/sign/reattach/final-sign workflow has not been confirmed
for the free OSS service. Signing integration must not be implemented by
weakening or bypassing that order.

## Source, build, and review requirements

- Signed artifacts must be produced from the public source repository and its
  versioned build scripts using locked dependencies.
- CI actions must be pinned to full commit hashes and use read-only repository
  permissions unless a narrower reviewed permission is required.
- Pull requests from non-maintainers require review before merge.
- Release source, dependency locks, signing configuration, and build workflow
  changes receive the same review as product code.
- Every signing request requires a manual approval after the unsigned artifact
  passes the repository's build, test, package, checksum, SBOM, and static
  verifier gates.
- Unsigned or self-signed binaries are not public production releases.

The public-source CI validates unsigned package structure but does not publish
those binaries as GitHub Actions artifacts. A provider adapter and SignPath
artifact configuration do not yet exist.

## Unsigned Portable testing channel

The user approved one narrow exception on 2026-07-19: an identity-free Basic
Portable ZIP may be published as a GitHub prerelease for evaluation and testing.
It is built from the public source commit with locked dependencies, includes the
runtime license inventory and copied license files, publishes a SHA-256 file,
and is verified as unsigned. It contains neither MSI/MSIX/Burn envelopes nor the
isolated CodecHost payload.

This exception does not activate the production signing policy, authorize live
operating reliance, satisfy SignPath eligibility by itself, or weaken any
production installer gate. The download page and archive must identify the
Engineering Preview dependency terms, unsigned SmartScreen behavior, missing
package identity, and manual removal/update behavior.

## Roles and access

The planned single-maintainer mapping is:

| Role | Member | Responsibility |
|---|---|---|
| Author | `koprodev` | Product source and build-script maintenance |
| Reviewer | `koprodev` | Review of outside contributions and release-critical changes |
| Approver | `koprodev` | Manual approval of each signing request |

The public repository is owned by `koprodev`; this table does not claim that a
SignPath role assignment already exists. MFA for the source host and SignPath
account must be verified before application. Signing credentials and provider
tokens must never be committed to the repository or printed in build logs.

## Release verification

Before publication, every signed candidate must pass:

- Windows trust verification of the exact signer and RFC 3161 timestamp;
- exact Publisher agreement across both identity MSIX manifests;
- MSI database, Burn extraction, package inventory, and checksum verification;
- a clean Windows VM lifecycle covering install, launch, repair, upgrade,
  rollback, removal, and the separate CodecHost identity boundary;
- final license, privacy, source-notice, and SBOM review.

The release process remains fail-closed if SignPath rejects, pauses, or revokes
the project, or if any expected signer, timestamp, artifact, or verification
evidence is missing.

## Incident response

A suspected signing compromise, unauthorized signing request, unexpected
artifact, or policy violation blocks further releases. The maintainer must
preserve the relevant source commit and CI evidence, notify SignPath through an
approved channel, request revocation when warranted, and publish a clear
security notice through the public source repository. A replacement release
must use a newly approved build and signing request; an unsigned replacement is
not an acceptable production fallback.

## Related policies

- [Privacy policy](privacy.md)
- [SignPath readiness checklist](signpath-readiness.md)
- [Release process](release-process.md)
- [Third-party notices](../THIRD-PARTY-NOTICES.md)
