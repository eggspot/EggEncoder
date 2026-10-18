using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3HuffmanDecoderTest
    {
        private static readonly (int IndexOfStart, int IndexOfEnd)[] Bands44100Hz = Mp3ScaleFactorTables.GetLongBlockBands(44100);

        [Fact]
        public void Decode_BigValuesOnly_Should_RoundTrip_TheEncoderOwnQuantizer()
        {
            var xr = new double[576];
            for (var i = 0; i < xr.Length; i++)
            {
                xr[i] = 1500.0 * Math.Sin(2 * Math.PI * 11 * i / 576.0);
            }

            var plan = Mp3Quantizer.FindPlan(xr, maxBits: 100_000);
            var writer = new BitWriter();
            Mp3Quantizer.Write(writer, plan);
            var endBitPosition = writer.BitPosition;

            var reader = new BitReader(writer.ToArray());
            var tableSelect = new[] { plan.TableSelect, plan.TableSelect, plan.TableSelect };

            var decoded = Mp3HuffmanDecoder.Decode(reader, endBitPosition, plan.BigValues, tableSelect, region0Count: 0, region1Count: 0, count1TableSelect: 0, Bands44100Hz);

            decoded.Should().Equal(plan.Quantized);
            reader.BitPosition.Should().Be(endBitPosition);
        }

        [Fact]
        public void Decode_Count1Quadruples_Should_RecoverMagnitudesAndSigns()
        {
            // Hand-build two count1 quadruples via Table A directly (the encoder's own baseline
            // never writes this region at all, so there's no existing in-house writer to round-trip
            // against here -- constructed directly from Mp3HuffmanTables.TableA's own
            // v*8+w*4+x*2+y flattening convention).
            var writer = new BitWriter();

            // Quadruple 0: (v=1, w=0, x=1, y=0) -> flat index 1*8+0*4+1*2+0 = 10
            var (code0, length0) = Mp3HuffmanTables.TableA.GetCode(10);
            writer.WriteBits(code0, length0);
            writer.WriteBits(1, 1); // sign of v: negative
            writer.WriteBits(0, 1); // sign of x: positive

            // Quadruple 1: (v=0, w=1, x=0, y=1) -> flat index 0*8+1*4+0*2+1 = 5
            var (code1, length1) = Mp3HuffmanTables.TableA.GetCode(5);
            writer.WriteBits(code1, length1);
            writer.WriteBits(0, 1); // sign of w: positive
            writer.WriteBits(1, 1); // sign of y: negative

            var endBitPosition = writer.BitPosition;
            var reader = new BitReader(writer.ToArray());

            var decoded = Mp3HuffmanDecoder.Decode(reader, endBitPosition, bigValues: 0, [1, 1, 1], region0Count: 0, region1Count: 0, count1TableSelect: 0, Bands44100Hz);

            decoded[0].Should().Be(-1);
            decoded[1].Should().Be(0);
            decoded[2].Should().Be(1);
            decoded[3].Should().Be(0);
            decoded[4].Should().Be(0);
            decoded[5].Should().Be(1);
            decoded[6].Should().Be(0);
            decoded[7].Should().Be(-1);
            reader.BitPosition.Should().Be(endBitPosition);
        }

        [Fact]
        public void Decode_WithEscapeValue_Should_ApplyLinbits()
        {
            // Table 24 (table_select 24, linbits=4, width=16): encode a pair whose x magnitude is
            // 15+7=22, escaping past the table's own direct range of 0-15.
            var (table, width, linbits) = Mp3HuffmanTables.GetBigValueTable(24);
            width.Should().Be(16);
            linbits.Should().Be(4);

            var maxDirect = width - 1;
            var writer = new BitWriter();
            var (code, length) = table.GetCode((maxDirect * width) + 0); // (x=15, y=0)
            writer.WriteBits(code, length);
            writer.WriteBits(7, linbits); // escape: x = 15 + 7 = 22
            writer.WriteBits(0, 1); // sign of x: positive
            // y == 0, no sign bit

            var endBitPosition = writer.BitPosition;
            var reader = new BitReader(writer.ToArray());

            var decoded = Mp3HuffmanDecoder.Decode(reader, endBitPosition, bigValues: 1, [24, 24, 24], region0Count: 0, region1Count: 0, count1TableSelect: 0, Bands44100Hz);

            decoded[0].Should().Be(22);
            decoded[1].Should().Be(0);
            reader.BitPosition.Should().Be(endBitPosition);
        }

        [Fact]
        public void Decode_WithDifferentTablesPerRegion_Should_SwitchTableAtTheRegionBoundary()
        {
            // region0_count=0 -> region0 is scalefactor band 0 only (lines 0-3 at 44.1kHz, width 4),
            // coded with table_select[0]=1. Both pairs that fit inside band 0 (lines 0-1 and 2-3)
            // are therefore still region0; the third pair (lines 4-5, scalefactor band 1) is the
            // first one actually in region1, coded with table_select[1]=2 -- region1_count is set
            // large enough that region2 is never reached.
            var writer = new BitWriter();

            var (table1, _, _) = Mp3HuffmanTables.GetBigValueTable(1);
            var (zeroPairCode, zeroPairLength) = table1.GetCode(0); // (x=0, y=0): no sign bits
            writer.WriteBits(zeroPairCode, zeroPairLength); // pair 0: lines 0-1, region0
            writer.WriteBits(zeroPairCode, zeroPairLength); // pair 1: lines 2-3, region0

            var (table2, _, _) = Mp3HuffmanTables.GetBigValueTable(2);
            var (code1, length1) = table2.GetCode((2 * 3) + 2); // table 2 (width 3): (x=2, y=2)
            writer.WriteBits(code1, length1); // pair 2: lines 4-5, region1
            writer.WriteBits(0, 1); // sign x
            writer.WriteBits(0, 1); // sign y

            var endBitPosition = writer.BitPosition;
            var reader = new BitReader(writer.ToArray());

            var decoded = Mp3HuffmanDecoder.Decode(reader, endBitPosition, bigValues: 3, [1, 2, 2], region0Count: 0, region1Count: 20, count1TableSelect: 0, Bands44100Hz);

            decoded[0].Should().Be(0);
            decoded[1].Should().Be(0);
            decoded[2].Should().Be(0);
            decoded[3].Should().Be(0);
            decoded[4].Should().Be(2);
            decoded[5].Should().Be(2);
            reader.BitPosition.Should().Be(endBitPosition);
        }

        [Fact]
        public void Decode_StopsAtFiveHundredSeventySix_EvenWithBitBudgetRemaining()
        {
            // 287 big_values pairs of (0,0) bring lineIndex to 574 -- too few lines remain
            // (574+4 > 576) for even one more count1 quadruple, so the loop must stop immediately
            // without attempting to decode trailing bits as a spurious quadruple, and must never
            // write past index 575.
            var writer = new BitWriter();
            var (table1, _, _) = Mp3HuffmanTables.GetBigValueTable(1);
            var (zeroPairCode, zeroPairLength) = table1.GetCode(0); // (x=0, y=0): no sign bits, no linbits

            for (var i = 0; i < 287; i++)
            {
                writer.WriteBits(zeroPairCode, zeroPairLength);
            }

            // Extra trailing bits that would otherwise look like further count1 data if the loop
            // didn't stop -- must be ignored/skipped, not decoded as another quadruple.
            writer.WriteBits(0b1111, 4);

            var endBitPosition = writer.BitPosition;
            var reader = new BitReader(writer.ToArray());

            var decoded = Mp3HuffmanDecoder.Decode(reader, endBitPosition, bigValues: 287, [1, 1, 1], region0Count: 20, region1Count: 0, count1TableSelect: 0, Bands44100Hz);

            decoded.Should().HaveCount(576);
            decoded.Should().AllSatisfy(v => v.Should().Be(0));
        }
    }
}
