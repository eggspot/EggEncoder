using System.Buffers.Binary;

namespace EggEncoder.Codecs.Opus
{
    // Writes an Ogg bitstream (RFC 3533) for a single logical stream. Always writes exactly one
    // packet per page -- a deliberate simplification (real encoders often batch several small
    // packets into one page for lower overhead, and split a packet larger than 65025 bytes across
    // several), valid per spec and more than sufficient here: an Opus packet is at most 1275 bytes,
    // so it always fits one page's 255-segment/65025-byte limit with room to spare, and OpusHead/
    // OpusTags are smaller still. OggPageReader still handles the general multi-page-packet case on
    // read, for robustness against any other encoder's real-world output.
    internal sealed class OggPageWriter
    {
        private readonly Stream _stream;
        private readonly uint _serialNumber;

        private uint _nextSequenceNumber;
        private bool _wroteFirstPage;

        public OggPageWriter(Stream stream, uint serialNumber)
        {
            _stream = stream;
            _serialNumber = serialNumber;
        }

        public void WritePacket(byte[] packet, long granulePosition, bool isEndOfStream)
        {
            var isBeginningOfStream = !_wroteFirstPage;
            _wroteFirstPage = true;

            var header = new byte[27];
            "OggS"u8.CopyTo(header);
            header[4] = 0; // stream_structure_version

            byte headerTypeFlag = 0;
            if (isBeginningOfStream)
            {
                headerTypeFlag |= 0x02;
            }

            if (isEndOfStream)
            {
                headerTypeFlag |= 0x04;
            }

            header[5] = headerTypeFlag;

            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6, 8), granulePosition);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), _serialNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), _nextSequenceNumber);
            _nextSequenceNumber++;

            // bytes 22-25 (the CRC field) are left zero for now; filled in once computed below.

            var lacingValues = BuildLacingValues(packet.Length);
            header[26] = (byte)lacingValues.Count;

            var lacingBytes = new byte[lacingValues.Count];
            for (var i = 0; i < lacingValues.Count; i++)
            {
                lacingBytes[i] = (byte)lacingValues[i];
            }

            var crc = OggCrc32.Compute(Concat(header, lacingBytes, packet));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), crc);

            _stream.Write(header);
            _stream.Write(lacingBytes);
            _stream.Write(packet);
        }

        // A packet of exactly N*255 bytes (N >= 1) needs a final zero-length lacing entry to
        // terminate it, since a lacing value of 255 always means "more data follows" -- without
        // that trailing zero, a reader can't tell a 255-byte packet apart from one that's still
        // continuing. A zero-length packet is the single edge case already handled correctly by
        // this same loop: it enters with remaining=0, skips the while body entirely, and the
        // guaranteed final Add(0) below is its own (and only) lacing value.
        private static List<int> BuildLacingValues(int packetLength)
        {
            var lacingValues = new List<int>();
            var remaining = packetLength;

            while (remaining >= 255)
            {
                lacingValues.Add(255);
                remaining -= 255;
            }

            lacingValues.Add(remaining);
            return lacingValues;
        }

        private static byte[] Concat(byte[] a, byte[] b, byte[] c)
        {
            var result = new byte[a.Length + b.Length + c.Length];
            a.CopyTo(result, 0);
            b.CopyTo(result, a.Length);
            c.CopyTo(result, a.Length + b.Length);
            return result;
        }
    }
}
