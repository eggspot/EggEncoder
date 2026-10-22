# Live Ingest & Restream Plan (RTMP / FLV / SRT)

`docs/streaming-plan.md` gave EggEncoder an *output* side for live media: push PCM in, get
fMP4/MPEG-TS segments and HLS playlists out. This plan adds everything around it that ffmpeg is
used for in live work, in pure managed C#:

- **(a) Ingest**: a user points any standard client (OBS, Streamlabs, vMix, ffmpeg, a hardware
  encoder) at `rtmp://host:1935/app/streamKey` and streams *into* this library.
- **(b) Stream out**: this library takes any source (a live ingest, an MP4/MOV/FLV/MPEG-TS file, an
  HTTP(S) URL, an HLS stream, an RTMP stream) and pushes it out live to any RTMP/RTMPS destination
  (YouTube, Twitch, Facebook, another server), or to HLS.
- **(c) Restream**: one input fanned out to many live destinations at once, the equivalent of
  `ffmpeg -re -i <in> -c copy -f flv rtmp://...` plus ffmpeg's `tee` muxer, with per-destination
  reconnects so one dead destination never stalls the others.

Video is **passthrough only** (remux, never decode/encode — see "Scope philosophy"). Audio can be
passed through, or optionally transcoded per output through the codecs and `PcmTransform`s that
already exist.

This is a multi-session initiative on the same scale as `docs/streaming-plan.md`. **This file is
the source of truth for what's done, what's next, and why.** Read `docs/streaming-plan.md` (the HLS
output side this plan feeds) and `docs/video-support-backlog.md` (why video codecs are gated) before
anything else.

## How to use this plan (read this first, every time)

You are almost certainly a fresh Claude Code session with no memory of the conversation that
created this file. Here is everything you need:

1. **Read this whole file once** before picking anything, including "Scope philosophy",
   "Security posture" and "Architecture overview". The timestamp, payload-format and bounds rules
   below are load-bearing: a demuxer or chunk parser that "works with OBS" but skips them is a
   remote crash or a desynced stream waiting to happen.
2. **Read the repo's own `CLAUDE.md`** for house conventions (feature branch + PR, never push to
   `main`, conventional commit prefixes, 100% branch coverage on new code, `xUnit` +
   `FluentAssertions` + AAA, `Feature_Condition_ExpectedBehavior` test naming, docs-sync in the same
   PR).
3. **Check `git log --oneline -20`, `gh pr list --state all`, and this file's Status lines
   together** before picking anything. If a feature branch from a prior session is open and
   unmerged, continue that instead of starting something new.
4. **Pick the first item whose Status is `not started` and whose "Depends on" items are all done.**
   The dependency order is real (see the backlog table); don't reorder without a stated reason, and
   record any reorder in this file.
5. **Implement the item with tests**, following the codebase's conventions:
   - Full branch coverage: every public type/method, happy path, edge cases, every validation and
     failure path, plus the malformed-input tests "Security posture" requires.
   - **Folders**: `src/EggEncoder/Media/` for the packet model (item 1), `src/EggEncoder/Containers/Flv/`
     and `src/EggEncoder/Containers/MpegTs/` for file-level containers, `src/EggEncoder/Live/Rtmp/`,
     `src/EggEncoder/Live/Sources/`, `src/EggEncoder/Live/Restream/`, `src/EggEncoder/Live/Srt/` for
     network and live-orchestration code. The existing `Streaming/` folder stays the HLS/fMP4/TS
     *output* side (so the new TS demuxer in `Containers/MpegTs/` deliberately lives apart from the
     existing `Streaming/MpegTs/MpegTsMuxer`: file-level containers vs live packaging; the demuxer
     reuses `MpegTsCrc32` rather than copying it); items here extend its muxers in place (item 5) rather than forking them.
   - **Reuse, don't duplicate.** `MovAtomReader`/`Mp4SampleTable`/`MovVideoDemuxer` (MP4 demux),
     `Mp4BoxBuilder` (box writing), `Mp4EsdsParser.BuildAudioSpecificConfig` (AAC config),
     `AdtsFrameWriter` (ADTS framing), `MpegTsMuxer`/`MpegTsCrc32`, `FmpInitSegmentWriter`/
     `FmpMediaSegmentWriter`, `HlsSegmenter`, `IStreamingSink`, `LiveAudioPump`, and every
     `IStreamingEncoder` already exist. Extend them; don't write a second copy.
   - Each item's own "Acceptance criteria" are the bar for done, measurable, not "looks right".
6. **Commit to a feature branch, open a PR, review twice (per the global `CLAUDE.md`), get an
   independent review, merge, run `dotnet test --configuration Release` on `main`.**
7. **Update the item's own `**Status**` line in this file, in the same PR**: replace
   `not started` with `done ([PR #n](...))` plus anything deliberately left out. If blocked, write
   `blocked: <reason>` and move on to the next unblocked item; never mark a blocked item done.
8. **Add every new public surface to `src/EggEncoder.AotSmokeTest/`** in the same PR that adds it
   (a loopback RTMP publish, an FLV round trip, and so on), so Native AOT regressions show up in CI.

## Scope philosophy

