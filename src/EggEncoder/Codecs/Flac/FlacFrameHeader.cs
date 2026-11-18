using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    internal static class FlacFrameHeader
    {
        internal const int SampleRateFromStreamInfo = 0;
        internal const int SampleRateKilohertzTail = 12;
        internal const int SampleRateHertzTail = 13;
        internal const int SampleRateTensOfHertzTail = 14;
        internal const int BlockSizeEightBitTail = 6;
        internal const int BlockSizeSixteenBitTail = 7;
        internal const int SampleSizeFromStreamInfo = 0;

        internal static int SampleRateCode(int sampleRate) => sampleRate switch
        {
            88200 => 1,
            176400 => 2,
            192000 => 3,
            8000 => 4,
            16000 => 5,
            22050 => 6,
            24000 => 7,
            32000 => 8,
            44100 => 9,
            48000 => 10,
            96000 => 11,
            _ when sampleRate % 1000 == 0 && sampleRate <= 255000 => SampleRateKilohertzTail,
            _ when sampleRate % 10 == 0 && sampleRate <= 655350 => SampleRateTensOfHertzTail,
            _ when sampleRate <= 0xFFFF => SampleRateHertzTail,
            _ => SampleRateFromStreamInfo,
        };

        internal static int SampleSizeCode(int bitsPerSample) => bitsPerSample switch
        {
            8 => 1,
            12 => 2,
            16 => 4,
            20 => 5,
            24 => 6,
            32 => 7,
            _ => SampleSizeFromStreamInfo,
        };

        internal static int BlockSizeCode(int blockSize) => blockSize switch
        {
            192 => 1,
            576 => 2,
            1152 => 3,
            2304 => 4,
            4608 => 5,
            256 => 8,
            512 => 9,
            1024 => 10,
            2048 => 11,
            4096 => 12,
            8192 => 13,
            16384 => 14,
            32768 => 15,
            <= 256 => BlockSizeEightBitTail,
            _ => BlockSizeSixteenBitTail,
        };

        internal static void WriteBlockSizeTail(BitWriter writer, int blockSizeCode, int blockSize)
        {
            if (blockSizeCode == BlockSizeEightBitTail)
            {
                writer.WriteBits((uint)(blockSize - 1), 8);
            }
            else if (blockSizeCode == BlockSizeSixteenBitTail)
            {
                writer.WriteBits((uint)(blockSize - 1), 16);
            }
        }

        internal static void WriteSampleRateTail(BitWriter writer, int sampleRateCode, int sampleRate)
        {
            switch (sampleRateCode)
            {
                case SampleRateKilohertzTail:
                    writer.WriteBits((uint)(sampleRate / 1000), 8);
                    break;
                case SampleRateHertzTail:
                    writer.WriteBits((uint)sampleRate, 16);
                    break;
                case SampleRateTensOfHertzTail:
                    writer.WriteBits((uint)(sampleRate / 10), 16);
                    break;
            }
        }
    }
}
