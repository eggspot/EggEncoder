namespace EggEncoder.Codecs.H264.Nvdec;

internal static class NvdecApi
{
    public const int CodecH264 = 4;
    public const int ChromaFormat420 = 1;
    public const int SurfaceFormatNv12 = 0;
    public const int DeinterlaceWeave = 0;
    public const int CreatePreferCuvid = 4;
    public const int PacketEndOfStream = 1;
    public const int PacketTimestamp = 2;

    private static readonly bool LongIsFourBytes = OperatingSystem.IsWindows();

    public static int ULongSize => LongIsFourBytes ? 4 : 8;

    public static class ParserParams
    {
        public const int Size = 136;
        public const int CodecType = 0;
        public const int MaxNumDecodeSurfaces = 4;
        public const int ClockRate = 8;
        public const int ErrorThreshold = 12;
        public const int MaxDisplayDelay = 16;
        public const int UserData = 40;
        public const int SequenceCallback = 48;
        public const int DecodePicture = 56;
        public const int DisplayPicture = 64;
    }

    public static class VideoFormat
    {
        public const int Codec = 0;
        public const int ProgressiveSequence = 12;
        public const int BitDepthLumaMinus8 = 13;
        public const int MinNumDecodeSurfaces = 15;
        public const int CodedWidth = 16;
        public const int CodedHeight = 20;
        public const int DisplayLeft = 24;
        public const int DisplayTop = 28;
        public const int DisplayRight = 32;
        public const int DisplayBottom = 36;
        public const int ChromaFormat = 40;
        public const int SignalFlags = 56;
        public const byte FullRangeBit = 0x08;
        public const int MatrixCoefficients = 59;
    }

    public static class DecodeCreateInfo
    {
        public static int Size => LongIsFourBytes ? 112 : 176;

        public static int Width => 0;

        public static int Height => LongIsFourBytes ? 4 : 8;

        public static int NumDecodeSurfaces => LongIsFourBytes ? 8 : 16;

        public static int CodecType => LongIsFourBytes ? 12 : 24;

        public static int ChromaFormat => LongIsFourBytes ? 16 : 28;

        public static int CreationFlags => LongIsFourBytes ? 20 : 32;

        public static int BitDepthMinus8 => LongIsFourBytes ? 24 : 40;

        public static int MaxWidth => LongIsFourBytes ? 32 : 56;

        public static int MaxHeight => LongIsFourBytes ? 36 : 64;

        public static int DisplayLeft => LongIsFourBytes ? 44 : 80;

        public static int DisplayTop => LongIsFourBytes ? 46 : 82;

        public static int DisplayRight => LongIsFourBytes ? 48 : 84;

        public static int DisplayBottom => LongIsFourBytes ? 50 : 86;

        public static int OutputFormat => LongIsFourBytes ? 52 : 88;

        public static int DeinterlaceMode => LongIsFourBytes ? 56 : 92;

        public static int TargetWidth => LongIsFourBytes ? 60 : 96;

        public static int TargetHeight => LongIsFourBytes ? 64 : 104;

        public static int NumOutputSurfaces => LongIsFourBytes ? 68 : 112;
    }

    public static class DisplayInfo
    {
        public const int PictureIndex = 0;
        public const int ProgressiveFrame = 4;
        public const int TopFieldFirst = 8;
        public const int Timestamp = 16;
    }

    public static class ProcessingParams
    {
        public const int Size = 264;
        public const int ProgressiveFrame = 0;
        public const int SecondField = 4;
        public const int TopFieldFirst = 8;
    }

    public static class SourcePacket
    {
        public static int Size => LongIsFourBytes ? 24 : 32;

        public static int Flags => 0;

        public static int PayloadSize => LongIsFourBytes ? 4 : 8;

        public static int Payload => LongIsFourBytes ? 8 : 16;

        public static int Timestamp => LongIsFourBytes ? 16 : 24;
    }

    public static class Memcpy2D
    {
        public const int Size = 128;
        public const int SourceMemoryType = 16;
        public const int SourceDevice = 32;
        public const int SourcePitch = 48;
        public const int DestinationMemoryType = 72;
        public const int DestinationHost = 80;
        public const int DestinationPitch = 104;
        public const int WidthInBytes = 112;
        public const int Height = 120;
    }
}
