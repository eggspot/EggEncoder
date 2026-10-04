using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.WavPack;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EggEncoder.UnitTests
{
    public class NativeEncoderTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _imaAdpcmWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono.wav");
        private static readonly string _msAdpcmWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav");
        private static readonly string _muLawWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono.wav");
        private static readonly string _aLawWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_alaw_mono.wav");
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
        public async Task Probe_ImaAdpcmWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_imaAdpcmWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_imaAdpcmWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_ima_wav");
            probeResult.CodecLongName.Should().Be("ADPCM IMA WAV");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(88200);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_MsAdpcmWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_msAdpcmWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_msAdpcmWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_ms");
            probeResult.CodecLongName.Should().Be("ADPCM Microsoft");
            probeResult.SampleRate.Should().Be(22050);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(44100);
            probeResult.TimeBase.Should().Be("1/22050");
        }

        [Fact]
        public async Task Probe_MuLawWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_muLawWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_muLawWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_mulaw");
            probeResult.CodecLongName.Should().Be("PCM mu-law / G.711 mu-law");
            probeResult.SampleRate.Should().Be(8000);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(16000);
            probeResult.TimeBase.Should().Be("1/8000");
        }

        [Fact]
        public async Task Probe_ALawWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_aLawWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_aLawWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_alaw");
            probeResult.CodecLongName.Should().Be("PCM A-law / G.711 A-law");
            probeResult.SampleRate.Should().Be(8000);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(16000);
            probeResult.TimeBase.Should().Be("1/8000");
        }

        [Fact]
        public async Task Probe_AiffFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var aiffPath = Path.Combine(tempDirectory, "source.aiff");
                var samples = Enumerable.Range(0, 44100 * 2).SelectMany(frame => new[] { frame % 1000, -(frame % 1000) }).ToArray();
                AiffFileBuilder.Create(aiffPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, samples);

                var probeResult = await _nativeEncoder.Probe(aiffPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("aiff");
                probeResult.SizeBytes.Should().Be(new FileInfo(aiffPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("pcm_s16be");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.ChannelLayout.Should().Be("stereo");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_CafFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var cafPath = Path.Combine(tempDirectory, "source.caf");
                var samples = Enumerable.Range(0, 44100 * 2).Select(frame => frame % 1000).ToArray();

                using (var session = AlacEncoderSession.OpenSession(cafPath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(cafPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("caf");
                probeResult.SizeBytes.Should().Be(new FileInfo(cafPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("alac");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_TtaFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var ttaPath = Path.Combine(tempDirectory, "source.tta");
                var samples = Enumerable.Range(0, 44100 * 2).Select(frame => frame % 1000).ToArray();

                using (var session = TtaEncoderSession.OpenSession(ttaPath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(ttaPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("tta");
                probeResult.SizeBytes.Should().Be(new FileInfo(ttaPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("tta");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_OpusFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var opusPath = Path.Combine(tempDirectory, "source.opus");
                const int sampleRate = 48000;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                using (var session = OpusEncoderSession.OpenSession(opusPath, channels: 1, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(opusPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.SizeBytes.Should().Be(new FileInfo(opusPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("opus");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_VorbisFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var oggPath = Path.Combine(tempDirectory, "source.ogg");
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                using (var session = VorbisEncoderSession.OpenSession(oggPath, channels: 1, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(oggPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.SizeBytes.Should().Be(new FileInfo(oggPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("vorbis");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_WavPackFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var wvPath = Path.Combine(tempDirectory, "source.wv");
                WavPackEncoder.Encode(sourceWavPath, wvPath);

                var probeResult = await _nativeEncoder.Probe(wvPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("wv");
                probeResult.SizeBytes.Should().Be(new FileInfo(wvPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("wavpack");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
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
                AssertNonEmptyWaveform(probeResult.Waveform);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_UnsupportedExtension_Should_Throw()
        {
            var act = () => _nativeEncoder.Probe("file.xyz");
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

            var act = () => encoder.Probe("file.xyz");

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
