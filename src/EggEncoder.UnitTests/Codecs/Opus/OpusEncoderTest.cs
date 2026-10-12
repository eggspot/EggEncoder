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
        public void Encode_WithTags_Should_Produce_Opus_Whose_TagsRoundTrip_Through_Decode()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_tags_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_tags_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, [0, 1000, -1000, 500]);
                var tags = new Dictionary<string, string> { ["TITLE"] = "Title", ["ARTIST"] = "Artist" };

                OpusEncoder.Encode(sourceWavPath, destOpusPath, tags: tags);

                var streamInfo = OpusDecoder.Decode(destOpusPath, (_, _, _, _, _) => { });

                streamInfo.Tags.Should().NotBeNull();
                streamInfo.Tags!.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
                streamInfo.Tags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Artist");
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOpusPath);
            }
        }

        [Fact]
        public void Encode_WithoutTags_Should_Produce_Opus_With_NullTags()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_notags_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_notags_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, [0, 1]);

                OpusEncoder.Encode(sourceWavPath, destOpusPath);

                var streamInfo = OpusDecoder.Decode(destOpusPath, (_, _, _, _, _) => { });
                streamInfo.Tags.Should().BeNull();
            }
            finally
            {
                File.Delete(sourceWavPath);
                File.Delete(destOpusPath);
            }
        }

        [Fact]
        public void Encode_WithInvalidTagFieldName_Should_Throw_Before_Creating_DestFile()
        {
            var sourceWavPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_badtag_{Guid.NewGuid():N}.wav");
            var destOpusPath = Path.Combine(Path.GetTempPath(), $"opus_encoder_badtag_dest_{Guid.NewGuid():N}.opus");
            try
            {
                WavFileBuilder.Create(sourceWavPath, channels: 1, SampleRate, bitsPerSample: 16, [0, 1]);
                var tags = new Dictionary<string, string> { ["BAD=NAME"] = "value" };

                var act = () => OpusEncoder.Encode(sourceWavPath, destOpusPath, tags: tags);

                act.Should().Throw<ArgumentException>();
                File.Exists(destOpusPath).Should().BeFalse();
            }
            finally
            {
                File.Delete(sourceWavPath);
                if (File.Exists(destOpusPath))
                {
                    File.Delete(destOpusPath);
                }
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
