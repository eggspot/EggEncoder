using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

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

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_wavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_s16le");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(2);
            probeResult.ChannelLayout.Should().Be("stereo");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.TimeBase.Should().Be("1/44100");
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

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("flac");
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.CodecName.Should().Be("flac");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.ChannelLayout.Should().Be("stereo");
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

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("mp3");
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.CodecName.Should().Be("mp3");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.ChannelLayout.Should().Be("stereo");
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

            probeResult.FormatName.Should().Be("aac");
            probeResult.SampleRate.Should().BePositive();
            probeResult.CodecName.Should().Be("aac");
            probeResult.CodecType.Should().Be("audio");
            probeResult.ChannelLayout.Should().Be("mono");
        }

        [Fact]
        public async Task Probe_WmaFile_Should_Return_Correct_Metadata()
        {
            var probeResult = await _nativeEncoder.Probe(_wmaFixturePath);

            probeResult.FormatName.Should().Be("asf");
            probeResult.SampleRate.Should().BePositive();
            probeResult.CodecName.Should().Be("wmav2");
            probeResult.CodecType.Should().Be("audio");
        }

        [Fact]
        public async Task Probe_MovFile_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_movFixturePath);

            probeResult.Waveform.Should().BeNull();

            probeResult.FormatName.Should().Be("mov");
            probeResult.DurationSeconds.Should().BeApproximately(5, 0.1);
            probeResult.CodecType.Should().Be("video");
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);
        }

        [Fact]
        public async Task Probe_Mp4File_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_mp4FixturePath);

            probeResult.Waveform.Should().BeNull();
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);

            probeResult.FormatName.Should().Be("mp4");
            probeResult.DurationSeconds.Should().BeApproximately(5, 0.1);
        }

        [Fact]
        public async Task Probe_Mp4FileWithAacAudio_Should_Return_AudioMetadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                const int sampleCount = sampleRate;

                var samples = new short[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var aacPath = Path.Combine(tempDirectory, "source.aac");
                AacEncoder.Encode(aacPath, samples, channels: 1, sampleRate);

                var rawFrames = Mp4FileBuilder.ExtractRawAacFrames(aacPath);
                var mp4Path = Path.Combine(tempDirectory, "source.mp4");
                Mp4FileBuilder.Create(mp4Path, sampleRate, rawFrames);

                var probeResult = await _nativeEncoder.Probe(mp4Path);

                probeResult.FormatName.Should().Be("mp4");
                probeResult.CodecType.Should().Be("video");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                AssertNonEmptyWaveform(probeResult.WaveformResult);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
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

        [Fact]
        public async Task Probe_Should_Log_Start_And_Completion()
        {
            await _nativeEncoder.Probe(_wavFixturePath);

            _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
        }

        [Fact]
        public async Task ConvertFile_Should_Log_Start_And_Completion()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                await _nativeEncoder.ConvertFile(_wavFixturePath, destPath);

                _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task CutFile_Should_Log_Start_And_Completion()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "cut.wav");

                await _nativeEncoder.CutFile(_wavFixturePath, destPath, 0, 1);

                _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_WithLoggingDisabled_Should_Not_Log()
        {
            var logger = new Mock<ILogger<NativeEncoder>>();
            var encoder = new NativeEncoder(logger.Object, enableLogging: false);

            await encoder.Probe(_wavFixturePath);

            logger.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task Probe_WithLoggingDisabled_OnFailure_Should_Not_Log()
        {
            var logger = new Mock<ILogger<NativeEncoder>>();
            var encoder = new NativeEncoder(logger.Object, enableLogging: false);

            var act = () => encoder.Probe("file.ogg");

            await act.Should().ThrowExactlyAsync<NotSupportedException>();
            logger.Invocations.Should().BeEmpty();
        }

        private static void AssertNonEmptyWaveform(IReadOnlyList<double>? waveform)
        {
            waveform.Should().NotBeNull();
            waveform.Should().NotBeEmpty();
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }
    }
}
