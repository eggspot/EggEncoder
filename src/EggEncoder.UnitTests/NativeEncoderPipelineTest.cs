using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Pcm;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EggEncoder.UnitTests
{
    // Covers IPcmTransformEncoder's WavSampleFormat overloads (ConvertFile, MixFiles, ConcatenateFiles),
    // which forward to AudioCutter's equivalents -- see AudioCutterPipelineTest for deeper coverage of
    // the underlying behavior. WAV-only for the same reason as AudioCutterPipelineTest: MP3/FLAC need
    // native win-x64 DLLs this dev machine doesn't have.
    public class NativeEncoderPipelineTest
    {
        private readonly Mock<ILogger<NativeEncoder>> _logger = new();
        private readonly NativeEncoder _nativeEncoder;

        public NativeEncoderPipelineTest()
        {
            _nativeEncoder = new NativeEncoder(_logger.Object);
        }

        [Fact]
        public async Task ConvertFile_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [1_000_000_000, -1_000_000_000]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                await ((IPcmTransformEncoder)_nativeEncoder).ConvertFile(sourcePath, destPath, new PcmTransformPipeline(), WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task MixFiles_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [1_000_000_000]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [500_000_000]);
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                await ((IPcmTransformEncoder)_nativeEncoder).MixFiles([new MixInput(firstPath), new MixInput(secondPath)], destPath, WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task ConcatenateFiles_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [1, 2]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [3]);
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                await ((IPcmTransformEncoder)_nativeEncoder).ConcatenateFiles([firstPath, secondPath], destPath, WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.TotalSamples.Should().Be(3);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task ConvertFile_DefaultOverload_Should_Still_Write_Integer_Wav()
        {
            // The pre-existing 3-arg overload must keep defaulting to Integer -- a backward-compatibility
            // guard now that a WavSampleFormat overload sits alongside it.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, interleavedSamples: [1, 2, 3]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                await ((IPcmTransformEncoder)_nativeEncoder).ConvertFile(sourcePath, destPath, new PcmTransformPipeline());

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }
    }
}
