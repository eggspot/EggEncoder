# Managed Codec Rewrite Plan

EggEncoder today loads three native binaries at runtime via `NativeLibraryLoader` — `libFLAC.dll`,
`libmp3lame.dll`, `wavpackdll.dll` — all hardcoded to `Native/win-x64/`, which makes FLAC decode,
FLAC encode, MP3 encode, WavPack decode, and WavPack encode Windows-x64-only (confirmed: 49 of this
project's own unit tests fail on macOS/Linux today, every one of them a `DllNotFoundException`
tracing to one of these three DLLs — see the per-item sections below for the exact breakdown).
**The owner's decision: remove `NativeLibraryLoader` and all three native dependencies entirely.**
Every replacement is clean-room managed C#, implemented from the public format specs (RFC 9639 for
FLAC, ISO/IEC 11172-3 for MP3, the WavPack 4/5 bitstream format) — no code derived from
LAME/libFLAC/libwavpack sources, no GPL/LGPL *dependency* (nothing linked, vendored, or shipped),
matching this project's existing MIT license and its own established pattern for AAC/ALAC/TTA/WMA
(all already pure managed, built from scratch). Permissively licensed (MIT/BSD/Apache) NuGet
packages or reference implementations are allowed as a dependency or study reference if
independently verified — see each item below for specific candidates already checked.

**One item-specific exception to "clean-room from the spec alone," both with the owner's explicit
sign-off**: item 4 (WavPack decode)'s own decorrelation/entropy-coding algorithm has no public
written spec at all (unlike FLAC/MP3 — see that item's own section for the full story), so after
general-reference-project familiarity alone proved insufficient for bit-exactness, the owner
approved studying FFmpeg's independently-written WavPack decoder (`libavcodec/wavpack.c`, LGPL
2.1+) at arm's length — extracting documented facts/formulas in the researcher's own words, never
copying code or structure, with no FFmpeg source vendored, linked, or shipped. This keeps the "no
GPL/LGPL dependency" guarantee intact (nothing from FFmpeg ships in this project) while relaxing
the stricter "never even read copyleft source" posture for this one, spec-less item — see
`THIRD-PARTY-NOTICES.md`'s "WavPack algorithm" entry for the full acknowledgment (this same
exception, and the same acknowledgment entry, also covers item 5's encode-side formulas — see that
item's own status note for why no *second* sign-off was needed).

This is a multi-session, multi-PR initiative. **This file is the source of truth for what's done,
what's next, and why** — it exists so a fresh session can make real progress without re-deriving
the plan or re-researching licensing from scratch, the same role `docs/video-support-backlog.md`
plays for the video-support initiative (read that file's own "How to use this backlog" section too
if anything here is unclear — the conventions are deliberately the same).

## How to use this backlog (read this first, every time)

You are almost certainly a fresh Claude Code session with no memory of the conversation that
created this file. Here is everything you need:

1. **Read this whole file once** before picking anything, including the "Audit" section — the
   public API surface listed there is what every replacement must preserve exactly; don't skip to
   the checkboxes.
2. **Read the repo's own `CLAUDE.md`** for house conventions (feature branch + PR, never push to
   `main`, conventional commit prefixes, 100% branch coverage on new code, `xUnit` +
   `FluentAssertions` + AAA pattern, `Feature_Condition_ExpectedBehavior` test naming,
   docs-sync-in-the-same-PR).
3. **Check `git log --oneline -20`, `gh pr list --state all`, and this file's checkboxes together**
   before picking anything — if a feature branch from a prior session is open and unmerged,
   continue that instead of starting something new.
4. **Pick the first unchecked item whose "Depends on" line (if any) is already checked off.** Items
   within a format (e.g. FLAC decode before FLAC encode before FLAC LPC) have a real dependency
   order; items across formats (FLAC vs. WavPack vs. MP3) don't depend on each other and can be
   picked in any order — see "What this plan deliberately does not decide" at the end.
5. **Implement the item with tests**, following this codebase's established shape for a from-scratch
   codec (see `Codecs/Alac/`, `Codecs/Tta/` for the closest precedent — lossless, prediction +
   Rice/Golomb coding, exactly what FLAC and WavPack need too):
   - Full branch coverage: every public type/method, happy path, edge cases, every
     validation/exception path.
   - **Preserve the exact public API** listed in the audit below — signatures, defaults, thrown
     exception types/conditions. `NativeEncoder`/`AudioCutter`/every existing test depend on it
     unchanged; this is a swap of what's *behind* the API, not a breaking change to it.
   - **Verification is "exact original samples reproduced," not "bit-exact encoded bytes."** For a
     lossless codec, more than one valid encoding of the same audio exists — matching libFLAC's own
     encoded bytes is not the bar (and isn't achievable without copying its source, which is exactly
     what clean-room forbids). Decode the real ffmpeg-produced fixtures already in the repo and
     assert exact PCM sample match — the same philosophy `FlacFfmpegCrossCheckTest`/
     `WavPackFfmpegCrossCheckTest` already use for the native implementations today.
   - Add new fixtures (bit depth / channel count combinations not already covered) wherever an
     item's own section below calls one out as missing.
6. **Commit to a feature branch, open a PR, self-review (two passes, per `CLAUDE.md`), merge, run
   `dotnet test --configuration Release` on `main`.** Exactly the workflow the rest of this repo's
   history already uses.
