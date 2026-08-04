using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace EggEncoder.UnitTests
{
    public class NativeEncoderTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _movFixturePath = Path.GetFullPath("Codecs/Mov/test.mov");
        private static readonly string _mp4FixturePath = Path.GetFullPath("Codecs/Mov/test.mp4");

        private readonly Mock<ILogger<NativeEncoder>> _logger = new();

        private readonly NativeEncoder _nativeEncoder;

        public NativeEncoderTest()
        {
            _nativeEncoder = new NativeEncoder(_logger.Object);
        }

        [Fact]
        public async Task Probe_WavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_wavFixturePath);

            probeResult.SampleRate.Should().Be(44100);
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSeconds.Should().Be(2);
            AssertNonEmptyWaveform(probeResult.WaveformResult);
        }

        [Fact]
        public async Task Probe_FlacFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var flacPath = Path.Combine(tempDirectory, "source.flac");
                FlacEncoder.Encode(_wavFixturePath, flacPath);

                var probeResult = await _nativeEncoder.Probe(flacPath);

                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.DurationInSeconds.Should().Be(2);
                AssertNonEmptyWaveform(probeResult.WaveformResult);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Mp3File_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "source.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = await _nativeEncoder.Probe(mp3Path);

                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.DurationInSeconds.Should().Be(2);
                AssertNonEmptyWaveform(probeResult.WaveformResult);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_MovFile_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_movFixturePath);

            probeResult.DurationInSeconds.Should().Be(5);
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);
            probeResult.WaveformResult.Should().BeNull();
        }

        [Fact]
        public async Task Probe_Mp4File_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_mp4FixturePath);

            probeResult.DurationInSeconds.Should().Be(5);
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);
            probeResult.WaveformResult.Should().BeNull();
        }

        [Fact]
        public async Task Probe_UnsupportedExtension_Should_Throw()
        {
            var act = () => _nativeEncoder.Probe("file.ogg");
            await act.Should().ThrowExactlyAsync<NotSupportedException>();
        }

        [Fact]
        public async Task ConvertFile_ToNestedMissingDirectory_Should_CreateDirectory_And_Convert()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "nested", "dest.flac");

                await _nativeEncoder.ConvertFile(_wavFixturePath, destPath);

                File.Exists(destPath).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task CutFile_ToNestedMissingDirectory_Should_CreateDirectory_And_Cut()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "nested", "cut.wav");

                await _nativeEncoder.CutFile(_wavFixturePath, destPath, 0, 1);

                File.Exists(destPath).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static void AssertNonEmptyWaveform(string? waveformResult)
        {
            waveformResult.Should().NotBeNull();

            var windows = JsonSerializer.Deserialize<List<double>>(waveformResult!);
            windows.Should().NotBeEmpty();
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }
    }
}
