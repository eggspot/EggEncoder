# Feature Roadmap

A ranked survey of candidate features beyond the managed-codec rewrite
(`docs/managed-codec-rewrite-plan.md`) and video support (`docs/video-support-backlog.md`), which
already have their own dedicated plans. This file is for everything else: new capabilities on top
of the now-complete, 100% pure-managed, cross-platform audio toolkit.

Unlike the other two planning docs, nothing here is committed to yet — this is a candidate list
for the owner to prioritize from, not a checklist in progress. Treat an item's own section as a
starting sketch, not a locked design; revise it once real implementation experience exists.

## How to use this doc

1. Read the repo's own `CLAUDE.md` for house conventions first (feature branch + PR, conventional
   commits, `xUnit`/`FluentAssertions`/AAA, full branch coverage, docs-sync in the same PR).
2. Pick an item by rank unless the owner says otherwise — the ranking already weighs impact
   against effort and risk.
3. An item's own section here is a *design sketch*, not a spec — validate it against the current
   codebase (things move between sessions) before implementing, and update this file with what
   you actually built, the same way `managed-codec-rewrite-plan.md`'s own status notes do.
4. If an item turns out to need its own multi-session plan (the way the codec rewrite and video
   support did), split it into its own dedicated doc and link it from here instead of growing
   this file indefinitely.

## Ranked candidates

