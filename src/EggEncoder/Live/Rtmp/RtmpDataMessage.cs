using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp.Amf0;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One AMF0 data message (message type 18, Adobe RTMP specification 1.0, section 7.1.2): a handler
    /// name and values. Publishers send stream metadata as <c>@setDataFrame</c>, <c>onMetaData</c>, then
    /// an ECMA array of properties (the same AMF0 an FLV script tag carries); <see cref="SetDataFrame"/>
    /// builds that and <see cref="TryGetMetadata"/> reads it, with or without the <c>@setDataFrame</c>
    /// wrapper. See docs/live-ingest-restream-plan.md item 3.
    /// </summary>
    public sealed class RtmpDataMessage
    {
        /// <summary>The handler name a publisher wraps its metadata in, telling the server to keep it for players.</summary>
        public const string SetDataFrameHandler = "@setDataFrame";

        /// <summary>The handler name of stream metadata.</summary>
        public const string OnMetaDataHandler = "onMetaData";

        /// <summary>Creates a data message.</summary>
        public RtmpDataMessage(string name, params Amf0Value[] values)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(values);
            Name = name;
            Values = values.ToArray();
        }

        /// <summary>The handler name.</summary>
        public string Name { get; }

        /// <summary>The values after the name, in order.</summary>
        public IReadOnlyList<Amf0Value> Values { get; }

        /// <summary>
        /// <c>@setDataFrame</c>, <c>onMetaData</c>, then <paramref name="metadata"/>'s properties as an
        /// ECMA array: what a publisher sends after <c>publish</c> succeeds.
        /// </summary>
        public static RtmpDataMessage SetDataFrame(FlvMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            return new RtmpDataMessage(SetDataFrameHandler, Amf0Value.FromString(OnMetaDataHandler), Amf0Value.FromEcmaArray(metadata.Properties));
        }

        /// <summary>Decodes a data message payload: a string name, then every value up to the end.</summary>
        /// <exception cref="RtmpProtocolException">Malformed AMF0 (including any of <see cref="Amf0Reader"/>'s bounds), or a name that isn't a string.</exception>
        public static RtmpDataMessage Decode(ReadOnlySpan<byte> payload)
        {
            try
            {
                var reader = new Amf0Reader(payload);
                var name = reader.ReadValue();
                if (name.Type is not (Amf0Type.String or Amf0Type.LongString))
                {
                    throw new RtmpProtocolException($"RTMP data message: the name is an AMF0 {name.Type}, not a string");
                }

                var values = new List<Amf0Value>();
                while (!reader.AtEnd)
                {
                    if (values.Count == Amf0Reader.MaxProperties)
                    {
                        throw new RtmpProtocolException($"RTMP data message '{name.String}': more than {Amf0Reader.MaxProperties} values");
                    }

                    values.Add(reader.ReadValue());
                }

                return new RtmpDataMessage(name.String!, [.. values]);
            }
            catch (InvalidDataException e)
            {
                throw new RtmpProtocolException($"RTMP data message: malformed AMF0 ({e.Message})", e);
            }
        }

        /// <summary>The AMF0 payload: name, then values.</summary>
        public byte[] Encode() => Amf0Writer.Encode([Amf0Value.FromString(Name), .. Values]);

        /// <summary>The data message carrying this on <paramref name="streamId"/>.</summary>
        public RtmpMessage ToMessage(uint streamId, uint timestamp = 0) => new(RtmpMessageType.DataAmf0, streamId, timestamp, Encode());

        /// <summary>
        /// The stream metadata this message carries: <c>onMetaData</c> followed by an ECMA array or
        /// object, optionally wrapped in <c>@setDataFrame</c>; <c>null</c> for any other message.
        /// </summary>
        public FlvMetadata? TryGetMetadata()
        {
            var values = Values;
            if (Name == SetDataFrameHandler)
            {
                if (values.Count == 0 || values[0].String != OnMetaDataHandler)
                {
                    return null;
                }

                values = values.Skip(1).ToArray();
            }
            else if (Name != OnMetaDataHandler)
            {
                return null;
            }

            return values.Count > 0 && values[0].Type is Amf0Type.EcmaArray or Amf0Type.Object
                ? new FlvMetadata(values[0].Properties)
                : null;
        }
    }
}
