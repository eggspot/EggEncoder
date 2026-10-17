using EggEncoder.Codecs.Flac;

namespace EggEncoder.UnitTests.TestUtilities
{
    public static class FlacTestDecoder
    {
        public static (FlacStreamInfo StreamInfo, int[] InterleavedSamples) DecodeAll(string flacFilePath)
        {
            var samples = new List<int>();
            var streamInfo = FlacDecoder.Decode(flacFilePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            return (streamInfo, [.. samples]);
        }

        public static (FlacStreamInfo StreamInfo, int[] InterleavedSamples) DecodeAll(Stream flacStream)
        {
            var samples = new List<int>();
            var streamInfo = FlacDecoder.Decode(flacStream, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            return (streamInfo, [.. samples]);
        }
    }
}
