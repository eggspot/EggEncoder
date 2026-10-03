using EggEncoder.Codecs.Alac;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacEncoderTest
    {
        [Fact]
        public void Encode_Then_Decode_Should_Reproduce_Exact_Samples()
        {
            var samples = new int[5000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16000));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_source_{Guid.NewGuid():N}.wav");
            var destCafPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_dest_{Guid.NewGuid():N}.caf");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 16000, bitsPerSample: 16, samples);

                AlacEncoder.Encode(sourceWavPath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.SampleRate.Should().Be(16000);
                streamInfo.Channels.Should().Be(1);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destCafPath);
            }
        }

        [Fact]
        public void Encode_From_StereoWav_Should_Reproduce_Exact_Samples()
        {
            var interleaved = new[] { 0, 0, 100, -50, -32768, 32767, 1, -1 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destCafPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_stereo_dest_{Guid.NewGuid():N}.caf");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleaved);

                AlacEncoder.Encode(sourceWavPath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destCafPath);
            }
        }

        [Fact]
        public void Encode_From_TwentyFourBitWav_Should_Reproduce_Exact_Samples()
        {
            var samples = new[] { 0, 8_388_607, -8_388_608, 1_000_000, -1_000_000 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_24bit_{Guid.NewGuid():N}.wav");
            var destCafPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_24bit_dest_{Guid.NewGuid():N}.caf");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 44100, bitsPerSample: 24, samples);

                AlacEncoder.Encode(sourceWavPath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, bitsPerSample, _) =>
                {
                    bitsPerSample.Should().Be(24);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.BitsPerSample.Should().Be(24);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destCafPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedChannelCountWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_multichannel_{Guid.NewGuid():N}.wav");
            var destCafPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_multichannel_dest_{Guid.NewGuid():N}.caf");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 3, sampleRate: 44100, bitsPerSample: 16, [0, 0, 0, 1, 1, 1]);

                var act = () => AlacEncoder.Encode(sourceWavPath, destCafPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destCafPath);
            }
        }
    }
}
