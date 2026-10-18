using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3ScaleFactorDecoderTest
    {
        [Fact]
        public void DecodeLongBlock_Granule0_Should_ReadEveryBandFresh_RegardlessOfScfsi()
        {
            // scalefac_compress=9 -> (slen1=2, slen2=2) per Mp3ScaleFactorTables.ScalefacCompressTable.
            var writer = new BitWriter();
            var expected = new int[21];
            for (var sfb = 0; sfb < 21; sfb++)
            {
                expected[sfb] = sfb % 4; // fits in 2 bits
                writer.WriteBits((uint)expected[sfb], 2);
            }

            var reader = new BitReader(writer.ToArray());
            var scfsi = new[] { true, true, true, true }; // must be ignored entirely at granule 0

            var decoded = Mp3ScaleFactorDecoder.DecodeLongBlock(reader, scalefacCompress: 9, granule: 0, scfsi, granule0Scalefactors: null);

            decoded.Should().Equal(expected);
        }

        [Fact]
        public void DecodeLongBlock_Granule1_WithScfsiSet_Should_ReuseGranule0Bands_AndNotConsumeBitsForThem()
        {
            // scfsi all true -> every band is reused from granule 0; granule 1's own bitstream
            // contains no scalefactor bits at all for this channel.
            var granule0 = Enumerable.Range(0, 21).Select(sfb => sfb + 1).ToArray();
            var reader = new BitReader([]); // empty: must not be read from at all
            var scfsi = new[] { true, true, true, true };

            var decoded = Mp3ScaleFactorDecoder.DecodeLongBlock(reader, scalefacCompress: 9, granule: 1, scfsi, granule0);

            decoded.Should().Equal(granule0);
            reader.RemainingBits.Should().Be(0);
        }

        [Fact]
        public void DecodeLongBlock_Granule1_WithScfsiPartiallySet_Should_MixReusedAndFreshBands()
        {
            // scfsi = [true, false, true, false] -> bands 0-5 (group0) and 11-15 (group2) reused
            // from granule 0; bands 6-10 (group1, slen1) and 16-20 (group3, slen2) read fresh.
            var granule0 = Enumerable.Range(0, 21).Select(sfb => sfb + 100).ToArray();

            var writer = new BitWriter();
            var freshValues = new Dictionary<int, int>();
            for (var sfb = 6; sfb <= 10; sfb++)
            {
                freshValues[sfb] = 1;
                writer.WriteBits(1, 2); // slen1 = 2 for scalefac_compress 9
            }

            for (var sfb = 16; sfb <= 20; sfb++)
            {
                freshValues[sfb] = 2;
                writer.WriteBits(2, 2); // slen2 = 2 for scalefac_compress 9
            }

            var reader = new BitReader(writer.ToArray());
            var scfsi = new[] { true, false, true, false };

            var decoded = Mp3ScaleFactorDecoder.DecodeLongBlock(reader, scalefacCompress: 9, granule: 1, scfsi, granule0);

            for (var sfb = 0; sfb < 21; sfb++)
            {
                var expected = freshValues.TryGetValue(sfb, out var fresh) ? fresh : granule0[sfb];
                decoded[sfb].Should().Be(expected, $"sfb {sfb}");
            }
        }

        [Fact]
        public void DecodeLongBlock_WithZeroLengthSlen_Should_ProduceAllZeroBandsForThatHalf_WithoutConsumingBits()
        {
            // scalefac_compress=0 -> (slen1=0, slen2=0): every band reads 0 bits and is implicitly 0.
            var reader = new BitReader([]);
            var scfsi = new[] { false, false, false, false };

            var decoded = Mp3ScaleFactorDecoder.DecodeLongBlock(reader, scalefacCompress: 0, granule: 0, scfsi, granule0Scalefactors: null);

            decoded.Should().AllSatisfy(v => v.Should().Be(0));
        }
    }
}
