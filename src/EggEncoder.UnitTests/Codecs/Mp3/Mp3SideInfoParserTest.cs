using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3SideInfoParserTest
    {
        [Fact]
        public void Parse_Stereo_Should_RoundTrip_TheEncoderOwnWriteSideInfo()
        {
            var encoder = new Mp3FrameEncoder(channels: 2, sampleRate: 44100, bitRateKbps: 128);
            var plans = new Mp3Quantizer.Plan[2, 2];
            plans[0, 0] = new Mp3Quantizer.Plan(GlobalGain: 100, TableSelect: 5, BigValues: 10, BitCount: 400, Quantized: []);
            plans[0, 1] = new Mp3Quantizer.Plan(GlobalGain: 110, TableSelect: 7, BigValues: 20, BitCount: 450, Quantized: []);
            plans[1, 0] = new Mp3Quantizer.Plan(GlobalGain: 120, TableSelect: 3, BigValues: 30, BitCount: 500, Quantized: []);
            plans[1, 1] = new Mp3Quantizer.Plan(GlobalGain: 130, TableSelect: 9, BigValues: 40, BitCount: 550, Quantized: []);

            var writer = new BitWriter();
            encoder.WriteSideInfo(writer, plans);
            var totalBits = writer.BitPosition;

            var reader = new BitReader(writer.ToArray());
            var sideInfo = Mp3SideInfoParser.Parse(reader, channels: 2);

            sideInfo.MainDataBegin.Should().Be(0);
            sideInfo.PrivateBits.Should().Be(0);
            sideInfo.Scfsi.Should().HaveCount(2);
            sideInfo.Scfsi[0].Should().AllBeEquivalentTo(false);
            sideInfo.Scfsi[1].Should().AllBeEquivalentTo(false);

            for (var gr = 0; gr < 2; gr++)
            {
                for (var ch = 0; ch < 2; ch++)
                {
                    var plan = plans[gr, ch];
                    var granule = sideInfo.Granules[gr, ch];

                    granule.PartTwoThreeLength.Should().Be(plan.BitCount);
                    granule.BigValues.Should().Be(plan.BigValues);
                    granule.GlobalGain.Should().Be(plan.GlobalGain);
                    granule.ScalefacCompress.Should().Be(0);
                    granule.TableSelect.Should().Equal(plan.TableSelect, plan.TableSelect, plan.TableSelect);
                    granule.Region0Count.Should().Be(0);
                    granule.Region1Count.Should().Be(0);
                    granule.Preflag.Should().BeFalse();
                    granule.ScalefacScale.Should().BeFalse();
                    granule.Count1TableSelect.Should().Be(0);
                }
            }

            reader.BitPosition.Should().Be(totalBits);
            totalBits.Should().Be(32 * 8); // _sideInfoBytes for stereo
        }

        [Fact]
        public void Parse_Mono_Should_RoundTrip_TheEncoderOwnWriteSideInfo()
        {
            var encoder = new Mp3FrameEncoder(channels: 1, sampleRate: 48000, bitRateKbps: 64);
            var plans = new Mp3Quantizer.Plan[2, 1];
            plans[0, 0] = new Mp3Quantizer.Plan(GlobalGain: 80, TableSelect: 2, BigValues: 15, BitCount: 300, Quantized: []);
            plans[1, 0] = new Mp3Quantizer.Plan(GlobalGain: 90, TableSelect: 4, BigValues: 25, BitCount: 350, Quantized: []);

            var writer = new BitWriter();
            encoder.WriteSideInfo(writer, plans);
            var totalBits = writer.BitPosition;

            var reader = new BitReader(writer.ToArray());
            var sideInfo = Mp3SideInfoParser.Parse(reader, channels: 1);

            sideInfo.Scfsi.Should().HaveCount(1);
            sideInfo.Granules[0, 0].GlobalGain.Should().Be(80);
            sideInfo.Granules[1, 0].GlobalGain.Should().Be(90);
            reader.BitPosition.Should().Be(totalBits);
            totalBits.Should().Be(17 * 8); // _sideInfoBytes for mono
        }

        [Fact]
        public void Parse_WithEveryFieldDistinct_Should_ReadEachFieldFromItsOwnCorrectPosition()
        {
            // Hand-built mono side info with every field a different, non-zero/non-default value
            // and the two granules distinguishable from each other -- the encoder's own baseline
            // never writes anything but zeros for most of these fields (scfsi, scalefac_compress,
            // region0/1_count, preflag, scalefac_scale, count1table_select), so the round-trip
            // tests above alone wouldn't catch a field read from the wrong bit offset.
            var writer = new BitWriter();
            writer.WriteBits(37, 9); // main_data_begin
            writer.WriteBits(11, 5); // private_bits (mono width)
            writer.WriteBits(0b1010, 4); // scfsi[0][0..3] = true, false, true, false

            WriteGranule(writer, partTwoThreeLength: 111, bigValues: 1, globalGain: 2, scalefacCompress: 3,
                tableSelect: [4, 5, 6], region0Count: 7, region1Count: 5, preflag: 1, scalefacScale: 0, count1TableSelect: 1);
            WriteGranule(writer, partTwoThreeLength: 222, bigValues: 9, globalGain: 10, scalefacCompress: 11,
                tableSelect: [12, 13, 14], region0Count: 15, region1Count: 6, preflag: 0, scalefacScale: 1, count1TableSelect: 0);

            var reader = new BitReader(writer.ToArray());
            var sideInfo = Mp3SideInfoParser.Parse(reader, channels: 1);

            sideInfo.MainDataBegin.Should().Be(37);
            sideInfo.PrivateBits.Should().Be(11);
            sideInfo.Scfsi[0].Should().Equal(true, false, true, false);

            var gr0 = sideInfo.Granules[0, 0];
            gr0.PartTwoThreeLength.Should().Be(111);
            gr0.BigValues.Should().Be(1);
            gr0.GlobalGain.Should().Be(2);
            gr0.ScalefacCompress.Should().Be(3);
            gr0.TableSelect.Should().Equal(4, 5, 6);
            gr0.Region0Count.Should().Be(7);
            gr0.Region1Count.Should().Be(5);
            gr0.Preflag.Should().BeTrue();
            gr0.ScalefacScale.Should().BeFalse();
            gr0.Count1TableSelect.Should().Be(1);

            var gr1 = sideInfo.Granules[1, 0];
            gr1.PartTwoThreeLength.Should().Be(222);
            gr1.BigValues.Should().Be(9);
            gr1.GlobalGain.Should().Be(10);
            gr1.ScalefacCompress.Should().Be(11);
            gr1.TableSelect.Should().Equal(12, 13, 14);
            gr1.Region0Count.Should().Be(15);
            gr1.Region1Count.Should().Be(6);
            gr1.Preflag.Should().BeFalse();
            gr1.ScalefacScale.Should().BeTrue();
            gr1.Count1TableSelect.Should().Be(0);
        }

        [Fact]
        public void Parse_WithWindowSwitchingFlagSet_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0, 9); // main_data_begin
            writer.WriteBits(0, 5); // private_bits (mono width)
            writer.WriteBits(0, 4); // scfsi[0][0..3]

            writer.WriteBits(0, 12); // part2_3_length
            writer.WriteBits(0, 9); // big_values
            writer.WriteBits(0, 8); // global_gain
            writer.WriteBits(0, 4); // scalefac_compress
            writer.WriteBits(1, 1); // window_switching_flag = 1 (short/mixed block)

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3SideInfoParser.Parse(reader, channels: 1);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        private static void WriteGranule(
            BitWriter writer,
            int partTwoThreeLength,
            int bigValues,
            int globalGain,
            int scalefacCompress,
            int[] tableSelect,
            int region0Count,
            int region1Count,
            int preflag,
            int scalefacScale,
            int count1TableSelect)
        {
            writer.WriteBits((uint)partTwoThreeLength, 12);
            writer.WriteBits((uint)bigValues, 9);
            writer.WriteBits((uint)globalGain, 8);
            writer.WriteBits((uint)scalefacCompress, 4);
            writer.WriteBits(0, 1); // window_switching_flag = 0 (long block)
            writer.WriteBits((uint)tableSelect[0], 5);
            writer.WriteBits((uint)tableSelect[1], 5);
            writer.WriteBits((uint)tableSelect[2], 5);
            writer.WriteBits((uint)region0Count, 4);
            writer.WriteBits((uint)region1Count, 3);
            writer.WriteBits((uint)preflag, 1);
            writer.WriteBits((uint)scalefacScale, 1);
            writer.WriteBits((uint)count1TableSelect, 1);
        }
    }
}
