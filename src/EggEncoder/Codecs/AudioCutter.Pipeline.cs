using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Pcm;
using System.Runtime.ExceptionServices;

namespace EggEncoder.Codecs
{
    /// <summary>One input to <see cref="AudioCutter.Mix(System.Collections.Generic.IReadOnlyList{MixInput}, string)"/>: a source file and its linear mix gain.</summary>
    public readonly record struct MixInput(string FilePath, double Gain = 1.0);

    /// <summary>
    /// Options for <see cref="AudioCutter.Cut(string, string, int, int, CutOptions)"/>. A fade, if requested,
    /// runs over the retained range (0..<see cref="FadeInSeconds"/> at the start, the last
    /// <see cref="FadeOutSeconds"/> at the end) before any transform in <see cref="Pipeline"/> runs.
    /// </summary>
    public sealed class CutOptions
    {
        /// <summary>Additional transforms to run after the fade (resampling, remixing, gain, etc). Optional.</summary>
        public PcmTransformPipeline? Pipeline { get; init; }

        /// <summary>Fade-in duration in seconds, measured from the start of the retained range. 0 = no fade-in.</summary>
        public double FadeInSeconds { get; init; }

        /// <summary>Fade-out duration in seconds, measured from the end of the retained range. 0 = no fade-out.</summary>
        public double FadeOutSeconds { get; init; }

        /// <summary>Curve shape used by both the fade-in and fade-out.</summary>
        public FadeCurve FadeCurve { get; init; } = FadeCurve.Linear;

        /// <summary>
        /// Sample representation for a <c>.wav</c> destination (see <see cref="WavSampleFormat"/>); ignored for
        /// every other destination format. Defaults to <see cref="WavSampleFormat.Integer"/>, the long-standing behavior.
        /// </summary>
        public WavSampleFormat DestinationWavFormat { get; init; } = WavSampleFormat.Integer;

        /// <summary>
        /// Sample representation for a <c>.aiff</c>/<c>.aif</c>/<c>.aifc</c> destination (see <see cref="AiffSampleFormat"/>);
        /// ignored for every other destination format. Defaults to <see cref="AiffSampleFormat.Integer"/>, the long-standing behavior.
        /// </summary>
        public AiffSampleFormat DestinationAiffFormat { get; init; } = AiffSampleFormat.Integer;
    }

