using EggEncoder.Live.Rtmp.Amf0;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One AMF0 command message (message type 20, Adobe RTMP specification 1.0, section 7.2): a command
    /// name, a transaction id, a command object (an AMF0 object, or null) and any further arguments.
    /// <see cref="Decode"/> reads any command; the static factories build the NetConnection/NetStream
    /// commands publishers and servers exchange (<c>connect</c>, <c>createStream</c>, <c>releaseStream</c>,
    /// <c>FCPublish</c>, <c>publish</c>, <c>play</c>, <c>deleteStream</c>, <c>_result</c>/<c>_error</c>,
    /// <c>onStatus</c>). See docs/live-ingest-restream-plan.md item 3.
    /// </summary>
    public sealed class RtmpCommand
    {
        /// <summary>The chunk stream id commands are sent on by convention.</summary>
        public const int ChunkStreamId = 3;

        /// <summary>The <c>flashVer</c> this library's <see cref="Connect"/> announces, in the form publishers use.</summary>
        public const string DefaultFlashVersion = "FMLE/3.0 (compatible; EggEncoder)";

        /// <summary>The <c>fmsVer</c> <see cref="ConnectResult"/> announces; publishers only check that it is present.</summary>
        public const string DefaultServerVersion = "FMS/3,5,7,7009";

        /// <summary>Creates a command.</summary>
        /// <param name="name">The command name, e.g. <c>connect</c>.</param>
        /// <param name="transactionId">Pairs a request with its <c>_result</c>/<c>_error</c>; 0 for commands that expect no reply.</param>
        /// <param name="commandObject">The command object; <c>null</c> means AMF0 null.</param>
        /// <param name="arguments">The arguments after the command object.</param>
        public RtmpCommand(string name, double transactionId, Amf0Value? commandObject, params Amf0Value[] arguments)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(arguments);
            Name = name;
            TransactionId = transactionId;
            CommandObject = commandObject ?? Amf0Value.Null;
            Arguments = arguments.ToArray();
        }

        /// <summary>The command name.</summary>
        public string Name { get; }

        /// <summary>The transaction id.</summary>
        public double TransactionId { get; }

        /// <summary>The command object, <see cref="Amf0Value.Null"/> when there is none.</summary>
        public Amf0Value CommandObject { get; }

        /// <summary>The arguments after the command object, in order.</summary>
        public IReadOnlyList<Amf0Value> Arguments { get; }

        /// <summary>The argument at <paramref name="index"/> when it is a string; <c>null</c> when missing or another type.</summary>
        public string? GetStringArgument(int index) =>
            index >= 0 && index < Arguments.Count && Arguments[index].Type is Amf0Type.String or Amf0Type.LongString
                ? Arguments[index].String
                : null;

        /// <summary>The argument at <paramref name="index"/> when it is a number; <c>null</c> when missing or another type.</summary>
        public double? GetNumberArgument(int index) =>
            index >= 0 && index < Arguments.Count && Arguments[index].Type == Amf0Type.Number ? Arguments[index].Number : null;

        /// <summary>
        /// The <c>code</c> of the info object a status reply carries (the first argument of
        /// <c>onStatus</c>, <c>_result</c> or <c>_error</c>); <c>null</c> when there is none.
        /// </summary>
        public string? StatusCode =>
            Arguments.Count > 0 && Arguments[0].GetProperty("code") is { Type: Amf0Type.String or Amf0Type.LongString } code ? code.String : null;

        /// <summary>
        /// Decodes an AMF0 command message payload: a string name, then optionally a number transaction
        /// id, a command object and arguments (a payload that stops after the name or the transaction id
        /// reads as transaction id 0 and a null command object).
        /// </summary>
        /// <exception cref="RtmpProtocolException">Malformed AMF0 (including any of <see cref="Amf0Reader"/>'s bounds), a name that isn't a string, or a transaction id that isn't a number.</exception>
        public static RtmpCommand Decode(ReadOnlySpan<byte> payload)
        {
            try
            {
                var reader = new Amf0Reader(payload);
                var name = reader.ReadValue();
                if (name.Type is not (Amf0Type.String or Amf0Type.LongString))
                {
                    throw new RtmpProtocolException($"RTMP command: the name is an AMF0 {name.Type}, not a string");
                }

                var transactionId = 0.0;
                if (!reader.AtEnd)
                {
                    var transaction = reader.ReadValue();
                    if (transaction.Type != Amf0Type.Number)
                    {
                        throw new RtmpProtocolException($"RTMP command '{name.String}': the transaction id is an AMF0 {transaction.Type}, not a number");
                    }

                    transactionId = transaction.Number;
                }

                var commandObject = reader.AtEnd ? Amf0Value.Null : reader.ReadValue();
                var arguments = new List<Amf0Value>();
                while (!reader.AtEnd)
                {
                    if (arguments.Count == Amf0Reader.MaxProperties)
                    {
                        throw new RtmpProtocolException($"RTMP command '{name.String}': more than {Amf0Reader.MaxProperties} arguments");
                    }

                    arguments.Add(reader.ReadValue());
                }

                return new RtmpCommand(name.String!, transactionId, commandObject, [.. arguments]);
            }
            catch (InvalidDataException e)
            {
                throw new RtmpProtocolException($"RTMP command: malformed AMF0 ({e.Message})", e);
            }
        }

        /// <summary>The AMF0 payload: name, transaction id, command object, arguments.</summary>
        public byte[] Encode() =>
            Amf0Writer.Encode([Amf0Value.FromString(Name), Amf0Value.FromNumber(TransactionId), CommandObject, .. Arguments]);

        /// <summary>The command message carrying this command on <paramref name="streamId"/> (0 for NetConnection commands).</summary>
        public RtmpMessage ToMessage(uint streamId = 0, uint timestamp = 0) =>
            new(RtmpMessageType.CommandAmf0, streamId, timestamp, Encode());

        /// <summary>
        /// <c>connect</c> (transaction id 1 by convention) to application <paramref name="app"/>, with
        /// the properties publishers send: <c>app</c>, <c>type</c>, <c>flashVer</c> and <c>tcUrl</c>,
        /// followed by any <paramref name="additionalProperties"/> (e.g. Enhanced RTMP's <c>fourCcList</c>).
        /// </summary>
        public static RtmpCommand Connect(
            double transactionId, string app, string tcUrl, IEnumerable<KeyValuePair<string, Amf0Value>>? additionalProperties = null)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(tcUrl);
            var properties = new List<KeyValuePair<string, Amf0Value>>
            {
                new("app", Amf0Value.FromString(app)),
                new("type", Amf0Value.FromString("nonprivate")),
                new("flashVer", Amf0Value.FromString(DefaultFlashVersion)),
                new("tcUrl", Amf0Value.FromString(tcUrl))
            };
            if (additionalProperties is not null)
            {
                properties.AddRange(additionalProperties);
            }

            return new RtmpCommand("connect", transactionId, Amf0Value.FromObject(properties));
        }

        /// <summary>
        /// The <c>_result</c> accepting a <c>connect</c>: server properties (<c>fmsVer</c>,
        /// <c>capabilities</c>) and an info object with <see cref="RtmpStatusCodes.ConnectSuccess"/>.
        /// </summary>
        public static RtmpCommand ConnectResult(double transactionId) => Result(
            transactionId,
            Amf0Value.FromObject(
            [
                new("fmsVer", Amf0Value.FromString(DefaultServerVersion)),
                new("capabilities", Amf0Value.FromNumber(31))
            ]),
            StatusInfo(RtmpStatusCodes.LevelStatus, RtmpStatusCodes.ConnectSuccess, "Connection succeeded.", objectEncoding: 0));

        /// <summary>A <c>_result</c> reply to transaction <paramref name="transactionId"/>.</summary>
        public static RtmpCommand Result(double transactionId, Amf0Value? commandObject, params Amf0Value[] arguments) =>
            new("_result", transactionId, commandObject, arguments);

        /// <summary>An <c>_error</c> reply to transaction <paramref name="transactionId"/>, with an info object at level <c>error</c>.</summary>
        public static RtmpCommand Error(double transactionId, string code, string description) =>
            new("_error", transactionId, null, StatusInfo(RtmpStatusCodes.LevelError, code, description));

        /// <summary><c>createStream</c>: asks for a message stream id to publish or play on.</summary>
        public static RtmpCommand CreateStream(double transactionId) => new("createStream", transactionId, null);

        /// <summary>The <c>_result</c> of <c>createStream</c>, carrying the new message stream id.</summary>
        public static RtmpCommand CreateStreamResult(double transactionId, uint streamId) =>
            Result(transactionId, null, Amf0Value.FromNumber(streamId));

        /// <summary><c>releaseStream</c>: what publishers send before <c>FCPublish</c> to free a stale publish of the same name.</summary>
        public static RtmpCommand ReleaseStream(double transactionId, string streamName) => NamedStream("releaseStream", transactionId, streamName);

        /// <summary><c>FCPublish</c>: a CDN-style announcement that a publish of <paramref name="streamName"/> is coming.</summary>
        public static RtmpCommand FCPublish(double transactionId, string streamName) => NamedStream("FCPublish", transactionId, streamName);

        /// <summary><c>FCUnpublish</c>: the publisher is about to stop publishing <paramref name="streamName"/>.</summary>
        public static RtmpCommand FCUnpublish(double transactionId, string streamName) => NamedStream("FCUnpublish", transactionId, streamName);

        /// <summary>
        /// <c>publish</c> (sent on the stream <c>createStream</c> returned, transaction id 0) of
        /// <paramref name="streamName"/> (the stream key) with <paramref name="publishingType"/>
        /// (<c>live</c>, <c>record</c> or <c>append</c>).
        /// </summary>
        public static RtmpCommand Publish(string streamName, string publishingType = "live")
        {
            ArgumentNullException.ThrowIfNull(streamName);
            ArgumentNullException.ThrowIfNull(publishingType);
            return new RtmpCommand("publish", 0, null, Amf0Value.FromString(streamName), Amf0Value.FromString(publishingType));
        }

        /// <summary>
        /// <c>play</c> (sent on the stream <c>createStream</c> returned, transaction id 0) of
        /// <paramref name="streamName"/>; <paramref name="start"/> -2 plays live or recorded, -1 live only.
        /// </summary>
        public static RtmpCommand Play(string streamName, double start = -2)
        {
            ArgumentNullException.ThrowIfNull(streamName);
            return new RtmpCommand("play", 0, null, Amf0Value.FromString(streamName), Amf0Value.FromNumber(start));
        }

        /// <summary><c>deleteStream</c>: the client is done with message stream <paramref name="streamId"/>.</summary>
        public static RtmpCommand DeleteStream(double transactionId, uint streamId) =>
            new("deleteStream", transactionId, null, Amf0Value.FromNumber(streamId));

        /// <summary>
        /// <c>onStatus</c> (transaction id 0, null command object) with an info object of
        /// <paramref name="level"/>, <paramref name="code"/> (see <see cref="RtmpStatusCodes"/>) and
        /// <paramref name="description"/>.
        /// </summary>
        public static RtmpCommand OnStatus(string level, string code, string description) =>
            new("onStatus", 0, null, StatusInfo(level, code, description));

        private static RtmpCommand NamedStream(string name, double transactionId, string streamName)
        {
            ArgumentNullException.ThrowIfNull(streamName);
            return new RtmpCommand(name, transactionId, null, Amf0Value.FromString(streamName));
        }

        private static Amf0Value StatusInfo(string level, string code, string description, double? objectEncoding = null)
        {
            ArgumentNullException.ThrowIfNull(level);
            ArgumentNullException.ThrowIfNull(code);
            ArgumentNullException.ThrowIfNull(description);
            var properties = new List<KeyValuePair<string, Amf0Value>>
            {
                new("level", Amf0Value.FromString(level)),
                new("code", Amf0Value.FromString(code)),
                new("description", Amf0Value.FromString(description))
            };
            if (objectEncoding is { } encoding)
            {
                properties.Add(new("objectEncoding", Amf0Value.FromNumber(encoding)));
            }

            return Amf0Value.FromObject(properties);
        }
    }
}
