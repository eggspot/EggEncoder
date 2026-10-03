# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is EggEncoder

EggEncoder is a .NET audio encoding/decoding toolkit built around a single `IMediaEncoder` abstraction (`Probe`, `ConvertFile`, `CutFile`) implemented entirely in-process by `NativeEncoder` — pure .NET + P/Invoke codec bindings, no external process, no ffmpeg dependency. Originally extracted from a music distribution platform's internal encoder project (which also had an ffmpeg-shell-out engine; that engine was dropped when EggEncoder became native-only).

## Commands

```bash
# Build
dotnet build --configuration Release

# Run all unit tests
dotnet test --configuration Release

# Run tests for a specific class
dotnet test src/EggEncoder.UnitTests/EggEncoder.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~Mp3EncoderTest"
```

## Architecture

### `IMediaEncoder` implementation

- **`NativeEncoder`** (`src/EggEncoder/NativeEncoder.cs`) — the sole `IMediaEncoder` implementation. Dispatches by file extension to the codec classes under `Codecs/`. No subprocess, no external binary dependency beyond the bundled `libmp3lame`/`libFLAC` DLLs.

### Codecs (`src/EggEncoder/Codecs/`)

| Folder | Contents |
|--------|----------|
| `Aac/` | Pure managed AAC decoder/encoder + scale-factor/Huffman tables (`AacDecoder`, `AacFrameDecoder`, `AacEncoder`, `AacFrameEncoder`, `AacEncoderSession`, `AacTables`). `AacFrameDecoder` decodes one raw_data_block into PCM independent of ADTS framing, shared by `AacDecoder` (ADTS) and `MovDecoder` (MP4) |
| `Aiff/` | `AiffReader`/`AiffWriter` — FORM/COMM/SSND (plain AIFF, not AIFC) PCM I/O, the big-endian counterpart to `Wav/`. Supports 8/16/24/32-bit integer PCM (8-bit is signed, unlike WAV's unsigned convention); `IeeeExtendedFloat` converts COMM's 80-bit extended-precision sample-rate field, which .NET has no built-in support for |
| `Alac/` | Pure managed ALAC (Apple Lossless) decoder/encoder in a CAF container — `AlacDecoder`/`AlacEncoder`/`AlacEncoderSession` (the public entry points), `AlacFrameDecoder`/`AlacFrameEncoder` (one ALAC packet: mono SCE or stereo CPE, adaptive-FIR-predicted + Rice-coded per channel, with a verbatim/uncompressed fallback per frame and ffmpeg-verified mid/side-style stereo decorrelation both ways — encode tries a fixed mid/side mix first (same formula decode un-mixes, so it round-trips exactly for any weight/shift), falling back to independent channels and then to verbatim only if a residual would overflow), `AlacLpcPredictor` (the sign-sign-adaptive predictor shared, not duplicated, between encode and decode), `AlacRiceCoder` (the modified-Rice residual coder, including its zero-run escape), `AlacSpecificConfig` (the ALAC "magic cookie"), `CafReader`/`CafWriter` (the CAF container itself). Scoped to mono/stereo, 16-bit integer PCM only — see `AlacDecoder`'s doc comment for why 20/24-bit depths are out of scope for now |
| `Flac/` | `FlacDecoder`/`FlacEncoder` — thin wrappers over native `libFLAC` P/Invoke bindings |
| `Mp3/` | `Mp3Decoder` (via the `NLayer` managed decoder), `Mp3Encoder` (native `libmp3lame` P/Invoke), `Mp3Probe` (manual frame-header parsing, no native call) |
| `Wav/` | `WavReader`/`WavWriter` — RIFF/WAVE PCM I/O, the common source/sink format all codecs read from or write to. Both support 8/16/24/32-bit integer and 32-bit IEEE float (`WavWriter.Create(..., isFloatFormat: true)`) |
| `Wma/` | Pure managed WMAv2 decoder/encoder (`WmaDecoder`, `WmaEncoder`, `WmaEncoderSession`, `WmaFrameEncoder`) + `AsfContainerReader`/`AsfContainerWriter` (ASF/WMA container I/O) + `WmaTables` |
| `Mov/` | `MovProbe` — MOV/MP4 metadata (duration/dimensions/codec); `MovDecoder` — decodes a mono AAC-LC 'soun' track via `stsd`/`esds`/sample-table demuxing (`Mp4EsdsParser`, `Mp4SampleTable`) and the shared `AacFrameDecoder`; `MovAtomReader` — shared atom-tree walker used by both |
| `Tta/` | Pure managed TTA (True Audio) lossless decoder/encoder — `TtaDecoder`/`TtaEncoder`/`TtaEncoderSession` (the public entry points), `TtaFrameDecoder`/`TtaFrameEncoder` (per-frame orchestration, including TTA's unconditional, per-sample-interleaved stereo decorrelation — no bitstream flag, unlike ALAC's optional mid/side), `TtaChannelState` (bundles one channel's Rice coder + adaptive filter + fixed predictor), `TtaRiceCoder` (the adaptive two-tier Rice/unary residual coder — unlike `AlacRiceCoder`, no verbatim/escape fallback since its unary code is fully unbounded), `TtaAdaptiveFilter` (order-8 sign-sign-adaptive filter), `TtaFixedPredictor`, `TtaReader`/`TtaWriter` (the TTA container: 22-byte header, mandatory seek table, per-frame data — each with its own CRC32, via `Crc32`). Scoped to mono/stereo, 16-bit integer PCM only |
| `AudioCutter.cs` | Format-dispatching `Convert`/`Cut` used by `NativeEncoder`; defines the internal `IAudioSink` interface implemented by each codec's writer/session type |
| `AudioCutter.Pipeline.cs` | Pipeline-aware `Convert`/`Cut` overloads (take a `PcmTransformPipeline` / `CutOptions`), plus `Mix` and `Concatenate` — see [PCM transform pipeline](#pcm-transform-pipeline-srceggencoderpcm) below |

### Supporting infrastructure

- **`Transform/`** — `BitReader`, `BitWriter`, `HuffmanTable`, `Mdct` — low-level bitstream and signal-processing primitives shared by the AAC/WMA codecs
- **`Native/`** — `FlacNative.cs`/`Mp3Native.cs` (`[LibraryImport]` P/Invoke declarations), `NativeLibraryLoader.cs` (a `[ModuleInitializer]` that registers a custom `DllImportResolver` so `libFLAC`/`libmp3lame` load from `Native/win-x64/` relative to `AppContext.BaseDirectory` regardless of the consuming app's working directory)
- **`Waveform/WaveformCalculator.cs`** — streaming peak-window calculator fed blocks during decode, used by every codec's probe path to produce `ProbeResult.Waveform`
- **`Results/ProbeResult.cs`** — the public `ProbeResult` DTO returned by every `Probe` call
- **`ServiceCollectionExtensions.cs`** — `AddEggEncoder(enableLogging: true)` DI registration; registers `NativeEncoder` itself as scoped, then maps both `IMediaEncoder` and `IPcmTransformEncoder` to resolve that same scoped instance. `enableLogging: false` fully silences `NativeEncoder`'s start/completion/failure logs

### PCM transform pipeline (`src/EggEncoder/Pcm/`)

A `PcmTransformPipeline` is an ordered list of `IPcmTransform`s run on each decoded `int[]` block, between decode and the `IAudioSink` write, wired in by `AudioCutter.Pipeline.cs`. It's opt-in: the original zero-pipeline `Convert`/`Cut` overloads are untouched and remain fully streaming. `IPcmTransform.Flush()` is a default interface method (existing implementers don't need to change) for a transform that holds output back across blocks (currently only `ResamplingTransform`); `PcmTransformPipeline.Flush(...)` cascades it through an entire pipeline — a flushed transform's tail is pushed through every later transform's own `Apply` before that transform's own `Flush` drains it in turn — and `AudioCutter.Pipeline.cs`'s `Convert`/`Cut` call it once after the last block, before finishing the destination sink.

| Transform | Does |
|-----------|------|
| `ResamplingTransform` | Kaiser-windowed-sinc polyphase resampler (anti-aliasing on downsample, band-limited reconstruction on upsample), precomputed into a polyphase table so resampling is a table lookup plus a dot product. Holds a self-trimming window of source history across blocks and withholds a destination frame until its full tap window has genuinely arrived, rather than duplicating an edge sample at a block boundary; total output frame count is a cumulative `floor(sourceFramesSoFar * ratio)`, exact regardless of block splits. Because of that withholding, the last few frames of a stream need `Flush()` (see below) to drain — `AudioCutter`'s pipeline-aware `Convert`/`Cut` call it automatically |
| `VolumeTransform` / `PeakNormalizationTransform` | Linear gain, and two-pass peak normalization to a target dBFS (`MeasurePeak` then `Apply`, or single-pass self-measuring) |
| `ChannelRemixTransform` | Mono↔stereo and general N↔M remixing (equal-weight downmix, cyclic upmix) |
| `BitDepthFormatTransform` | Rescales between the native ranges of 8/16/24/32-bit samples (this codebase has no separate "normalized" scale — every bit depth is signed and sign-extended to its own native range, e.g. 8-bit is -128..127; see `WavReader`/`WavWriter`) |
| `FadeTransform` | Fade-in/out (linear or equal-power) over a fixed total frame count, tracked across blocks the same way as `ResamplingTransform` |
| `BiquadTransform` | A single second-order IIR filter section using the RBJ Audio EQ Cookbook formulas: low/high pass, band pass, notch, all pass, peaking EQ, low/high shelf. Direct Form II Transposed (two state values per channel) |
| `ButterworthTransform` | Steeper low/high pass than a single `BiquadTransform` can provide, via `order / 2` cascaded `BiquadTransform` stages sharing a cutoff frequency, each at a different Q from the standard Butterworth pole-pair formula (maximally flat passband). `order` must be a positive even integer |
| `FirFilterTransform` | General FIR convolution (ffmpeg `afir`/general-FIR parity): applies a caller-supplied tap array identically to every channel, with no filter-design code of its own — the raw-taps constructor is the only entry point, since accepting arbitrary taps is the whole point. Keeps each channel's last `taps.Length - 1` samples as history across blocks |

`PcmTransformPipeline.ComputeOutputFormat` lets a caller learn the pipeline's final (channels, sample rate, bit depth) before the first block arrives, so the destination sink can be opened up front. Because `WavWriter` is the only sink that needs an exact frame count at open time (it writes a fixed-size RIFF header with no patch-up on `Finish()`), `AudioCutter.Pipeline.cs`'s `DeferredWavSink` buffers written blocks and only opens the real `WavWriter` in `Finish()`, once the true count (which a frame-count-changing transform like resampling can't predict up front) is known; every other destination format streams straight through since their encoder sessions don't take a frame count at all.

`Mix` (2+ same-format sources, per-input gain, silence-padded to the longest) decodes every input fully into memory — clip-length material, not multi-hour streams. `Concatenate` (2+ same-format sources) is fully streaming.

Transform instances carry cross-block state (e.g. `ResamplingTransform`'s fractional position, `FadeTransform`'s frame position, `PeakNormalizationTransform`'s measured gain, `BiquadTransform`/`ButterworthTransform`/`FirFilterTransform`'s filter history) — build a fresh `PcmTransformPipeline` per `Convert`/`Cut`/`Mix` call rather than reusing one across multiple calls.

**Float PCM** (`Pcm/FloatSampleConverter.cs`): the pipeline itself is exclusively `int[]`-based — there's no `IPcmTransform` for float, since that would mean reworking every transform's shared contract. Instead `FloatSampleConverter.FromFloat`/`ToFloat` convert at the application boundary, using the same -1.0..1.0 ↔ 32-bit-int-native-range scale `WavReader`/`WavWriter` already use for float WAV I/O; `float.NaN` maps to 0 (silence) rather than the unspecified result of a raw `(int)double.NaN` cast, and `float.PositiveInfinity`/`NegativeInfinity` clamp to ±1.0. `AudioCutter.ReadWavAsFloat`/`WriteWavFromFloat` wrap this for the WAV case. A float WAV *source* has always decoded transparently into int PCM through `Convert`/`Cut`/`Mix`/`Concatenate` (`WavReader.IsFloatFormat` handles this on read); for a float WAV *destination*, `WavSampleFormat.Float32` (`Codecs/WavSampleFormat.cs`) can be passed to those same entry points (`Convert`/`Cut` via `CutOptions.DestinationWavFormat`/`Mix`/`Concatenate`) — it requires the destination's bit depth to already be 32 and throws `NotSupportedException` against a non-WAV destination. The default, `WavSampleFormat.Integer`, is unchanged from before this option existed.

### Native binary packaging

`libmp3lame.dll` and `libFLAC.dll` live at `src/EggEncoder/Native/win-x64/` and are packed via the NuGet `contentFiles` convention (see `EggEncoder.csproj`) so they land at `Native/win-x64/*.dll` relative to the consuming application's output directory — exactly where `NativeLibraryLoader` expects them. If you change this packaging, keep it in sync with `NativeLibraryLoader.Resolve`.

### Native AOT

`EggEncoder.csproj` sets `IsAotCompatible=true` (enables the trim/AOT/single-file Roslyn analyzers on every build). The codebase relies only on AOT-safe interop: `[LibraryImport]` (not `[DllImport]`) for P/Invoke, `[UnmanagedCallersOnly]` static methods + `GCHandle` (not marshaled delegate closures) for native callbacks (see `FlacDecoder`), and `AppContext.BaseDirectory` (not `Assembly.Location`) for native binary resolution. `src/EggEncoder.AotSmokeTest/` is a `PublishAot=true` console project that round-trips WAV → MP3/FLAC → probe through a real native-compiled binary in CI — see "Testing Conventions" below. If you add a dependency or interop call, make sure it doesn't reintroduce reflection-based marshaling or `Reflection.Emit`.

## Testing Conventions

- Framework: **xUnit** + **FluentAssertions**
- Pattern: **AAA** (Arrange / Act / Assert)
- Naming: `Feature_Condition_ExpectedBehavior` (e.g., `Probe_WavFile_Should_Return_Correct_Metadata_And_Waveform`)
- Tests live in `src/EggEncoder.UnitTests/`, mirroring the `src/EggEncoder/` folder structure
- Fixture audio files (`.wav`/`.flac`/`.mp3`/`.mov`/`.mp4`) live alongside their tests and are copied to the test output directory — see `<None ... CopyToOutputDirectory>` entries in `EggEncoder.UnitTests.csproj`
- Round-trip and cross-check tests (e.g. `FlacFfmpegCrossCheckTest`) validate native codec output against ffmpeg-produced reference fixtures checked into the repo — no external ffmpeg install is needed to run the tests, only the fixture files themselves
- `src/EggEncoder.AotSmokeTest/` covers Native AOT: it's a separate `PublishAot=true` console project (not an xUnit test, since xUnit runs under the JIT) that CI publishes with `dotnet publish -r win-x64` and then executes, to catch AOT/trimming regressions that the build-time analyzer alone can't (e.g. inside the `NLayer` dependency, which ships no AOT metadata of its own)

## Release Process

Fully automatic tag-based versioning — every push to `main` triggers a release:

1. Push / merge to `main`
2. CI analyzes commit messages since last tag using conventional commits:
   - `fix:` / `perf:` / `chore:` → **patch** bump (1.1.0 → 1.1.1)
   - `feat:` → **minor** bump (1.1.0 → 1.2.0)
   - `BREAKING CHANGE` / `feat!:` → **major** bump (1.1.0 → 2.0.0)
3. Version derived from last git tag (not csproj) → builds with `-p:Version=` → tests → packs → publishes to NuGet
4. Creates git tag `v<version>` + GitHub Release with artifacts
5. Concurrency group serializes publish runs to prevent tag race conditions

No manual version editing needed. No commits pushed back to main. Just use conventional commit prefixes.

## Working Style

- **NEVER push directly to `main`** — always create a feature branch and open a PR
- Break complex tasks into smaller incremental commits
- A single PR should focus on one logical change
- Commit and push to the feature branch after each verified, self-contained unit of work
- Use conventional commit prefixes (`feat:`, `fix:`, `perf:`, `chore:`, `docs:`) — the publish pipeline auto-detects version bumps from these
- Any change to the bundled native binaries or their packaging must be reflected in `THIRD-PARTY-NOTICES.md`
