using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Alac
{
    // Writes a CAF (Core Audio Format) file holding ALAC-compressed audio. The whole packet list is
    // known by the time this is called (see AlacEncoderSession), so this writes the file in one pass,
    // with no need to seek back and patch a size field afterward: 'desc' and 'kuki' are fixed-size and
    // known up front; 'pakt' needs the full packet-size list, which by now it has; 'data' is written
    // last with its exact (not "-1 = to EOF") size, since nothing about it depends on anything written
    // after it.
    internal static class CafWriter
    {
        public static void Write(Stream destination, AlacSpecificConfig config, IReadOnlyList<byte[]> packets, long totalValidFrames)
        {
            using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);

            writer.Write("caff"u8);
            WriteUInt16BigEndian(writer, 1); // mFileVersion
            WriteUInt16BigEndian(writer, 0); // mFileFlags

            WriteDescChunk(writer, config);
            WriteKukiChunk(writer, config);
            WritePaktChunk(writer, packets, totalValidFrames);
            WriteDataChunk(writer, packets);
        }

        private static void WriteDescChunk(BinaryWriter writer, AlacSpecificConfig config)
        {
            writer.Write("desc"u8);
            WriteInt64BigEndian(writer, 32); // chunk size: CAFAudioFormat is a fixed 32 bytes

            WriteDoubleBigEndian(writer, config.SampleRate);
            writer.Write("alac"u8);
            WriteUInt32BigEndian(writer, 0); // mFormatFlags -- ALAC defines none
            WriteUInt32BigEndian(writer, 0); // mBytesPerPacket -- variable (compressed)
            WriteUInt32BigEndian(writer, (uint)config.FrameLength); // mFramesPerPacket
            WriteUInt32BigEndian(writer, (uint)config.NumChannels); // mChannelsPerFrame
            WriteUInt32BigEndian(writer, 0); // mBitsPerChannel -- 0 for a compressed format; depth lives in the cookie
        }

        private static void WriteKukiChunk(BinaryWriter writer, AlacSpecificConfig config)
        {
            writer.Write("kuki"u8);
            WriteInt64BigEndian(writer, AlacSpecificConfig.EncodedSize);

            Span<byte> cookie = stackalloc byte[AlacSpecificConfig.EncodedSize];
            config.WriteTo(cookie);
            writer.Write(cookie);
        }

        private static void WritePaktChunk(BinaryWriter writer, IReadOnlyList<byte[]> packets, long totalValidFrames)
        {
            var sizeFields = new List<byte>();
            foreach (var packet in packets)
            {
                AppendVariableLengthQuantity(sizeFields, packet.Length);
            }

            writer.Write("pakt"u8);
            WriteInt64BigEndian(writer, 24 + sizeFields.Count);

            WriteInt64BigEndian(writer, packets.Count); // mNumberPackets
            WriteInt64BigEndian(writer, totalValidFrames); // mNumberValidFrames
            WriteInt32BigEndian(writer, 0); // mPrimingFrames
            WriteInt32BigEndian(writer, 0); // mRemainderFrames
            writer.Write(sizeFields.ToArray());
        }

        private static void WriteDataChunk(BinaryWriter writer, IReadOnlyList<byte[]> packets)
        {
            var totalPacketBytes = packets.Sum(packet => (long)packet.Length);

            writer.Write("data"u8);
            WriteInt64BigEndian(writer, 4 + totalPacketBytes);

            WriteUInt32BigEndian(writer, 0); // mEditCount
            foreach (var packet in packets)
            {
                writer.Write(packet);
            }
        }

        private static void AppendVariableLengthQuantity(List<byte> destination, int value)
        {
            // 7 payload bits per byte, most-significant group first, high bit = "more bytes follow".
            var groups = new List<byte> { (byte)(value & 0x7F) };
            value >>= 7;
            while (value > 0)
            {
                groups.Add((byte)(0x80 | (value & 0x7F)));
                value >>= 7;
            }

            for (var i = groups.Count - 1; i >= 0; i--)
            {
                destination.Add(groups[i]);
            }
        }

        private static void WriteUInt16BigEndian(BinaryWriter writer, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteInt32BigEndian(BinaryWriter writer, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteInt64BigEndian(BinaryWriter writer, long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteDoubleBigEndian(BinaryWriter writer, double value)
        {
            // BinaryPrimitives has no WriteDoubleBigEndian overload; every platform this project
            // targets is little-endian (same assumption BinaryPrimitives' own big-endian helpers make
            // for every other type here), so this always reverses rather than branching on
            // BitConverter.IsLittleEndian for a case that can't occur on a supported platform.
            Span<byte> bytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(bytes, value);
            bytes.Reverse();

            writer.Write(bytes);
        }
    }
}
