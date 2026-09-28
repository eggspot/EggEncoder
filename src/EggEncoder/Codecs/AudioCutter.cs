using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;
using EggEncoder.Pcm;

namespace EggEncoder.Codecs
{
    internal interface IAudioSink : IDisposable
    {
        void WriteInterleavedSamples(int[] buffer, int frameCount);

        void Finish();
    }

    public static partial class AudioCutter
    {
        private const int FramesPerBlock = 4096;

        /// <summary>
        /// Delegates to the pipeline-aware overload with an empty pipeline, which for an empty pipeline
        /// is exactly this method's original standalone implementation (verified: an empty
        /// PcmTransformPipeline.Apply is a no-op passthrough, and ComputeOutputFormat with no transforms
        /// returns the source format unchanged) -- kept as one implementation instead of two so a future
        /// fix to the shared decode/sink lifecycle can't be applied to one path and missed in the other.
        /// </summary>
        public static void Convert(string sourceFilePath, string destFilePath)
        {
            Convert(sourceFilePath, destFilePath, new PcmTransformPipeline());
        }

        /// <summary>Delegates to the pipeline-aware overload with empty CutOptions; see the Convert() overload's remarks.</summary>
        public static bool Cut(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds)
        {
            return Cut(sourceFilePath, destFilePath, startInSeconds, endInSeconds, new CutOptions());
        }

        private static void DecodeSource(string sourceFilePath, string sourceExtension, AudioBlockDecodedCallback onBlockDecoded)
        {
            switch (sourceExtension)
            {
                case ".wav":
                    using (var wavReader = WavReader.Open(sourceFilePath))
                    {
                        var buffer = new int[FramesPerBlock * wavReader.Channels];

                        int framesRead;
                        while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                        {
                            onBlockDecoded(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels), wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, wavReader.TotalSamples);
                        }
                    }

                    break;
                case ".flac":
                    FlacDecoder.Decode(sourceFilePath, onBlockDecoded);
                    break;
                case ".mp3":
                    Mp3Decoder.Decode(sourceFilePath, onBlockDecoded);
                    break;
                case ".aac":
                    AacDecoder.Decode(sourceFilePath, onBlockDecoded);
                    break;
                case ".wma":
                    WmaDecoder.Decode(sourceFilePath, onBlockDecoded);
                    break;
                case ".mov":
                case ".mp4":
                    MovDecoder.Decode(sourceFilePath, onBlockDecoded);
                    break;
                default:
                    throw new NotSupportedException($"Decoding '{sourceExtension}' files is not supported by the native audio encoder");
            }
        }

        private static IAudioSink OpenSink(string destExtension, string destFilePath, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            return destExtension switch
            {
                ".wav" => WavWriter.Create(destFilePath, channels, sampleRate, bitsPerSample, totalFrames),
                ".flac" => FlacEncoder.OpenSession(destFilePath, channels, bitsPerSample, sampleRate),
                ".mp3" => Mp3Encoder.OpenSession(destFilePath, channels, sampleRate, bitsPerSample),
                ".aac" => AacEncoderSession.OpenSession(destFilePath, channels, sampleRate),
                ".wma" => WmaEncoderSession.OpenSession(destFilePath, channels, sampleRate),
                _ => throw new NotSupportedException($"Converting to '{destExtension}' files is not supported by the native audio encoder")
            };
        }

        private static void ForwardOverlap(ReadOnlySpan<int> block, int channels, long blockStartFrame, long startSample, long endSample, ScratchBuffer scratch, Action<int[], int> writeInterleavedSamples)
        {
            var blockFrames = block.Length / channels;
            var blockEndFrame = blockStartFrame + blockFrames;

            var overlapStart = Math.Max(blockStartFrame, startSample);
            var overlapEnd = Math.Min(blockEndFrame, endSample);

            if (overlapStart >= overlapEnd)
            {
                return;
            }

            var sliceStartFrame = (int)(overlapStart - blockStartFrame);
            var sliceFrameCount = (int)(overlapEnd - overlapStart);
            var slice = block.Slice(sliceStartFrame * channels, sliceFrameCount * channels);

            var buffer = scratch.CopyFrom(slice);
            writeInterleavedSamples(buffer, sliceFrameCount);
        }

        private static (long StartSample, long EndSample) GetSampleRange(int sampleRate, long totalSamples, int startInSeconds, int endInSeconds)
        {
            var startSample = Math.Clamp((long)startInSeconds * sampleRate, 0, totalSamples);
            var endSample = Math.Clamp((long)endInSeconds * sampleRate, startSample, totalSamples);

            return (startSample, endSample);
        }

        // Reused, grow-only backing array so repeated block callbacks don't allocate on every invocation.
        private sealed class ScratchBuffer
        {
            private int[] _buffer = [];

            public int[] CopyFrom(ReadOnlySpan<int> source)
            {
                if (_buffer.Length < source.Length)
                {
                    _buffer = new int[source.Length];
                }

                source.CopyTo(_buffer);
                return _buffer;
            }
        }
    }
}
