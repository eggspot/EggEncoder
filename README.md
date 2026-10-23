# 🥚 EggEncoder

> **Audio encoding/decoding toolkit for .NET** — 12 formats (AAC, AIFF incl. AIFC, ALAC, AU, FLAC, MP3, Opus, TTA, Vorbis, WAV incl. IMA/MS/Yamaha ADPCM and G.711, WavPack, WMA) plus MOV/MP4 probing, entirely pure C#, built-in waveform generation, and an opt-in PCM transform pipeline (resampling, gain/peak/loudness normalization, dynamics compression, noise gating, peak limiting, echo/delay, pan/balance, channel remix, fades, parametric EQ, FIR filtering, mixing), all behind one `IMediaEncoder` interface.

Sponsored by [eggspot.app](https://eggspot.app)

[![CI](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml/badge.svg)](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EggEncoder.svg)](https://www.nuget.org/packages/EggEncoder)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/eggspot)](https://github.com/sponsors/eggspot)

📖 **[Full documentation →](https://eggspot.github.io/EggEncoder/)**

## Overview

EggEncoder gives you a single `IMediaEncoder` abstraction — `Probe`, `ConvertFile`, `CutFile` — implemented entirely in-process by `NativeEncoder`: pure .NET codec implementations for every supported format (AAC, WAV, AIFF, AU, ALAC, FLAC, MP3, TTA, WMA, Opus, Vorbis, WavPack). No external process, no ffmpeg install, no subprocess overhead.

### Why EggEncoder?

- 🚀 **100% pure managed, fully in-process** — no native binary, no P/Invoke, anywhere in this project; no subprocess/shell-out overhead anywhere
- 🌍 **Truly cross-platform** — runs anywhere .NET 8/9/10 runs (Windows, Linux, macOS); CI builds and tests on all three
- ❄️ **Native AOT compatible** — no reflection, no dynamic code; publish with `PublishAot=true` and it just works
- 🎼 **Broad format coverage** — AAC, AIFF (incl. AIFC), ALAC, AU, FLAC, MP3, Opus, TTA, Vorbis, WAV, WavPack, WMA decode/encode; MOV/MP4 metadata probing + mono AAC-LC audio decode
- 📊 **Built-in waveform generation** — normalized peak windows for any decoded stream
- 🌊 **Stream-based `Probe`** — `Probe(Stream, AudioFormat)` probes a source that isn't a local file (an HTTP response body, blob storage, an in-memory buffer); every supported format (WAV/AIFF/AU/FLAC/MP3/AAC/WavPack/TTA/ALAC/Opus/Vorbis/WMA/MOV/MP4) — see [docs/feature-roadmap.md](docs/feature-roadmap.md)
- 🌊 **Stream-based `ConvertFile`/`CutFile`** — `ConvertFile(Stream, AudioFormat, Stream, AudioFormat)`/`CutFile(Stream, AudioFormat, int, int, Stream, AudioFormat)` convert/cut between a source `Stream` and a destination `Stream`, PCM transform pipeline included; any `Stream`-readable source format into a WAV, AIFF, AU, FLAC, TTA, AAC, or WMA `Stream` destination today, more destination formats landing incrementally
- 📡 **Live streaming (early)** — `IStreamingEncoder`/`AacStreamingEncoder.Create` push PCM in and get encoded AAC-LC access units out via a callback, with no destination file/`Stream` or known total length required; the first piece of a larger HLS/fMP4/MPEG-TS packaging effort — see [docs/streaming-plan.md](docs/streaming-plan.md) for what's done and what's next
- 🏷️ **WAV metadata tags** — `Probe` surfaces a WAV file's `'LIST'`/`'INFO'` chunk (title, artist, etc.) via `ProbeResult.Tags`, and `WavWriter.Create` can write them back out too
- 🎙️ **MP3 ID3v2 tags** — `Probe` surfaces an MP3 file's ID3v2.3/2.4 text frames (title, artist, album, etc.) via the same `ProbeResult.Tags`, and `Mp3Encoder.Encode`/`OpenSession` can write a fresh ID3v2.4 tag back out too
- 💬 **FLAC/Opus/Vorbis comment tags** — `Probe` surfaces a FLAC/Opus/Vorbis file's Vorbis comments (title, artist, album, etc.) via the same `ProbeResult.Tags`, and `FlacEncoder`/`OpusEncoder`/`VorbisEncoder`'s own `Encode`/`OpenSession` can write them back out too
- 🎼 **AIFF metadata tags** — `Probe` surfaces an AIFF file's `'NAME'`/`'AUTH'`/`'(c) '` text chunks (and an embedded ID3v2 `'ID3 '` chunk, if present) via the same `ProbeResult.Tags`, and `AiffWriter.Create` can write the three native text chunks back out too
- 🔊 **EBU R128 loudness measurement and normalization** — `Probe` surfaces real ITU-R BS.1770-4 / EBU R128 integrated loudness (in LUFS) via `ProbeResult.IntegratedLoudness`, alongside the existing peak/RMS level — a perceptual measurement, not just a sample-level average; `LoudnessTransform` (two-pass, like `PeakNormalizationTransform`) normalizes to a target LUFS
- 🎚️ **MP3 gapless-playback metadata** — every MP3 `Mp3Encoder`/`OpenSession` writes carries a de-facto-standard "LAME tag" encoder-delay/padding header; `Probe` surfaces it (from this project's own output or any other LAME-tagged file) via `ProbeResult.EncoderDelaySamples`/`EncoderPaddingSamples`, and already folds it into a more accurate `DurationSeconds`
- ✂️ **Sample-accurate cutting** — trim audio files without a full decode→encode round trip
- 🛑 **`CancellationToken` support** — `ConvertFile`/`CutFile` (and `Probe`) each have a `CancellationToken`-accepting overload; `ConvertFile`/`CutFile` check it once per decoded block for real, sub-second cancellation, not just once up front
- 🎛️ **PCM transform pipeline** — resampling, gain/peak normalization, channel remix, bit-depth/float conversion, fades, parametric EQ (biquad + Butterworth) and general FIR filtering, mixing, and concatenation — opt-in, composable, and layered onto `Convert`/`Cut` without touching the original API
- 🪶 **Dependency-light** — only `Microsoft.Extensions.*.Abstractions` and a handful of pure managed codec NuGet packages (NLayer, Concentus, NVorbis, OggVorbisEncoder)
- 📖 **MIT licensed** — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the licenses of the pure managed codec dependencies above

## Installation

```bash
dotnet add package EggEncoder
```

No native binaries, no extra install steps — just the package.

## Quick Start

```csharp
using EggEncoder;

// Dependency injection (recommended)
builder.Services.AddEggEncoder();
```

```csharp
public class MediaService(IMediaEncoder mediaEncoder)
{
    public async Task<ProbeResult> Inspect(string filePath) =>
        await mediaEncoder.Probe(filePath);

    public Task Transcode(string sourcePath, string destPath) =>
        mediaEncoder.ConvertFile(sourcePath, destPath);

    public Task Trim(string sourcePath, string destPath, int startSeconds, int endSeconds) =>
        mediaEncoder.CutFile(sourcePath, destPath, startSeconds, endSeconds);
}
```

### Without DI

```csharp
using EggEncoder;
using Microsoft.Extensions.Logging.Abstractions;

IMediaEncoder encoder = new NativeEncoder(NullLogger<NativeEncoder>.Instance);
var probeResult = await encoder.Probe("track.flac");
```

## PCM Transform Pipeline

`AddEggEncoder()` also registers `IPcmTransformEncoder` (same `NativeEncoder` instance as `IMediaEncoder`) — an opt-in `PcmTransformPipeline` of composable `IPcmTransform`s that runs between decode and the destination write:

```csharp
using EggEncoder.Codecs;
using EggEncoder.Pcm;

var pipeline = new PcmTransformPipeline(
    new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 2),
    new VolumeTransform(gain: 1.5));

AudioCutter.Convert(sourcePath, destPath, pipeline);
// or via DI: await pcmTransformEncoder.ConvertFile(sourcePath, destPath, pipeline);
// (pcmTransformEncoder: IPcmTransformEncoder, injected the same way as IMediaEncoder above)

// Float WAV destination (source stays a normal 32-bit int/float WAV; Float32 only changes the
// on-disk encoding of the destination -- see "Important limits" below):
AudioCutter.Convert(sourcePath, floatDestPath, WavSampleFormat.Float32);
```

Covers **resampling** (`ResamplingTransform`), **gain / peak normalization** (`VolumeTransform` / `PeakNormalizationTransform`), **dynamics compression** (`CompressorTransform`, threshold/ratio/attack/release/makeup gain), **noise gating** (`NoiseGateTransform`, the mirror-image downward expander below threshold), **peak limiting** (`LimiterTransform`, infinite-ratio compression plus a final per-sample hard clip, guaranteeing the output never exceeds the ceiling), **echo / delay** (`EchoTransform`, a feedback delay line with a decaying repeat tail drained via `Flush`), **stereo pan/balance** (`PanTransform`, linear or equal-power), **channel remix** (`ChannelRemixTransform`, mono↔stereo and general N↔M), **bit-depth and float conversion** (`BitDepthFormatTransform` for 8/16/24/32-bit, `FloatSampleConverter` for int↔IEEE-float), **fades on a cut** (`FadeTransform` via `CutOptions`), **mixing / concatenation** (`AudioCutter.Mix`, `AudioCutter.Concatenate`), **parametric EQ filters** (`BiquadTransform` — RBJ Audio EQ Cookbook low/high pass, band pass, notch, all pass, peaking EQ, low/high shelf — and `ButterworthTransform` for steeper cascaded low/high pass), and **general FIR convolution** (`FirFilterTransform`, arbitrary caller-supplied taps). Full walkthrough and type reference: [Advanced Features](https://eggspot.github.io/EggEncoder/Advanced-Features.html#pcm-transform-pipeline) / [API Reference](https://eggspot.github.io/EggEncoder/API-Reference.html#pcm-transform-pipeline).

**Important limits:**
- A `PcmTransformPipeline` instance carries state across blocks (resampler history, fade position, measured peak gain, biquad/Butterworth/FIR filter history) — build a fresh one per `Convert`/`Cut`/`Mix` call, don't reuse across calls.
- A pipeline containing `ResamplingTransform` (or any stateful transform implementing `IPcmTransform.Flush`) needs `pipeline.Flush(...)` called once after the last block, to drain output the transform was still holding back — `AudioCutter.Convert(pipeline)`/`Cut(options)` already do this for you; only a driver written against `PcmTransformPipeline` directly needs to call it itself.
- True whole-file peak normalization needs `AudioCutter.MeasurePeakAmplitude` followed by `PeakNormalizationTransform.MeasurePeak` *before* the pipeline runs — otherwise it silently normalizes against only the first decode block.
- `Mix`/`Concatenate` require every source to share the same channels/sample rate/bit depth. `Mix` decodes all sources fully into memory (clip-length material, not multi-hour streams).
- `ResamplingTransform` is a Kaiser-windowed-sinc polyphase filter (anti-aliasing on downsample, band-limited reconstruction on upsample) — call `Flush()` (see above) to get its last few frames, which it can't produce until it either sees more input or is told there isn't any.

## Live Streaming (early)

A push-style, incremental HLS packaging pipeline, genuinely independent of the `IMediaEncoder` file/`Stream` surface above. `IStreamingEncoder` encodes PCM into frames via a callback; a fragmented MP4 (fMP4/CMAF) muxer or an MPEG-TS muxer packages frames into segments; `HlsSegmenter` decides segment boundaries and writes segments + an `.m3u8` playlist through an `IStreamingSink`, in either VOD mode (every segment stays in the playlist) or live mode (a bounded sliding window of the most recent segments, old ones reclaimed via the sink). A top-level master playlist (`M3u8MasterPlaylistWriter`) lists multiple audio-bitrate renditions for a player to choose from. `LiveAudioPump` feeds PCM from a genuinely live source (not neat, exactly-on-time blocks) into an encoder, with an optional silence-fill policy for real gaps in arrival. AAC-LC (`AacStreamingEncoder`), Opus (`OpusStreamingEncoder`), MP3 (`Mp3StreamingEncoder`), and FLAC (`FlacStreamingEncoder`) all implement `IStreamingEncoder`; the muxer/segmenter/playlist layers above are AAC-LC-only so far — see [docs/streaming-plan.md](docs/streaming-plan.md) for the full ordered plan and what's merged versus still open.

```csharp
using EggEncoder.Streaming;
using EggEncoder.Streaming.Aac;
using EggEncoder.Streaming.Hls;

Directory.CreateDirectory("hls-output");
var sink = new FileSystemStreamingSink("hls-output"); // or InMemoryStreamingSink / DelegateStreamingSink
var segmenter = new HlsSegmenter(channels: 1, sampleRate: 44100, targetDurationSeconds: 6, sink);
// or: new HlsSegmenter(1, 44100, 6, sink, SegmentFormat.MpegTs) for legacy .ts segments instead of fMP4

using var encoder = AacStreamingEncoder.Create(channels: 1, sampleRate: 44100, frame => segmenter.AddFrame(frame));

encoder.Write(pcmSamplesAsTheyArrive);
// ... more Write calls as PCM arrives, with no upper bound ...
encoder.Complete(); // flushes any partial trailing encoded frame
segmenter.Complete(); // flushes the final (possibly short) segment and writes #EXT-X-ENDLIST
```

For multiple bitrates, write each rendition's own media playlist with its own `HlsSegmenter`, then list them in one master playlist:

```csharp
using EggEncoder.Streaming.Hls;

var master = M3u8MasterPlaylistWriter.BuildMasterPlaylist([
    new HlsRenditionDescriptor("low/playlist.m3u8", BandwidthBitsPerSecond: 64_000, CodecsString: "mp4a.40.2"),
    new HlsRenditionDescriptor("high/playlist.m3u8", BandwidthBitsPerSecond: 128_000, CodecsString: "mp4a.40.2"),
]);
```

For a genuinely live source (a microphone callback, an incoming stream) rather than a file read on your own schedule, wrap the encoder in `LiveAudioPump`:

```csharp
var pump = new LiveAudioPump(encoder, DriftPolicy.SilenceFill); // or DriftPolicy.Ignore (the default) to disable gap detection

// Called by your own capture source, on its own schedule, as PCM becomes available:
pump.PushSamples(capturedSamples, DateTimeOffset.UtcNow);
```

`OpusStreamingEncoder` implements the same `IStreamingEncoder` interface, fixed at 48kHz:

```csharp
using EggEncoder.Streaming.Opus;

using var opusEncoder = OpusStreamingEncoder.Create(channels: 1, sampleRate: 48000, frame =>
{
    // frame.SampleCount is always 960 here -- Opus has no short-final-frame concept, unlike AAC-LC
});
```

`Mp3StreamingEncoder` is the third, emitting self-delimiting MPEG frames with no extra container framing needed at all:

```csharp
using EggEncoder.Streaming.Mp3;

using var mp3Encoder = Mp3StreamingEncoder.Create(channels: 1, sampleRate: 44100, frame =>
{
    // frame.SampleCount reports the real sample count, like AAC-LC -- except the mandatory flush
    // frame Complete() emits, which is purely synthetic and reports 0
}, bitRateKbps: 192);
```

`FlacStreamingEncoder` is the fourth and last, lossless, with no encoder lookahead at all -- but its raw frames, unlike the other three, are not independently decodable without a real STREAMINFO built out-of-band (a container-level concept this raw encoder deliberately leaves out of scope):

```csharp
using EggEncoder.Streaming.Flac;

using var flacEncoder = FlacStreamingEncoder.Create(channels: 1, sampleRate: 44100, bitsPerSample: 16, frame =>
{
    // frame.SampleCount reports the real sample count for every frame, including a genuinely
    // short final one -- FLAC needs no padding at all, unlike AAC-LC/Opus/MP3
});
```

### Media packet model (live ingest foundation)

`EggEncoder.Media` is the codec-agnostic, already-encoded packet model every future live source (RTMP ingest, FLV/MP4/MPEG-TS files) and output (RTMP push, HLS with video, recording) shares, so video can be remuxed without decoding: `MediaTrackInfo`/`MediaTrackSet` describe tracks (codec, timescale, codec configuration, a `ConfigVersion` that bumps on a mid-stream change), `MediaPacket` carries one access unit with PTS/DTS ticks and a keyframe flag, and `IMediaPacketSink` is the async output contract. Supporting pieces: `MediaTimestamp.Rescale` (128-bit, round-half-to-even timescale conversion), `AnnexBConverter` (H.264/H.265 length-prefixed ↔ Annex B start codes), `AvcDecoderConfigurationRecord`/`HevcDecoderConfigurationRecord` (avcC/hvcC parse + build, byte-exact round trip), `H264SpsParser`/`H265SpsParser` (profile, level, chroma format, bit depth and cropped size from an SPS, plus the RFC 6381 `CODECS` string), and `MediaPacketFactory.FromEncodedAudioFrame` so every `IStreamingEncoder` can feed it. Video is parsed for parameter-set headers only, never decoded.

```csharp
using EggEncoder.Media;

var avcC = AvcDecoderConfigurationRecord.Parse(avcCBytes);
var sps = H264SpsParser.Parse(avcC.SequenceParameterSets[0].Span); // 1920x1080, "avc1.640028"
var video = new MediaTrackInfo(1, MediaKind.Video, MediaCodec.H264, timescale: 1000, avcCBytes, configVersion: 0, width: sps.Width, height: sps.Height);
byte[] annexB = AnnexBConverter.ToAnnexB(packet.Payload.Span, avcC.NalLengthSize); // for MPEG-TS
long pts90k = MediaTimestamp.Rescale(packet.Pts, fromTimescale: 1000, toTimescale: 90000);
```

### FLV demux and mux

`EggEncoder.Containers.Flv` reads and writes FLV files (Adobe FLV spec v10.1 plus Enhanced FLV) at the packet level, so H.264/HEVC/AV1/VP9 video and AAC/MP3 audio are remuxed, never decoded. `FlvDemuxer` is incremental (`Feed` bytes in any chunk size, from a file, an HTTP body or a socket) and reports a `MediaTrackSet` once each track's sequence header arrives, then `MediaPacket`s with unwrapped 64-bit millisecond timestamps and the signed composition offset; a new sequence header bumps the track's `ConfigVersion`. `FlvFileSource` wraps it as `IAsyncEnumerable<MediaPacket> ReadPacketsAsync(CancellationToken)` over any readable `Stream` (no seeking needed). `FlvWriter` (an `IMediaPacketSink`, plus a synchronous `Open`/`Write`/`Complete` core) writes the header, `onMetaData`, a sequence header before each track's first frame and after each configuration change, legacy tags for H.264/AAC/MP3 and Enhanced-FLV FourCC tags (`hvc1`/`av01`/`vp09`) for the rest. `FlvMetadata` exposes `onMetaData` (duration, width, height, framerate, data rates, encoder). Unsupported codecs and stray tags are skipped and counted rather than failing the stream; a bad header, a `PreviousTagSize` mismatch, a malformed sequence header or a truncated tag throws `InvalidDataException`. The AMF0 codec behind `onMetaData` (`EggEncoder.Live.Rtmp.Amf0`: `Amf0Reader`, `Amf0Writer`, `Amf0Value`) is public too, with bounded nesting depth, string length and property counts.

```csharp
using EggEncoder.Containers.Flv;

await using var source = new FlvFileSource(File.OpenRead("in.flv"));
await using var writer = new FlvWriter(File.Create("out.flv"));
var opened = false;
await foreach (var packet in source.ReadPacketsAsync())
{
    if (!opened) { await writer.OpenAsync(source.Tracks!); opened = true; }
    await writer.WriteAsync(packet); // remux: no decode, no re-encode
}
await writer.CompleteAsync();
```

### Fragmented MP4 (fMP4/CMAF) with video

`FmpInitSegmentWriter.BuildInitSegment(MediaTrackSet)` and `new FmpMediaSegmentWriter(MediaTrackSet).BuildMediaSegment(packets, nextDecodeTimes)` write fMP4 init and media segments from packets: H.264 (`avc1`) and H.265 (`hvc1`) video passed through without decoding, next to AAC (mono or stereo). Per-sample durations come from the DTS deltas, video samples carry sync flags and signed composition offsets for B-frames, and each `tfdt` is the track's real first DTS. Pass the next DTS of a track in `nextDecodeTimes` when you know it (a segmenter cutting at the next keyframe does) to make that track's last duration exact. Timestamps must be non-negative and strictly increasing per track; nothing is rebased. Checked with `ffprobe` and an `ffmpeg` decode of a real H.264 + AAC stream. `HlsSegmenter` uses it for HLS with video (below).

```csharp
using EggEncoder.Streaming.Mp4;

var init = FmpInitSegmentWriter.BuildInitSegment(tracks);    // tracks: e.g. FlvFileSource.Tracks or an RtmpIngestStream's
var writer = new FmpMediaSegmentWriter(tracks);
var segment = writer.BuildMediaSegment(gopPackets, new Dictionary<int, long> { [videoTrackId] = nextKeyframe.Dts });
```

### MPEG-TS with video

`new MpegTsMuxer(MediaTrackSet).BuildSegment(packets)` writes MPEG-TS segments from packets: H.264 (`stream_type` 0x1B) and H.265 (0x24) video passed through without decoding, next to AAC as ADTS (0x0F). Each segment is a standalone `.ts` file starting with PAT and PMT; video access units are converted to Annex B with an access unit delimiter first and the track's SPS/PPS (VPS/SPS/PPS for H.265) in front of every keyframe that doesn't carry its own, and a PCR runs 700 ms behind the DTS (held at 0 until then) on the video PID, or on the audio PID when there is no video, with PCR-only packets bridging any gap of more than 100 ms (up to 5 s; a longer one is a timestamp jump). Packets are written in DTS order, timestamps are rescaled to 90 kHz but not rebased, and continuity counters run on from one segment to the next, so a playlist of segments reads as one stream. AAC must fit ADTS (object type 1-4, an indexed sample rate, channel configuration 1-7), so HE-AAC with explicit SBR signalling is rejected here although fMP4 accepts it. Checked with `ffprobe` and an `ffmpeg` decode of real H.264 + AAC and H.265 streams.

```csharp
using EggEncoder.Streaming.MpegTs;

var muxer = new MpegTsMuxer(tracks);       // tracks: e.g. FlvFileSource.Tracks or an RtmpIngestStream's
File.WriteAllBytes("segment0.ts", muxer.BuildSegment(gopPackets));
```


### HLS with video

`new HlsSegmenter(MediaTrackSet tracks, targetDurationSeconds, sink[, liveWindowSize][, SegmentFormat])` + `AddPacket(packet)` + `Complete()` packages a packet stream (an RTMP ingest, an FLV file) as HLS with H.264/H.265 video and AAC audio, without decoding, in fMP4 or MPEG-TS segments, VOD or live sliding window. Segments are cut at video keyframes once the target duration is reached (on every packet for audio-only streams), so each one starts decodable and runs at most one GOP past the target; `EXTINF` comes from the keyframes' PTS. Packets before the first keyframe are dropped and counted (`DroppedLeadingPacketCount`). `HlsMediaPacketSink` wraps it as an `IMediaPacketSink` (it builds the segmenter in `OpenAsync`), and `M3u8MasterPlaylistWriter.BuildCodecsString(tracks)` gives the master playlist's `CODECS` value from the SPS and AudioSpecificConfig (`"avc1.64001F,mp4a.40.2"`). A track's configuration can't change mid-stream yet ([live plan](docs/live-ingest-restream-plan.md) item 5d). Checked with `ffprobe` and an `ffmpeg` decode of the playlists made from a real H.264 + AAC stream.

```csharp
using EggEncoder.Streaming.Hls;

await using var hls = new HlsMediaPacketSink(new FileSystemStreamingSink("out"), targetDurationSeconds: 4);
await foreach (var packet in source.ReadPacketsAsync())   // source: e.g. an FlvFileSource or an RtmpIngestStream
{
    if (hls.Segmenter is null) await hls.OpenAsync(source.Tracks!);
    await hls.WriteAsync(packet);
}
await hls.CompleteAsync();
```

### RTMP protocol core

`EggEncoder.Live.Rtmp` implements RTMP below the "publish a stream" level (Adobe RTMP specification 1.0), pure and synchronous over bytes so the coming server and client share it: `RtmpHandshake` (the simple version-3 handshake as a byte-level state machine for either side; RTMPE is rejected), `RtmpChunkReader` (incremental `Feed`, reassembles messages across interleaved chunk streams, every header type, extended timestamps, applies Set Chunk Size/Abort/Window Acknowledgement Size, says when an Acknowledgement is due, and `Complete()` on connection close reports a chunk or message cut short), `RtmpChunkWriter` (splits messages with the most compact header), `RtmpProtocolControl`/`RtmpUserControl` (control messages and events), `RtmpCommand` (`connect`, `createStream`, `releaseStream`, `FCPublish`, `publish`, `play`, `deleteStream`, `_result`/`_error`, `onStatus`) and `RtmpDataMessage` (`@setDataFrame`/`onMetaData` to `FlvMetadata`). Every bound from the plan's security posture is enforced before allocating (`RtmpChunkReaderOptions`: chunk size, message length, chunk stream count, buffered bytes, each with a hard ceiling), and every malformed input (truncation included, once `Complete()` is called) ends in a typed `RtmpProtocolException`. Verified against a real ffmpeg publish captured as wire bytes.

```csharp
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;

var reader = new RtmpChunkReader(message =>
{
    if (message.Type == RtmpMessageType.CommandAmf0)
        Console.WriteLine(RtmpCommand.Decode(message.Payload.Span).Name); // connect, releaseStream, FCPublish, createStream, publish
    else if (message.Type == RtmpMessageType.Video)
        Console.WriteLine(FlvTagBodyParser.ParseVideo(message.Payload.Span).PacketType); // an RTMP media body is an FLV tag body
});
reader.Feed(bytesFromSocket); // any piece size, after RtmpHandshake completes
```

### RTMP server (ingest)

`RtmpServer` accepts publishes from OBS, Streamlabs, vMix, ffmpeg or a hardware encoder at `rtmp://host:port/app/streamKey` (RTMPS with a certificate) and hands you each one as an `RtmpIngestStream` of packets: H.264/H.265 and AAC exactly as sent, ready to remux without decoding. Closed by default: it listens on `127.0.0.1` only and refuses every publish until `AuthorizePublishAsync` says yes; one publisher per stream key; chunk/message/AMF0/name bounds; timeouts on the handshake, on reaching `publish`, on an idle publisher and on a consumer that stops reading; a bounded packet queue with TCP backpressure (nothing dropped). `stream.Disconnect()` kicks a publisher. Verified against a real ffmpeg publish, live and as a replayed wire capture.

```csharp
using System.Net;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.Media;

await using var server = new RtmpServer(new RtmpServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 1935), // opt in to every interface
    AuthorizePublishAsync = (request, _) => ValueTask.FromResult(request.AppName == "live" && request.StreamKey == secretKey)
});
server.PublishStarted += (_, stream) => _ = Task.Run(async () =>
{
    // Record the publish to FLV; stream.Tracks describes the packet just read. App names and stream
    // keys come from the publisher and aren't path-safe, so don't build file paths from them.
    await using var recording = new FlvWriter(File.Create($"recording-{DateTime.UtcNow:yyyyMMdd-HHmmss}.flv"));
    MediaTrackSet? tracks = null;
    await foreach (var packet in stream.ReadPacketsAsync())
    {
        if (!ReferenceEquals(tracks, stream.Tracks))
        {
            if (tracks is null) await recording.OpenAsync(stream.Tracks!);
            else recording.UpdateTracks(stream.Tracks!);
            tracks = stream.Tracks;
        }

        await recording.WriteAsync(packet);
    }

    if (tracks is not null) await recording.CompleteAsync();
});
await server.StartAsync(); // OBS: Server rtmp://this-host/live, Stream Key secretKey
```

**Planned:** the rest of live ingest and restream — RTMP/RTMPS push to YouTube/Twitch/any server, video passthrough into HLS playlists (fMP4 and MPEG-TS segments with video are done, above), and one-input-to-many-outputs restreaming, all pure managed. Not shipped yet; see [docs/live-ingest-restream-plan.md](docs/live-ingest-restream-plan.md) for the plan and its status.

## Supported Formats

| Format | Probe | Decode | Encode |
|--------|:---:|:---:|:---:|
| WAV    | ✅ | ✅⁶ ¹¹ | ✅⁶ ⁷ ¹¹ |
| AIFF   | ✅ | ✅⁹ | ✅⁹ |
| AU (.au) | ✅ | ✅ | ✅¹⁰ |
| ALAC (.caf) | ✅ | ✅ | ✅² |
| TTA    | ✅ | ✅ | ✅² |
| WavPack (.wv) | ✅ | ✅ | ✅⁵ |
| Opus (.opus) | ✅ | ✅ | ✅³ |
| Vorbis (.ogg) | ✅ | ✅ | ✅⁴ |
| FLAC   | ✅ | ✅ | ✅ |
| MP3    | ✅ | ✅ | ✅ |
| AAC    | ✅ | ✅ | ✅ |
| WMA    | ✅ | ✅ | ✅ |
| MOV/MP4 | ✅ | ✅¹ | ❌ |

¹ MOV/MP4 decode is audio-only, mono AAC-LC tracks — video frames are never decoded. Files without a matching audio track still probe fine (metadata only).

² ALAC supports 16-bit and 24-bit PCM for decode and encode (20-bit is out of scope — see `AlacDecoder`'s doc comment); TTA is 16-bit PCM only (see `TtaDecoder`'s doc comment). Mono and stereo are both supported by both.

³ Opus is lossy and fixed at 48kHz (Opus's native/highest internal rate) regardless of the source's own rate — resample first via `ResamplingTransform` if it isn't already 48kHz. Mono and stereo, 16-bit PCM, channel mapping family 0 only.

⁴ Vorbis is lossy, mono/stereo, 16-bit PCM — unlike Opus, any sample rate is supported (no fixed-rate resampling requirement).

⁵ WavPack supports 16-bit and 24-bit lossless integer PCM (its own lossy/hybrid and floating-point modes are out of scope). Mono and stereo only. Both WavPack *decode* and *encode* are pure managed implementations (clean-room from the WavPack 4/5 format spec plus, for the codec algorithm that spec doesn't itself document, an owner-approved arm's-length study of FFmpeg's own independently-written decoder — see THIRD-PARTY-NOTICES.md); no native binary is involved for this codec any more. The encoder uses a fixed 5-term decorrelation cascade (matching the real reference encoder's own simplest term set), independent-channel stereo, not yet compression-competitive; its output round-trips exactly through this project's own decoder, but has not yet achieved full compatibility with the real reference `wvunpack` CLI for arbitrary content. Also unlike every other codec here, WavPack cannot represent an empty/zero-sample stream at all (confirmed from its own reference CLI, which refuses to encode one) — encoding one throws `NotSupportedException` rather than producing a file.

⁶ `WavReader` also decodes IMA ADPCM (`WAVE_FORMAT_IMA_ADPCM`, format tag 17) and MS ADPCM (`WAVE_FORMAT_ADPCM`, format tag 2, see footnote 8) — still a `.wav` file, just a different `fmt` chunk codec, so it's read automatically by `Probe`/`Convert`/`Cut`/pipeline sources with no extra API. Mono and stereo only; reports as 16-bit PCM once decoded (the coded width is 4 bits for both). `WavWriter` also encodes both (block-structured, buffered internally rather than one sample at a time — see `WavSampleFormat.ImaAdpcm`/`ImaAdpcmEncoder` and `WavSampleFormat.MsAdpcm`/`MsAdpcmEncoder`), selectable the same way `Float32`/`MuLaw`/`ALaw` are.

⁷ `WavReader`/`WavWriter` also decode/encode G.711 companded PCM (`WAVE_FORMAT_ALAW`/`WAVE_FORMAT_MULAW`, format tags 6/7) — again still a `.wav` file, read/written automatically with no extra API beyond `WavSampleFormat.ALaw`/`MuLaw` as a `Convert`/`Cut`/`Mix`/`Concatenate` destination, the same way `Float32` works. Any channel count (G.711 has no structural reason to limit it, unlike every other codec here); reports/requires 16-bit PCM at the boundary (the coded width is 8 bits). Unlike IMA ADPCM's own block-structured encode, G.711 has no block structure or adaptive state at all, so its own encode is a simple one-sample-at-a-time companding formula.

⁸ MS ADPCM (`WAVE_FORMAT_ADPCM`, format tag 2), mono and stereo only — a genuinely different algorithm from IMA ADPCM (linear prediction from a per-file coefficient table carried in the `fmt` chunk itself, rather than IMA ADPCM's universal fixed step table), so it's its own decoder and encoder, not a variant of `ImaAdpcmDecoder`/`ImaAdpcmEncoder`. `MsAdpcmEncoder` always selects the standard table's simplest coefficient pair (predictor index 0) for every block — confirmed from FFmpeg's own real encoder that this, not a per-block search over the other 6 standard pairs, is what real-world encoders actually ship — and always writes that full 7-pair standard table into the file's own `fmt` chunk extension regardless.

⁹ `AiffReader`/`AiffWriter` also handle AIFC (FORM/AIFC), still under the `.aiff`/`.aif`/`.aifc` extensions — no separate dispatch, `AiffReader` just understands the AIFC form type's extra `compressionType` field in its COMM chunk. Covers `NONE`/`twos` (big-endian PCM, the same as plain AIFF), `sowt` (little-endian PCM), `fl32`/`fl64` (big-endian IEEE float, decoded at this codebase's usual int32-native-range scale — both report 32-bit PCM resolution), and `alaw`/`ulaw` (G.711, reusing the same `G711Codec` the WAV side uses, not a second implementation) — all read AND write, selectable on write via `AiffSampleFormat` (see `AudioCutter.Convert(..., AiffSampleFormat)`/`CutOptions.DestinationAiffFormat`). `ima4` (QuickTime IMA4 ADPCM, decode only) is also covered — a materially different bitstream from WAV's own IMA ADPCM (no verbatim first sample per block, and for stereo each channel's own 34-byte sub-block is written whole rather than nibble-interleaved), though the underlying per-nibble math is identical and reused directly from `Wav.ImaAdpcmDecoder` — see `Ima4Decoder`.

¹⁰ AU (Sun/NeXT, magic `.snd`) has no chunk structure at all — one fixed 24-byte header, optionally followed by an annotation string, then raw big-endian samples with no byte-alignment padding. Covers 8/16/24/32-bit signed integer PCM, 32/64-bit IEEE float (both reporting 32-bit PCM resolution, the same reasoning as AIFC's own `fl64`), and mu-law/A-law G.711 (again reusing `G711Codec`) — all read AND write, selectable on write via `AuSampleFormat` (see `AudioCutter.Convert(..., AuSampleFormat)`/`CutOptions.DestinationAuFormat`). Every other defined AU encoding (G.721/G.722/G.723 ADPCM, fragmented samples) is real but obscure and out of scope, the same way AIFC's `MAC3`/`MAC6` (MACE) are.

¹¹ Yamaha ADPCM (`WAVE_FORMAT_YAMAHA_ADPCM`, format tag 32), mono and stereo only, full decode AND encode (`YamahaAdpcmDecoder`/`YamahaAdpcmEncoder`) — still a `.wav` file, read automatically by `Probe`/`Convert`/`Cut`/pipeline sources with no extra API, selectable on write via `WavSampleFormat.YamahaAdpcm`. Unlike IMA/MS ADPCM, it has no block structure at all — no per-block header, no `wSamplesPerBlock` `fmt` chunk extension — each channel's predictor/step state just carries continuously across the whole stream. Like MS ADPCM's own encoder (and unlike IMA ADPCM's own search), its nibble is computed via a direct closed-form formula, confirmed structurally identical to FFmpeg's own real `adpcm_yamaha_compress_sample`.

`IMediaEncoder.CutFile` decodes any supported source (WAV, AIFF, AU, ALAC, TTA, WavPack, Opus, Vorbis, FLAC, MP3, AAC, WMA, and MOV/MP4 files with a mono AAC-LC audio track) and can cut into any supported destination format, including converting as it trims — sample-accurate, no re-encode of the untouched region.

WAV supports 8-bit unsigned, 16/24/32-bit signed integer, and 32-bit IEEE float PCM (read and write), plus IMA ADPCM decode+encode, MS ADPCM decode+encode, Yamaha ADPCM decode+encode, and G.711 mu-law/A-law decode+encode (see footnotes 6/7/8/11 above). AIFF (`.aiff`/`.aif`/`.aifc`) supports 8/16/24/32-bit signed integer PCM, read and write (plain FORM/AIFF), plus AIFC (FORM/AIFC) read and write for `NONE`/`twos`/`sowt` integer PCM, `fl32`/`fl64` float, and `alaw`/`ulaw` G.711, plus `ima4` (QuickTime IMA4 ADPCM) decode (see footnote 9). AU (`.au`) supports 8/16/24/32-bit signed integer PCM, 32/64-bit float, and mu-law/A-law G.711, all read and write (see footnote 10). ALAC (`.caf`, Apple Lossless in a CAF container) supports mono and stereo, 16-bit or 24-bit integer PCM, read and write. TTA (`.tta`, True Audio) supports mono and stereo, 16-bit integer PCM, read and write. WavPack (`.wv`) supports mono and stereo, 16-bit or 24-bit lossless integer PCM, read and write. Opus (`.opus`, in a from-scratch OggOpus container) supports mono and stereo, 16-bit integer PCM at a fixed 48kHz, read and write. Vorbis (`.ogg`) supports mono and stereo, 16-bit integer PCM at any sample rate, read and write. A float WAV *source* always decodes transparently into int PCM, the same as any other bit depth. For a float, mu-law, A-law, IMA ADPCM, MS ADPCM, or Yamaha ADPCM WAV *destination*, pass `WavSampleFormat.Float32`/`MuLaw`/`ALaw`/`ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` to `AudioCutter.Convert`/`Cut` (via `CutOptions.DestinationWavFormat`)/`Mix`/`Concatenate` — the default (`WavSampleFormat.Integer`) is unchanged; `Float32` requires the destination's bit depth to already be 32, `MuLaw`/`ALaw`/`ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` require 16 (widen/narrow with `BitDepthFormatTransform` first if needed), and `ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` additionally require mono or stereo. `AudioCutter.ReadWavAsFloat`/`WriteWavFromFloat`/`FloatSampleConverter` remain available for working with `float[]` directly instead of driving int PCM through a pipeline.

## License

MIT — see [LICENSE](LICENSE). EggEncoder bundles no native binaries of any kind; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the licenses of its pure managed codec
dependencies.
