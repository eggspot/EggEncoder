using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Live.Rtmp.Amf0
{
    /// <summary>
    /// Writes AMF0 values (Adobe AMF0 specification): the inverse of <see cref="Amf0Reader"/> for every
    /// value it can produce, so a write followed by a read gives back an equal value within the reader's bounds. Nesting is
    /// capped at <see cref="Amf0Reader.MaxNestingDepth"/>. The reader's other bounds are deliberately
    /// not enforced here: a string over <see cref="Amf0Reader.MaxStringBytes"/> or an object with more
    /// than <see cref="Amf0Reader.MaxProperties"/> properties is valid AMF0 and is written, but
    /// <see cref="Amf0Reader"/> refuses to read it back.
    /// </summary>
    public static class Amf0Writer
    {
        /// <summary>Encodes <paramref name="values"/> back to back, as an RTMP command or FLV script tag body.</summary>
        public static byte[] Encode(params Amf0Value[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var buffer = new ArrayBufferWriter<byte>();
            foreach (var value in values)
            {
                Write(buffer, value);
            }

            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>Appends one encoded value to <paramref name="destination"/>.</summary>
        /// <exception cref="ArgumentException">Nesting deeper than <see cref="Amf0Reader.MaxNestingDepth"/>, or a property name over 65,535 UTF-8 bytes.</exception>
        public static void Write(IBufferWriter<byte> destination, Amf0Value value)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(value);
            Write(destination, value, depth: 0);
        }

        private static void Write(IBufferWriter<byte> destination, Amf0Value value, int depth)
        {
            WriteByte(destination, (byte)value.Type);
            switch (value.Type)
            {
                case Amf0Type.Number:
                    WriteDouble(destination, value.Number);
                    break;
                case Amf0Type.Boolean:
                    WriteByte(destination, value.Boolean ? (byte)1 : (byte)0);
                    break;
                case Amf0Type.String:
                    WriteShortString(destination, value.String!);
                    break;
                case Amf0Type.LongString:
                case Amf0Type.XmlDocument:
                    var bytes = Encoding.UTF8.GetBytes(value.String!);
                    WriteUInt32(destination, (uint)bytes.Length);
                    destination.Write(bytes);
                    break;
                case Amf0Type.Date:
                    WriteDouble(destination, value.Number);
                    WriteUInt16(destination, (ushort)value.TimeZone);
                    break;
                case Amf0Type.Object:
                    WriteProperties(destination, value, EnterNested(depth));
                    break;
                case Amf0Type.EcmaArray:
                    WriteUInt32(destination, (uint)value.Properties.Count);
                    WriteProperties(destination, value, EnterNested(depth));
                    break;
                case Amf0Type.StrictArray:
                    var itemDepth = EnterNested(depth);
                    WriteUInt32(destination, (uint)value.Items.Count);
                    foreach (var item in value.Items)
                    {
                        Write(destination, item, itemDepth);
                    }

                    break;
            }
        }

        private static int EnterNested(int depth) =>
            depth + 1 <= Amf0Reader.MaxNestingDepth
                ? depth + 1
                : throw new ArgumentException($"AMF0 values nest deeper than {Amf0Reader.MaxNestingDepth} levels");

        private static void WriteProperties(IBufferWriter<byte> destination, Amf0Value value, int depth)
        {
            foreach (var property in value.Properties)
            {
                WriteShortString(destination, property.Key);
                Write(destination, property.Value, depth);
            }

            WriteUInt16(destination, 0);
            WriteByte(destination, 0x09);
        }

        private static void WriteShortString(IBufferWriter<byte> destination, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"AMF0 short string of {bytes.Length} UTF-8 bytes exceeds 65,535");
            }

            WriteUInt16(destination, (ushort)bytes.Length);
            destination.Write(bytes);
        }

        private static void WriteByte(IBufferWriter<byte> destination, byte value)
        {
            destination.GetSpan(1)[0] = value;
            destination.Advance(1);
        }

        private static void WriteUInt16(IBufferWriter<byte> destination, ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination.GetSpan(2), value);
            destination.Advance(2);
        }

        private static void WriteUInt32(IBufferWriter<byte> destination, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination.GetSpan(4), value);
            destination.Advance(4);
        }

        private static void WriteDouble(IBufferWriter<byte> destination, double value)
        {
            BinaryPrimitives.WriteDoubleBigEndian(destination.GetSpan(8), value);
            destination.Advance(8);
        }
    }
}
