# M8-A/M8-B format corpus

The repository tracks the corpus contract, not third-party binary samples. Set
`EZYIMAGEVIEWER_FORMAT_CORPUS` to a local cache whose paths match
`corpus-manifest.json`. Every sample must declare its source, SPDX license (or an
explicit redistribution restriction), and SHA-256 before it can enter the gate.

`EZYIMAGEVIEWER_REQUIRE_COMPLETE_FORMAT_CORPUS=1` turns the normal validation test
into the release gate: every M8-A extension must contain at least 30 normal samples,
all referenced files must exist, and every digest must match. The test never downloads
or rewrites corpus files.

Golden images are addressed by relative path and SHA-256 in the same manifest. Keep
generated or third-party binaries outside git unless their license and repository size
impact have been reviewed explicitly.

## Isolated-codec contract (schema v2)

PDF and PSD entries use schema v2. Every codec sample has a stable `id`, one or
more `scenarios`, producer name/version/platform, and exact expected Direct Host
inspect/decode results plus the installed-product outcome. Successful metadata is
exact: page count and native dimensions are not ranges. Password-protected samples
must carry an explicit `password` field; filenames never determine the expectation.
Within each format, input paths and SHA-256 digests are unique, and the normal set
must span at least two distinct producer names. Generic-format samples cannot use
codec-only fields.

Every successful Host baseline has a golden for the same page and target. A golden
records that page's native width and height, its reference renderer and version,
SHA-256 digest, sRGB premultiplied-BGRA8 contract, and explicit maximum channel
delta, alpha delta, changed-pixel ratio, and mean absolute error. No tolerance may
exceed the release fidelity policy: RGB channel delta 64, alpha delta 64,
changed-pixel threshold 16, changed-pixel ratio 10%, and mean absolute error 4.0.
Decode targets are limited to the Host boundary of 65,500 pixels. PDF and PSD each
require at least 30 normal samples plus at least one large, boundary, corrupt, and
security sample and the format-specific scenario matrix.

Requirement 14.2 also calls for small files, but it does not define a byte-size
threshold. The release corpus must therefore curate and document small examples;
the executable gate does not invent a numeric cutoff and the tracked empty corpus
does not yet satisfy that curation requirement.

Scenario expectations are bidirectional. Rendering, layer, color, ICC, and alpha
scenarios require exact Host/product success and a golden. Encrypted PDF requires
password refusal, malformed structure requires corrupt-input refusal, compression
bombs require resource-limit refusal, and slow-render cancellation requires Host
success plus an explicit product cancellation delay. Combining scenarios with
incompatible outcomes is invalid.

`EZYIMAGEVIEWER_RUN_CODEC_CORPUS=1` runs the Direct Host gate. It proves exact
CodecHost protocol results and pixel goldens only; it is not proof of product
activation, AppContainer isolation, inherited-handle transport, or profile reset.

The installed-product boundary is a separate opt-in gate selected by
`EZYIMAGEVIEWER_RUN_INSTALLED_CODEC_CORPUS=1`. It uses the installed framework package
through `DocumentLoader.LoadFileAsync`, verifies exact user-facing outcomes, inherited
read handles, and per-request profile reset. For a `canceled` product outcome, the
Direct Host gate treats the same baseline as an exact success and checks its golden;
the installed-product gate instead cancels through `CancellationToken` after the
manifest's explicit `cancellationAfterMilliseconds` delay and requires cancellation
to cross the installed Host boundary.

Both codec gates are real xUnit skips by default. Once either gate is opted in, an
empty or incomplete PDF/PSD corpus fails closed before files or installed packages
are exercised. No external corpus or golden binary is stored in this repository, so
the tracked empty manifest is not evidence that either opt-in gate passes.
