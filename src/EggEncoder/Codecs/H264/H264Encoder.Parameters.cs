using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

public sealed partial class H264Encoder
{
    private static readonly (int Level, long MaxMacroblocksPerSecond, int MaxFrameSize, int MaxDpbMacroblocks)[] Levels =
    [
        (10, 1485, 99, 396),
        (11, 3000, 396, 900),
        (12, 6000, 396, 2376),
        (13, 11880, 396, 2376),
        (20, 11880, 396, 2376),
        (21, 19800, 792, 4752),
        (22, 20250, 1620, 8100),
        (30, 40500, 1620, 8100),
        (31, 108000, 3600, 18000),
        (32, 216000, 5120, 20480),
        (40, 245760, 8192, 32768),
        (41, 245760, 8192, 32768),
        (42, 522240, 8704, 34816),
        (50, 589824, 22080, 110400),
        (51, 983040, 36864, 184320),
        (52, 2073600, 36864, 184320)
    ];

    internal static int ChooseLevel(int widthInMacroblocks, int heightInMacroblocks, VideoFrameRate frameRate, int referenceFrames)
    {
        var frameSize = widthInMacroblocks * heightInMacroblocks;
        var rate = (long)Math.Ceiling(frameSize * frameRate.FramesPerSecond);
        foreach (var (level, maxRate, maxFrameSize, maxDpb) in Levels)
        {
            var maxSide = Math.Sqrt(8.0 * maxFrameSize);
            if (frameSize <= maxFrameSize && rate <= maxRate && widthInMacroblocks <= maxSide && heightInMacroblocks <= maxSide && frameSize * referenceFrames <= maxDpb)
            {
                return level;
            }
        }

        throw new NotSupportedException($"H.264: {widthInMacroblocks * 16}x{heightInMacroblocks * 16} at {frameRate} fps needs more than level 5.2 allows");
    }

    private static byte[] WriteSequenceParameterSet(H264EncoderSettings settings, VideoColorRange range, VideoColorMatrix matrix)
    {
        var widthInMacroblocks = (settings.Width + 15) / 16;
        var heightInMacroblocks = (settings.Height + 15) / 16;
        var level = ChooseLevel(widthInMacroblocks, heightInMacroblocks, settings.FrameRate, 1);
        var bits = new BitWriter();
        var profileIdc = settings.Profile switch
        {
            H264Profile.Baseline => 66,
            H264Profile.Main => 77,
            _ => 100
        };
        bits.WriteBits((uint)profileIdc, 8);
        bits.WriteBits(settings.Profile == H264Profile.Baseline ? 0xC0u : settings.Profile == H264Profile.Main ? 0x40u : 0u, 8);
        bits.WriteBits((uint)level, 8);
        bits.WriteUe(0);
        if (profileIdc == 100)
        {
            bits.WriteUe(1);
            bits.WriteUe(0);
            bits.WriteUe(0);
            bits.WriteFlag(false);
            bits.WriteFlag(false);
        }

        bits.WriteUe(Log2MaxFrameNum - 4);
        bits.WriteUe(2);
        bits.WriteUe(1);
        bits.WriteFlag(false);
        bits.WriteUe((uint)(widthInMacroblocks - 1));
        bits.WriteUe((uint)(heightInMacroblocks - 1));
        bits.WriteFlag(true);
        bits.WriteFlag(true);
        var cropRight = ((widthInMacroblocks * 16) - settings.Width) / 2;
        var cropBottom = ((heightInMacroblocks * 16) - settings.Height) / 2;
        var cropping = cropRight > 0 || cropBottom > 0;
        bits.WriteFlag(cropping);
        if (cropping)
        {
            bits.WriteUe(0);
            bits.WriteUe((uint)cropRight);
            bits.WriteUe(0);
            bits.WriteUe((uint)cropBottom);
        }

        bits.WriteFlag(true);
        WriteVideoUsability(bits, settings.FrameRate, range, matrix);
        bits.WriteTrailingBits();
        return bits.ToArray();
    }

    private static void WriteVideoUsability(BitWriter bits, VideoFrameRate frameRate, VideoColorRange range, VideoColorMatrix matrix)
    {
        bits.WriteFlag(true);
        bits.WriteBits(1, 8);
        bits.WriteFlag(false);
        bits.WriteFlag(true);
        bits.WriteBits(5, 3);
        bits.WriteFlag(range == VideoColorRange.Full);
        bits.WriteFlag(true);
        var colour = matrix == VideoColorMatrix.Bt709 ? 1u : 6u;
        bits.WriteBits(colour, 8);
        bits.WriteBits(colour, 8);
        bits.WriteBits(colour, 8);
        bits.WriteFlag(false);
        bits.WriteFlag(true);
        bits.WriteBits((uint)frameRate.Denominator, 32);
        bits.WriteBits((uint)frameRate.Numerator * 2, 32);
        bits.WriteFlag(true);
        bits.WriteFlag(false);
        bits.WriteFlag(false);
        bits.WriteFlag(false);
        bits.WriteFlag(true);
        bits.WriteFlag(true);
        bits.WriteUe(0);
        bits.WriteUe(0);
        bits.WriteUe(16);
        bits.WriteUe(16);
        bits.WriteUe(0);
        bits.WriteUe(1);
    }

    private static byte[] WritePictureParameterSet(H264EncoderSettings settings)
    {
        var bits = new BitWriter();
        bits.WriteUe(0);
        bits.WriteUe(0);
        bits.WriteFlag(settings.Profile != H264Profile.Baseline);
        bits.WriteFlag(false);
        bits.WriteUe(0);
        bits.WriteUe(0);
        bits.WriteUe(0);
        bits.WriteFlag(false);
        bits.WriteBits(0, 2);
        bits.WriteSe(0);
        bits.WriteSe(0);
        bits.WriteSe(0);
        bits.WriteFlag(true);
        bits.WriteFlag(false);
        bits.WriteFlag(false);
        if (settings.Profile == H264Profile.High)
        {
            bits.WriteFlag(true);
            bits.WriteFlag(false);
            bits.WriteSe(0);
        }

        bits.WriteTrailingBits();
        return bits.ToArray();
    }

    private void WriteSliceHeader(BitWriter bits, SliceHeader header, bool keyframe)
    {
        bits.WriteUe(0);
        bits.WriteUe(keyframe ? 7u : 5u);
        bits.WriteUe(0);
        bits.WriteBits((uint)header.FrameNum, Log2MaxFrameNum);
        if (keyframe)
        {
            bits.WriteUe((uint)_idrPictureId);
        }
        else
        {
            bits.WriteFlag(false);
            bits.WriteFlag(false);
        }

        if (keyframe)
        {
            bits.WriteFlag(false);
            bits.WriteFlag(false);
        }
        else
        {
            bits.WriteFlag(false);
        }

        if (_pps.EntropyCodingCabac && !keyframe)
        {
            bits.WriteUe(0);
        }

        bits.WriteSe(header.QpDelta);
        bits.WriteUe(0);
        bits.WriteSe(0);
        bits.WriteSe(0);
    }
}
