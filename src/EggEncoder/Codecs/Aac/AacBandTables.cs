namespace EggEncoder.Codecs.Aac
{
    internal sealed class AacBandTables
    {
        private static readonly ushort[] Long96 =
        [
            0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212,
            240, 276, 320, 384, 448, 512, 576, 640, 704, 768, 832, 896, 960, 1024
        ];

        private static readonly ushort[] Short96 = [0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 92, 128];

        private static readonly ushort[] Long64 =
        [
            0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240,
            268, 304, 344, 384, 424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824, 864, 904, 944, 984, 1024
        ];

        private static readonly ushort[] Long32 =
        [
            0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80, 88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264,
            292, 320, 352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896, 928, 960, 992, 1024
        ];

        private static readonly ushort[] Long24 =
        [
            0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 76, 84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204,
            220, 240, 260, 284, 308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704, 768, 832, 896, 960, 1024
        ];

        private static readonly ushort[] Short24 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 64, 76, 92, 108, 128];

        private static readonly ushort[] Long16 =
        [
            0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124, 136, 148, 160, 172, 184, 196, 212, 228, 244, 260, 280,
            300, 320, 344, 368, 396, 424, 456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960, 1024
        ];

        private static readonly ushort[] Short16 = [0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 60, 72, 88, 108, 128];

        private static readonly ushort[] Long8 =
        [
            0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172, 188, 204, 220, 236, 252, 268, 288, 308, 328, 348,
            372, 396, 420, 448, 476, 508, 544, 580, 620, 664, 712, 764, 820, 880, 944, 1024
        ];

        private static readonly ushort[] Short8 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 60, 72, 88, 108, 128];

        private static readonly int[] TnsMaxBandsLong = [31, 31, 34, 40, 42, 51, 46, 46, 42, 42, 42, 39, 39];
        private static readonly int[] TnsMaxBandsShort = [9, 9, 10, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14];

        private AacBandTables(ushort[] longOffsets, ushort[] shortOffsets, int tnsMaxBandsLong, int tnsMaxBandsShort)
        {
            LongOffsets = longOffsets;
            ShortOffsets = shortOffsets;
            TnsMaxBandsLongWindow = tnsMaxBandsLong;
            TnsMaxBandsShortWindow = tnsMaxBandsShort;
        }

        public ushort[] LongOffsets { get; }

        public ushort[] ShortOffsets { get; }

        public int TnsMaxBandsLongWindow { get; }

        public int TnsMaxBandsShortWindow { get; }

        public static AacBandTables ForSampleRate(int sampleRate)
        {
            var index = Array.IndexOf(AacTables.SampleRates, sampleRate);
            if (index < 0)
            {
                throw new NotSupportedException($"AAC sample rate {sampleRate} Hz has no standard scalefactor band table");
            }

            return ForIndex(index);
        }

        public static AacBandTables ForIndex(int samplingFrequencyIndex)
        {
            var (longOffsets, shortOffsets) = samplingFrequencyIndex switch
            {
                0 or 1 => (Long96, Short96),
                2 => (Long64, Short96),
                3 or 4 => (AacTables.ScalefactorBandOffsets1024_44100Or48000, AacTables.ScalefactorBandOffsets128_44100Or48000),
                5 => (Long32, AacTables.ScalefactorBandOffsets128_44100Or48000),
                6 or 7 => (Long24, Short24),
                8 or 9 or 10 => (Long16, Short16),
                11 or 12 => (Long8, Short8),
                _ => throw new InvalidDataException($"Invalid AAC sampling frequency index {samplingFrequencyIndex}")
            };

            return new AacBandTables(longOffsets, shortOffsets, TnsMaxBandsLong[samplingFrequencyIndex], TnsMaxBandsShort[samplingFrequencyIndex]);
        }
    }
}
