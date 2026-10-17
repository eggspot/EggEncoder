# Live Streaming Plan (HLS / fMP4 / MPEG-TS)

EggEncoder today is a file-in, file-out (or, since `docs/feature-roadmap.md` item 1,
`Stream`-in/`Stream`-out) audio toolkit: decode a whole source, or encode a whole destination, in
one call. This plan adds a genuinely different shape on top of that: **push PCM in continuously,
get packaged live-streaming output out continuously** — an incremental encoder, container muxers
(fMP4/CMAF and MPEG-TS), an HLS segmenter, and `.m3u8` playlist writers (VOD and live sliding
window), all 100% pure managed C#, no native library, no ffmpeg/subprocess, AOT-safe, cross-platform
— the same non-negotiables every other part of this codebase already holds itself to.

This is a multi-session, multi-month initiative, the same scale as `docs/managed-codec-rewrite-plan.md`
and `docs/video-support-backlog.md`. **This file is the source of truth for what's done, what's
next, and why** — the same role those two files play for their own initiatives. Read `docs/
feature-roadmap.md` item 1 (the `Stream`-based API this plan builds directly on top of) and
`docs/managed-codec-rewrite-plan.md` (the clean-room/licensing conventions this plan reuses
verbatim) before anything else.

## How to use this plan (read this first, every time)

You are almost certainly a fresh Claude Code session with no memory of the conversation that
created this file. Here is everything you need:

1. **Read this whole file once** before picking anything, including "Scope philosophy" and
   "Architecture overview" — the box-format/timestamp details below are load-bearing, not
   decoration; skipping to the checkboxes risks a muxer that looks right and isn't.
2. **Read the repo's own `CLAUDE.md`** for house conventions (feature branch + PR, never push to
   `main`, conventional commit prefixes, 100% branch coverage on new code, `xUnit` +
   `FluentAssertions` + AAA pattern, `Feature_Condition_ExpectedBehavior` test naming,
   docs-sync-in-the-same-PR).
3. **Check `git log --oneline -20`, `gh pr list --state all`, and this file's checkboxes together**
   before picking anything — if a feature branch from a prior session is open and unmerged,
   continue that instead of starting something new.
4. **Pick the first unchecked item whose "Depends on" line is already checked off.** Items here
   have a real, mostly-linear dependency order (unlike `managed-codec-rewrite-plan.md`'s
   independent-per-format items) — the encoder API has to exist before a muxer can consume its
   output, the muxer before the segmenter can package it, the segmenter before a playlist has
   segments to describe. Don't reorder without a stated reason.
5. **Implement the item with tests**, following this codebase's established conventions:
   - Full branch coverage: every public type/method, happy path, edge cases, every
     validation/exception path.
   - **New namespace, new folder**: `src/EggEncoder/Streaming/`, mirroring how `Codecs/` is
     organized by format — e.g. `Streaming/Aac/AacStreamingEncoder.cs`, `Streaming/Mp4/
     FmpInitSegmentWriter.cs`, `Streaming/Hls/HlsSegmenter.cs`. Don't scatter streaming types into
     `Codecs/` itself; this is a genuinely different concern (packaging/transport, not codec
     bitstreams) layered on top of it.
   - **Reuse, don't duplicate, existing codec internals.** `AacFrameEncoder`'s own MDCT/windowing/
     quantization logic is exactly what item 1 needs — only its *output* plumbing (two
     `Stream.Write` calls per frame, hardcoded to one `Stream`) needs to change shape, not the
     actual encoding math. The same principle applies to every later item: `Mp4SampleTable`'s own
     box-walking conventions, `BitWriter`, `WavFileBuilder`-style test helpers, etc.
   - Each item's own "Acceptance criteria" are the bar for done — measurable, not "looks right."
     Where an item says to parse this project's own output with this project's own reader, that's
     a real, already-available conformance oracle, not a placeholder.
6. **Commit to a feature branch, open a PR, self-review (two passes, per `CLAUDE.md`), merge, run
   `dotnet test --configuration Release` on `main`.** Exactly the workflow the rest of this repo's
   history already uses.
7. **Update the item's own `**Status**` line in this file, in the same PR** (unlike
   `docs/managed-codec-rewrite-plan.md`/`docs/video-support-backlog.md`'s `- [ ]`/`- [x]` checkbox
   convention, this plan's own backlog items are numbered `##` headings, each already ending in a
   `**Status**: not started.` line — replace that line with a one-line note: merged PR link, plus
   anything genuinely left out of scope). If genuinely blocked, don't silently mark it done — leave
   the line as `not started` (or `blocked: <reason>`), state exactly why, and move to the next
   unblocked item instead.
8. **Do not touch PR #50** (the Sponsors badge) — unrelated to this initiative.

## Scope philosophy

- **Clean-room from the public spec, full stop** — the same posture `managed-codec-rewrite-plan.md`
  already established for FLAC/MP3/WavPack, applied here to the container/transport specs instead
  of codec specs:
  - **RFC 8216** — HTTP Live Streaming (HLS): `.m3u8` playlist grammar, segmenting rules, tag
    semantics.
  - **ISO/IEC 14496-12** — ISO base media file format (the box structure `MovAtomReader`/
    `Mp4SampleTable` already read); this plan adds the *write* side, plus the fragmented-file boxes
    (`moof`/`mfhd`/`traf`/`tfhd`/`tfdt`/`trun`, `mvex`/`trex`) the read side has never needed before
    now, since every existing fixture is a plain, non-fragmented `moov`+`mdat` file.
  - **ISO/IEC 23000-19** — CMAF (Common Media Application Format): the specific fMP4 profile
    (brand/box constraints) real HLS players actually expect for fMP4 segments, narrower than bare
    ISOBMFF allows.
  - **ISO/IEC 13818-1** — MPEG-TS: packetization (188-byte TS packets, PAT/PMT, PES headers, the
    90kHz system clock) for the legacy (non-fMP4) HLS segment format.
  - **ADTS** (part of the MPEG-2/4 AAC specs already behind `AacDecoder`/`AacEncoder`) — already
    implemented in this codebase; reused, not re-specified, for MPEG-TS's own AAC framing
    convention.
  - No code derived from any non-MIT-compatible reference muxer/segmenter/player (ffmpeg's
    `libavformat` mp4/hls muxers, GPAC, hls.js, etc.) — read box/tag *names and field meanings* from
    the specs above; where a spec is genuinely silent on a real-world convention (CMAF brand
    values are the likely candidate — see item 2's own section), verify against a *real encoder's
    output* (ffmpeg- or Shaka-Packager-produced, inspected as data) the same arm's-length way
    `managed-codec-rewrite-plan.md` item 4 verified WavPack's own container fields, never by
    reading that tool's source.
- **No native library, no ffmpeg/subprocess, anywhere in the shipped library.** `ffmpeg`/`ffprobe`
  are allowed only as a *local, optional, dev-time oracle* for generating reference fixtures and
  for an environment-gated cross-check test (skip cleanly if not found on `PATH`) — never a runtime
  or CI-time dependency, the same convention `docs/video-support-backlog.md` already established
  for its own fixture generation.
- **AOT-safe, cross-platform, from the first PR.** No reflection, no `Reflection.Emit`, no
  dynamically-loaded assemblies — build every new type against the same constraints
  `EggEncoder.csproj`'s `IsAotCompatible=true` already enforces for the rest of this project. Add
  streaming round-trips to `EggEncoder.AotSmokeTest` once item 2 (the first real file-producing
  item) lands.
- **Permissively licensed (MIT/BSD/Apache) managed dependencies are allowed** as a reference to
  study or as an actual `PackageReference`, if independently verified — not assumed from a README
  claim. None are anticipated as *required* (every format here is simple enough, and specified
  precisely enough, for clean-room from scratch — unlike WavPack's own undocumented codec, there's
  no analogous "no public spec exists" gap anywhere in this plan), but this stays open per item if
  a genuine need surfaces.
- **Synchronous, callback-based I/O, matching this codebase's own existing idiom.** Every decode
  path here already streams via a synchronous callback (`AudioBlockDecodedCallback`), not
  `IAsyncEnumerable`/`Stream.ReadAsync`/events — `docs/feature-roadmap.md` item 1 explicitly called
  real async I/O out of scope for the same reason. This plan's own new callback shapes (see
  "Architecture overview" below) follow that precedent deliberately, for the same reason: every
  codec's own encode loop here is written against synchronous calls throughout, and introducing a
  second, async-flavored idiom alongside the existing one would fragment the codebase's own style
  for no caller-visible benefit a thin synchronous wrapper couldn't already give an async caller.
