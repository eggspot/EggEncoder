namespace EggEncoder.Codecs.Mp3;

internal enum MpegAudioVersion
{
    Mpeg1,
    Mpeg2,
    Mpeg25
}

internal readonly record struct MpegAudioHeader(
    MpegAudioVersion Version,
    int Layer,
    bool HasCrc,
    int BitRateKbps,
    int SampleRate,
    int SampleRateIndex,
    bool Padding,
    int ChannelMode,
    int ModeExtension)
{
    private static readonly int[] Mpeg1BitRates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] LsfBitRates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRates = [44100, 48000, 32000, 22050, 24000, 16000, 11025, 12000, 8000];

    public const int MonoMode = 3;

    public const int JointStereoMode = 1;

    public bool IsLsf => Version != MpegAudioVersion.Mpeg1;

    public int Channels => ChannelMode == MonoMode ? 1 : 2;

    public int SamplesPerFrame => IsLsf ? 576 : 1152;

    public int Granules => IsLsf ? 1 : 2;

    public int FrameLength => ((IsLsf ? 72 : 144) * BitRateKbps * 1000 / SampleRate) + (Padding ? 1 : 0);

    public int SideInfoLength => IsLsf ? (Channels == 1 ? 9 : 17) : (Channels == 1 ? 17 : 32);

    public bool MidSide => ChannelMode == JointStereoMode && (ModeExtension & 2) != 0;

    public bool Intensity => ChannelMode == JointStereoMode && (ModeExtension & 1) != 0;

    public static bool TryParse(ReadOnlySpan<byte> bytes, out MpegAudioHeader header)
    {
        header = default;
        if (bytes.Length < 4 || bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var versionBits = (bytes[1] >> 3) & 3;
        var layerBits = (bytes[1] >> 1) & 3;
        var bitRateIndex = bytes[2] >> 4;
        var sampleRateBits = (bytes[2] >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitRateIndex is 0 or 15 || sampleRateBits == 3)
        {
            return false;
        }

        var version = versionBits switch
        {
            3 => MpegAudioVersion.Mpeg1,
            2 => MpegAudioVersion.Mpeg2,
            _ => MpegAudioVersion.Mpeg25
        };
        var sampleRateIndex = ((int)version * 3) + sampleRateBits;
        var layer = 4 - layerBits;
        var bitRate = layer == 3 ? (version == MpegAudioVersion.Mpeg1 ? Mpeg1BitRates : LsfBitRates)[bitRateIndex] : 0;
        header = new MpegAudioHeader(
            version,
            layer,
            (bytes[1] & 1) == 0,
            bitRate,
            SampleRates[sampleRateIndex],
            sampleRateIndex,
            (bytes[2] & 2) != 0,
            bytes[3] >> 6,
            (bytes[3] >> 4) & 3);
        return true;
    }

    public bool IsCompatibleWith(MpegAudioHeader other) =>
        Version == other.Version && Layer == other.Layer && SampleRate == other.SampleRate;
}
