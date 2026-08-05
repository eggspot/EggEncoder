# 🥚 EggEncoder

> **Audio encoding/decoding toolkit for .NET** — native MP3/FLAC codec bindings, managed AAC/WMA encode/decode, and built-in waveform generation, all behind one `IMediaEncoder` interface.

Sponsored by [eggspot.app](https://eggspot.app)

[![CI](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml/badge.svg)](https://github.com/eggspot/EggEncoder/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EggEncoder.svg)](https://www.nuget.org/packages/EggEncoder)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

📖 **[Full documentation →](https://eggspot.github.io/EggEncoder/)**

## Overview

EggEncoder gives you a single `IMediaEncoder` abstraction — `Probe`, `ConvertFile`, `CutFile` — implemented entirely in-process by `NativeEncoder`: pure .NET codec implementations (AAC, WAV, WMA) plus native P/Invoke bindings to `libmp3lame` and `libFLAC`. No external process, no ffmpeg install, no subprocess overhead.

### Why EggEncoder?

- 🚀 **Fully native, in-process** — direct P/Invoke to LAME (MP3) and libFLAC, no subprocess/shell-out overhead
- 🎼 **Broad format coverage** — AAC, FLAC, MP3, WAV, WMA decode/encode; MOV/MP4 metadata probing + mono AAC-LC audio decode
- 📊 **Built-in waveform generation** — normalized peak windows for any decoded stream
- ✂️ **Sample-accurate cutting** — trim audio files without a full decode→encode round trip
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

## Supported Formats

| Format | Probe | Decode | Encode |
|--------|:---:|:---:|:---:|
| WAV    | ✅ | ✅ | ✅ |
| FLAC   | ✅ | ✅ | ✅ |
| MP3    | ✅ | ✅ | ✅ |
| AAC    | ✅ | ✅ | ✅ |
| WMA    | ✅ | ✅ | ✅ |
| MOV/MP4 | ✅ | ✅¹ | ❌ |

¹ MOV/MP4 decode is audio-only, mono AAC-LC tracks — video frames are never decoded. Files without a matching audio track still probe fine (metadata only).

`IMediaEncoder.CutFile` decodes any supported source (WAV, FLAC, MP3, AAC, WMA, and MOV/MP4 files with a mono AAC-LC audio track) and can cut into any supported destination format, including converting as it trims — sample-accurate, no re-encode of the untouched region.

## License

MIT — see [LICENSE](LICENSE). EggEncoder bundles pre-built `libmp3lame.dll` (LGPL-2.1) and
`libFLAC.dll` (BSD-style) as separate, dynamically-loaded native binaries; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