- **Clean-room from public specs only.** The specs this plan is built from:
  - **Adobe RTMP Specification 1.0** (2012): handshake, chunk stream, message types, protocol
    control messages, user control messages, the NetConnection/NetStream command flow.
  - **Adobe AMF0 Specification** (Action Message Format, version 0): the command-message encoding
    RTMP uses for `connect`/`createStream`/`publish`/`play`/`onMetaData`.
  - **Adobe FLV and F4V File Format Specification v10.1** (Annex E for FLV): file header, tag
    header, `AUDIODATA`/`VIDEODATA` tag bodies, `AVCPacketType`/`AACPacketType`, `SCRIPTDATA`.
  - **Enhanced RTMP / Enhanced FLV** (Veovera Software Organization, v1 and v2): the `ExVideoTagHeader`
    with FourCC codec ids (`hvc1`, `av01`, `vp09`), multitrack, and the `fourCcList` connect
    property. Needed because OBS 29.1+ sends HEVC/AV1 this way.
  - **ISO/IEC 14496-15**: `AVCDecoderConfigurationRecord` (`avcC`) and
    `HEVCDecoderConfigurationRecord` (`hvcC`), the codec-config blobs FLV, RTMP and MP4 all carry.
  - **ITU-T H.264 / H.265, sequence parameter set syntax only**: parsing an SPS for width, height,
    profile and level is allowed (and needed for `avc1`/`hvc1` sample entries and HLS `CODECS`
    strings). Slice data is never parsed.
  - **ISO/IEC 13818-1** (MPEG-TS, already the basis of `MpegTsMuxer`), **RFC 8216** (HLS, already
    the basis of `HlsSegmenter`), **ISO/IEC 14496-12** (ISOBMFF).
  - **SRT**: the IETF draft `draft-sharabayko-srt` (Haivision's protocol description, last revision
    -01, expired but still the public description), for item 9 only. `libsrt` is MPL-2.0: never
    read its source; it may only be used as a black-box interoperability oracle, like ffmpeg.
  - The Adobe documents are Adobe-copyrighted but published for implementers; RTMP 1.0 ships with a
    royalty-free patent licence for implementations. Implement from them, never copy their text
    into this repo. **RTMPE (Adobe's encrypted RTMP) is out of scope**: that licence excludes
    circumventing content protection. RTMPS is plain RTMP over TLS and is in scope.
  - Where a spec is silent on a real-world convention (OBS's exact `connect` properties, YouTube's
    `releaseStream`/`FCPublish` calls, the `@setDataFrame` wrapper around `onMetaData`), verify
    against a **real client's wire bytes** captured as data (an `ffmpeg`/OBS publish to our own
    server, logged), the same arm's-length way `docs/streaming-plan.md` verified CMAF brand values.
    Never read the source of ffmpeg's `libavformat/rtmp*`, nginx-rtmp-module, SRS, MediaMTX or
    librtmp (GPL/LGPL or otherwise not MIT-compatible).
- **No native code, no subprocess, no ffmpeg/nginx in the shipped library.** Networking uses only
  `System.Net.Sockets`, `SslStream` and `HttpClient`. `ffmpeg`/`ffprobe` are allowed only as an
  optional, environment-gated local test oracle that skips cleanly when absent.
- **Video is passthrough only.** H.264/HEVC/AV1/VP9 payloads are copied, never decoded or encoded;
  those codecs stay gated in `docs/video-support-backlog.md`. Repackaging already-encoded bytes from
  one container to another is not the codec work that backlog's Phase 4 gates. Parsing parameter sets (SPS/VPS/PPS
  headers) for dimensions/profile/level is allowed. Converting H.264/HEVC between length-prefixed
  (AVCC/HVCC, used by FLV/RTMP/MP4) and Annex B start-code framing (used by MPEG-TS) is allowed:
  it is container framing, not codec work.
- **Audio may be transcoded per output**, only through codecs that already exist here (decode AAC
  via `AacFrameDecoder`, re-encode via `AacStreamingEncoder`, any `PcmTransform` in between). Video
  transcoding stays out of scope.
- **Async network I/O.** `docs/streaming-plan.md` chose synchronous callbacks because its work is
  CPU-bound encode. Sockets are not: a server holding many idle connections, a client waiting on a
  slow CDN, and a fan-out with N independent destinations all need real async I/O with
  `CancellationToken`. See "API shape" below for the decided shape and why.
- **Permissive pure-managed NuGet packages only if verified, own code preferred.** None are
  anticipated: every protocol here is fully specified publicly.
- **AOT-safe from the first PR.** No reflection-based serialization (AMF0 is hand-written, not
  `System.Text.Json`-style reflection), no `Reflection.Emit`, no dynamically loaded assemblies.

## Security posture (applies to every network-facing item)

An RTMP or SRT listener is attack surface reachable from the network. Every item that parses bytes
from a peer must:

1. **Bound every length and count field before allocating.** Concrete defaults (all configurable
   through an options type, all with a hard upper limit the options cannot exceed):
   - Inbound RTMP chunk size (`Set Chunk Size` from the peer): accept 1..`MaxInboundChunkSize`
     (default 65,536; the spec allows up to 2^31-1, which must never be honoured blindly). OBS and
     ffmpeg send 4,096.
   - RTMP message length (24-bit field, up to 16 MiB on the wire): reject above `MaxMessageLength`
     (default 8 MiB, enough for a large 4K keyframe; anything bigger closes the connection).
   - Concurrent partially-assembled messages per connection (one per chunk stream id): cap the
     number of live chunk stream ids (default 64) and the total buffered bytes per connection.
   - AMF0: nesting depth default 32 (real `connect`/`onMetaData` objects are 2-3 levels deep),
     strings capped at 64 KiB, properties per object/array capped at 1,024, and every count also
     bounded by the bytes left in the message, never trusted on its own.
   - Concurrent connections per server: default 64 (an in-process server, not a dedicated CDN
     edge); publishers per stream key: 1; stream keys/app names: length-limited (default 256 bytes)
     and validated before they reach any callback or any filesystem path.
   - FLV/MPEG-TS/MP4 file demux: the same "size field must fit inside what is left of the
     container" rule the AU/WAV/AIFF/CAF fixes (PRs #135, #136) established.
2. **Time out everything**: handshake (default 10 s), first `connect`/`publish` after the
   handshake (default 10 s), idle/no-data on a publishing connection (default 30 s), outbound
   connect (default 10 s). A peer that stalls mid-handshake must not hold a socket forever.
3. **Bind to `IPAddress.Loopback` (`127.0.0.1`), port 1935, by default.** Listening on all interfaces is an explicit opt-in on the
   server options.
4. **Authorize before accepting a publish.** The stream-key callback runs before any media is
   accepted; the default (no callback configured) rejects every publish rather than accepting all.
5. **Malformed-input tests are mandatory** for every parser: truncated input, every length field
   set to its maximum, nesting at and past the limit, an unknown message type, a chunk referencing a
   chunk stream that was never started, and random-byte fuzzing that must end in a clean rejection
   (a typed exception or a closed connection), never an unhandled exception, an infinite loop, or
   unbounded memory.

## Architecture overview

