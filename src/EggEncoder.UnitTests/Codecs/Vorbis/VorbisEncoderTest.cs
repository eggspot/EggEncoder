using EggEncoder.Codecs.Vorbis;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Vorbis
{
    public class VorbisEncoderTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void Encode_Then_Decode_Should_Reproduce_RecognizableSignal()
        {
            var samples = new int[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_source_{Guid.NewGuid():N}.wav");
            var destOggPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_dest_{Guid.NewGuid():N}.ogg");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, samples);

                VorbisEncoder.Encode(sourceWavPath, destOggPath);

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(destOggPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.SampleRate.Should().Be(SampleRate);
                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOggPath);
            }
        }

        [Fact]
        public void Encode_From_StereoWav_Should_Produce_DecodableFile()
        {
            // Unlike Opus's own equivalent test (a handful of samples is enough there), Vorbis's large
            // fixed encoder lookahead (see VorbisEncoderSessionTest's remarks) swallows a clip this
            // short entirely, so this needs enough real audio to still have decodable content left
            // once that lookahead has drained.
            var frameCount = SampleRate / 2;
            var interleaved = new int[frameCount * 2];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var value = (int)(8000 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
                interleaved[frame * 2] = value;
                interleaved[(frame * 2) + 1] = value;
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destOggPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_stereo_dest_{Guid.NewGuid():N}.ogg");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, SampleRate, bitsPerSample: 16, interleaved);

                VorbisEncoder.Encode(sourceWavPath, destOggPath);

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(destOggPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOggPath);
            }
        }

        [Fact]
        public void Encode_WithLowerQuality_Should_Produce_Smaller_File()
        {
            // Mirrors Mp3EncoderTest's Encode_WithLowerBitRate_Should_Produce_Smaller_File:
            // confirms the quality parameter is actually wired through VorbisEncoder.Encode into
            // VorbisEncoderSession.OpenSession, not silently ignored -- the convenience wrapper
            // exposes it as an optional parameter the same way FlacEncoder/Mp3Encoder expose their
            // own compressionLevel/bitRateKbps knobs, so it needs the same pass-through coverage.
            var frameCount = SampleRate * 2;
            var samples = new int[frameCount];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_quality_src_{Guid.NewGuid():N}.wav");
            var highQualityPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_quality_high_{Guid.NewGuid():N}.ogg");
            var lowQualityPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_quality_low_{Guid.NewGuid():N}.ogg");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, samples);

                VorbisEncoder.Encode(sourceWavPath, highQualityPath, quality: 0.9f);
                VorbisEncoder.Encode(sourceWavPath, lowQualityPath, quality: -0.1f);

                var highQualitySize = new FileInfo(highQualityPath).Length;
                var lowQualitySize = new FileInfo(lowQualityPath).Length;

                lowQualitySize.Should().BeLessThan(highQualitySize, $"expected quality=-0.1 ({lowQualitySize} bytes) to be smaller than quality=0.9 ({highQualitySize} bytes)");
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(highQualityPath);
                File.Delete(lowQualityPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedChannelCountWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_bad_channels_{Guid.NewGuid():N}.wav");
            var destOggPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_bad_channels_dest_{Guid.NewGuid():N}.ogg");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 3, SampleRate, bitsPerSample: 16, [0, 0, 0, 1, 1, 1]);

                var act = () => VorbisEncoder.Encode(sourceWavPath, destOggPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOggPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedBitDepthWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_bad_depth_{Guid.NewGuid():N}.wav");
            var destOggPath = Path.Combine(Path.GetTempPath(), $"vorbis_encoder_bad_depth_dest_{Guid.NewGuid():N}.ogg");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 24, [0, 1, 2, 3]);

                var act = () => VorbisEncoder.Encode(sourceWavPath, destOggPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOggPath);
            }
        }
    }
}
