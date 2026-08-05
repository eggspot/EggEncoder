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
| `Aac/` | Pure managed AAC decoder/encoder + scale-factor/Huffman tables (`AacDecoder`, `AacEncoder`, `AacEncoderSession`, `AacTables`) |
| `Flac/` | `FlacDecoder`/`FlacEncoder` — thin wrappers over native `libFLAC` P/Invoke bindings |
| `Mp3/` | `Mp3Decoder` (via the `NLayer` managed decoder), `Mp3Encoder` (native `libmp3lame` P/Invoke), `Mp3Probe` (manual frame-header parsing, no native call) |
| `Wav/` | `WavReader`/`WavWriter` — RIFF/WAVE PCM I/O, the common source/sink format all codecs read from or write to |
| `Wma/` | `WmaDecoder` + `AsfContainerReader` (ASF/WMA container parsing) + `WmaTables` |
| `Mov/` | `MovProbe` — MOV/MP4 atom-tree walker for metadata-only probing (no audio decode) |
| `AudioCutter.cs` | Format-dispatching `Convert`/`Cut` used by `NativeEncoder`; defines the internal `IAudioSink` interface implemented by each codec's writer/session type |

### Supporting infrastructure

- **`Transform/`** — `BitReader`, `BitWriter`, `HuffmanTable`, `Mdct` — low-level bitstream and signal-processing primitives shared by the AAC/WMA codecs
- **`Native/`** — `FlacNative.cs`/`Mp3Native.cs` (`[LibraryImport]` P/Invoke declarations), `NativeLibraryLoader.cs` (a `[ModuleInitializer]` that registers a custom `DllImportResolver` so `libFLAC`/`libmp3lame` load from `Native/win-x64/` relative to `AppContext.BaseDirectory` regardless of the consuming app's working directory)
- **`Waveform/WaveformCalculator.cs`** — streaming peak-window calculator fed blocks during decode, used by every codec's probe path to produce `ProbeResult.Waveform`
- **`Results/ProbeResult.cs`** — the public `ProbeResult` DTO returned by every `Probe` call
- **`ServiceCollectionExtensions.cs`** — `AddEggEncoder(enableLogging: true)` DI registration; registers `IMediaEncoder` → `NativeEncoder` (scoped). `enableLogging: false` fully silences `NativeEncoder`'s start/completion/failure logs

### Native binary packaging

`libmp3lame.dll` and `libFLAC.dll` live at `src/EggEncoder/Native/win-x64/` and are packed via the NuGet `contentFiles` convention (see `EggEncoder.csproj`) so they land at `Native/win-x64/*.dll` relative to the consuming application's output directory — exactly where `NativeLibraryLoader` expects them. If you change this packaging, keep it in sync with `NativeLibraryLoader.Resolve`.

## Testing Conventions

- Framework: **xUnit** + **FluentAssertions**
- Pattern: **AAA** (Arrange / Act / Assert)
- Naming: `Feature_Condition_ExpectedBehavior` (e.g., `Probe_WavFile_Should_Return_Correct_Metadata_And_Waveform`)
- Tests live in `src/EggEncoder.UnitTests/`, mirroring the `src/EggEncoder/` folder structure
- Fixture audio files (`.wav`/`.flac`/`.mp3`/`.mov`/`.mp4`) live alongside their tests and are copied to the test output directory — see `<None ... CopyToOutputDirectory>` entries in `EggEncoder.UnitTests.csproj`
- Round-trip and cross-check tests (e.g. `FlacFfmpegCrossCheckTest`) validate native codec output against ffmpeg-produced reference fixtures checked into the repo — no external ffmpeg install is needed to run the tests, only the fixture files themselves

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