- **Decode direction is explicitly out of scope for this plan.** This is a *packaging* initiative —
  PCM in, a playable HLS stream out. Ingesting an *existing* live HLS/fMP4/MPEG-TS stream (the
  reverse direction) is a different, substantial effort of its own and not addressed here; if it
  becomes a real need later, it gets its own plan document, the same way video support and the
  managed codec rewrite each got their own.

## Prerequisites (read before item 1)

Two gaps already exist elsewhere in this codebase that intersect with this plan. Neither blocks
starting item 1, but both need a stated resolution before the item that actually depends on them.

### AAC encoder delay (blocks item 3's timestamp math, not items 1–2)

`AacFrameEncoder` (`src/EggEncoder/Codecs/Aac/AacEncoder.cs`) encodes with a 50%-overlap MDCT: the
very first emitted frame windows an all-zero `_previousBlock` against the first real 1024 samples
in `_currentBlock`. That's a real, deterministic **one-frame (1024-sample) encoder delay** — the
same *kind* of gap `docs/feature-roadmap.md` item 4 already found, measured, and fixed for MP3 (the
`1680`-sample constant, confirmed empirically via cross-correlation, not derived from the bitstream
layout alone after a first, wrong attempt at the constant was caught exactly that way). Nothing in
this codebase currently measures or exposes AAC's own equivalent constant.

This matters here specifically because item 3 (the fMP4/CMAF muxer) needs correct
`baseMediaDecodeTime`/presentation-timestamp math across segment boundaries — get the delay wrong
and every segment boundary after the first is off by a fixed, audible amount, the exact class of
bug gapless-playback metadata exists to prevent.

