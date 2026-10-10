namespace EggEncoder.Codecs.WavPack
{
    // The 32-byte WavPack block header, laid out exactly per the official WavPack 4/5 binary file
    // format specification (https://www.wavpack.com/WavPack5FileFormat.pdf, section 2.0) -- this
    // part of the format (unlike the actual decorrelation/entropy codec, which that same document
    // explicitly does not describe) is fully and precisely documented, so this class is implemented
    // directly from that spec rather than from general recollection.
    internal sealed class WavPackBlockHeader
    {
        public const int ByteLength = 32;

        public required uint CkSize { get; init; }

        public required ushort Version { get; init; }

        public required long BlockIndex { get; init; }

        public required long TotalSamples { get; init; }

        public required uint BlockSamples { get; init; }

        public required uint Flags { get; init; }

        public required uint Crc { get; init; }

        public int BytesPerSample => (int)(Flags & 0x3) + 1;

        public bool IsMono => (Flags & 0x4) != 0;

        public bool IsHybrid => (Flags & 0x8) != 0;

        public bool IsJointStereo => (Flags & 0x10) != 0;

        public bool IsFloat => (Flags & 0x80) != 0;

        public int LeftShift => (int)((Flags >> 13) & 0x1F);

        public int SampleRateIndex => (int)((Flags >> 23) & 0xF);

        public bool IsFalseStereo => (Flags & 0x4000_0000) != 0;

        public bool IsInitialBlockOfSequence => (Flags & 0x800) != 0;

        public bool IsFinalBlockOfSequence => (Flags & 0x1000) != 0;

        public int BitsPerSample => BytesPerSample * 8;

        // RFC-style "the 15 standard rates, index 15 means consult ID_SAMPLE_RATE metadata instead"
        // table from the same spec section.
        private static readonly int[] _standardSampleRates =
        [
            6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000,
        ];

        public int? StandardSampleRate => SampleRateIndex < _standardSampleRates.Length ? _standardSampleRates[SampleRateIndex] : null;

        public static WavPackBlockHeader Parse(byte[] data, int offset)
        {
            if (data[offset] != (byte)'w' || data[offset + 1] != (byte)'v' || data[offset + 2] != (byte)'p' || data[offset + 3] != (byte)'k')
            {
                throw new InvalidDataException("Expected a WavPack block ('wvpk' magic) but found none -- the file is corrupt, truncated, or not WavPack.");
            }

            var ckSize = ReadUInt32(data, offset + 4);
            var version = ReadUInt16(data, offset + 8);
            var blockIndexHigh = data[offset + 10];
            var totalSamplesHigh = data[offset + 11];
            var totalSamplesLow = ReadUInt32(data, offset + 12);
            var blockIndexLow = ReadUInt32(data, offset + 16);
            var blockSamples = ReadUInt32(data, offset + 20);
            var flags = ReadUInt32(data, offset + 24);
            var crc = ReadUInt32(data, offset + 28);

            // total_samples' lower 32 bits all set is the spec's own "unknown total length"
            // sentinel -- never combined with the high byte in that case.
            var totalSamples = totalSamplesLow == 0xFFFFFFFF
                ? -1L
                : ((long)totalSamplesHigh << 32) | totalSamplesLow;

            return new WavPackBlockHeader
            {
                CkSize = ckSize,
                Version = version,
                BlockIndex = ((long)blockIndexHigh << 32) | blockIndexLow,
                TotalSamples = totalSamples,
                BlockSamples = blockSamples,
                Flags = flags,
                Crc = crc,
            };
        }

        private static ushort ReadUInt16(byte[] data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }
}
