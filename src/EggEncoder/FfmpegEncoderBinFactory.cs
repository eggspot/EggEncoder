namespace EggEncoder
{
    public interface IFfmpegEncoderBinFactory
    {
        string FfmpegPath { get; }

        string FfprobePath { get; }
    }

    public class FfmpegEncoderBinFactory : IFfmpegEncoderBinFactory
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        public FfmpegEncoderBinFactory(FfmpegEncoderConfiguration encoderConfiguration)
        {
            var ffmpegBinPath = encoderConfiguration?.FfmpegBinPath ?? throw new ArgumentNullException(nameof(encoderConfiguration.FfmpegBinPath));

            _ffmpegPath = $"{ffmpegBinPath}\\ffmpeg.exe";
            _ffprobePath = $"{ffmpegBinPath}\\ffprobe.exe";

            if (!File.Exists(_ffmpegPath))
            {
                throw new FileNotFoundException($"ffmpeg.exe was not found at '{_ffmpegPath}'. Ensure ffmpeg is installed at the configured FfmpegBinPath before resolving IMediaEncoder.", _ffmpegPath);
            }

            if (!File.Exists(_ffprobePath))
            {
                throw new FileNotFoundException($"ffprobe.exe was not found at '{_ffprobePath}'. Ensure ffmpeg is installed at the configured FfmpegBinPath before resolving IMediaEncoder.", _ffprobePath);
            }
        }

        public string FfmpegPath => _ffmpegPath;

        public string FfprobePath => _ffprobePath;
    }

    public class FfmpegEncoderConfiguration
    {
        public required string FfmpegBinPath { get; set; }
    }
}
