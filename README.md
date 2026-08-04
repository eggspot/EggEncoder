# 🥚 EggEncoder

> **Audio encoding/decoding toolkit for .NET** — native MP3/FLAC codec bindings, managed AAC/WMA decode, waveform generation, and an ffmpeg wrapper fallback, all behind one `IMediaEncoder` interface.

Sponsored by [eggspot.app](https://eggspot.app)

[![CI](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml/badge.svg)](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EggEncoder.svg)](https://www.nuget.org/packages/EggEncoder)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

📖 **[Full documentation →](https://eggspot.github.io/EggEncoder/)**

## Overview

EggEncoder gives you a single `IMediaEncoder` abstraction for probing, converting, and cutting audio files, backed by two interchangeable implementations:

- **`NativeEncoder`** — pure .NET codec implementations (AAC, FLAC, MP3, WAV, WMA) plus native P/Invoke bindings to `libmp3lame` and `libFLAC`. No external process, no ffmpeg install required, fastest option on Windows x64.
- **`FfmpegEncoder`** — shells out to an `ffmpeg`/`ffprobe` binary you provide. Broader format support (including video container probing via MOV/MP4), useful when you need formats the native codecs don't cover.

Both implementations return the same `ProbeResult` shape (duration, sample rate, bit depth, bitrate, waveform peaks) so callers can switch between them via configuration without touching call sites.

### Why EggEncoder?

- 🎯 **One interface, two engines** — swap native ⇄ ffmpeg via a single config flag
- 🚀 **Native codec bindings** — direct P/Invoke to LAME (MP3) and libFLAC, no subprocess overhead
- 🎼 **Broad format coverage** — AAC, FLAC, MP3, WAV, WMA decode/encode; MOV/MP4 metadata probing
- 📊 **Built-in waveform generation** — normalized peak windows for any decoded stream
- ✂️ **Sample-accurate cutting** — trim audio files without a full decode→encode round trip through ffmpeg
- 🪶 **Dependency-light** — only `Microsoft.Extensions.*.Abstractions` and `NLayer`
- 📖 **MIT licensed** — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the bundled native codec licenses (LGPL-2.1 LAME, BSD-style libFLAC)

## Installation

```bash
dotnet add package EggEncoder
```

Native codec binaries (`libmp3lame.dll`, `libFLAC.dll`) ship inside the package for `win-x64` and are copied to your output directory automatically.

## Quick Start

```csharp
using EggEncoder;

// Dependency injection (recommended)
builder.Services.AddEggEncoder(options =>
{
    options.UseNativeEncoder = true; // or false to use ffmpeg
    // options.FfmpegBinPath = @"C:\tools\ffmpeg"; // required when UseNativeEncoder = false
});
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

## Supported Formats

| Format | Native probe | Native decode | Native encode | ffmpeg |
|--------|:---:|:---:|:---:|:---:|
| WAV    | ✅ | ✅ | ✅ | ✅ |
| FLAC   | ✅ | ✅ | ✅ | ✅ |
| MP3    | ✅ | ✅ | ✅ | ✅ |
| AAC    | ✅ | ✅ | ✅ | ✅ |
| WMA    | ✅ | ✅ | ❌ | ✅ |
| MOV/MP4 (metadata only) | ✅ | ❌ | ❌ | ✅ |

`NativeEncoder.CutFile` supports WAV, FLAC, MP3, and AAC (sample-accurate, no re-encode of the untouched region). `FfmpegEncoder` supports whatever your ffmpeg build supports.

## Configuration

`EggEncoderOptions`:

| Property | Type | Description |
|----------|------|--------------|
| `UseNativeEncoder` | `bool` | `true` resolves `IMediaEncoder` as `NativeEncoder`; `false` resolves `FfmpegEncoder`. Default `false`. |
| `FfmpegBinPath` | `string?` | Directory containing `ffmpeg.exe` and `ffprobe.exe`. Required when `UseNativeEncoder` is `false`. `FfmpegEncoderBinFactory` throws `FileNotFoundException` at startup if either binary is missing — EggEncoder does not download or provision ffmpeg for you. |

## License

MIT — see [LICENSE](LICENSE). EggEncoder bundles pre-built `libmp3lame.dll` (LGPL-2.1) and
`libFLAC.dll` (BSD-style) as separate, dynamically-loaded native binaries; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
