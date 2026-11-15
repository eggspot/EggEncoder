# Video Support Backlog

EggEncoder today is audio-only (MP3/FLAC/AAC/WAV/WMA/AIFF/ALAC/TTA), pure .NET + P/Invoke,
no ffmpeg/subprocess dependency anywhere. This document tracks the plan to extend it to video
decode, encode, and format conversion under the exact same philosophy: no ffmpeg at runtime,
pure managed code wherever feasible, P/Invoke to a small number of permissively-licensed native
libraries where it isn't (the same pattern already used for `libmp3lame`/`libFLAC`).

This is a multi-session, multi-month initiative. **This file is the source of truth for what's
done, what's next, and why** — it exists so a fresh session can make real progress without
re-deriving the plan or re-researching licensing from scratch.

## How to use this backlog (read this first, every time)

You are almost certainly a fresh Claude Code session with no memory of the conversation that
created this file. Here is everything you need:

1. **Read this whole file once.** It's long on purpose — the "why" behind each item's
   prioritization and feasibility classification is as important as the checklist itself. Don't
   skip straight to the checkboxes.
2. **Read the repo's own `CLAUDE.md`** for the house conventions (feature branch + PR, never push
   to `main`, conventional commit prefixes, 100% branch coverage on new code, `xUnit` +
   `FluentAssertions` + AAA pattern, `Feature_Condition_ExpectedBehavior` test naming).
3. **Check `git log --oneline -20`, `gh pr list --state all`, and this file's checkboxes together**
   before picking anything — if a feature branch from a prior session is open and unmerged,
   continue that instead of starting something new. If an item here is checked off but you can't
   find a merged PR for it, something is inconsistent — investigate before trusting the checkbox.
4. **Pick the first unchecked item** whose "Depends on" line (if any) is already checked off.
   Phase 5 (below) comes before items 3-11.
   Items are pre-ordered by priority/dependency/value — you shouldn't need to reorder them, only
   skip one if it's genuinely blocked (see step 8).
5. **Read the "Architecture notes" section below** for where the existing pieces you'll build on
   live (`MovAtomReader`, `Mp4SampleTable`, `NativeLibraryLoader`, the `IMediaEncoder` surface,
   the `FlacFfmpegCrossCheckTest` pattern) — don't duplicate what already exists.
6. **Implement the item with tests.** Follow the audio codecs' established shape:
   - Full branch coverage: every public type/method, happy path, edge cases, every
     validation/exception path.
   - **Use a real ffmpeg-produced fixture wherever one is feasible**, the same way
     `FlacFfmpegCrossCheckTest` decodes an ffmpeg-produced `.flac` and compares it bit-exactly
     against the original source. For video this usually means: generate a short, small,
     synthetic test clip with `ffmpeg` (a solid-color or simple-pattern source is fine — this is
     for correctness cross-checking, not perceptual quality), check the clip into the test
     fixtures directory (keep it tiny — a few KB to low hundreds of KB, a handful of frames at a
     low resolution like 64x64 or 160x90, not a real video), and decode it with both this
     library's new code and record the expected values via `ffprobe`/`ffmpeg`-dumped ground
     truth (frame count, keyframe positions, raw sample bytes/hashes, pixel values) baked into the
     test as literals — exactly like `MovProbeTest`'s existing `test.mov`/`test.mp4` fixtures,
     whose ground truth (`DurationInSeconds=5`, `Width=640`, `Height=360`, `CodecFourCc="avc1"`)
     was read directly off `ffprobe`'s own output for that file. No external ffmpeg install should
     ever be required to *run* the tests — only to *generate* the fixture once, by you, now.
   - If an item wraps a native library, mirror `Native/FlacNative.cs` + `Native/NativeLibraryLoader.cs`
     exactly: `[LibraryImport]` (not `[DllImport]`), AOT-safe, loaded from `Native/win-x64/` via the
     existing custom `DllImportResolver`. Add the new `.dll` to that same `win-x64` directory and to
     `EggEncoder.csproj`'s `PackageCopyToOutput` item group (copy the existing `libmp3lame.dll`/
     `libFLAC.dll` entries' shape exactly).
   - **Every new bundled native binary must get an entry in `THIRD-PARTY-NOTICES.md`**, mirroring
     the existing `libmp3lame.dll`/`libFLAC.dll` entries' format (project name, license, how it's
     used, source availability link).
7. **Wire it into the existing surface where it genuinely fits.** Early container-only items
   (demuxing/muxing with no decodable codec yet) are legitimately internal infrastructure with
   no end-user-visible change — that's fine, don't force a premature `IMediaEncoder` hookup. Once
   an item makes a real decode or encode path *usable end-to-end* (bytes in, pixels/frames out, or
   frames in, a valid file out), wire it into `NativeEncoder`'s extension-dispatch switches
   (`Probe`/`ConvertFile`/`CutFile`) the same way every audio codec is wired in — see
   `NativeEncoder.cs`'s `.caf`/`.tta` cases for the pattern. `CodecType` in `ProbeResult` is
   already `"video"` for MOV/MP4 video metadata (see `ProbeVideo`) — reuse it, don't invent a
   parallel field. **Video-specific concerns that have no audio equivalent** (keyframes/GOPs,
   frame-accurate cutting at non-keyframe boundaries needing a decode-from-the-prior-keyframe step,
   A/V muxing keeping two tracks in sync) are real extensions to the `Probe`/`ConvertFile`/`CutFile`
   shape, not a reason to invent a parallel interface — extend the existing one thoughtfully,
   document the extension, and say so explicitly in the item's own PR description so the next
   session understands why the signature changed.
8. **Commit to a feature branch, open a PR, self-review (two passes, per `CLAUDE.md`), merge.**
   Exactly the same workflow the audio codec work already uses (see any of PRs #14-#24 for the
   shape: feature branch, conventional commit prefix, PR with what+why+test-coverage, two
   self-review passes catching real things, squash-merge).
