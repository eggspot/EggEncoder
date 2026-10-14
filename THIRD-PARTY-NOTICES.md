# Third-Party Notices

EggEncoder's MIT license (see [LICENSE](LICENSE)) covers the EggEncoder source code only.
As of the managed codec rewrite (`docs/managed-codec-rewrite-plan.md`), EggEncoder no longer
bundles or loads any pre-built native binary of any kind — every codec is pure managed C#,
either implemented directly in this project or via a permissively-licensed managed NuGet
dependency (acknowledged individually below). FLAC, MP3, and WavPack were all previously
backed by bundled native libraries (`libFLAC.dll`, `libmp3lame.dll`, `wavpackdll.dll`
respectively); none of them ship any more.

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

## MP3 encoder-delay/padding metadata ("LAME tag") — clean-room acknowledgment (byte layout read from FFmpeg's real source, no code copied)

The managed MP3 **encoder**'s dedicated Info-tag header frame (`Mp3FrameEncoder.BuildInfoTagFrame`)
and the corresponding read-side parsing (`Mp3Probe.TryReadLameTagDelayAndPadding`) implement the
de-facto-standard "LAME tag" — an Xing-style VBR-header extension carrying gapless-playback
encoder-delay/padding metadata that real-world encoders (LAME itself, and FFmpeg's own Lavc/Lavf
muxers) write, and real-world players read, with no official specification of its own. With the
owner's explicit sign-off (per the same arm's-length methodology already established for WavPack
and the MP3 encoder itself, above), the exact field order and byte sizes were confirmed directly
against **FFmpeg's own real source** — `libavformat/mp3dec.c`'s `mp3_parse_info_tag`/
`mp3_parse_vbr_tags` functions (part of [FFmpeg](https://ffmpeg.org/), GNU Lesser General Public
License v2.1 or later), fetched from FFmpeg's own public GitHub mirror and read directly, not
assumed from memory or secondary sources. Only the factual byte layout (field order, sizes, the
12+12-bit delay/padding split) was carried over, read to understand and describe the format in
this project's own words; no FFmpeg source is vendored, linked, or reproduced, and the actual
writing/reading code (`BuildInfoTagFrame`/`TryReadLameTagDelayAndPadding`, both `BitWriter`/raw
byte-offset based) is this project's own, independently written implementation, not a translation
or transcription of FFmpeg's C.

This project's own `Mp3Encoder.EncoderDelaySamples` constant (1680) and the corresponding
encoder-padding formula were **not** read from FFmpeg or LAME source at all — they were derived
empirically, specific to this project's own encoder design (the dedicated Info-tag header frame
plus this encoder's own one-granule MDCT history warm-up), by measuring this exact encoder+decoder
pair's own real round-trip delay via normalized cross-correlation against known test signals, then
cross-checked two further ways: against an independent real `ffmpeg` decode of the same file
(binary only, as a black box, never its source — the same "real `lame`/`ffmpeg` CLI builds... as
independent binaries" methodology the MP3 encoder's own entry above already establishes), and
against a real `ffmpeg`+`libmp3lame`-produced reference file's own raw tag bytes (parsed directly
to confirm the general `realSamples = frameCount*1152 - startPad - endPad` identity holds, using
LAME's own real, independently-written `start_pad`/`end_pad` values as an external check, not this
project's own).

No source from LAME itself was read at any point for this feature (the owner's sign-off covered
FFmpeg's source specifically, not LAME's) — including the LAME-tag's own Music CRC and Info Tag
CRC-16 fields, whose exact checksum algorithm/byte range could not be confirmed by black-box
experimentation alone within that boundary. Rather than guess, this project's own writer leaves
both fields as `0x0000` (an honest "not computed" value, not a fabricated one), documented as such
in `Mp3FrameEncoder.BuildInfoTagFrame`'s own doc comment; FFmpeg's own decode-side code never gates
the delay/padding fields on either CRC (confirmed directly from its source), so nothing this
feature needs depends on them.

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