```
 SOURCES (item 8 unless noted)                             OUTPUTS
 ───────────────                                           ───────
 RTMP ingest server (item 4) ──┐                     ┌──► RTMP/RTMPS push client (item 6)
 RTMP pull client (item 6) ────┤                     ├──► HLS: HlsSegmenter + fMP4/TS muxers
 FLV file/URL (items 2, 8) ────┤                     │       with video tracks (item 5)
 MP4/MOV file (MovVideoDemuxer │    MediaPacket      ├──► FLV file recording (item 2)
   + MovDecoder audio) ────────├──► stream ─────────►├──► MPEG-TS file recording (item 5)
 MPEG-TS file/URL ─────────────┤   (item 1)          └──► SRT caller/listener (item 9)
 HLS pull, m3u8 ───────────────┤      │
 SRT listener (item 9) ────────┘      │
                                      ▼
                         Restream hub (item 7): one input,
                         N outputs, each with its own bounded
                         queue, reconnect/backoff, optional
                         audio-only transcode, failure isolation

 Real-time pacing (-re), looping and timestamp rebasing sit between
 a file/URL source and the hub (item 8).
```

Every source produces, and every output consumes, one shared **packet-level media model** (item 1).
That is what lets an RTMP ingest go straight to HLS, or an MP4 file go straight to YouTube, without
decoding video: each source only has to speak "packets", and each output only has to accept them.

### API shape

Decided by research for this plan (recorded in the PR that added this file, so the owner can
overrule it). Three layers, each with its own idiom:

1. **Pure, synchronous parsers and writers** — `FlvTagBodyParser`/`FlvDemuxer`/`FlvWriter`, the RTMP chunk
   (de)multiplexer, the AMF0 codec, `AnnexBConverter`, every muxer. They take `ReadOnlySpan<byte>`
   input or write to a `Stream`/buffer and never touch a socket. This keeps the existing codebase
   idiom (`EncodedAudioFrameCallback`, `IStreamingSink`, `MpegTsMuxer.BuildSegment`) and makes
   every parser unit-testable and fuzzable without any networking.
2. **Async sources and sinks at the network/file boundary.**
   - A source exposes `IAsyncEnumerable<MediaPacket> ReadPacketsAsync(CancellationToken)` plus its
     `MediaTrackSet` once the track headers are known.
   - An output implements `IMediaPacketSink : IAsyncDisposable` with
     `ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken)`,
     `ValueTask WriteAsync(MediaPacket packet, CancellationToken)` and
     `ValueTask CompleteAsync(CancellationToken)`. `ValueTask` gives a single output natural
     backpressure.
   - `MediaPacketSinkAdapter` bridges the existing synchronous `HlsSegmenter`/`IStreamingSink`
     into an `IMediaPacketSink`, so the HLS side needs no async rewrite.
3. **Fan-out isolation** (item 7): `RestreamHub` gives every output its own
   `System.Threading.Channels` bounded queue, its own pump task and its own `ReconnectPolicy`, so
   one slow or dead destination can never block ingest or any other output. The built-in
   `BoundedChannelFullMode.DropOldest` is not used: dropping single packets breaks every
   following video frame until the next keyframe. On overflow the hub discards that output's
   queued packets up to the next video keyframe (`OverflowPolicy.DropToNextKeyframe`) and counts
   it in that output's `RestreamOutputStats`.

**Why this is an explicit exception to `docs/streaming-plan.md`'s "synchronous callbacks" rule:**
that rule was justified by CPU-bound encode loops; sockets, TLS and reconnects are I/O-bound, and a
synchronous callback driven from inside a socket read loop would let one slow destination stall the
ingest, which defeats failure isolation. `docs/streaming-plan.md`'s own synchronous-callback
bullet ties that choice to its encode loops, its "Decode direction is out of scope" bullet defers
ingest to a plan of its own, and `docs/feature-roadmap.md` item 1 called real async I/O out of scope
for its `Stream` overloads; this plan is the ingest plan those deferred to. Everything below the network boundary keeps the synchronous idiom.
`System.Threading.Channels`, `IAsyncEnumerable`, `Socket` and `SslStream` are all in-box on every
target framework (`net8.0`/`net9.0`/`net10.0`) and AOT-safe.

### Packet model (item 1's shapes, named here because every later item uses them)

```csharp
namespace EggEncoder.Media;

public enum MediaKind { Audio, Video, Data }
public enum MediaCodec { Aac, Mp3, Opus, H264, H265, Av1, Vp9 }
// MediaKind.Data is reserved: timed-metadata tracks (onMetaData updates, ID3 passthrough) are
// deferred; until a data codec is added, sources surface metadata out of band, not as packets.

public sealed record MediaTrackInfo(
    int TrackId, MediaKind Kind, MediaCodec Codec,
    int Timescale,                          // ticks per second for this track's Pts/Dts
    ReadOnlyMemory<byte> CodecConfiguration, // avcC / hvcC / AudioSpecificConfig bytes
    int ConfigVersion,
    int SampleRate = 0, int Channels = 0, int Width = 0, int Height = 0);

public sealed record MediaTrackSet(IReadOnlyList<MediaTrackInfo> Tracks);

public readonly record struct MediaPacket(
    int TrackId,
    long Pts, long Dts,                     // ticks in that track's Timescale
    bool IsKeyframe,
    int ConfigVersion,
    ReadOnlyMemory<byte> Payload);          // AVCC for H.264/H.265; raw access unit for AAC
```

Rules, each one decided for a concrete reason:

- **Timestamps are `long` ticks in each track's own timescale** (1000 for RTMP/FLV, 90000 for
  MPEG-TS, the `mdhd` timescale for MP4, the sample rate for audio from our own encoders), converted
  only at a muxer boundary by one `MediaTimestamp.Rescale(value, fromScale, toScale)` helper using
  `Math.BigMul`/`Int128` with round-half-to-even. Not a fixed 90 kHz (44.1 kHz sample counts don't
  divide into it: the same drift `MpegTsMuxer` already avoids by computing from total samples), and
  not `TimeSpan` (100 ns ticks still round 1024/44100).
- **Both PTS and DTS** are kept: H.264/HEVC with B-frames needs the composition offset (FLV
  `CompositionTime`, MP4 `ctts`, fMP4 `trun` sample composition offsets, TS PES PTS vs DTS).
- **Codec configuration is track-level, not in-band.** A mid-stream change (a new FLV sequence
  header, a new SPS) produces a new `MediaTrackInfo` with a higher `ConfigVersion`; packets carry the
  version they belong to, and a muxer that sees it change writes a new init segment / an
  `EXT-X-DISCONTINUITY` / a new FLV sequence header.
