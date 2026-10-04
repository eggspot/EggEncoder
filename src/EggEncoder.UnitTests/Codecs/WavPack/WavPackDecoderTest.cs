using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackDecoderTest
    {
        private static readonly string _stereoFixturePath = Path.GetFullPath("Codecs/WavPack/sample_ffmpeg.wv");
        private static readonly string _threeChannelFixturePath = Path.GetFullPath("Codecs/WavPack/sample_3channel.wv");
        private static readonly string _floatFixturePath = Path.GetFullPath("Codecs/WavPack/sample_float.wv");

        [Fact]
        public void Decode_StereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var decoded = new List<int>();

            var streamInfo = WavPackDecoder.Decode(_stereoFixturePath, (block, channels, sampleRate, bitsPerSample, _) =>
            {
                channels.Should().Be(2);
                sampleRate.Should().Be(44100);
                bitsPerSample.Should().Be(16);
                decoded.AddRange(block.ToArray());
            });

            streamInfo.Channels.Should().Be(2);
            streamInfo.SampleRate.Should().Be(44100);
            streamInfo.BitsPerSample.Should().Be(16);
            decoded.Should().NotBeEmpty();
        }

        [Fact]
        public void Decode_Should_Invoke_The_Callback_MultipleTimes_With_Consistent_Metadata()
        {
            var callbackCount = 0;
            long? firstReportedTotal = null;
            var totalDecodedFrames = 0L;

            var streamInfo = WavPackDecoder.Decode(_stereoFixturePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                callbackCount++;
                channels.Should().Be(2);
                sampleRate.Should().Be(44100);
                bitsPerSample.Should().Be(16);

                firstReportedTotal ??= totalSamples;
                totalSamples.Should().Be(firstReportedTotal, "every callback should report the same, already-known total");

                totalDecodedFrames += block.Length / channels;
            });

            callbackCount.Should().BeGreaterThan(1);
            totalDecodedFrames.Should().Be(streamInfo.TotalSamples);
        }

        [Fact]
        public void Decode_WithUnsupportedChannelCount_Should_Throw()
        {
            // WavPackEncoderSession.OpenSession itself blocks anything but mono/stereo, so to exercise
            // WavPackDecoder's own matching guard this uses a genuine 3-channel .wv file produced by
            // ffmpeg's own (independent) WavPack encoder -- a real file the official libwavpack can
            // open and report NumChannels == 3 for, rather than a hand-corrupted byte that might fail
            // for an unrelated reason before ever reaching this decoder's own check.
            var act = () => WavPackDecoder.Decode(_threeChannelFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithFloatingPointSamples_Should_Throw()
        {
            // Same rationale as the 3-channel case above: a genuine ffmpeg-produced floating-point
            // WavPack file, to exercise this decoder's lossless-integer-only guard for real.
            var act = () => WavPackDecoder.Decode(_floatFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithMissingOrMalformedFile_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_malformed_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, [1, 2, 3, 4]);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithNonExistentFile_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_missing_{Guid.NewGuid():N}.wv");

            var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<InvalidDataException>();
        }
    }
}
