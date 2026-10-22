using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpCommandTest
    {
        private static RtmpCommand RoundTrip(RtmpCommand command) => RtmpCommand.Decode(command.Encode());

        [Fact]
        public void Connect_Should_Carry_The_Publisher_Properties_Then_The_Additional_Ones()
        {
            var fourCcList = Amf0Value.FromStrictArray([Amf0Value.FromString("hvc1")]);
            var command = RoundTrip(RtmpCommand.Connect(1, "live", "rtmp://example.com/live", [new("fourCcList", fourCcList)]));

            command.Name.Should().Be("connect");
            command.TransactionId.Should().Be(1);
            command.CommandObject.Properties.Select(p => p.Key).Should().Equal("app", "type", "flashVer", "tcUrl", "fourCcList");
            command.CommandObject.GetProperty("app")!.String.Should().Be("live");
            command.CommandObject.GetProperty("flashVer")!.String.Should().Be(RtmpCommand.DefaultFlashVersion);
            command.CommandObject.GetProperty("tcUrl")!.String.Should().Be("rtmp://example.com/live");
            command.CommandObject.GetProperty("fourCcList")!.Items[0].String.Should().Be("hvc1");
            command.Arguments.Should().BeEmpty();
            RoundTrip(RtmpCommand.Connect(1, "a", "b")).CommandObject.Properties.Should().HaveCount(4);
        }

        [Fact]
        public void ConnectResult_Should_Carry_Server_Properties_And_Connect_Success()
        {
            var command = RoundTrip(RtmpCommand.ConnectResult(1));

            command.Name.Should().Be("_result");
            command.TransactionId.Should().Be(1);
            command.CommandObject.GetProperty("fmsVer")!.String.Should().Be(RtmpCommand.DefaultServerVersion);
            command.CommandObject.GetProperty("capabilities")!.Number.Should().Be(31);
            command.StatusCode.Should().Be(RtmpStatusCodes.ConnectSuccess);
            command.Arguments[0].GetProperty("level")!.String.Should().Be(RtmpStatusCodes.LevelStatus);
            command.Arguments[0].GetProperty("objectEncoding")!.Number.Should().Be(0);
        }

        [Fact]
        public void StreamCommands_Should_Round_Trip()
        {
            var createStream = RoundTrip(RtmpCommand.CreateStream(4));
            createStream.Name.Should().Be("createStream");
            createStream.TransactionId.Should().Be(4);
            createStream.CommandObject.Type.Should().Be(Amf0Type.Null);
            createStream.Arguments.Should().BeEmpty();

            var created = RoundTrip(RtmpCommand.CreateStreamResult(4, 1));
            created.Name.Should().Be("_result");
            created.GetNumberArgument(0).Should().Be(1);
            created.StatusCode.Should().BeNull("a number argument has no code");

            foreach (var (command, name) in new[]
            {
                (RtmpCommand.ReleaseStream(2, "key"), "releaseStream"),
                (RtmpCommand.FCPublish(3, "key"), "FCPublish"),
                (RtmpCommand.FCUnpublish(6, "key"), "FCUnpublish")
            })
            {
                var decoded = RoundTrip(command);
                decoded.Name.Should().Be(name);
                decoded.TransactionId.Should().Be(command.TransactionId);
                decoded.GetStringArgument(0).Should().Be("key");
            }

            var publish = RoundTrip(RtmpCommand.Publish("key"));
            publish.Name.Should().Be("publish");
            publish.TransactionId.Should().Be(0);
            publish.GetStringArgument(0).Should().Be("key");
            publish.GetStringArgument(1).Should().Be("live");
            RoundTrip(RtmpCommand.Publish("key", "record")).GetStringArgument(1).Should().Be("record");

            var play = RoundTrip(RtmpCommand.Play("key"));
            play.Name.Should().Be("play");
            play.GetStringArgument(0).Should().Be("key");
            play.GetNumberArgument(1).Should().Be(-2);
            RoundTrip(RtmpCommand.Play("key", -1)).GetNumberArgument(1).Should().Be(-1);

            var deleteStream = RoundTrip(RtmpCommand.DeleteStream(7, 1));
            deleteStream.Name.Should().Be("deleteStream");
            deleteStream.GetNumberArgument(0).Should().Be(1);
        }

        [Fact]
        public void StatusReplies_Should_Carry_Level_Code_And_Description()
        {
            var onStatus = RoundTrip(RtmpCommand.OnStatus(RtmpStatusCodes.LevelError, RtmpStatusCodes.PublishBadName, "Rejected."));
            onStatus.Name.Should().Be("onStatus");
            onStatus.TransactionId.Should().Be(0);
            onStatus.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
            onStatus.Arguments[0].GetProperty("level")!.String.Should().Be(RtmpStatusCodes.LevelError);
            onStatus.Arguments[0].GetProperty("description")!.String.Should().Be("Rejected.");
            onStatus.Arguments[0].GetProperty("objectEncoding").Should().BeNull();

            var error = RoundTrip(RtmpCommand.Error(1, RtmpStatusCodes.ConnectRejected, "No such app."));
            error.Name.Should().Be("_error");
            error.TransactionId.Should().Be(1);
            error.StatusCode.Should().Be(RtmpStatusCodes.ConnectRejected);
            error.Arguments[0].GetProperty("level")!.String.Should().Be(RtmpStatusCodes.LevelError);

            var result = RoundTrip(RtmpCommand.Result(5, null, Amf0Value.Undefined));
            result.CommandObject.Type.Should().Be(Amf0Type.Null);
            result.Arguments[0].Type.Should().Be(Amf0Type.Undefined);
        }

        [Fact]
        public void ToMessage_Should_Be_An_Amf0_Command_On_The_Given_Stream()
        {
            var message = RtmpCommand.Publish("key").ToMessage(streamId: 1, timestamp: 9);

            message.Type.Should().Be(RtmpMessageType.CommandAmf0);
            message.StreamId.Should().Be(1);
            message.Timestamp.Should().Be(9);
            RtmpCommand.Decode(message.Payload.Span).Name.Should().Be("publish");
            RtmpCommand.CreateStream(2).ToMessage().StreamId.Should().Be(0);
        }

        [Fact]
        public void Arguments_Accessors_Should_Return_Null_For_Missing_Or_Mistyped()
        {
            var command = new RtmpCommand("x", 0, null, Amf0Value.FromNumber(1), Amf0Value.FromLongString("long"));

            command.GetStringArgument(0).Should().BeNull();
            command.GetStringArgument(1).Should().Be("long");
            command.GetStringArgument(2).Should().BeNull();
            command.GetStringArgument(-1).Should().BeNull();
            command.GetNumberArgument(0).Should().Be(1);
            command.GetNumberArgument(1).Should().BeNull();
            command.GetNumberArgument(5).Should().BeNull();
            command.GetNumberArgument(-1).Should().BeNull();
            command.StatusCode.Should().BeNull();
            new RtmpCommand("x", 0, null).StatusCode.Should().BeNull();
            new RtmpCommand("x", 0, null, Amf0Value.FromObject([new("code", Amf0Value.FromNumber(1))])).StatusCode.Should().BeNull();
        }

        [Fact]
        public void Constructor_Should_Copy_Arguments_And_Reject_Nulls()
        {
            var arguments = new[] { Amf0Value.FromNumber(1) };
            var command = new RtmpCommand("x", 0, null, arguments);
            arguments[0] = Amf0Value.FromNumber(2);

            command.Arguments[0].Number.Should().Be(1);
            command.CommandObject.Should().BeSameAs(Amf0Value.Null);
            FluentActions.Invoking(() => new RtmpCommand(null!, 0, null)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpCommand("x", 0, null, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.Connect(1, null!, "u")).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.Connect(1, "a", null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.ReleaseStream(1, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.Publish(null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.Publish("k", null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.Play(null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.OnStatus(null!, "c", "d")).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.OnStatus("l", null!, "d")).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpCommand.OnStatus("l", "c", null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Decode_NameOnlyOrNameAndTransaction_Should_Default_The_Rest()
        {
            var nameOnly = RtmpCommand.Decode(Amf0Writer.Encode(Amf0Value.FromString("onBWDone")));
            nameOnly.TransactionId.Should().Be(0);
            nameOnly.CommandObject.Type.Should().Be(Amf0Type.Null);
            nameOnly.Arguments.Should().BeEmpty();

            var withTransaction = RtmpCommand.Decode(Amf0Writer.Encode(Amf0Value.FromLongString("x"), Amf0Value.FromNumber(3)));
            withTransaction.Name.Should().Be("x");
            withTransaction.TransactionId.Should().Be(3);
            withTransaction.CommandObject.Type.Should().Be(Amf0Type.Null);
        }

        public static TheoryData<string, byte[]> MalformedCommands => new()
        {
            { "empty", [] },
            { "name is a number", Amf0Writer.Encode(Amf0Value.FromNumber(1)) },
            { "transaction id is a string", Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromString("1")) },
            { "string length past the end", [0x02, 0x00, 0x10, (byte)'c'] },
            { "AMF3 switch", Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromNumber(1)).Concat(new byte[] { 0x11 }).ToArray() },
            { "nesting past the limit", [.. Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromNumber(1)), .. NestedBytes(Amf0Reader.MaxNestingDepth + 1)] },
            { "too many arguments", Amf0Writer.Encode([Amf0Value.FromString("x"), Amf0Value.FromNumber(1), Amf0Value.Null, .. Enumerable.Repeat(Amf0Value.Null, Amf0Reader.MaxProperties + 1)]) },
        };

        internal static Amf0Value Nested(int depth)
        {
            var value = Amf0Value.FromNumber(1);
            for (var i = 0; i < depth; i++)
            {
                value = Amf0Value.FromObject([new("a", value)]);
            }

            return value;
        }

        // Amf0Writer refuses to nest past the limit too, so a hostile payload is built by hand.
        internal static byte[] NestedBytes(int depth)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < depth; i++)
            {
                bytes.AddRange([0x03, 0x00, 0x01, (byte)'a']);
            }

            bytes.AddRange(Amf0Writer.Encode(Amf0Value.FromNumber(1)));
            for (var i = 0; i < depth; i++)
            {
                bytes.AddRange([0x00, 0x00, 0x09]);
            }

            return [.. bytes];
        }

        [Theory]
        [MemberData(nameof(MalformedCommands))]
        public void Decode_Malformed_Should_Throw_Protocol_Exception(string name, byte[] payload)
        {
            FluentActions.Invoking(() => RtmpCommand.Decode(payload)).Should().Throw<RtmpProtocolException>(name);
        }

        [Fact]
        public void Decode_MalformedAmf0_Should_Wrap_The_Amf0_Error()
        {
            var thrown = FluentActions.Invoking(() => RtmpCommand.Decode([0x02, 0x00, 0x10])).Should().Throw<RtmpProtocolException>().Which;

            thrown.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Fact]
        public void Decode_NestingAtTheLimit_Should_Succeed()
        {
            var payload = Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromNumber(1), Nested(Amf0Reader.MaxNestingDepth));

            RtmpCommand.Decode(payload).CommandObject.Type.Should().Be(Amf0Type.Object);
        }

        [Fact]
        public void DataMessage_SetDataFrame_Should_Round_Trip_As_Metadata()
        {
            var metadata = new FlvMetadata([new("width", Amf0Value.FromNumber(1280)), new("encoder", Amf0Value.FromString("obs"))]);
            var message = RtmpDataMessage.SetDataFrame(metadata).ToMessage(streamId: 1, timestamp: 5);

            message.Type.Should().Be(RtmpMessageType.DataAmf0);
            message.StreamId.Should().Be(1);
            message.Timestamp.Should().Be(5);
            var decoded = RtmpDataMessage.Decode(message.Payload.Span);
            decoded.Name.Should().Be(RtmpDataMessage.SetDataFrameHandler);
            decoded.Values[0].String.Should().Be(RtmpDataMessage.OnMetaDataHandler);
            decoded.Values[1].Type.Should().Be(Amf0Type.EcmaArray);
            var read = decoded.TryGetMetadata()!;
            read.Width.Should().Be(1280);
            read.Encoder.Should().Be("obs");
        }

        [Fact]
        public void DataMessage_TryGetMetadata_Should_Accept_Bare_OnMetaData_And_Objects_Only()
        {
            var properties = Amf0Value.FromObject([new("height", Amf0Value.FromNumber(720))]);

            new RtmpDataMessage("onMetaData", properties).TryGetMetadata()!.Height.Should().Be(720);
            new RtmpDataMessage("onMetaData", Amf0Value.FromNumber(1)).TryGetMetadata().Should().BeNull();
            new RtmpDataMessage("onMetaData").TryGetMetadata().Should().BeNull();
            new RtmpDataMessage("@setDataFrame").TryGetMetadata().Should().BeNull();
            new RtmpDataMessage("@setDataFrame", Amf0Value.FromString("onCuePoint"), properties).TryGetMetadata().Should().BeNull();
            new RtmpDataMessage("@setDataFrame", Amf0Value.FromString("onMetaData")).TryGetMetadata().Should().BeNull();
            new RtmpDataMessage("onTextData", properties).TryGetMetadata().Should().BeNull();
        }

        [Fact]
        public void DataMessage_Constructor_Should_Copy_Values_And_Reject_Nulls()
        {
            var values = new[] { Amf0Value.FromNumber(1) };
            var message = new RtmpDataMessage("x", values);
            values[0] = Amf0Value.Null;

            message.Values[0].Number.Should().Be(1);
            RtmpDataMessage.Decode(message.Encode()).Values.Should().ContainSingle().Which.Number.Should().Be(1);
            FluentActions.Invoking(() => new RtmpDataMessage(null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpDataMessage("x", null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => RtmpDataMessage.SetDataFrame(null!)).Should().Throw<ArgumentNullException>();
        }

        public static TheoryData<string, byte[]> MalformedDataMessages => new()
        {
            { "empty", [] },
            { "name is null", Amf0Writer.Encode(Amf0Value.Null) },
            { "nesting past the limit", [.. Amf0Writer.Encode(Amf0Value.FromString("onMetaData")), .. NestedBytes(Amf0Reader.MaxNestingDepth + 1)] },
            { "too many values", Amf0Writer.Encode([Amf0Value.FromString("x"), .. Enumerable.Repeat(Amf0Value.Null, Amf0Reader.MaxProperties + 1)]) },
        };

        [Theory]
        [MemberData(nameof(MalformedDataMessages))]
        public void DataMessage_DecodeMalformed_Should_Throw_Protocol_Exception(string name, byte[] payload)
        {
            FluentActions.Invoking(() => RtmpDataMessage.Decode(payload)).Should().Throw<RtmpProtocolException>(name);
        }

        [Fact]
        public void DataMessage_DecodeLongStringName_Should_Succeed()
        {
            RtmpDataMessage.Decode(Amf0Writer.Encode(Amf0Value.FromLongString("onMetaData"))).Name.Should().Be("onMetaData");
        }
    }
}
