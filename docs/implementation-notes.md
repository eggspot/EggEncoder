# Implementation notes

Reasons and edge cases behind code that keeps no comments. Each section names the files it covers.

## WAV reading (`Codecs/Wav/WavReader.cs`)

- **Chunk walk.** A corrupted chunk size can point far past the end of the file. The next position is
  clamped to the stream's length: a `FileStream` accepts a position past the end (ending the walk), but a
  `MemoryStream` throws `ArgumentOutOfRangeException` once the offset passes `int.MaxValue`.
- **Truncated chunks.** The walk only checks the position between chunks, so a chunk whose fields run past
  the end (a truncated `fmt `) makes `BinaryReader` throw `EndOfStreamException`. `OpenCore` converts that
  to `InvalidDataException`, as `AiffReader` and `AuReader` do, and disposes only a stream it opened
  itself.
- **Unsized `data` chunks.** A `data` chunk whose size is 0xFFFFFFFF, points past the end of the stream, or
  is 0 with no well-formed chunk after it (printable four-character id and a size that fits) runs to the
  end of the stream, PCM floored to whole frames. Streaming writers that never patch their header leave
  these sizes; ffmpeg and libsndfile read them the same way. The walk stops there, because the bytes
  after are audio. A `fact` count of 0, or one next to such a chunk, is ignored.
