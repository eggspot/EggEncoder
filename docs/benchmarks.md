# Benchmarks

`src/EggEncoder.Benchmarks/` is a standalone [BenchmarkDotNet](https://benchmarkdotnet.org/) console
project measuring the hot paths in this codebase's own codec/container I/O. It is **not** part of
the test suite and is **not** run in CI — these are local, manual, point-in-time measurements for
spotting a real regression or comparing two implementation choices, not a tracked performance
budget.

## How to run

```bash
# Full suite (takes several minutes -- each benchmark runs multiple warmup + measured iterations)
dotnet run --project src/EggEncoder.Benchmarks --configuration Release

# One class only
dotnet run --project src/EggEncoder.Benchmarks --configuration Release -- --filter "*CodecDecodeBenchmarks*"

# A quick, low-confidence smoke run (3 iterations instead of BenchmarkDotNet's own default ~15)
dotnet run --project src/EggEncoder.Benchmarks --configuration Release -- --filter "*CodecDecodeBenchmarks*" --job short
```

Always run with `--configuration Release` — a Debug build's numbers aren't representative and
BenchmarkDotNet itself warns loudly if you forget. Each benchmark class builds its own fixture
file(s) in `[GlobalSetup]` (a synthetic 440Hz sine tone, not a checked-in fixture — these are
throughput/allocation measurements, not correctness tests, so the exact audio content doesn't
matter) and deletes them in `[GlobalCleanup]`.

## What's covered

| Class | Measures |
|-------|----------|
| `WavIoBenchmarks` | `WavReader`/`WavWriter` read/write throughput — the common I/O path every other codec's own `Probe`/`ConvertFile`/`CutFile` bottlenecks through |
| `AudioCutterBenchmarks` | End-to-end `AudioCutter.Convert` (decode block → encode block) throughput for WAV→MP3 and WAV→FLAC — the path a real transcode call actually runs, encode and decode together |
| `AacStreamingBenchmarks` | `AacEncoderSession`'s own incremental per-sample push throughput and allocation behavior — the encoder most directly used in a real-time/streaming pipeline (see `docs/streaming-plan.md`) |
| `CodecDecodeBenchmarks` | Pure decode throughput, isolated from encoding, for `FlacDecoder`/`Mp3Decoder`/`WavPackDecoder`/`TtaDecoder` — the four codecs `docs/managed-codec-rewrite-plan.md` replaced (or, for MP3 decode, is in the process of replacing) a native/external dependency with a from-scratch managed implementation for |

## What's not covered

Encode throughput for WavPack/TTA/ALAC/Opus/Vorbis/WMA specifically (only MP3/FLAC encode is
covered, via `AudioCutterBenchmarks`'s combined decode+encode measurement) and decode throughput
for ALAC/Opus/Vorbis/WMA/AAC(file)/MOV. Add a benchmark class here (following `CodecDecodeBenchmarks`'s
own shape: build a fixture in `[GlobalSetup]`, one `[Benchmark]` method per codec, clean up in
`[GlobalCleanup]`) if a specific codec path's performance becomes a real question, rather than
pre-emptively covering everything.
