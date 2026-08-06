using System.Text;

namespace EggEncoder.Codecs.Wma
{
    // Writes a minimal but valid ASF file containing a single WMAv2 audio stream: Header Object
    // (File Properties + Stream Properties) + Data Object + fixed-size Data Packets, one WMA frame
    // payload per packet. This is the mirror of AsfContainerReader -- every field it reads here is
    // populated with a real, correct value; every field AsfContainerReader ignores is still filled
    // in per the ASF spec (for interop with real players/decoders) but doesn't need to round-trip
    // through anything in this codebase.
    internal static class AsfContainerWriter
    {
        private const ushort WmaV2FormatTag = 0x0161;
        private const int PacketHeaderOverhead = 12; // lengthFlags(1) + propertyFlags(1) + sendTime(4) + duration(2) + payloadFlags(1) + streamNumber(1) + payloadLength(2)

        public static void Write(string filePath, int channels, int sampleRate, long totalSamples, IReadOnlyList<byte[]> framePayloads)
        {
            var packetSize = PacketHeaderOverhead;
            foreach (var payload in framePayloads)
            {
                packetSize = Math.Max(packetSize, PacketHeaderOverhead + payload.Length);
            }

            var extraData = new byte[] { 0, 0, 0, 0, 0x01, 0x00 }; // flags2 = 0x0001: VLC exponents, no bit reservoir, fixed block length
            var durationIn100Ns = sampleRate > 0 ? (long)(totalSamples * 10_000_000.0 / sampleRate) : 0;
            var averageBytesPerSecond = durationIn100Ns > 0
                ? (uint)(((long)framePayloads.Count * packetSize * 10_000_000L) / durationIn100Ns)
                : 0u;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            var fileId = Guid.NewGuid();

            var streamPropertiesContent = BuildStreamPropertiesContent(channels, sampleRate, averageBytesPerSecond, packetSize, extraData);
            var streamPropertiesObject = WrapObject(AsfGuids.StreamPropertiesObject, streamPropertiesContent);

            var filePropertiesContent = BuildFilePropertiesContent(fileId, framePayloads.Count, packetSize, durationIn100Ns, averageBytesPerSecond);
            var filePropertiesObject = WrapObject(AsfGuids.FilePropertiesObject, filePropertiesContent);

            const int headerObjectCount = 2;
            var headerObjectContent = new byte[4 + 2 + filePropertiesObject.Length + streamPropertiesObject.Length];
            BitConverter.GetBytes(headerObjectCount).CopyTo(headerObjectContent, 0);
            // bytes [4,6) are the 2 reserved bytes AsfContainerReader skips; left zero.
            filePropertiesObject.CopyTo(headerObjectContent, 6);
            streamPropertiesObject.CopyTo(headerObjectContent, 6 + filePropertiesObject.Length);

            var headerObject = WrapObject(AsfGuids.HeaderObject, headerObjectContent);

            writer.Write(headerObject);

            writer.Write(AsfGuids.DataObject.ToByteArray());
            var dataObjectSize = (ulong)(24 + 16 + 8 + 2 + ((long)framePayloads.Count * packetSize));
            writer.Write(dataObjectSize);
            writer.Write(fileId.ToByteArray());
            writer.Write((ulong)framePayloads.Count);
            writer.Write((ushort)0); // reserved

            foreach (var payload in framePayloads)
            {
                WritePacket(writer, payload, packetSize);
            }
        }

        private static byte[] BuildFilePropertiesContent(Guid fileId, int packetCount, int packetSize, long durationIn100Ns, uint averageBytesPerSecond)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write(fileId.ToByteArray());
            writer.Write((ulong)0); // FileSize -- unknown until the whole file is written; not consumed by AsfContainerReader
            writer.Write((ulong)0); // CreationDate
            writer.Write((ulong)packetCount);
            writer.Write((ulong)durationIn100Ns);
            writer.Write((ulong)durationIn100Ns);
            writer.Write((ulong)0); // Preroll
            writer.Write((uint)0x2); // Flags: seekable
            writer.Write((uint)packetSize); // MinimumDataPacketSize
            writer.Write((uint)packetSize); // MaximumDataPacketSize
            writer.Write(averageBytesPerSecond * 8); // MaximumBitrate (bits/sec)

            return buffer.ToArray();
        }

        private static byte[] BuildStreamPropertiesContent(int channels, int sampleRate, uint averageBytesPerSecond, int packetSize, byte[] extraData)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write(AsfGuids.AudioStreamType.ToByteArray());
            writer.Write(AsfGuids.NoErrorCorrection.ToByteArray());
            writer.Write((ulong)0); // TimeOffset

            var typeSpecificData = BuildWaveFormatEx(channels, sampleRate, averageBytesPerSecond, packetSize, extraData);

            writer.Write((uint)typeSpecificData.Length);
            writer.Write((uint)0); // ErrorCorrectionDataLength
            writer.Write((ushort)0x0001); // Flags: stream number 1, not encrypted
            writer.Write((uint)0); // reserved
            writer.Write(typeSpecificData);

            return buffer.ToArray();
        }

        private static byte[] BuildWaveFormatEx(int channels, int sampleRate, uint averageBytesPerSecond, int packetSize, byte[] extraData)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write(WmaV2FormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write(averageBytesPerSecond);
            writer.Write((ushort)packetSize); // nBlockAlign: not consumed by AsfContainerReader/WmaDecoder
            writer.Write((ushort)16); // wBitsPerSample
            writer.Write((ushort)extraData.Length);
            writer.Write(extraData);

            return buffer.ToArray();
        }

        private static void WritePacket(BinaryWriter writer, byte[] payload, int packetSize)
        {
            const byte lengthFlags = 0x01; // multiple-payloads-present bit set, every length-type subfield absent
            const byte propertyFlags = 0x00; // media-object-number/offset/replicated-data-length all absent
            const byte payloadFlagsOnePayload = 0x01;
            const byte streamNumber = 0x01;

            var packetStart = writer.BaseStream.Position;

            writer.Write(lengthFlags);
            writer.Write(propertyFlags);
            writer.Write((uint)0); // Packet Send Time -- not consumed by AsfContainerReader
            writer.Write((ushort)0); // Packet Duration
            writer.Write(payloadFlagsOnePayload);
            writer.Write(streamNumber);
            writer.Write((ushort)payload.Length);
            writer.Write(payload);

            var written = (int)(writer.BaseStream.Position - packetStart);
            if (written < packetSize)
            {
                writer.Write(new byte[packetSize - written]);
            }
        }

        private static byte[] WrapObject(Guid objectGuid, byte[] content)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write(objectGuid.ToByteArray());
            writer.Write((ulong)(24 + content.Length));
            writer.Write(content);

            return buffer.ToArray();
        }
    }
}
