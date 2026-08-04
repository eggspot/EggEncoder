using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;

namespace EggEncoder.Codecs
{
    internal interface IAudioSink : IDisposable
    {
        void WriteInterleavedSamples(int[] buffer, int frameCount);

        void Finish();
    }

    public static class AudioCutter
    {
        private const int FramesPerBlock = 4096;

        public static void Convert(string sourceFilePath, string destFilePath)
        {
            var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            switch (sourceExtension)
            {
                case ".wav":
                    ConvertWav();
                    break;
                case ".flac":
                    ConvertFlac();
                    break;
                case ".mp3":
                    ConvertMp3();
                    break;
                case ".aac":
                    ConvertAac();
                    break;
                case ".wma":
                    ConvertWma();
                    break;
                default:
                    throw new NotSupportedException($"Converting '{sourceExtension}' files is not supported by the native audio encoder");
            }

            void ConvertWav()
            {
                using var wavReader = WavReader.Open(sourceFilePath);
                using var destSink = OpenSink(destExtension, destFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, wavReader.TotalSamples);

                var buffer = new int[FramesPerBlock * wavReader.Channels];

                int framesRead;
                while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                {
                    destSink.WriteInterleavedSamples(buffer, framesRead);
                }

                destSink.Finish();
            }

            void ConvertFlac()
            {
                IAudioSink? destSink = null;

                try
                {
                    FlacDecoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        destSink ??= OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalSamples);
                        destSink.WriteInterleavedSamples(block.ToArray(), block.Length / channels);
                    });

