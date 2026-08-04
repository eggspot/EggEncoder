using EggEncoder.Codecs.Mp3;

namespace EggEncoder.UnitTests.TestUtilities
{
    public static class Mp3TestDecoder
    {
        public static (Mp3StreamInfo StreamInfo, int[] InterleavedSamples) DecodeAll(string mp3FilePath)
        {
            var samples = new List<int>();
            var streamInfo = Mp3Decoder.Decode(mp3FilePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            return (streamInfo, [.. samples]);
        }
    }
}
