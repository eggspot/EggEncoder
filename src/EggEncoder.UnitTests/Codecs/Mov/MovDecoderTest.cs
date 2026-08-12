using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Mov;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mov
{
    public class MovDecoderTest
    {
        private static readonly string _movFixturePath = Path.GetFullPath("Codecs/Mov/test.mov");

        [Fact]
        public void Decode_MonoAacTrack_Should_Match_StandaloneAacDecoder_Exactly()
        {
            const int sampleRate = 44100;
            const int sampleCount = sampleRate; // 1 second

            var originalSamples = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                originalSamples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var aacPath = Path.Combine(tempDirectory, "source.aac");
                AacEncoder.Encode(aacPath, originalSamples, channels: 1, sampleRate);

                var rawFrames = Mp4FileBuilder.ExtractRawAacFrames(aacPath);
                rawFrames.Should().NotBeEmpty();

                var mp4Path = Path.Combine(tempDirectory, "source.mp4");
                Mp4FileBuilder.Create(mp4Path, sampleRate, rawFrames);

                var mp4Samples = new List<int>();
                var mp4StreamInfo = MovDecoder.Decode(mp4Path, (block, channels, decodedSampleRate, bitsPerSample, totalSamples) =>
                {
                    channels.Should().Be(1);
                    decodedSampleRate.Should().Be(sampleRate);
                    mp4Samples.AddRange(block.ToArray());
                });

                mp4StreamInfo.Channels.Should().Be(1);
                mp4StreamInfo.SampleRate.Should().Be(sampleRate);
                mp4StreamInfo.TotalSamples.Should().Be(rawFrames.Count * 1024L);
                mp4Samples.Should().HaveCount(rawFrames.Count * 1024);

                // MovDecoder and AacDecoder both decode through the same AacFrameDecoder engine --
                // one raw_data_block at a time, ADTS or not. Given the identical underlying frames,
                // their PCM output must be bit-for-bit identical; any difference means a bug in the
                // MP4 demuxing/wiring, not in AAC decoding itself.
                var adtsSamples = new List<int>();
                AacDecoder.Decode(aacPath, (block, _, _, _, _) => adtsSamples.AddRange(block.ToArray()));

                mp4Samples.Should().Equal(adtsSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Decode_FileWithoutAudioTrack_Should_Throw()
        {
            var act = () => MovDecoder.Decode(_movFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<InvalidDataException>();
        }
    }
}