                    destSink?.Finish();
                }
                finally
                {
                    destSink?.Dispose();
                }
            }

            void ConvertMp3()
            {
                IAudioSink? destSink = null;

                try
                {
                    Mp3Decoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        destSink ??= OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalSamples);
                        destSink.WriteInterleavedSamples(block.ToArray(), block.Length / channels);
                    });

                    destSink?.Finish();
                }
                finally
                {
                    destSink?.Dispose();
                }
            }

            void ConvertAac()
            {
                IAudioSink? destSink = null;

                try
                {
                    AacDecoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        destSink ??= OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalSamples);
                        destSink.WriteInterleavedSamples(block.ToArray(), block.Length / channels);
                    });

                    destSink?.Finish();
                }
                finally
                {
                    destSink?.Dispose();
                }
            }

            void ConvertWma()
            {
                IAudioSink? destSink = null;

                try
                {
                    WmaDecoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        destSink ??= OpenSink(destExtension, destFilePath, channels, sampleRate, bitsPerSample, totalSamples);
                        destSink.WriteInterleavedSamples(block.ToArray(), block.Length / channels);
                    });

                    destSink?.Finish();
                }
                finally
                {
                    destSink?.Dispose();
                }
            }
        }

        public static bool Cut(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds)
        {
            var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var destExtension = Path.GetExtension(destFilePath).ToLowerInvariant();

            if (destExtension != sourceExtension)
            {
                throw new NotSupportedException($"Cutting a '{sourceExtension}' source into a '{destExtension}' destination is not supported by the native audio encoder");
            }

            return sourceExtension switch
            {
                ".wav" => CutWav(),
                ".flac" => CutFlac(),
                ".mp3" => CutMp3(),
                ".aac" => CutAac(),
                _ => throw new NotSupportedException($"Cutting '{sourceExtension}' files is not supported by the native audio encoder")
            };

            bool CutWav()
            {
                using var wavReader = WavReader.Open(sourceFilePath);

                var (startSample, endSample) = GetSampleRange(wavReader.SampleRate, wavReader.TotalSamples, startInSeconds, endInSeconds);
                if (startSample >= endSample)
                {
                    return false;
                }

                using var wavWriter = WavWriter.Create(destFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, endSample - startSample);

                var buffer = new int[FramesPerBlock * wavReader.Channels];
                var currentFrame = 0L;

                int framesRead;
                while (currentFrame < endSample && (framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
                {
                    ForwardOverlap(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels), wavReader.Channels, currentFrame, startSample, endSample, wavWriter.WriteInterleavedSamples);
                    currentFrame += framesRead;
                }

                return true;
            }

            bool CutFlac()
            {
                FlacEncoderSession? session = null;
                var currentFrame = 0L;
                var rangeComputed = false;
                var startSample = 0L;
                var endSample = 0L;

                try
                {
                    FlacDecoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        if (!rangeComputed)
                        {
                            (startSample, endSample) = GetSampleRange(sampleRate, totalSamples, startInSeconds, endInSeconds);
                            rangeComputed = true;

                            if (startSample < endSample)
                            {
                                session = FlacEncoder.OpenSession(destFilePath, channels, bitsPerSample, sampleRate);
                            }
                        }

                        if (session is null)
                        {
                            return;
                        }

                        ForwardOverlap(block, channels, currentFrame, startSample, endSample, session.WriteInterleavedSamples);
                        currentFrame += block.Length / channels;
                    });

                    session?.Finish();
                }
                finally
                {
                    session?.Dispose();
                }

                return session is not null;
            }

            bool CutMp3()
            {
                Mp3EncoderSession? session = null;
                var currentFrame = 0L;
                var rangeComputed = false;
                var startSample = 0L;
                var endSample = 0L;

                try
                {
                    Mp3Decoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        if (!rangeComputed)
                        {
                            (startSample, endSample) = GetSampleRange(sampleRate, totalSamples, startInSeconds, endInSeconds);
                            rangeComputed = true;

                            if (startSample < endSample)
                            {
                                session = Mp3Encoder.OpenSession(destFilePath, channels, sampleRate, bitsPerSample);
                            }
                        }

                        if (session is null)
                        {
                            return;
                        }

                        ForwardOverlap(block, channels, currentFrame, startSample, endSample, session.WriteInterleavedSamples);
                        currentFrame += block.Length / channels;
                    });

                    session?.Finish();
                }
                finally
                {
                    session?.Dispose();
                }

                return session is not null;
            }

            bool CutAac()
            {
                AacEncoderSession? session = null;
                var currentFrame = 0L;
                var rangeComputed = false;
                var startSample = 0L;
                var endSample = 0L;

                try
                {
                    AacDecoder.Decode(sourceFilePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                    {
                        if (!rangeComputed)
                        {
                            (startSample, endSample) = GetSampleRange(sampleRate, totalSamples, startInSeconds, endInSeconds);
                            rangeComputed = true;

                            if (startSample < endSample)
                            {
                                session = AacEncoderSession.OpenSession(destFilePath, channels, sampleRate);
                            }
                        }

                        if (session is null)
                        {
                            return;
                        }

                        ForwardOverlap(block, channels, currentFrame, startSample, endSample, session.WriteInterleavedSamples);
                        currentFrame += block.Length / channels;
                    });

                    session?.Finish();
                }
                finally
                {
                    session?.Dispose();
                }

                return session is not null;
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
                _ => throw new NotSupportedException($"Converting to '{destExtension}' files is not supported by the native audio encoder")
            };
        }

        private static void ForwardOverlap(ReadOnlySpan<int> block, int channels, long blockStartFrame, long startSample, long endSample, Action<int[], int> writeInterleavedSamples)
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

            writeInterleavedSamples(block.Slice(sliceStartFrame * channels, sliceFrameCount * channels).ToArray(), sliceFrameCount);
        }

        private static (long StartSample, long EndSample) GetSampleRange(int sampleRate, long totalSamples, int startInSeconds, int endInSeconds)
        {
            var startSample = Math.Clamp((long)startInSeconds * sampleRate, 0, totalSamples);
            var endSample = Math.Clamp((long)endInSeconds * sampleRate, startSample, totalSamples);

            return (startSample, endSample);
        }
    }
}
