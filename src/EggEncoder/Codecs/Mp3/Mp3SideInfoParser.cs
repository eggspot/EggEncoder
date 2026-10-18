using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // One granule/channel's own side info fields -- shaped to feed directly into the already-built
    // decode chain: TableSelect/Region0Count/Region1Count/Count1TableSelect/PartTwoThreeLength/
    // BigValues match Mp3HuffmanDecoder.Decode's own parameters, ScalefacCompress matches
    // Mp3ScaleFactorDecoder.DecodeLongBlock's, and GlobalGain/Preflag/ScalefacScale match
    // Mp3Requantizer.Decode's (Preflag/ScalefacScale already converted from the bitstream's raw
    // 1-bit fields to bool, exactly the type each expects).
    internal readonly record struct Mp3GranuleChannelSideInfo(
        int PartTwoThreeLength,
        int BigValues,
        int GlobalGain,
        int ScalefacCompress,
        int[] TableSelect,
        int Region0Count,
        int Region1Count,
        bool Preflag,
        bool ScalefacScale,
        int Count1TableSelect);

    // One frame's full side info -- the fixed-size block (17 bytes mono, 32 bytes stereo) that
    // immediately follows the frame header. Scfsi is [channel][band 0..3], already converted to
    // bool so it feeds directly into Mp3ScaleFactorDecoder.DecodeLongBlock's own scfsi parameter.
    internal readonly record struct Mp3SideInfo(
        int MainDataBegin,
        int PrivateBits,
        bool[][] Scfsi,
        Mp3GranuleChannelSideInfo[,] Granules);

    // Parses the side info Mp3FrameEncoder.WriteSideInfo writes, immediately following the frame
    // header, for the in-house decode chain (docs/external-dependency-removal-plan.md Phase 1
    // sub-item 1a). Field order below is the real ISO/IEC 11172-3 bitstream syntax order -- the
    // same order Mp3FrameEncoder.WriteSideInfo itself already uses (confirmed directly from that
    // method's own source, and cross-checked against a real encoder's bitstream.c ordering): per
    // granule/channel, table_select[0..2] comes before region0_count/region1_count, not after --
    // so this parser already matches real-world (LAME/ffmpeg) files, not just this project's own
    // encoder output. Long blocks only for this sub-item (1b/1c cover short/mixed blocks and joint
    // stereo): a window_switching_flag of 1 means a short or mixed block, whose remaining fields
    // (block_type/mixed_block_flag/subblock_gain, replacing table_select[2]/region0_count/
    // region1_count at the same total bit width) this parser does not yet know how to read, so it
    // throws immediately rather than mis-parsing them as long-block fields -- the bit position
    // doesn't need to stay consistent afterward since the whole frame is out of scope either way.
    internal static class Mp3SideInfoParser
    {
        private const int GranuleCount = 2;
        private const int ScfsiBandCount = 4;
        private const int TableSelectCount = 3;

        public static Mp3SideInfo Parse(BitReader reader, int channels)
        {
            var mainDataBegin = (int)reader.ReadBits(9);
            var privateBits = (int)reader.ReadBits(channels == 1 ? 5 : 3);

            var scfsi = new bool[channels][];
            for (var ch = 0; ch < channels; ch++)
            {
                scfsi[ch] = new bool[ScfsiBandCount];
                for (var band = 0; band < ScfsiBandCount; band++)
                {
                    scfsi[ch][band] = reader.ReadBits(1) == 1;
                }
            }

            var granules = new Mp3GranuleChannelSideInfo[GranuleCount, channels];
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    granules[gr, ch] = ParseGranuleChannel(reader);
                }
            }

            return new Mp3SideInfo(mainDataBegin, privateBits, scfsi, granules);
        }

        private static Mp3GranuleChannelSideInfo ParseGranuleChannel(BitReader reader)
        {
            var partTwoThreeLength = (int)reader.ReadBits(12);
            var bigValues = (int)reader.ReadBits(9);
            var globalGain = (int)reader.ReadBits(8);
            var scalefacCompress = (int)reader.ReadBits(4);
            var windowSwitchingFlag = reader.ReadBits(1);

            if (windowSwitchingFlag == 1)
            {
                throw new NotSupportedException("Short/mixed blocks (window_switching_flag=1) are not yet supported for MP3 decode -- only long blocks.");
            }

            var tableSelect = new int[TableSelectCount];
            for (var i = 0; i < TableSelectCount; i++)
            {
                tableSelect[i] = (int)reader.ReadBits(5);
            }

            var region0Count = (int)reader.ReadBits(4);
            var region1Count = (int)reader.ReadBits(3);
            var preflag = reader.ReadBits(1) == 1;
            var scalefacScale = reader.ReadBits(1) == 1;
            var count1TableSelect = (int)reader.ReadBits(1);

            return new Mp3GranuleChannelSideInfo(
                partTwoThreeLength,
                bigValues,
                globalGain,
                scalefacCompress,
                tableSelect,
                region0Count,
                region1Count,
                preflag,
                scalefacScale,
                count1TableSelect);
        }
    }
}
