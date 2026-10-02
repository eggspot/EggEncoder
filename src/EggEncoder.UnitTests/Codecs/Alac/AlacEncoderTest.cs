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
        public void Encode_From_StereoWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destCafPath = Path.Combine(Path.GetTempPath(), $"alac_encoder_stereo_dest_{Guid.NewGuid():N}.caf");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, [0, 0, 1, 1]);

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
