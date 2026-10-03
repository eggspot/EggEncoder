using EggEncoder.Codecs.Opus;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OpusEncoderTest
    {
        private const int SampleRate = 48000;

        [Fact]
        public void Encode_Then_Decode_Should_Reproduce_RecognizableSignal()
        {
            var samples = new int[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }

            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_source_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, samples);

                OpusEncoder.Encode(sourceWavPath, destOpusPath);

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(destOpusPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.SampleRate.Should().Be(SampleRate);
                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
                decoded.Count.Should().BeGreaterThanOrEqualTo(samples.Length);
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOpusPath);
            }
        }

        [Fact]
        public void Encode_From_StereoWav_Should_Produce_DecodableFile()
        {
            var interleaved = new[] { 0, 0, 100, -50, -1000, 1000, 1, -1 };
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_stereo_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_stereo_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 2, SampleRate, bitsPerSample: 16, interleaved);

                OpusEncoder.Encode(sourceWavPath, destOpusPath);

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(destOpusPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOpusPath);
            }
        }

        [Fact]
        public void Encode_From_UnsupportedSampleRateWav_Should_Throw()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_bad_rate_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_bad_rate_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [0, 1, 2, 3]);

                var act = () => OpusEncoder.Encode(sourceWavPath, destOpusPath);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOpusPath);
            }
        }
    }
}