- **Canonical H.264/H.265 payload framing is length-prefixed (AVCC/HVCC, 4-byte lengths).** FLV,
  RTMP, MP4 and fMP4 all use it, and so does `MovVideoDemuxer`'s output; only MPEG-TS uses Annex B.
  `AnnexBConverter` converts both ways: the TS muxer converts on write (inserting an AUD, and SPS/PPS
  before every IDR), and TS/HLS/SRT sources normalize to AVCC on read.
- **AAC payload is the raw access unit, no ADTS**, the same convention `EncodedAudioFrame` already
  uses; the TS muxer adds ADTS via `AdtsFrameWriter`.
- **Payloads are owned, immutable `ReadOnlyMemory<byte>`.** Fan-out shares one packet across N
  queues, which pooled buffers would make unsafe without ref-counting. At RTMP rates (on the order of
  1,000 packets per second) plain allocation is fine; pooled, ref-counted buffers are an
  optimisation for later, only with profiling numbers to justify them.
- **Naming**: "packet" (container-level, already encoded, any codec) is deliberately a different
  word from the existing "frame" (`EncodedAudioFrame`, encoder output). `EncodedAudioFrame` →
  `MediaPacket` conversion is a small adapter in item 1.

## Ranked, ordered backlog

| # | Item | Depends on |
|---|------|------------|
| 1 | [Packet-level media model](#1-packet-level-media-model) | none |
| 2 | [FLV demux + mux (file level, Enhanced FLV)](#2-flv-demux--mux-file-level-enhanced-flv) | 1 |
| 3 | [RTMP core: AMF0, handshake, chunk stream, control and command messages](#3-rtmp-core-amf0-handshake-chunk-stream-control-and-command-messages) | none (protocol layer); 2 for media message bodies |
| 4 | [RTMP server / ingest](#4-rtmp-server--ingest) | 1, 2, 3 |
| 5 | [Video tracks in the fMP4 and MPEG-TS muxers; packet-driven HLS](#5-video-tracks-in-the-fmp4-and-mpeg-ts-muxers-packet-driven-hls) | 1, 2 (FLV fixture as the test packet source) |
| 6 | [RTMP client: publish (rtmp/rtmps) and play](#6-rtmp-client-publish-rtmprtmps-and-play) | 3, 4 (loopback tests) |
| 7 | [Restream fan-out](#7-restream-fan-out) | 1, 5, 6 |
| 8 | [Live sources: files, URLs, HLS pull, RTMP pull; pacing, looping, rebasing](#8-live-sources-files-urls-hls-pull-rtmp-pull-pacing-looping-rebasing) | 1, 2, 5, 6, 7 |
| 9 | [SRT ingest and output](#9-srt-ingest-and-output) | 1, 7, 8 (MPEG-TS demux) |
| 10 | [Future / out of scope: WHIP/WebRTC, RTSP, LL-HLS, DASH](#10-future--out-of-scope-whipwebrtc-rtsp-ll-hls-dash) | — |

The packet model comes first because it is the output type of every demuxer, including FLV's.
Items 1 and 3 have no dependency on each other and can be done in either order; 5 needs 1 and 2 and
can be built alongside 3-4 once 2 is in, giving OBS → HLS end to end as soon as 4 lands. 4 needs 2 because an
RTMP audio/video message body *is* an FLV tag body. 6's tests are loopback runs against item 4's
server. 8 comes after the fan-out so that file/URL sources are tested against the real hub, and it
creates the MPEG-TS demuxer that SRT (9) needs.

---

## 1. Packet-level media model

**Problem**: every existing streaming type is audio-only and shaped around one encoder's output
(`EncodedAudioFrame`, `Mp4EncodedFrame`). Nothing can describe "track 1 is H.264 1280x720 with this
`avcC`, track 2 is AAC stereo 48 kHz", and nothing can carry an already-encoded video packet with
PTS/DTS from a demuxer to a muxer.

**Design** (`src/EggEncoder/Media/`):

- `MediaKind`, `MediaCodec`, `MediaTrackInfo`, `MediaTrackSet`, `MediaPacket` exactly as sketched in
  "API shape" above; `IMediaPacketSink`, plus `MediaPacketSinkAdapter`'s interface (its HLS body
  lands in item 5).
- `MediaTimestamp.Rescale(long value, int fromTimescale, int toTimescale)`: overflow-safe,
  round-half-to-even, validated (positive timescales).
- `AnnexBConverter`: AVCC/HVCC ↔ Annex B for one access unit, both directions, rejecting a length
  prefix that runs past the buffer and a start-code stream with no NAL units.
- `AvcDecoderConfigurationRecord` / `HevcDecoderConfigurationRecord`: parse and build (ISO/IEC
  14496-15), exposing the SPS/PPS(/VPS) NAL units and `lengthSizeMinusOne`.
- `H264SpsParser` / `H265SpsParser`: Exp-Golomb parse of the SPS header fields only (profile,
  level, chroma format, width/height with cropping), enough for `MediaTrackInfo.Width/Height` and
  an HLS/RFC 6381 `CODECS` string (`avc1.64001F`, `hvc1.1.6.L93.B0`). Emulation-prevention bytes
  are removed before parsing. Slice data is never touched.
- `EncodedAudioFrame` → `MediaPacket` adapter (`MediaPacketFactory.FromEncodedAudioFrame(...)`),
  so every existing `IStreamingEncoder` can feed the new pipeline unchanged.

**Acceptance criteria**:

- `AnnexBConverter` round-trips (AVCC → Annex B → AVCC is byte-identical) on real access units from
  a checked-in ffmpeg-produced H.264 MP4 fixture read through `MovVideoDemuxer` (a tiny 64x64,
  few-frame clip, as `docs/video-support-backlog.md` prescribes for fixtures).
- `H264SpsParser` reports the width/height/profile/level `ffprobe` reported for that fixture
  (baked into the test as literals); cropping (1920x1080 coded as 1088 rows) is tested with a
  hand-built or captured SPS literal, since the tiny fixture can't exercise it.
- `Rescale` never overflows internally (the intermediate product is 128-bit); a result that does
  not fit in a `long` throws `OverflowException` rather than wrapping; 1024 samples at 44,100 Hz to
  90 kHz rounds half-to-even as specified.
- Malformed `avcC`/`hvcC`/SPS input (truncated, zero NAL count, length past end) throws
  `InvalidDataException`, never `IndexOutOfRangeException`.

**Status**: done ([PR #138](https://github.com/eggspot/EggEncoder/pull/138)). `MediaPacketSinkAdapter` is deliberately not here: its HLS body lands in item 5, and only the `IMediaPacketSink` contract it implements ships now. Fixtures: the real H.264 access units come from the existing 640x360 `Codecs/Mov/test.mp4` (not a new 64x64 clip); 1080p cropping and every HEVC case use real x264/x265 avcC/hvcC records baked in as hex (`src/EggEncoder.UnitTests/Media/MediaFixtures.cs`).

## 2. FLV demux + mux (file level, Enhanced FLV)

**Problem**: FLV is RTMP's payload format (an RTMP audio/video message body is exactly an FLV tag
body) and the format ffmpeg's `-f flv` writes. It is the shortest path to ingest, recording, and
"stream a file to YouTube".

**Design** (`src/EggEncoder/Containers/Flv/`):

- `FlvTagBodyParser` (pure, span-based, shared with item 4): legacy `VIDEODATA` (FrameType,
  CodecID 7 = AVC with `AVCPacketType` 0 sequence header / 1 NALU / 2 end of sequence, plus the
  signed 24-bit `CompositionTime`), legacy `AUDIODATA` (SoundFormat 10 = AAC with `AACPacketType`
  0 = `AudioSpecificConfig` / 1 = raw; SoundFormat 2 = MP3), and Enhanced-FLV `ExVideoTagHeader`
  (IsExHeader bit, `PacketType` SequenceStart/CodedFrames/CodedFramesX/SequenceEnd/Metadata,
  FourCC `avc1`/`hvc1`/`av01`/`vp09`). Unknown codec ids produce a typed "unsupported codec"
  result for that tag rather than throwing for the whole stream.
- `FlvDemuxer`: incremental (`Feed(ReadOnlySpan<byte>)`, so it works on a file, an HTTP body or a
  network stream), emitting a `MediaTrackSet` once sequence headers have been seen and then
  `MediaPacket`s; `FlvFileSource` wraps it as an async source over a `Stream`.
- `ScriptDataAmf0`: `onMetaData` (and the `@setDataFrame` wrapper) decoded through item 3's AMF0
  reader, exposed as typed metadata (`duration`, `width`, `height`, `framerate`, `videodatarate`,
  `audiodatarate`, `encoder`); if item 3 isn't merged yet, this item adds the AMF0 reader in
  `Live/Rtmp/Amf0/` and item 3 reuses it.
- `FlvWriter` (`IMediaPacketSink` and a synchronous core): header, `PreviousTagSize` fields,
  `onMetaData`, sequence headers before the first frame of each track and again when
  `ConfigVersion` changes, legacy tags for H.264/AAC/MP3 and Enhanced tags for HEVC/AV1/VP9.
- FLV timestamps are 32-bit milliseconds (24 bits + 8-bit extension); the writer rescales from each
  track's timescale and the demuxer handles the extension byte and 32-bit wrap.

**Acceptance criteria**:

- Demux a checked-in, ffmpeg-produced FLV fixture (H.264 + AAC, a few frames) and match `ffprobe`
  ground truth: packet count per track, keyframe positions, PTS/DTS, codec config bytes.
- Round trip: demux → `FlvWriter` → demux is packet-identical (payload, timestamps, flags).
- An Enhanced-FLV HEVC fixture (ffmpeg 6.1+ writes these with `-c:v libx265 -f flv`) demuxes with
  `MediaCodec.H265` and the right `hvcC`.
- Env-gated: `ffprobe` on our written FLV reports the expected streams (skips if absent).
- Malformed input: truncated header, a tag `DataSize` past EOF, a `PreviousTagSize` mismatch,
  a truncated `avcC`, a sequence header with no NAL units — all rejected cleanly. A negative
  `CompositionTime` is legal (the field is signed) and must round-trip, with its own test.

**Status**: done ([PR #139](https://github.com/eggspot/EggEncoder/pull/139)). Deliberately left out: `.flv` is not wired into `NativeEncoder`/`IMediaEncoder` (`Probe`/`ConvertFile`/`CutFile`), tracked as `docs/feature-roadmap.md` item 8 (decided by research: `docs/video-support-backlog.md` rule 7, and `AacFrameDecoder` is mono-only today); the `onMetaData` duration is written as 0 and never patched (the writer is forward-only, the live-FLV convention); no AVC end-of-sequence tag is written on `Complete`; Enhanced RTMP v2 audio (`ExAudioTagHeader`, so Opus/FLAC in FLV), Multitrack and ModEx are typed `Unsupported` on read and not writable; AMF0 reader bounds are fixed constants for now (item 3 makes them options). The ffprobe cross-check is gated on `EGGENCODER_FFMPEG_ORACLE=1` (xUnit 2 has no dynamic skip), and passes locally against ffmpeg 9.0.2.

## 3. RTMP core: AMF0, handshake, chunk stream, control and command messages

**Problem**: everything RTMP needs below the "publish a stream" level, written once and shared by
the server (item 4) and the client (item 6).

**Design** (`src/EggEncoder/Live/Rtmp/`), all pure and synchronous over spans/buffers:

- `Amf0Reader`/`Amf0Writer`: Number, Boolean, String, Long String, Object, Null, Undefined,
  ECMA Array, Strict Array, Date, Object End, XML Document (read as a string); Reference (0x07),
  Typed Object (0x10), MovieClip/RecordSet (reserved) and the AMF3 switch (marker 0x11) rejected
  with a typed error. Values as a small `Amf0Value` discriminated type, no reflection. Every bound
  from "Security posture".
- `RtmpHandshake`: the simple C0/C1/C2 ↔ S0/S1/S2 exchange from the spec, as a state machine over
  bytes. Whether common publishers (ffmpeg, OBS, vMix) also expect the server to answer the
  undocumented digest-style handshake is checked against captured wire bytes in this item and
  implemented only if a real client needs it (never RTMPE).
- `RtmpChunkReader`/`RtmpChunkWriter`: basic header (1-3 bytes, chunk stream ids 2..65599), message
  header types 0-3, extended timestamps (including the type-3 extended-timestamp rule), per chunk
  stream reassembly state, `Set Chunk Size`/`Abort`/`Acknowledgement`/`Window Acknowledgement
  Size`/`Set Peer Bandwidth` handling, and the bounds from "Security posture".
- `RtmpMessage` (type id, stream id, timestamp, payload) and `RtmpUserControl` (Stream Begin,
  Stream EOF, Ping Request/Response).
- `RtmpCommand` encode/decode: `connect`, `_result`/`_error`, `createStream`, `releaseStream`,
  `FCPublish`/`FCUnpublish`, `publish`, `play`, `deleteStream`, `onStatus` (with the standard
  `NetStream.Publish.Start` / `NetStream.Play.Start` codes), and `@setDataFrame`/`onMetaData`
  data messages.

**Acceptance criteria**:

- A full handshake + `connect` + `createStream` + `publish` exchange between `RtmpChunkWriter` and
  `RtmpChunkReader` over an in-memory pipe, at chunk sizes 128 (default), 4096 and 65536, with
  messages that span many chunks and interleave across chunk stream ids.
- Wire bytes of `connect`/`publish` captured once from a real `ffmpeg` publish (and OBS, when a
  session has it available), stored as a checked-in binary fixture captured as data, parse
  correctly.
- Fuzz/malformed tests for every bound (oversized chunk size, message length over the limit,
  extended timestamp truncated, type-1 header on a never-started chunk stream, AMF nesting past
  the limit, a string length past the end) end in a typed `RtmpProtocolException`.

**Status**: done ([PR #140](https://github.com/eggspot/EggEncoder/pull/140)). The capture is ffmpeg 9.0.2's (`src/EggEncoder.UnitTests/Live/Rtmp/ffmpeg_publish.bin`); no OBS capture yet (item 4's manual OBS check covers it). ffmpeg completes the simple handshake, so no digest handshake was implemented. The AMF0 bounds stay `Amf0Reader` constants equal to the defaults above; the chunk-level bounds are `RtmpChunkReaderOptions`, for item 4's server options to embed. `RtmpProtocolException` is an `IOException` (`InvalidDataException` is sealed). Interleaving is covered on read (hand-built chunks); `RtmpChunkWriter` writes each message whole. Truncation (including a cut-short extended timestamp) is reported by `RtmpChunkReader.Complete()`, which item 4 calls when a connection closes; until then the reader waits for more bytes, and item 4's idle timeout covers a peer that just stops. Decided by research: a type 3 header that starts a message right after a type 0 one uses the type 0 timestamp as its delta (the specification's type 3 description; the writer never emits that case), and a peer that omits the extended timestamp on continuation chunks (older librtmp/ffmpeg) is detected on its first continuation and latched per chunk stream.

## 4. RTMP server / ingest

**Problem**: the headline feature: OBS/vMix/ffmpeg/hardware encoders publish to
`rtmp://host:1935/app/streamKey`, and this library receives the stream as packets.

**Design** (`src/EggEncoder/Live/Rtmp/RtmpServer.cs`):

- `RtmpServer : IAsyncDisposable` with `RtmpServerOptions` (bind endpoint, default
  `IPAddress.Loopback:1935`; every bound and timeout from "Security posture"; optional
  `X509Certificate2` for RTMPS listening via `SslStream`), `StartAsync`/`StopAsync`, and the
  actual bound port exposed (so tests can bind port 0).
- `AuthorizePublishAsync: Func<RtmpPublishRequest, CancellationToken, ValueTask<bool>>` (app name,
  stream key, remote endpoint, `connect` properties). No callback configured means every publish is
  rejected (`NetStream.Publish.BadName`), never "accept all".
- `PublishStarted`/`PublishEnded` notifications delivering an `RtmpIngestStream` per publish: its
  `MediaTrackSet` (built from `onMetaData` + sequence headers via item 2's tag-body parser) and an
  `IAsyncEnumerable<MediaPacket>`. A single-publisher-per-key rule; a second publish to a live key
  is rejected.
- Connection lifetime: one task per connection, all I/O async with the server's
  `CancellationToken`, acknowledgements sent per the window size, graceful `deleteStream`/
  `FCUnpublish`/socket-close handling, and a publisher disconnect completing its packet stream
  rather than faulting it.
- `play` from a client is **not** in this item (server-side playback/relay is a later extension,
  covered by the hub in item 7 plus HLS); this item is ingest only.

**Acceptance criteria**:

- In-process loopback: a test-only minimal publisher built on item 3's primitives (item 6's
  client replaces it once merged) connects to `127.0.0.1:<ephemeral>`, publishes item 2's FLV
  fixture, and the server's packet stream equals the fixture's packets exactly.
- Auth: a rejected key gets `NetStream.Publish.BadName` and no packets reach the consumer; no
  callback = rejected.
- Bounds and timeouts: a client that stalls mid-handshake is dropped after `HandshakeTimeout`; the
  connection limit is enforced; every "Security posture" bound has a test that sends a violating
  peer and sees the connection closed while other connections keep working.
- Env-gated cross-check: `ffmpeg -re -i fixture.flv -c copy -f flv rtmp://127.0.0.1:<port>/live/key`
  is received intact (skips cleanly if `ffmpeg` is absent). A manual OBS check is documented in the
  PR, not automated.
- AOT smoke test: loopback publish → server → packet count.

**Status**: done ([PR #141](https://github.com/eggspot/EggEncoder/pull/141)). Decided by research (recorded in the PR): publishes are delivered through `PublishStarted`/`PublishEnded` events (not an accept-style API), with the end also visible as the packet enumeration finishing and `RtmpIngestStream.Completion`; `Tracks` advances on the reader's side in queue order, as in `FlvFileSource`; the per-publish queue is lossless with TCP backpressure, bounded by count and bytes, and a consumer that leaves it full past `ConsumerStallTimeout` (a new option, default 10 s) gets the publisher disconnected (dropping to the next keyframe stays item 7's per-output policy); the tracks to wait for come from `onMetaData`'s `videocodecid`/`audiocodecid` when it precedes the first media message, else both (an audio-only publisher that sends no metadata has its packets held until the 1,024-packet fallback or the end); timeouts run on an injectable `TimeProvider`, tested with `Microsoft.Extensions.TimeProvider.Testing` (test project only). Implementation choices: `FlvDemuxer`'s tag-level logic moved into the internal `FlvTagDemuxer`, shared unchanged by the server; one publish per connection; a publisher that disconnects mid-message ends its publish normally (the partial message is dropped); `releaseStream`/`FCPublish`/`getStreamLength` get `_result`, other calls with a transaction id get `_error` `NetConnection.Call.Failed`; aggregate messages (type 22) and AMF3 data are ignored, an AMF3 command closes the connection. The manual OBS check is described in the PR but was not run (an unattended session); the automated cross-checks are a replayed ffmpeg 9.0.2 wire capture and the `EGGENCODER_FFMPEG_ORACLE=1`-gated live `ffmpeg -re` publish, which passes locally.

## 5. Video tracks in the fMP4 and MPEG-TS muxers; packet-driven HLS

**Problem**: `FmpInitSegmentWriter`/`FmpMediaSegmentWriter`/`MpegTsMuxer`/`HlsSegmenter` only know
one mono-or-stereo AAC track. An RTMP ingest or an MP4 file needs H.264(+AAC) to go straight to HLS
without decoding.

**Design** (extend the existing types in `src/EggEncoder/Streaming/`, don't fork them):

- fMP4: multi-track init segment (`avc1` + `avcC`, `hvc1` + `hvcC`, existing `mp4a` + `esds`), one
  `traf` per track per segment, `trun` with per-sample duration, size, flags (sync vs non-sync) and
  signed composition offsets (version 1 `trun`).
- MPEG-TS: PMT with `stream_type` 0x1B (H.264) / 0x24 (HEVC) alongside the existing 0x0F (ADTS
  AAC), video PES with PTS and DTS, Annex B conversion via item 1's `AnnexBConverter` with an AUD
  and SPS/PPS before every IDR, and **PCR** on the video PID (the audio-only muxer documented PCR as
  a deliberate MVP omission; with video it is required for real players).
- `HlsSegmenter` accepts `MediaPacket`s for a `MediaTrackSet`: segments are cut only at video
  keyframes once the target duration is reached (audio-only renditions keep today's frame-boundary
  rule), `EXT-X-TARGETDURATION` keeps today's "real maximum observed" rule, and a `ConfigVersion`
  change starts a new init segment with `EXT-X-DISCONTINUITY`. Master-playlist `CODECS` strings
  come from item 1's SPS parser.
- The existing audio-only constructors keep working unchanged (no breaking change);
  `MediaPacketSinkAdapter` wraps the packet-driven path as an `IMediaPacketSink`.

**Acceptance criteria**:

- `MovAtomReader` self-parses the multi-track init segment; the existing test-only `moof`/`trun`
  reader verifies per-sample flags and composition offsets.
- Item 2's FLV fixture (H.264 + AAC) → packets → HLS (fMP4 and TS) (the MP4 source path arrives in
  item 8): every segment starts with a keyframe,
  segment durations within one GOP of target, playlist grammar checked by the existing test-only
  checker.
- TS: PAT/PMT/PES/PCR fields checked by a test-only TS reader (or item 8's demuxer once merged);
  PTS/DTS monotonic, PCR ≤ DTS.
- Env-gated: `ffprobe` on the produced playlist reports both streams with correct duration;
  `ffmpeg -i playlist.m3u8 -f null -` decodes without errors.
- Existing audio-only streaming tests pass unchanged.

**Status**: not started.

## 6. RTMP client: publish (rtmp/rtmps) and play

**Problem**: pushing to YouTube/Twitch/Facebook/any RTMP server, and pulling an RTMP stream as a
source.

**Design** (`src/EggEncoder/Live/Rtmp/RtmpClient.cs`):

- `RtmpPublishClient : IMediaPacketSink` for `rtmp://` and `rtmps://` URLs (TLS via `SslStream`,
  default certificate validation, a caller-supplied validation callback only as an explicit
  option). Sends the command sequence real ingest services expect (`connect` with `tcUrl`/`app`/
  `flashVer`/`fourCcList` for Enhanced RTMP, `releaseStream`, `FCPublish`, `createStream`,
  `publish`), then `@setDataFrame`/`onMetaData`, sequence headers and media messages through item 3's
  chunk writer with a 4096 outbound chunk size. The exact command order and properties are verified
  against real client wire bytes (item 3's captures), never against another implementation's source.
- `RtmpPlayClient`: `connect`/`createStream`/`play`, exposing the received stream as an async
  `MediaPacket` source (item 8 builds on it).
- Connect/handshake timeouts, `CancellationToken` everywhere, a clean `FCUnpublish`/`deleteStream`
  on `CompleteAsync`. Reconnect is **not** in the client (it belongs to the hub's per-output
  policy, item 7); the client reports a typed failure and stops.
- Stream keys are secrets: never logged, never included in exception messages.

**Acceptance criteria**:

- Loopback: `RtmpPublishClient` → item 4's `RtmpServer` (plain and RTMPS with a test certificate
  generated in-process) delivers item 2's fixture packet-identically; `RtmpPlayClient` against a
  test-only play responder built on item 3 does the same in reverse.
- Env-gated: publish to a local `ffmpeg -listen 1 -i rtmp://127.0.0.1:<port>/live/key -c copy out.flv`
  and `ffprobe` the result (skips if absent). Pushing to real YouTube/Twitch needs credentials and is
  a documented manual check only, never a test.
- Failure paths: connection refused, TLS failure, server `_error`/`NetStream.Publish.BadName`,
  server closes mid-stream — each surfaces a typed exception and frees the socket.
- AOT smoke test: client → server loopback.

**Status**: not started.

## 7. Restream fan-out

**Problem**: `ffmpeg -i in -c copy -f tee "[f=flv]rtmp://a|[f=flv]rtmp://b|[f=hls]out.m3u8"` with
the failure handling ffmpeg's tee muxer only partly has: one input to N outputs where any output
can fail, reconnect, or fall behind without affecting the input or the others.

**Design** (`src/EggEncoder/Live/Restream/`):

- `RestreamHub : IMediaPacketSink`: `AddOutput(string name, Func<CancellationToken,
  ValueTask<IMediaPacketSink>> openOutput, RestreamOutputOptions)` / `RemoveOutput` at any time,
  including while live.
- Per output: a bounded `Channel<MediaPacket>`, a pump task, `ReconnectPolicy` (exponential
  backoff with jitter, max attempts or unlimited), and on reconnect the hub replays the current
  `MediaTrackSet`'s sequence headers and waits for the next video keyframe before sending media,
  so a reconnected destination starts decodable. `OverflowPolicy.DropToNextKeyframe` as described
  in "API shape"; `RestreamOutputStats` (packets sent/dropped, reconnect count, last error, state)
  readable per output.
- Optional per-output audio transcode: `AudioTranscodeOptions` decodes AAC to PCM, runs a
  `PcmTransformPipeline` (resample, loudness, channel remix), and re-encodes with an
  `IStreamingEncoder` (AAC-LC today); video stays passthrough.
- Timestamps are rebased per output so each destination sees a stream starting near zero, and
  stay monotonic across reconnects.

**Acceptance criteria**:

- One loopback ingest → hub → three outputs (two loopback RTMP servers + in-memory HLS): all three
  receive the full stream.
- Kill one destination server mid-stream: the other outputs receive every packet with no added
  delay beyond the test's tolerance; the killed output reconnects when its server comes back and
  its first media packet after reconnect is a keyframe preceded by sequence headers.
- A deliberately slow output (sink that blocks) triggers `DropToNextKeyframe` and its stats, while
  the input and the other outputs never block.
- Audio transcode output: decoded audio round-trips with the existing SNR-floor helpers.

**Status**: not started.

## 8. Live sources: files, URLs, HLS pull, RTMP pull; pacing, looping, rebasing

**Problem**: ffmpeg's `-re -i <anything>`: turn a file, an HTTP URL, an HLS stream or an RTMP stream
into a live packet stream that can be fed into the hub.

**Design** (`src/EggEncoder/Live/Sources/`), each source an async `MediaPacket` source:

- `Mp4FileSource`: video from `MovVideoDemuxer` (extended with per-sample decode/composition times
  from `stts`/`ctts`, plus `avcC`/`hvcC` from the sample entry) and AAC audio from the existing
  `MovDecoder` sample-table path as raw access units (no decode), interleaved by DTS.
- `FlvFileSource` (item 2), `MpegTsSource` (new `Containers/MpegTs/MpegTsDemuxer`: PAT/PMT, PES
  reassembly, ADTS → raw AAC, Annex B → AVCC, 33-bit PTS wrap, continuity-counter checks; the
  inverse of the existing muxer), `HttpProgressiveSource` (an `HttpClient` stream into the FLV or TS
  demuxer, chosen by content type or sniffing), `HlsPullSource` (media and master playlists,
  rendition selection, segment fetch for TS and fMP4, live playlist reload at target duration,
  `EXT-X-DISCONTINUITY` handling), `RtmpPullSource` (item 6's play client).
- `RealtimePacer`: the `-re` behaviour, releasing packets at DTS pace against a monotonic clock
  (`Stopwatch`/`TimeProvider`, injectable for tests).
- `LoopingSource` and `TimestampRebaser`: restart a finite source and keep timestamps continuous
  across loops and across a switch from one source to another (a playlist of files, a fallback
  slate when the live input drops).
- HLS pull is the largest piece; split it into its own PR (8b) if it doesn't fit with the rest.

**Acceptance criteria**:

- Each source reproduces `ffprobe` ground truth for a checked-in fixture (packet counts, keyframes,
  timestamps); HTTP and HLS sources are tested against an in-process loopback HTTP server (a small
  test-only `TcpListener`-based helper; no ASP.NET dependency) serving checked-in fixtures, no
  external network.
- `RealtimePacer` with a fake `TimeProvider`: release times match DTS within a stated tolerance.
- Looping a 2-second file 3 times gives monotonic, gap-free timestamps; switching sources mid-stream
  does too.
- End to end: MP4 file → `-re` pacer → hub → loopback RTMP server and HLS, packets identical to the
  file's.

**Status**: not started.

## 9. SRT ingest and output

**Problem**: SRT is the second protocol professional encoders and contribution links use, mostly
carrying MPEG-TS over UDP with retransmission.

**Design** (`src/EggEncoder/Live/Srt/`): clean-room from `draft-sharabayko-srt` only: UDP packet
format, the caller/listener handshake (induction + conclusion, HSv5), `streamid`, ACK/ACKACK/NAK
loss recovery, TSBPD latency, keepalive, shutdown. Payload is MPEG-TS, handled by the existing muxer
(output) and item 8's demuxer (input). Encryption (AES key wrapping in the handshake) is a separate
later sub-item, only if it can be done with in-box `System.Security.Cryptography`.

**Acceptance criteria**: loopback caller ↔ listener transfer of a TS fixture with injected packet
loss (a test-only lossy UDP relay) recovers every packet; env-gated cross-check with
`ffmpeg ... -f mpegts srt://127.0.0.1:<port>?mode=caller` when ffmpeg has SRT support (skip cleanly
otherwise); every "Security posture" bound applies (packet sizes, handshake flood, buffer sizes).

**Status**: not started; do not start before items 1-8 are done.

## 10. Future / out of scope: WHIP/WebRTC, RTSP, LL-HLS, DASH

- **WHIP/WebRTC**: needs ICE, DTLS-SRTP and (for browsers) Opus/H.264 RTP packetization. DTLS is not
  in-box in .NET, so this is out of scope unless research shows a pure-managed, permissive DTLS path.
- **RTSP/RTP**: well specified (RFC 7826, RFC 3550, RFC 6184 for H.264), feasible later; it serves IP
  cameras more than live streaming, so it ranks below everything above.
- **LL-HLS**: tracked as `docs/streaming-plan.md` item 7.
- **MPEG-DASH**: the fMP4 segments from item 5 are already DASH-compatible; an MPD writer is a small
  later item if demand appears.
- **Server-side playback** (`play` from our own RTMP server, HTTP-FLV serving): a natural extension
  of items 4 and 7, not scheduled.

**Status**: not scheduled.

## Top-level test strategy

1. **Everything runs with no external network.** Network tests use in-process loopback on
   `127.0.0.1` with ephemeral ports (bind port 0, read the real port back), our client against our
   server.
2. **Checked-in fixtures generated offline** with `ffmpeg` (tiny clips: 64x64, a few frames, a
   second of audio), with `ffprobe` ground truth baked into tests as literals, the
   `docs/video-support-backlog.md` convention.
3. **ffmpeg/ffprobe cross-checks are optional and env-gated**: they skip cleanly when the tools are
   absent and are never a CI dependency.
4. **Self-conformance first**: our demuxer parses our muxer's output, and our server receives our
   client's stream, packet-identical, before any external tool is involved.
5. **Malformed-input and fuzz tests for every parser and every listener**, per "Security posture".
6. **Concurrency tests are deterministic**: fake `TimeProvider`, explicit gates instead of
   `Task.Delay`-based timing, and hard per-test timeouts so a hang fails rather than stalls CI.
7. **AOT smoke test** for each new public surface, and 100% branch coverage of new production code.
