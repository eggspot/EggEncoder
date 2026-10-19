using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    // Direct tests against WavPackBlockEncoder's own multi-term decorrelation cascade (see that
    // type's own doc comment for why it's 5 terms, not 1 -- docs/managed-codec-rewrite-plan.md
    // item 5a). WavPackRoundTripTest already covers the full file-based encode/decode round trip
    // through every one of these terms for real content; this file covers what that black-box
    // round trip can't: the exact term/delta list written, and the defensive branches
    // TermCascade's own fixed term list never actually reaches.
    public class WavPackBlockEncoderTest
    {
        [Fact]
        public void Encode_Should_WriteTheExpectedFiveTermCascade()
        {
            WavPackBlockEncoder.TermCascade.Should().Equal(
                (18, 2),
                (18, 2),
                (2, 2),
                (17, 2),
                (3, 2));
        }

        [Fact]
        public void Encode_Mono_DecorrTermsBytes_Should_EncodeEachTermAndDeltaCorrectly()
        {
            var samples = new[] { new[] { 100, -200, 300, -400, 500, 123, -456 } };

            var (decorrTerms, _, _, _) = WavPackBlockEncoder.Encode(samples, channels: 1);

            decorrTerms.Should().HaveCount(5);
            for (var i = 0; i < decorrTerms.Length; i++)
            {
                var (term, delta) = WavPackBlockEncoder.TermCascade[i];
                var expectedByte = (byte)(((term + 5) & 0x1F) | (delta << 5));
                decorrTerms[i].Should().Be(expectedByte, $"pass {i} (term {term}, delta {delta})");
            }
        }

        [Fact]
        public void Encode_Then_Decode_Mono_ThroughARealBlock_Should_ReproduceExactSamples()
        {
            var samples = new int[4096];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            }

            AssertRoundTripsThroughARealBlock(channels: 1, bitsPerSample: 16, [samples]);
        }

        [Fact]
        public void Encode_Then_Decode_Stereo_ThroughARealBlock_Should_ReproduceExactSamples()
        {
            var left = new int[4096];
            var right = new int[4096];
            for (var i = 0; i < left.Length; i++)
            {
                left[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
                right[i] = (int)(8000 * Math.Sin(2 * Math.PI * 220 * i / 44100.0));
            }

            AssertRoundTripsThroughARealBlock(channels: 2, bitsPerSample: 16, [left, right]);
        }

        [Fact]
        public void Encode_Then_Decode_Silence_ThroughARealBlock_Should_ReproduceExactSamples()
        {
            // The simplest possible content (every residual exactly 0 at every pass) -- still
            // exercises the full 5-term cascade, not a shortcut path.
            AssertRoundTripsThroughARealBlock(channels: 2, bitsPerSample: 16, [new int[4096], new int[4096]]);
        }

        [Fact]
        public void EncodeMono_WithUnsupportedTerm_Should_Throw()
        {
            var pass = new WavPackDecorrPass { Term = 99, Delta = 2 };

            var act = () => WavPackBlockEncoder.EncodeMono(pass, pos: 0, sample: 42);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void EncodeStereo_WithUnsupportedTerm_Should_Throw()
        {
            var pass = new WavPackDecorrPass { Term = -1, Delta = 2 };

            var act = () => WavPackBlockEncoder.EncodeStereo(pass, pos: 0, left: 1, right: 2);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        private static void AssertRoundTripsThroughARealBlock(int channels, int bitsPerSample, int[][] channelSamples)
        {
            var blockBytes = WavPackBlockWriter.WriteBlock(channelSamples, channels, bitsPerSample, sampleRate: 44100, blockIndex: 0, totalSamples: channelSamples[0].Length);

            var header = WavPackBlockHeader.Parse(blockBytes, 0);
            var decoded = WavPackBlockDecoder.Decode(blockBytes, WavPackBlockHeader.ByteLength, (int)header.CkSize + 8, header, out var crc, out _);

            crc.Should().Be(header.Crc, "the real encoder's own written CRC must match what decoding the same samples recomputes");
            for (var c = 0; c < channels; c++)
            {
                decoded[c].Should().Equal(channelSamples[c], $"channel {c}");
            }
        }
    }
}
