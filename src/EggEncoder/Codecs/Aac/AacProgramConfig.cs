using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    internal static class AacProgramConfig
    {
        public static void Skip(BitReader reader)
        {
            reader.SkipBits(4 + 2 + 4);
            var front = (int)reader.ReadBits(4);
            var side = (int)reader.ReadBits(4);
            var back = (int)reader.ReadBits(4);
            var lfe = (int)reader.ReadBits(2);
            var associated = (int)reader.ReadBits(3);
            var coupling = (int)reader.ReadBits(4);
            for (var i = 0; i < 3; i++)
            {
                if (reader.ReadBits(1) != 0)
                {
                    reader.SkipBits(i == 2 ? 3 : 4);
                }
            }

            reader.SkipBits((front + side + back) * 5);

            reader.SkipBits((lfe * 4) + (associated * 4) + (coupling * 5));
            reader.ByteAlign();
            var commentBytes = (int)reader.ReadBits(8);
            reader.SkipBits(commentBytes * 8);
        }
    }
}
