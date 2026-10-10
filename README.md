# 🥚 EggEncoder

> **Audio encoding/decoding toolkit for .NET** — 12 formats (AAC, AIFF incl. AIFC, ALAC, AU, FLAC, MP3, Opus, TTA, Vorbis, WAV incl. IMA/MS/Yamaha ADPCM and G.711, WavPack, WMA) plus MOV/MP4 probing, mostly pure C# alongside native MP3/FLAC/WavPack bindings, built-in waveform generation, and an opt-in PCM transform pipeline (resampling, gain/peak normalization, dynamics compression, noise gating, peak limiting, echo/delay, pan/balance, channel remix, fades, parametric EQ, FIR filtering, mixing), all behind one `IMediaEncoder` interface.

Sponsored by [eggspot.app](https://eggspot.app)

[![CI](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml/badge.svg)](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EggEncoder.svg)](https://www.nuget.org/packages/EggEncoder)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

📖 **[Full documentation →](https://eggspot.github.io/EggEncoder/)**

## Overview

EggEncoder gives you a single `IMediaEncoder` abstraction — `Probe`, `ConvertFile`, `CutFile` — implemented entirely in-process by `NativeEncoder`: pure .NET codec implementations (AAC, WAV, AIFF, AU, ALAC, TTA, WMA, Opus, Vorbis) plus native P/Invoke bindings to `libmp3lame`, `libFLAC`, and `wavpackdll`. No external process, no ffmpeg install, no subprocess overhead.

### Why EggEncoder?

- 🚀 **Fully native, in-process** — direct P/Invoke to LAME (MP3), libFLAC, and WavPack, no subprocess/shell-out overhead
- ❄️ **Native AOT compatible** — no reflection, no dynamic code; publish with `PublishAot=true` and it just works
- 🎼 **Broad format coverage** — AAC, AIFF (incl. AIFC), ALAC, AU, FLAC, MP3, Opus, TTA, Vorbis, WAV, WavPack, WMA decode/encode; MOV/MP4 metadata probing + mono AAC-LC audio decode
- 📊 **Built-in waveform generation** — normalized peak windows for any decoded stream
- 🏷️ **WAV metadata tags** — `Probe` surfaces a WAV file's `'LIST'`/`'INFO'` chunk (title, artist, etc.) via `ProbeResult.Tags`
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

Covers **resampling** (`ResamplingTransform`), **gain / peak normalization** (`VolumeTransform` / `PeakNormalizationTransform`), **dynamics compression** (`CompressorTransform`, threshold/ratio/attack/release/makeup gain), **noise gating** (`NoiseGateTransform`, the mirror-image downward expander below threshold), **peak limiting** (`LimiterTransform`, infinite-ratio compression plus a final per-sample hard clip, guaranteeing the output never exceeds the ceiling), **echo / delay** (`EchoTransform`, a feedback delay line with a decaying repeat tail drained via `Flush`), **stereo pan/balance** (`PanTransform`, linear or equal-power), **channel remix** (`ChannelRemixTransform`, mono↔stereo and general N↔M), **bit-depth and float conversion** (`BitDepthFormatTransform` for 8/16/24/32-bit, `FloatSampleConverter` for int↔IEEE-float), **fades on a cut** (`FadeTransform` via `CutOptions`), **mixing / concatenation** (`AudioCutter.Mix`, `AudioCutter.Concatenate`), **parametric EQ filters** (`BiquadTransform` — RBJ Audio EQ Cookbook low/high pass, band pass, notch, all pass, peaking EQ, low/high shelf — and `ButterworthTransform` for steeper cascaded low/high pass), and **general FIR convolution** (`FirFilterTransform`, arbitrary caller-supplied taps). Full walkthrough and type reference: [Advanced Features](https://eggspot.github.io/EggEncoder/Advanced-Features.html#pcm-transform-pipeline) / [API Reference](https://eggspot.github.io/EggEncoder/API-Reference.html#pcm-transform-pipeline).

**Important limits:**
- A `PcmTransformPipeline` instance carries state across blocks (resampler history, fade position, measured peak gain, biquad/Butterworth/FIR filter history) — build a fresh one per `Convert`/`Cut`/`Mix` call, don't reuse across calls.
- A pipeline containing `ResamplingTransform` (or any stateful transform implementing `IPcmTransform.Flush`) needs `pipeline.Flush(...)` called once after the last block, to drain output the transform was still holding back — `AudioCutter.Convert(pipeline)`/`Cut(options)` already do this for you; only a driver written against `PcmTransformPipeline` directly needs to call it itself.
- True whole-file peak normalization needs `AudioCutter.MeasurePeakAmplitude` followed by `PeakNormalizationTransform.MeasurePeak` *before* the pipeline runs — otherwise it silently normalizes against only the first decode block.
- `Mix`/`Concatenate` require every source to share the same channels/sample rate/bit depth. `Mix` decodes all sources fully into memory (clip-length material, not multi-hour streams).
- `ResamplingTransform` is a Kaiser-windowed-sinc polyphase filter (anti-aliasing on downsample, band-limited reconstruction on upsample) — call `Flush()` (see above) to get its last few frames, which it can't produce until it either sees more input or is told there isn't any.

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

⁵ WavPack supports 16-bit and 24-bit lossless integer PCM (its own lossy/hybrid and floating-point modes are out of scope). Mono and stereo only. Unlike every other codec here, WavPack decode/encode is via a native binary (`wavpackdll.dll`, the official WavPack project's own prebuilt library) rather than a pure-managed implementation — no pure-managed WavPack decoder/encoder exists. Also unlike every other codec here, WavPack cannot represent an empty/zero-sample stream at all (confirmed from its own reference CLI, which refuses to encode one) — encoding one throws `NotSupportedException` rather than producing a file.

⁶ `WavReader` also decodes IMA ADPCM (`WAVE_FORMAT_IMA_ADPCM`, format tag 17) and MS ADPCM (`WAVE_FORMAT_ADPCM`, format tag 2, see footnote 8) — still a `.wav` file, just a different `fmt` chunk codec, so it's read automatically by `Probe`/`Convert`/`Cut`/pipeline sources with no extra API. Mono and stereo only; reports as 16-bit PCM once decoded (the coded width is 4 bits for both). `WavWriter` also encodes both (block-structured, buffered internally rather than one sample at a time — see `WavSampleFormat.ImaAdpcm`/`ImaAdpcmEncoder` and `WavSampleFormat.MsAdpcm`/`MsAdpcmEncoder`), selectable the same way `Float32`/`MuLaw`/`ALaw` are.

⁷ `WavReader`/`WavWriter` also decode/encode G.711 companded PCM (`WAVE_FORMAT_ALAW`/`WAVE_FORMAT_MULAW`, format tags 6/7) — again still a `.wav` file, read/written automatically with no extra API beyond `WavSampleFormat.ALaw`/`MuLaw` as a `Convert`/`Cut`/`Mix`/`Concatenate` destination, the same way `Float32` works. Any channel count (G.711 has no structural reason to limit it, unlike every other codec here); reports/requires 16-bit PCM at the boundary (the coded width is 8 bits). Unlike IMA ADPCM's own block-structured encode, G.711 has no block structure or adaptive state at all, so its own encode is a simple one-sample-at-a-time companding formula.

⁸ MS ADPCM (`WAVE_FORMAT_ADPCM`, format tag 2), mono and stereo only — a genuinely different algorithm from IMA ADPCM (linear prediction from a per-file coefficient table carried in the `fmt` chunk itself, rather than IMA ADPCM's universal fixed step table), so it's its own decoder and encoder, not a variant of `ImaAdpcmDecoder`/`ImaAdpcmEncoder`. `MsAdpcmEncoder` always selects the standard table's simplest coefficient pair (predictor index 0) for every block — confirmed from FFmpeg's own real encoder that this, not a per-block search over the other 6 standard pairs, is what real-world encoders actually ship — and always writes that full 7-pair standard table into the file's own `fmt` chunk extension regardless.

⁹ `AiffReader`/`AiffWriter` also handle AIFC (FORM/AIFC), still under the `.aiff`/`.aif`/`.aifc` extensions — no separate dispatch, `AiffReader` just understands the AIFC form type's extra `compressionType` field in its COMM chunk. Covers `NONE`/`twos` (big-endian PCM, the same as plain AIFF), `sowt` (little-endian PCM), `fl32`/`fl64` (big-endian IEEE float, decoded at this codebase's usual int32-native-range scale — both report 32-bit PCM resolution), and `alaw`/`ulaw` (G.711, reusing the same `G711Codec` the WAV side uses, not a second implementation) — all read AND write, selectable on write via `AiffSampleFormat` (see `AudioCutter.Convert(..., AiffSampleFormat)`/`CutOptions.DestinationAiffFormat`). `ima4` (QuickTime IMA4 ADPCM, decode only) is also covered — a materially different bitstream from WAV's own IMA ADPCM (no verbatim first sample per block, and for stereo each channel's own 34-byte sub-block is written whole rather than nibble-interleaved), though the underlying per-nibble math is identical and reused directly from `Wav.ImaAdpcmDecoder` — see `Ima4Decoder`.

¹⁰ AU (Sun/NeXT, magic `.snd`) has no chunk structure at all — one fixed 24-byte header, optionally followed by an annotation string, then raw big-endian samples with no byte-alignment padding. Covers 8/16/24/32-bit signed integer PCM, 32/64-bit IEEE float (both reporting 32-bit PCM resolution, the same reasoning as AIFC's own `fl64`), and mu-law/A-law G.711 (again reusing `G711Codec`) — all read AND write, selectable on write via `AuSampleFormat` (see `AudioCutter.Convert(..., AuSampleFormat)`/`CutOptions.DestinationAuFormat`). Every other defined AU encoding (G.721/G.722/G.723 ADPCM, fragmented samples) is real but obscure and out of scope, the same way AIFC's `MAC3`/`MAC6` (MACE) are.

¹¹ Yamaha ADPCM (`WAVE_FORMAT_YAMAHA_ADPCM`, format tag 32), mono and stereo only, full decode AND encode (`YamahaAdpcmDecoder`/`YamahaAdpcmEncoder`) — still a `.wav` file, read automatically by `Probe`/`Convert`/`Cut`/pipeline sources with no extra API, selectable on write via `WavSampleFormat.YamahaAdpcm`. Unlike IMA/MS ADPCM, it has no block structure at all — no per-block header, no `wSamplesPerBlock` `fmt` chunk extension — each channel's predictor/step state just carries continuously across the whole stream. Like MS ADPCM's own encoder (and unlike IMA ADPCM's own search), its nibble is computed via a direct closed-form formula, confirmed structurally identical to FFmpeg's own real `adpcm_yamaha_compress_sample`.

`IMediaEncoder.CutFile` decodes any supported source (WAV, AIFF, AU, ALAC, TTA, WavPack, Opus, Vorbis, FLAC, MP3, AAC, WMA, and MOV/MP4 files with a mono AAC-LC audio track) and can cut into any supported destination format, including converting as it trims — sample-accurate, no re-encode of the untouched region.

WAV supports 8-bit unsigned, 16/24/32-bit signed integer, and 32-bit IEEE float PCM (read and write), plus IMA ADPCM decode+encode, MS ADPCM decode+encode, Yamaha ADPCM decode+encode, and G.711 mu-law/A-law decode+encode (see footnotes 6/7/8/11 above). AIFF (`.aiff`/`.aif`/`.aifc`) supports 8/16/24/32-bit signed integer PCM, read and write (plain FORM/AIFF), plus AIFC (FORM/AIFC) read and write for `NONE`/`twos`/`sowt` integer PCM, `fl32`/`fl64` float, and `alaw`/`ulaw` G.711, plus `ima4` (QuickTime IMA4 ADPCM) decode (see footnote 9). AU (`.au`) supports 8/16/24/32-bit signed integer PCM, 32/64-bit float, and mu-law/A-law G.711, all read and write (see footnote 10). ALAC (`.caf`, Apple Lossless in a CAF container) supports mono and stereo, 16-bit or 24-bit integer PCM, read and write. TTA (`.tta`, True Audio) supports mono and stereo, 16-bit integer PCM, read and write. WavPack (`.wv`) supports mono and stereo, 16-bit or 24-bit lossless integer PCM, read and write. Opus (`.opus`, in a from-scratch OggOpus container) supports mono and stereo, 16-bit integer PCM at a fixed 48kHz, read and write. Vorbis (`.ogg`) supports mono and stereo, 16-bit integer PCM at any sample rate, read and write. A float WAV *source* always decodes transparently into int PCM, the same as any other bit depth. For a float, mu-law, A-law, IMA ADPCM, MS ADPCM, or Yamaha ADPCM WAV *destination*, pass `WavSampleFormat.Float32`/`MuLaw`/`ALaw`/`ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` to `AudioCutter.Convert`/`Cut` (via `CutOptions.DestinationWavFormat`)/`Mix`/`Concatenate` — the default (`WavSampleFormat.Integer`) is unchanged; `Float32` requires the destination's bit depth to already be 32, `MuLaw`/`ALaw`/`ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` require 16 (widen/narrow with `BitDepthFormatTransform` first if needed), and `ImaAdpcm`/`MsAdpcm`/`YamahaAdpcm` additionally require mono or stereo. `AudioCutter.ReadWavAsFloat`/`WriteWavFromFloat`/`FloatSampleConverter` remain available for working with `float[]` directly instead of driving int PCM through a pipeline.

## License

MIT — see [LICENSE](LICENSE). EggEncoder bundles pre-built `libmp3lame.dll` (LGPL-2.1),
`libFLAC.dll` (BSD-style), and `wavpackdll.dll` (BSD-style) as separate, dynamically-loaded
native binaries; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