| # | Feature | Impact | Effort | Depends on |
|---|---------|--------|--------|------------|
| 1 | [Stream-based `IMediaEncoder` overloads](#1-stream-based-api-design-sketch) | High | Medium | none |
| 2 | [Format-native metadata tags (ID3v2 for MP3, Vorbis comments for FLAC/Opus/Vorbis)](#2-format-native-metadata-tags) | High | Medium | none |
| 3 | [True loudness normalization (EBU R128 / ITU-R BS.1770)](#3-loudness-normalization-ebu-r128) — done | Medium | Medium | none |
| 4 | [Explicit encoder delay/padding metadata (gapless playback)](#4-encoder-delaypadding-metadata) — done for MP3, Vorbis still open | Medium | Low–Medium | none |
| 5 | [`CancellationToken` support across `Probe`/`ConvertFile`/`CutFile`](#5-cancellationtoken-support) — done | Medium | Low | #1 (same call sites) |
| 6 | [Multi-channel (&gt;2) support for the lossy codecs](#6-multi-channel-2-support) | Low–Medium | High | none |
| 7 | [Cue points / chapter markers](#7-cue-points--chapter-markers) | Low | Medium | none |

Ranking rationale: #1 and #2 are the two most-requested-shape gaps for a library already this
complete (every real-world audio pipeline eventually needs "don't require a local file path" and
"round-trip the metadata my source files already have"); #3 and #4 are smaller, well-scoped
quality-of-life wins; #5 piggybacks on #1's own call-site changes so it's cheap once that lands;
#6 and #7 are real but lower-demand for this project's own stated use case (high-throughput music
distribution pipelines, which are overwhelmingly mono/stereo and rarely need embedded chapter
markers).

---

## 1. Stream-based API (design sketch)

**Problem**: every `IMediaEncoder` method (`Probe`, `ConvertFile`, `CutFile`) and every codec's
own `Open`/`Create`/`OpenSession` entry point takes a file path (`string`) and opens its own
`FileStream` internally. A caller whose source or destination isn't a local file — an
`HttpResponseMessage` body, a blob storage `Stream`, an in-memory `MemoryStream` for a test or a
web upload — has to buffer to a temp file first, which this library's own "no subprocess, no
external process" pitch doesn't really deliver on for that case.

**Design**:

- Add a `Stream`-based overload set alongside the existing path-based ones, not a replacement —
  every existing path-based call forwards to the new overload internally (`ConvertFile(string
  src, string dest) => ConvertFile(File.OpenRead(src), DetectFormat(src), File.Create(dest),
  DetectFormat(dest))`), so no public signature changes and no behavior change for existing
  callers.
- The one real complication: format is currently inferred from the file *extension*
  (`NativeEncoder`'s own dispatch-by-extension design, `AudioCutter.cs`'s own role). A `Stream`
  has no extension, so every new overload needs an explicit format parameter instead — a new
  `AudioFormat` enum (`Wav`, `Aiff`, `Au`, `Alac`, `Flac`, `Mp3`, `Tta`, `WavPack`, `Opus`,
  `Vorbis`, `Aac`, `Wma`, mirroring the extension-dispatch switch in `AudioCutter.cs` 1:1) rather
  than re-deriving it from magic bytes — sniffing is strictly worse here (slower, and some
  formats, e.g. raw `.aac` ADTS, don't have a clean magic-byte signature to sniff).
- Seekability is the real constraint underneath the API surface, not an afterthought: `WavWriter`
  needs an exact `totalFrames`/pre-known size up front (or `AudioCutter.Pipeline.cs`'s own
  `DeferredWavSink` buffering trick) because it writes a fixed header with no patch-up; `FlacEncoder`
  patches STREAMINFO in place at `Finish()` by seeking back to a fixed byte offset. Both already
  work against a `FileStream` *because* `FileStream` is seekable — the new overloads should accept
  any `Stream` for *reading* **in principle**, but this needs checking per format, not assumed
  uniformly: `WavReader`'s own chunk-walk parses by alternating `Stream.Position`/`Seek` calls
  throughout (confirmed while implementing item 1's own first slice — see Status below), so its
  own `Open(Stream)` requires `CanSeek` same as a destination would, while `FlacDecoder` genuinely
  needs no seek at all (it reads its whole source into memory up front before parsing anything).
  Require `CanSeek` for any *destination* that `WavWriter`/`FlacEncoder` backs, and throw a clear `ArgumentException` up
  front rather than failing confusingly mid-write against a non-seekable destination (e.g. an
  HTTP response body stream). Every other destination codec here already streams forward-only
  with no seek-back, so this restriction is narrower than "every destination must be seekable."
- `Probe` needs no destination at all, so it's the simplest new overload:
  `Task<ProbeResult> Probe(Stream source, AudioFormat format)`.
- None of this touches the PCM transform pipeline (`AudioCutter.Pipeline.cs`) — it already
  operates on decoded `int[]` blocks, is source/destination-agnostic internally, and just needs
  its own `IAudioSink`/source plumbing updated to accept the new `Stream`-based entry points the
  same way it already accepts path-based ones.
- Ownership: path-based overloads already open and own their own `FileStream`, disposing it when
  done. `Stream`-based overloads should **not** dispose the caller's stream (the caller opened it,
  the caller closes it) — the opposite default from the path-based ones, and worth calling out
  explicitly in the XML doc for each new overload so it isn't assumed either way.

**Out of scope for a first version**: true async I/O (`Stream.ReadAsync`/`WriteAsync` plumbed all
the way through every codec's own decode/encode loop) — every codec here is written against
synchronous `Stream`/`BinaryReader` calls throughout, and threading real async through all of them
is a much larger, separate effort than just accepting a `Stream` instead of a path. A first version
can expose `Task`-returning signatures (matching `IMediaEncoder`'s own existing async shape) that
run synchronously against the given stream under the hood, same as how `Probe`/`ConvertFile`
already work today — genuine async I/O is its own, later roadmap item if real usage shows it's
needed.

**Status: twelve formats done** (`IMediaEncoder.Probe(Stream, AudioFormat)`) — the `AudioFormat`
enum, the interface method, and real `Stream` support across all four adaptation shapes this
item's own design (and its own prior status notes) called out:
`WavReader.Open(Stream)`/`AiffReader.Open(Stream)`/`AuReader.Open(Stream)`/`CafReader.Open(Stream)`
(the `FileStream`-field-widened-to-`Stream` shape, each requiring `CanSeek` since their own header
parse relies on `Stream.Position`/`Seek` throughout -- confirmed per format, not assumed
uniformly, the correction the original design sketch above already flagged);
`FlacDecoder.Decode(Stream, ...)`/`AacDecoder.Decode(Stream, ...)`/`WavPackDecoder.Decode(Stream, ...)`/
`OpusDecoder.Decode(Stream, ...)`/`WmaDecoder.Decode(Stream, ...)` (the read-fully-into-`byte[]`
shape, confirmed to need no seek at all -- `WmaDecoder`'s own `AsfContainerReader` already parsed
from a plain `byte[]` with no `Stream`/`Seek` involved at all even before this overload existed,
so its own `Read(string, ...)` just gained a sibling `Read(byte[], ...)` it now delegates to, and
`OpusDecoder`'s own `OggPageReader` likewise has zero `Seek`/`Position` calls of its own -- the
buffering is needed only because `ReadOpusHead`/`CountTotalSamples` each need their own
independent pass from the start, not because any single pass needs to seek);
`TtaReader.Open(Stream)`/`TtaDecoder.Decode(Stream, ...)` (read strictly *sequentially*, with zero
`Seek`/`Position` calls anywhere in the reader -- so it needs no seek support either, without even
needing the read-fully-into-memory buffering the previous shape uses to get there); and
`VorbisDecoder.Decode(Stream, ...)` (the one format that *does* require `CanSeek` -- confirmed
directly, not assumed, by feeding NVorbis's own `VorbisReader` a non-seekable stream and observing
it throw `NotSupportedException` from its own internal seek call -- but needed no "widen"/"read
fully into memory" adaptation of its own at all, since `VorbisReader`'s own constructor already
supports not disposing the caller's stream directly via `closeOnDispose: false`, a first-class
feature of that dependency this project didn't have to build itself); and `MovProbe.Probe(Stream)`/
`MovDecoder.Decode(Stream, ...)` (the same `CanSeek`-requiring `FileStream`-widened-to-`Stream`
treatment WAV/AIFF/AU/CAF already got -- `MovAtomReader`'s own atom-tree walk already used
`stream.Position` assignments extensively for random access even before this change, so both
widened mechanically once checked; `NativeEncoder.Stream.cs`'s own `ProbeMovStream` composes them
the same way the path-based `ProbeVideo` already does, including the embedded-audio-track
graceful-degradation behavior -- the one genuine difference from the path-based overload is that a
`Stream` source has no file extension to distinguish a MOV container from an MP4 one, so this
always reports `FormatName`/`FormatLongName` as `"mov"`/`"QuickTime / MOV"`, the same default the
path-based overload itself already falls back to for any extension other than `.mp4`).

MP3 is what's left for `Probe`. It's still specifically blocked on NLayer's own
`MpegFile(Stream)` constructor having no `leaveOpen` option, so it can't honor this type's own
"never dispose the caller's stream" contract without further work (e.g. wrapping the stream in a
disposal-suppressing adapter, or an upstream NLayer change) -- not attempted here, since that's a
real design decision for the owner, not a mechanical port like every other format this item has
confirmed.

`ConvertFile`/`CutFile`'s own `Stream` overloads (the destination-seekability half) are now wired
up: `IMediaEncoder.ConvertFile(Stream, AudioFormat, Stream, AudioFormat)`/
`CutFile(Stream, AudioFormat, int, int, Stream, AudioFormat)` (plus their own `CancellationToken`
overloads), backed by `AudioCutter.Stream.cs`'s own `Convert`/`Cut` -- the `AudioFormat`-keyed
counterpart to `AudioCutter.cs`'s own extension-keyed `DecodeSource`/`OpenSink`
(`DecodeSourceStream`/`OpenSinkStream`), reusing `CutOptions`/`BuildCutPipeline`/`ForwardOverlap`/
`GetSampleRange`/`ScratchBuffer`/`DeferredFixedHeaderSink` directly from `AudioCutter.Pipeline.cs`
rather than duplicating any of that logic -- including the `PcmTransformPipeline` parameter, so a
`ResamplingTransform`/`VolumeTransform`/etc. in the pipeline works for a `Stream` destination
exactly the same way it already does for a path-based one (verified directly: a `Stream`-to-`Stream`
`Convert` with a frame-count-changing `ResamplingTransform` correctly goes through
`DeferredFixedHeaderSink`'s own buffer-then-patch path, the same as the path-based WAV case).
`DecodeSourceStream` covers every source format `Probe(Stream, AudioFormat)` already does (every
format except MP3, for the same NLayer `MpegFile(Stream)`-has-no-`leaveOpen` reason). `OpenSinkStream`
now covers WAV/AIFF/AU, via `WavWriter.Create(Stream, ...)`/`AiffWriter.Create(Stream, ...)`/
`AuWriter.Create(Stream, ...)` -- all three happen to need no `Stream.CanSeek` at all, since each
one's header is a fixed size with every field already known from `totalFrames` up front -- plus
FLAC, via `FlacEncoder.OpenSession(Stream, ...)`, the first destination here that genuinely does
require `CanSeek`: `FlacEncoderSession.Finish()` still seeks back to a fixed byte offset to patch
STREAMINFO's provisional totalSamples/MD5/block-size-range fields once the real values are known,
exactly the way the path-based overload's own `FileStream` destination already does, so the new
overload validates `destStream.CanSeek` up front and throws a clear `ArgumentException` (not a
confusing mid-`Finish()` failure) if it isn't. `FlacEncoderSession` itself was widened from a
`FileStream` field to a `Stream` one plus a `leaveOpen` flag (mirroring `WavWriter`'s own
`_leaveOpen` convention from this same item's earlier work) so `Dispose()` only closes a stream
this type actually owns -- the path-based overload still owns (and disposes) the `FileStream` it
opens; the Stream-based one does not dispose the caller's stream. `FlacEncoder`'s own channel/
bits-per-sample/tag validation was factored into a shared private helper so the path-based and
Stream-based `OpenSession` overloads can't drift apart on what's valid. `AiffWriter`/`AuWriter`
were widened mechanically, the same validate-then-write refactor `WavWriter` already went through
-- neither needed any seek logic touched since they had none to begin with. Every other
destination codec's own `Stream` write side (and its own seekability requirement, to be confirmed
per format rather than assumed) is what's still open for this item, alongside MP3's own blocker
above.

## 2. Format-native metadata tags

**Status: done**, all four sub-items. WAV `'LIST'`/`'INFO'` tags are read *and* written
(`WavReader.Tags`/`ProbeResult.Tags`, `WavWriter.Create`'s own `tags` parameter, PR #85). ID3v2 for
MP3 is read *and* written (`Mp3Probe`/`Mp3Encoder`, PR #89, text information frames only). Vorbis
comments for FLAC/Opus/Vorbis are read *and* written (`Codecs.VorbisCommentReader`/
`VorbisCommentWriter`, shared by FLAC's own `VORBIS_COMMENT` metadata block and Opus's `OpusTags`
packet; Vorbis itself delegates to NVorbis/OggVorbisEncoder's own container handling instead, PRs
#90/#91). AIFF's own `'LIST'`-equivalent is read *and* written (`AiffReader.Tags`/
`AiffWriter.Create`'s own `tags` parameter): read supports both the simple `'NAME'`/`'AUTH'`/
`'(c) '` text chunks and an embedded `'ID3 '` chunk (reusing `Codecs.Mp3.Id3v2Reader`, since some
real-world AIFF files carry one), while write only ever emits the three simple text chunks —
AIFF's own native convention, not the embedded-ID3v2 one, the same "write one native mechanism
per format, read tolerates more" asymmetry WAV's own ADPCM variants already established.

## 3. Loudness normalization (EBU R128)

**Status: done -- both measurement and normalization.**
`WaveformCalculator` gained an optional `sampleRate` constructor parameter that, when given, also
measures real ITU-R BS.1770-4 / EBU R128 integrated loudness (in LUFS) alongside the existing
peak/RMS tracking, via `GetIntegratedLoudness()` -- surfaced as `ProbeResult.IntegratedLoudness`
(nullable `double`; `null` if loudness wasn't requested or the audio is shorter than one 400ms
gating block; can legitimately be `double.NegativeInfinity` for audio entirely below the
standard's own -70 LUFS absolute silence gate, which is a real measurement, not a "couldn't
measure" sentinel). Every `Probe`/`Probe(Stream, AudioFormat)` entry point in `NativeEncoder.cs`/
`NativeEncoder.Stream.cs` now populates it.

Correction to the original design sketch above: the K-weighting pre-filter does **not** reuse
`Pcm.BiquadTransform` directly after all -- that type's RBJ-cookbook coefficient formula and the
standard's own tan-prewarped bilinear-transform formula are two different (if related)
parameterizations, and only the latter is verified to reproduce the standard's published 48kHz
coefficient table. `Waveform.KWeightingFilter` implements the standard's own formula instead (as
two cascaded Direct-Form-II-Transposed biquads, the same structural pattern `BiquadTransform`
uses, just with different coefficients) -- see that type's own doc comment for exactly which
public source (the reference `libebur128` implementation) the constants were verified against,
not assumed from memory. Confirmed end-to-end against the standard's own published conformance
test signal (a 997Hz sine wave at -23dBFS peak must measure -23.0 LUFS) before this was considered
done, not just from the filter coefficients matching in isolation.

**Status: done -- normalization half also landed.** `Pcm.LoudnessTransform` (alongside the
existing `PeakNormalizationTransform`, which normalizes to a *peak* dBFS target instead -- the two
serve genuinely different goals, matching real-world mastering/distribution tooling's own
distinction between peak and loudness normalization) adjusts gain to hit a target LUFS. It's a
two-pass transform exactly as sketched above, but with one deliberate difference from
`PeakNormalizationTransform`'s own shape: `Apply` has **no** single-pass self-measuring fallback
at all. `PeakNormalizationTransform` can reasonably self-measure from whatever one decode block
`Apply` is first given (an incomplete but still meaningful peak estimate); EBU R128's own gating
algorithm cannot produce a meaningful number from one ~4096-frame block the same way, so
`LoudnessTransform.MeasureLoudness` must be called first with a true whole-file measurement
(`AudioCutter.MeasureIntegratedLoudness`, the loudness counterpart to this file's own existing
`MeasurePeakAmplitude`) or `Apply` throws `InvalidOperationException` rather than silently
normalizing against a wrong, block-local value. `null` or `double.NegativeInfinity` (EBU R128's
own "measured, but entirely below the absolute silence gate" sentinel) both pass samples through
unchanged, since no finite gain reaches a finite target from either starting point.

## 4. Encoder delay/padding metadata

**Status: done for MP3.** Opus already has an explicit `pre_skip` field; Vorbis still has no
equivalent (a caller has to find the true sample-accurate alignment empirically via
cross-correlation, the same way `VorbisEncoderSessionTest`'s own round-trip SNR helper already
does -- that gap is unchanged and still open). MP3 now writes and reads the MP3 ecosystem's own
de facto standard: an Xing-style "Info" header frame carrying a LAME-style encoder-delay/padding
extension. `Mp3Encoder.EncoderDelaySamples` (a fixed `1680`) and a per-file padding value
`Mp3EncoderSession.Finish()` computes are written into every file this encoder produces;
`Mp3Probe`/`ProbeResult` surface them back as `EncoderDelaySamples`/`EncoderPaddingSamples`
(parsed from any LAME-tagged file, not just this project's own output) and `DurationSeconds`/
`DurationInSeconds` already subtract them for a more accurate reported duration.

The exact byte layout was confirmed directly against FFmpeg's own real
`libavformat/mp3dec.c` source (with the owner's explicit sign-off to study it at arm's length,
ideas/structure only, no code copied -- see `THIRD-PARTY-NOTICES.md`'s own clean-room entry for
this feature), not assumed from memory; the `1680` constant and the padding formula were verified
empirically (normalized cross-correlation against known test signals, cross-checked against both
a real `ffmpeg` decode and a real `ffmpeg`+`libmp3lame`-produced reference file's own raw tag
bytes) rather than derived from the byte layout alone -- a first attempt at the constant (528,
treating only this encoder's own granule-history warm-up and forgetting the dedicated header
frame's own 1152-sample contribution) was caught exactly this way, by the empirical check
disagreeing with the arithmetic, before it shipped.

This work also surfaced and fixed a real, independently-found bug, not something anticipated
going in: `Mp3EncoderSession.Finish()` previously only silence-padded the final partial frame up
to a whole 1152 samples, with no additional flush frame -- but this encoder's own one-granule
MDCT history needs one more full frame of "future" context before the true tail of real audio
becomes recoverable from the bitstream at all, so the last ~1 frame's worth of genuinely real
input samples were silently and permanently lost on every encode, independent of this metadata
feature. `Finish()` now always appends one more full frame of silence to flush that tail
correctly; `Mp3EncoderSnrTest`'s new `Encode_Should_PreserveTheExactTailOfRealAudio_...` test
checks this directly (the bug doesn't reliably show up in a whole-signal SNR or duration check
alone).

One real, deliberately accepted limitation: this encoder's own "EggEncdr" version string (not
"LAME"/"Lavf"/"Lavc") means FFmpeg's own demuxer -- whose delay/padding parsing gates specifically
on those three exact strings -- won't itself auto-trim a file this encoder writes, since writing
one of those three strings would falsely claim to be that tool. This project's own `Mp3Probe`
reads the fields regardless of version string for exactly that reason. Also not computed: the
LAME tag's own Music/Info Tag CRC-16 fields (written as `0x0000`, honestly documented rather than
guessed) -- confirming libmp3lame's own exact checksum convention would have needed reading LAME's
own source, outside this change's sign-off (FFmpeg's own source only), and FFmpeg's own decode-side
code never gates the delay/padding fields on either CRC anyway.

## 5. `CancellationToken` support — done

`IMediaEncoder.ConvertFile`/`CutFile`/`Probe` (and the `Stream`-based `Probe` overloads) each
gained a `CancellationToken`-accepting overload, added as new default-interface-method overloads
rather than new parameters on the existing methods (binary-compatible: no existing caller's
compiled code breaks). `ConvertFile`/`CutFile` check the token once per decoded block, inside the
single shared `AudioCutter.DecodeSource` callback that every codec's own decode loop already
funnels through (`AudioCutter.cs`/`AudioCutter.Pipeline.cs`) — this gives real, sub-second
cancellation granularity for every format without touching each codec's own ~24+ individual decode
call sites. `Probe`'s own `CancellationToken` support is deliberately shallow by contrast — checked
once up front, before any decode starts, not per-block — because `Probe`'s internal structure is
fragmented per-format (20+ separate methods, each with its own `waveformCalculator` callback) with
no single shared callback funnel the way `Convert`/`Cut` have; this is a stated scope boundary, not
a silent gap. A cancelled call throws `OperationCanceledException` and produces no destination
file. `NativeEncoder`'s own catch blocks log `OperationCanceledException` as informational (an
expected outcome), not as an error, before rethrowing.

## 6. Multi-channel (>2) support

Every lossy codec here (MP3, Opus, Vorbis, AAC, WMA) and several lossless ones (ALAC, TTA,
WavPack) are scoped to mono/stereo only, by explicit design — matching this project's own stated
"high-throughput music distribution pipelines" use case, which is overwhelmingly stereo. Real
demand would need to come from an actual surround-sound use case before this is worth the real
effort it'd take (channel layout/mapping conventions differ per format, and several of the
delegated-to NuGet dependencies, e.g. Concentus for Opus, may have their own multi-channel
limitations independent of anything this project controls).

## 7. Cue points / chapter markers

WAV's own `'LIST'`/`'adtl'` sub-type (cue-point labels/notes, as opposed to `'INFO'`'s metadata
tags) is explicitly out of scope today (`WavReader.TryParseListInfoTags`'s own doc comment calls
this out directly). Real demand for this is low for the stated use case (chapter markers matter
far more for audiobooks/podcasts than for music distribution), so this stays last in the ranking
unless the owner's own use case changes.
