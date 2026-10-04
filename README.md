# 🥚 EggEncoder

> **Audio encoding/decoding toolkit for .NET** — 11 formats (AAC, AIFF, ALAC, FLAC, MP3, Opus, TTA, Vorbis, WAV incl. IMA ADPCM/G.711, WavPack, WMA) plus MOV/MP4 probing, mostly pure C# alongside native MP3/FLAC/WavPack bindings, built-in waveform generation, and an opt-in PCM transform pipeline (resampling, gain/peak normalization, channel remix, fades, parametric EQ, FIR filtering, mixing), all behind one `IMediaEncoder` interface.

Sponsored by [eggspot.app](https://eggspot.app)

[![CI](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml/badge.svg)](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EggEncoder.svg)](https://www.nuget.org/packages/EggEncoder)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

📖 **[Full documentation →](https://eggspot.github.io/EggEncoder/)**

## Overview

EggEncoder gives you a single `IMediaEncoder` abstraction — `Probe`, `ConvertFile`, `CutFile` — implemented entirely in-process by `NativeEncoder`: pure .NET codec implementations (AAC, WAV, AIFF, ALAC, TTA, WMA, Opus, Vorbis) plus native P/Invoke bindings to `libmp3lame`, `libFLAC`, and `wavpackdll`. No external process, no ffmpeg install, no subprocess overhead.

### Why EggEncoder?

- 🚀 **Fully native, in-process** — direct P/Invoke to LAME (MP3), libFLAC, and WavPack, no subprocess/shell-out overhead
- ❄️ **Native AOT compatible** — no reflection, no dynamic code; publish with `PublishAot=true` and it just works
- 🎼 **Broad format coverage** — AAC, AIFF, ALAC, FLAC, MP3, Opus, TTA, Vorbis, WAV, WavPack, WMA decode/encode; MOV/MP4 metadata probing + mono AAC-LC audio decode
- 📊 **Built-in waveform generation** — normalized peak windows for any decoded stream
- ✂️ **Sample-accurate cutting** — trim audio files without a full decode→encode round trip
- 🎛️ **PCM transform pipeline** — resampling, gain/peak normalization, channel remix, bit-depth/float conversion, fades, parametric EQ (biquad + Butterworth) and general FIR filtering, mixing, and concatenation — opt-in, composable, and layered onto `Convert`/`Cut` without touching the original API
- 🪶 **Dependency-light** — only `Microsoft.Extensions.*.Abstractions` and `NLayer`
- 📖 **MIT licensed** — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the bundled native codec licenses (LGPL-2.1 LAME, BSD-style libFLAC, BSD-style WavPack)

## Installation

```bash
dotnet add package EggEncoder
```

Native codec binaries (`libmp3lame.dll`, `libFLAC.dll`, `wavpackdll.dll`) ship inside the package for `win-x64` and are copied to your output directory automatically.

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

Covers **resampling** (`ResamplingTransform`), **gain / peak normalization** (`VolumeTransform` / `PeakNormalizationTransform`), **channel remix** (`ChannelRemixTransform`, mono↔stereo and general N↔M), **bit-depth and float conversion** (`BitDepthFormatTransform` for 8/16/24/32-bit, `FloatSampleConverter` for int↔IEEE-float), **fades on a cut** (`FadeTransform` via `CutOptions`), **mixing / concatenation** (`AudioCutter.Mix`, `AudioCutter.Concatenate`), **parametric EQ filters** (`BiquadTransform` — RBJ Audio EQ Cookbook low/high pass, band pass, notch, all pass, peaking EQ, low/high shelf — and `ButterworthTransform` for steeper cascaded low/high pass), and **general FIR convolution** (`FirFilterTransform`, arbitrary caller-supplied taps). Full walkthrough and type reference: [Advanced Features](https://eggspot.github.io/EggEncoder/Advanced-Features.html#pcm-transform-pipeline) / [API Reference](https://eggspot.github.io/EggEncoder/API-Reference.html#pcm-transform-pipeline).

**Important limits:**
- A `PcmTransformPipeline` instance carries state across blocks (resampler history, fade position, measured peak gain, biquad/Butterworth/FIR filter history) — build a fresh one per `Convert`/`Cut`/`Mix` call, don't reuse across calls.
- A pipeline containing `ResamplingTransform` (or any stateful transform implementing `IPcmTransform.Flush`) needs `pipeline.Flush(...)` called once after the last block, to drain output the transform was still holding back — `AudioCutter.Convert(pipeline)`/`Cut(options)` already do this for you; only a driver written against `PcmTransformPipeline` directly needs to call it itself.
- True whole-file peak normalization needs `AudioCutter.MeasurePeakAmplitude` followed by `PeakNormalizationTransform.MeasurePeak` *before* the pipeline runs — otherwise it silently normalizes against only the first decode block.
- `Mix`/`Concatenate` require every source to share the same channels/sample rate/bit depth. `Mix` decodes all sources fully into memory (clip-length material, not multi-hour streams).
- `ResamplingTransform` is a Kaiser-windowed-sinc polyphase filter (anti-aliasing on downsample, band-limited reconstruction on upsample) — call `Flush()` (see above) to get its last few frames, which it can't produce until it either sees more input or is told there isn't any.

## Supported Formats

| Format | Probe | Decode | Encode |
|--------|:---:|:---:|:---:|
| WAV    | ✅ | ✅⁶ | ✅⁷ |
| AIFF   | ✅ | ✅ | ✅ |
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

⁵ WavPack supports 16-bit and 24-bit lossless integer PCM (its own lossy/hybrid and floating-point modes are out of scope). Mono and stereo only. Unlike every other codec here, WavPack decode/encode is via a native binary (`wavpackdll.dll`, the official WavPack project's own prebuilt library) rather than a pure-managed implementation — no pure-managed WavPack decoder/encoder exists. Also unlike every other codec here, WavPack cannot represent an empty/zero-sample stream at all (confirmed from its own reference CLI, which refuses to encode one) — encoding one throws `NotSupportedException` rather than producing a file.

⁶ `WavReader` also decodes IMA ADPCM (`WAVE_FORMAT_IMA_ADPCM`, format tag 17) — still a `.wav` file, just a different `fmt` chunk codec, so it's read automatically by `Probe`/`Convert`/`Cut`/pipeline sources with no extra API. Mono and stereo only; reports as 16-bit PCM once decoded (the coded width is 4 bits). Decode only — `WavWriter` does not encode ADPCM.

⁷ `WavReader`/`WavWriter` also decode/encode G.711 companded PCM (`WAVE_FORMAT_ALAW`/`WAVE_FORMAT_MULAW`, format tags 6/7) — again still a `.wav` file, read/written automatically with no extra API beyond `WavSampleFormat.ALaw`/`MuLaw` as a `Convert`/`Cut`/`Mix`/`Concatenate` destination, the same way `Float32` works. Any channel count (G.711 has no structural reason to limit it, unlike every other codec here); reports/requires 16-bit PCM at the boundary (the coded width is 8 bits). Unlike IMA ADPCM, G.711 has no block structure or adaptive state, so both directions are fully supported.

`IMediaEncoder.CutFile` decodes any supported source (WAV, AIFF, ALAC, TTA, WavPack, Opus, Vorbis, FLAC, MP3, AAC, WMA, and MOV/MP4 files with a mono AAC-LC audio track) and can cut into any supported destination format, including converting as it trims — sample-accurate, no re-encode of the untouched region.

WAV supports 8-bit unsigned, 16/24/32-bit signed integer, and 32-bit IEEE float PCM (read and write), plus IMA ADPCM decode and G.711 mu-law/A-law decode+encode (see footnotes 6/7 above). AIFF (`.aiff`/`.aif`) supports 8/16/24/32-bit signed integer PCM, read and write — plain AIFF (FORM/COMM/SSND) only, not the AIFC compressed/float variant. ALAC (`.caf`, Apple Lossless in a CAF container) supports mono and stereo, 16-bit or 24-bit integer PCM, read and write. TTA (`.tta`, True Audio) supports mono and stereo, 16-bit integer PCM, read and write. WavPack (`.wv`) supports mono and stereo, 16-bit or 24-bit lossless integer PCM, read and write. Opus (`.opus`, in a from-scratch OggOpus container) supports mono and stereo, 16-bit integer PCM at a fixed 48kHz, read and write. Vorbis (`.ogg`) supports mono and stereo, 16-bit integer PCM at any sample rate, read and write. A float WAV *source* always decodes transparently into int PCM, the same as any other bit depth. For a float, mu-law, or A-law WAV *destination*, pass `WavSampleFormat.Float32`/`MuLaw`/`ALaw` to `AudioCutter.Convert`/`Cut` (via `CutOptions.DestinationWavFormat`)/`Mix`/`Concatenate` — the default (`WavSampleFormat.Integer`) is unchanged; `Float32` requires the destination's bit depth to already be 32, `MuLaw`/`ALaw` require 16 (widen/narrow with `BitDepthFormatTransform` first if needed). `AudioCutter.ReadWavAsFloat`/`WriteWavFromFloat`/`FloatSampleConverter` remain available for working with `float[]` directly instead of driving int PCM through a pipeline.

## License

MIT — see [LICENSE](LICENSE). EggEncoder bundles pre-built `libmp3lame.dll` (LGPL-2.1),
`libFLAC.dll` (BSD-style), and `wavpackdll.dll` (BSD-style) as separate, dynamically-loaded
native binaries; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
