namespace EggEncoder.Codecs.H264.Nvenc;

internal static class NvencApi
{
    public const uint ApiVersion = 12;
    public const uint MinimumDriverApiVersion = (12 << 4) | 0;

    public const int Success = 0;
    public const int NeedMoreInput = 17;

    public const int DeviceTypeCuda = 1;
    public const int TuningHighQuality = 1;
    public const int RateControlVbr = 1;
    public const uint BufferFormatNv12 = 0x1;
    public const uint BufferFormatIyuv = 0x100;
    public const int PictureStructFrame = 1;
    public const int PictureFlagEndOfStream = 8;
    public const int PictureTypeIdr = 3;
    public const int EntropyCabac = 1;
    public const int EntropyCavlc = 2;
    public const int AdaptiveTransformDisable = 1;
    public const int AdaptiveTransformEnable = 2;

    public static class OpenSessionParams
    {
        public const int Size = 1552;
        public const uint Version = 0x7001000C;
        public const int DeviceType = 4;
        public const int Device = 8;
        public const int ApiVersion = 24;
    }

    public static class Config
    {
        public const int Size = 3584;
        public const uint Version = 0xF008000C;
        public const int ProfileGuid = 4;
        public const int GopLength = 20;
        public const int FrameIntervalP = 24;
        public const int RateControlMode = 44;
        public const int AverageBitRate = 60;
        public const int MaxBitRate = 64;
        public const int RateControlFlags = 76;
        public const uint RateControlLookaheadBit = 1u << 5;
        public const int TargetQuality = 128;
        public const int TargetQualityLsb = 129;
        public const int LookaheadDepth = 130;
        public const int H264Flags = 168;
        public const uint H264OutputAudBit = 1u << 6;
        public const uint H264DisableSpsPpsBit = 1u << 7;
        public const uint H264RepeatSpsPpsBit = 1u << 12;
        public const int H264Level = 172;
        public const int H264IdrPeriod = 176;
        public const int H264AdaptiveTransformMode = 200;
        public const int H264EntropyCodingMode = 212;
        public const int H264ChromaFormatIdc = 360;
        public const int VuiVideoSignalTypePresent = 248;
        public const int VuiVideoFormat = 252;
        public const int VuiFullRange = 256;
        public const int VuiColourDescriptionPresent = 260;
        public const int VuiColourPrimaries = 264;
        public const int VuiTransferCharacteristics = 268;
        public const int VuiColourMatrix = 272;
    }

    public static class PresetConfig
    {
        public const int Size = 5128;
        public const uint Version = 0xF004000C;
        public const int Config = 8;
    }

    public static class InitializeParams
    {
        public const int Size = 1808;
        public const uint Version = 0xF005000C;
        public const int EncodeGuid = 4;
        public const int PresetGuid = 20;
        public const int EncodeWidth = 36;
        public const int EncodeHeight = 40;
        public const int DarWidth = 44;
        public const int DarHeight = 48;
        public const int FrameRateNum = 52;
        public const int FrameRateDen = 56;
        public const int EnableEncodeAsync = 60;
        public const int EnablePtd = 64;
        public const int EncodeConfig = 88;
        public const int MaxEncodeWidth = 96;
        public const int MaxEncodeHeight = 100;
        public const int TuningInfo = 136;
    }

    public static class CreateInputBuffer
    {
        public const int Size = 776;
        public const uint Version = 0x7001000C;
        public const int Width = 4;
        public const int Height = 8;
        public const int BufferFormat = 16;
        public const int InputBuffer = 24;
    }

    public static class CreateBitstreamBuffer
    {
        public const int Size = 776;
        public const uint Version = 0x7001000C;
        public const int BitstreamBuffer = 16;
    }

    public static class LockInputBuffer
    {
        public const int Size = 1544;
        public const uint Version = 0x7001000C;
        public const int InputBuffer = 8;
        public const int BufferDataPtr = 16;
        public const int Pitch = 24;
    }

    public static class PictureParams
    {
        public const int Size = 3360;
        public const uint Version = 0xF006000C;
        public const int InputWidth = 4;
        public const int InputHeight = 8;
        public const int InputPitch = 12;
        public const int EncodePicFlags = 16;
        public const int FrameIndex = 20;
        public const int InputTimeStamp = 24;
        public const int InputDuration = 32;
        public const int InputBuffer = 40;
        public const int OutputBitstream = 48;
        public const int BufferFormat = 64;
        public const int PictureStruct = 68;
    }

