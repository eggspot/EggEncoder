using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.MpegTs
{
    // A test-only MPEG-TS reader, just enough to check MpegTsMuxer's packet mode field by field:
    // TS headers, adaptation fields (stuffing must be all 0xFF), PAT/PMT and PES reassembly. Not a
    // general-purpose demuxer (that's live ingest plan item 8): it asserts this muxer's own
    // conventions. ReadPat/ReadPmt read a section from one packet; a test with a PMT spanning
    // two packets only counts them.
    internal static class MpegTsTestReader
    {
        public const int PacketSize = 188;

        public static List<TsPacket> ReadPackets(byte[] ts)
        {
            (ts.Length % PacketSize).Should().Be(0, "a segment is a whole number of TS packets");

            var packets = new List<TsPacket>();
            for (var offset = 0; offset < ts.Length; offset += PacketSize)
            {
                var packet = ts.AsSpan(offset, PacketSize);
                packet[0].Should().Be(0x47, "every TS packet starts with the sync byte");
                var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                var adaptationFieldControl = (packet[3] >> 4) & 0x3;
                adaptationFieldControl.Should().NotBe(0, "'00' is reserved");

                var position = 4;
                int? adaptationFieldLength = null;
                var randomAccess = false;
                long? pcr = null;
                if ((adaptationFieldControl & 0x2) != 0)
                {
                    adaptationFieldLength = packet[4];
                    var maxLength = adaptationFieldControl == 0x2 ? 183 : 182;
                    adaptationFieldLength.Value.Should().BeLessThanOrEqualTo(maxLength);
                    if (adaptationFieldControl == 0x2)
                    {
                        adaptationFieldLength.Value.Should().Be(183, "an adaptation-field-only packet fills the packet");
                    }

                    var fieldEnd = 5 + adaptationFieldLength.Value;
                    var fieldPosition = 5;
                    if (adaptationFieldLength > 0)
                    {
                        var flags = packet[5];
                        (flags & 0xAF).Should().Be(0, "only random_access_indicator and PCR_flag are ever set");
                        randomAccess = (flags & 0x40) != 0;
                        fieldPosition = 6;
                        if ((flags & 0x10) != 0)
                        {
                            var pcrBase = ((long)packet[6] << 25) | ((long)packet[7] << 17) | ((long)packet[8] << 9) | ((long)packet[9] << 1) | ((long)packet[10] >> 7);
                            (packet[10] & 0x7E).Should().Be(0x7E, "the 6 reserved bits are ones");
                            var extension = ((packet[10] & 0x1) << 8) | packet[11];
                            pcr = (pcrBase * 300) + extension;
                            fieldPosition = 12;
                        }
                    }

                    packet[fieldPosition..fieldEnd].ToArray().Should().NotContain(b => b != 0xFF, "adaptation-field stuffing bytes are 0xFF");
                    position = fieldEnd;
                }

                var payload = (adaptationFieldControl & 0x1) != 0 ? packet[position..].ToArray() : [];
                packets.Add(new TsPacket(
                    packets.Count,
                    pid,
                    (packet[1] & 0x40) != 0,
                    adaptationFieldControl,
                    packet[3] & 0x0F,
                    adaptationFieldLength,
                    randomAccess,
                    pcr,
                    payload));
            }

            return packets;
        }

        // Returns the PMT PID the (single-program) PAT names.
        public static int ReadPat(TsPacket packet)
        {
            packet.Pid.Should().Be(0);
            var section = ReadSection(packet, tableId: 0x00);
            return ((section[10] & 0x1F) << 8) | section[11];
        }

        public static (int PcrPid, List<(byte StreamType, int Pid)> Streams) ReadPmt(TsPacket packet)
        {
            var section = ReadSection(packet, tableId: 0x02);
            var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
            var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
            var streams = new List<(byte, int)>();
            for (var position = 12 + programInfoLength; position < section.Length - 4;)
            {
                var esInfoLength = ((section[position + 3] & 0x0F) << 8) | section[position + 4];
                streams.Add((section[position], ((section[position + 1] & 0x1F) << 8) | section[position + 2]));
                position += 5 + esInfoLength;
            }

            return (pcrPid, streams);
        }

        // Reassembles the PES packets of one PID. A non-zero PES_packet_length must match the
        // reassembled bytes exactly: no bytes may follow a PES in its last TS packet.
        public static List<Pes> ReadPes(IReadOnlyList<TsPacket> packets, int pid)
        {
            var result = new List<Pes>();
            List<byte>? buffer = null;
            TsPacket? first = null;
            foreach (var packet in packets.Where(p => p.Pid == pid && p.Payload.Length > 0))
            {
                if (packet.PayloadUnitStart)
                {
                    if (buffer is not null)
                    {
                        result.Add(ParsePes(buffer.ToArray(), first!));
                    }

                    buffer = [];
                    first = packet;
                }

                buffer.Should().NotBeNull("a PID's first payload packet starts a PES");
                buffer!.AddRange(packet.Payload);
            }

            if (buffer is not null)
            {
                result.Add(ParsePes(buffer.ToArray(), first!));
            }

            return result;
        }

        public static long DecodeTimestamp(ReadOnlySpan<byte> bytes, int expectedPrefix)
        {
            (bytes[0] >> 4).Should().Be(expectedPrefix);
            (bytes[0] & 1).Should().Be(1, "marker bit");
            (bytes[2] & 1).Should().Be(1, "marker bit");
            (bytes[4] & 1).Should().Be(1, "marker bit");
            return ((long)((bytes[0] >> 1) & 0x7) << 30) | ((long)((((bytes[1] << 8) | bytes[2]) >> 1) & 0x7FFF) << 15) | (long)((((bytes[3] << 8) | bytes[4]) >> 1) & 0x7FFF);
        }

        private static byte[] ReadSection(TsPacket packet, int tableId)
        {
            packet.PayloadUnitStart.Should().BeTrue();
            packet.Payload[0].Should().Be(0, "pointer_field");
            packet.Payload[1].Should().Be((byte)tableId);
            var sectionLength = ((packet.Payload[2] & 0x0F) << 8) | packet.Payload[3];
            var section = packet.Payload.AsSpan(1, 3 + sectionLength).ToArray();
            packet.Payload.Skip(4 + sectionLength).Should().NotContain(b => b != 0xFF, "0xFF fills a packet after a section");
            return section;
        }

        private static Pes ParsePes(byte[] bytes, TsPacket first)
        {
            bytes.AsSpan(0, 3).ToArray().Should().Equal(0x00, 0x00, 0x01);
            var pesPacketLength = (bytes[4] << 8) | bytes[5];
            if (pesPacketLength != 0)
            {
                bytes.Length.Should().Be(6 + pesPacketLength, "PES_packet_length counts every byte after it, and nothing follows the PES");
            }

            (bytes[6] & 0xC0).Should().Be(0x80, "the '10' marker bits");
            var ptsDtsFlags = bytes[7] >> 6;
            var headerDataLength = bytes[8];
            long pts;
            long? dts = null;
            if (ptsDtsFlags == 0x3)
            {
                headerDataLength.Should().Be(10);
                pts = DecodeTimestamp(bytes.AsSpan(9, 5), 0x3);
                dts = DecodeTimestamp(bytes.AsSpan(14, 5), 0x1);
            }
            else
            {
                ptsDtsFlags.Should().Be(0x2);
                headerDataLength.Should().Be(5);
                pts = DecodeTimestamp(bytes.AsSpan(9, 5), 0x2);
            }

            return new Pes(first, bytes[3], pesPacketLength, (bytes[6] & 0x04) != 0, pts, dts, bytes[(9 + headerDataLength)..]);
        }

        internal sealed record TsPacket(
            int Index,
            int Pid,
            bool PayloadUnitStart,
            int AdaptationFieldControl,
            int ContinuityCounter,
            int? AdaptationFieldLength,
            bool RandomAccess,
            long? Pcr,
            byte[] Payload);

        internal sealed record Pes(TsPacket First, byte StreamId, int PesPacketLength, bool DataAlignment, long Pts, long? Dts, byte[] Es)
        {
            public long DecodeTime => Dts ?? Pts;
        }
    }
}
