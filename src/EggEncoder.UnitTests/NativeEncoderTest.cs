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
        private static readonly string _aacFixturePath = Path.GetFullPath("Codecs/Aac/tone_mono.aac");
        private static readonly string _wmaFixturePath = Path.GetFullPath("Codecs/Wma/tone_mono.wma");

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

            AssertNonEmptyWaveform(probeResult.WaveformResult);

            probeResult.Format.FormatName.Should().Be("wav");
            probeResult.Format.SizeBytes.Should().Be(new FileInfo(_wavFixturePath).Length);
            probeResult.Format.DurationSeconds.Should().BeApproximately(2, 0.1);
            probeResult.Format.StreamCount.Should().Be(1);

            probeResult.Stream.CodecType.Should().Be("audio");
            probeResult.Stream.CodecName.Should().Be("pcm_s16le");
            probeResult.Stream.SampleRate.Should().Be(44100);
            probeResult.Stream.Channels.Should().Be(2);
            probeResult.Stream.ChannelLayout.Should().Be("stereo");
            probeResult.Stream.BitsPerSample.Should().Be(16);
            probeResult.Stream.TimeBase.Should().Be("1/44100");
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

                AssertNonEmptyWaveform(probeResult.WaveformResult);

                probeResult.Format.FormatName.Should().Be("flac");
                probeResult.Format.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.Stream.CodecName.Should().Be("flac");
                probeResult.Stream.SampleRate.Should().Be(44100);
                probeResult.Stream.BitsPerSample.Should().Be(16);
                probeResult.Stream.ChannelLayout.Should().Be("stereo");
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

                AssertNonEmptyWaveform(probeResult.WaveformResult);

                probeResult.Format.FormatName.Should().Be("mp3");
                probeResult.Format.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.Stream.CodecName.Should().Be("mp3");
                probeResult.Stream.SampleRate.Should().Be(44100);
                probeResult.Stream.BitsPerSample.Should().Be(16);
                probeResult.Stream.ChannelLayout.Should().Be("stereo");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_AacFile_Should_Return_Correct_Metadata()
        {
            var probeResult = await _nativeEncoder.Probe(_aacFixturePath);

            probeResult.Format.FormatName.Should().Be("aac");
            probeResult.Stream.SampleRate.Should().BePositive();
            probeResult.Stream.CodecName.Should().Be("aac");
            probeResult.Stream.CodecType.Should().Be("audio");
            probeResult.Stream.ChannelLayout.Should().Be("mono");
        }

        [Fact]
        public async Task Probe_WmaFile_Should_Return_Correct_Metadata()
        {
            var probeResult = await _nativeEncoder.Probe(_wmaFixturePath);

            probeResult.Format.FormatName.Should().Be("asf");
            probeResult.Stream.SampleRate.Should().BePositive();
            probeResult.Stream.CodecName.Should().Be("wmav2");
            probeResult.Stream.CodecType.Should().Be("audio");
        }

        [Fact]
        public async Task Probe_MovFile_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_movFixturePath);

            probeResult.WaveformResult.Should().BeNull();

            probeResult.Format.FormatName.Should().Be("mov");
            probeResult.Format.DurationSeconds.Should().BeApproximately(5, 0.1);
            probeResult.Stream.CodecType.Should().Be("video");
            probeResult.Stream.Width.Should().Be(640);
            probeResult.Stream.Height.Should().Be(360);
        }

        [Fact]
        public async Task Probe_Mp4File_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_mp4FixturePath);

            probeResult.WaveformResult.Should().BeNull();
            probeResult.Stream.Width.Should().Be(640);
            probeResult.Stream.Height.Should().Be(360);

            probeResult.Format.FormatName.Should().Be("mp4");
            probeResult.Format.DurationSeconds.Should().BeApproximately(5, 0.1);
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
