namespace EggEncoder.Live.Rtmp.Amf0
{
    /// <summary>
    /// One AMF0 value: the hand-written, reflection-free value type RTMP command messages and FLV
    /// <c>onMetaData</c> script tags are decoded into and encoded from. Create one through the static
    /// factories; read it through the property matching its <see cref="Type"/>. See
    /// docs/live-ingest-restream-plan.md items 2 and 3.
    /// </summary>
    public sealed class Amf0Value
    {
        private static readonly IReadOnlyList<KeyValuePair<string, Amf0Value>> NoProperties = [];
        private static readonly IReadOnlyList<Amf0Value> NoItems = [];

        /// <summary>The shared AMF0 <c>null</c> value.</summary>
        public static readonly Amf0Value Null = new(Amf0Type.Null);

        /// <summary>The shared AMF0 <c>undefined</c> value.</summary>
        public static readonly Amf0Value Undefined = new(Amf0Type.Undefined);

        private Amf0Value(Amf0Type type)
        {
            Type = type;
            Properties = NoProperties;
            Items = NoItems;
        }

        /// <summary>Which AMF0 type this is, and therefore which property holds its value.</summary>
        public Amf0Type Type { get; private init; }

        /// <summary>The value of a <see cref="Amf0Type.Number"/>, or the milliseconds since the Unix epoch of a <see cref="Amf0Type.Date"/>.</summary>
        public double Number { get; private init; }

        /// <summary>The value of a <see cref="Amf0Type.Boolean"/>.</summary>
        public bool Boolean { get; private init; }

        /// <summary>The value of a <see cref="Amf0Type.String"/>, <see cref="Amf0Type.LongString"/> or <see cref="Amf0Type.XmlDocument"/>; <c>null</c> for every other type.</summary>
        public string? String { get; private init; }

        /// <summary>The time-zone field of a <see cref="Amf0Type.Date"/> (the specification says it should be 0).</summary>
        public short TimeZone { get; private init; }

        /// <summary>The properties of an <see cref="Amf0Type.Object"/> or <see cref="Amf0Type.EcmaArray"/>, in wire order; empty for every other type.</summary>
        public IReadOnlyList<KeyValuePair<string, Amf0Value>> Properties { get; private init; }

        /// <summary>The items of a <see cref="Amf0Type.StrictArray"/>; empty for every other type.</summary>
        public IReadOnlyList<Amf0Value> Items { get; private init; }

        /// <summary>A <see cref="Amf0Type.Number"/>.</summary>
        public static Amf0Value FromNumber(double value) => new(Amf0Type.Number) { Number = value };

        /// <summary>A <see cref="Amf0Type.Boolean"/>.</summary>
        public static Amf0Value FromBoolean(bool value) => new(Amf0Type.Boolean) { Boolean = value };

        /// <summary>
        /// A <see cref="Amf0Type.String"/>, or a <see cref="Amf0Type.LongString"/> when its UTF-8 form is
        /// longer than the 65,535 bytes a short string's length field holds.
        /// </summary>
        public static Amf0Value FromString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var type = System.Text.Encoding.UTF8.GetByteCount(value) > ushort.MaxValue ? Amf0Type.LongString : Amf0Type.String;
            return new Amf0Value(type) { String = value };
        }

        // A short string is never over 65,535 bytes on the wire, so the reader skips FromString's byte count.
        internal static Amf0Value FromShortString(string value) => new(Amf0Type.String) { String = value };

        /// <summary>A <see cref="Amf0Type.LongString"/>, whatever its length (what the reader returns for a long-string marker).</summary>
        public static Amf0Value FromLongString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return new Amf0Value(Amf0Type.LongString) { String = value };
        }

        /// <summary>An <see cref="Amf0Type.XmlDocument"/>.</summary>
        public static Amf0Value FromXmlDocument(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return new Amf0Value(Amf0Type.XmlDocument) { String = value };
        }

        /// <summary>A <see cref="Amf0Type.Date"/>: milliseconds since the Unix epoch, UTC.</summary>
        public static Amf0Value FromDate(double millisecondsSinceEpoch, short timeZone = 0) =>
            new(Amf0Type.Date) { Number = millisecondsSinceEpoch, TimeZone = timeZone };

        /// <summary>An anonymous <see cref="Amf0Type.Object"/>. The properties are copied, in order.</summary>
        public static Amf0Value FromObject(IEnumerable<KeyValuePair<string, Amf0Value>> properties) =>
            new(Amf0Type.Object) { Properties = CopyProperties(properties) };

        /// <summary>An <see cref="Amf0Type.EcmaArray"/> (an associative array, what <c>onMetaData</c> usually carries). The properties are copied, in order.</summary>
        public static Amf0Value FromEcmaArray(IEnumerable<KeyValuePair<string, Amf0Value>> properties) =>
            new(Amf0Type.EcmaArray) { Properties = CopyProperties(properties) };

        /// <summary>A <see cref="Amf0Type.StrictArray"/>. The items are copied, in order.</summary>
        public static Amf0Value FromStrictArray(IEnumerable<Amf0Value> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            var copy = items.ToArray();
            if (Array.IndexOf(copy, null) >= 0)
            {
                throw new ArgumentException("Items must not contain null", nameof(items));
            }

            return new Amf0Value(Amf0Type.StrictArray) { Items = copy };
        }

        /// <summary>
        /// The value of the first property named <paramref name="name"/> of an object or ECMA array, or
        /// <c>null</c> if there is none (or this value has no properties).
        /// </summary>
        public Amf0Value? GetProperty(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            foreach (var property in Properties)
            {
                if (property.Key == name)
                {
                    return property.Value;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString() => Type switch
        {
            Amf0Type.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Amf0Type.Boolean => Boolean ? "true" : "false",
            Amf0Type.String or Amf0Type.LongString or Amf0Type.XmlDocument => String!,
            Amf0Type.Date => $"Date({Number.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            Amf0Type.Object or Amf0Type.EcmaArray => $"{Type}[{Properties.Count}]",
            Amf0Type.StrictArray => $"StrictArray[{Items.Count}]",
            _ => Type.ToString()
        };

        private static KeyValuePair<string, Amf0Value>[] CopyProperties(IEnumerable<KeyValuePair<string, Amf0Value>> properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            var copy = properties.ToArray();
            foreach (var property in copy)
            {
                if (property.Key is null || property.Value is null)
                {
                    throw new ArgumentException("Property names and values must not be null", nameof(properties));
                }
            }

            return copy;
        }
    }
}
