using System.Buffers;
using System.Buffers.Binary;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    // RtmpChunkWriter and RtmpChunkReader together: the writer's output must read back exactly, and the
    // reader must also accept what other writers legitimately produce (hand-built chunks below).
    public class RtmpChunkStreamTest
    {
        private static byte[] Payload(int length, int seed = 1)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static RtmpMessage Message(RtmpMessageType type, uint streamId, uint timestamp, int length, int seed = 1) =>
            new(type, streamId, timestamp, Payload(length, seed));

        internal static List<RtmpMessage> Read(byte[] bytes, int feedSize = int.MaxValue, RtmpChunkReaderOptions? options = null)
        {
            var messages = new List<RtmpMessage>();
            var reader = new RtmpChunkReader(messages.Add, options);
            for (var offset = 0; offset < bytes.Length; offset += Math.Min(feedSize, bytes.Length - offset))
            {
                reader.Feed(bytes.AsSpan(offset, Math.Min(feedSize, bytes.Length - offset)));
            }

            return messages;
        }

        private static void ShouldMatch(IReadOnlyList<RtmpMessage> actual, IReadOnlyList<RtmpMessage> expected)
        {
            actual.Should().HaveCount(expected.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                actual[i].Type.Should().Be(expected[i].Type, $"message {i}");
                actual[i].StreamId.Should().Be(expected[i].StreamId, $"message {i}");
                actual[i].Timestamp.Should().Be(expected[i].Timestamp, $"message {i}");
                actual[i].Payload.ToArray().Should().Equal(expected[i].Payload.ToArray(), $"message {i}");
            }
        }

        // Covers every header format, every basic-header width, extended timestamps (absolute, delta,
        // repeated on continuation chunks), zero-length messages and wrapping timestamps.
        private static (byte[] Bytes, List<RtmpMessage> Messages) KitchenSink(int chunkSize)
        {
            var writer = new RtmpChunkWriter();
            var output = new ArrayBufferWriter<byte>();
            var expected = new List<RtmpMessage>();
            if (chunkSize != RtmpChunkReader.DefaultChunkSize)
            {
                writer.WriteSetChunkSize(output, chunkSize);
                expected.Add(RtmpProtocolControl.CreateSetChunkSize(chunkSize));
            }

            void Add(int chunkStreamId, RtmpMessage message)
            {
                writer.Write(output, chunkStreamId, message);
                expected.Add(message);
            }

            Add(3, RtmpCommand.Connect(1, "live", "rtmp://localhost/live").ToMessage());
            Add(4, Message(RtmpMessageType.Video, 1, 0, 300));
            Add(4, Message(RtmpMessageType.Video, 1, 33, 300, 2));
            Add(4, Message(RtmpMessageType.Video, 1, 66, 300, 3));
            Add(4, Message(RtmpMessageType.Video, 1, 99, 5000, 4));
            Add(64, Message(RtmpMessageType.Audio, 1, 10, 7));
            Add(319, Message(RtmpMessageType.Audio, 1, 20, 0));
            Add(320, Message(RtmpMessageType.DataAmf0, 1, 30, 200));
            Add(RtmpChunkWriter.MaxChunkStreamId, Message(RtmpMessageType.Audio, 2, 40, 129));
            Add(63, Message(RtmpMessageType.Video, 1, 0x01000000, 400, 5));
            Add(63, Message(RtmpMessageType.Video, 1, 0x01000000 + 0x01000000, 400, 6));
            Add(63, Message(RtmpMessageType.Video, 1, 0x01000000 + (2 * 0x01000000), 400, 7));
            Add(5, Message(RtmpMessageType.Audio, 1, 0xFFFFFFF0, 10));
            Add(5, Message(RtmpMessageType.Audio, 1, 0x00000010, 10, 8));
            Add(5, Message(RtmpMessageType.Audio, 1, 0x00000005, 10, 9));
            return (output.WrittenSpan.ToArray(), expected);
        }

        [Theory]
        [InlineData(128)]
        [InlineData(4096)]
        [InlineData(65536)]
        [InlineData(1)]
        public void WriterThenReader_KitchenSink_Should_Round_Trip_Every_Message(int chunkSize)
        {
            var (bytes, expected) = KitchenSink(chunkSize);

            ShouldMatch(Read(bytes), expected);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(17)]
        public void Reader_FedInSmallPieces_Should_Match_Reading_All_At_Once(int feedSize)
        {
            var (bytes, expected) = KitchenSink(128);

            ShouldMatch(Read(bytes, feedSize), expected);
        }

        [Fact]
        public void Reader_MessageLargerThanFeedSlice_Should_Round_Trip()
        {
            var writer = new RtmpChunkWriter();
            var output = new ArrayBufferWriter<byte>();
            writer.WriteSetChunkSize(output, 65536);
            var message = Message(RtmpMessageType.Video, 1, 0, 300_000);
            writer.Write(output, 6, message);

            var messages = Read(output.WrittenSpan.ToArray());

            ShouldMatch(messages.Skip(1).ToList(), [message]);
        }

        [Fact]
        public void Writer_HeaderFormats_Should_Be_The_Most_Compact_The_Previous_Message_Allows()
        {
            var writer = new RtmpChunkWriter();

            int FormatOf(RtmpMessage message)
            {
                var output = new ArrayBufferWriter<byte>();
                writer.Write(output, 4, message);
                return output.WrittenSpan[0] >> 6;
            }

            FormatOf(Message(RtmpMessageType.Audio, 1, 100, 10)).Should().Be(0, "first message on the chunk stream");
            FormatOf(Message(RtmpMessageType.Audio, 1, 120, 10)).Should().Be(2, "only the delta is new");
            FormatOf(Message(RtmpMessageType.Audio, 1, 140, 10)).Should().Be(3, "the delta repeats");
            FormatOf(Message(RtmpMessageType.Audio, 1, 160, 10)).Should().Be(3);
            FormatOf(Message(RtmpMessageType.Audio, 1, 170, 10)).Should().Be(2, "a new delta");
            FormatOf(Message(RtmpMessageType.Audio, 1, 180, 11)).Should().Be(1, "a new length");
            FormatOf(Message(RtmpMessageType.Video, 1, 190, 11)).Should().Be(1, "a new type");
            FormatOf(Message(RtmpMessageType.Video, 1, 200, 11)).Should().Be(3, "type 1 set the delta to 10");
            FormatOf(Message(RtmpMessageType.Video, 2, 210, 11)).Should().Be(0, "a new message stream id");
            FormatOf(Message(RtmpMessageType.Video, 2, 210, 11)).Should().Be(2, "type 0 sets no delta, so a zero delta is still new");
            FormatOf(Message(RtmpMessageType.Video, 2, 100, 11)).Should().Be(0, "the timestamp went backwards");
        }

        [Theory]
        [InlineData(2, new byte[] { 0x02 })]
        [InlineData(63, new byte[] { 0x3F })]
        [InlineData(64, new byte[] { 0x00, 0x00 })]
        [InlineData(319, new byte[] { 0x00, 0xFF })]
        [InlineData(320, new byte[] { 0x01, 0x00, 0x01 })]
        [InlineData(65599, new byte[] { 0x01, 0xFF, 0xFF })]
        public void Writer_BasicHeader_Should_Use_One_Two_Or_Three_Bytes(int chunkStreamId, byte[] expected)
        {
            var output = new ArrayBufferWriter<byte>();
            new RtmpChunkWriter().Write(output, chunkStreamId, Message(RtmpMessageType.Audio, 0, 0, 1));

            output.WrittenSpan[..expected.Length].ToArray().Should().Equal(expected);
            output.WrittenCount.Should().Be(expected.Length + 11 + 1);
        }

        [Fact]
        public void Writer_ExtendedTimestamp_Should_Be_Repeated_On_Every_Continuation_Chunk()
        {
            var output = new ArrayBufferWriter<byte>();
            new RtmpChunkWriter().Write(output, 3, Message(RtmpMessageType.Video, 1, 0x12345678, 200));
            var bytes = output.WrittenSpan.ToArray();

            bytes.AsSpan(1, 3).ToArray().Should().Equal(0xFF, 0xFF, 0xFF);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)).Should().Be(0x12345678u);
            var continuation = 1 + 11 + 4 + 128;
            bytes[continuation].Should().Be(0xC3);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(continuation + 1)).Should().Be(0x12345678u);
            bytes.Should().HaveCount(continuation + 1 + 4 + 72);
        }

        [Fact]
        public void Writer_TimestampJustBelowMarker_Should_Not_Use_Extended_Field()
        {
            var output = new ArrayBufferWriter<byte>();
            new RtmpChunkWriter().Write(output, 3, Message(RtmpMessageType.Video, 1, 0xFFFFFE, 1));

            output.WrittenCount.Should().Be(1 + 11 + 1);
            ShouldMatch(Read(output.WrittenSpan.ToArray()), [Message(RtmpMessageType.Video, 1, 0xFFFFFE, 1)]);
        }

        [Fact]
        public void Writer_TimestampEqualToMarker_Should_Use_Extended_Field()
        {
            var output = new ArrayBufferWriter<byte>();
            var message = Message(RtmpMessageType.Video, 1, 0xFFFFFF, 1);
            new RtmpChunkWriter().Write(output, 3, message);

            output.WrittenCount.Should().Be(1 + 11 + 4 + 1);
            ShouldMatch(Read(output.WrittenSpan.ToArray()), [message]);
        }

        [Fact]
        public void Writer_Validation_Should_Reject_Bad_Arguments()
        {
            var writer = new RtmpChunkWriter();
            var output = new ArrayBufferWriter<byte>();
            var message = Message(RtmpMessageType.Audio, 0, 0, 1);

            writer.Invoking(w => w.Write(null!, 3, message)).Should().Throw<ArgumentNullException>();
            writer.Invoking(w => w.Write(output, 1, message)).Should().Throw<ArgumentOutOfRangeException>();
            writer.Invoking(w => w.Write(output, RtmpChunkWriter.MaxChunkStreamId + 1, message)).Should().Throw<ArgumentOutOfRangeException>();
            writer.Invoking(w => w.Write(output, 3, new RtmpMessage(RtmpMessageType.Video, 1, 0, new byte[RtmpMessage.MaxPayloadLength + 1])))
                .Should().Throw<ArgumentException>().WithParameterName("message");
            writer.Invoking(w => w.WriteSetChunkSize(null!, 128)).Should().Throw<ArgumentNullException>();
            writer.Invoking(w => w.WriteSetChunkSize(output, 0)).Should().Throw<ArgumentOutOfRangeException>();
            writer.Invoking(w => w.WriteSetChunkSize(output, RtmpMessage.MaxPayloadLength + 1)).Should().Throw<ArgumentOutOfRangeException>();
            output.WrittenCount.Should().Be(0);
            writer.ChunkSize.Should().Be(RtmpChunkReader.DefaultChunkSize);
        }

        [Fact]
        public void Writer_MaxPayloadLength_Should_Round_Trip()
        {
            var writer = new RtmpChunkWriter();
            var output = new ArrayBufferWriter<byte>();
            writer.WriteSetChunkSize(output, 65536);
            var message = Message(RtmpMessageType.Video, 1, 0, RtmpMessage.MaxPayloadLength);
            writer.Write(output, 6, message);

            var messages = Read(output.WrittenSpan.ToArray(), options: new RtmpChunkReaderOptions { MaxMessageLength = RtmpMessage.MaxPayloadLength, MaxBufferedBytes = 32 * 1024 * 1024 });

            ShouldMatch(messages.Skip(1).ToList(), [message]);
        }

        // --- Hand-built chunks: what other writers legitimately send. ---

        private static byte[] Header(int format, int chunkStreamId, uint timestamp = 0, int length = 0, byte type = 0, uint streamId = 0)
        {
            var header = new List<byte> { (byte)((format << 6) | chunkStreamId) };
            if (format <= 2)
            {
                header.AddRange([(byte)(timestamp >> 16), (byte)(timestamp >> 8), (byte)timestamp]);
            }

            if (format <= 1)
            {
                header.AddRange([(byte)(length >> 16), (byte)(length >> 8), (byte)length, type]);
            }

            if (format == 0)
            {
                header.AddRange([(byte)streamId, (byte)(streamId >> 8), (byte)(streamId >> 16), (byte)(streamId >> 24)]);
            }

            return [.. header];
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] UInt32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }

        [Fact]
        public void Reader_MessagesInterleavedAcrossChunkStreams_Should_Reassemble_Each()
        {
            var a = Payload(300, 1);
            var b = Payload(200, 2);
            var bytes = Concat(
                Header(0, 4, 1000, a.Length, 9, 1), a[..128],
                Header(0, 6, 2000, b.Length, 8, 1), b[..128],
                Header(3, 4), a[128..256],
                Header(3, 6), b[128..],
                Header(3, 4), a[256..]);

            var messages = Read(bytes);

            ShouldMatch(messages,
            [
                new RtmpMessage(RtmpMessageType.Audio, 1, 2000, b),
                new RtmpMessage(RtmpMessageType.Video, 1, 1000, a)
            ]);
        }

        [Fact]
        public void Reader_Type3HeaderStartingAMessage_Should_Repeat_The_Previous_Delta()
        {
            var bytes = Concat(
                Header(0, 4, 1000, 2, 8, 1), [1, 2],
                Header(3, 4), [3, 4],
                Header(2, 4, 20), [5, 6],
                Header(3, 4), [7, 8],
                Header(1, 4, 5, 1, 9), [9],
                Header(3, 4), [10]);

            // Directly after a type 0 header, its timestamp is the delta (the specification's own
            // type 3 description), so the second message is at 2000.
            Read(bytes).Select(m => (m.Timestamp, m.Type)).Should().Equal(
                (1000u, RtmpMessageType.Audio),
                (2000u, RtmpMessageType.Audio),
                (2020u, RtmpMessageType.Audio),
                (2040u, RtmpMessageType.Audio),
                (2045u, RtmpMessageType.Video),
                (2050u, RtmpMessageType.Video));
        }

        [Fact]
        public void Reader_Type3AfterExtendedType0_Should_Read_The_Extended_Field_And_Use_It_As_The_Delta()
        {
            var bytes = Concat(
                Header(0, 4, 0xFFFFFF, 1, 9, 1), UInt32(0x01000000), [1],
                Header(3, 4), UInt32(0x01000000), [2]);

            Read(bytes).Select(m => m.Timestamp).Should().Equal(0x01000000u, 0x02000000u);
        }

        [Fact]
        public void Reader_ContinuationWithoutExtendedTimestamp_Should_Still_Read_The_Payload()
        {
            // The specification says continuation chunks repeat the extended timestamp; some older
            // publishers leave it out. Both must read the same.
            var payload = Payload(200, 3);
            var withRepeat = Concat(Header(0, 3, 0xFFFFFF, payload.Length, 9, 1), UInt32(0x01020304), payload[..128], Header(3, 3), UInt32(0x01020304), payload[128..]);
            var withoutRepeat = Concat(Header(0, 3, 0xFFFFFF, payload.Length, 9, 1), UInt32(0x01020304), payload[..128], Header(3, 3), payload[128..]);

            var expected = new RtmpMessage(RtmpMessageType.Video, 1, 0x01020304, payload);
            ShouldMatch(Read(withRepeat), [expected]);
            ShouldMatch(Read(withoutRepeat), [expected]);
            ShouldMatch(Read(withoutRepeat, feedSize: 1), [expected]);
        }

        [Fact]
        public void Reader_PeerThatOmitsTheRepeat_Should_Be_Read_That_Way_For_Every_Later_Continuation()
        {
            // The first continuation shows this peer omits the repeat; a later continuation whose
            // payload happens to start with the extended value's bytes is then still payload.
            var first = Payload(200, 5);
            var second = Payload(200, 6);
            UInt32(0x01020304).CopyTo(second, 128);
            var bytes = Concat(
                Header(0, 3, 0xFFFFFF, first.Length, 9, 1), UInt32(0x01020304), first[..128], Header(3, 3), first[128..],
                Header(0, 3, 0xFFFFFF, second.Length, 9, 1), UInt32(0x01020304), second[..128], Header(3, 3), second[128..]);

            ShouldMatch(Read(bytes),
            [
                new RtmpMessage(RtmpMessageType.Video, 1, 0x01020304, first),
                new RtmpMessage(RtmpMessageType.Video, 1, 0x01020304, second)
            ]);
        }

        [Fact]
        public void Reader_PeerThatRepeats_Should_Consume_The_Field_On_Every_Later_Continuation()
        {
            // Once the peer is known to repeat the field, it is consumed even if its value differs.
            var first = Payload(200, 7);
            var second = Payload(200, 8);
            var bytes = Concat(
                Header(0, 3, 0xFFFFFF, first.Length, 9, 1), UInt32(0x01020304), first[..128], Header(3, 3), UInt32(0x01020304), first[128..],
                Header(0, 3, 0xFFFFFF, second.Length, 9, 1), UInt32(0x01020305), second[..128], Header(3, 3), UInt32(0x0BADF00D), second[128..]);

            ShouldMatch(Read(bytes, feedSize: 1),
            [
                new RtmpMessage(RtmpMessageType.Video, 1, 0x01020304, first),
                new RtmpMessage(RtmpMessageType.Video, 1, 0x01020305, second)
            ]);
        }

        [Fact]
        public void Reader_ExtendedDelta_Should_Be_Added_To_The_Previous_Timestamp()
        {
            var bytes = Concat(
                Header(0, 3, 100, 1, 8, 1), [1],
                Header(2, 3, 0xFFFFFF), UInt32(0x01000000), [2],
                Header(3, 3), UInt32(0x01000000), [3]);

            Read(bytes).Select(m => m.Timestamp).Should().Equal(100u, 100u + 0x01000000, 100u + 0x02000000);
        }

        [Fact]
        public void Reader_ZeroLengthMessage_Should_Be_Reported_With_An_Empty_Payload()
        {
            var bytes = Concat(Header(0, 3, 7, 0, 20, 0), Header(3, 3));

            var messages = Read(bytes);

            messages.Select(m => m.Timestamp).Should().Equal(7u, 14u);
            messages.Should().OnlyContain(m => m.Payload.IsEmpty);
        }

        [Fact]
        public void Reader_SetChunkSize_Should_Apply_From_The_Next_Chunk_And_Still_Be_Reported()
        {
            var payload = Payload(300, 4);
            var bytes = Concat(
                Header(0, 2, 0, 4, 1, 0), UInt32(1000),
                Header(0, 4, 0, payload.Length, 9, 1), payload);
            var messages = new List<RtmpMessage>();
            var reader = new RtmpChunkReader(messages.Add);

            reader.Feed(bytes);

            reader.ChunkSize.Should().Be(1000);
            messages.Select(m => m.Type).Should().Equal(RtmpMessageType.SetChunkSize, RtmpMessageType.Video);
            messages[1].Payload.ToArray().Should().Equal(payload);
        }

        [Fact]
        public void Reader_Abort_Should_Drop_The_Partial_Message_And_Free_Its_Bytes()
        {
            // Without the abort freeing its 128 bytes, the second 300-byte message would exceed the limit.
            var bytes = Concat(
                Header(0, 4, 0, 300, 9, 1), Payload(128),
                Header(0, 2, 0, 4, 2, 0), UInt32(4),
                Header(0, 4, 50, 300, 9, 1), Payload(128), Header(3, 4), Payload(128, 2), Header(3, 4), Payload(44, 3),
                Header(3, 2), UInt32(4),
                Header(3, 2), UInt32(9));

            var messages = Read(bytes, options: new RtmpChunkReaderOptions { MaxBufferedBytes = 304 });

            // The last two aborts name a chunk stream with nothing in progress and one never used.
            messages.Select(m => (m.Type, m.Timestamp)).Should().Equal(
                (RtmpMessageType.Abort, 0u), (RtmpMessageType.Video, 50u), (RtmpMessageType.Abort, 0u), (RtmpMessageType.Abort, 0u));
            messages[1].Payload.Length.Should().Be(300);
        }

        [Fact]
        public void Reader_AbortThenNewMessage_Should_Not_Count_Aborted_Bytes_Against_The_Limit()
        {
            // 256 bytes of the aborted message plus the 4-byte Abort itself fit; 128 more would not.
            var options = new RtmpChunkReaderOptions { MaxBufferedBytes = 260 };
            var bytes = Concat(
                Header(0, 4, 0, 300, 9, 1), Payload(128), Header(3, 4), Payload(128),
                Header(0, 2, 0, 4, 2, 0), UInt32(4),
                Header(0, 6, 0, 250, 9, 1), Payload(128), Header(3, 6), Payload(122));

            Read(bytes, options: options).Select(m => m.Type).Should().Equal(RtmpMessageType.Abort, RtmpMessageType.Video);
        }

        [Fact]
        public void Reader_Acknowledgement_Should_Be_Due_Once_Per_Window()
        {
            var messages = new List<RtmpMessage>();
            var reader = new RtmpChunkReader(messages.Add);

            reader.TryTakeAcknowledgement(out var sequence).Should().BeFalse("no window set yet");
            sequence.Should().Be(0);
            reader.Feed(Concat(Header(0, 2, 0, 4, 5, 0), UInt32(100)));
            reader.AcknowledgementWindowSize.Should().Be(100u);
            reader.TryTakeAcknowledgement(out _).Should().BeFalse("16 bytes so far");

            reader.Feed(Concat(Header(0, 4, 0, 90, 9, 1), Payload(90)));
            reader.BytesReceived.Should().Be(16 + 12 + 90);
            reader.TryTakeAcknowledgement(out sequence).Should().BeTrue();
            sequence.Should().Be(118u);
            reader.TryTakeAcknowledgement(out _).Should().BeFalse("the window restarted");
        }

        // --- Bounds and malformed input: always a typed RtmpProtocolException. ---

        public static TheoryData<string, byte[], int> MalformedStreams => new()
        {
            { "never started (type 1)", Header(1, 4, 0, 1, 9), 1 },
            { "never started (type 2)", Header(2, 4, 0), 1 },
            { "never started (type 3)", Header(3, 4), 1 },
            { "message too long", Header(0, 4, 0, 1001, 9, 1), 1 },
            { "new header mid-message", Concat(Header(0, 4, 0, 200, 9, 1), Payload(128), Header(0, 4, 0, 1, 9, 1), [0]), 1 },
            { "type 1 mid-message", Concat(Header(0, 4, 0, 200, 9, 1), Payload(128), Header(1, 4, 0, 1, 9), [0]), 1 },
            { "chunk size over the limit", Concat(Header(0, 2, 0, 4, 1, 0), UInt32(2000)), 1 },
            { "chunk size zero", Concat(Header(0, 2, 0, 4, 1, 0), UInt32(0)), 1 },
            { "chunk size top bit set", Concat(Header(0, 2, 0, 4, 1, 0), UInt32(0x80000010)), 1 },
            { "chunk size payload too short", Concat(Header(0, 2, 0, 3, 1, 0), [0, 0, 1]), 1 },
            { "abort payload too long", Concat(Header(0, 2, 0, 5, 2, 0), [0, 0, 0, 4, 0]), 1 },
            { "abort of chunk stream 1", Concat(Header(0, 2, 0, 4, 2, 0), UInt32(1)), 1 },
            { "window size payload empty", Header(0, 2, 0, 0, 5, 0), 1 },
            { "too many chunk streams", Concat(Header(0, 3, 0, 1, 9, 1), [0], Header(0, 4, 0, 1, 9, 1), [0], Header(0, 5, 0, 1, 9, 1), [0]), 1 },
            { "too many buffered bytes", Concat(Header(0, 3, 0, 500, 9, 1), Payload(128), Header(0, 4, 0, 500, 9, 1), Payload(128), Header(0, 5, 0, 500, 9, 1), Payload(128)), 1 },
            { "fed in one piece", Concat(Header(0, 4, 0, 1001, 9, 1)), int.MaxValue },
        };

        [Theory]
        [MemberData(nameof(MalformedStreams))]
        public void Reader_MalformedOrOverLimit_Should_Throw_Protocol_Exception_Then_Refuse_Further_Input(string name, byte[] bytes, int feedSize)
        {
            var options = new RtmpChunkReaderOptions { MaxMessageLength = 1000, MaxInboundChunkSize = 1000, MaxChunkStreams = 2, MaxBufferedBytes = 300 };
            var reader = new RtmpChunkReader(_ => { }, options);

            var feed = () =>
            {
                for (var offset = 0; offset < bytes.Length; offset += Math.Min(feedSize, bytes.Length - offset))
                {
                    reader.Feed(bytes.AsSpan(offset, Math.Min(feedSize, bytes.Length - offset)));
                }
            };

            feed.Should().Throw<RtmpProtocolException>(name);
            reader.Invoking(r => r.Feed([0x03])).Should().Throw<InvalidOperationException>(name);
        }

        [Fact]
        public void Reader_CallbackThatThrows_Should_Fault_The_Reader()
        {
            var reader = new RtmpChunkReader(_ => throw new InvalidTimeZoneException("boom"));

            reader.Invoking(r => r.Feed(Concat(Header(0, 3, 0, 1, 9, 1), [0]))).Should().Throw<InvalidTimeZoneException>();
            reader.Invoking(r => r.Feed([])).Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Reader_NullCallback_Should_Throw()
        {
            var create = () => new RtmpChunkReader(null!);

            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Reader_ClaimedLengthWithoutData_Should_Not_Allocate_The_Claimed_Length()
        {
            // 61 chunk streams each claiming 8 MiB, one 128-byte chunk sent each: nothing near 488 MiB
            // may be held, and the buffered-bytes limit counts bytes received, not bytes claimed.
            var parts = new List<byte[]>();
            for (var id = 3; id < 64; id++)
            {
                parts.Add(Header(0, id, 0, 8 * 1024 * 1024, 9, 1));
                parts.Add(Payload(128, id));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            Read(Concat([.. parts]), options: new RtmpChunkReaderOptions { MaxChunkStreams = 64, MaxBufferedBytes = 61 * 128 });
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            allocated.Should().BeLessThan(2 * 1024 * 1024);
        }

        [Fact]
        public void Reader_RandomBytes_Should_End_In_A_Protocol_Exception_Or_Nothing()
        {
            var options = new RtmpChunkReaderOptions { MaxMessageLength = 64 * 1024, MaxBufferedBytes = 256 * 1024, MaxChunkStreams = 16 };
            for (var seed = 0; seed < 500; seed++)
            {
                var random = new Random(seed);
                var bytes = new byte[random.Next(1, 4096)];
                random.NextBytes(bytes);
                var reader = new RtmpChunkReader(_ => { }, options);

                try
                {
                    reader.Feed(bytes);
                }
                catch (RtmpProtocolException)
                {
                }
            }
        }

        [Fact]
        public void Reader_MutatedRealCapture_Should_End_In_A_Protocol_Exception_Or_Nothing()
        {
            var bytes = File.ReadAllBytes(RtmpFfmpegCaptureTest.Fixture)[(1 + (2 * RtmpHandshake.PacketLength))..];
            for (var seed = 0; seed < 300; seed++)
            {
                var random = new Random(seed);
                var mutated = (byte[])bytes.Clone();
                for (var i = 0; i < 1 + random.Next(8); i++)
                {
                    mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                }

                var reader = new RtmpChunkReader(message =>
                {
                    // What a server would do with each message: decode commands and data, and
                    // protocol control is already applied by the reader.
                    if (message.Type == RtmpMessageType.CommandAmf0)
                    {
                        RtmpCommand.Decode(message.Payload.Span);
                    }
                    else if (message.Type == RtmpMessageType.DataAmf0)
                    {
                        RtmpDataMessage.Decode(message.Payload.Span);
                    }
                });

                try
                {
                    reader.Feed(mutated);
                }
                catch (RtmpProtocolException)
                {
                }
            }
        }

        // --- An in-memory publish session: both sides built only from these primitives. ---

        [Theory]
        [InlineData(128)]
        [InlineData(4096)]
        [InlineData(65536)]
        public void PublishExchange_OverInMemoryPipe_Should_Reach_Publish_Start(int chunkSize)
        {
            var clientHandshake = RtmpHandshake.CreateClient();
            var serverHandshake = RtmpHandshake.CreateServer();
            serverHandshake.Start();
            clientHandshake.Process(serverHandshake.Process(clientHandshake.Start())).Should().NotBeEmpty();

            var toServer = new ArrayBufferWriter<byte>();
            var toClient = new ArrayBufferWriter<byte>();
            var clientWriter = new RtmpChunkWriter();
            var serverWriter = new RtmpChunkWriter();
            var serverReceived = new List<RtmpCommand>();
            var clientReceived = new List<RtmpCommand>();
            var media = new List<RtmpMessage>();

            var serverReader = new RtmpChunkReader(message =>
            {
                if (message.Type is RtmpMessageType.Audio or RtmpMessageType.Video)
                {
                    media.Add(message);
                    return;
                }

                if (message.Type != RtmpMessageType.CommandAmf0)
                {
                    return;
                }

                var command = RtmpCommand.Decode(message.Payload.Span);
                serverReceived.Add(command);
                var reply = command.Name switch
                {
                    "connect" => RtmpCommand.ConnectResult(command.TransactionId).ToMessage(),
                    "createStream" => RtmpCommand.CreateStreamResult(command.TransactionId, 1).ToMessage(),
                    "publish" => RtmpCommand.OnStatus(RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart, "go").ToMessage(message.StreamId),
                    _ => (RtmpMessage?)null
                };
                if (reply is { } r)
                {
                    serverWriter.Write(toClient, RtmpCommand.ChunkStreamId, r);
                }
            });
            var clientReader = new RtmpChunkReader(message =>
            {
                if (message.Type == RtmpMessageType.CommandAmf0)
                {
                    clientReceived.Add(RtmpCommand.Decode(message.Payload.Span));
                }
            });

            void Pump()
            {
                serverReader.Feed(toServer.WrittenSpan);
                toServer.ResetWrittenCount();
                clientReader.Feed(toClient.WrittenSpan);
                toClient.ResetWrittenCount();
            }

            clientWriter.WriteSetChunkSize(toServer, chunkSize);
            serverWriter.WriteSetChunkSize(toClient, chunkSize);
            clientWriter.Write(toServer, RtmpCommand.ChunkStreamId, RtmpCommand.Connect(1, "live", "rtmp://127.0.0.1/live",
                [new("pad", Amf0Value.FromString(new string('x', 5000)))]).ToMessage());
            Pump();
            clientWriter.Write(toServer, RtmpCommand.ChunkStreamId, RtmpCommand.CreateStream(2).ToMessage());
            Pump();
            var streamId = (uint)clientReceived[^1].GetNumberArgument(0)!.Value;
            clientWriter.Write(toServer, 8, RtmpCommand.Publish("key").ToMessage(streamId));
            var video = Message(RtmpMessageType.Video, streamId, 0, 70_000);
            var audio = Message(RtmpMessageType.Audio, streamId, 0, 300);
            clientWriter.Write(toServer, 6, video);
            clientWriter.Write(toServer, 4, audio);
            Pump();

            serverReceived.Select(c => c.Name).Should().Equal("connect", "createStream", "publish");
            serverReceived[2].GetStringArgument(0).Should().Be("key");
            clientReceived.Select(c => c.Name).Should().Equal("_result", "_result", "onStatus");
            clientReceived[0].StatusCode.Should().Be(RtmpStatusCodes.ConnectSuccess);
            clientReceived[2].StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            ShouldMatch(media, [video, audio]);
        }
    }
}
