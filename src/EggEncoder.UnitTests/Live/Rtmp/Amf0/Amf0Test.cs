using System.Buffers;
using System.Buffers.Binary;
using EggEncoder.Live.Rtmp.Amf0;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp.Amf0
{
    public class Amf0Test
    {
        private static Amf0Value RoundTrip(Amf0Value value)
        {
            var bytes = Amf0Writer.Encode(value);
            var reader = new Amf0Reader(bytes);
            var result = reader.ReadValue();
            reader.AtEnd.Should().BeTrue();
            reader.Position.Should().Be(bytes.Length);
            return result;
        }

        private static Amf0Value Read(params byte[] bytes) => new Amf0Reader(bytes).ReadValue();

        private static Action ReadAction(byte[] bytes) => () => new Amf0Reader(bytes).ReadValue();

        [Fact]
        public void Number_RoundTrip_Should_Preserve_Value_And_Wire_Bytes()
        {
            var bytes = Amf0Writer.Encode(Amf0Value.FromNumber(1.5));

            bytes.Should().Equal(0x00, 0x3F, 0xF8, 0, 0, 0, 0, 0, 0);
            RoundTrip(Amf0Value.FromNumber(-42.25)).Number.Should().Be(-42.25);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Boolean_RoundTrip_Should_Preserve_Value(bool value)
        {
            var result = RoundTrip(Amf0Value.FromBoolean(value));

            result.Type.Should().Be(Amf0Type.Boolean);
            result.Boolean.Should().Be(value);
            result.ToString().Should().Be(value ? "true" : "false");
        }

        [Fact]
        public void Boolean_NonZeroByte_Should_Read_As_True()
        {
            Read(0x01, 0x7F).Boolean.Should().BeTrue();
        }

        [Fact]
        public void String_RoundTrip_Should_Use_Short_String_Marker_And_Utf8()
        {
            var bytes = Amf0Writer.Encode(Amf0Value.FromString("hé"));

            bytes.Should().Equal(0x02, 0x00, 0x03, (byte)'h', 0xC3, 0xA9);
            var result = RoundTrip(Amf0Value.FromString("onMetaData"));
            result.Type.Should().Be(Amf0Type.String);
            result.String.Should().Be("onMetaData");
            result.ToString().Should().Be("onMetaData");
        }

        [Fact]
        public void FromString_Over65535Utf8Bytes_Should_Become_LongString()
        {
            var text = new string('a', ushort.MaxValue + 1);

            var value = Amf0Value.FromString(text);
            var bytes = Amf0Writer.Encode(value);

            value.Type.Should().Be(Amf0Type.LongString);
            bytes[0].Should().Be(0x0C);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)).Should().Be((uint)text.Length);
        }

        [Fact]
        public void LongString_RoundTrip_Should_Keep_LongString_Type()
        {
            var result = RoundTrip(Amf0Value.FromLongString("short but long"));

            result.Type.Should().Be(Amf0Type.LongString);
            result.String.Should().Be("short but long");
        }

        [Fact]
        public void XmlDocument_RoundTrip_Should_Preserve_Text()
        {
            var result = RoundTrip(Amf0Value.FromXmlDocument("<a/>"));

            result.Type.Should().Be(Amf0Type.XmlDocument);
            result.String.Should().Be("<a/>");
            result.ToString().Should().Be("<a/>");
        }

        [Fact]
        public void NullAndUndefined_RoundTrip_Should_Return_Shared_Instances()
        {
            RoundTrip(Amf0Value.Null).Should().BeSameAs(Amf0Value.Null);
            RoundTrip(Amf0Value.Undefined).Should().BeSameAs(Amf0Value.Undefined);
            Amf0Value.Null.ToString().Should().Be("Null");
            Amf0Value.Null.String.Should().BeNull();
            Amf0Value.Null.Properties.Should().BeEmpty();
            Amf0Value.Null.Items.Should().BeEmpty();
        }

        [Fact]
        public void Date_RoundTrip_Should_Preserve_Milliseconds_And_TimeZone()
        {
            var result = RoundTrip(Amf0Value.FromDate(1_700_000_000_000, timeZone: -60));

            result.Type.Should().Be(Amf0Type.Date);
            result.Number.Should().Be(1_700_000_000_000);
            result.TimeZone.Should().Be(-60);
            result.ToString().Should().Be("Date(1700000000000)");
        }

        [Fact]
        public void Object_RoundTrip_Should_Preserve_Property_Order_And_Nesting()
        {
            var value = Amf0Value.FromObject(
            [
                new("app", Amf0Value.FromString("live")),
                new("nested", Amf0Value.FromObject([new("x", Amf0Value.FromNumber(1))])),
                new("flag", Amf0Value.FromBoolean(true))
            ]);

            var result = RoundTrip(value);

            result.Type.Should().Be(Amf0Type.Object);
            result.Properties.Select(p => p.Key).Should().Equal("app", "nested", "flag");
            result.GetProperty("app")!.String.Should().Be("live");
            result.GetProperty("nested")!.GetProperty("x")!.Number.Should().Be(1);
            result.GetProperty("missing").Should().BeNull();
            result.ToString().Should().Be("Object[3]");
        }

        [Fact]
        public void EcmaArray_RoundTrip_Should_Write_Count_And_Preserve_Properties()
        {
            var value = Amf0Value.FromEcmaArray([new("duration", Amf0Value.FromNumber(0)), new("width", Amf0Value.FromNumber(64))]);

            var bytes = Amf0Writer.Encode(value);
            var result = RoundTrip(value);

            bytes[0].Should().Be(0x08);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)).Should().Be(2);
            bytes[^3..].Should().Equal(0, 0, 0x09);
            result.Type.Should().Be(Amf0Type.EcmaArray);
            result.GetProperty("width")!.Number.Should().Be(64);
            result.ToString().Should().Be("EcmaArray[2]");
        }

        [Fact]
        public void EcmaArray_WrongCountHint_Should_Read_Until_Object_End()
        {
            // count says 5, only one property follows: the object-end marker terminates it.
            byte[] bytes = [0x08, 0, 0, 0, 5, 0, 1, (byte)'a', 0x05, 0, 0, 0x09];

            var result = Read(bytes);

            result.Properties.Should().ContainSingle().Which.Key.Should().Be("a");
        }

        [Fact]
        public void StrictArray_RoundTrip_Should_Preserve_Items()
        {
            var result = RoundTrip(Amf0Value.FromStrictArray([Amf0Value.FromNumber(1), Amf0Value.Null, Amf0Value.FromString("x")]));

            result.Type.Should().Be(Amf0Type.StrictArray);
            result.Items.Should().HaveCount(3);
            result.Items[2].String.Should().Be("x");
            result.ToString().Should().Be("StrictArray[3]");
        }

        [Fact]
        public void Encode_SeveralValues_Should_Write_Them_Back_To_Back()
        {
            var bytes = Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromNumber(1), Amf0Value.Null);
            var reader = new Amf0Reader(bytes);

            reader.ReadValue().String.Should().Be("connect");
            reader.ReadValue().Number.Should().Be(1);
            reader.ReadValue().Type.Should().Be(Amf0Type.Null);
            reader.AtEnd.Should().BeTrue();
        }

        [Fact]
        public void Write_ToBufferWriter_Should_Append()
        {
            var buffer = new ArrayBufferWriter<byte>();

            Amf0Writer.Write(buffer, Amf0Value.Null);
            Amf0Writer.Write(buffer, Amf0Value.Undefined);

            buffer.WrittenSpan.ToArray().Should().Equal(0x05, 0x06);
        }

        [Fact]
        public void Nesting_AtLimit_Should_RoundTrip_And_Past_Limit_Should_Throw_On_Write()
        {
            var value = Amf0Value.Null;
            for (var i = 0; i < Amf0Reader.MaxNestingDepth; i++)
            {
                value = Amf0Value.FromStrictArray([value]);
            }

            RoundTrip(value).Type.Should().Be(Amf0Type.StrictArray);
            var tooDeep = Amf0Value.FromObject([new("a", value)]);
            var act = () => Amf0Writer.Encode(tooDeep);
            act.Should().Throw<ArgumentException>().WithMessage("*nest deeper*");
        }

        [Fact]
        public void Read_NestingPastLimit_Should_Throw()
        {
            var bytes = new List<byte>();
            for (var i = 0; i <= Amf0Reader.MaxNestingDepth; i++)
            {
                bytes.AddRange(new byte[] { 0x0A, 0, 0, 0, 1 });
            }

            bytes.Add(0x05);

            ReadAction(bytes.ToArray()).Should().Throw<InvalidDataException>().WithMessage("*nesting deeper*");
        }

        [Fact]
        public void Read_EcmaArrayNestingPastLimit_Should_Throw()
        {
            var bytes = new List<byte>();
            for (var i = 0; i <= Amf0Reader.MaxNestingDepth; i++)
            {
                bytes.AddRange(new byte[] { 0x08, 0, 0, 0, 1, 0, 1, (byte)'a' });
            }

            ReadAction(bytes.ToArray()).Should().Throw<InvalidDataException>().WithMessage("*nesting deeper*");
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0x00, 0x3F })]
        [InlineData(new byte[] { 0x01 })]
        [InlineData(new byte[] { 0x02, 0x00 })]
        [InlineData(new byte[] { 0x02, 0x00, 0x05, (byte)'a' })]
        [InlineData(new byte[] { 0x0C, 0, 0, 0 })]
        [InlineData(new byte[] { 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
        [InlineData(new byte[] { 0x03, 0, 1, (byte)'a' })]
        [InlineData(new byte[] { 0x03, 0, 0 })]
        [InlineData(new byte[] { 0x08, 0, 0 })]
        [InlineData(new byte[] { 0x0A, 0, 0, 0, 1 })]
        public void Read_Truncated_Should_Throw_InvalidDataException(byte[] bytes)
        {
            ReadAction(bytes).Should().Throw<InvalidDataException>();
        }

        [Theory]
        [InlineData(0x04)] // MovieClip (reserved)
        [InlineData(0x07)] // Reference
        [InlineData(0x0D)] // Unsupported
        [InlineData(0x0E)] // RecordSet (reserved)
        [InlineData(0x10)] // Typed Object
        [InlineData(0x11)] // AVM+ (AMF3) switch
        [InlineData(0xFF)]
        public void Read_UnsupportedMarker_Should_Throw(byte marker)
        {
            ReadAction([marker, 0, 0, 0, 0]).Should().Throw<InvalidDataException>().WithMessage("*unsupported type marker*");
        }

        [Fact]
        public void Read_BareObjectEndMarker_Should_Throw()
        {
            ReadAction([0x09]).Should().Throw<InvalidDataException>().WithMessage("*object-end*");
        }

        [Fact]
        public void Read_LongStringPastLimit_Should_Throw_Before_Allocating()
        {
            ReadAction([0x0C, 0xFF, 0xFF, 0xFF, 0xFF]).Should().Throw<InvalidDataException>().WithMessage("*exceeds*");
            ReadAction([0x0F, 0x00, 0x01, 0x00, 0x01]).Should().Throw<InvalidDataException>().WithMessage("*exceeds*");
        }

        [Fact]
        public void Read_StringAtLimit_Should_Succeed()
        {
            var bytes = new byte[5 + Amf0Reader.MaxStringBytes];
            bytes[0] = 0x0C;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), Amf0Reader.MaxStringBytes);
            bytes.AsSpan(5).Fill((byte)'z');

            Read(bytes).String.Should().HaveLength(Amf0Reader.MaxStringBytes);
        }

        [Fact]
        public void Read_StrictArrayCountPastLimitOrData_Should_Throw()
        {
            ReadAction([0x0A, 0xFF, 0xFF, 0xFF, 0xFF]).Should().Throw<InvalidDataException>().WithMessage("*strict array count*");
            ReadAction([0x0A, 0, 0, 0, 3, 0x05, 0x05]).Should().Throw<InvalidDataException>().WithMessage("*strict array count*");
            var overLimit = new byte[5 + Amf0Reader.MaxProperties + 1];
            overLimit[0] = 0x0A;
            BinaryPrimitives.WriteUInt32BigEndian(overLimit.AsSpan(1), Amf0Reader.MaxProperties + 1);
            overLimit.AsSpan(5).Fill(0x05);
            ReadAction(overLimit).Should().Throw<InvalidDataException>().WithMessage("*strict array count*");
        }

        [Fact]
        public void Read_ObjectWithTooManyProperties_Should_Throw()
        {
            var properties = Enumerable.Range(0, Amf0Reader.MaxProperties + 1)
                .Select(i => new KeyValuePair<string, Amf0Value>($"p{i}", Amf0Value.Null));
            var bytes = Amf0Writer.Encode(Amf0Value.FromObject(properties));

            ReadAction(bytes).Should().Throw<InvalidDataException>().WithMessage("*more than*properties*");
        }

        [Fact]
        public void Read_ObjectWithExactlyMaxProperties_Should_Succeed()
        {
            var properties = Enumerable.Range(0, Amf0Reader.MaxProperties)
                .Select(i => new KeyValuePair<string, Amf0Value>($"p{i}", Amf0Value.Null));

            RoundTrip(Amf0Value.FromObject(properties)).Properties.Should().HaveCount(Amf0Reader.MaxProperties);
        }

        [Fact]
        public void Read_EmptyKeyNotFollowedByEndMarker_Should_Read_As_Property()
        {
            byte[] bytes = [0x03, 0, 0, 0x05, 0, 0, 0x09];

            var result = Read(bytes);

            result.Properties.Should().ContainSingle().Which.Key.Should().BeEmpty();
        }

        [Fact]
        public void RandomBytes_Should_Never_Throw_Anything_But_InvalidDataException()
        {
            var random = new Random(1234);
            for (var i = 0; i < 5000; i++)
            {
                var bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
                if (bytes.Length > 0)
                {
                    bytes[0] = (byte)random.Next(0, 0x12);
                }

                try
                {
                    var reader = new Amf0Reader(bytes);
                    while (!reader.AtEnd)
                    {
                        reader.ReadValue();
                    }
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        [Fact]
        public void Factories_NullArguments_Should_Throw()
        {
            ((Action)(() => Amf0Value.FromString(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromLongString(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromXmlDocument(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromObject(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromEcmaArray(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromStrictArray(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Value.FromStrictArray([null!]))).Should().Throw<ArgumentException>();
            ((Action)(() => Amf0Value.FromObject([new("a", null!)]))).Should().Throw<ArgumentException>();
            ((Action)(() => Amf0Value.FromObject([new(null!, Amf0Value.Null)]))).Should().Throw<ArgumentException>();
            ((Action)(() => Amf0Value.Null.GetProperty(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Writer.Encode(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Writer.Write(null!, Amf0Value.Null))).Should().Throw<ArgumentNullException>();
            ((Action)(() => Amf0Writer.Write(new ArrayBufferWriter<byte>(), null!))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void FromObject_Should_Copy_The_Properties()
        {
            var source = new List<KeyValuePair<string, Amf0Value>> { new("a", Amf0Value.Null) };

            var value = Amf0Value.FromObject(source);
            source.Add(new("b", Amf0Value.Null));

            value.Properties.Should().HaveCount(1);
        }

        [Fact]
        public void Write_PropertyNameOver65535Bytes_Should_Throw()
        {
            var value = Amf0Value.FromObject([new(new string('k', ushort.MaxValue + 1), Amf0Value.Null)]);

            ((Action)(() => Amf0Writer.Encode(value))).Should().Throw<ArgumentException>().WithMessage("*65,535*");
        }
    }
}
