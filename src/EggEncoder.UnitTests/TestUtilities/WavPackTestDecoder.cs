using EggEncoder.Codecs.WavPack;

namespace EggEncoder.UnitTests.TestUtilities
{
    public static class WavPackTestDecoder
    {
        public static (WavPackStreamInfo StreamInfo, int[] InterleavedSamples) DecodeAll(string wvFilePath)
        {
            var samples = new List<int>();
            var streamInfo = WavPackDecoder.Decode(wvFilePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            return (streamInfo, [.. samples]);
        }
    }
}
