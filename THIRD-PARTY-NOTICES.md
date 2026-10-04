# Third-Party Notices

EggEncoder's MIT license (see [LICENSE](LICENSE)) covers the EggEncoder source code only.
The package also redistributes three pre-built native codec libraries as separate, unmodified
binary files under `Native/win-x64/`. Their licenses apply to those files independently.

## libmp3lame.dll — LAME MP3 encoder

- **Project**: [LAME](https://lame.sourceforge.io/)
- **License**: GNU Lesser General Public License v2.1 (LGPL-2.1) — full text at
  https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- **How it's used**: EggEncoder loads `libmp3lame.dll` dynamically at runtime via
  `NativeLibrary.Load` / P/Invoke (see `Native/Mp3Native.cs`, `Native/NativeLibraryLoader.cs`).
  It is shipped as a separate, unmodified binary file alongside the managed assembly, not
  statically linked or embedded — consumers of this package may replace the DLL with their own
  LGPL-compliant build without recompiling EggEncoder, satisfying LGPL-2.1's relinking
  requirement.
- **Source availability**: LAME source code is available at https://lame.sourceforge.io/download.php

## libFLAC.dll — FLAC reference codec

- **Project**: [FLAC](https://xiph.org/flac/) (Xiph.Org Foundation)
- **License**: BSD-style Xiph.Org license (permissive) — full text at
  https://xiph.org/flac/license.html
- **How it's used**: Loaded dynamically at runtime via `NativeLibrary.Load` / P/Invoke (see
  `Native/FlacNative.cs`, `Native/NativeLibraryLoader.cs`), shipped as a separate unmodified
  binary file under `Native/win-x64/`.
- **Source availability**: FLAC source code is available at https://github.com/xiph/flac

## wavpackdll.dll — WavPack reference library

- **Project**: [WavPack](https://github.com/dbry/WavPack) (David Bryant)
- **License**: BSD-style license — full text at
  https://github.com/dbry/WavPack/blob/master/COPYING
- **How it's used**: Loaded dynamically at runtime via `NativeLibrary.Load` / P/Invoke (see
  `Native/WavPackNative.cs`, `Native/NativeLibraryLoader.cs`), shipped as a separate unmodified
  binary file under `Native/win-x64/`. The bundled DLL is the official project's own prebuilt
  binary from its GitHub release (`wavpack-5.9.0-dll.zip`, `x64/wavpackdll.dll`), checksum-verified
  against that release's own published `sums.txt` before being committed here.
- **Source availability**: WavPack source code is available at https://github.com/dbry/WavPack

## NLayer — managed MP3 decoder

- **Project**: [NLayer](https://github.com/naudio/NLayer)
- **License**: MIT / LGPL dual-licensed (used here under MIT)
- **How it's used**: Referenced as a standard NuGet `PackageReference` for MP3 decoding
  (`Codecs/Mp3/Mp3Decoder.cs`); no source is vendored.

## Concentus — managed Opus encoder/decoder

- **Project**: [Concentus](https://github.com/lostromb/concentus)
- **License**: BSD-style license (copyright Xiph.Org Foundation, Skype Limited, CSIRO,
  Microsoft Corporation, and other contributors) — full text at
  https://github.com/lostromb/concentus/blob/master/LICENSE
- **How it's used**: Referenced as a standard NuGet `PackageReference` for Opus encoding and
  decoding (`Codecs/Opus/OpusEncoderSession.cs`, `Codecs/Opus/OpusDecoder.cs`); no source is
  vendored. `Concentus.Native`/`Concentus.Native.NetCore` (optional sibling packages providing a
  P/Invoke adapter to a native `libopus`) are deliberately **not** referenced — this project
  relies specifically on Concentus's own pure managed implementation, and
  `OpusRuntimeConfiguration` pins `OpusCodecFactory.AttemptToUseNativeLibrary = false` so it never
  silently falls back to an unrelated native `opus`/`libopus` library that happens to be present
  on a given host.

## NVorbis — managed Ogg Vorbis decoder

- **Project**: [NVorbis](https://github.com/NVorbis/NVorbis)
- **License**: MIT (copyright Andrew Ward) — full text at
  https://github.com/NVorbis/NVorbis/blob/master/LICENSE
- **How it's used**: Referenced as a standard NuGet `PackageReference` for Ogg Vorbis decoding
  (`Codecs/Vorbis/VorbisDecoder.cs`); no source is vendored. Unlike Concentus, there is no sibling
  native-adapter package to guard against — NVorbis has no native `libvorbis` fallback option at
  all, pure managed is its only mode.

## OggVorbisEncoder — managed Ogg Vorbis encoder

- **Project**: [.NET-Ogg-Vorbis-Encoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder)
- **License**: MIT (copyright Steve Lillis) — full text at
  https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder/blob/master/LICENSE
- **How it's used**: Referenced as a standard NuGet `PackageReference` for Ogg Vorbis encoding
  (`Codecs/Vorbis/VorbisEncoderSession.cs`); no source is vendored. As with NVorbis, there is no
  native `libvorbisenc` fallback option to guard against — it's pure managed only.

---

*Before the first public release, verify the license text links above still resolve and
match the exact terms distributed with the bundled binaries.*
