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
| 3 | [True loudness normalization (EBU R128 / ITU-R BS.1770)](#3-loudness-normalization-ebu-r128) | Medium | Medium | none |
| 4 | [Explicit encoder delay/padding metadata (gapless playback)](#4-encoder-delaypadding-metadata) | Medium | Low–Medium | none |
| 5 | [`CancellationToken` support across `Probe`/`ConvertFile`/`CutFile`](#5-cancellationtoken-support) | Medium | Low | #1 (same call sites) |
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

**Status: six formats done** (`IMediaEncoder.Probe(Stream, AudioFormat)`) — the `AudioFormat`
enum, the interface method, and real `Stream` support for both adaptation shapes this item's own
design called out: `WavReader.Open(Stream)`/`AiffReader.Open(Stream)`/`AuReader.Open(Stream)` (the
`FileStream`-field-widened-to-`Stream` shape, each requiring `CanSeek` since their own header
parse relies on `Stream.Position`/`Seek` throughout -- confirmed per format, not assumed
uniformly, the correction the original design sketch above already flagged) and
`FlacDecoder.Decode(Stream, ...)`/`AacDecoder.Decode(Stream, ...)`/`WavPackDecoder.Decode(Stream, ...)`
(the read-fully-into-`byte[]` shape, confirmed to need no seek at all for any of the three). Every
other `AudioFormat` still throws `NotSupportedException` from `Probe(Stream, AudioFormat)` with a
message pointing back here. `ConvertFile`/`CutFile`'s own `Stream` overloads (the
destination-seekability half) are not started -- that's the only piece of this item still open.

## 2. Format-native metadata tags

WAV `'LIST'`/`'INFO'` tags are now read *and* written (`WavReader.Tags`/`ProbeResult.Tags`,
`WavWriter.Create`'s own `tags` parameter). Every other format has its own, completely different
metadata convention that this project currently touches nowhere: ID3v2 for MP3 (the near-universal
tagging format for the format, layered on top of/around the MPEG frames rather than part of the
bitstream itself), Vorbis comments for FLAC/Opus/Vorbis/OggVorbis (a simple vendor-string +
key=value list, embedded in FLAC's own `VORBIS_COMMENT` metadata block or an Ogg `OpusTags`/
Vorbis comment header packet), and AIFF's own `'LIST'`-equivalent (`ID3 `/`NAME`/`AUTH`/`(c) `
chunks — a different convention from WAV's INFO sub-chunks despite both being chunk-based).
Scoped as three separate, independent sub-items (one format family's convention at a time, same
"pick by rank, no forced ordering between unrelated formats" precedent the codec rewrite plan
itself established for FLAC vs. WavPack vs. MP3) rather than one combined item.

## 3. Loudness normalization (EBU R128)

`WaveformCalculator.RmsLevel` already says explicitly it's "a rough loudness indicator, not an
ITU-R BS.1770 perceptual loudness measurement" — real EBU R128/ITU-R BS.1770 loudness (the
standard streaming/broadcast platforms actually target, e.g. "normalize to -14 LUFS") needs a
K-weighting pre-filter (two cascaded biquads — reuses `BiquadTransform` directly, no new filter
math needed) followed by a windowed mean-square measurement with the standard's own gating rules
(absolute + relative gating to exclude silence/quiet passages from skewing the measurement). A new
`LoudnessTransform`/`LoudnessMeter` alongside the existing `PeakNormalizationTransform` (which
normalizes to a *peak* dBFS target, not a *perceptual loudness* one) — the two serve genuinely
different normalization goals, matching real-world mastering/distribution tooling's own
distinction between peak and loudness normalization.

## 4. Encoder delay/padding metadata

Opus has an explicit `pre_skip` field for this; Vorbis and this project's own MP3/encoder-side
codecs don't, so today a caller has to find the true sample-accurate alignment empirically via
cross-correlation (`VorbisEncoderSessionTest`'s own round-trip SNR helper does exactly this, and
it's noted as a real gap for any caller who isn't just measuring SNR in a test). A real encoder
delay/padding header (the MP3 ecosystem's own de facto standard: an LAME-style header comment
encoding delay/padding sample counts in a dedicated Xing-adjacent frame, which `Mp3Probe` already
partially understands per its own `IsVariableBitRate`/Xing-awareness) would let a real player trim
exactly, not empirically. Likely the smallest-effort item on this list if scoped to "write the
metadata," since computing the delay value itself is already deterministic from each encoder's own
known, fixed lookahead.

## 5. `CancellationToken` support

Every `IMediaEncoder` method already returns `Task`, but none currently accept a
`CancellationToken` — a caller driving a long `ConvertFile`/`CutFile` call from a web request or a
background job has no way to cancel it short of abandoning the `Task` outright (which doesn't
actually stop the underlying decode/encode loop). Cheapest to land as part of #1's own overload
work, since that's already touching every one of these call sites; each codec's own per-block
decode/write loop would need one `token.ThrowIfCancellationRequested()` check per block/frame
(not per-sample — too fine-grained to matter, and would add real per-sample overhead for no
benefit).

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
