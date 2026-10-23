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
BenchmarkDotNet itself warns loudly if you forget. Every benchmark class generates its own input
in `[GlobalSetup]` (a synthetic 440Hz sine tone, not a checked-in fixture — these are
throughput/allocation measurements, not correctness tests, so the exact audio content doesn't
matter). Fixture files built there are deleted in `[GlobalCleanup]`; a benchmark that writes its
own output file (e.g. `WavIoBenchmarks`' writer, `AacStreamingBenchmarks`) creates and deletes it
inside each iteration instead.

## What's covered

| Class | Measures |
|-------|----------|
| `WavIoBenchmarks` | `WavReader`/`WavWriter` read/write throughput — the common I/O path every other codec's own `Probe`/`ConvertFile`/`CutFile` bottlenecks through |
| `AudioCutterBenchmarks` | End-to-end `AudioCutter.Convert` (decode block → encode block) throughput for WAV→MP3 and WAV→FLAC — the path a real transcode call actually runs, encode and decode together |
| `AacStreamingBenchmarks` | `AacEncoderSession`'s own incremental push throughput (1024-sample blocks, 5s mono) and allocation behavior — the encoder most directly used in a real-time/streaming pipeline (see `docs/streaming-plan.md`) |
| `CodecDecodeBenchmarks` | Pure decode throughput, isolated from encoding, for `FlacDecoder`/`Mp3Decoder`/`WavPackDecoder`/`TtaDecoder`/`AlacDecoder`/`WmaDecoder`/`AacDecoder` — FLAC/WavPack/TTA are `docs/managed-codec-rewrite-plan.md`'s own from-scratch managed rewrites; MP3 decode is still NLayer-backed, pending `docs/external-dependency-removal-plan.md` Phase 1, so its number is the baseline the in-house decoder will be compared against. AAC is a mono fixture (`AacEncoder` is mono-only), so its number is per mono stream |

## What's not covered

Encode throughput for WavPack/TTA/ALAC/Opus/Vorbis/WMA/AIFF/AU (MP3/FLAC encode is covered via
`AudioCutterBenchmarks`'s combined WAV-decode+encode measurement, AAC via `AacStreamingBenchmarks`)
and decode throughput for Opus/Vorbis/MOV/AIFF/AU. Add a benchmark class here (following `CodecDecodeBenchmarks`'s
own shape: build a fixture in `[GlobalSetup]`, one `[Benchmark]` method per codec, clean up in
`[GlobalCleanup]`) if a specific codec path's performance becomes a real question, rather than
pre-emptively covering everything.

## Results log

Point-in-time numbers from real runs, newest first, so a later change can be compared against
them. Same machine for every row of one entry; numbers from different machines aren't comparable.

### Fast MDCT (Apple M4, .NET 10.0.12, default job)

`Transform.Mdct` was a direct O(N^2) sum calling `Math.Cos` in its inner loop. A power-of-two
coefficient count of 4 or more now runs through the MDCT/DCT-IV fold and an N/2-point complex FFT
(O(N log N), plans cached per size); MP3's 18-point transform keeps the direct sum with cached
cosines, bit-identical to before. Every existing test passes unchanged (the full unit-test suite
itself went from ~26 s to ~4 s on the same machine). The WMA/AAC decode "before" numbers are the
previous entry's (that change didn't move them); the rest were re-measured on that entry's merged code.

| Benchmark | Before | After | Allocated |
|-----------|-------:|------:|----------:|
| `WmaDecoder` 10s stereo/16-bit | 11,416 ms | 134.0 ms (85x) | unchanged (54.7 MB) |
| `AacDecoder` 10s mono/16-bit | 2,946 ms | 117.9 ms (25x) | unchanged (42.9 MB) |
| `AacEncoderSession` 5s mono, incremental | 1,461 ms | 6.6 ms (220x) | unchanged (11.3 MB) |
| `AudioCutter.Convert` 10s WAV -> MP3 | 217.7 ms | 125.5 ms | unchanged (169 MB) |
| `AudioCutter.Convert` 10s WAV -> FLAC | 202.6 ms | 198.7 ms | unchanged (149 MB; no MDCT) |

WMA/AAC decode still allocate 40-55 MB per 10 s (new arrays per frame in the decoders and in
`Mdct`'s own `double[]`-returning API); pooling those is a possible next step.

### BitReader word reads + chunked FLAC MD5 (Apple M4, .NET 10.0.12, default job)

`Transform.BitReader.ReadBits`/`ReadUnary` read a 64-bit big-endian window per call instead of
looping once per bit; `FlacPcmMd5.Append` hashes 4,096-sample chunks instead of calling
`IncrementalHash.AppendData` once per sample (the real FLAC bottleneck: the decoder's bit reads
weren't). Decoded output is unchanged (the full test suite passes as is).

| Benchmark | Before | After | Allocated |
|-----------|-------:|------:|----------:|
| `FlacDecoder` 10s stereo/16-bit | 52.05 ms | 7.40 ms | unchanged (220 KB) |
| `AlacDecoder` 10s stereo/16-bit | 16.64 ms | 10.78 ms | unchanged (11.0 MB) |
| `TtaDecoder` 10s stereo/16-bit | 14.87 ms | 14.91 ms | unchanged |
| `AudioCutter.Convert` 10s WAV -> FLAC | 247.4 ms | 199.8 ms | unchanged (149 MB) |
| `WmaDecoder` 10s stereo/16-bit | 11,416 ms | 11,421 ms | unchanged |
| `AacDecoder` 10s mono/16-bit | 2,946 ms | 2,962 ms | unchanged |

WMA and AAC decode are dominated by `Transform.Mdct.Inverse`, a direct O(N^2) transform that calls
`Math.Cos` in its inner loop (slower than real time for WMA); a fast MDCT is the next perf item.
TTA didn't move: its decode time isn't in bit reads.

