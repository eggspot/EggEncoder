using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderGaplessTest
    {
        private const int SampleRate = 44100;
        private const int FrameSamples = 1152;
        private const int DecoderDelaySamples = 529;
        private const int SourceFrames = 100_003;

        [Fact]
        public void Probe_EncodedFile_Should_CarryTheEncoderDelayAndPadding_ThatAddUpToTheSourceLength()
        {
            var mp3Path = EncodeWhiteNoise(out _);

            try
            {
                var probe = Mp3Probe.Probe(mp3Path);

                probe.EncoderDelaySamples.Should().Be(528);
                probe.EncoderDelaySamples.Should().Be(Mp3Encoder.EncoderDelaySamples);
                (ExpectedAudioFrames() * FrameSamples - probe.EncoderDelaySamples!.Value - probe.EncoderPaddingSamples!.Value)
                    .Should().Be(SourceFrames);
                probe.DurationSeconds.Should().BeApproximately((double)SourceFrames / SampleRate, 1e-9);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void Decode_EncodedFile_Should_OutputEveryAudioFrame_AndLagTheSourceByTheEncoderPlusDecoderDelay()
        {
            var mp3Path = EncodeWhiteNoise(out var source);

            try
            {
                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);

                decoded.Length.Should().Be(ExpectedAudioFrames() * FrameSamples);
                FindAlignmentOffset(source, decoded, maxOffset: 4 * FrameSamples)
                    .Should().Be(Mp3Encoder.EncoderDelaySamples + DecoderDelaySamples);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void Decode_TrimmedTheLameWay_Should_BeExactlyTheSourceLength_AndLineUpWithIt()
        {
            var mp3Path = EncodeWhiteNoise(out var source);

            try
            {
                var probe = Mp3Probe.Probe(mp3Path);
                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);

                var trimStart = probe.EncoderDelaySamples!.Value + DecoderDelaySamples;
                var trimEnd = probe.EncoderPaddingSamples!.Value - DecoderDelaySamples;
                var trimmed = decoded[trimStart..^trimEnd];

                trimmed.Length.Should().Be(SourceFrames);
                FindAlignmentOffset(source, trimmed, maxOffset: 64).Should().Be(0);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        private static int ExpectedAudioFrames()
        {
            const int flushFrames = 1;
            return ((SourceFrames + FrameSamples - 1) / FrameSamples) + flushFrames;
        }

        private static string EncodeWhiteNoise(out int[] source)
        {
            var random = new Random(4321);
            source = new int[SourceFrames];
            for (var i = 0; i < source.Length; i++)
            {
                source[i] = (int)((random.NextDouble() * 2.0 - 1.0) * 16000);
            }

            var mp3Path = Mp3EncoderLevelTest.TempPath("gapless");
            using var session = Mp3Encoder.OpenSession(mp3Path, channels: 1, SampleRate, bitsPerSample: 16);
            session.WriteInterleavedSamples(source, source.Length);
            session.Finish();

            return mp3Path;
        }

        private static int FindAlignmentOffset(int[] source, int[] decoded, int maxOffset)
        {
            const int compareStart = 5000;
            const int compareLength = 4000;
            var bestOffset = 0;
            var bestCorrelation = double.MinValue;

            for (var offset = 0; offset <= maxOffset; offset++)
            {
                double dot = 0, sourceEnergy = 0, decodedEnergy = 0;
                for (var i = compareStart; i < compareStart + compareLength; i++)
                {
                    dot += (double)decoded[offset + i] * source[i];
                    sourceEnergy += (double)source[i] * source[i];
                    decodedEnergy += (double)decoded[offset + i] * decoded[offset + i];
                }

                var correlation = dot / Math.Sqrt(sourceEnergy * decodedEnergy);
                if (correlation > bestCorrelation)
                {
                    bestCorrelation = correlation;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }
    }
}
