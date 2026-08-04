using EggEncoder.Results;
using System.Diagnostics;

namespace EggEncoder
{
    public class FfmpegEncoder : IMediaEncoder
    {
        private readonly IFfmpegEncoderBinFactory _ffmpegEncoderBinFactory;
        private readonly ILogger _logger;

        private static readonly SemaphoreSlim _semaphoreLock = new(1);
        private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

        public FfmpegEncoder(
            IFfmpegEncoderBinFactory ffmpegEncoderBinFactory,
            ILogger<FfmpegEncoder> logger)
        {
            _ffmpegEncoderBinFactory = ffmpegEncoderBinFactory ?? throw new ArgumentNullException(nameof(ffmpegEncoderBinFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProbeResult> Probe(string filePath)
        {
            var result = await GetProbeResult();
            result.WaveformResult = await GetWaveformResult();

            return result;

            async Task<ProbeResult> GetProbeResult()
            {
                try
                {
                    _logger.LogInformation($"Start probe '{filePath}'");

                    await _semaphoreLock.WaitAsync();
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = _ffmpegEncoderBinFactory.FfprobePath,
                        Arguments = $"-v error -i \"{filePath}\" -print_format json -show_format -show_streams",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });

                    var output = process!.StandardOutput.ReadToEnd();
                    var error = process!.StandardError.ReadToEnd();
                    await process.WaitForExitAsync();

                    _logger.LogInformation($"Probe '{filePath}', output: '{output}'");
                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new Exception(error);
                    }

                    var ffmpegProbeResult = JsonSerializer.Deserialize<FfmpegProbeResult>(output);
                    var streamResult = ffmpegProbeResult?.Streams?.FirstOrDefault(s => s.HasSampleRate());

                    var bitsPerSample = !string.IsNullOrWhiteSpace(streamResult!.BitsPerRawSample)
                        ? Convert.ToInt32(streamResult.BitsPerRawSample)
                        : streamResult.BitsPerSample;

                    var result = new ProbeResult
                    {
                        DurationInSeconds = (int)Convert.ToDouble(ffmpegProbeResult?.Format?.DurationInSeconds),
                        BitsPerSample = bitsPerSample,
                        BitRate = int.TryParse(streamResult.BitRate, out var _parsedBitRate) ? _parsedBitRate : default,
                        SampleRate = int.TryParse(streamResult.SampleRate, out var _parsedSampleRate) ? _parsedSampleRate : default,
                        Height = streamResult.Height,
                        Width = streamResult.Width,
                        Result = JsonSerializer.Serialize(new
                        {
                            ffmpegProbeResult!.Format,
                            Stream = streamResult
                        }, _jsonSerializerOptions)
                    };

                    return result;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to probe '{filePath}' {e.Message}");
                    throw;
                }
                finally
                {
                    _semaphoreLock.Release();
                }
            }

            async Task<string?> GetWaveformResult()
            {
                var output = string.Empty;
                try
                {
                    _logger.LogInformation($"Start probe waveform '{filePath}'");

                    string NormalizeFilePath()
                    {
                        return filePath
                            .Replace(@":\", @"\\:/");
                    }

                    var inputFormat = $"amovie={NormalizeFilePath()},asetnsamples=44100,astats=metadata=1:reset=1";

                    await _semaphoreLock.WaitAsync();
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = _ffmpegEncoderBinFactory.FfprobePath,
                        Arguments = $"-v error -f lavfi -i \"{inputFormat}\" -show_entries frame_tags=lavfi.astats.Overall.RMS_difference -of json",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });

                    output = process!.StandardOutput.ReadToEnd();
                    var error = process!.StandardError.ReadToEnd();
                    await process.WaitForExitAsync();

                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogError($"Failed to probe waveform '{filePath}' {error}");
                        return null;
                    }

                    var probeWaveformResult = JsonSerializer.Deserialize<FfmpegProbeWaveformResult>(output);
                    return JsonSerializer.Serialize(probeWaveformResult?.Values, _jsonSerializerOptions);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to probe waveform '{filePath}' {e.Message}, output: '{output}'");
                    return null;
                }
                finally
                {
                    _semaphoreLock.Release();
                }
            }
        }

        public async Task ConvertFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                _logger.LogInformation($"Start convert '{sourceFilePath}' to '{destFilePath}'");

                var directory = Path.GetDirectoryName(destFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var customArguments = "";
                if (destFilePath.EndsWith(".mp3", StringComparison.InvariantCultureIgnoreCase))
                {
                    customArguments = "-b:a 320k";
                }

                await _semaphoreLock.WaitAsync();
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = _ffmpegEncoderBinFactory.FfmpegPath,
                    Arguments = $"-y -v error -i \"{sourceFilePath}\" {customArguments} \"{destFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                var output = process!.StandardOutput.ReadToEnd();
                var error = process!.StandardError.ReadToEnd();
                await process.WaitForExitAsync();

                _logger.LogInformation($"Convert '{sourceFilePath}' to '{destFilePath}', output: '{output}'");
                if (!string.IsNullOrEmpty(error))
                {
                    throw new Exception(error);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to convert from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
            finally
            {
                _semaphoreLock.Release();
            }
        }

        public async Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds)
        {
            try
            {
                _logger.LogInformation($"Start cut '{sourceFilePath}' to '{destFilePath}' start {startInSeconds} end {endInSeconds}");

                var directory = Path.GetDirectoryName(destFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var extension = Path.GetExtension(destFilePath)[1..];

                await _semaphoreLock.WaitAsync();
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = _ffmpegEncoderBinFactory.FfmpegPath,
                    Arguments = $"-y -v error -ss {GetDuration(startInSeconds)} -to {GetDuration(endInSeconds)} -i \"{sourceFilePath}\" -c:a {extension} \"{destFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                var output = process!.StandardOutput.ReadToEnd();
                var error = process!.StandardError.ReadToEnd();
                await process.WaitForExitAsync();

                _logger.LogInformation($"Cut '{sourceFilePath}' to '{destFilePath}', output: '{output}'");
                if (!string.IsNullOrEmpty(error))
                {
                    throw new Exception(error);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to cut from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
            finally
            {
                _semaphoreLock.Release();
            }

            static string GetDuration(int seconds)
            {
                var timeSpan = TimeSpan.FromSeconds(seconds);
                return $"{(int)Math.Floor(timeSpan.TotalHours):00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
            }
        }
    }
}