9. **Check the item off in this file** (`- [ ]` → `- [x]`) **in the same PR**, with a one-line
   "status" note after it (merged PR link + anything genuinely left out of scope for that item —
   see the existing checked items below for the format once any exist). If genuinely blocked
   (licensing uncertainty that needs the owner's real sign-off, a dependency item not actually
   done despite being checked, research that can't reach confidence — same discipline as the
   audio codec work's `tool_uses`-verification lesson, see below), **do not silently skip it** —
   leave it unchecked, write exactly why under it, and move to the next genuinely-unblocked item
   instead, the same way the audio codec backlog handled WavPack being assessed NO-GO for a single
   session.
10. **Update the external scope file** at `/Users/hv/.hermes/herdr-claude-state/scope/eggencoder.md`
    (not part of this repo, never committed) with what changed, what's left, and any open
    questions — same convention the audio codec work already used every session.

### A lesson from the audio codec work, worth repeating here

A research pass for one of the audio codecs (TTA) once returned a "confirmed via ffmpeg's source"
verdict while having made **zero actual web-search/fetch tool calls** — it fabricated algorithm
details from training-data memory while presenting them as verified, and got real facts wrong.
The licensing research behind *this very backlog* caught a similar staleness risk the other
direction: a naive assumption that "AV1 is the obviously safe, patent-clean choice" would have
been **wrong as of 2026** without actually checking (see AV1's entry below). **Patent pool status,
licensing terms, and even codec risk rankings are not timeless facts — they drift, and your
training data has a cutoff.** Any licensing/feasibility claim in a future item's research must be
backed by something actually fetched *this session*, with a citation, not reconstructed from
memory — and if a sub-agent/research fork is used, check its actual tool-call count before
trusting a "confirmed" verdict, the same discipline already established for the audio codec work.

## Scope philosophy

- **No ffmpeg, no subprocess, ever.** This is non-negotiable and matches the existing audio side
  exactly.
- **Pure managed C# first**, for containers always, and for codecs wherever genuinely feasible in
  a reasonable number of sessions (see each codec's classification below).
- **P/Invoke to one permissively-licensed native library per codec** where pure-C# isn't
  realistic — same shape as `libmp3lame`/`libFLAC` today: a single bundled `win-x64` DLL, loaded
  via the existing `NativeLibraryLoader`, documented in `THIRD-PARTY-NOTICES.md`. LGPL is
  acceptable for a *dynamically-loaded, unmodified, replaceable* binary (the existing
  `libmp3lame.dll`/LGPL-2.1 precedent already establishes this for this specific distribution
  shape) — but GPL (full copyleft, e.g. `x264`) is **not**, since that licensing would require this
  whole project to become GPL to redistribute it.
- **Patent-encumbered codecs are flagged, never bundled silently.** An independently-written (or
  independently-sourced) implementation avoids *copyright* infringement, not *patent*
  infringement — patents cover techniques, not source code. A codec with an active, commercially-
  enforced patent pool (H.264, HEVC — see below) is explicitly gated behind the repo owner's own
  legal sign-off before any implementation work starts on it, and is never bundled as a side
  effect of unrelated work. "Royalty-free" codecs aren't automatically zero-risk either — AV1's
  current (2026) litigation exposure is flagged with the same honesty, just at a lower severity
  tier than H.264/HEVC's actively-collecting pools.
- **Decode before encode**, always, for a new codec — decode has one correct answer to converge
  on; encode is open-ended quality/ratio tuning that's only worth doing once decode is solid and
  there's a real use case for producing that format.
- **No overselling.** "Match or exceed ffmpeg's coverage" is the stated ambition, not a promise
  every item here keeps. Several formats below are explicitly marked low-priority-forever or
  out-of-scope, with the real reason stated plainly.

## Architecture notes (what already exists to build on)

- **`IMediaEncoder`** (`src/EggEncoder/IMediaEncoder.cs`): `Probe`/`ConvertFile`/`CutFile`. Every
  audio codec dispatches through `NativeEncoder`'s extension `switch` statements
  (`NativeEncoder.cs`) into per-format `Codecs/<Format>/` classes. Video formats should follow the
  identical dispatch-by-extension shape once they're end-to-end usable (see step 7 above).
- **`Results/ProbeResult.cs`**: already has `CodecType` (`"video"` is already a used value — see
  `NativeEncoder.ProbeVideo`), `Width`/`Height`, generic enough for video metadata without
  changes.
- **`Codecs/Mov/`**: the existing MOV/MP4 groundwork, directly reusable:
  - `MovAtomReader.cs` — generic ISO base media (box/atom) tree walker, format-agnostic, shared
    between audio decode, metadata probing, and (since item 1) video demuxing. Also has
    `FindTrackByHandlerType(stream, moov, handlerType)` — finds the first `trak` whose
    `mdia`/`hdlr` component type matches (`"soun"` for audio, `"vide"` for video); use this
    rather than writing a new track-lookup loop for anything else added here.
  - `Mp4SampleTable.cs` — resolves a `stbl` box's `stsz`/`stsc`/`stco`/`co64` into
    `(offset, size)` pairs per sample via `ReadSamples` (format-agnostic — doesn't know or care
    whether the samples are audio or video), plus `ReadSyncSamples` (since item 1) for the
    optional `stss` (sync sample / keyframe) box — returns a 0-indexed `HashSet<int>?`, where
    `null` specifically means "no `stss` box at all" (spec: every sample is a sync sample) and a
    non-null empty set means "`stss` present but declares zero entries" (spec: no sample is ever
    a sync sample) — a real distinction the two don't collapse into each other.
  - `MovDecoder.cs` / `MovProbe.cs` / `MovVideoDemuxer.cs` — audio-decode, metadata-probe, and
    (since item 1) video-sample-demuxing respectively, all three built on the two readers above
    following the same shape: find the track, resolve its sample table, return raw per-sample
    data. `MovVideoDemuxer.DemuxVideoTrack` returns raw bytes/keyframe-flags only, no codec
    decode — a future codec item builds its decoder on top of this, not by re-deriving container
    parsing.
  - `test.mov` / `test.mp4` in `src/EggEncoder.UnitTests/Codecs/Mov/`: real H.264-in-MP4 fixtures
    (640x360, `avc1`, 125 frames @ 25fps, confirmed via `ffprobe` to have exactly one keyframe at
    sample index 0) with zero audio track — already used by item 1's own tests
    (`MovVideoDemuxerTest.cs`) and immediately reusable by any future item needing a real
    container-level (not yet decodable) video fixture.
- **`Native/`**: `NativeLibraryLoader.cs`'s `[ModuleInitializer]`-registered `DllImportResolver`
  already generalizes to any additional library name added to its `_managedLibraryNames` array —
  adding `libvpx`/`dav1d`/etc. later is a one-line change there, not a new mechanism.
  `FlacNative.cs`/`Mp3Native.cs` are the exact `[LibraryImport]` + `[UnmanagedCallConv]` shape to
  mirror for any new native codec binding.
- **`FlacFfmpegCrossCheckTest.cs`** (`src/EggEncoder.UnitTests/Codecs/Flac/`): the cross-check
  fixture pattern to mirror — decode an ffmpeg-produced reference file with this library's own
  decoder and compare bit-exactly against the known-correct source, with the fixture checked into
  the repo (no ffmpeg install needed to *run* the test).
- **AOT**: `EggEncoder.csproj` has `IsAotCompatible=true`. Any new P/Invoke must use
  `[LibraryImport]` (not `[DllImport]`) and `[UnmanagedCallersOnly]` + `GCHandle` for native
  callbacks (see `FlacDecoder`'s existing pattern), never `Reflection.Emit` or marshaled delegate
  closures.

## Backlog

Ordered by priority/dependency/value. An item's "Depends on" line names a prerequisite by number.
Phase 5 comes first: pick its first unchecked item whose "Depends on" items are all checked off
before any of items 3-11.

### Phase 5 — Video rendering (takes priority over items 3-11)

Decided by the repo owner on 2026-11-14: EggEncoder renders short videos end to end with no
ffmpeg — decode H.264 clips, edit frames (fit, crop, rotate, blur, boxes, text, subtitles, fades),
mix audio, encode H.264 + AAC into a progressive MP4. Its first consumer is a production video
renderer that today shells out to ffmpeg; every item below exists because that renderer needs it.
H.264 is approved for this phase (see Phase 4); VP9/VP8/AV1/MPEG-2 and the extra containers (items
3-11) wait until this phase is done.

Rules for every Phase 5 item, on top of the ones above:

- **Clean-room.** Written from the ITU-T/ISO/IEC specifications only. No code is copied or ported
  from ffmpeg, x264, openh264 or any other implementation; reading them to understand a spec point is
  fine, copying is not. Each codec item adds a provenance line to `THIRD-PARTY-NOTICES.md` like the
  managed codec rewrites already did.
- **Third-party readability is proven in CI without ffmpeg.** Every writer or encoder item checks in
  a few tiny output files with `ffprobe`/`ffmpeg -f framemd5` ground truth baked in as literals; the
  live ffmpeg oracle stays gated on `EGGENCODER_FFMPEG_ORACLE=1` (`FlvWriterTest`'s pattern).
- **Fixture size.** Each checked-in fixture stays small (≤ 50 KB for filter/PSNR fixtures). Real
  third-party clips are never checked in; tests against them run only when a local path is set.
- **AOT.** New public types are added to `EggEncoder.AotSmokeTest` in the same PR.
- **Numbering.** These items continue this file's numbering; always say "backlog item N" (the
  live-ingest and streaming plans have their own item numbers).
- **Public text.** `README.md`, `docs/index.html` and `docs/feature-roadmap.md` describe what
  ships; they change in the PR that ships the feature, not before.

- [x] **15. MP4 file source** — `Containers/Mp4/Mp4FileSource`, an MP4 → `MediaPacket` source:
  real decode/composition times from `stts`/`ctts`, `elst` applied (B-frame shift, AAC priming),
  `avcC`/`hvcC`/`esds` into `MediaTrackInfo`, tracks interleaved by DTS, keyframe seek, rotation
  from the `tkhd` matrix (a new optional `MediaTrackInfo` field). Fragmented MP4 is refused with
  `NotSupportedException`. `MovProbe` gains audio presence, frame count, frame rate and rotation.
  Box readers stay in `Codecs/Mov/`; `Containers/Mp4/` holds only the packet-level source, writer,
  joiner and splitter, mirroring `Containers/Flv/`. This is the MP4 part of
  `docs/live-ingest-restream-plan.md` item 8, whose pacing, looping and other sources stay there.
  - **Depends on**: 1.
  - Status: done. `Mp4FileSource` reads H.264, H.265, AAC and MP3 tracks (other codecs are
    skipped and counted), applies the first media edit plus any leading empty edits (later edits
    are ignored), widens 1- and 2-byte NAL lengths to 4, and seeks per track to the last sync sample
    at or before the start time (non-video tracks one sample earlier for decoder pre-roll). Packets
    are ordered by decode time, not by file position as ffmpeg's demuxer orders close ones.
    `ProbeResult` gains `FrameCount`, `FrameRate`, `Rotation` and `HasAudio` for MOV/MP4.
- [x] **16. Stereo AAC-LC decode** — channel pair elements, M/S and intensity stereo, TNS, pulse
  data; LFE ignored for stereo output; PCE read; HE-AAC (SBR/PS) refused with
  `NotSupportedException`. `MovDecoder` returns two channels instead of throwing. TNS and pulse
  come from hand-assembled bitstreams (a test-only bit writer), since ffmpeg's encoder writes
  neither by default; PCM is compared within a stated tolerance, like the mono tests.
  - **Depends on**: nothing.
  - Status: done. `AacFrameDecoder(sampleRate, channels)` decodes SCE, CPE (common window, M/S,
    intensity stereo), LFE (parsed and dropped), DSE, PCE and FIL elements, pulse data, TNS, PNS,
    every window sequence with the previous frame's window shape on the rising half, and the
    scalefactor band tables of every sampling rate. Coupling channels, more than two channels, gain
    control and SBR fill data are refused with `NotSupportedException`. Against ffmpeg's decode the
    samples differ by at most 3 (ADTS stereo at 44.1 kHz with M/S, intensity stereo and short
    windows; mono at 8, 16, 24, 32, 64 and 96 kHz; stereo AAC in MP4 at 48 kHz); noise
    substitution matches in energy only. The existing AAC encoder now ends each raw_data_block
    with ID_END, which it left out.
- [x] **17. Stereo AAC-LC encode with bitrate control** — 48 kHz stereo at a target bitrate
  (192 kb/s is the first consumer's), checked with item 16's decoder and baked ffmpeg-decode
  literals.
  - **Depends on**: 16.
  - Status: done. `AacFrameEncoder` writes mono (SCE) or stereo (CPE, common window, M/S chosen per
    band) at any standard sample rate and a target bit rate (`AacEncoder.Encode`,
    `AacEncoderSession.OpenSession` and `AacStreamingEncoder.Create` overloads take `bitRate`; 0
    means 96 kb/s per channel). Long windows only. Each band's allowed noise comes from its
    energy, a tonality estimate (spectral flatness: 26 dB below tonal bands, 8 dB below noise-like
    ones) and the absolute hearing threshold; a global offset is binary-searched so the frame fits
    the average budget plus half the bit reservoir. Noise-like bands from 4 kHz up are sent as
    perceptual noise substitution; the bandwidth follows the rate per channel. On a test signal at
    192 kb/s stereo the SNR (27.7 dB) equals ffmpeg's native AAC encoder at the same rate.
    `encoded_stereo_48000_192k.aac` is a checked-in encoder output with its ffmpeg decode; ffprobe
    reads it as AAC LC 48 kHz stereo. Two bugs of the previous mono encoder are fixed: its
    spectrum was half the decoder's scale (output 6 dB too quiet) and its sign bits were inverted.
- [x] **18. Mixing and loudness** — `Pcm/PcmMixer`: N inputs with per-input gain, start offsets,
  looping, a first/longest/shortest duration rule and no normalization (ffmpeg `amix`
  `normalize=0`); a silence source; integrated loudness (ITU-R BS.1770, gated, stereo) read from an
  MP4; loudness normalization as measure → gain → true-peak limiter at a ceiling (the consumer uses
  -1.5 dBTP), documented as close to but not identical with ffmpeg `loudnorm`; normalization
  buffers the mixed audio (duration × 48000 × 2 × 4 bytes, stated in the API docs); mono 16 kHz
  downmix for speech recognition input.
  - **Depends on**: 15, 16.
  - Status: done. `PcmAudio` holds interleaved float PCM (mono or stereo, unclipped):
    `Decode(path)` reads any audio file `AudioCutter` reads plus the first AAC track of an
    MP4/M4A/MOV through `Mp4FileSource`: the first frame sits at its edit-list time and the rest follow
    without gaps, so the priming is dropped, a leading empty edit becomes silence and the end is trimmed,
    never extended, to the edit list (within 3 of ffmpeg's decode);
    `Silence`, `ConvertTo` (resampling without clipping), `DownmixForSpeech` (mono 16 kHz) and
    `ToInt16` for the encoder. `PcmMixer.Mix` sums `PcmMixInput`s (gain, start offset, loop) under a
    `PcmMixLength` rule or a fixed length, converting each input to the mix format, with no
    normalization or clipping. `LoudnessMeter` measures integrated loudness (the gating
    `WaveformCalculator` uses, now shared through one accumulator) and true peak (4x windowed-sinc
    oversampling); both match ffmpeg `ebur128` within 0.15 LU and 0.1 dB on the test signals.
    `LoudnessNormalizer.Normalize` applies the gain to the target, then a look-ahead true-peak
    limiter that never lets the true peak, as `LoudnessMeter` measures it, pass the ceiling (-70 to
    0 dBTP, -1.5 by default); it lands within 1 LU of ffmpeg's two-pass linear `loudnorm` (gated
    oracle) and takes about a second for a minute of heavily limited 48 kHz stereo. Audio shorter than
    400 ms is only limited, silence is left as it is.
- [x] **19. Sidechain ducking** — `Pcm/SidechainCompressor`, a two-input processor (key and target,
  block-aligned), not an `IPcmTransform`: ffmpeg `sidechaincompress`'s threshold, ratio, attack and
  release, its default knee, makeup and detection, with ffmpeg's own envelope timing (see Status);
  the gated oracle test bounds the RMS error against ffmpeg.
  - **Depends on**: 18.
  - Status: done. `SidechainCompressor` (float, key and target mono or stereo, fixed at
    construction) with `SidechainCompressorSettings` holding ffmpeg's options, ranges and defaults
    (input gain, threshold, ratio, attack, release, makeup, knee, RMS/peak detection,
    average/maximum link, key gain, mix; upward mode left out). It does not reuse
    `CompressorTransform`'s envelope: that one is an exp(-1/(fs·t)) peak follower, while matching
    ffmpeg's ducking needs its smoothing coefficient min(1, 4000/(ms·fs)) on the key's absolute or
    squared level, so the envelope here is ffmpeg's. Gain is computed in the log domain with a
    cubic Hermite soft knee. Against ffmpeg (2021-12-20 git build) the gain stays within 0.005 at 18
    points for three settings (baked literals); the gated oracle bounds the whole-output RMS error
    at 0.002. `Duck(target, key, settings)` converts the key's rate and keeps the target's length
    (the key counts as silence after it ends; ffmpeg ends its output with the key).
- [x] **20. Video frames** — top-level `Video/`: planar YUV 4:2:0 8-bit `VideoFrame`, odd sizes
  allowed (chroma `(w+1)/2`), per-plane stride, pooled buffers, colour range (limited/full) and
  matrix (BT.601/BT.709) carried on the frame, `RgbaImage` for overlay sources only;
  `IVideoFrameTransform`, mirroring `Pcm/IPcmTransform`.
  - **Depends on**: nothing.
  - Status: done. `VideoFrame` (1..16384 per side, 64-byte-aligned strides, `PlaneY/U/V` and
    `GetRow`, `Range`, `Matrix`, `Timestamp`, `Duration`, `Fill`, `Clone`, `CopyTo`; one owner,
    `Dispose` returns the buffer and is idempotent) rented from `VideoFramePool` (exact-size buffers
    per frame size, at most `MaxRetainedPerSize` free ones kept, `RetainedBytes` observable,
    thread-safe; `VideoFrame.Create` uses `VideoFramePool.Shared`). An internal padded rent puts a
    32/16-pixel border around the planes for the H.264 decoder's reference pictures, with the
    visible planes starting inside it, so decoded frames need no copy. `YuvColor`/`RgbaColor`
    convert with the BT.601/BT.709 coefficients in limited or full range; an unspecified matrix
    is BT.601 below 720 lines and BT.709 from 720 up. `VideoFrameRate` is an exact fraction
    (frame times computed from the index with Int128). `RgbaImage` (straight alpha) and
    `AlphaMask` (coverage, for text) are the overlay sources. `IVideoFrameTransform` has
    `GetOutputSize` (so rotation can swap sides), `CanChangeFrameCount`, `Apply(frame, emit)`
    taking ownership of the frame, and a default `Flush`; `VideoFrameTransformPipeline` chains them
    like `PcmTransformPipeline`.
- [x] **21. Frame operations** — `Video/Filters/`: scale (bicubic, Lanczos; separable, SIMD), fit
  and pad to a canvas, crop, rotate/transpose by 90/180/270, frame-rate change by PTS
  (drop/duplicate), box blur, alpha-blended box, RGBA overlay, fade to/from a colour, crossfade —
  all in YUV. Tests: exact values on synthetic frames plus a PSNR floor against ffmpeg output
  (kernels and chroma siting differ).
  - **Depends on**: 20.
  - Status: done. `ScaleTransform` (bilinear, bicubic Keys a=-0.6 = ffmpeg's default, Lanczos 3;
    14-bit weights per input size, widened when shrinking; an optional source rectangle cropped
    inside the scaler; chroma left-sited horizontally and centred vertically; rows then columns,
    columns vectorised), `FitTransform` (contain + pad, or cover from the centred source rectangle,
    even sizes on even canvases), `CropTransform`/`PadTransform` (positions rounded down to even, as
    ffmpeg does on 4:2:0), `RotateTransform` (90/180/270 clockwise), `FrameRateTransform` (ffmpeg
    `fps` with `round=near`, exact slot times from `VideoFrameRate`), `BoxBlurTransform` (running
    sums, luma/chroma radius, passes, optional region), `FillBoxTransform`, `OverlayTransform` (RGBA
    converted once per colour space), `VideoFadeTransform`, `VideoDraw` (rectangles, coverage masks
    with one colour: what item 22 draws text with; chroma blended by the coverage of its luma
    pixels, so odd positions are exact) and `VideoBlend` (crossfade, opaque picture in picture).
    Shared: `VideoRectangle`; time windows are start-inclusive, end-exclusive; scale and blur take a
    `maxDegreeOfParallelism` (1 by default). Against ffmpeg (2021-12-20 build) on a 64x48 test
    picture checked in as raw YUV with ffmpeg's outputs: scale and cover at least 35 dB PSNR, boxblur
    at least 40 dB, transpose identical. One 1080x1920 frame on one core of an i7-8850H: cover from
    1920x1080 11.4 ms, radius-20 blur 7.2 ms, a 400x200 overlay 1.1 ms (`VideoFilterBenchmarks`).
- [x] **22. Text** — TrueType parser (`cmap` 4/12, `glyf` including composite glyphs such as stacked
  Vietnamese marks, `hmtx`, `kern` and basic GPOS pair kerning) and an anti-aliased scanline
  rasterizer; text normalized to NFC; the caller supplies the font bytes. Draws text with colour,
  outline, shadow, background box, wrapping and time windows. An OFL font is checked in for the
  tests (with its `THIRD-PARTY-NOTICES.md` entry); tests assert geometry and coverage, not PSNR
  against `drawtext`. CFF/OTF outlines and complex-script shaping are out of scope.
  - **Depends on**: 20.
  - Status: done. `Video/Text/`: `TrueTypeFont.Load` (bytes copied; `head`, `hhea`, `maxp`, `OS/2`
    win metrics, `hmtx`, `loca`, `glyf` simple and composite glyphs with offsets, scales, 2x2
    transforms, point matching and nesting up to 8 deep, `cmap` 4 and 12, kerning from `GPOS` pair
    adjustment formats 1 and 2 under the `kern` feature, extension lookups included, else a `kern`
    format 0 table; every offset bounds-checked, damaged fonts throw `InvalidDataException`, CFF and
    collections `NotSupportedException`). `TextRenderer` (NFC, '\n' and word wrapping, long words
    broken between text elements, kerning, quarter-pixel glyph positions, a 4096-glyph cache) and
    `RenderedText` (fill, outline = the fill dilated by a disc, shadow, background box on one canvas;
    `DrawOn`, `Place` with nine `TextAnchor`s) using item 21's `VideoDraw.BlendMask`;
    `TextOverlayTransform` (drawtext-like, time window, wraps to the frame width). The rasterizer
    flattens curves to 0.025 px and accumulates exact signed areas of line pieces split at pixel
    boundaries. Tests: metrics, glyph indices, advances, kerning and composite bounds against
    fontTools on a Noto Sans subset (OFL, test-only) and two fonts made for the tests (kern table;
    GPOS through extension lookups); exact coverage of squares and a curved glyph's analytic area
    within 1 %; NFC; layout, wrapping, alignment; styles; truncated and garbled fonts (3,000
    mutations) only ever throw `InvalidDataException`.
- [x] **23. Subtitle burn-in** — takes cues (start, end, text) directly, with styles in libass units
  (font size, vertical margin, outline and alignment in PlayResY=288 units scaled to the frame
  height). Output differs visibly from libass's own font fallback; that's intended.
  - **Depends on**: 22.
  - Status: done. `SubtitleBurnInTransform(TextRenderer, cues, SubtitleStyle?)` with `SubtitleCue`
    (start inclusive, end exclusive) and `SubtitleStyle` (font size, primary/outline/back colours,
    outline, shadow, margins L/R/V, numpad alignment, BorderStyle 1 or 3, PlayResX/Y; defaults are
    ffmpeg's SRT header: size 16, outline 1, shadow 0, margins 10, alignment 2, 384x288). Vertical
    sizes scale by frame height / PlayResY, horizontal margins by frame width / PlayResX; the font
    size is ascent + descent (OS/2 win metrics) as libass sizes fonts. `\N` breaks lines, `\h` is a
    non-breaking space, override tags are removed; BorderStyle 3 draws an opaque box in the outline
    colour with the outline as padding; overlapping cues stack away from their edge. Each cue is
    rendered once per frame size. Tests: placement for every alignment row and column, scaling with
    frame height, stacking, timing edges, BorderStyle 3, tags and line breaks, defaults, re-render on
    a size change.
- [x] **24. H.264 decode I** — `Codecs/H264/` (full SPS/PPS including VUI in new internal types; the
  public `H264SpsInfo` record is unchanged): I slices, CAVLC, intra 4x4/16x16/chroma prediction,
  I_PCM, dequantization and inverse transforms, deblocking, frame cropping. Refused with
  `NotSupportedException`: High 10 and above, 4:2:2/4:4:4, monochrome, interlaced (PAFF/MBAFF),
  FMO/ASO, SP/SI slices. Fixtures (`-g 1 --no-cabac`): multiple slices, deblocking idc 0/1/2 with
  offsets, constrained intra prediction, chroma QP offset; I_PCM hand-built; ground truth from
  `ffmpeg -f framemd5`.
  - **Depends on**: 20.
  - Status: done. `Codecs/H264/`: `H264Decoder` (public; avcC or Annex B access units in,
    cropped `VideoFrame`s out with the access unit's timestamp, `Width`/`Height`/`FrameRate`/
    `ColorRange`/`ColorMatrix` from the active SPS) on internal `H264BitReader` (64-bit window,
    Exp-Golomb, `more_rbsp_data`), `SequenceParameterSet`/`PictureParameterSet` (full syntax incl.
    VUI, scaling lists with both fall-back rules; refusals as listed), `SliceHeader` (every field,
    incl. reference list modification, prediction weights and reference marking for items 25-27),
    `CavlcTables`/`CavlcResidual` (code tables read from the Recommendation's text, prefix-free by
    test), `SliceDecoder` (I macroblocks: I_NxN 4x4, I_16x16, I_PCM; nC and intra mode prediction
    across macroblock and slice edges, constrained intra), `IntraPrediction`, `Dequantizer`/
    `InverseTransform`, `DeblockingFilter` (bS from intra, coefficients and motion; idc 0/1/2 and
    offsets) and picture assembly with a copy-out of the cropped area (the decoded pictures keep a
    32-pixel border for item 25). Bit-exact with ffmpeg on nine x264 streams (single and four
    slices, deblocking off, offsets and idc 2 (slice headers rewritten by a script), constrained
    intra, chroma QP offset, a cropped 66x50 picture, QP 4 and 48) and on hand-built I_PCM pictures;
    20,000 mutated streams only ever throw `InvalidDataException`/`NotSupportedException`.
- [ ] **25. H.264 decode II** — P slices, motion vector prediction, quarter-pel luma and
  eighth-pel chroma interpolation, multiple reference frames, weighted P prediction, the DPB's
  sliding window. MMCO, long-term references, reference list modification and POC type 1 are tested
  with hand-built bitstreams or JVT conformance vectors through a local-path test.
  - **Depends on**: 24.
  - Status: not started.
- [ ] **26. H.264 decode III** — CABAC. Fixtures: Main profile with `bframes=0`.
  - **Depends on**: 25.
  - Status: not started.
- [ ] **27. H.264 decode IV (High profile)** — B slices with spatial and temporal direct modes,
  weighted bi-prediction, 8x8 transform and intra 8x8, scaling matrices, output reordering.
  Synthetic fixtures only; real camera and AI-model clips through the local-path test.
  - **Depends on**: 26.
  - Status: not started.
- [ ] **28. H.264 encode I** — I and P frames, CAVLC, intra mode decision (SAD/SATD), hexagon search
  with quarter-pel refinement, deblocking, CRF-like rate control, a correct `level_idc`, VUI timing,
  SAR 1:1, SPS cropping for sizes that aren't multiples of 16. Tests: encoder reconstruction equals
  item 24-25's decoder output bit for bit; tiny outputs checked in with framemd5/ffprobe literals;
  a PSNR floor.
  - **Depends on**: 24, 25.
  - Status: not started.
- [ ] **29. H.264 encode II** — CABAC, 8x8 transform (High profile), row-wavefront threading.
  Targets, measured in `EggEncoder.Benchmarks` plus an environment-gated test: at least real time
  for 1080x1920 at 30 fps on 4 vCPU, and size within 15% of x264 `veryfast crf 20` at equal PSNR on
  a 10 s 1080p clip measured locally.
  - **Depends on**: 26, 27, 28.
  - Status: not started.
- [ ] **30. JPEG frame grab** — a baseline JPEG encoder (full range, quality mapped from ffmpeg's
  `-q:v` scale and stated), tested with a small decoder in the test project. A scene-change score
  is deferred: only film analysis uses it, and films need codecs outside this phase.
  - **Depends on**: 20.
  - Status: not started.
- [ ] **31. Composition API** — `Video/Composition/VideoComposition`: scenes (clip, seek/trim, fit,
  overlays, per-track volumes), concatenation, looped music with ducking, loudness normalization,
  end text and subtitles, written to MP4 frame by frame, cancellable, with progress. Inputs without
  a file extension are recognized by sniffing; the JPEG thumbnail comes from its own frames; the
  result carries has-audio, size, duration and integrated loudness. Targets: a 60 s 1080x1920
  render in at most twice real time on 4 vCPU, peak memory at most 1.5 GB.
  - **Depends on**: 2, 17, 18, 19, 21, 23, 27, 29, 30.
  - Status: not started.


### Phase 0 — Container infrastructure (pure C#, zero patent/licensing risk)

- [x] **1. MP4/MOV video sample demuxing** — extract each video sample's raw bytes, byte offset,
  and keyframe (sync-sample) flag from an existing MP4/MOV file, with no codec decode at all.
  - **Feasibility**: pure C#, small. `Mp4SampleTable` already resolves `stsz`/`stsc`/`stco` into
    `(offset, size)` pairs; this item adds reading the `stss` box (sync sample table — a simple
    fixed list of 1-indexed sample numbers; its *absence* means "every sample is a sync sample"
    per the ISO/IEC 14496-12 spec) and a new `MovVideoDemuxer` that finds the video (`vide`
    handler) track the same way `MovDecoder` finds the `soun` track today.
  - **Depends on**: nothing — builds directly on existing `MovAtomReader`/`Mp4SampleTable`.
  - **Test plan**: the existing `test.mov`/`test.mp4` fixtures already have verified ground truth
    (125 samples, exactly one keyframe at index 0, specific per-sample sizes/offsets — dumped via
    `ffprobe -show_entries frame=pkt_pos,pkt_size,key_frame`) — use them directly, no new fixture
    needed. Add hand-built malformed-box edge cases mirroring `CafReaderTest`/`TtaReaderTest`'s
    style (missing `stss` → every sample is a keyframe; empty video track; truncated tables).
  - **Not in scope for this item**: `edts`/`elst` (edit lists) and `ctts` (composition-time
    offsets) are not read — matches the existing audio demuxer's same limitation, documented
    there as "not handled." Fragmented MP4 (`moof`/`mvex`) also stays out of scope, same as audio.
  - **Status: DONE.** `MovVideoDemuxer.DemuxVideoTrack` + `Mp4SampleTable.ReadSyncSamples` +
    `MovAtomReader.FindTrackByHandlerType` (the latter extracted from, and now shared with,
    `MovDecoder`'s pre-existing audio-track lookup — a pure refactor, no behavior change).
    Verified against the real `test.mp4`/`test.mov` fixtures (matches `ffprobe` ground truth
    exactly: `avc1`, 125 samples, keyframe only at index 0, offset=48/size=6162 for sample 0,
    size=60 for the last sample). Full branch coverage including the `stss`-absent vs.
    `stss`-present-but-empty distinction, a truncated sync-sample table, missing video track,
    missing `moov`, and zero `stsd` entries. PR:
    [#25](https://github.com/eggspot/EggEncoder/pull/25) (merged, released as `v4.14.2`). A later
    self-review pass added the truncated-sync-sample-table test and fixed this status note's own
    then-missing PR link -- a reminder to actually fill this field in, not leave the placeholder
    the way the first draft of this entry did.

- [x] **2. MP4/MOV video muxing** — write a minimal but valid MP4/MOV file from a list of
  already-encoded video sample byte arrays + keyframe flags + width/height/codec fourCC.
  - **Widened for Phase 5 (2026-11-14)**: built as `Containers/Mp4/Mp4Writer : IMediaPacketSink`,
    mirroring `FlvWriter` (sync core with async wrappers, ConfigVersion and negative-DTS checks; a
    mid-stream ConfigVersion change is refused), with H.264, H.265 (passthrough) and AAC tracks
    validated through `FmpInitSegmentWriter.ToInitTrack`, the last-sample-duration rule and packet
    checks extracted from `FmpMediaSegmentWriter` rather than copied, `moov` before `mdat`, real
    durations (version 1 boxes past 32 bits), `ctts` version 0 or 1, one `elst` entry per track when
    the first PTS isn't 0 or the audio has priming, chunks of at most 0.5 s interleaved by DTS, the
    rotation matrix written back, files over 4 GiB refused, and a caller-supplied scratch stream
    (default: a delete-on-close temp file). Plus `Mp4Joiner` (stream copy across files with
    rebased timestamps, priming handled at the joins, codec configurations compared byte for byte)
    and `Mp4Splitter` (keyframe-aligned parts with timestamps reset). Tests round-trip through item
    15 and `MovProbe`, remux `test.mp4`, compare box order and sample tables with an ffmpeg
    `-c copy -movflags +faststart` fixture, and check in tiny outputs with ffprobe literals.
  - **Depends on (widened)**: 15.
  - **Feasibility**: pure C#, small-to-medium. `Mp4FileBuilder.CreateVideoOnly` (added for item 1,
    test-only, in `src/EggEncoder.UnitTests/TestUtilities/`) already builds exactly this box
    layout — `stsd`/`stts`/`stsc`/`stsz`/`stco`/`stss`/`vmhd`/`tkhd`/`hdlr` for a single video
    track — and is a direct template for this item's real production code
    (`Codecs/Mov/MovVideoMuxer.cs` or similar); don't re-derive the box layout from the ISO spec
    from scratch when a working, already-tested example exists. Needed before *any* encode item
    (item 3+) produces something end-to-end usable — an encoder that can only emit raw
    elementary-stream bytes with nowhere valid to put them isn't shippable on its own.
  - **Depends on**: 1 (shares box-reading knowledge/round-trip testing, though muxing itself is a
    write-only concern).
  - **Test plan**: round-trip test — mux a few arbitrary (non-decodable, doesn't matter) sample
    byte arrays with a known keyframe pattern, then demux with item 1's `MovVideoDemuxer`, assert
    the offsets/sizes/keyframe-flags/fourCC all come back exactly as given. Note: item 1's reader
    deliberately does *not* expose width/height (that stays `MovProbe`'s job, reading `tkhd`, to
    avoid two independent sources of truth for the same metadata) — check width/height round-trip
    via `MovProbe.Probe` instead, not by extending `MovVideoTrackInfo`. A real-codec cross-check
    (write real VP9/AV1/H.264 bytes and confirm `ffprobe` can open the result) becomes possible
    once a real codec exists (item 3+) — add that cross-check retroactively once one does, don't
    block this item on it.
  - Status: done (widened form). `Mp4Writer` writes `ftyp`, `moov`, `mdat` for H.264, H.265 and AAC
    with stts/ctts (v1 when negative)/stss/stsc/stsz/stco, an `elst` for a late start, B-frame delay
    or AAC priming, version 1 `mvhd`/`tkhd`/`mdhd`/`elst` past 32 bits, the rotation matrix, and
    chunks of at most 0.5 s interleaved by DTS; payloads wait in a scratch stream (default: a
    delete-on-close temp file) and the file is written in one forward pass. Packets carry no
    duration, so each track's last sample repeats the previous one (a remuxed AAC track can end a
    few milliseconds longer than its source). `writer_remux.mp4` is the checked-in remux of
    `h264_aac_rotated.mp4`: ffprobe reads the same packets and `ffmpeg -f framemd5` the same frames
    as from the source. `Mp4Joiner` stream-copies files with identical tracks, offsetting each by
    the previous one's longest track and dropping later inputs' AAC priming packets; `Mp4Splitter`
    cuts at video keyframes (audio-only: the first packet at or after the time) and restarts each
    part's timestamps at 0.

### Phase 1 — First real codec: decode

- [ ] **3. VP9 decode via `libvpx`** — the first "bytes in, pixels out" codec.
  - **Feasibility**: needs a native library — VP9 is far too complex for a from-scratch managed
    implementation to be realistic in a reasonable number of sessions (motion compensation,
    multiple reference frames, loop filtering, superblock partitioning). Wrap `libvpx`
    (BSD-3-Clause, Google/AOMedia's own reference implementation, decode+encode for both VP8 and
    VP9 in one library) via P/Invoke — same shape as `libmp3lame`/`libFLAC`.
  - **Why VP9 first, not AV1**: research for this backlog (verified via live web search, not
    memory — see citations in the licensing summary below) found AV1's patent-risk picture is
    *not* as clean as commonly assumed going into 2026: an active Dolby v. Snap lawsuit (filed
    March 2026) alleges AV1 uses Dolby-owned patents outside AOMedia's own cross-license pledge,
    and a Sisvel patent-pool royalty claim against AV1 has been an unresolved standoff since 2020.
    VP9 traces to the same Google royalty-free patent grant lineage as VP8, with no comparable
    *currently-litigated* claim found. VP9 is therefore the better-understood, lower-current-risk
    choice to implement first, even though AV1 is the more modern/efficient codec.
  - **Depends on**: 2 (needs somewhere to write decoded-and-then-re-encoded output for a full
    round trip to be testable/useful — though the decoder itself could land before the muxer if
    sessions prefer to split it that way; use judgment).
  - **Native library work**: new `Native/win-x64/libvpx.dll` (needs to be built/obtained — check
    for an official prebuilt or build from the BSD-licensed source; document exactly which build
    and flags in `THIRD-PARTY-NOTICES.md`), `Native/VpxNative.cs` P/Invoke declarations mirroring
    `FlacNative.cs`'s shape, `NativeLibraryLoader`'s `_managedLibraryNames` array extended.
  - **Test plan**: generate a tiny (a few frames, low resolution, e.g. 64x64) VP9-in-MP4 (or
    VP9-in-WebM once/if item 9 lands first — MP4 can carry VP9 via the `vp09` sample entry, so
    WebM isn't actually required for this item) test clip with `ffmpeg`, check it into the test
    fixtures directory, decode with the new code, and cross-check decoded pixel values/hashes
    against `ffmpeg`-dumped raw YUV ground truth for the same file — the video equivalent of
    `FlacFfmpegCrossCheckTest`.
  - Status: not started.

- [ ] **4. VP9 encode via `libvpx`**
  - **Depends on**: 3 (same library, decoder already wired), 2 (muxer, to produce a real playable
    file rather than a raw elementary stream).
  - **Test plan**: encode-then-decode-with-this-library's-own-item-3-decoder round trip (same
    shape as the audio codecs' own round-trip tests), plus a cross-check that `ffmpeg`/`ffprobe`
    can also open and decode the file this library produces (proves real-world interop, not just
    self-consistency — the same distinction the ALAC audio work drew between "round-trips with
    itself" and "genuinely spec-valid").
  - Status: not started.

- [ ] **5. VP8 decode + encode via `libvpx`**
  - **Feasibility**: same library as items 3/4, so the incremental native-binding cost is small —
    `libvpx` covers both codecs. VP8 is legacy relative to VP9 (lower real-world priority) but
    cheap to add once the VP9 binding exists.
  - **Licensing note, flag honestly**: Google's own patents carry an irrevocable royalty-free
    grant, but a historical Nokia IPR dispute (64 granted + 22 pending patents, refused
    royalty-free/FRAND commitment) was found in 2011-2013-era sources during this backlog's
    research — its *current* 2026 status could not be verified (no recent source found either
    way). Flag this for the owner before shipping VP8 specifically; don't assume it's dormant
    without checking again at implementation time.
  - **Depends on**: 3, 4.
  - Status: not started.

- [ ] **6. AV1 decode via `dav1d`**
  - **Feasibility**: `dav1d` (BSD-2-Clause, VideoLAN/AOMedia-funded, decode-only, built
    specifically to be fast/small/portable) is the right library if this is pursued — but see the
    litigation flag in item 3's rationale above. AOMedia members (which doesn't include Dolby)
    have a genuine mutual cross-license; the risk is specifically from Dolby's and Sisvel's
    patent claims *outside* that pledge. This is a materially different risk tier than
    H.264/HEVC's actively-and-uncontroversially-collecting pools (items 11-13) — AV1's claims are
    *disputed*, not an established mandatory-payment regime — but it is no longer accurate to call
    AV1 "obviously safe," and this item should be flagged to the owner before starting, same as
    (at a lower severity than) the gated codecs below.
  - **Depends on**: 2.
  - Status: not started.

- [ ] **7. AV1 encode via `SVT-AV1`**
  - **Feasibility**: SVT-AV1 (BSD-2-Clause for versions ≤0.8.7, BSD-3-Clause-Clear for ≥0.9, plus
    the AOMedia Patent License 1.0) is the right choice over the `libaom` reference encoder, which
    is also BSD-licensed but far slower in practice. Same litigation flag as item 6 applies.
  - **Depends on**: 6, 2.
  - Status: not started.

### Phase 2 — Pure-C# legacy codec (patent-clear, lower real-world priority)

- [ ] **8. MPEG-2 Video decode (pure C#)**
  - **Feasibility**: genuinely implementable in pure C# — MPEG-2's last US patent (#7,334,248)
    expired February 13, 2018, confirmed via multiple sources (now 8 years clear as of this
    writing). It's meaningfully smaller/simpler than H.264/VP9/AV1 (no in-loop deblocking filter,
    simpler motion compensation, well-documented via the public ISO/IEC 13818-2 spec and decades
    of tutorials/reference material) — a reasonable "second real codec" to attempt in pure managed
    code, sitting between "no codec" and a modern P/Invoke-wrapped one in implementation
    complexity. Real-world value is shrinking (legacy broadcast/DVD-era format) — hence lower
    priority than the royalty-free modern codecs above despite being patent-clear and
    implementation-friendly. Encode is a reasonable follow-on if decode lands cleanly, but is
    explicitly not bundled into this item.
  - **Depends on**: 1, 2 (container plumbing; MPEG-2 is typically carried in MPEG-TS/PS, which is
    its own small container work not otherwise on this list — scope that as part of this item or
    split it out when picked up, use judgment at the time).
  - Status: not started.

### Phase 3 — Additional containers (do after at least one native codec exists, so there's a real payload worth muxing)

- [ ] **9. Matroska/WebM demux (pure C#)**
  - **Feasibility**: pure C#, EBML parsing (a binary tree format, broadly analogous in spirit to
    the MP4 box structure already handled). Matroska is now an IETF standard (RFC 9559),
    explicitly royalty-free and patent-free as a container. WebM is a defined Matroska subset.
    Becomes meaningfully valuable once VP8/VP9/AV1 exist (WebM is their native/expected
    container) — hence placed after Phase 1, not before.
  - **Depends on**: ideally 3 or 6 (a codec worth muxing into WebM), though the demuxer itself has
    no hard technical dependency.
  - Status: not started.

- [ ] **10. Matroska/WebM mux (pure C#)**
  - **Depends on**: 9.
  - Status: not started.

- [ ] **11. AVI (RIFF) demux + mux (pure C#)**
  - **Feasibility**: pure C#, simpler container than MP4/MKV (flat RIFF chunk structure). Legacy
    format, low real-world priority — include for completeness/"match ffmpeg's coverage" but don't
    prioritize ahead of anything in Phase 0-2.
  - **Depends on**: nothing technical; sequenced last because of low value, not difficulty.
  - Status: not started.

### Phase 4 — Patent-encumbered codecs: EXPLICITLY GATED

**Do not start any item in this phase without the repo owner's explicit, direct sign-off on the
specific item**, obtained fresh at the time (don't rely on an old approval from this document's
original authoring session — licensing positions and the owner's risk tolerance can both change).
These are listed for completeness against the "match ffmpeg's coverage" ambition, not as
work to default into.

**Sign-off recorded, 2026-11-14 (H.264 only):** the repo owner approved H.264 decode and encode
(items 12 and 13), written in-house in pure C#, after checking the patent question with a lawyer.
They're built as Phase 5 items 24-29, not through `openh264`: Cisco's royalty coverage applies only
to its own binaries downloaded separately by the end user, and only to uses that receive no
remuneration, so bundling its binary would not have inherited that coverage for a commercial user
anyway (checked against openh264.org's `BINARY_LICENSE.txt` and FAQ that day). HEVC (item 14)
stays gated; writing already-encoded H.265 samples into a container (backlog item 2) is
passthrough, not codec work, and stays allowed. An in-house implementation isn't covered by
Cisco's grant; it rests on the owner's own legal advice.

- [ ] **12. H.264/AVC decode via `openh264`** — was gated on owner sign-off; approved 2026-11-14
  for an in-house build (see the sign-off above), so `openh264` itself isn't used.
  - **Licensing, the critical nuance**: the H.264 patent pool (MPEG LA, consolidated into Via
    Licensing Alliance in 2023) is actively, commercially enforced — this is not a dormant or
    symbolic royalty regime. Cisco's `openh264` is BSD-licensed *source*, and Cisco has paid the
    pool's royalties on behalf of users of Cisco's own **precompiled binaries** specifically — a
    source found during this backlog's research states plainly that "any software projects that
    use Cisco's source code instead of its binaries would be legally responsible for paying all
    royalties to MPEG LA themselves." **This means the implementation must bundle Cisco's actual
    prebuilt `openh264` DLL** (matching this repo's existing "bundle a prebuilt, unmodified binary"
    pattern for `libmp3lame`/`libFLAC` exactly) rather than compile from source, to have any chance
    of inheriting that royalty coverage — and this must be re-verified against Cisco's current
    terms at implementation time, not assumed to still hold from this document's authoring date.
  - **Depends on**: 2 (and 1, for round-trip testing).
  - Status: approved 2026-11-14 (see the sign-off above); built in pure C# as Phase 5 items 24-27
    instead of through `openh264`.

- [ ] **13. H.264/AVC encode via `openh264`** — was gated like item 12; approved the same day.
  Do not use `x264` for this even if sign-off is given — `x264` is GPL-licensed, which is
  copyleft and incompatible with bundling into this MIT-licensed project's binary distribution
  the way every other native dependency here is bundled.
  - **Depends on**: 12.
  - Status: approved 2026-11-14 (see the sign-off above); built in pure C# as Phase 5 items 28-29
    instead of through `openh264`.

- [ ] **14. H.265/HEVC decode via `libde265`** — **GATED, needs explicit owner sign-off, and is
  the lowest-priority item in this entire backlog.**
  - **Licensing**: `libde265` itself is LGPL-3.0, which is an acceptable *license category* under
    this repo's existing precedent (the same dynamically-loaded/unmodified/relinkable shape
    already accepted for `libmp3lame`'s LGPL-2.1) — the license is not the blocker. The patent
    situation is: HEVC's licensing landscape consolidated *further* in December 2025 (Access
    Advance acquired Via LA's HEVC/VVC pools; Velos Media dissolved in 2023 and returned its
    patents to their original owners), is now administered as roughly 29,000 patents under one
    pool, and is demonstrably still actively enforced (28 new companies signed licenses in just
    the first half of 2026). This is a more concentrated but no less commercially active
    royalty regime than H.264's. Given the real-world value of HEVC support against this level of
    licensing complexity/cost, **seriously consider not pursuing this item at all** unless there's
    a specific, stated business requirement that justifies it — flag this recommendation to the
    owner explicitly rather than defaulting to "eventually get to it."
  - **Depends on**: 2 (and 1).
  - Status: not started.

### Explicitly out of scope / low-priority-forever (stated plainly, not silently dropped)

- **HEVC encode** — not listed as its own item above; if item 14 (decode) is ever greenlit and
  shipped, revisit whether encode is separately justified, but don't assume it follows
  automatically given the licensing cost already incurred for decode alone.
- **Theora decode/encode** — genuinely the lowest-risk codec found in this research (On2 gave an
  irrevocable "to all of mankind" royalty-free patent grant when donating VP3's lineage to Xiph;
  BSD-style license either for a pure-C# attempt or via `libtheora`) but real-world demand for
  Theora today is minimal (it lost the royalty-free-codec race to VP8/VP9/AV1 over a decade ago).
  Worth picking up opportunistically if ever a session has nothing higher-value to do and wants a
  very low-risk codec exercise, but not placed on the numbered roadmap above — don't let it block
  or displace anything in Phases 0-3.
- **MPEG-4 Part 2** (the old DivX/Xvid-era codec) — patent-clear as of July 19, 2026 (the last
  worldwide patent, a Brazilian filing, expired that date), but real-world value is now almost
  entirely legacy-library playback. Same treatment as Theora: clear to pick up opportunistically,
  not on the numbered roadmap.
- **VC-1** — mostly patent-expired per current sources but an exact full-expiration date couldn't
  be confirmed during this backlog's research, and real-world relevance (old WMV content, some
  Blu-ray) is niche enough that it isn't worth the remaining verification effort right now. Revisit
  only if a concrete need surfaces.
- **DRM-wrapped formats of any kind** — categorically out of scope; this is a format-conversion
  toolkit, not a DRM-circumvention tool, regardless of the underlying codec's own licensing status.
- **Any codec with no permissively-licensed implementation available at all** — none were
  identified as flatly infeasible during this research (every codec investigated has at least one
  BSD/LGPL-class implementation); if a future item turns out to only have GPL-or-worse options
  with no alternative, treat it the same way `x264` was treated for item 13: out of scope for
  bundling, full stop, regardless of how else it might be classified above.

## Licensing research summary (for reference — see each item above for how this maps to priority)

Verified via live web research at the time this backlog was authored (16 tool calls: 15
WebSearch + 1 WebFetch, all cited); re-verify anything load-bearing before acting on it if much
time has passed, per the lesson stated earlier in this document.

| Format | Verdict | Key fact |
|---|---|---|
| MP4/MOV, Matroska/WebM, Ogg | Clear | Public specs, no patent concerns on the container itself (Matroska is IETF RFC 9559) |
| AVI (RIFF) | Clear, legacy | OpenDML extension spec public since 1996; "proprietary" label is about origin, not an active licensing regime |
| MPEG-2 Video | Clear | Last US patent (#7,334,248) expired Feb 13, 2018 |
| MPEG-4 Part 2 | Clear | Last worldwide patent expired July 19, 2026 |
| Theora | Clear | On2's irrevocable royalty-free grant to Xiph on donation |
| VP8 | Mostly clear | Google royalty-free grant; historical Nokia IPR dispute's current status unverified |
| VP9 | Mostly clear | Same Google grant lineage as VP8; no current litigated claim found |
| AV1 | Clear-but-contested | AOMedia cross-license holds, but Dolby sued Snap (Mar 2026) and Sisvel has an unresolved royalty claim since 2020 — both outside the AOMedia pledge |
| H.264/AVC | Actively enforced | Via Licensing Alliance (successor to MPEG LA since 2023); patents run to ~2027 |
| H.265/HEVC | Actively enforced, worse | Access Advance consolidated the HEVC/VVC pools further in Dec 2025 (~29,000 patents); still signing new licensees in 2026 |
| VC-1 | Mostly expired, unconfirmed exact date | Niche/legacy; not worth further verification right now |

| Library | License | Role |
|---|---|---|
| `dav1d` | BSD-2-Clause | AV1 decode |
| `SVT-AV1` | BSD-2/3-Clause + AOMedia Patent License 1.0 | AV1 encode (preferred over `libaom`'s reference encoder for speed) |
| `libvpx` | BSD-3-Clause | VP8 + VP9 decode and encode (one library, both codecs) |
| `openh264` | BSD (source), but royalty coverage is binary-only | Not used: Cisco's coverage needs its binary downloaded separately by the end user and excludes uses that receive remuneration (checked 2026-11-14); H.264 is built in pure C# instead (Phase 5) |
| `libde265` | LGPL-3.0 | HEVC decode — license itself is fine under this repo's existing LGPL precedent; the patent pool is the real gate |
| `libtheora` | BSD-style (Xiph) | Theora decode/encode |
