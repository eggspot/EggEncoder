using EggEncoder.Codecs.WavPack;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackEncoderTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void Encode_Then_Decode_Should_Reproduce_Exact_Samples()
        {
            var samples = new int[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_source_{Guid.NewGuid():N}.wav");
            var destWvPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_dest_{Guid.NewGuid():N}.wv");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, samples);

                WavPackEncoder.Encode(sourceWavPath, destWvPath);

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(destWvPath);

                streamInfo.SampleRate.Should().Be(SampleRate);
                streamInfo.Channels.Should().Be(1);
                streamInfo.BitsPerSample.Should().Be(16);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destWvPath);
            }
        }

        [Fact]
        public void Encode_From_StereoWav_Should_Produce_DecodableFile()
        {
            var interleaved = new[] { 0, 0, 100, -50, -1000, 1000, 1, -1 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destWvPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_stereo_dest_{Guid.NewGuid():N}.wv");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, SampleRate, bitsPerSample: 16, interleaved);

                WavPackEncoder.Encode(sourceWavPath, destWvPath);

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(destWvPath);

                streamInfo.Channels.Should().Be(2);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destWvPath);
            }
        }

        [Fact]
        public void Encode_From_24BitWav_Should_Produce_DecodableFile()
        {
            var interleaved = new[] { 0, -8388608, 8388607, 12345 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_24bit_{Guid.NewGuid():N}.wav");
            var destWvPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_24bit_dest_{Guid.NewGuid():N}.wv");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 24, interleaved);

                WavPackEncoder.Encode(sourceWavPath, destWvPath);

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(destWvPath);

                streamInfo.BitsPerSample.Should().Be(24);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destWvPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedChannelCountWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_bad_channels_{Guid.NewGuid():N}.wav");
            var destWvPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_bad_channels_dest_{Guid.NewGuid():N}.wv");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 3, SampleRate, bitsPerSample: 16, [0, 0, 0, 1, 1, 1]);

                var act = () => WavPackEncoder.Encode(sourceWavPath, destWvPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destWvPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedBitDepthWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_bad_depth_{Guid.NewGuid():N}.wav");
            var destWvPath = Path.Combine(Path.GetTempPath(), $"wavpack_encoder_bad_depth_dest_{Guid.NewGuid():N}.wv");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 8, [0, 1, 2, 3]);

                var act = () => WavPackEncoder.Encode(sourceWavPath, destWvPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destWvPath);
            }
        }
    }
}
