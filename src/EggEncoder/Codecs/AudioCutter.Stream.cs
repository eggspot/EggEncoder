using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Au;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Codecs.Wma;
using EggEncoder.Pcm;

namespace EggEncoder.Codecs
{
    public static partial class AudioCutter
    {
        /// <summary>
        /// Converts a source <see cref="Stream"/> into a destination <see cref="Stream"/> -- for either
        /// side that isn't a local file (an HTTP response body, blob storage, an in-memory buffer).
        /// Neither <paramref name="source"/> nor <paramref name="dest"/> is disposed by this call -- the
        /// caller opened them, so the caller is responsible for disposing them. <paramref name="sourceFormat"/>/
        /// <paramref name="destFormat"/> must be given explicitly, the same reason
        /// <see cref="IMediaEncoder.Probe(Stream, AudioFormat)"/> needs one: a <see cref="Stream"/> has no
        /// file extension to dispatch on. Not every <see cref="AudioFormat"/> is supported yet, on either
        /// side (see <c>docs/feature-roadmap.md</c> item 1 for status) -- an unsupported one throws
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        public static void Convert(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat)
        {
            Convert(source, sourceFormat, dest, destFormat, new PcmTransformPipeline());
        }

        /// <summary>Same as <see cref="Convert(Stream, AudioFormat, Stream, AudioFormat)"/>, but checks <paramref name="cancellationToken"/> once per decoded block -- see <see cref="Codecs.AudioCutter.Convert(string, string, PcmTransformPipeline, System.Threading.CancellationToken)"/>'s own doc comment for the full rationale (docs/feature-roadmap.md item 5).</summary>
        public static void Convert(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat, CancellationToken cancellationToken)
        {
            Convert(source, sourceFormat, dest, destFormat, new PcmTransformPipeline(), cancellationToken);
        }

        /// <summary>Same as <see cref="Convert(Stream, AudioFormat, Stream, AudioFormat)"/>, but runs every decoded block through <paramref name="pipeline"/> before it reaches the destination sink -- see the path-based <see cref="Convert(string, string, PcmTransformPipeline)"/>'s own doc comment for the full "destination sink opened lazily" rationale, which applies identically here.</summary>
        public static void Convert(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat, PcmTransformPipeline pipeline)
        {
            Convert(source, sourceFormat, dest, destFormat, pipeline, CancellationToken.None);
        }

        /// <summary>Same as <see cref="Convert(Stream, AudioFormat, Stream, AudioFormat, PcmTransformPipeline)"/>, but checks <paramref name="cancellationToken"/> once per decoded block.</summary>
        public static void Convert(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat, PcmTransformPipeline pipeline, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(dest);
            ArgumentNullException.ThrowIfNull(pipeline);

            IAudioSink? destSink = null;
            var scratch = new ScratchBuffer();
            var sourceChannels = 0;
            var sourceSampleRate = 0;
            var sourceBitsPerSample = 0;
            var decodedFrames = 0L;

            try
            {
                DecodeSourceStream(source, sourceFormat, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    decodedFrames += block.Length / channels;
                    sourceChannels = channels;
                    sourceSampleRate = sampleRate;
                    sourceBitsPerSample = bitsPerSample;

                    var scratchBuffer = scratch.CopyFrom(block);
                    var (outBuffer, outFrameCount, outChannels, outSampleRate, outBitsPerSample) =
                        pipeline.Apply(scratchBuffer, block.Length / channels, channels, sampleRate, bitsPerSample);

                    destSink ??= OpenSinkStream(destFormat, dest, outChannels, outSampleRate, outBitsPerSample, pipeline.CanChangeFrameCount ? null : totalSamples);
                    destSink.WriteInterleavedSamples(outBuffer, outFrameCount);
                });

                if (decodedFrames == 0)
                {
                    throw NoAudioDecoded($"The {sourceFormat} source stream");
                }

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
        /// Same as <see cref="Codecs.AudioCutter.Cut(string, string, int, int)"/>, but between a source
        /// <see cref="Stream"/> and a destination <see cref="Stream"/> -- see
        /// <see cref="Convert(Stream, AudioFormat, Stream, AudioFormat)"/>'s own doc comment for the
        /// ownership/format-parameter rationale, which applies identically here.
        /// </summary>
        public static bool Cut(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat)
        {
            return Cut(source, sourceFormat, startInSeconds, endInSeconds, dest, destFormat, new CutOptions());
        }

        /// <summary>Same as <see cref="Cut(Stream, AudioFormat, int, int, Stream, AudioFormat)"/>, but checks <paramref name="cancellationToken"/> once per decoded block.</summary>
        public static bool Cut(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat, CancellationToken cancellationToken)
        {
            return Cut(source, sourceFormat, startInSeconds, endInSeconds, dest, destFormat, new CutOptions(), cancellationToken);
        }

        /// <summary>Same as <see cref="Cut(Stream, AudioFormat, int, int, Stream, AudioFormat)"/>, but additionally applies an optional fade and transform pipeline (see <see cref="CutOptions"/>) to the retained range before it reaches the destination sink.</summary>
        public static bool Cut(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat, CutOptions options)
        {
            return Cut(source, sourceFormat, startInSeconds, endInSeconds, dest, destFormat, options, CancellationToken.None);
        }

        /// <summary>Same as <see cref="Cut(Stream, AudioFormat, int, int, Stream, AudioFormat, CutOptions)"/>, but checks <paramref name="cancellationToken"/> once per decoded block.</summary>
        public static bool Cut(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat, CutOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(dest);
            ArgumentNullException.ThrowIfNull(options);

            IAudioSink? destSink = null;
            var rangeComputed = false;
            var startSample = 0L;
            var endSample = 0L;
            var retainedFrames = 0L;
            var currentFrame = 0L;
            var decodedFrames = 0L;
            var scratch = new ScratchBuffer();
            PcmTransformPipeline? effectivePipeline = null;
            var sourceChannels = 0;
            var sourceSampleRate = 0;
            var sourceBitsPerSample = 0;

            try
            {
                DecodeSourceStream(source, sourceFormat, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    decodedFrames += block.Length / channels;

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

                        destSink ??= OpenSinkStream(destFormat, dest, outChannels, outSampleRate, outBitsPerSample, effectivePipeline.CanChangeFrameCount ? null : retainedFrames);
                        destSink.WriteInterleavedSamples(outBuffer, outFrameCount);
                    });

                    currentFrame += block.Length / channels;
                });

