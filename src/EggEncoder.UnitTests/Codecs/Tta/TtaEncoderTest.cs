using EggEncoder.Codecs.Tta;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaEncoderTest
    {
        [Fact]
        public void Encode_Then_Decode_Should_Reproduce_Exact_Samples()
        {
            var samples = new int[5000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16000));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_source_{Guid.NewGuid():N}.wav");
            var destTtaPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_dest_{Guid.NewGuid():N}.tta");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 16000, bitsPerSample: 16, samples);

                TtaEncoder.Encode(sourceWavPath, destTtaPath);

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destTtaPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.SampleRate.Should().Be(16000);
                streamInfo.Channels.Should().Be(1);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destTtaPath);
            }
        }

        [Fact]
        public void Encode_From_StereoWav_Should_Reproduce_Exact_Samples()
        {
            var interleaved = new[] { 0, 0, 100, -50, -32768, 32767, 1, -1 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destTtaPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_stereo_dest_{Guid.NewGuid():N}.tta");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleaved);

                TtaEncoder.Encode(sourceWavPath, destTtaPath);

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destTtaPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destTtaPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedChannelCountWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_multichannel_{Guid.NewGuid():N}.wav");
            var destTtaPath = Path.Combine(Path.GetTempPath(), $"tta_encoder_multichannel_dest_{Guid.NewGuid():N}.tta");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 3, sampleRate: 44100, bitsPerSample: 16, [0, 0, 0, 1, 1, 1]);

                var act = () => TtaEncoder.Encode(sourceWavPath, destTtaPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destTtaPath);
            }
        }
    }
}
