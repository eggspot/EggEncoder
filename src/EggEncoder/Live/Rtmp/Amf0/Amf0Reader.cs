using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Live.Rtmp.Amf0
{
    /// <summary>
    /// Reads AMF0 values (Adobe AMF0 specification) from a byte span, as RTMP command messages and FLV
    /// script-data tags carry them. Every length and count is bounded before anything is allocated
    /// (docs/live-ingest-restream-plan.md "Security posture"): nesting deeper than
    /// <see cref="MaxNestingDepth"/>, a string longer than <see cref="MaxStringBytes"/>, more than
    /// <see cref="MaxProperties"/> properties or items, any length past the end of the data, and every
    /// unsupported type marker all throw <see cref="InvalidDataException"/>.
    /// </summary>
    public ref struct Amf0Reader
    {
        /// <summary>How deeply objects and arrays may nest; real <c>connect</c>/<c>onMetaData</c> payloads are 2-3 levels deep.</summary>
        public const int MaxNestingDepth = 32;

        /// <summary>The longest string (short, long or XML) accepted, in UTF-8 bytes.</summary>
        public const int MaxStringBytes = 64 * 1024;

        /// <summary>The most properties one object/ECMA array, or items one strict array, may hold.</summary>
        public const int MaxProperties = 1024;

        private const byte ObjectEndMarker = 0x09;

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        /// <summary>Starts reading at the beginning of <paramref name="data"/>.</summary>
        public Amf0Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>Bytes consumed so far.</summary>
        public readonly int Position => _position;

        /// <summary>True once every byte has been consumed.</summary>
        public readonly bool AtEnd => _position >= _data.Length;

        /// <summary>Reads the next value.</summary>
        /// <exception cref="InvalidDataException">Truncated or malformed data, an unsupported type marker, or a bound exceeded.</exception>
        public Amf0Value ReadValue() => ReadValue(depth: 0);

        private Amf0Value ReadValue(int depth)
        {
            var marker = ReadByte();
            switch (marker)
            {
                case (byte)Amf0Type.Number:
                    return Amf0Value.FromNumber(ReadDouble());
                case (byte)Amf0Type.Boolean:
                    return Amf0Value.FromBoolean(ReadByte() != 0);
                case (byte)Amf0Type.String:
                    return Amf0Value.FromShortString(ReadUtf8(ReadUInt16()));
                case (byte)Amf0Type.LongString:
                    return Amf0Value.FromLongString(ReadUtf8(ReadUInt32()));
                case (byte)Amf0Type.XmlDocument:
                    return Amf0Value.FromXmlDocument(ReadUtf8(ReadUInt32()));
                case (byte)Amf0Type.Null:
                    return Amf0Value.Null;
                case (byte)Amf0Type.Undefined:
                    return Amf0Value.Undefined;
                case (byte)Amf0Type.Date:
                    var milliseconds = ReadDouble();
                    var timeZone = (short)ReadUInt16();
                    return Amf0Value.FromDate(milliseconds, timeZone);
                case (byte)Amf0Type.Object:
                    return Amf0Value.FromObject(ReadProperties(EnterNested(depth)));
                case (byte)Amf0Type.EcmaArray:
                    // The associative-count field is only a hint (writers get it wrong); the
                    // object-end marker is what terminates the array, as for an Object.
                    ReadUInt32();
                    return Amf0Value.FromEcmaArray(ReadProperties(EnterNested(depth)));
                case (byte)Amf0Type.StrictArray:
                    return Amf0Value.FromStrictArray(ReadItems(EnterNested(depth)));
                case ObjectEndMarker:
                    throw new InvalidDataException($"AMF0: unexpected object-end marker at offset {_position - 1}");
                default:
                    throw new InvalidDataException($"AMF0: unsupported type marker 0x{marker:X2} at offset {_position - 1}");
            }
        }

        private static int EnterNested(int depth)
        {
            if (depth + 1 > MaxNestingDepth)
            {
                throw new InvalidDataException($"AMF0: nesting deeper than {MaxNestingDepth} levels");
            }

            return depth + 1;
        }

        private List<KeyValuePair<string, Amf0Value>> ReadProperties(int depth)
        {
            var properties = new List<KeyValuePair<string, Amf0Value>>();
            while (true)
            {
                var name = ReadUtf8(ReadUInt16());
                if (name.Length == 0 && Peek() == ObjectEndMarker)
                {
                    _position++;
                    return properties;
                }

                if (properties.Count == MaxProperties)
                {
                    throw new InvalidDataException($"AMF0: more than {MaxProperties} properties in one object");
                }

                properties.Add(new KeyValuePair<string, Amf0Value>(name, ReadValue(depth)));
            }
        }

        private List<Amf0Value> ReadItems(int depth)
        {
            var count = ReadUInt32();
            // Every value is at least one byte, so a count past what is left is a lie.
            if (count > MaxProperties || count > (uint)(_data.Length - _position))
            {
                throw new InvalidDataException($"AMF0: strict array count {count} exceeds the limit or the data left");
            }

            var items = new List<Amf0Value>((int)count);
            for (var i = 0; i < count; i++)
            {
                items.Add(ReadValue(depth));
            }

            return items;
        }

        private string ReadUtf8(uint length)
        {
            if (length > MaxStringBytes)
            {
                throw new InvalidDataException($"AMF0: string length {length} exceeds the {MaxStringBytes}-byte limit");
            }

            Require((int)length);
            var text = Encoding.UTF8.GetString(_data.Slice(_position, (int)length));
            _position += (int)length;
            return text;
        }

        private readonly byte Peek()
        {
            Require(1);
            return _data[_position];
        }

        private byte ReadByte()
        {
            Require(1);
            return _data[_position++];
        }

        private ushort ReadUInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_position..]);
            _position += 2;
            return value;
        }

        private uint ReadUInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        private double ReadDouble()
        {
            Require(8);
            var value = BinaryPrimitives.ReadDoubleBigEndian(_data[_position..]);
            _position += 8;
            return value;
        }

        private readonly void Require(int count)
        {
            if (count > _data.Length - _position)
            {
                throw new InvalidDataException($"AMF0: truncated, needed {count} bytes at offset {_position}");
            }
        }
    }
}