                if (decodedFrames == 0)
                {
                    throw NoAudioDecoded($"The {sourceFormat} source stream");
                }

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

        private static void DecodeSourceStream(Stream source, AudioFormat format, AudioBlockDecodedCallback onBlockDecoded)
        {
            switch (format)
            {
                case AudioFormat.Wav:
                    using (var wavReader = WavReader.Open(source))
                    {
                        var buffer = new int[FramesPerBlock * wavReader.Channels];

                        int framesRead;
                        while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                        {
                            onBlockDecoded(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels), wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, wavReader.TotalSamples);
                        }
                    }

                    break;
                case AudioFormat.Aiff:
                    using (var aiffReader = AiffReader.Open(source))
                    {
                        var buffer = new int[FramesPerBlock * aiffReader.Channels];

                        int framesRead;
                        while ((framesRead = aiffReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                        {
                            onBlockDecoded(new ReadOnlySpan<int>(buffer, 0, framesRead * aiffReader.Channels), aiffReader.Channels, aiffReader.SampleRate, aiffReader.BitsPerSample, aiffReader.TotalSamples);
                        }
                    }

                    break;
                case AudioFormat.Au:
                    using (var auReader = AuReader.Open(source))
                    {
                        var buffer = new int[FramesPerBlock * auReader.Channels];

                        int framesRead;
                        while ((framesRead = auReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                        {
                            onBlockDecoded(new ReadOnlySpan<int>(buffer, 0, framesRead * auReader.Channels), auReader.Channels, auReader.SampleRate, auReader.BitsPerSample, auReader.TotalSamples);
                        }
                    }

                    break;
                case AudioFormat.Flac:
                    FlacDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Aac:
                    AacDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Wma:
                    WmaDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Mov:
                    MovDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Caf:
                    AlacDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Tta:
                    TtaDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Opus:
                    OpusDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Vorbis:
                    VorbisDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.WavPack:
                    WavPackDecoder.Decode(source, onBlockDecoded);
                    break;
                case AudioFormat.Mp3:
                    Mp3Decoder.Decode(source, onBlockDecoded);
                    break;
                default:
                    throw new NotSupportedException($"'{format}' is not a defined AudioFormat value, so there is no Stream-based decode for it.");
            }
        }

        private static IAudioSink OpenSinkStream(AudioFormat destFormat, Stream dest, int channels, int sampleRate, int bitsPerSample, long? exactTotalFrames)
        {
            switch (destFormat)
            {
                case AudioFormat.Wav:
                    return exactTotalFrames.HasValue
                        ? WavWriter.Create(dest, channels, sampleRate, bitsPerSample, exactTotalFrames.Value)
                        : new DeferredFixedHeaderSink(channels, totalFrames => WavWriter.Create(dest, channels, sampleRate, bitsPerSample, totalFrames));
                case AudioFormat.Aiff:
                    return exactTotalFrames.HasValue
                        ? AiffWriter.Create(dest, channels, sampleRate, bitsPerSample, exactTotalFrames.Value)
                        : new DeferredFixedHeaderSink(channels, totalFrames => AiffWriter.Create(dest, channels, sampleRate, bitsPerSample, totalFrames));
                case AudioFormat.Au:
                    return exactTotalFrames.HasValue
                        ? AuWriter.Create(dest, channels, sampleRate, bitsPerSample, exactTotalFrames.Value)
                        : new DeferredFixedHeaderSink(channels, totalFrames => AuWriter.Create(dest, channels, sampleRate, bitsPerSample, totalFrames));
                case AudioFormat.Flac:
                    return FlacEncoder.OpenSession(dest, channels, bitsPerSample, sampleRate);
                case AudioFormat.Tta:
                    return TtaEncoderSession.OpenSession(dest, channels, sampleRate, bitsPerSample);
                case AudioFormat.Aac:
                    return AacEncoderSession.OpenSession(dest, channels, sampleRate, bitsPerSample);
                case AudioFormat.Wma:
                    return WmaEncoderSession.OpenSession(dest, channels, sampleRate, bitsPerSample);
                default:
                    throw new NotSupportedException($"Converting to an AudioFormat.{destFormat} stream destination is not supported yet -- see docs/feature-roadmap.md item 1 for status. Use the path-based Convert(string, ...)/Cut(string, ...) overload for this format in the meantime.");
            }
        }
    }
}
