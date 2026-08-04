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

                _logger.LogInformation($"Probe '{filePath}', result: '{result.Result}'");

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

            var durationInSeconds = wavReader.SampleRate > 0 ? (int)(wavReader.TotalSamples / wavReader.SampleRate) : 0;

            return new ProbeResult
            {
                DurationInSeconds = durationInSeconds,
                BitsPerSample = wavReader.BitsPerSample,
                BitRate = wavReader.SampleRate * wavReader.BitsPerSample * wavReader.Channels,
                SampleRate = wavReader.SampleRate,
                Height = null,
                Width = null,
                Result = JsonSerializer.Serialize(new { wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample }),
                WaveformResult = JsonSerializer.Serialize(waveformCalculator.GetNormalizedWindows())
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

            var durationInSeconds = streamInfo.SampleRate > 0 ? (int)(streamInfo.TotalSamples / streamInfo.SampleRate) : 0;

            return new ProbeResult
            {
                DurationInSeconds = durationInSeconds,
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                SampleRate = streamInfo.SampleRate,
                Height = null,
                Width = null,
                Result = JsonSerializer.Serialize(new { streamInfo.Channels, streamInfo.SampleRate, streamInfo.BitsPerSample }),
                WaveformResult = JsonSerializer.Serialize(waveformCalculator?.GetNormalizedWindows() ?? [])
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
                DurationInSeconds = mp3ProbeResult.DurationInSeconds,
                BitsPerSample = 16,
                BitRate = mp3ProbeResult.BitRate,
                SampleRate = mp3ProbeResult.SampleRate,
                Height = null,
                Width = null,
                Result = JsonSerializer.Serialize(mp3ProbeResult),
                WaveformResult = JsonSerializer.Serialize(waveformCalculator?.GetNormalizedWindows() ?? [])
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

            var durationInSeconds = streamInfo.SampleRate > 0 ? (int)(streamInfo.TotalSamples / streamInfo.SampleRate) : 0;

            return new ProbeResult
            {
                DurationInSeconds = durationInSeconds,
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                SampleRate = streamInfo.SampleRate,
                Height = null,
                Width = null,
                Result = JsonSerializer.Serialize(new { streamInfo.Channels, streamInfo.SampleRate, streamInfo.BitsPerSample }),
                WaveformResult = JsonSerializer.Serialize(waveformCalculator?.GetNormalizedWindows() ?? [])
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

            var durationInSeconds = streamInfo.SampleRate > 0 ? (int)(streamInfo.TotalSamples / streamInfo.SampleRate) : 0;

            return new ProbeResult
            {
                DurationInSeconds = durationInSeconds,
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                SampleRate = streamInfo.SampleRate,
                Height = null,
                Width = null,
                Result = JsonSerializer.Serialize(new { streamInfo.Channels, streamInfo.SampleRate, streamInfo.BitsPerSample }),
                WaveformResult = JsonSerializer.Serialize(waveformCalculator?.GetNormalizedWindows() ?? [])
            };
        }

        private static ProbeResult ProbeVideo(string filePath)
        {
            var movProbeResult = MovProbe.Probe(filePath);

            return new ProbeResult
            {
                DurationInSeconds = movProbeResult.DurationInSeconds,
                BitsPerSample = null,
                BitRate = null,
                SampleRate = null,
                Height = movProbeResult.Height,
                Width = movProbeResult.Width,
                Result = JsonSerializer.Serialize(movProbeResult),
                WaveformResult = null
            };
        }
    }
}
