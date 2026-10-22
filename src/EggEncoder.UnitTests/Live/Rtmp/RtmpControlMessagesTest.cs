using System.Buffers.Binary;
using EggEncoder.Live.Rtmp;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpControlMessagesTest
    {
        [Fact]
        public void ProtocolControl_EveryMessage_Should_Round_Trip_On_Stream_0()
        {
            var setChunkSize = RtmpProtocolControl.CreateSetChunkSize(4096);
            var abort = RtmpProtocolControl.CreateAbort(65599);
            var acknowledgement = RtmpProtocolControl.CreateAcknowledgement(uint.MaxValue);
            var window = RtmpProtocolControl.CreateWindowAcknowledgementSize(2_500_000);
            var bandwidth = RtmpProtocolControl.CreateSetPeerBandwidth(5_000_000, RtmpPeerBandwidthLimitType.Dynamic);

            new[] { setChunkSize, abort, acknowledgement, window, bandwidth }.Should().OnlyContain(m => m.StreamId == 0 && m.Timestamp == 0);
            setChunkSize.Type.Should().Be(RtmpMessageType.SetChunkSize);
            setChunkSize.Payload.ToArray().Should().Equal(0, 0, 0x10, 0);
            RtmpProtocolControl.ReadSetChunkSize(setChunkSize.Payload.Span).Should().Be(4096);
            abort.Type.Should().Be(RtmpMessageType.Abort);
            RtmpProtocolControl.ReadAbort(abort.Payload.Span).Should().Be(65599);
            acknowledgement.Type.Should().Be(RtmpMessageType.Acknowledgement);
            RtmpProtocolControl.ReadAcknowledgement(acknowledgement.Payload.Span).Should().Be(uint.MaxValue);
            window.Type.Should().Be(RtmpMessageType.WindowAcknowledgementSize);
            RtmpProtocolControl.ReadWindowAcknowledgementSize(window.Payload.Span).Should().Be(2_500_000u);
            bandwidth.Type.Should().Be(RtmpMessageType.SetPeerBandwidth);
            bandwidth.Payload.Length.Should().Be(5);
            RtmpProtocolControl.ReadSetPeerBandwidth(bandwidth.Payload.Span).Should().Be((5_000_000u, RtmpPeerBandwidthLimitType.Dynamic));
        }

        [Fact]
        public void ProtocolControl_LargestChunkSize_Should_Round_Trip()
        {
            RtmpProtocolControl.ReadSetChunkSize(RtmpProtocolControl.CreateSetChunkSize(int.MaxValue).Payload.Span).Should().Be(RtmpProtocolControl.MaxChunkSize);
        }

        [Fact]
        public void ProtocolControl_Create_Should_Reject_Invalid_Arguments()
        {
            FluentActions.Invoking(() => RtmpProtocolControl.CreateSetChunkSize(0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => RtmpProtocolControl.CreateAbort(1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => RtmpProtocolControl.CreateAbort(65600)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(new byte[] { 0, 0, 0, 0 }, "*outside*")]
        [InlineData(new byte[] { 0x80, 0, 0, 1 }, "*outside*")]
        [InlineData(new byte[] { 0, 0, 1 }, "*3 bytes*")]
        [InlineData(new byte[] { 0, 0, 0, 1, 0 }, "*5 bytes*")]
        public void ReadSetChunkSize_Invalid_Should_Throw(byte[] payload, string message)
        {
            FluentActions.Invoking(() => RtmpProtocolControl.ReadSetChunkSize(payload)).Should().Throw<RtmpProtocolException>().WithMessage(message);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(65600u)]
        public void ReadAbort_ChunkStreamOutOfRange_Should_Throw(uint chunkStreamId)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, chunkStreamId);

            FluentActions.Invoking(() => RtmpProtocolControl.ReadAbort(payload)).Should().Throw<RtmpProtocolException>();
        }

        [Fact]
        public void ReadAcknowledgementAndWindow_WrongLength_Should_Throw()
        {
            FluentActions.Invoking(() => RtmpProtocolControl.ReadAcknowledgement(new byte[3])).Should().Throw<RtmpProtocolException>();
            FluentActions.Invoking(() => RtmpProtocolControl.ReadWindowAcknowledgementSize(new byte[8])).Should().Throw<RtmpProtocolException>();
        }

        [Fact]
        public void ReadSetPeerBandwidth_WrongLengthOrUnknownLimitType_Should_Throw()
        {
            FluentActions.Invoking(() => RtmpProtocolControl.ReadSetPeerBandwidth(new byte[4])).Should().Throw<RtmpProtocolException>();
            FluentActions.Invoking(() => RtmpProtocolControl.ReadSetPeerBandwidth(new byte[] { 0, 0, 1, 0, 3 }))
                .Should().Throw<RtmpProtocolException>().WithMessage("*limit type 3*");
        }

        [Theory]
        [InlineData(RtmpPeerBandwidthLimitType.Hard)]
        [InlineData(RtmpPeerBandwidthLimitType.Soft)]
        public void ReadSetPeerBandwidth_EveryLimitType_Should_Round_Trip(RtmpPeerBandwidthLimitType limitType)
        {
            RtmpProtocolControl.ReadSetPeerBandwidth(RtmpProtocolControl.CreateSetPeerBandwidth(1, limitType).Payload.Span).LimitType.Should().Be(limitType);
        }

        public static TheoryData<RtmpUserControl, int> UserControlEvents => new()
        {
            { RtmpUserControl.StreamBegin(1), 6 },
            { RtmpUserControl.StreamEof(2), 6 },
            { new RtmpUserControl(RtmpUserControlEventType.StreamDry, 3), 6 },
            { RtmpUserControl.SetBufferLength(4, 3000), 10 },
            { new RtmpUserControl(RtmpUserControlEventType.StreamIsRecorded, 5), 6 },
            { RtmpUserControl.PingRequest(123456), 6 },
            { RtmpUserControl.PingResponse(123456), 6 },
        };

        [Theory]
        [MemberData(nameof(UserControlEvents))]
        public void UserControl_EveryEvent_Should_Round_Trip(RtmpUserControl userControl, int payloadLength)
        {
            var message = userControl.ToMessage(timestamp: 42);

            message.Type.Should().Be(RtmpMessageType.UserControl);
            message.StreamId.Should().Be(0);
            message.Timestamp.Should().Be(42);
            message.Payload.Length.Should().Be(payloadLength);
            RtmpUserControl.Read(message.Payload.Span).Should().Be(userControl);
        }

        [Fact]
        public void UserControl_Factories_Should_Set_Type_And_Value()
        {
            RtmpUserControl.StreamBegin(7).Should().Be(new RtmpUserControl(RtmpUserControlEventType.StreamBegin, 7));
            RtmpUserControl.SetBufferLength(7, 100).Should().Be(new RtmpUserControl(RtmpUserControlEventType.SetBufferLength, 7, 100));
            RtmpUserControl.PingResponse(9).ToMessage().Payload.ToArray().Should().Equal(0, 7, 0, 0, 0, 9);
        }

        [Fact]
        public void UserControl_UnknownEvent_Should_Be_Kept_With_Its_First_Value()
        {
            RtmpUserControl.Read(new byte[] { 0, 31, 0, 0, 0, 8, 1, 2 }).Should().Be(new RtmpUserControl((RtmpUserControlEventType)31, 8));
            RtmpUserControl.Read(new byte[] { 0, 32 }).Should().Be(new RtmpUserControl((RtmpUserControlEventType)32, 0));
        }

        [Theory]
        [InlineData(new byte[] { })]
        [InlineData(new byte[] { 0 })]
        [InlineData(new byte[] { 0, 0, 0, 0, 1 })]
        [InlineData(new byte[] { 0, 3, 0, 0, 0, 1, 0, 0, 0 })]
        [InlineData(new byte[] { 0, 6 })]
        public void UserControl_TooShort_Should_Throw(byte[] payload)
        {
            FluentActions.Invoking(() => RtmpUserControl.Read(payload)).Should().Throw<RtmpProtocolException>();
        }
    }
}
