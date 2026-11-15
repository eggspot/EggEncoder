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
| `VideoFilterBenchmarks` | One 1080x1920 frame through `FitTransform` (Cover, bicubic, from 1920x1080), `BoxBlurTransform` (radius 20/10) and `OverlayTransform` (a 400x200 logo), one thread each — the per-frame cost of the filters a video render spends most of its time in (video backlog item 21). Each iteration includes cloning the input frame |
| `H264DecodeBenchmarks` | `H264Decoder` on a whole Annex B file named by `EGGENCODER_H264_BENCHMARK_FILE` (no H.264 file is checked in for this; make one locally, for example with ffmpeg/x264), one thread, frames disposed as they come out |
| `H264EncodeBenchmarks` | `H264Encoder` on 30 frames of 1080x1920 (a synthetic pattern, or raw `yuv420p` frames named by `EGGENCODER_H264_ENCODE_BENCHMARK_FILE`), High profile CRF 23, on 1 and 4 threads |
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

### H.264 encode (Intel Core i7-8850H, 6 cores / 12 threads, .NET 10.0.12, AVX2, scratch harness timing `Encode` calls)

30 frames of 1080x1920 `testsrc2` (mostly flat bars, a few moving objects), High profile:

| Setting | Frames per second | Size | Luma PSNR |
|---------|-----:|-----:|-----:|
| CRF 23, 1 thread | 9.8 | 1,634,541 B | 50.2 dB |
| CRF 23, 4 threads | 13.5 | same bytes | same |
| CRF 28, 4 threads | 15.9 | 1,126,042 B | 45.8 dB |
| CRF 29, 4 threads | 15.4 | 1,009,000 B | 44.7 dB |
| x264 `veryfast crf 20`, 4 threads | about 50 | 955,302 B | 45.5 dB |

At equal PSNR (45.5 dB, interpolated between CRF 28 and 29) the output is about 14.5% larger than
x264's. 60 frames of a detailed clip (a zooming Mandelbrot set with temporal noise) run at 3.0 fps on
one thread and 5.5 fps on four. The 4-thread rate is limited by the single entropy-writing stage and
by the wavefront on frames whose expensive macroblocks sit in a few columns. Before this entry the
wavefront waited by spinning and yielding, and a 4-thread run of the detailed clip on a busy machine
once took 2,564 s; a blocking wait after a short spin fixed it (10.9 s).

### H.264 decode (Intel Core i7-8850H, .NET 10.0.12, AVX2, short job, `--inProcess`)

Three 1080p streams of 60 frames made locally from `testsrc2` with ffmpeg's libx264: High profile
(medium preset: B-pyramid, 8x8 transform, CABAC, weighted P), Main profile CABAC P (three
references, no B) and Baseline CAVLC P. Before this entry the decoder ran at 25, 37 and 58 fps;
predicting straight into the frame, vectorised interpolation and bi-prediction, one 16x16
prediction for uniform direct macroblocks, an allocation-free intra 8x8 path, pooled per-picture
arrays and cheaper deblocking decisions (a coefficient mask per macroblock, an equality check before
the motion rule, no bounds checks in the sample filter) brought it to:

| Stream | Mean (60 frames) | Frames per second | Allocated |
|--------|-----:|-----:|----------:|
| High profile, medium preset | 1.344 s | 45 | 14.5 MB |
| Main profile CABAC P | 983 ms | 61 | 12.7 MB |
| Baseline CAVLC P | 654 ms | 92 | 8.5 MB |

For comparison, ffmpeg's decoder (hand-written SIMD assembly) on the same files and machine: 0.33 s,
0.28 s and 0.16 s with one thread, 0.11 s, 0.10 s and 0.08 s with its default frame threads. The
renderer needs well under one core for real-time 1080p decode, so frame threading is left for later.

### Video filters (Intel Core i7-8850H, .NET 10.0.12, AVX2, default job, `--inProcess`)

Budgets from the video backlog's item 21 plan: the composition (item 31) needs about 15 frames a
second end to end on 4 vCPU, so one 1080x1920 frame gets at most 15 ms for the scale and 10 ms for
a large blur on one core. First version: 20.0 ms / 12.3 ms (and 12 MB allocated per blurred frame);
after removing bounds checks from the horizontal pass, accumulating the vertical taps in registers,
narrowing with vectors, and reusing the blur's row sums:

| Benchmark | Mean | Allocated |
|-----------|-----:|----------:|
| `FitTransform` Cover bicubic 1920x1080 -> 1080x1920 | 11.4 ms | 9.6 KB |
| `BoxBlurTransform` radius 20/10 on 1080x1920 | 7.2 ms | 9.5 KB |
| `OverlayTransform` 400x200 logo on 1080x1920 | 1.1 ms | 0.2 KB |

The local SDK has no .NET 11, so BenchmarkDotNet's generated project can't build the library's
net11 target here; `--inProcess` runs the benchmarks in the host process instead.

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