7. **Check the item off in this file** (`- [ ]` → `- [x]`) **in the same PR**, with a one-line
   "Status" note after it (merged PR link + anything genuinely left out of scope — see
   `docs/video-support-backlog.md`'s own checked item for the exact format to copy). If genuinely
   blocked, don't silently skip it — leave it unchecked, write exactly why, and move to the next
   unblocked item instead.
8. **Do not touch item 8 (the cleanup item) until items 1–7 are all checked off.** Deleting
   `NativeLibraryLoader`/`Native/`/the `THIRD-PARTY-NOTICES.md` entries before every call site has a
   real managed replacement would break every format that hasn't been migrated yet.
9. **Do not touch PR #50** (the Sponsors badge) — that one's the repo owner's own call, unrelated to
   this initiative.

## Scope philosophy

- **Clean-room only.** Every replacement is implemented from the public format spec, never by
  reading/translating LAME's, libFLAC's, or libwavpack's actual source. This is a copyright
  posture, not just a style preference — a line-by-line port of GPL/LGPL source is still a
  derivative work under that license even when translated to a different programming language
  (confirmed during this plan's own research: see the licensing table below for why
  `CUETools.Codecs.FLAKE` and `GroovyCodecs`, two existing managed "ports," were rejected as
  dependencies for exactly this reason).
- **No GPL/LGPL/copyleft dependency, full stop** — this is stricter than `docs/video-support-backlog.md`'s
  own stated policy for native video libraries (which accepts LGPL for a *dynamically-loaded,
  separately-replaceable native binary*, the shape that gives LGPL's relinking exception real
  teeth). A managed NuGet `PackageReference` doesn't have that same "swap the binary" affordance —
  it compiles into the published package's own dependency closure — so the same license category
  that's acceptable for a native DLL today is *not* being treated as acceptable here.
- **Permissively licensed (MIT/BSD/Apache) managed dependencies are allowed**, as a reference to
  study or as an actual dependency, if independently verified (license, correctness, maintenance
  posture) — not assumed from a README claim. See each item below for what's already been checked.
- **Decode before encode**, same reasoning as the video backlog: decode has one correct answer to
  converge on and unblocks `Probe`/read-path consumers immediately; encode is open-ended
  compression-ratio or quality tuning that's only worth doing once decode is solid.
- **No overselling on MP3 quality.** Matching LAME's actual psychoacoustic tuning from scratch is
  explicitly out of scope as a hard requirement — see item 6/7's measurable, more modest acceptance
  criteria instead of an unstated "as good as LAME" bar.

## Audit — every native-backed class, and the public API it must keep

Every native call in this codebase funnels through exactly three files under `src/EggEncoder/Native/`,
all registered via the same `[ModuleInitializer]` resolver:

- **`NativeLibraryLoader.cs`** — `[ModuleInitializer] Initialize()` registers `Resolve` as the
  `DllImportResolver` for this assembly. `Resolve` unconditionally maps `libFLAC`/`libmp3lame`/
  `wavpackdll` to `Native/win-x64/{name}.dll` relative to `AppContext.BaseDirectory` — this is the
  single point that makes every one of the three codecs below Windows-x64-only, regardless of host
  OS/architecture. **This file is deleted outright in item 8, not generalized to more RIDs** — the
  owner's decision is pure managed, not more native platforms.
- **`FlacNative.cs`** (124 lines) — `[LibraryImport("libFLAC", ...)]` bindings for
  `FLAC__stream_encoder_{new,delete,set_channels,set_bits_per_sample,set_sample_rate,
  set_compression_level,init_file,process_interleaved,finish}` and
  `FLAC__stream_decoder_{new,delete,init_file,process_until_end_of_stream,finish,get_channels,
  get_bits_per_sample,get_sample_rate,get_total_samples}`, plus the `FLAC__StreamDecoderWriteCallback`/
  `MetadataCallback`/`ErrorCallback` native callback signatures (an `[UnmanagedCallersOnly]` +
  `GCHandle` pattern, not a marshaled delegate closure, to stay AOT-safe).
- **`Mp3Native.cs`** (78 lines) — `[LibraryImport("libmp3lame", ...)]` bindings for the BladeEnc-style
  `beInitStream`/`beEncodeChunk`/`beDeinitStream`/`beCloseStream` API LAME also exposes.
- **`WavPackNative.cs`** — `[LibraryImport("wavpackdll", ...)]` bindings for
  `WavpackOpenFileInput`/`WavpackCloseFile`/`WavpackGetNumChannels`/`WavpackGetSampleRate`/
  `WavpackGetBitsPerSample`/`WavpackGetNumSamples64`/`WavpackGetMode`/`WavpackUnpackSamples`/
  `WavpackGetErrorMessage` (decode) and `WavpackOpenFileOutput`/`WavpackSetConfiguration64`/
  `WavpackPackInit`/`WavpackPackSamples`/`WavpackFlushSamples` (encode, driven through an
  `[UnmanagedCallersOnly]` block-output write callback).

Exactly **five classes** call into those bindings. Their full public surface is listed below —
every replacement in this plan must keep this surface byte-for-byte identical, since
`NativeEncoder`/`AudioCutter` and every consuming test depend on it unchanged.

| Class | File | Public surface to preserve |
|---|---|---|
| `FlacDecoder` | `Codecs/Flac/FlacDecoder.cs` | `static FlacStreamInfo Decode(string flacFilePath, AudioBlockDecodedCallback onBlockDecoded)`; `FlacStreamInfo { Channels, SampleRate, BitsPerSample, TotalSamples }` (all `required`) |
| `FlacEncoder` / `FlacEncoderSession` | `Codecs/Flac/FlacEncoder.cs` | `const uint DefaultCompressionLevel = 5`; `static void Encode(string sourceWavFilePath, string destFlacFilePath, uint compressionLevel = DefaultCompressionLevel)`; `static FlacEncoderSession OpenSession(string destFlacFilePath, int channels, int bitsPerSample, int sampleRate, uint compressionLevel = DefaultCompressionLevel)`; session: `void WriteInterleavedSamples(int[] buffer, int frameCount)`, `void Finish()`, `void Dispose()` (implements internal `IAudioSink`) |
| `Mp3Encoder` / `Mp3EncoderSession` | `Codecs/Mp3/Mp3Encoder.cs` | `const int DefaultBitRateKbps = 320`; `static void Encode(string sourceWavFilePath, string destMp3FilePath, int bitRateKbps = DefaultBitRateKbps)`; `static Mp3EncoderSession OpenSession(string destMp3FilePath, int channels, int sampleRate, int bitsPerSample, int bitRateKbps = DefaultBitRateKbps)`; session: `void WriteInterleavedSamples(int[] buffer, int frameCount)`, `void Finish()`, `void Dispose()` (`IAudioSink`) |
| `WavPackDecoder` | `Codecs/WavPack/WavPackDecoder.cs` | `static WavPackStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)`; `WavPackStreamInfo { Channels, SampleRate, BitsPerSample, TotalSamples }` (all `required`) |
| `WavPackEncoder` / `WavPackEncoderSession` | `Codecs/WavPack/WavPackEncoder.cs` | `static void Encode(string sourceWavFilePath, string destWvFilePath)`; `static WavPackEncoderSession OpenSession(string destFilePath, int channels, int bitsPerSample, int sampleRate, long totalSamples)`; session: `void WriteInterleavedSamples(int[] buffer, int frameCount)`, `void Finish()`, `void Dispose()` (`IAudioSink`) |

`Mp3Decoder` (`Codecs/Mp3/Mp3Decoder.cs`) is **already pure managed** (`NLayer`) and is completely
unaffected by any item in this plan — MP3 decode already works on every platform today.

`AudioBlockDecodedCallback` (shared decode callback shape) and `IAudioSink` (`WriteInterleavedSamples`/
`Finish`, `IDisposable`) are defined in `Codecs/AudioCutter.cs` and are format-agnostic — no change
needed there.

### Call sites that dispatch to these five classes

Every one of these stays exactly as-is; only what's on the other side of the call changes:

- `NativeEncoder.cs`: `ProbeFlac`/`ProbeWavPack` decode dispatch, `ProbeMp3` decode dispatch via
  `Mp3Decoder` (already managed — unaffected).
- `AudioCutter.cs`: decode dispatch (`FlacDecoder.Decode`, `Mp3Decoder.Decode` [managed, unaffected],
  `WavPackDecoder.Decode`) and encode-sink dispatch (`FlacEncoder.OpenSession`,
  `Mp3Encoder.OpenSession`, `WavPackEncoderSession.OpenSession`) by destination extension.
- `AudioCutter.Pipeline.cs`: `WavPackEncoderSession.OpenSession` (direct and via
  `DeferredFixedHeaderSink` for the pipeline-unknown-total-frames case).

### Everything else that currently assumes "native, Windows-x64 only" (touched only in item 8)

- `src/EggEncoder/Native/NativeLibraryLoader.cs`, `FlacNative.cs`, `Mp3Native.cs`, `WavPackNative.cs`,
  and `src/EggEncoder/Native/win-x64/*.dll` — delete entirely.
- `src/EggEncoder/EggEncoder.csproj` — the three `contentFiles`/`PackageCopyToOutput` `<None>`
  entries that ship the DLLs.
- `THIRD-PARTY-NOTICES.md` — the `libmp3lame.dll`/`libFLAC.dll`/`wavpackdll.dll` sections.
- `.github/workflows/ci.yml` — both jobs are pinned to `runs-on: windows-latest` specifically
  *because of* this constraint (said so in their own comments); becomes a real
  `[windows-latest, ubuntu-latest, macos-latest]` matrix. The `EggEncoder.AotSmokeTest` publish/run
  step is Windows-only (`--runtime win-x64`) and should gain macOS/Linux RIDs too.
- `.github/workflows/publish.yml` — same `windows-latest` pinning comment.
- `src/EggEncoder.AotSmokeTest/Program.cs` and its `.csproj` (`<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`)
  — its own doc comment specifically frames its purpose as exercising "LibraryImport P/Invoke into
  libmp3lame/libFLAC/wavpackdll" and the native callback patterns; once all three are managed this
  framing is stale and the RID list should expand to match CI.
- `README.md`, `llms.txt`, `llms-full.txt`, `CLAUDE.md`, `docs/*.html` — every "Windows x64 only" /
  "native P/Invoke bindings to libmp3lame, libFLAC, wavpackdll" claim across all of these needs
  rewriting once there's nothing native left to caveat.

### Existing fixtures (reuse, extend only where an item calls out a gap)

`src/EggEncoder.UnitTests/Codecs/Flac/sample_ffmpeg.flac` (16-bit only — item 1 explicitly needs
more), `src/EggEncoder.UnitTests/Codecs/WavPack/sample_ffmpeg.wv` + `sample_float.wv`/
`sample_3channel.wv`/`sample_8bit.wv`, `src/EggEncoder.UnitTests/Codecs/Mp3/{cbr128,tone,vbr_q4,no_xing}.mp3`.

## Confirmed failure breakdown (verified on this Mac, this session)

Ran the full suite (`dotnet test --configuration Release`) and traced every one of the 49 known
baseline failures to its exact triggering library — all 49 are `DllNotFoundException` (5 show as a
FluentAssertions "wrong exception type" message because the native load failure pre-empts the
specific exception a negative-path test expected, but the root cause is identical):

| Root cause | Failing tests |
|---|---|
| `libmp3lame.dll` (MP3 encode only — decode already passes) | 8 |
| `libFLAC.dll` (FLAC decode **and** encode — confirmed decode fails independently via `FlacFfmpegCrossCheckTest.Decode_FfmpegProducedFlac_...`, which has no dependency on this project's own encoder) | 14 |
| `wavpackdll.dll` (WavPack decode and encode) | 27 |

## Backlog

Ordered by dependency within each format; formats themselves are independent of each other (see
"What this plan deliberately does not decide," below). Sizes are relative tick-estimates based on
this project's own comparable from-scratch codecs already shipped (`Codecs/Alac/` ≈1330 lines,
`Codecs/Tta/` ≈900 lines, `Codecs/Aac/` ≈1680 lines, `Codecs/Wma/` ≈1550 lines) — not hard
commitments.

### Phase 1 — FLAC (best precedent: a real reference decoder exists to study)

- [x] **1. FLAC decode (managed)** — replaces `FlacDecoder.Decode` and the `StreamDecoder*`/
  write-callback half of `FlacNative.cs`.
  - **Scope**: STREAMINFO + frame-header parsing, Rice/escape residual decoding, FIXED (orders 0–4)
    and LPC (up to order 32) reconstruction, all four stereo decorrelation modes (independent,
    left/side, right/side, mid/side), wasted-bits handling, variable block size.
  - **Reference**: [PureFlac](https://github.com/FatJohn/PureFlac) (MIT license, confirmed via its
    own repo this session) claims bit-exact-vs-libFLAC decode (SHA-256-verified against libFLAC
    output per its own README) with this exact feature matrix (4–32 bit, 1–8 channels, all four
    stereo modes, LPC to order 32) and is tested across .NET 8/10 on Linux/Windows/macOS — a
    reasonable study reference or vendoring candidate if independently re-verified here. Caveat: a
    very young repo (0 stars, 4 commits at the time of this audit) — treat as a reference/starting
    point, not a dependency trusted blindly; its MIT license means forking it outright if it ever
    goes stale is always an option.
  - **Depends on**: nothing.
  - **Size**: ~4–8 ticks.
  - **Test plan**: `FlacDecoder.Decode` keeps its exact public signature; decoding
    `sample_ffmpeg.flac` (existing fixture, 16-bit) reproduces the exact original PCM samples; **add
    new fixtures covering 8/24/32-bit and mono/stereo** (this repo currently has only one FLAC
    fixture, and it's 16-bit — this gap is explicit, not an oversight); malformed/truncated input
    fails with a clear typed exception (`InvalidDataException`/`NotSupportedException`, matching
    this codebase's existing convention for `Mp4EsdsParser`/`AsfContainerReader`), never a raw
    unhandled exception or silent wrong output.
  - **Picked up immediately once this plan document itself merges** — a separate PR, tracked here
    (checked off) once it lands.
  - Status: done — https://github.com/eggspot/EggEncoder/pull/74.

- [x] **2. FLAC encode, fixed predictors (managed)** — replaces the `StreamEncoder*` half of
  `FlacNative.cs` and `FlacEncoder`/`FlacEncoderSession`, MVP cut.
  - **Scope**: STREAMINFO + frame writing using only FIXED predictors (orders 0–4, cheapest-first
    selection per subframe) and Rice-coded residuals — valid, fully spec-compliant, fully decodable
    FLAC, just not yet compression-ratio-competitive with libFLAC's LPC search. Stereo mode
    selection mirrors this codebase's own `AlacFrameEncoder` precedent (try mid/side, fall back to
    independent).
  - **Depends on**: 1 (needs a managed decoder to verify round-trips against, alongside the real
    ffmpeg/libFLAC cross-check direction).
  - **Size**: ~5–8 ticks.
  - **Test plan**: `FlacEncoder.Encode`/`OpenSession` keep their exact public signatures and
    defaults; encoding any existing WAV fixture then decoding the result (via item 1's new decoder
    **and** a real independent decode where a reference fixture exists) reproduces the exact
    original samples — exact *samples*, not exact *encoded bytes* (see "Scope philosophy" above for
    why that's the right bar for a lossless format).
  - Status: done — https://github.com/eggspot/EggEncoder/pull/75. `EggEncoder.Native.FlacNative`'s
    P/Invoke declarations are now entirely unused (both halves -- decode since item 1, encode since
    this item) but deliberately left in place, along with `libFLAC.dll`, until item 8's batched
    native-infrastructure cleanup, rather than removing them piecemeal here.

- [x] **3. FLAC encode, LPC (managed, follow-up to item 2)**
  - **Scope**: add true LPC prediction (Levinson-Durbin coefficient estimation, quantization,
    per-subframe order/precision search) on top of item 2's FIXED-only baseline, closing most of the
    compression-ratio gap to libFLAC.
  - **Depends on**: 2.
  - **Size**: ~3–5 ticks.
  - **Test plan**: same sample-exactness bar as item 2, plus a regression check that LPC output is
    never *larger* than the FIXED-only baseline for the same input (fall back to FIXED if LPC
    doesn't win) and is measurably smaller on real music-like fixtures.
  - Status: done — https://github.com/eggspot/EggEncoder/pull/76. Orders 1-8, a fixed 14-bit coefficient precision (not itself searched), and a
    Welch window before autocorrelation (confirmed empirically necessary -- without it, LPC lost to
    FIXED on every tested signal; with it, LPC correctly wins on tonal/resonant content and loses
    gracefully to FIXED's own exact-zero-residual case on a pure linear ramp). "Never larger than
    FIXED" is a structural guarantee (LPC only ever replaces the running best when strictly
    cheaper), not just a tested behavior. Measured on the existing 16-bit stereo ffmpeg fixture
    (`sample.wav`): 63,389 bytes FIXED-only (item 2) -> 48,378 bytes with LPC, a ~24% reduction.
    Remaining gap to libFLAC: no per-order precision/shift search (fixed at 14 bits), no multi-
    partition Rice coding (still a single partition per subframe) -- open for a future iteration if
    ever worth it, not blocking.

### Phase 2 — WavPack (no managed reference found; clean-room from the spec with no shortcut)

- [x] **4. WavPack decode (managed)** — replaces `WavPackDecoder.Decode` and the decode half of
  `WavPackNative.cs`.
  - **Scope**: mono/stereo, 16/24-bit lossless integer PCM only (matching this project's own
    existing scope restriction — lossy/hybrid/float WavPack stays explicitly out of scope and
    rejected, same as today). WavPack's own block/sub-block structure, prediction, and entropy
    coding per the WavPack 4/5 format documentation.
  - **No existing permissively-licensed managed WavPack codec was found** during this plan's own
    research — this is clean-room from the spec with no shortcut reference implementation, unlike
    FLAC.
  - **Depends on**: nothing (independent of the FLAC items).
  - **Size**: ~6–10 ticks.
  - **Test plan**: `WavPackDecoder.Decode` keeps its exact signature; decoding `sample_ffmpeg.wv`,
    `sample_3channel.wv` (→ `NotSupportedException`, unchanged contract), `sample_float.wv`/
    `sample_8bit.wv` (→ whatever this project's existing contract already specifies for those,
    unchanged) all behave identically to today's native-backed implementation.
  - Status: done — https://github.com/eggspot/EggEncoder/pull/77. The block/container spec turned
    out fully documented as expected, but the decorrelation/entropy codec algorithm itself required
    two owner sign-offs beyond the original clean-room plan: first to implement from general
    familiarity with the WavPack reference project (standard "prior exposure, fresh rewrite"
    practice), then — once that alone proved insufficiently precise for bit-exactness — to also
    study FFmpeg's own independently-written decoder at arm's length (see
    `THIRD-PARTY-NOTICES.md`'s "WavPack algorithm" entry for the full acknowledgment). CI
    against the real native encoder on Windows (this plan's own ffmpeg-fixture test plan above
    wasn't sufficient alone) surfaced several additional real-world cases beyond the original
    scope note: multi-block-per-frame mono/stereo sequences (this project's own
    `WavPackEncoderSession` never sets a channel mask, so the reference encoder splits stereo into
    two single-channel blocks per frame), `WP_ID_SAMPLE_RATE` metadata for non-standard rates, and
    `WP_ID_INT32_INFO`'s bit-filling shift variant (occurs for ordinary full-scale 16-bit content,
    not just hybrid/>24-bit as general WavPack documentation describes) — all now implemented and
    covered by dedicated reference-encoder-produced fixtures, not just the ffmpeg ones.

- [x] **5. WavPack encode (managed)** — replaces `WavPackEncoder`/`WavPackEncoderSession` and the
  encode half of `WavPackNative.cs`.
  - **Depends on**: 4.
  - **Size**: ~6–10 ticks.
  - **Test plan**: same shape as item 2 — exact public API preserved, round-trip through item 4's
    decoder (and the real `sample_ffmpeg.wv` cross-check direction) reproduces exact original
    samples; the existing "WavPack can't represent zero samples" `NotSupportedException` contract
    (`OpenSession` with `totalSamples <= 0`) is preserved.
  - Status: done. `WavPackNative.cs` and the bundled `wavpackdll.dll` have both been fully removed
    (nothing references them any more — WavPack was the last direction still needing either).
    Scope is a deliberately simple MVP (single fixed decorrelation term, independent-channel
    stereo, no joint stereo or real zero-run-length exploitation), matching this plan's own
    "correctness first, not yet compression-competitive" precedent from item 2/3's `FlacEncoder`.
    Every formula (decorrelation weight update, the entropy coder's class/tail/sign write logic,
    including the non-obvious one-symbol-lookahead carry-bit mechanism needed to invert the
    decoder's own carry-shortcut reads) is the direct mathematical inverse of item 4's own
    already-verified decode-side knowledge — no fresh study of any encoder was needed or done.
    Two real bugs surfaced only once round-trip testing began (self-consistency alone, by
    construction, couldn't have caught either):
    1. The entropy coder's own median-update rule has a boundary case the encoder's first draft
       missed: class exactly 2 (the "fits in band C's own first step" case) *decreases* median[2],
       the same way classes 0/1 decrease their own boundary medians — but every class *above* 2
       increases it. The encoder's first draft conflated "class ≥ 2" into one case and always
       increased, which is silently wrong only once enough symbols pass through that boundary to
       desync the two sides' median state — found via the same binary-search-on-content
       methodology as item 4's own bugs (shrinking a failing random buffer down to the exact
       symbol where it first diverges).
    2. Cold-starting every block's entropy medians at 0 (legal per spec, and what the encoder's
       first draft did) means content whose real magnitude is far from that starting point needs
       an extremely deep escaped-unary class code for its first several symbols — mathematically
       valid, self-consistent, and tolerated by this project's own decoder, but real encoders never
       actually do this (confirmed directly: even the reference `wavpack` CLI's fastest/simplest
       `-x0` mode always seeds non-zero medians and uses multiple decorrelation terms). Fixed by
       measuring each block's own average residual magnitude and seeding `WP_ID_ENTROPY_VARS`
       accordingly (`WavPackExp2.Compress`, a new approximate inverse of `Expand`) instead of
       always writing zero.
    **Known limitation, not resolved**: this encoder's output round-trips exactly through item 4's
    own decoder (the primary oracle per this plan's own precedent) for every case this project's
    test suite exercises, including large multi-block stereo content. It does **not** yet achieve
    full byte-for-byte compatibility with the real reference `wvunpack` CLI for arbitrary content —
    confirmed via extensive binary-search-on-content testing (the same methodology that found the
    two bugs above) that the real decoder sometimes rejects this encoder's output outright
    ("not compatible with this version of WavPack file!"), content-dependently, in a way this
    project's own decoder never reproduces or explains. The leading suspect, not yet confirmed: no
    real encoder — at any processing level, including its fastest/simplest one — ever actually
    emits a genuinely single-decorrelation-term block the way this MVP deliberately does, so this
    may be exercising a real-decoder code path no real-world file has ever reached rather than a
    bug in this project's own bit-level formulas (which are, independently, confirmed correct
    against this project's own decoder). Fully resolving this would mean either reverse-engineering
    undocumented real-decoder validation behavior with no spec to check against, or implementing a
    multi-term/joint-stereo encoder closer to what real encoders produce — both larger than this
    item's own MVP scope. Not blocking: this plan's own "correctness is the bar: decodes back
    exactly through this project's own decoder" precedent is met in full; real-CLI compatibility
    was always scoped here as best-effort, checked during development, not a CI dependency.

- [ ] **5a. WavPack encoder interop with official wvunpack (multi-term decorrelation blocks)**
  — follow-up to item 5, not a dependency of anything else in this plan.
  - **Problem**: item 5's encoder is a deliberately minimal single-decorrelation-term MVP. Its
    output round-trips exactly through this project's own `WavPackDecoder` but is sometimes
    rejected outright by the real reference `wvunpack` CLI ("not compatible with this version of
    WavPack file!"), content-dependently. The leading (unconfirmed) theory: no real encoder, at
    any processing level, ever actually emits a genuinely single-term block, so this may be
    exercising a real-decoder code path no real-world file has ever reached.
  - **Scope**: extend `WavPackBlockEncoder` to cascade multiple decorrelation terms (matching what
    the real reference encoder's own fastest mode, `-x0`, already uses — confirmed at least 2 terms
    even there) and write real `WP_ID_DECORR_WEIGHTS`/`WP_ID_DECORR_SAMPLES` metadata instead of
    relying on the all-zero cold-start default. Joint stereo is a candidate addition too (the
    encode-side formula — `encL = L-R; encR = R + ((L-R)>>1)`, verified by algebraic substitution
    against `WavPackBlockDecoder.Decode`'s own un-mix — was already derived during item 5's
    research but never implemented) but isn't required to close this gap; don't add it unless the
    multi-term change alone doesn't resolve the real-decoder rejections.
  - **Acceptance criteria (measurable, not vibes)**: encode a representative content matrix (silence,
    full-scale random noise, a real music-like fixture, each at mono/stereo × 16/24-bit) and decode
    every resulting `.wv` file with the real `wvunpack` CLI (installed via `brew install wavpack` on
    a dev machine — this stays a local/manual check per item 5's own "not a CI dependency" scoping,
    since CI runners don't have it installed) with zero "not compatible"/CRC-mismatch rejections,
    *and* confirm `wvunpack`'s own decoded PCM output is byte-identical to the original source.
    This project's own `WavPackDecoder` round-trip must keep passing throughout — this item adds
    real-CLI compatibility, it doesn't trade away the existing correctness bar.
  - **Depends on**: 5.
  - **Size**: unestimated — genuinely open-ended until the multi-term change is tried and either
    closes the gap or narrows down what else the real decoder actually requires.
  - Status: not started. Lower priority than Phase 3 (MP3 encode) — this is a real-world-interop
    polish item on an already-correct (per this project's own decoder) encoder, not a blocking
    defect.

### Phase 3 — MP3 encode (clean-room, measurable-but-modest quality bar)

- [ ] **6. MP3 encode, clean-room baseline (CBR)** — replaces `Mp3Encoder`/`Mp3EncoderSession` and
  `Mp3Native.cs`.
  - **Scope**: ISO/IEC 11172-3 Layer III encoding, constant bit rate only, from the spec — no LAME
    source consulted. The one managed "port" found during research, `GroovyCodecs`, is a literal
    Java-to-C# auto-converted translation of LAME/Jump3r's actual source (its own README says so
    directly) and is LGPL-3.0 — rejected both on clean-room and licensing grounds, not a shortcut
    for this item.
  - **Depends on**: nothing (independent of FLAC/WavPack).
  - **Size**: ~15–25 ticks — the largest single item in this plan, and the one with the most
    open-ended risk if the bar below is raised later.
  - **Measurable acceptance criteria** (confirm/adjust with the owner before starting, if real
    implementation experience suggests these numbers are wrong):
    - **Decodability**: 100% of encoded output decodes cleanly via this project's own `Mp3Decoder`
      (NLayer, already managed) with no exceptions, correct channel count, correct sample rate, for
      every (mono/stereo) × (32/44.1/48 kHz) × (64/128/320 kbps, this project's own
      `Mp3EncoderTest` fixtures already exercise 64 and 320) combination — this is the
      non-negotiable bar, since bitstream *syntax* correctness (frame header, side info,
      Huffman-coded data, bit reservoir field) is categorically different from, and more important
      than, coding *quality* for this baseline (see Design below).
    - **Size**: output file size within ±10% of `bitRateKbps × durationSeconds / 8` (mirrors
      `Mp3EncoderTest.Encode_WithLowerBitRate_Should_Produce_Smaller_File`'s existing spirit), at
      every bitrate in the matrix above.
    - **Quality**: decode the encoded output and measure SNR against the original PCM — the same
      methodology this codebase's own `VorbisEncoderSessionTest` round-trip SNR helper already uses
      for a lossy codec. Initial numeric floor TBD once a first working encoder gives real numbers
      to calibrate against — not a claim of LAME-equivalent quality; record the actual measured SNR
      per bitrate in this item's own status note once implemented, so item 7 has a real baseline to
      improve from instead of a guess. Silence/near-silence fixtures are exempt from this check (a
      near-zero denominator makes dB meaningless there), the same way other codecs' own round-trip
      tests already special-case silence.
    - **Explicitly not required**: VBR, joint-stereo/intensity-stereo modes, block switching
      (short/mixed blocks — long blocks only for the baseline), non-trivial bit-reservoir
      borrowing, or matching LAME's output size/quality at the same nominal bitrate.
  - **Design** (pipeline stages, left to right through one frame's two granules; ISO/IEC 11172-3
    section references are to the publicly available format spec itself, consulted the same way
    this plan's own FLAC/WavPack items already were — not to any encoder's source):
    1. **Polyphase analysis filter bank** (§3-annex, 32 subbands) — splits each channel's PCM into
       32 subbands via the spec's own 512-tap windowed filter coefficients (Annex B's analysis
       window table, publicly tabulated, not derived from any encoder's source). Reuse `Mp3Probe`'s
       own existing header-field knowledge (sync word, version/layer/bitrate-index/sample-rate-
       index/mode layout) for the frame header rather than re-deriving it. New:
       `Mp3PolyphaseFilter` (or similar name TBD at implementation time).
    2. **Hybrid filter / MDCT** (§2.4.3.4) — each subband's 18 (long-block) samples per granule run
       through a 36-point MDCT to produce 18 spectral coefficients. **Reuses `Transform.Mdct`
       directly** (confirmed: already a generic, arbitrary-even-length direct-definition MDCT used
       by AAC/WMA) rather than writing a new one — the same "don't duplicate shared infra"
       precedent `FlacFrameDecoder.NeedsWideLpcAccumulator`/`WavPackEntropyDecoder.Band` etc.
       already set elsewhere in this codebase. **Baseline simplification**: always use long blocks
       (no block-switching/transient detection) — short/mixed blocks are a quality refinement for
       item 7, not required for a valid, decodable baseline bitstream.
    3. **Quantization** (§2.4.3.4.6) — the spec's own non-uniform (power-law) quantizer:
       `ix = NINT((|xr| · 2^(-gain/4))^0.75 − 0.0946)`, with a `global_gain` and one `scalefactor`
       per scalefactor band, chosen so the quantized values fit the frame's target bit budget.
       **Baseline simplification**: a simple outer-loop binary search on `global_gain` alone (no
       per-scalefactor-band noise-shaping/requantization loop, no psychoacoustic masking model at
       all) to hit the target bit count — real encoders' own perceptual bit allocation is exactly
       the "decades of tuning" gap item 7's own scope note already acknowledges may never fully
       close; the baseline's job is a *valid, reasonably-sized* bitstream, not a *perceptually
       optimal* one.
    4. **Huffman coding** (§2.4.3.4.8, Annex B's Huffman tables) — entropy-codes the quantized
       `big_values`/`count1` regions using the spec's own published code tables (table selection
       per scalefactor-band region, same as any compliant decoder's own inverse tables — NLayer
       already has the decode-side ones, encode needs their inverse, i.e. value → code rather than
       code → value, tabulated the same way from the same spec annex).
    5. **Bit reservoir** (§2.4.2.3) — even CBR mode's bitstream syntax requires `main_data_begin`
       (a backpointer letting a frame borrow bits from the previous frame's unused budget).
       **Baseline simplification**: never borrow — `main_data_begin = 0` on every frame, trivially
       spec-legal (borrowing is optional per frame) and sidesteps the reservoir *accounting*
       entirely for the first implementation; revisit if the ±10% size target proves hard to hit
       without it.
    6. **Bitstream formatting** (§2.4.1/2.4.2) — frame header (sync word, MPEG version/layer,
       bitrate/sample-rate indices, mode) + side info + main data, byte-packed per the spec's own
       layout. **Reuses `Transform.BitWriter` directly** — confirmed it already writes MSB-first
       (`WriteBits` iterates `i` from `bitCount - 1` down to `0`), exactly MP3's own bitstream
       convention (the opposite of WavPack's LSB-first one `WavPackBitWriter` had to write fresh
       for), so no new bit writer is needed for this item at all. New: `Mp3FrameEncoder` to drive
       it with the actual frame/side-info/Huffman-data layout.
    - **Stereo mode**: "normal" (independent/dual-mono) stereo only for the baseline — no
      joint/intensity stereo (already excluded above, repeated here since it affects quantization
      scope too: independent stereo quantizes/Huffman-codes each channel's granule completely
      separately, with no cross-channel step to design at all for this item).
  - Status: not started.

- [ ] **7. MP3 encode, VBR/quality (managed, follow-up to item 6)**
  - **Scope**: variable bit rate modes and whatever psychoacoustic/bit-allocation improvements prove
    tractable without consulting LAME's source, raising the SNR floor from item 6.
  - **Depends on**: 6.
  - **Size**: open-ended — genuine LAME-competitive quality may never be fully closed by a
    from-scratch effort (decades of community tuning went into LAME specifically). Revisit scope
    after item 6 ships with real numbers in hand, rather than committing to a size now.
  - Status: not started.

### Phase 4 — Cleanup (do last, only once items 1–7 are all merged)

- [ ] **8. Delete native infrastructure, expand CI**
  - **Scope**: `WavPackNative.cs` and `Native/win-x64/wavpackdll.dll` are already gone (removed
    early, alongside item 5, rather than held for this item — WavPack was fully managed in both
    directions at that point, so there was no reason to keep shipping a now-unreferenced binary).
    Remaining: delete `NativeLibraryLoader.cs`, `FlacNative.cs`, `Mp3Native.cs`,
    `Native/win-x64/*.dll` (the rest); remove the remaining `<None>` packaging entries from
    `EggEncoder.csproj`; remove the corresponding `THIRD-PARTY-NOTICES.md` sections; change
    `ci.yml`/`publish.yml` from
    `windows-latest`-only to a real `[windows-latest, ubuntu-latest, macos-latest]` matrix; update
    `EggEncoder.AotSmokeTest` to multi-RID and refresh its own doc comment (no more native-interop
    framing); full docs-sync pass across README/llms.txt/llms-full.txt/CLAUDE.md/docs/*.html
    removing every "Windows x64 only" claim.
  - **Depends on**: 1, 2, 3, 4, 5, 6 (7 is optional/open-ended — don't block cleanup on it; CBR-only
    MP3 is still a complete, fully-managed replacement for what shipped natively before).
  - **Size**: ~3–5 ticks — mechanical, but touches a lot of files.
  - Status: not started.

## What this plan deliberately does not decide

- **Cross-format ordering is a free choice.** FLAC (1–3), WavPack (4–5), and MP3 (6–7) don't depend
  on each other — a future session can pick any unblocked item regardless of format. This plan
  orders them FLAC-first only because FLAC has a real reference decoder to study (PureFlac);
  WavPack and MP3 don't.
- **The exact SNR/quality floor for item 6** is left as "TBD, calibrate from the first real
  implementation" rather than a guessed number — committing to an unverified threshold now would be
  more likely to need revision than to hold.
- **Item 7's size** is explicitly open-ended; it's a separate decision point for the owner once item
  6's real numbers exist, not a commitment made here.

## Licensing research summary

Verified via live web research this session (3 WebSearch + 4 WebFetch calls, all cited) —
re-verify anything load-bearing before acting on it if much time has passed, per
`docs/video-support-backlog.md`'s own stated lesson about not trusting stale/memory-based licensing
claims.

| Candidate | License | Verdict | Why |
|---|---|---|---|
| [PureFlac](https://github.com/FatJohn/PureFlac) | MIT | Usable as reference/vendoring candidate for item 1 | Confirmed MIT directly from the repo; claims SHA-256-verified bit-exact decode vs. libFLAC |
| [SimpleFlac](https://github.com/jdpurcell/SimpleFlac) | MIT | Considered, not preferred over PureFlac | MIT and single-file, but its own README discloses 8-bit and non-whole-byte bit depths are disabled by default — a real gap against this project's existing FLAC feature matrix; also less tested (6 stars, 5 commits) |
| [CUETools.Codecs.FLAKE](https://www.nuget.org/packages/CUETools.Codecs.FLAKE) | **LGPL-3.0** | **Rejected** for item 2/3 | Its own package description says "this has not been tested much yet, I just converted it to .net standard"; LGPL-3.0 is excluded under this plan's "no copyleft dependency, full stop" policy (see Scope philosophy) regardless of test maturity |
| [GroovyCodecs](https://github.com/jongoochgithub/GroovyCodecs) | **LGPL-3.0** | **Rejected** for item 6 | Its own README states it's an auto-converted Java-to-C# port of LAME/Jump3r's actual source — fails the clean-room requirement *and* carries the license this whole initiative exists to get away from; its own README also disclaims optimization/quality |

No permissively-licensed managed WavPack codec (decode or encode) exists on NuGet, per this
repo's own pre-existing `CLAUDE.md` note on `WavPack/` ("no pure-managed WavPack decoder/encoder
exists on NuGet, unlike MP3/Opus/Vorbis") — this plan did not re-run a fresh WavPack-specific
search this session, so re-verify before relying on it if much time has passed, same caveat as
everything else in this table.
