using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Probe
    {
        private const int MaxSyncSearchBytes = 65536;

        private static readonly int[] _mpeg1Layer3BitrateKbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1];
        private static readonly int[] _mpeg2Layer3BitrateKbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1];
        private static readonly int[] _mpeg1SampleRates = [44100, 48000, 32000, -1];
        private static readonly int[] _mpeg2SampleRates = [22050, 24000, 16000, -1];
        private static readonly int[] _mpeg25SampleRates = [11025, 12000, 8000, -1];

        public static Mp3ProbeResult Probe(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            stream.Position = SkipId3V2Tag(stream);

            var frame = FindFirstFrame(stream)
                ?? throw new InvalidDataException($"'{filePath}' does not contain a valid MP3 frame");

            var vbrInfo = TryReadVbrHeader(stream, frame.Offset, frame.Header);
            var trailingTagSize = HasId3V1Tag(stream) ? 128 : 0;
            var audioDataLength = stream.Length - frame.Offset - trailingTagSize;

            int durationInSeconds;
            int bitRateKbps;
            bool isVariableBitRate;

            if (vbrInfo is { FrameCount: > 0 } knownVbrInfo)
            {
                var samplesPerFrame = frame.Header.IsMpeg1 ? 1152 : 576;
                var totalSamples = (long)knownVbrInfo.FrameCount * samplesPerFrame;
                durationInSeconds = (int)(totalSamples / frame.Header.SampleRate);
                bitRateKbps = durationInSeconds > 0 && knownVbrInfo.ByteCount > 0
                    ? (int)(knownVbrInfo.ByteCount * 8 / 1000 / durationInSeconds)
                    : frame.Header.BitRateKbps;
                isVariableBitRate = knownVbrInfo.IsVbr;
            }
            else
            {
                durationInSeconds = (int)(audioDataLength * 8 / 1000 / frame.Header.BitRateKbps);
                bitRateKbps = frame.Header.BitRateKbps;
                isVariableBitRate = false;
            }

            return new Mp3ProbeResult
            {
                DurationInSeconds = durationInSeconds,
                SampleRate = frame.Header.SampleRate,
                Channels = frame.Header.Channels,
                BitRate = bitRateKbps * 1000,
                IsVariableBitRate = isVariableBitRate
            };
        }

        private static long SkipId3V2Tag(Stream stream)
        {
            if (stream.Length < 10)
            {
                return 0;
            }

            stream.Position = 0;
            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);

            if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
            {
                return 0;
            }

            var size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            return 10 + size;
        }

        private static Mp3Frame? FindFirstFrame(Stream stream)
        {
            var searchStart = stream.Position;
            var bytesToSearch = (int)Math.Min(stream.Length - searchStart, MaxSyncSearchBytes);
            if (bytesToSearch < 4)
            {
                return null;
            }

            var buffer = new byte[bytesToSearch];
            stream.ReadExactly(buffer);

            for (var i = 0; i + 4 <= buffer.Length; i++)
            {
                var header = TryParseFrameHeader(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
                if (header is not null)
                {
                    return new Mp3Frame(header.Value, searchStart + i);
                }
            }

            return null;
        }

        private static Mp3FrameHeader? TryParseFrameHeader(byte b0, byte b1, byte b2, byte b3)
        {
            if (b0 != 0xFF || (b1 & 0xE0) != 0xE0)
            {
                return null;
            }

            var versionBits = (b1 >> 3) & 0x03;
            var layerBits = (b1 >> 1) & 0x03;

            if (versionBits == 1 || layerBits != 1)
            {
                return null;
            }

            var isMpeg1 = versionBits == 3;
            var isMpeg25 = versionBits == 0;
            var hasCrc = (b1 & 0x01) == 0;

            var bitrateIndex = (b2 >> 4) & 0x0F;
            var sampleRateIndex = (b2 >> 2) & 0x03;
            var padding = (b2 >> 1) & 0x01;

            var channelModeBits = (b3 >> 6) & 0x03;
            var channels = channelModeBits == 3 ? 1 : 2;

            var bitrateKbps = (isMpeg1 ? _mpeg1Layer3BitrateKbps : _mpeg2Layer3BitrateKbps)[bitrateIndex];
            var sampleRate = (isMpeg1 ? _mpeg1SampleRates : isMpeg25 ? _mpeg25SampleRates : _mpeg2SampleRates)[sampleRateIndex];

            if (bitrateKbps <= 0 || sampleRate <= 0)
            {
                return null;
            }

            var frameLengthBytes = ((isMpeg1 ? 144 : 72) * bitrateKbps * 1000 / sampleRate) + padding;
            var sideInfoSize = isMpeg1
                ? (channels == 1 ? 17 : 32)
                : (channels == 1 ? 9 : 17);

            return new Mp3FrameHeader(sampleRate, bitrateKbps, channels, frameLengthBytes, sideInfoSize, hasCrc, isMpeg1);
        }

        private static VbrInfo? TryReadVbrHeader(Stream stream, long frameOffset, Mp3FrameHeader header)
        {
            var vbrHeaderOffset = frameOffset + 4 + (header.HasCrc ? 2 : 0) + header.SideInfoSize;
            if (vbrHeaderOffset + 8 > stream.Length)
            {
                return null;
            }

            stream.Position = vbrHeaderOffset;
            Span<byte> tagBuffer = stackalloc byte[4];
            stream.ReadExactly(tagBuffer);

            var tag = Encoding.ASCII.GetString(tagBuffer);
            if (tag != "Xing" && tag != "Info")
            {
                return null;
            }

            Span<byte> flagsBuffer = stackalloc byte[4];
            stream.ReadExactly(flagsBuffer);
            var flags = BinaryPrimitives.ReadUInt32BigEndian(flagsBuffer);

            uint frameCount = 0;
            uint byteCount = 0;

            if ((flags & 0x01) != 0)
            {
                Span<byte> frameCountBuffer = stackalloc byte[4];
                stream.ReadExactly(frameCountBuffer);
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(frameCountBuffer);
            }

            if ((flags & 0x02) != 0)
            {
                Span<byte> byteCountBuffer = stackalloc byte[4];
                stream.ReadExactly(byteCountBuffer);
                byteCount = BinaryPrimitives.ReadUInt32BigEndian(byteCountBuffer);
            }

            return new VbrInfo(frameCount, byteCount, tag == "Xing");
        }

        private static bool HasId3V1Tag(Stream stream)
        {
            if (stream.Length < 128)
            {
                return false;
            }

            stream.Position = stream.Length - 128;
            Span<byte> tag = stackalloc byte[3];
            stream.ReadExactly(tag);
            return tag[0] == (byte)'T' && tag[1] == (byte)'A' && tag[2] == (byte)'G';
        }
    }

    public class Mp3ProbeResult
    {
        public required int DurationInSeconds { get; init; }

        public required int SampleRate { get; init; }

        public required int Channels { get; init; }

        public required int BitRate { get; init; }

        public required bool IsVariableBitRate { get; init; }
    }

    internal readonly record struct Mp3FrameHeader(int SampleRate, int BitRateKbps, int Channels, int FrameLengthBytes, int SideInfoSize, bool HasCrc, bool IsMpeg1);

    internal readonly record struct Mp3Frame(Mp3FrameHeader Header, long Offset);

    internal readonly record struct VbrInfo(uint FrameCount, uint ByteCount, bool IsVbr);
}