    public static class LockBitstream
    {
        public const int Size = 1544;
        public const uint Version = 0x7002000C;
        public const int OutputBitstream = 8;
        public const int BitstreamSizeInBytes = 36;
        public const int BitstreamBufferPtr = 56;
        public const int PictureType = 64;
    }

    public static class SequenceParamPayload
    {
        public const int Size = 1544;
        public const uint Version = 0x7001000C;
        public const int InBufferSize = 4;
        public const int SpsPpsBuffer = 16;
        public const int OutSpsPpsPayloadSize = 24;
    }

    public static class FunctionList
    {
        public const int Size = 2552;
        public const uint Version = 0x7002000C;
        public const int GetInputFormatCount = 48;
        public const int GetInputFormats = 56;
        public const int InitializeEncoder = 96;
        public const int CreateInputBuffer = 104;
        public const int DestroyInputBuffer = 112;
        public const int CreateBitstreamBuffer = 120;
        public const int DestroyBitstreamBuffer = 128;
        public const int EncodePicture = 136;
        public const int LockBitstream = 144;
        public const int UnlockBitstream = 152;
        public const int LockInputBuffer = 160;
        public const int UnlockInputBuffer = 168;
        public const int GetSequenceParams = 184;
        public const int DestroyEncoder = 224;
        public const int OpenEncodeSessionEx = 240;
        public const int GetLastErrorString = 304;
        public const int GetEncodePresetConfigEx = 320;
    }

    public static readonly Guid CodecH264 = FromBytes(0x62, 0x27, 0xC8, 0x6B, 0x63, 0x4E, 0xA4, 0x4C, 0xAA, 0x85, 0x1E, 0x50, 0xF3, 0x21, 0xF6, 0xBF);
    public static readonly Guid ProfileBaseline = FromBytes(0xAA, 0xBC, 0x27, 0x07, 0xC4, 0x78, 0x83, 0x4C, 0x8C, 0x2F, 0xEF, 0x3D, 0xFF, 0x26, 0x7C, 0x6A);
    public static readonly Guid ProfileMain = FromBytes(0xD4, 0xC1, 0xB5, 0x60, 0xFE, 0x67, 0x90, 0x47, 0x94, 0xD5, 0xC4, 0x72, 0x6D, 0x7B, 0x6E, 0x6D);
    public static readonly Guid ProfileHigh = FromBytes(0x09, 0xC3, 0xCB, 0xE7, 0x7A, 0x4F, 0x89, 0x4B, 0xAF, 0x2A, 0xD5, 0x37, 0xC9, 0x2B, 0xE3, 0x10);

    public static readonly Guid[] Presets =
    [
        FromBytes(0x3E, 0x8D, 0x0A, 0xFC, 0xF8, 0x45, 0xF8, 0x4C, 0x80, 0xC7, 0x29, 0x88, 0x71, 0x59, 0x0E, 0xBF),
        FromBytes(0xB8, 0xCF, 0x81, 0xF5, 0xD6, 0x88, 0x81, 0x43, 0x93, 0xF0, 0xDF, 0x13, 0xF9, 0xC2, 0x7D, 0xAB),
        FromBytes(0x10, 0x01, 0x85, 0x36, 0x07, 0x3A, 0x1F, 0x44, 0x94, 0xD5, 0x36, 0x70, 0x63, 0x1F, 0x91, 0xF6),
        FromBytes(0x26, 0xB8, 0xA7, 0x90, 0x06, 0xDF, 0x62, 0x48, 0xB9, 0xD2, 0xCD, 0x6D, 0x73, 0xA0, 0x86, 0x81),
        FromBytes(0xB4, 0xE6, 0xC6, 0x21, 0x7A, 0x29, 0xBA, 0x4C, 0x99, 0x8F, 0xB6, 0xCB, 0xDE, 0x72, 0xAD, 0xE3),
        FromBytes(0x79, 0xC2, 0x75, 0x8E, 0x99, 0x62, 0xB6, 0x4A, 0x83, 0x02, 0x0B, 0x21, 0x5A, 0x33, 0x5C, 0xF5),
        FromBytes(0x12, 0x8C, 0x84, 0x84, 0x71, 0x6F, 0x13, 0x4C, 0x93, 0x1B, 0x53, 0xE2, 0x83, 0xF5, 0x79, 0x74)
    ];

    private static Guid FromBytes(params byte[] bytes) => new(bytes);
}
