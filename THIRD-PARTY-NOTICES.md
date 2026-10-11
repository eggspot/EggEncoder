# Third-Party Notices

EggEncoder's MIT license (see [LICENSE](LICENSE)) covers the EggEncoder source code only.
The package also redistributes pre-built native codec libraries as separate, unmodified
binary files under `Native/win-x64/`. Their licenses apply to those files independently.

## libFLAC.dll — FLAC reference codec

- **Project**: [FLAC](https://xiph.org/flac/) (Xiph.Org Foundation)
- **License**: BSD-style Xiph.Org license (permissive) — full text at
  https://xiph.org/flac/license.html
- **How it's used**: Loaded dynamically at runtime via `NativeLibrary.Load` / P/Invoke (see
  `Native/FlacNative.cs`, `Native/NativeLibraryLoader.cs`), shipped as a separate unmodified
  binary file under `Native/win-x64/`.
- **Source availability**: FLAC source code is available at https://github.com/xiph/flac

## WavPack algorithm (decode and encode) — clean-room acknowledgment (no code copied)

WavPack (`.wv`) decode and encode are both now pure managed EggEncoder code (`Codecs/WavPack/`) —
no native binary is bundled or loaded for this codec any more (the previous `wavpackdll.dll` /
`Native/WavPackNative.cs` native decode+encode path, the official WavPack project's own prebuilt
library under a BSD-style license, has been fully removed now that neither direction needs it).
The block/decorrelation/entropy-coding algorithm itself was derived with the owner's explicit
sign-off from two sources beyond the official (and, for the codec itself, intentionally
incomplete) WavPack 4/5 file format specification:

- **General familiarity with the WavPack project's own reference implementation**
  ([github.com/dbry/WavPack](https://github.com/dbry/WavPack), BSD-style license) informed the
  overall shape of the algorithm (cascaded decorrelation passes, an adaptive median-based entropy
  coder). No source from that project was read or copied during this implementation; this is a
  standard "prior exposure informs a from-scratch rewrite" acknowledgment, not a derived-work
  claim.
- **FFmpeg's independently-written WavPack decoder**
  (`libavcodec/wavpack.c`/`wavpack.h`, part of [FFmpeg](https://ffmpeg.org/), GNU Lesser General
  Public License v2.1 or later) was studied at arm's length to extract the precise formulas needed
  for bit-exact decode (exact weight-update/restore math, the entropy coder's class/tail/carry
  state machine, joint-stereo and "false stereo" reconstruction, etc.) — read only to understand
  and describe the algorithm in this project's own words, never to copy code or structure. No
  FFmpeg source is vendored, linked, or reproduced; only the underlying facts/formulas (which are
  not themselves copyrightable) were carried over into an independent C# implementation.

The managed **encoder**'s own formulas were not a second, separate study of either source above —
every one of them (decorrelation weight update, the entropy coder's class/tail/carry write logic)
is the direct mathematical inverse of this same already-documented decode-side knowledge, derived
by this project itself rather than by consulting any encoder implementation.

## NLayer — managed MP3 decoder

- **Project**: [NLayer](https://github.com/naudio/NLayer)
- **License**: MIT / LGPL dual-licensed (used here under MIT)
- **How it's used**: Referenced as a standard NuGet `PackageReference` for MP3 decoding
  (`Codecs/Mp3/Mp3Decoder.cs`); no source is vendored. Its own decoder source
  (`NLayer/Decoder/*.cs`) was also read at arm's length, read-only, as a test oracle while
  debugging the managed MP3 **encoder** below (`git log`-visible under `feat/managed-mp3-encoder`)
  — to cross-check this project's own Huffman table transcription and understand decode-side
  framing expectations (bit reservoir, `part2_3_length`/`part3end`, region boundaries). No NLayer
  source was copied; only independently-rederived facts (confirmed correct by mechanical,
  bit-for-bit comparison, not by inspection) fed back into the encoder's own, separately-written
  code.

## MP3 (MPEG-1 Layer III) encode — clean-room acknowledgment (no encoder source copied)

The managed MP3 **encoder** (`Codecs/Mp3/Mp3*.cs`, replacing the former `libmp3lame.dll` P/Invoke
path) was implemented from the published ISO/IEC 11172-3 standard text and this project's own
derivations only. No source from any MP3 **encoder** (LAME, GroovyCodecs, shine, FFmpeg's own
encoder, or any other) was read or copied. Real `lame`/`ffmpeg` CLI builds were used, black-box
only (as independent binaries, never their source), to cross-validate bitstream structure during
development — the same arm's-length methodology already established for WavPack above. NLayer's
own **decoder** source was read as a test oracle per the note above; this is explicitly a
decode-side-only exception load-bearing for verification, not an encoder-algorithm source.

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
