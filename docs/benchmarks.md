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
| `CompositionBenchmarks` | `VideoComposition.Render` of 60 s at 1080x1920 30 fps built from the H.264 MP4 named by `EGGENCODER_COMPOSITION_BENCHMARK_CLIP` (looped as scenes), four threads |
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

### MP3 encode (Intel Core i7-8850H, 6 cores / 12 threads, .NET 10, best of 6 encodes)

60 s of stereo 44.1 kHz 16-bit WAV (a 440 Hz tone plus pink noise), `Mp3Encoder.Encode` at 192 kb/s CBR and
`Mp3Encoder.EncodeVbr` at quality 4, including reading the WAV. ffmpeg 2021 build with `libmp3lame` (`-b:a 192k`
and `-q:a 4`): 0.75 s wall, of which 0.15 s is ffmpeg starting and reading the file (`-f null`).

| Encoder | CBR 192 | VBR q4 |
|---------|-----:|-----:|
| Before (one thread) | 2,794 ms | 2,362 ms |
| One thread, after the changes below | about 1,140 ms | about 925 ms |
| Default (up to 4 threads) | 462 ms | 465 ms |
| ffmpeg + LAME | about 600 ms | about 600 ms |

Every row writes the same bytes. What changed: the polyphase filter keeps its 512-sample history in a buffer it shifts
once every 112 calls instead of every call, and runs its window and cosine matrix with one vector lane per output
(each lane keeps the scalar summation order, with separate multiply and add, so results are identical); the
18-point MDCT runs inputs-outer with vector lanes over outputs (same order per output); the quantiser takes
`|x|^0.75` once per granule instead of `Math.Pow` per coefficient per gain, stops at the last coefficient that can
be nonzero for the gain, and VBR reuses a granule's plan for a gain across every bitrate it tries; the session keeps
pending samples in one array instead of removing a frame from the front of a list each time. Frames are analysed in
order (the two channels' filter banks on two threads), collected in batches of 32 and planned and written on up to
three more threads while the next batch is analysed, because no frame borrows from another (no bit reservoir).

### MP3 decode (Intel Core i7-8850H, 6 cores / 12 threads, .NET 10.0.12, AVX2 + FMA, scratch harness, best of 30 decodes after one warm-up)

60 s of stereo 44.1 kHz 192 kb/s LAME MP3 (two tones with noise), decoded to interleaved 16-bit PCM.
ffmpeg (2021 build) measured with `-threads 1 -benchmark ... -c:a pcm_s16le -f null -`, best of four
runs; its MP3 decoder has no threading. Output within 1 LSB of ffmpeg's on every sample.

| Decoder | Time | Real time |
|---------|-----:|-----:|
| `Mp3Decoder`, 1 thread | 62 ms | about 970x |
| `Mp3Decoder`, default (4 threads) | 21 ms | about 2,850x |
| ffmpeg, 1 thread | 73-77 ms | about 800x |

How it got there from 207 ms on one core: a symmetric IMDCT, 256-bit synthesis with the 32 DCT
outputs expanded into the V buffer by reversed vector loads, the hybrid filter writing time-slot-major
so synthesis reads contiguous slots, fused multiply-add where the CPU has it, requantisation fused into
Huffman decoding for long blocks with one 32-bit peek per pair (signs read from the same window,
branch-free) and pair values baked into the lookup entries, and, the largest single step, several
independent accumulators in the window, matrix and IMDCT loops, which were latency-bound on one chain
of dependent multiply-adds each.

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
renderer needs well under one core for real-time 1080p decode.

Later, deblocking moved to a second thread running a row behind decoding (best of 6-8 runs in a
scratch harness, same machine): 1.025 s (59 fps), 0.825 s (73 fps) and 0.526 s (114 fps) for the
same three streams. On the decoding thread, inter prediction is now about 44% and CABAC residual
decoding about 24% of the time.

Then frame threads: several pictures decode at once, each waiting on its references' deblocked rows
(scratch harness, best of 8 runs, ffmpeg timed in the same session since this laptop's speed drifts
with temperature):

| Stream | One picture at a time | Default frame threads | ffmpeg, 1 thread | ffmpeg, default threads |
|--------|-----:|-----:|-----:|-----:|
| High profile, medium preset | 1.195 s | 0.410 s | 0.382 s | 0.115 s |
| Main profile CABAC P | 0.935 s | 0.314 s | 0.315 s | 0.109 s |
| Baseline CAVLC P | 0.599 s | 0.224 s | 0.176 s | 0.082 s |

Scaling stops near 3x on this 6-core laptop: with every core busy each thread runs about 1.6 times
slower than alone (all-core clocks and hyper-threads). Spinning before a blocking wait cost about
10%, so the decoder's progress counters block at once. The per-thread decode speed is now the limit.

Run serially (`DOTNET_PROCESSOR_COUNT=1`, so deblocking shares the decoding thread), deblocking was
29-31% of the time and motion compensation 18-26%; inside deblocking the edge-strength decisions cost
more than the sample filter. A uniform-motion flag per macroblock (one motion check per edge between
two such macroblocks, none for their internal edges) and filtering eight samples at once (vertical
edges through an 8x8 byte transpose) changed, interleaved A/B runs in one session:

| Stream | Serial before | Serial after | Frame threads before | Frame threads after |
|--------|-----:|-----:|-----:|-----:|
| High profile, medium preset | 1.268 s | 1.075 s | 0.394 s | 0.377 s |
| Main profile CABAC P | 0.972 s | 0.925 s | 0.358 s | 0.344 s |
| Baseline CAVLC P | 0.682 s | 0.693 s | 0.250 s | 0.243 s |

A per-thread profile (TraceEvent over a `dotnet-trace` capture, serial run) then showed explicit
weighting applied with identity weights (x264 writes a weight table but most references keep the
default weight), implicit bi-prediction with equal weights, and full-pel motion compensation copying
4-16-byte rows through the general memmove. Skipping identity weights, averaging when the weights are
equal, copying full-pel rows with fixed-width loads, and a progress wait that no longer takes a lock
once the rows are ready: serial 1.048 -> 0.947 s (High), 0.860 -> 0.723 s (Main), 0.695 -> 0.631 s
(Baseline); with frame threads 0.422 -> 0.386 s, 0.413 -> 0.367 s, Baseline unchanged (A/B, same
session).

This laptop runs other work (about 20% CPU in the background) and drifts with temperature, so later
steps are measured with `abtool`: both builds loaded into one process in separate
`AssemblyLoadContext`s, alternated run by run, the median ratio of adjacent pairs reported, the
process at high priority and pinned to one core. An A/A run reads 0.99. Interpolating the second
list straight into the frame with averaging (no temporary block, no separate pass) when bi-prediction
is a plain average: 0.980 on the High profile stream. Allowing one picture in flight per logical
processor (12 here) instead of at most eight: about 10% with frame threads. Same session, default
threads:

| Stream | `H264Decoder` | ffmpeg, default threads |
|--------|-----:|-----:|
| High profile, medium preset | 0.253 s | 0.098 s |
| Main profile CABAC P | 0.207 s | 0.101 s |
| Baseline CAVLC P | 0.202 s | 0.070 s |

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