- **`fact` chunk.** For non-PCM formats the `fact` total is the authoritative sample count; it lets a
  decoder drop the padding at the end of the last block. Without it, the total comes from the data: IMA
  and MS ADPCM count whole blocks, G.711 is one byte per sample, and Yamaha ADPCM is two nibbles per byte
  (undercounting by at most mono's one pad nibble).
- **IMA ADPCM block bound.** `wSamplesPerBlock` may not exceed what the block's data bytes hold
  (`(blockAlign - 4·channels)·8 / (4·channels) + 1`); a larger value would run the block decode past its
  buffer.
- **MS ADPCM block bound.** Mono can leave the last nibble of its final byte unused when the remaining
  sample count is odd, so its bound is `2 + 2·(blockAlign - 7)`; stereo uses both nibbles of every byte,
  so its bound is exactly `2 + (blockAlign - 14)`.
- **ADPCM final blocks.** Real encoders pad the last block to the full block size and rely on the `fact`
  total to mark where audio ends (checked against an ffmpeg-made file). A short final block only happens in
  a truncated file, and decoding stops there.
- **Yamaha ADPCM.** No blocks: each channel's predictor and step carry through the whole stream, and one
  leftover high nibble is kept between calls when an odd number of nibbles has been read (mono only). In a
  truncated file the reader stops at the last whole frame instead of keeping `ReadByte`'s -1 as a pending
  byte: `-1 & 0x0F` masks to 15, but the matching `-1 >> 4` stays -1 and indexes the step tables with a
  negative index (found by mutation testing).
- **G.711.** Any channel count works: it's a per-sample companding formula with no block structure. It
  decodes to 16-bit samples but stores one byte per sample.
- **Float WAV.** Samples go through `FloatSampleConverter`, so NaN, infinities and out-of-range values are
  handled the same at every entry point.
- **`LIST`/`INFO` tags.** Only a `LIST` chunk whose sub-type is `INFO` is read as tags (`adtl` cue labels
  and other sub-types are skipped). A file can carry several `LIST` chunks, so a later non-`INFO` one never
  replaces tags already found. Tags are optional metadata, so a malformed list keeps the tags read before
  the problem instead of failing the open. Sub-chunk bounds are checked against both the `LIST` size and
  the stream's real length, so a corrupted size can't allocate a huge buffer. Strings are read as UTF-8 (a
  superset of the conventional 7-bit ANSI) with trailing nulls trimmed.

## Converting, cutting and mixing (`Codecs/AudioCutter*.cs`)

- **Sinks open on the first block.** `Convert` and `Cut` open the destination only after the pipeline's
  first successful `Apply`, so a pipeline that rejects the source format (a mismatched
  `ChannelRemixTransform`, say) leaves no header-only file behind. A source that decodes to no samples
  throws `InvalidDataException` instead of finishing without a file.
- **Fixed-header formats.** `WavWriter` and `AiffWriter` write their sizes at `Create` with no patch-up,
  so they need the exact frame count first. When nothing can change the frame count, the real writer
  opens directly; otherwise `DeferredFixedHeaderSink` buffers the samples and opens it in `Finish`. The
  deferred sink takes an "open the real writer" callback rather than one class per format.
- **AU and WavPack outputs** also ask for an exact count, although AU has an "unknown size" sentinel and
  WavPack can take `total_samples == -1` and patch it later. Reusing the deferred sink avoids a second,
  format-specific patch-up path.
- **`Concatenate`** can't know the combined frame count before every source is decoded, so a WAV
  destination always defers; other destinations stream straight through.
- **`Mix`** decodes its inputs in parallel (`Parallel.For`, each input with its own reader), keeping all
  of them in memory as before. Failures are collected per index and the first one in input order is
  rethrown with its original type and stack, as the earlier sequential loop did. Samples are summed in
  `double` and clamped only once, so loud correlated inputs don't clip mid-sum.
- **`Stream` destinations.** WAV, AIFF and AU need no `CanSeek` (every size is known up front). FLAC needs
  it: `FlacEncoderSession.Finish` seeks back to patch STREAMINFO. TTA and WMA buffer their encoded frames
  until `Finish` and write in one forward pass. AAC writes each ADTS frame as soon as it's encoded. AAC and
  WMA take the source bit depth and rescale to 16-bit, since a 24-bit source would otherwise wrap into
  noise.
- **Scratch buffer.** Block callbacks reuse one grow-only array instead of allocating per block.

## Probing (`NativeEncoder.cs`)

- The path and `Stream` forms of the MP3 and MOV/MP4 probes share one mapping helper each, so the two can't
  drift apart.
- A MOV/MP4 probe whose audio track is missing or can't be decoded (non-AAC-LC, more than two channels)
  still returns the video metadata with no audio fields, instead of failing the whole probe.

## FLAC output that stores accept (`Codecs/Flac/FlacFrameEncoder.cs`, `FlacFrameHeader.cs`, `FlacEncoder.cs`)

Files have to pass the stores' own validators, not just decode. Apple's FLAC validator (iTunes Transporter's
mediatoolkit) rejected files from v4.36.0 to v4.114.1 ("Invalid use of a reserved value in frame 0", then a CRC
failure on frame 1) because every frame header used sample rate code 0 and sample size code 0 ("take it from
STREAMINFO"). RFC 9639 allows that, and libFLAC and ffmpeg decode it, so the round-trip tests passed. The native
libFLAC encoder used up to v4.35 always wrote the explicit codes, and Spotify's ingestion failed on the same files.

So the encoder follows what libFLAC 1.4.3 writes:
- block size, sample rate and sample size codes as in libFLAC's `FLAC__frame_add_header`, with the 8-bit kHz,
  16-bit Hz and 16-bit tens-of-Hz end-of-header forms for other rates; code 0 only when nothing else fits;
- Rice method 0 (4-bit parameters) unless a parameter above 14 is needed, no escape partitions, and a VERBATIM
  subframe for incompressible blocks (libFLAC's encoder never writes escapes by default);
- STREAMINFO with the real smallest and largest frame sizes.

`FlacFrameHeaderTest` checks the codes for every supported rate and bit depth. Its libFLAC comparison runs when
`EGGENCODER_FLAC_PATH` points at a `flac` binary (1.4.x): `flac -t` must verify the MD5, and every frame's codes
and block size must equal a `flac -8` encode of the same audio.

Release check (manual, Apple's jars can't go in this public repo): run iTunes Transporter's FLAC validator
(`com.apple.jingle.leghorn.audio.FlacDescriberApplication <file> false`, classpath = Transporter's `share/OSGi-Bundles`
and `lib` jars plus Guava) on a 16-bit 44.1 kHz and a 24-bit 48 kHz encode after any change to the FLAC encoder. A
file passes when no `AudioDescriptionException` is printed; the Transporter build used here then stops on a missing
report-writer class, which happens for passing files only.

## FLAC encoder choices (`Codecs/Flac/FlacFrameEncoder.cs`)

Since v4.115.0 the encoder decides by estimates and computes exact residuals only for what it chose, the way libFLAC
and ffmpeg do:
- **Fixed order:** the smallest sum of |residual| over orders 0-4, all computed in one pass from sample 4 (libFLAC's
  `compute_best_predictor`). The sums add in 32-bit lanes for as many steps as the bit depth allows without overflow,
  then widen.
- **LPC order:** the smallest `bits(error) x (n - order) + order x (precision + bits per sample)`, with
  `bits = max(0, 0.5 log2(error / 2n))` from the Levinson error (libFLAC's `compute_best_order`).
- **Stereo:** estimated bits per channel from the best fixed sum, using the Rice estimate below capped at the raw
  sample size. The log formula alone assumes Laplacian residuals and underestimates noise by about 1.5 bits per
  sample, which picked mid/side for uncorrelated noise.
- **Rice parameter:** from the zigzag total `2 sum|r| - negatives`, the best of log2(mean) and its neighbours
  under `n(k+1) + ((total - n/2) >> k)` (ffmpeg's estimate). The written bits are exact either way.

Wide LPC sums (24-bit and the 25-bit side channel) run in `double` lanes: products stay below 2^39 and sums below
2^42, so every value is exact, and the floor-shift becomes `floor(sum / 2^shift)`. Converting back adds 1.5 x 2^52,
not 2^52: a negative value below 2^52 changes the exponent and the bits stop being the integer.
