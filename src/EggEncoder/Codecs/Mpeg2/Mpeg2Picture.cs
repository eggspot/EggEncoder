namespace EggEncoder.Codecs.Mpeg2
{
    internal sealed class Mpeg2Picture
    {
        public Mpeg2Picture(int macroblockWidth, int macroblockHeight)
        {
            LumaWidth = macroblockWidth * 16;
            LumaHeight = macroblockHeight * 16;
            ChromaWidth = macroblockWidth * 8;
            ChromaHeight = macroblockHeight * 8;
            Planes = [new byte[LumaWidth * LumaHeight], new byte[ChromaWidth * ChromaHeight], new byte[ChromaWidth * ChromaHeight]];
        }

        public byte[][] Planes { get; }

        public int LumaWidth { get; }

        public int LumaHeight { get; }

        public int ChromaWidth { get; }

        public int ChromaHeight { get; }

        public TimeSpan Timestamp { get; set; }

        public bool IsBidirectional { get; set; }

        public int Width(int plane) => plane == 0 ? LumaWidth : ChromaWidth;

        public int Height(int plane) => plane == 0 ? LumaHeight : ChromaHeight;
    }
}
