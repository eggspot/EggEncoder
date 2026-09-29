using EggEncoder.Codecs;
using EggEncoder.Pcm;

namespace EggEncoder
{
    public partial class NativeEncoder : IPcmTransformEncoder
    {
        public Task ConvertFile(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline)
        {
            return ConvertFile(sourceFilePath, destFilePath, pipeline, WavSampleFormat.Integer);
        }

        public Task ConvertFile(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline, WavSampleFormat destinationWavFormat)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            try
            {
                LogInformation($"Start native pipeline convert '{sourceFilePath}' to '{destFilePath}'");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Convert(sourceFilePath, destFilePath, pipeline, destinationWavFormat);

                LogInformation($"Completed native pipeline convert '{sourceFilePath}' to '{destFilePath}'");

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to pipeline convert from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        public Task<bool> CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds, CutOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            try
            {
                LogInformation($"Start native pipeline cut '{sourceFilePath}' to '{destFilePath}' start {startInSeconds} end {endInSeconds}");

                EnsureDestinationDirectory(destFilePath);
                var produced = AudioCutter.Cut(sourceFilePath, destFilePath, startInSeconds, endInSeconds, options);

                LogInformation(produced
                    ? $"Completed native pipeline cut '{sourceFilePath}' to '{destFilePath}'"
                    : $"Completed native pipeline cut '{sourceFilePath}' to '{destFilePath}': requested range was outside the source duration, no file written");

                return Task.FromResult(produced);
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to pipeline cut from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        public Task MixFiles(IReadOnlyList<MixInput> inputs, string destFilePath)
        {
            return MixFiles(inputs, destFilePath, WavSampleFormat.Integer);
        }

        public Task MixFiles(IReadOnlyList<MixInput> inputs, string destFilePath, WavSampleFormat destinationWavFormat)
        {
            ArgumentNullException.ThrowIfNull(inputs);

            try
            {
                LogInformation($"Start native mix of {inputs.Count} inputs to '{destFilePath}'");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Mix(inputs, destFilePath, destinationWavFormat);

                LogInformation($"Completed native mix to '{destFilePath}'");

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to mix to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        public Task ConcatenateFiles(IReadOnlyList<string> sourceFilePaths, string destFilePath)
        {
            return ConcatenateFiles(sourceFilePaths, destFilePath, WavSampleFormat.Integer);
        }

        public Task ConcatenateFiles(IReadOnlyList<string> sourceFilePaths, string destFilePath, WavSampleFormat destinationWavFormat)
        {
            ArgumentNullException.ThrowIfNull(sourceFilePaths);

            try
            {
                LogInformation($"Start native concatenate of {sourceFilePaths.Count} sources to '{destFilePath}'");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Concatenate(sourceFilePaths, destFilePath, destinationWavFormat);

                LogInformation($"Completed native concatenate to '{destFilePath}'");

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to concatenate to '{destFilePath}' {e.Message}");
                throw;
            }
        }
    }
}