**Proposed resolution**: before item 3 starts, add a small, standalone step (its own tiny PR, or
folded into item 3's own first commit — either is fine) that measures AAC's own encoder delay the
same empirical way MP3's was measured: encode a known test signal, decode it back through
`AacDecoder`, and find the true sample alignment via normalized cross-correlation (reusing the
exact technique `VorbisEncoderSessionTest`'s own round-trip SNR helper already established for
Vorbis's similarly-undocumented MDCT lookahead). Confirm the measured value is the expected `1024`
exactly (one full frame) rather than assuming it — this architecture (zero-padded `_previousBlock`
on the first call) should produce exactly that, but "should produce" is exactly the kind of
assumption the MP3 `528` constant already proved wrong once. Expose the confirmed value as a public
constant (`AacStreamingEncoder.EncoderDelaySamples` or similar, named to match
`Mp3Encoder.EncoderDelaySamples`'s own existing precedent) for the muxer to use directly.

### MP3 `Stream`-based decode gap (does not block this plan at all; listed for completeness)

`docs/feature-roadmap.md` item 1's own status note already documents this: NLayer's
`MpegFile(Stream)` constructor has no `leaveOpen` option, so `Probe(Stream, AudioFormat.Mp3)` still
throws `NotSupportedException` rather than risking disposing a caller's stream it doesn't own. This
plan's own codec priority is AAC-LC first (see item 1's own scope), and MP3 as a *streaming encode*
target (item 8, lowest priority) doesn't touch NLayer at all — `Mp3Encoder`/`Mp3EncoderSession` are
already pure managed encode, with no NLayer dependency; NLayer is decode-only. This gap would only
become a real blocker if a future item needed to *decode* a live MP3 input stream, which nothing in
this plan currently calls for.

**Proposed resolution, for whenever it is genuinely needed**: a small `NonDisposingStreamWrapper :
Stream` adapter — delegates every member to an inner `Stream`, overrides `Dispose(bool)` to leave
the inner stream open — passed to `NLayer.MpegFile`'s own constructor in place of the caller's
stream directly. This is a well-understood, minimal pattern (no NLayer source change, no upstream
dependency needed) and could land as its own tiny, independent PR whenever it's actually needed,
with no dependency on anything in this plan.

## Architecture overview

```
 PCM in (int[], this codebase's usual native-range convention)
   │
   ▼
 IStreamingEncoder.Write(ReadOnlySpan<int>)         <- item 1
   │  (buffers to one codec frame, encodes, emits via callback)
   ▼
 EncodedAudioFrameCallback(EncodedAudioFrame frame)  <- item 1's own output shape
   │                                        │
   ▼ (raw access units)                     ▼ (same raw access units, ADTS-wrapped)
 fMP4/CMAF muxer                          MPEG-TS muxer
 (init segment once; one                 (PAT/PMT once; one
  moof+mdat per segment)  <- item 2        .ts packet stream per segment)  <- item 4
   │                                        │
   └──────────────────┬─────────────────────┘
                       ▼
              HlsSegmenter (segment-boundary policy,
              drives the muxer above, hands segment
              bytes + duration to a sink)             <- item 3
                       │
                       ▼
              IStreamingSink (file system / in-memory /
              delegate-to-caller-callback)             <- item 3
                       │
                       ▼
              m3u8 media playlist writer (VOD or live
              sliding window)                          <- item 3

 Master playlist (multiple media playlists -> one
 #EXT-X-STREAM-INF index)                              <- item 5

 Live PCM pump (timestamps, clock-drift/silence-fill
 policy, sits in front of IStreamingEncoder.Write)     <- item 6
```

Key shapes, named here once since every later item references them:

```csharp
namespace EggEncoder.Streaming;

// One codec-native encoded frame -- a `ref struct` so a per-frame allocation is never required
// just to hand bytes to the callback; callers that need to keep the bytes past the callback's own
// return (every muxer does) copy them into their own buffer, the same already-established
// `AudioBlockDecodedCallback`/`ReadOnlySpan<int>` convention this type mirrors.
public readonly ref struct EncodedAudioFrame
{
    public ReadOnlySpan<byte> Payload { get; }   // the raw access unit -- no ADTS/container framing
    public int SampleCount { get; }              // samples this frame represents (1024 for AAC-LC)
    public long FrameIndex { get; }              // 0-based, monotonically increasing per session
}

public delegate void EncodedAudioFrameCallback(EncodedAudioFrame frame);

public interface IStreamingEncoder : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    int SamplesPerFrame { get; }
    void Write(ReadOnlySpan<int> interleavedSamples);
    void Complete(); // flushes any partial trailing frame, same role Finish()/Flush() play elsewhere
}
```

`Payload` is deliberately the *raw access unit*, not ADTS-framed — that's the shape the fMP4 muxer
wants directly (one `mdat` sample per frame, no framing overhead repeated per frame the way ADTS's
own per-frame header would add), and the MPEG-TS muxer wraps it in ADTS itself (reusing
`AacEncoder.cs`'s own existing `WriteAdtsFrame` logic, refactored into a small standalone
`AdtsFrameWriter.Wrap(ReadOnlySpan<byte> payload, int sampleRateIndex, int channels) -> byte[]`
helper both the legacy standalone-ADTS-file path and the new MPEG-TS muxer call) rather than
requiring two different encoders for the same codec.

## Ranked, ordered backlog

| # | Item | Depends on |
|---|------|------------|
| 1 | [Incremental streaming encoder API + AAC-LC session](#1-incremental-streaming-encoder-api--aac-lc-session) | none |
| 2 | [fMP4/CMAF muxer (init + media segments)](#2-fmp4cmaf-muxer-init--media-segments) | 1, AAC encoder-delay prerequisite |
| 3a | [HLS segmenter + m3u8 playlist writer (VOD only) + sink abstraction](#3a-hls-segmenter--m3u8-playlist-writer-vod-only--sink-abstraction) | 2 |
| 3b | [HLS live sliding window](#3b-hls-live-sliding-window) | 3a |
| 4 | [MPEG-TS muxer (alternative segment format)](#4-mpeg-ts-muxer-alternative-segment-format) | 1, 3a |
| 5 | [Master playlist (multi-bitrate)](#5-master-playlist-multi-bitrate) | 3a |
| 6 | [Live input source: PCM pump, timestamps, drift/silence-fill policy](#6-live-input-source-pcm-pump-timestamps-driftsilence-fill-policy) | 1 |
| 7 | [Low-latency HLS (future sketch, not scheduled)](#7-low-latency-hls-future-sketch-not-scheduled) | 3a, 3b |
| 8 | [Opus/MP3/FLAC streaming encoder sessions](#8-opusmp3flac-streaming-encoder-sessions) | 1 |

Items 1→2→3a are a strict chain (each literally can't be tested without the previous one existing).
3b, 4, and 5 all branch off 3a and don't depend on each other. 6 and 8 branch off 1 only and can be done
in any order relative to 2–5 (they don't touch muxing/packaging at all). 7 is deliberately listed
last and unscheduled — see its own section.

---

## 1. Incremental streaming encoder API + AAC-LC session

**Problem**: every encoder in this codebase today (`AacEncoder`, `FlacEncoder`, `Mp3Encoder`, etc.)
either encodes a whole in-memory buffer at once (`Encode(path, samples, ...)`) or exposes an
`OpenSession`-style `IAudioSink` that writes *decoded* PCM straight to one destination file/Stream
it owns the framing for. Nothing hands the *encoded* bytes back to the caller as they're produced —
which is exactly what a muxer needs: "here is one AAC access unit, with no file attached to it at
all yet."

**Design**:

- `IStreamingEncoder`/`EncodedAudioFrame`/`EncodedAudioFrameCallback` as sketched in "Architecture
  overview" above, in a new `src/EggEncoder/Streaming/IStreamingEncoder.cs`.
- `src/EggEncoder/Streaming/Aac/AacStreamingEncoder.cs`: a static factory,
  `AacStreamingEncoder.Create(int channels, int sampleRate, EncodedAudioFrameCallback onFrameEncoded)
  -> IStreamingEncoder`, matching every other codec's own `OpenSession`-style factory shape. Scoped
  to mono AAC-LC only, matching `AacEncoder`'s own existing scope exactly (no new capability being
  added here, just a new output shape for the same encoder) — throws `NotSupportedException` for
  stereo/unsupported sample rates with the exact same messages `AacFrameEncoder.
  ValidateAndGetSampleRateIndex` already throws today.
- Internally, this reuses `AacFrameEncoder`'s own MDCT/windowing/quantization code essentially
  unchanged — only `EncodeAndEmitFrame`'s own tail (today: build an ADTS header + call
  `outputStream.Write` twice) changes, to build the frame's *raw access unit* bytes into a reusable,
  grow-if-needed buffer and invoke `onFrameEncoded` with an `EncodedAudioFrame` wrapping it, once
  per frame. The existing `AacFrameEncoder`/`AacEncoder.Encode`/`AacEncoderSession` (file-oriented,
  ADTS-framed) stay exactly as they are — this is a new, additional entry point into the same
  underlying frame-encoding logic, not a replacement; refactor the shared internals into a form
  both the existing ADTS-file path and the new streaming path call, rather than duplicating the
  MDCT/quantization logic a second time. This same refactor is also where `WriteAdtsFrame`'s own
  ADTS-wrapping logic (sync word, profile, sample-rate index, channel config, frame length) gets
  pulled out into the standalone `AdtsFrameWriter.Wrap(ReadOnlySpan<byte> payload, int
  sampleRateIndex, int channels) -> byte[]` helper referenced in "Architecture overview" — natural
  to do here since this item already has to touch that exact code, and it means item 4's own
  MPEG-TS muxer later has a single, already-tested ADTS implementation to call rather than needing
  its own extraction pass against code this item already rewrote once.
- `IStreamingEncoder.Write` buffers samples internally (mirrors `AacFrameEncoder.WriteSample`'s own
  existing per-sample accumulation into `_currentBlock`) and emits a frame via the callback the
  moment a full `SamplesPerFrame` block is available — *synchronously, within the `Write` call that
  completes the block*, not deferred. `Complete()` flushes whatever partial block remains (mirrors
  `AacFrameEncoder.Flush`) as one final, possibly-short frame, then marks the session done; calling
  `Write` after `Complete()` throws `InvalidOperationException`, the same "once drained, this
  instance is done" contract `ResamplingTransform.Flush`/`Apply` already establish for a comparable
  one-way lifecycle.
- The encoder-delay constant from "Prerequisites" above lives here:
  `AacStreamingEncoder.EncoderDelaySamples` (a `public const int`, expected value `1024`, confirmed
  empirically before this item is considered done — not assumed from the architecture description
  alone).

**Acceptance criteria**:

- `AacStreamingEncoder.Create(...).Write(...)` fed the exact same PCM `AacEncoder.Encode` already
  encodes in its own existing tests produces, frame-by-frame, the exact same *raw access unit*
  bytes as `AacEncoder.Encode`'s own ADTS output once each ADTS frame's 7-byte header is stripped —
  proving the refactor didn't change the actual encoded bitstream, only how it's delivered. This is
  a strong, cheap, already-available oracle: no new reference signal needed, just a byte-level
  comparison against this project's own pre-existing, already-correct encoder.
- `EncoderDelaySamples` is confirmed via the cross-correlation method described in "Prerequisites,"
  not merely asserted to equal `1024`.
- `Complete()` with zero samples ever written produces no frames (an empty stream has nothing to
  flush) — the zero-length edge case every comparable `Flush()`/`Finish()` in this codebase already
  has to handle correctly.
- `Write` after `Complete()` throws `InvalidOperationException`; stereo/unsupported-sample-rate
  `Create` calls throw `NotSupportedException` with the existing messages unchanged.
- 100% branch coverage of every new type (happy path, the empty-stream edge case, the
  post-`Complete()` misuse case, every validation path) — xUnit + FluentAssertions + AAA, matching
  every other test in this codebase.

**Size**: small–medium. The hard part (MDCT/quantization) already exists and is reused verbatim;
this is a refactor of the output plumbing plus a new public surface and its own tests.

**Status**: done ([PR #110](https://github.com/eggspot/EggEncoder/pull/110)).
`IStreamingEncoder`/`EncodedAudioFrame`/`EncodedAudioFrameCallback`
(`src/EggEncoder/Streaming/`) plus `Streaming.Aac.AacStreamingEncoder`, backed by a refactored
`AacFrameEncoder` that emits each frame's raw access unit, real sample count, and frame index via a
callback instead of writing ADTS bytes to a fixed `Stream`; `AacEncoder.Encode`/`AacEncoderSession`
now supply that callback themselves (building an ADTS frame via the newly-extracted
`AdtsFrameWriter.Wrap`, reused later by item 4's own MPEG-TS muxer) rather than `AacFrameEncoder`
writing ADTS directly, with byte-for-byte parity confirmed against the pre-refactor output.
`EncoderDelaySamples` (`1024`) reuses the empirical confirmation already in
`AacEncoderTest.Encode_ThenDecode_Should_ReconstructToneSignal_UpToScale`'s own cross-correlation
(that test only reaches an acceptable SNR once this exact one-frame offset is compensated for) --
no new measurement code was needed since this was already established.

## 2. fMP4/CMAF muxer (init + media segments)

**Problem**: nothing in this codebase writes an MP4/MOV container today — `CafReader`/`CafWriter`
write CAF, a different (if related) Apple container; `MovAtomReader`/`Mp4SampleTable`/
`Mp4EsdsParser` only *read* ISOBMFF. A live HLS stream using fMP4/CMAF segments (the modern,
TS-free HLS segment format, and a prerequisite for item 4's own ADTS/MPEG-TS alternative to make
sense as genuinely an *alternative*) needs both: one **init segment** (`ftyp`+`moov`, written once
per rendition, describing the track but holding no samples) and a stream of **media segments**
(`moof`+`mdat` each, one per HLS segment, holding that segment's own samples).

**Design**:

- `src/EggEncoder/Streaming/Mp4/FmpInitSegmentWriter.cs`: `BuildInitSegment(int channels, int
  sampleRate, byte[] audioSpecificConfig) -> byte[]`. Builds, in order: `ftyp` (major_brand `iso5`,
  minor_version `512`, compatible_brands `[iso6, mp41]` — CMAF's own recommended values; **verify
  against a real Shaka-Packager- or ffmpeg-produced CMAF fMP4 fixture's own raw bytes before
  shipping this item**, the spec alone under-specifies real-world player expectations here, the
  same way AIFC's own `compressionName` convention needed a real fixture to pin down, not just the
  spec text) → `moov` → `mvhd` (duration `0`, since a fragmented file's true duration isn't known
  up front) → `trak` → `tkhd` → `mdia` → `mdhd` (timescale = the track's own sample rate, matching
  real encoders' convention, confirmed against a real fixture) → `hdlr` (`soun`) → `minf` → `stbl`
  with a *populated* `stsd` (one `mp4a` entry, carrying `esds`/`AudioSpecificConfig` — reuses
  `Mp4EsdsParser`'s own byte-layout knowledge in reverse) but *empty* `stts`/`stsc`/`stsz`/`stco`
  (zero entries each — a fragmented file's actual samples live in media segments, never in the init
  segment) → `mvex` → `trex` (default_sample_description_index `1`, the only genuinely new box this
  codebase's own reader has never needed to understand, since every existing fixture is
  non-fragmented).
- `src/EggEncoder/Streaming/Mp4/FmpMediaSegmentWriter.cs`: a stateful, one-per-rendition writer
  (parallel to `WavWriter`'s own "stateful session" shape, not a static one-shot function, since
  `baseMediaDecodeTime`/`sequence_number` are cumulative across segments) exposing
  `BuildMediaSegment(IReadOnlyList<EncodedAudioFrame-shaped (byte[] Payload, int SampleCount)>
  frames) -> byte[]`, called once per completed HLS segment (see item 3): `moof` → `mfhd`
  (`sequence_number`, incrementing by one per call) → `traf` → `tfhd` (`track_ID`,
  `default-base-is-moof` flag set — the modern, CMAF-expected addressing mode, vs. the older
  base-data-offset convention) → `tfdt` (`baseMediaDecodeTime` = the running total of samples
  emitted so far, in the track's own timescale — the field that makes segment N+1 play seamlessly
  after segment N with no gap or overlap, carried forward across `BuildMediaSegment` calls) →
  `trun` (`sample_count`, `data_offset`, and a per-sample `sample_size` list — AAC's own frame sizes
  vary with content even at a nominally constant bitrate; `sample_duration` is constant
  (`SamplesPerFrame`, from item 1) so can use the `trun` header's own default-duration flag instead
  of repeating it per sample) → `mdat` (every frame's payload bytes, concatenated in order, exactly
  matching `trun`'s own sample list).
- Output is an in-memory `byte[]` per segment/init-segment call — no file I/O, no sink dependency —
  deferring "where do these bytes go" entirely to item 3, which is the first item that actually
  needs a real destination.

**Acceptance criteria**:

- **Primary conformance oracle (always runs, no external tool needed)**: feed a produced init
  segment + one media segment, concatenated, through `MovAtomReader`'s own `EnumerateAtoms`/
  `FindAtom` directly (not through `MovDecoder`/`Mp4SampleTable`, which only understand the
  non-fragmented `stsz`/`stco`/`stsc` layout — a fragmented file's own `moof`/`traf`/`trun` sample
  table is a genuinely different box set this item's own test code parses directly, kept
  intentionally separate from `Mp4SampleTable` rather than extending that type, since the goal here
  is self-conformance-checking this item's own output, not general-purpose fragmented-MP4 read
  support — a real feature, but a different one, out of this plan's scope unless the owner asks for
  it explicitly later) and confirm: `ftyp` present with the expected brand; `moov`/`trak`/`mdia`/
  `stbl` resolves the same channels/sample rate/`AudioSpecificConfig` that were written in;
  `moof`/`traf`/`tfhd`/`tfdt`/`trun` resolves the exact same per-sample sizes and the exact
  cumulative `baseMediaDecodeTime` the test itself computed independently, and the `mdat` payload
  bytes at the `trun`-declared offsets match the original encoded frames byte-for-byte.
- **Secondary oracle, environment-gated (skip cleanly if absent)**: if `ffprobe` is found on
  `PATH`, run it against a produced init+media-segment file pair (concatenated, or written as
  separate files per the `-f mp4`-vs-`-f hls`-style convention ffprobe expects) and confirm it
  reports the same duration/sample rate/channel count — never a hard CI dependency, matching this
  project's existing ffmpeg-as-optional-oracle precedent.
- A second, then third, `BuildMediaSegment` call on the same writer produces strictly increasing
  `sequence_number`/`baseMediaDecodeTime` values, each consistent with the previous segment's own
  total sample count — the specific property that makes gapless multi-segment playback actually
  gapless.
- Zero-frame `BuildMediaSegment` call (an edge case a real segmenter should never trigger, but the
  muxer itself must still handle predictably) either throws `ArgumentException` or produces a
  zero-sample, still-structurally-valid segment — pick one, document which, test it.
- 100% branch coverage.

**Size**: medium–large. This is new box-writing code with no existing writer-side precedent to
mechanically adapt (unlike, say, widening an existing writer for `Stream` support) — budget
accordingly, and don't be surprised if real-fixture verification (the `ftyp` brand values
especially) takes real back-and-forth against an actual reference file's bytes.

**Status**: done ([PR #111](https://github.com/eggspot/EggEncoder/pull/111)).
`FmpInitSegmentWriter.BuildInitSegment`/`FmpMediaSegmentWriter.
BuildMediaSegment` (`src/EggEncoder/Streaming/Mp4/`), backed by a new shared `Mp4BoxBuilder`
(`Codecs.Mov`, alongside `MovAtomReader` -- both are low-level ISOBMFF plumbing) and
`Mp4EsdsParser.BuildAudioSpecificConfig`/`BuildEsdsBoxContent` (the write-side counterpart to that
type's existing parse methods). `ftyp` brand values, box field layout/ordering, and `tfhd`/`tfdt`/
`trun` flag choices were all confirmed against a real ffmpeg-produced CMAF fMP4 fixture's own bytes
(`ffmpeg ... -movflags +frag_keyframe+empty_moov+default_base_moof`), not assumed from the spec text
alone -- see the acceptance-criteria note below for exactly what that comparison covered. Primary
conformance oracle (self-parse via `MovAtomReader` for the init segment, a small test-only
moof/traf/tfhd/tfdt/trun reader for media segments) passes, including multi-segment
`sequence_number`/`baseMediaDecodeTime` continuity and a rejected zero-frame segment.

**New finding, out of scope for this item**: `ffprobe` correctly reports this item's own produced
container's `codec_name`/`sample_rate`/`channels` (the actual acceptance bar above) -- but a full
`ffmpeg -i ... -f null -` decode attempt fails with `Input buffer exhausted before END element
found`/`channel element 0.0 is not allocated`. Isolated (by testing both files side by side) to a
**pre-existing** characteristic of `AacEncoder`/`AacFrameEncoder`'s own bitstream, reproducible
identically from its existing ADTS `.aac` file output -- not something this item's fMP4 muxing
introduced. `AacEncoderTest`'s own round-trip test has only ever verified against this project's
own `AacDecoder`, never against ffmpeg's independent decoder, so this gap was simply never surfaced
before now. Not investigated further here (a codec-bitstream-compliance question, not a
container/muxing one) -- worth its own follow-up item if the owner wants AAC output to be
ffmpeg-decodable, but it does not block this item's own acceptance criteria or anything later in
this plan, since every later item's own oracle is either this project's own reader/decoder or
`ffprobe`'s metadata reporting, neither of which this gap affects.

## 3a. HLS segmenter + m3u8 playlist writer (VOD only) + sink abstraction

**Problem**: item 2 can build one segment at a time, but nothing yet decides *when* to cut a
segment, writes the resulting bytes anywhere real, or describes the resulting segments in a
playlist a player can actually fetch.

**Design**:

- `src/EggEncoder/Streaming/IStreamingSink.cs`:
  ```csharp
  public interface IStreamingSink
  {
      void WriteInitSegment(ReadOnlySpan<byte> bytes);
      void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes);
      void WritePlaylist(ReadOnlySpan<byte> utf8Bytes); // called again each time the playlist changes
      void DeleteMediaSegment(long sequenceNumber); // live sliding-window cleanup; VOD never calls this
  }
  ```
  Three implementations: `FileSystemStreamingSink` (writes `init.mp4`/`segment_{n}.m4s`/
  `playlist.m3u8`-shaped files into a directory, naming convention configurable), `InMemoryStreamingSink`
  (keeps everything in dictionaries — the primary vehicle for this item's own tests, and generally
  useful for a caller who wants bytes without touching a filesystem at all), and
  `DelegateStreamingSink` (four constructor-supplied delegates, one per interface member — the
  concrete answer to "a user callback for HTTP/S3 upload" without requiring every such caller to
  write a full class).
- `src/EggEncoder/Streaming/Hls/HlsSegmenter.cs`: owns an `IStreamingEncoder` indirectly (receives
  `EncodedAudioFrame`s via a callback wired to item 1's own output), accumulates frames until their
  cumulative duration reaches a configured `targetDurationSeconds` *or more* (HLS segments are cut
  on frame boundaries only — never mid-frame — so real segment duration is always >= target, by at
  most one frame's worth; `RFC 8216`'s own `#EXT-X-TARGETDURATION` is itself defined as a ceiling
  real segments must not exceed by more than a small tolerance, which this rounding-up approach
  satisfies as long as `targetDurationSeconds` already has reasonable headroom — document this
  explicitly rather than silently hoping no caller notices), then hands the accumulated frames to
  item 2's `FmpMediaSegmentWriter`, writes the result via `IStreamingSink.WriteMediaSegment`, and
  appends a segment descriptor (`duration`, `sequenceNumber`) to its own internal list. **VOD mode
  only in this item** (see item 3b for the live sliding window): every segment ever produced stays
  in the list; `Complete()` flushes the last (possibly short) segment, writes the final playlist
  with `#EXT-X-ENDLIST`, and that's it.
- `src/EggEncoder/Streaming/Hls/M3u8PlaylistWriter.cs`: a stateless formatter, `BuildMediaPlaylist
  (int targetDurationSeconds, long mediaSequence, IReadOnlyList<HlsSegmentDescriptor> segments,
  string? initSegmentUri, bool endList) -> string` (no `version` parameter -- this writer always
  declares `Version = 7`, confirmed against a real ffmpeg-produced fMP4 HLS fixture using the exact
  same combination this writer needs (fMP4 + `EXT-X-MAP`, no `EXT-X-I-FRAMES-ONLY`) -- **not** the
  spec-table-derived value `6` an earlier version of this plan/code shipped with; see this item's
  own "Found during item 5" status-note addendum below for the correction -- a public const rather
  than a configurable parameter, since nothing in this plan ever needs a different value). Emits,
  per `RFC 8216` §4.3.3/§4.4: `#EXTM3U`, `#EXT-X-VERSION:7`, `#EXT-X-TARGETDURATION:
  {n}`, `#EXT-X-MEDIA-SEQUENCE:{mediaSequence}`, `#EXT-X-MAP:URI="{initSegmentUri}"` (omitted when
  `initSegmentUri` is `null` -- MPEG-TS segments from item 4 need no init segment at all), then per
  segment `#EXTINF:{duration},` + the segment's own URI, and finally `#EXT-X-ENDLIST` only when
  `endList` is true. `#EXT-X-DISCONTINUITY` is not implemented -- nothing in this plan produces a
  genuine discontinuity yet (that would need a source-level concept, e.g. a live-input dropout from
  item 6, to signal one); add it if/when something does, rather than threading an always-false flag
  through every segment descriptor speculatively now.
- `#EXT-X-TARGETDURATION`'s own value is **not** simply the configured `targetDurationSeconds`
  echoed back -- since cutting on frame boundaries only can overshoot it, `HlsSegmenter` tracks the
  real maximum segment duration observed so far and writes `ceil(max(configured, observed))` on
  every playlist rewrite, so the tag can never be violated by a segment the playlist already lists
  (RFC 8216 §4.3.3.1 requires `TARGETDURATION` be no shorter than any listed segment's own
  duration) -- confirmed by a test that deliberately forces an overshoot.

**Acceptance criteria**:

- A VOD run against a known-length synthetic PCM source produces: the expected segment *count*
  (`ceil(totalDuration / targetDuration)`, accounting for the "cut on frame boundaries only"
  rounding), every segment's own duration within one frame-duration of the target (except the
  final, necessarily-short one), and a playlist whose segment durations sum to the source's total
  duration within the same one-frame tolerance.
- Playlist grammar validation: every required tag (`#EXTM3U`, `#EXT-X-VERSION`,
  `#EXT-X-TARGETDURATION`, `#EXT-X-MEDIA-SEQUENCE`) present exactly once, `#EXTINF` lines paired
  1:1 with segment URI lines, `#EXT-X-ENDLIST` present for VOD and absent for an in-progress
  (not-yet-`Complete()`d) playlist — checked by direct string/line assertions (not a full m3u8
  parser; that's more machinery than this plan's own test needs justify).
- `InMemoryStreamingSink`/`FileSystemStreamingSink`/`DelegateStreamingSink` each round-trip every
  write correctly on their own terms (content equality for the first two, exact forwarding for the
  third); `FileSystemStreamingSink` rejects a non-existent directory immediately, and
  `DeleteMediaSegment` against an unwritten/already-deleted segment is a safe no-op on both
  file-backed and in-memory sinks.
- An end-to-end run through a real `AacStreamingEncoder` into a live `HlsSegmenter` (not just
  directly-constructed synthetic frames) produces segments whose own `trun` sample counts sum back
  to the exact total PCM sample count written.
- 100% branch coverage across `HlsSegmenter`/`M3u8PlaylistWriter`/all three sink implementations.

**Size**: large even after splitting off the live sliding window into its own item (3b) below —
this is the item where the most genuinely new policy logic (segment-boundary decisions) lives, on
top of two other substantial new items underneath it.

**Status**: done ([PR #112](https://github.com/eggspot/EggEncoder/pull/112)).
`IStreamingSink` + `FileSystemStreamingSink`/`InMemoryStreamingSink`/
`DelegateStreamingSink` (`src/EggEncoder/Streaming/`), `HlsSegmenter`/`M3u8PlaylistWriter`/
`HlsSegmentDescriptor` (`src/EggEncoder/Streaming/Hls/`). `HlsSegmenter.AddFrame` takes an
`EncodedAudioFrame` directly (not a pre-converted `Mp4EncodedFrame`) -- it copies `Payload` into its
own accumulating list internally, so a caller can wire `AacStreamingEncoder.Create(..., frame =>
segmenter.AddFrame(frame))` directly with no manual conversion step. VOD-only, as this item's own
title now reflects (split from the original combined item per this plan's own explicitly-allowed
"split into two PRs" escape hatch) -- see item 3b for the live sliding window.

**Found during item 5, fixed in its own PR**: `M3u8PlaylistWriter.Version` originally shipped as
`6`, "corrected" down from an initial `7` during this PR's own self-review on the strength of a
recollected (not verified) reading of RFC 8216 §7's compatibility table. While generating a real
multi-variant HLS fixture for item 5's own `CODECS` string verification, a real, current ffmpeg's
own `hls` muxer was observed declaring `EXT-X-VERSION:7` for the exact same fMP4+`EXT-X-MAP`/no-
I-FRAMES-ONLY combination this project's own playlist writer produces -- contradicting the earlier
"corrected" value. Fixed back to `7`, this time confirmed against that real fixture rather than a
recollected spec reading, matching this plan's own "verify against a real fixture, not memory"
discipline it should have followed the first time. A genuine process lesson, not just a trivia
correction: a self-review "fix" is not automatically more correct than what it replaces -- it needs
the same verification bar as the original code.

## 3b. HLS live sliding window

**Problem**: item 3a only ever grows its segment list and writes a single, final `#EXT-X-ENDLIST`
playlist. A genuinely live stream needs a bounded playlist (so a player's own playlist fetch stays
cheap and old segments can be reclaimed) that keeps growing at its live edge instead of ending.

**Design**: extend `HlsSegmenter` with a live-mode constructor overload taking a `liveWindowSize`
(the number of segments to keep *listed and retained*). Once the internal segment list exceeds that
size after a cut, drop the oldest segment from the list (its `sequenceNumber` is no longer described
in the playlist, and `#EXT-X-MEDIA-SEQUENCE` advances to match the new oldest-retained segment) and
call `IStreamingSink.DeleteMediaSegment` for it — matching real live HLS's own sliding-window
disk-usage behavior. No `#EXT-X-ENDLIST` is ever written in live mode until an explicit `Complete()`
call signals the live stream itself has ended (at which point it behaves like VOD's own finalization
from here on).

**Acceptance criteria**:

- After producing more segments than `liveWindowSize`, the playlist lists exactly `liveWindowSize`
  segments, `#EXT-X-MEDIA-SEQUENCE` matches the oldest *retained* segment's own sequence number, and
  `IStreamingSink.DeleteMediaSegment` was called exactly once per dropped segment, each with the
  correct sequence number, in the order they aged out.
- No `#EXT-X-ENDLIST` appears in any playlist write before `Complete()` is called in live mode; it
  appears in the next write after.
- `liveWindowSize` smaller than 1, or a VOD-mode call to whatever API surfaces live-only behavior,
  throws a clear, typed exception rather than silently behaving like VOD or like an empty window.
- 100% branch coverage of the new live-mode code paths.

**Size**: small–medium, now that item 3a's own segment-cutting/playlist-writing machinery already
exists to build on.

**Status**: done ([PR #113](https://github.com/eggspot/EggEncoder/pull/113)). Added a second public
`HlsSegmenter` constructor overload taking
`liveWindowSize` (the existing VOD constructor is untouched and still the default choice); both
delegate to one shared private constructor so the two modes can't drift apart. `CutSegment` prunes
the oldest segment(s) in a `while` loop once the retained count exceeds the window, calling
`IStreamingSink.DeleteMediaSegment` for each. The "VOD-mode call to whatever API surfaces live-only
behavior" criterion above doesn't apply to the actual design: there is no separate live-only method
or property to call inappropriately -- live-vs-VOD is chosen entirely by which constructor is used,
and every other method (`AddFrame`/`Complete`) behaves identically in either mode (the live-only
`liveWindowSize < 1` validation lives in the live constructor itself, where it's the only
constructor parameter specific to that mode).

## 4. MPEG-TS muxer (alternative segment format)

**Problem**: not every HLS player/CDN prefers fMP4 segments; the legacy `.ts` segment format (plain
MPEG-TS, ADTS-framed AAC) is still widely deployed and simpler for some consumers. This item gives
the segmenter from item 3 a second segment-format option alongside fMP4.

**Design**:

- `src/EggEncoder/Streaming/MpegTs/MpegTsMuxer.cs`: given a list of `Mp4EncodedFrame`-shaped
  payloads for one segment (the same durable shape item 3's own `HlsSegmenter.AddFrame` already
  builds for item 2's media-segment writer), wraps each in ADTS via `AdtsFrameWriter.Wrap`,
  packetizes into PES packets (one PES per AAC frame is the simplest, spec-legal choice; PES header
  carries a `PTS` in the MPEG-TS-mandated 90kHz clock — a *different* timescale from fMP4's own
  configurable one). **PTS is computed from the running TOTAL sample count, not accumulated as a
  fixed per-frame delta**: `1024 * 90000 / 44100` is not an integer, so a naive per-frame delta
  would drift over a long enough stream; instead each frame's PTS is independently
  `round(cumulativeSamplesBeforeThisFrame * 90000.0 / sampleRate)`, wrapped to 33 bits per the
  spec's own clock-rollover rule -- bounded to at most ±0.5 ticks of rounding error at any point,
  never unboundedly drifting. Splits each PES into 188-byte TS packets with the mandatory sync byte
  (`0x47`)/PID/continuity-counter fields (confirmed against a real ffmpeg-produced `.ts` fixture:
  every packet uses `adaptation_field_control`=01/payload-only, even a short final one, with plain
  `0xFF` stuffing bytes rather than an adaptation field's own stuffing mechanism), and emits a PAT +
  PMT (declaring one `stream_type=0x0F`/ADTS-AAC stream) at the start of every segment -- every
  segment is an independently-parseable file with its own fresh continuity counters, matching real
  HLS TS segmenters' own convention (PTS continuity is preserved across segments by the muxer's own
  internal running sample-count state instead, the same role `FmpMediaSegmentWriter`'s own
  `baseMediaDecodeTime` state plays for fMP4). PAT/PMT CRC-32 uses ISO/IEC 13818-1 Annex B's own
  variant (`MpegTsCrc32`): same polynomial and bit direction as `Codecs.Opus.OggCrc32`, but a
  different initial value (0xFFFFFFFF vs. 0) -- not interchangeable, so it gets its own small
  implementation rather than reusing that one with the wrong seed. PCR is **not** implemented (a
  deliberate MVP scope limitation -- see the status note below).
- The HLS segmenter (item 3) gains a `SegmentFormat` enum (`Fmp4`, `MpegTs`) constructor parameter
  (defaulting to `Fmp4`, so every existing call site is unaffected); when `MpegTs` is selected, no
  init segment is written at all, `CutSegment` calls this muxer instead of item 2's, the playlist
  omits `#EXT-X-MAP` entirely, and segment URIs use a `.ts` extension instead of `.m4s`.

**Acceptance criteria**:

- **Primary conformance oracle**: a hand-written (test-only) minimal MPEG-TS/PES/ADTS parser —
  not a general-purpose one, just enough to walk TS packets, reassemble PES payloads, strip ADTS
  headers, and recover the original frame payload bytes and PTS values — confirms every frame
  round-trips byte-for-byte and every PTS value exactly matches
  `round(cumulativeSamplesBeforeThisFrame * 90000.0 / sampleRate)` (not a fixed per-frame delta --
  see the design note above on why that would drift).
- **Secondary oracle, environment-gated**: if `ffprobe` is found on `PATH`, confirm it accepts a
  produced `.ts` segment and reports the expected codec/sample rate/channel count.
- PAT/PMT are present and well-formed (correct sync byte, correct table IDs) at the start of every
  produced segment.
- 100% branch coverage.

**Size**: medium. TS packetization itself is mechanical and well-specified; most of the real effort
is the test-only parser needed to verify it (budget real time for that, it's not throwaway code —
treat it with the same correctness bar as the production muxer it's checking).

**Status**: done ([PR #114](https://github.com/eggspot/EggEncoder/pull/114)).
`MpegTsMuxer`/`MpegTsCrc32` (`src/EggEncoder/Streaming/MpegTs/`),
wired into `HlsSegmenter` via a new `SegmentFormat` parameter (default `Fmp4`, fully backward
compatible). PAT/PMT byte layout (table_ids, `section_length`, `PCR_PID`/`stream_type` field
positions) and the PES header's 5-byte PTS-only encoding were confirmed bit-for-bit against a real
ffmpeg-produced `.ts` fixture (`ffmpeg ... -f mpegts`), not assumed from the spec text alone.
Self-conformance (byte-for-byte frame round-trip + exact PTS values + multi-segment PTS continuity
with independently-reset PAT/PMT/audio continuity counters per segment) passes. A full `ffmpeg`
decode of this muxer's own real output shows *more* decode errors than the equivalent plain-ADTS
file (799 vs. 245 error lines for the same content) -- investigated directly: this project's own
self-conformance parser recovers byte-for-byte identical ADTS frames from both the `.ts` file and
the plain `.aac` file (0 mismatches across all 87 frames), proving the TS muxing itself is correct;
the extra ffmpeg errors are a downstream effect of how its AAC decoder's own error-recovery
resyncs differently depending on how data arrives from its TS demuxer vs. a flat file, layered on
top of the same pre-existing, already-documented `AacFrameEncoder` bitstream gap from item 2's own
status note -- not a new defect introduced here. PCR is not implemented (PMT's `PCR_PID` still
points at the audio stream, matching real convention, but no adaptation-field PCR values are ever
inserted) -- a real, stated MVP scope limitation that could affect a strict broadcast-grade
decoder's buffering model, though file-based players generally tolerate deriving timing from PTS
alone; add it as its own follow-up if a real player is found that needs it.

## 5. Master playlist (multi-bitrate)

**Problem**: a real HLS deployment with more than one audio bitrate/rendition needs one top-level
playlist a player fetches first, listing every available rendition's own media playlist.

**Design**: `src/EggEncoder/Streaming/Hls/M3u8MasterPlaylistWriter.cs`:
`BuildMasterPlaylist(IReadOnlyList<HlsRenditionDescriptor> renditions) -> string`, where
`HlsRenditionDescriptor` carries `(string Uri, int BandwidthBitsPerSecond, string CodecsString)`
(e.g. `"mp4a.40.2"` for AAC-LC, the exact RFC 6381 codec string real HLS players expect — confirmed
against a real multi-bitrate-audio HLS stream's own master playlist, not assumed from the AAC
object-type number alone). Emits one `#EXT-X-STREAM-INF:BANDWIDTH={n},CODECS="{s}"` + URI pair per
rendition — the simpler "N independent audio-only variant streams" structure real-world podcast/
radio HLS output commonly uses, not the `#EXT-X-MEDIA:TYPE=AUDIO` grouped-rendition form (that form
exists for *video-plus-alternate-audio-track* scenarios this plan doesn't address; note it here as
a documented, deliberately-deferred option if the owner's own use case grows to need it later,
rather than building it speculatively now).

**Acceptance criteria**:

- Grammar validation (same style as item 3's media-playlist check): `#EXTM3U` present once, one
  `#EXT-X-STREAM-INF`+URI pair per rendition, `BANDWIDTH`/`CODECS` attributes present and correctly
  quoted/formatted.
- A real player-compatible `CODECS="mp4a.40.2"` string for AAC-LC, verified against a real
  reference master playlist's own bytes.
- 100% branch coverage.

**Size**: small. The simplest item in this plan — a pure, stateless string formatter with no
muxing/timestamp concerns at all.

**Status**: done (PR TBD). `M3u8MasterPlaylistWriter.BuildMasterPlaylist`/`HlsRenditionDescriptor`
(`src/EggEncoder/Streaming/Hls/`). Reuses `M3u8PlaylistWriter.Version` directly (no separate
version constant) so the two writers can never disagree about which `EXT-X-VERSION` to declare.
`"mp4a.40.2"` and the overall `#EXT-X-STREAM-INF:BANDWIDTH={n},CODECS="{s}"` + URI shape (no
`AVERAGE-BANDWIDTH`, which is optional per spec and this writer has no meaningful value to supply
for) were confirmed against a real multi-variant HLS master playlist generated via ffmpeg's own
`-var_stream_map` -- the same run that also reconfirmed `EXT-X-VERSION:7` (see item 3a's own
"Found during item 5" addendum).

## 6. Live input source: PCM pump, timestamps, drift/silence-fill policy

**Problem**: `IStreamingEncoder.Write` (item 1) is already the right shape for *feeding* PCM in,
but a genuinely live capture source (a microphone callback, an incoming network PCM stream) arrives
on its own schedule, not in neat, exactly-on-time blocks — and can drift (run fractionally fast or
slow relative to its own declared sample rate) or hiccup (a gap in arrival that doesn't correspond
to real silence in the source).

**Design**: `src/EggEncoder/Streaming/LiveAudioPump.cs` — wraps an `IStreamingEncoder`, exposes
`PushSamples(ReadOnlySpan<int> interleavedSamples, DateTimeOffset? captureTimestamp = null)`.
Internally tracks, from the first `PushSamples` call, the expected total sample count implied by
elapsed wall-clock time (`captureTimestamp - firstCaptureTimestamp`) versus the actual cumulative
sample count pushed so far, and applies a configurable `DriftPolicy`:
- `DriftPolicy.Ignore` (default): push every sample through unmodified, regardless of measured
  drift — the right choice when the caller already has its own clock discipline and just wants a
  pass-through.
- `DriftPolicy.SilenceFill`: if actual-so-far falls behind expected-so-far by more than a
  configurable threshold (a real gap, not just scheduling jitter), insert silence frames to catch
  up before the next real samples are pushed — keeping wall-clock alignment for a source that
  occasionally stalls, at the cost of audibly briefly "going quiet" rather than skipping ahead in
  time, the same tradeoff every real live-broadcast silence-fill policy makes.
`captureTimestamp` is optional specifically so a caller with no real wall clock available (feeding
from a file for testing, say) can omit it and get pure pass-through behavior with zero drift logic
engaged, regardless of which `DriftPolicy` is configured — drift detection needs a clock to compare
against, and silently inventing one would be worse than not detecting drift at all.

**Acceptance criteria**:

- `DriftPolicy.Ignore` with deliberately-irregular `captureTimestamp` values pushes every sample
  through unchanged, in order, with no insertions — proving "ignore" genuinely ignores timing.
- `DriftPolicy.SilenceFill` fed a synthetic gap (two pushes whose `captureTimestamp` values are
  further apart than the samples between them account for) inserts the correct number of silent
  samples to close the gap, verified by feeding the encoder's own resulting total sample count back
  and confirming it matches wall-clock-implied duration within one frame's tolerance.
- Omitting `captureTimestamp` entirely never triggers silence-fill, under either policy.
- 100% branch coverage.

**Size**: small–medium.

**Status**: not started.

## 7. Low-latency HLS (future sketch, not scheduled)

RFC 8216's own low-latency extensions — `EXT-X-PART`/partial segments (sub-segment chunks a player
can start fetching before the containing segment is fully produced, reducing end-to-end latency
below one full target-duration), `EXT-X-PRELOAD-HINT`, blocking playlist reloads — are real,
well-specified, and *not* part of this plan's own scheduled work. They'd layer on top of item 3's
own segmenter (a partial segment is naturally "the frames accumulated so far, within the current
in-progress segment, flushed early") without changing items 1/2/4/5's own design, which is why
nothing above needs to pre-emptively accommodate them structurally. Revisit this item once items
1–6 are done and real low-latency demand exists — implementing it speculatively now, with no
consumer to validate the design against, risks exactly the kind of premature-abstraction this
codebase's own working style already warns against.

**Status**: not started; deliberately unscheduled.

## 8. Opus/MP3/FLAC streaming encoder sessions

Follow-up to item 1, extending the same `IStreamingEncoder` shape to the codecs item 1 itself
doesn't cover (AAC-LC is first because it's HLS's own most universally-supported audio codec, and
every muxer/segmenter item above is designed, tested, and shipped against it before any other codec
needs to prove the pattern generalizes). Each is its own small, independent sub-item once item 1
lands:

- **Opus**: reuses `OpusEncoderSession`'s own Concentus-backed encode internals, the same "change
  only the output plumbing, not the codec logic" principle item 1 established for AAC. Opus frames
  are naturally segment-boundary-friendly (fixed 20ms/960-sample frames at Opus's own fixed 48kHz),
  and CMAF has a defined Opus-in-fMP4 convention (`dOps` box) worth targeting directly rather than
  inventing one.
- **MP3**: reuses `Mp3EncoderSession`'s own pure-managed encode internals (no NLayer involvement at
  all — NLayer is decode-only, see "Prerequisites" above). MPEG-TS has a long-established native
  MP3-framing convention (unlike AAC's ADTS-via-wrapping approach, MP3 frames are already
  self-delimiting) worth confirming against a real reference file rather than assumed.
- **FLAC**: lossless streaming is a real but lower-demand case for a *live-streaming* plan
  specifically (FLAC-over-HLS is uncommon in practice vs. AAC/Opus/MP3); worth doing once the
  pattern is proven three times over, not before.

**Status**: not started.

## Top-level test strategy (applies across every item above)

Beyond each item's own "Acceptance criteria," the testing *approach* that recurs throughout this
plan:

1. **Self-conformance by parsing our own output with our own reader, wherever one already
   exists.** `MovAtomReader`'s own atom-tree walk works unchanged against this plan's own
   fMP4 output (see item 2) with zero new production reader code needed — the single strongest,
   cheapest oracle this plan has, and the default choice wherever it applies.
2. **`ffprobe`/`ffmpeg`, as a local, optional, environment-gated secondary oracle only** — every
   such test must skip cleanly (not fail) when neither is found on `PATH`, the same convention
   `docs/video-support-backlog.md` already established for its own fixture generation. Never a CI
   dependency; CI must pass with neither tool present.
3. **Play-through decode**, wherever a decode path already exists to drive (AAC's own `AacDecoder`
   against item 1's raw access units directly, no muxer involved) — confirms the *encoded bitstream
   itself* round-trips correctly, independent of any container/segmenting concern.
4. **Segment-duration tolerance**, not exact-match — HLS segments are cut on codec-frame
  boundaries, so "exactly `targetDurationSeconds`" is never the real bar; "within one frame's worth
  of the target, except deliberately-short final/live segments" is.
5. **Playlist grammar validation via a small, test-only line-by-line checker** — not a full m3u8
   parser (more machinery than any test here needs) and not loose string-contains assertions either
   (too weak to catch a malformed tag) — a checker that knows the specific required tags, their
   required order/cardinality, and attribute syntax this plan's own writers need to get right.
6. **100% branch coverage of every new production type**, the same bar every other part of this
   codebase already holds itself to — happy path, every edge case (empty input, exactly-one-frame
   input, a live window boundary, etc.), every validation/exception path.
