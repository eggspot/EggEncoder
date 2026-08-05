using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;
using EggEncoder.Results;
using EggEncoder.Waveform;

namespace EggEncoder
{
    public class NativeEncoder : IMediaEncoder
    {
        private const int FramesPerBlock = 4096;

        private readonly ILogger _logger;

        public NativeEncoder(ILogger<NativeEncoder> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ProbeResult> Probe(string filePath)
        {
            try
            {
                _logger.LogInformation($"Start native probe '{filePath}'");

                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var result = extension switch
                {
                    ".wav" => ProbeWav(filePath),
                    ".flac" => ProbeFlac(filePath),
                    ".mp3" => ProbeMp3(filePath),
                    ".aac" => ProbeAac(filePath),
                    ".wma" => ProbeWma(filePath),
                    ".mov" or ".mp4" => ProbeVideo(filePath),
                    _ => throw new NotSupportedException($"Probing '{extension}' files is not supported by the native audio encoder")
                };

                _logger.LogInformation($"Probe '{filePath}', format: '{result.FormatName}', codec: '{result.CodecName}', duration: {result.DurationSeconds}s");

                return Task.FromResult(result);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to probe '{filePath}' {e.Message}");
                throw;
            }
        }

        public Task ConvertFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                _logger.LogInformation($"Start native convert '{sourceFilePath}' to '{destFilePath}'");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Convert(sourceFilePath, destFilePath);

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to convert from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        public Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds)
        {
            try
            {
                _logger.LogInformation($"Start native cut '{sourceFilePath}' to '{destFilePath}' start {startInSeconds} end {endInSeconds}");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Cut(sourceFilePath, destFilePath, startInSeconds, endInSeconds);

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to cut from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        private static void EnsureDestinationDirectory(string destFilePath)
        {
            var directory = Path.GetDirectoryName(destFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }
        }

        private static ProbeResult ProbeWav(string filePath)
        {
            using var wavReader = WavReader.Open(filePath);
            var waveformCalculator = new WaveformCalculator(wavReader.TotalSamples, wavReader.Channels, wavReader.BitsPerSample);

            var buffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels));
            }

            var durationSeconds = wavReader.SampleRate > 0 ? (double)wavReader.TotalSamples / wavReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeWavCodec(wavReader.BitsPerSample, wavReader.IsFloatFormat);

            return new ProbeResult
            {
                FormatName = "wav",
                FormatLongName = "WAV / WAVE (Waveform Audio)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = codecName,
                CodecLongName = codecLongName,
                SampleRate = wavReader.SampleRate,
                Channels = wavReader.Channels,
                ChannelLayout = DescribeChannelLayout(wavReader.Channels),
                BitsPerSample = wavReader.BitsPerSample,
                BitRate = wavReader.SampleRate * wavReader.BitsPerSample * wavReader.Channels,
                DurationInSamples = wavReader.TotalSamples,
                TimeBase = wavReader.SampleRate > 0 ? $"1/{wavReader.SampleRate}" : null,
                Waveform = waveformCalculator.GetNormalizedWindows()
            };
        }

        private static ProbeResult ProbeFlac(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = FlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "flac",
                FormatLongName = "FLAC (Free Lossless Audio Codec)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "flac",
                CodecLongName = "FLAC (Free Lossless Audio Codec)",
                SampleRate = streamInfo.SampleRate,
                Channels = streamInfo.Channels,
                ChannelLayout = DescribeChannelLayout(streamInfo.Channels),
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                DurationInSamples = streamInfo.TotalSamples,
                TimeBase = streamInfo.SampleRate > 0 ? $"1/{streamInfo.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeMp3(string filePath)
        {
            var mp3ProbeResult = Mp3Probe.Probe(filePath);
            WaveformCalculator? waveformCalculator = null;

            Mp3Decoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            return new ProbeResult
            {
                FormatName = "mp3",
                FormatLongName = "MP3 (MPEG audio layer 3)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = mp3ProbeResult.DurationSeconds,
                CodecType = "audio",
                CodecName = "mp3",
                CodecLongName = "MP3 (MPEG audio layer 3)",
                SampleRate = mp3ProbeResult.SampleRate,
                Channels = mp3ProbeResult.Channels,
                ChannelLayout = DescribeChannelLayout(mp3ProbeResult.Channels),
                BitsPerSample = 16,
                BitRate = mp3ProbeResult.BitRate,
                IsVariableBitRate = mp3ProbeResult.IsVariableBitRate,
                TimeBase = mp3ProbeResult.SampleRate > 0 ? $"1/{mp3ProbeResult.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeAac(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = AacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "aac",
                FormatLongName = "ADTS AAC (Advanced Audio Coding)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "aac",
                CodecLongName = "AAC-LC (Advanced Audio Coding, Low Complexity profile)",
                SampleRate = streamInfo.SampleRate,
                Channels = streamInfo.Channels,
                ChannelLayout = DescribeChannelLayout(streamInfo.Channels),
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                DurationInSamples = streamInfo.TotalSamples,
                TimeBase = streamInfo.SampleRate > 0 ? $"1/{streamInfo.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeWma(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = WmaDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "asf",
                FormatLongName = "ASF (Advanced / Active Streaming Format)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "wmav2",
                CodecLongName = "Windows Media Audio 2",
                SampleRate = streamInfo.SampleRate,
                Channels = streamInfo.Channels,
                ChannelLayout = DescribeChannelLayout(streamInfo.Channels),
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                DurationInSamples = streamInfo.TotalSamples,
                TimeBase = streamInfo.SampleRate > 0 ? $"1/{streamInfo.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeVideo(string filePath)
        {
            var movProbeResult = MovProbe.Probe(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var (formatName, formatLongName) = extension switch
            {
                ".mp4" => ("mp4", "MP4 (MPEG-4 Part 14)"),
                _ => ("mov", "QuickTime / MOV")
            };

            return new ProbeResult
            {
                FormatName = formatName,
                FormatLongName = formatLongName,
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = movProbeResult.DurationSeconds,
                CodecType = "video",
                CodecName = movProbeResult.CodecFourCc,
                Width = movProbeResult.Width,
                Height = movProbeResult.Height,
                Waveform = null
            };
        }

        private static (string CodecName, string CodecLongName) DescribeWavCodec(int bitsPerSample, bool isFloatFormat)
        {
            if (isFloatFormat)
            {
                return ("pcm_f32le", "PCM 32-bit floating-point little-endian");
            }

            return bitsPerSample switch
            {
                8 => ("pcm_u8", "PCM unsigned 8-bit"),
                16 => ("pcm_s16le", "PCM signed 16-bit little-endian"),
                24 => ("pcm_s24le", "PCM signed 24-bit little-endian"),
                32 => ("pcm_s32le", "PCM signed 32-bit little-endian"),
                _ => ($"pcm_s{bitsPerSample}le", $"PCM signed {bitsPerSample}-bit little-endian")
            };
        }

        private static string DescribeChannelLayout(int channels)
        {
            return channels switch
            {
                1 => "mono",
                2 => "stereo",
                _ => $"{channels} channels"
            };
        }

        private static long GetFileSize(string filePath)
        {
            return new FileInfo(filePath).Length;
        }
    }
}