    public static partial class AudioCutter
    {
        /// <summary>
        /// Same as <see cref="Convert(string, string)"/>, but runs every decoded block through
        /// <paramref name="pipeline"/> before it reaches the destination sink. The destination sink is opened
        /// lazily, after the first block successfully passes through <paramref name="pipeline"/>, using that
        /// call's actual returned (channels, sample rate, bit depth) -- so a pipeline that rejects the source
        /// format (e.g. a mismatched <see cref="EggEncoder.Pcm.ChannelRemixTransform"/>) never creates a
        /// destination file at all.
        /// </summary>
        /// <remarks>
        /// <paramref name="pipeline"/> is intended for a single Convert/Cut call: transforms such as
        /// <see cref="ResamplingTransform"/> and <see cref="FadeTransform"/> carry state across blocks. Reusing
        /// one pipeline instance across multiple calls doesn't just carry that state over -- since
        /// <see cref="PcmTransformPipeline.Flush"/> now runs automatically at the end of each call, a second
        /// call against a pipeline containing <see cref="ResamplingTransform"/> (or any transform overriding
        /// <see cref="IPcmTransform.Flush"/>) throws <see cref="InvalidOperationException"/> instead of silently
        /// producing wrong output. Build a fresh <see cref="PcmTransformPipeline"/> per call.
        /// </remarks>
        public static void Convert(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline)
        {
            Convert(sourceFilePath, destFilePath, pipeline, WavSampleFormat.Integer, AiffSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Convert(string, string, PcmTransformPipeline)"/>, but additionally selects the
        /// on-disk sample representation for a <c>.wav</c> destination (see <see cref="WavSampleFormat"/>);
        /// ignored for every other destination format.
        /// </summary>
        public static void Convert(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline, WavSampleFormat destinationWavFormat)
        {
            Convert(sourceFilePath, destFilePath, pipeline, destinationWavFormat, AiffSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Convert(string, string, PcmTransformPipeline)"/>, but additionally selects the
        /// on-disk sample representation for a <c>.aiff</c>/<c>.aif</c>/<c>.aifc</c> destination (see
        /// <see cref="AiffSampleFormat"/>); ignored for every other destination format.
        /// </summary>
        public static void Convert(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline, AiffSampleFormat destinationAiffFormat)
        {
            Convert(sourceFilePath, destFilePath, pipeline, WavSampleFormat.Integer, destinationAiffFormat);
        }

        private static void Convert(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline, WavSampleFormat destinationWavFormat, AiffSampleFormat destinationAiffFormat)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            IAudioSink? destSink = null;
            var scratch = new ScratchBuffer();
            var sourceChannels = 0;
            var sourceSampleRate = 0;
            var sourceBitsPerSample = 0;

            try
            {
                DecodeSource(sourceFilePath, sourceExtension, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    sourceChannels = channels;
                    sourceSampleRate = sampleRate;
                    sourceBitsPerSample = bitsPerSample;

                    var scratchBuffer = scratch.CopyFrom(block);
                    var (outBuffer, outFrameCount, outChannels, outSampleRate, outBitsPerSample) =
                        pipeline.Apply(scratchBuffer, block.Length / channels, channels, sampleRate, bitsPerSample);

                    // Opened only after the first successful Apply() call: if the pipeline rejects the
                    // source format (e.g. a mismatched ChannelRemixTransform), no destination file is
                    // ever created, instead of leaving a truncated header-only file behind.
                    destSink ??= OpenSinkForPipeline(destExtension, destFilePath, outChannels, outSampleRate, outBitsPerSample, pipeline.CanChangeFrameCount ? null : totalSamples, destinationWavFormat, destinationAiffFormat);
                    destSink.WriteInterleavedSamples(outBuffer, outFrameCount);
                });

                if (destSink is not null)
                {
                    var (flushBuffer, flushFrameCount, _, _, _) = pipeline.Flush(sourceChannels, sourceSampleRate, sourceBitsPerSample);
                    if (flushFrameCount > 0)
                    {
                        destSink.WriteInterleavedSamples(flushBuffer, flushFrameCount);
                    }
                }

                destSink?.Finish();
            }
            finally
            {
                destSink?.Dispose();
            }
        }

        /// <summary>
        /// Same as <see cref="Cut(string, string, int, int)"/>, but additionally applies an optional fade and
        /// transform pipeline (see <see cref="CutOptions"/>) to the retained range before it reaches the
        /// destination sink.
        /// </summary>
        public static bool Cut(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds, CutOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            IAudioSink? destSink = null;
            var rangeComputed = false;
            var startSample = 0L;
            var endSample = 0L;
            var retainedFrames = 0L;
            var currentFrame = 0L;
            var scratch = new ScratchBuffer();
            PcmTransformPipeline? effectivePipeline = null;
            var sourceChannels = 0;
            var sourceSampleRate = 0;
            var sourceBitsPerSample = 0;

            try
            {
                DecodeSource(sourceFilePath, sourceExtension, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    if (!rangeComputed)
                    {
                        (startSample, endSample) = GetSampleRange(sampleRate, totalSamples, startInSeconds, endInSeconds);
                        rangeComputed = true;

                        if (startSample < endSample)
                        {
                            retainedFrames = endSample - startSample;
                            effectivePipeline = BuildCutPipeline(options, retainedFrames, sampleRate);
                        }
                    }

                    if (effectivePipeline is null)
                    {
                        return;
                    }

                    sourceChannels = channels;
                    sourceSampleRate = sampleRate;
                    sourceBitsPerSample = bitsPerSample;

                    ForwardOverlap(block, channels, currentFrame, startSample, endSample, scratch, (trimmedBuffer, trimmedFrameCount) =>
                    {
                        var (outBuffer, outFrameCount, outChannels, outSampleRate, outBitsPerSample) =
                            effectivePipeline!.Apply(trimmedBuffer, trimmedFrameCount, channels, sampleRate, bitsPerSample);

                        // Opened only after the first successful Apply() call -- see the Convert(pipeline)
                        // overload's matching comment.
                        destSink ??= OpenSinkForPipeline(destExtension, destFilePath, outChannels, outSampleRate, outBitsPerSample, effectivePipeline.CanChangeFrameCount ? null : retainedFrames, options.DestinationWavFormat, options.DestinationAiffFormat);
                        destSink.WriteInterleavedSamples(outBuffer, outFrameCount);
                    });

                    currentFrame += block.Length / channels;
                });

                if (destSink is not null)
                {
                    var (flushBuffer, flushFrameCount, _, _, _) = effectivePipeline!.Flush(sourceChannels, sourceSampleRate, sourceBitsPerSample);
                    if (flushFrameCount > 0)
                    {
                        destSink.WriteInterleavedSamples(flushBuffer, flushFrameCount);
                    }
                }

                destSink?.Finish();
            }
            finally
            {
                destSink?.Dispose();
            }

            return destSink is not null;
        }

        /// <summary>
        /// Mixes two or more same-format (matching channels, sample rate, and bit depth) sources into one
        /// file, applying each input's gain before summing. Shorter inputs are padded with silence to the
        /// length of the longest input. Decodes every input fully into memory: intended for clip-length
        /// material, not multi-hour streams.
        /// </summary>
        public static void Mix(IReadOnlyList<MixInput> inputs, string destFilePath)
        {
            Mix(inputs, destFilePath, WavSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Mix(IReadOnlyList{MixInput}, string)"/>, but additionally selects the on-disk
        /// sample representation for a <c>.wav</c> destination (see <see cref="WavSampleFormat"/>); ignored
        /// for every other destination format.
        /// </summary>
        public static void Mix(IReadOnlyList<MixInput> inputs, string destFilePath, WavSampleFormat destinationWavFormat)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            if (inputs.Count < 2)
            {
                throw new ArgumentException("Mix requires at least two inputs", nameof(inputs));
            }

            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            // Each input is decoded independently (own reader, own local state), so this is safe to run
            // concurrently -- decoding N clip-length files one at a time paid for N files' worth of I/O
            // and CPU serially for no reason. Parallel.For bounds concurrency to a sane degree (unlike
            // firing one unbounded Task.Run per input) and still keeps every input fully in memory at
            // once, same as the old sequential version's memory profile once all N are decoded.
            //
            // A per-index failure array (rather than letting an exception escape the loop body, which
            // Parallel.For would wrap in AggregateException) lets the first *input-order* failure be
            // re-thrown afterward with its original type and stack trace intact -- matching the
            // sequential behavior this replaces (which always failed on the first bad input by index,
            // not whichever one happened to fault first under concurrent execution).
            var decoded = new (int[] Samples, int Channels, int SampleRate, int BitsPerSample)[inputs.Count];
            var failures = new Exception?[inputs.Count];

            Parallel.For(0, inputs.Count, i =>
            {
                try
                {
                    decoded[i] = DecodeFully(inputs[i].FilePath);
                }
                catch (Exception ex)
                {
                    failures[i] = ex;
                }
            });

            foreach (var failure in failures)
            {
                if (failure is not null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }

            var channels = decoded[0].Channels;
            var sampleRate = decoded[0].SampleRate;
            var bitsPerSample = decoded[0].BitsPerSample;

            for (var i = 1; i < decoded.Length; i++)
            {
                if (decoded[i].Channels != channels || decoded[i].SampleRate != sampleRate || decoded[i].BitsPerSample != bitsPerSample)
                {
                    throw new NotSupportedException(
                        $"Mix requires all inputs to share the same format; '{inputs[0].FilePath}' is {channels}ch/{sampleRate}Hz/{bitsPerSample}-bit " +
                        $"but '{inputs[i].FilePath}' is {decoded[i].Channels}ch/{decoded[i].SampleRate}Hz/{decoded[i].BitsPerSample}-bit");
                }
            }

            var maxSampleCount = 0;
            foreach (var source in decoded)
            {
                maxSampleCount = Math.Max(maxSampleCount, source.Samples.Length);
            }

            // Accumulate in double so a run of loud, positively-correlated sources doesn't clip mid-sum;
            // only the final mixed value is clamped to the destination bit depth's native range.
            var accumulator = new double[maxSampleCount];
            for (var i = 0; i < decoded.Length; i++)
            {
                var samples = decoded[i].Samples;
                var gain = inputs[i].Gain;
                for (var s = 0; s < samples.Length; s++)
                {
                    accumulator[s] += samples[s] * gain;
                }
            }

            var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
            var mixed = new int[maxSampleCount];
            for (var i = 0; i < maxSampleCount; i++)
            {
                mixed[i] = (int)Math.Clamp(Math.Round(accumulator[i]), minValue, maxValue);
            }

            var totalFrames = maxSampleCount / channels;

            using var destSink = OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalFrames, destinationWavFormat);

            var offset = 0;
            while (offset < mixed.Length)
            {
                var samplesThisBlock = Math.Min(FramesPerBlock * channels, mixed.Length - offset);
                var block = new int[samplesThisBlock];
                Array.Copy(mixed, offset, block, 0, samplesThisBlock);
                destSink.WriteInterleavedSamples(block, samplesThisBlock / channels);
                offset += samplesThisBlock;
            }

            destSink.Finish();
        }

        /// <summary>
        /// Concatenates two or more same-format (matching channels, sample rate, and bit depth) source files,
        /// in order, into one destination file. Sources are decoded and written one block at a time; for
        /// every destination format except WAV and AIFF that's fully streaming with nothing held fully in
        /// memory. A WAV or AIFF destination is the exception: since WavWriter/AiffWriter need an exact
        /// frame count when they're created and Concatenate has no cheap way to learn the combined total
        /// before every source has been decoded, the combined output is buffered in memory until the true
        /// total is known (see DeferredFixedHeaderSink).
        /// </summary>
        public static void Concatenate(IReadOnlyList<string> sourceFilePaths, string destFilePath)
        {
            Concatenate(sourceFilePaths, destFilePath, WavSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Concatenate(IReadOnlyList{string}, string)"/>, but additionally selects the
        /// on-disk sample representation for a <c>.wav</c> destination (see <see cref="WavSampleFormat"/>);
        /// ignored for every other destination format.
        /// </summary>
        public static void Concatenate(IReadOnlyList<string> sourceFilePaths, string destFilePath, WavSampleFormat destinationWavFormat)
        {
            ArgumentNullException.ThrowIfNull(sourceFilePaths);
            if (sourceFilePaths.Count < 2)
            {
                throw new ArgumentException("Concatenate requires at least two source files", nameof(sourceFilePaths));
            }

            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            IAudioSink? destSink = null;
            var scratch = new ScratchBuffer();
            var expectedChannels = -1;
            var expectedSampleRate = -1;
            var expectedBitsPerSample = -1;

            try
            {
                foreach (var sourceFilePath in sourceFilePaths)
                {
                    var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();

                    DecodeSource(sourceFilePath, sourceExtension, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        if (expectedChannels == -1)
                        {
                            expectedChannels = channels;
                            expectedSampleRate = sampleRate;
                            expectedBitsPerSample = bitsPerSample;
                            // Unlike Convert/Cut, there's no cheap way to know the combined total frame
                            // count before every source has been decoded, so a WAV destination always
                            // defers (see OpenSinkForPipeline) -- Concatenate is fully streaming only for
                            // non-WAV destinations, whose encoder sessions don't need a frame count at all.
                            destSink = OpenSinkForPipeline(destExtension, destFilePath, channels, sampleRate, bitsPerSample, exactTotalFrames: null, destinationWavFormat);
                        }
                        else if (channels != expectedChannels || sampleRate != expectedSampleRate || bitsPerSample != expectedBitsPerSample)
                        {
                            throw new NotSupportedException(
                                $"Concatenate requires all sources to share the same format; '{sourceFilePaths[0]}' is {expectedChannels}ch/{expectedSampleRate}Hz/{expectedBitsPerSample}-bit " +
                                $"but '{sourceFilePath}' is {channels}ch/{sampleRate}Hz/{bitsPerSample}-bit");
                        }

                        var buffer = scratch.CopyFrom(block);
                        destSink!.WriteInterleavedSamples(buffer, block.Length / channels);
                    });
                }

                destSink?.Finish();
            }
            finally
            {
                destSink?.Dispose();
            }
        }

        private static PcmTransformPipeline BuildCutPipeline(CutOptions options, long retainedFrames, int sampleRate)
        {
            List<IPcmTransform>? transforms = null;

            if (options.FadeInSeconds > 0 || options.FadeOutSeconds > 0)
            {
                var fadeInFrames = (long)Math.Round(options.FadeInSeconds * sampleRate);
                var fadeOutFrames = (long)Math.Round(options.FadeOutSeconds * sampleRate);
                transforms = [new FadeTransform(retainedFrames, fadeInFrames, fadeOutFrames, options.FadeCurve)];
            }

            if (options.Pipeline is { HasTransforms: true })
            {
                transforms ??= [];
                transforms.AddRange(options.Pipeline.Transforms);
            }

            return transforms is null ? new PcmTransformPipeline() : new PcmTransformPipeline([.. transforms]);
        }

        // WavWriter and AiffWriter both need an exact frame count up front (each writes a fixed-size
        // header -- RIFF for WAV, FORM/COMM/SSND for AIFF -- at Create time with no patch-up on Finish).
        // Every other sink format ignores the count, so it always streams straight through regardless of
        // exactTotalFrames. For either of those two: if the caller already knows the exact output frame
        // count (exactTotalFrames has a value -- true whenever nothing in play can change frame count,
        // e.g. a pipeline with no resampling, or no pipeline at all), open the real writer directly
        // instead of paying for the deferred sink's whole-file in-memory buffering.
        private static IAudioSink OpenSinkForPipeline(string destExtension, string destFilePath, int channels, int sampleRate, int bitsPerSample, long? exactTotalFrames, WavSampleFormat destinationWavFormat = WavSampleFormat.Integer, AiffSampleFormat destinationAiffFormat = AiffSampleFormat.Integer)
        {
            if (destExtension == ".wav")
            {
                return exactTotalFrames.HasValue
                    ? WavWriter.Create(destFilePath, channels, sampleRate, bitsPerSample, exactTotalFrames.Value, destinationWavFormat)
                    : new DeferredFixedHeaderSink(channels, totalFrames => WavWriter.Create(destFilePath, channels, sampleRate, bitsPerSample, totalFrames, destinationWavFormat));
            }

            if (destExtension is ".aiff" or ".aif" or ".aifc")
            {
                return exactTotalFrames.HasValue
                    ? AiffWriter.Create(destFilePath, channels, sampleRate, bitsPerSample, exactTotalFrames.Value, destinationAiffFormat)
                    : new DeferredFixedHeaderSink(channels, totalFrames => AiffWriter.Create(destFilePath, channels, sampleRate, bitsPerSample, totalFrames, destinationAiffFormat));
            }

            if (destExtension == ".wv")
            {
                // Unlike WAV/AIFF, WavpackSetConfiguration64 could instead take total_samples == -1
                // ("unknown") and have the real count patched into the file's first block after the
                // fact -- but requiring an exact count up front here reuses this already-proven
                // deferred-sink machinery instead of this project needing to implement that native
                // seek-and-patch sequence itself.
                return exactTotalFrames.HasValue
                    ? WavPackEncoderSession.OpenSession(destFilePath, channels, bitsPerSample, sampleRate, exactTotalFrames.Value)
                    : new DeferredFixedHeaderSink(channels, totalFrames => WavPackEncoderSession.OpenSession(destFilePath, channels, bitsPerSample, sampleRate, totalFrames));
            }

            return OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalFrames: 0, destinationWavFormat);
        }

        private static (int[] Samples, int Channels, int SampleRate, int BitsPerSample) DecodeFully(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var samples = new List<int>();
            var channels = 0;
            var sampleRate = 0;
            var bitsPerSample = 0;

            DecodeSource(filePath, extension, (block, blockChannels, blockSampleRate, blockBitsPerSample, _) =>
            {
                channels = blockChannels;
                sampleRate = blockSampleRate;
                bitsPerSample = blockBitsPerSample;
                samples.AddRange(block.ToArray());
            });

            return ([.. samples], channels, sampleRate, bitsPerSample);
        }

        /// <summary>
        /// Decodes <paramref name="sourceFilePath"/> fully to find its peak absolute sample value, for use
        /// with <see cref="PeakNormalizationTransform.MeasurePeak(long, int)"/> to get a true whole-file
        /// measurement into a pipeline that will otherwise only ever see one decode block at a time. The
        /// returned bit depth is the source's own -- pass it straight through to MeasurePeak so it can
        /// tell whether a later transform in the same pipeline has since changed the scale.
        /// </summary>
        public static (long Peak, int BitsPerSample) MeasurePeakAmplitude(string sourceFilePath)
        {
            var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var maxAbs = 0L;
            var bitsPerSample = 0;

            DecodeSource(sourceFilePath, extension, (block, _, _, blockBitsPerSample, _) =>
            {
                bitsPerSample = blockBitsPerSample;
                var blockMax = PeakNormalizationTransform.ComputeMaxAbsoluteSample(block);
                if (blockMax > maxAbs) maxAbs = blockMax;
            });

            return (maxAbs, bitsPerSample);
        }

        /// <summary>
        /// Reads a WAV file -- any supported integer bit depth, or 32-bit IEEE float -- fully into
        /// normalized float samples. A convenience for callers that want <see langword="float"/>[]
        /// directly rather than driving int PCM through a <see cref="PcmTransformPipeline"/> themselves;
        /// see <see cref="FloatSampleConverter"/> for the underlying per-sample conversion, which is the
        /// same conversion regardless of whether the source file was itself integer or float.
        /// </summary>
        public static (float[] Samples, int Channels, int SampleRate) ReadWavAsFloat(string sourceFilePath)
        {
            using var reader = WavReader.Open(sourceFilePath);
            var buffer = new int[FramesPerBlock * reader.Channels];
            var samples = new List<int>();

            int framesRead;
            while ((framesRead = reader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                samples.AddRange(buffer.AsSpan(0, framesRead * reader.Channels).ToArray());
            }

            var floatSamples = FloatSampleConverter.ToFloat(samples.ToArray(), reader.BitsPerSample);
            return (floatSamples, reader.Channels, reader.SampleRate);
        }

        /// <summary>
        /// Writes normalized float samples (range -1.0..1.0; out-of-range values are clamped) to a
        /// 32-bit IEEE float WAV file. A convenience for callers that have <see langword="float"/>[]
        /// directly (e.g. synthesized audio) rather than driving int PCM through <c>AudioCutter</c>; see
        /// <see cref="FloatSampleConverter"/> for the underlying per-sample conversion.
        /// </summary>
        public static void WriteWavFromFloat(string destFilePath, ReadOnlySpan<float> interleavedSamples, int channels, int sampleRate)
        {
            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive");
            }

            if (interleavedSamples.Length % channels != 0)
            {
                throw new ArgumentException(
                    $"interleavedSamples.Length ({interleavedSamples.Length}) is not a whole number of {channels}-channel frames",
                    nameof(interleavedSamples));
            }

            var intSamples = FloatSampleConverter.FromFloat(interleavedSamples);
            var totalFrames = intSamples.Length / channels;

            using var writer = WavWriter.Create(destFilePath, channels, sampleRate, bitsPerSample: 32, totalFrames, WavSampleFormat.Float32);
            writer.WriteInterleavedSamples(intSamples, totalFrames);
            writer.Finish();
        }

        // Buffers written samples in memory and only opens the real sink -- for any format whose writer
        // needs an exact frame count up front (WAV, AIFF) -- once Finish() reports the true total. See
        // OpenSinkForPipeline. Parameterized by an "open the real writer" callback rather than one of
        // these per format (there were briefly two, nearly identical save for which Create method they
        // called) so a future third fixed-header format doesn't mean a third near-duplicate class.
        private sealed class DeferredFixedHeaderSink : IAudioSink
        {
            private readonly int _channels;
            private readonly Func<long, IAudioSink> _openWriter;
            private readonly List<int[]> _chunks = [];
            private long _totalFrames;

            public DeferredFixedHeaderSink(int channels, Func<long, IAudioSink> openWriter)
            {
                _channels = channels;
                _openWriter = openWriter;
            }

            public void WriteInterleavedSamples(int[] buffer, int frameCount)
            {
                if (frameCount <= 0)
                {
                    return;
                }

                var sampleCount = frameCount * _channels;
                var copy = new int[sampleCount];
                Array.Copy(buffer, copy, sampleCount);
                _chunks.Add(copy);
                _totalFrames += frameCount;
            }

            public void Finish()
            {
                using var writer = _openWriter(_totalFrames);
                foreach (var chunk in _chunks)
                {
                    writer.WriteInterleavedSamples(chunk, chunk.Length / _channels);
                }

                writer.Finish();
            }

            public void Dispose()
            {
                _chunks.Clear();
            }
        }
    }
}
