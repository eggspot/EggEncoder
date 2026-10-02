using System.Buffers.Binary;

namespace EggEncoder.Codecs.Alac
{
    // The 24-byte ALAC "magic cookie" (ALACSpecificConfig), stored verbatim as a CAF 'kuki' chunk's
    // payload (no wrapper -- unlike the MP4 container, which wraps the same 24 bytes inside its own
    // 'alac' box with an extra 12-byte header this reader/writer never has to deal with, since this
    // codebase only speaks the CAF container for ALAC). All fields are big-endian.
    internal sealed class AlacSpecificConfig
    {
        public const int EncodedSize = 24;

        public required int FrameLength { get; init; }

        public required int BitDepth { get; init; }

        public required int Pb { get; init; }

        public required int Mb { get; init; }

        public required int Kb { get; init; }

        public required int NumChannels { get; init; }

        public required int MaxRun { get; init; }

        public required int SampleRate { get; init; }

        public static AlacSpecificConfig Parse(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < EncodedSize)
            {
                throw new InvalidDataException($"ALACSpecificConfig must be {EncodedSize} bytes, got {bytes.Length}");
            }

            var frameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var bitDepth = bytes[5];
            var pb = bytes[6];
            var mb = bytes[7];
            var kb = bytes[8];
            var numChannels = bytes[9];
            var maxRun = BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]);
            var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);

            if (frameLength <= 0)
            {
                throw new InvalidDataException($"ALACSpecificConfig has a non-positive frameLength ({frameLength})");
            }

            if (numChannels == 0)
            {
                throw new InvalidDataException("ALACSpecificConfig declares 0 channels");
            }

            if (kb == 0)
            {
                throw new InvalidDataException("ALACSpecificConfig declares a Rice parameter limit (kb) of 0, which cannot encode anything");
            }

            return new AlacSpecificConfig
            {
                FrameLength = frameLength,
                BitDepth = bitDepth,
                Pb = pb,
                Mb = mb,
                Kb = kb,
                NumChannels = numChannels,
                MaxRun = maxRun,
                SampleRate = sampleRate
            };
        }

        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < EncodedSize)
            {
                throw new ArgumentException($"Destination must be at least {EncodedSize} bytes", nameof(destination));
            }

            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)FrameLength);
            destination[4] = 0; // compatibleVersion
            destination[5] = (byte)BitDepth;
            destination[6] = (byte)Pb;
            destination[7] = (byte)Mb;
            destination[8] = (byte)Kb;
            destination[9] = (byte)NumChannels;
            BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)MaxRun);
            BinaryPrimitives.WriteUInt32BigEndian(destination[12..], 0); // maxFrameBytes, unknown
            BinaryPrimitives.WriteUInt32BigEndian(destination[16..], 0); // avgBitRate, unknown
            BinaryPrimitives.WriteUInt32BigEndian(destination[20..], (uint)SampleRate);
        }
    }
}
