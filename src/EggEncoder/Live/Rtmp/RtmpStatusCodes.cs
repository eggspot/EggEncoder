namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// The standard <c>code</c> strings of the info objects in <c>_result</c>, <c>_error</c> and
    /// <c>onStatus</c> commands (NetConnection and NetStream status codes), and their <c>level</c> values.
    /// </summary>
    public static class RtmpStatusCodes
    {
        /// <summary>The <c>level</c> of a successful or informational status.</summary>
        public const string LevelStatus = "status";

        /// <summary>The <c>level</c> of a failed request.</summary>
        public const string LevelError = "error";

        /// <summary><c>connect</c> succeeded.</summary>
        public const string ConnectSuccess = "NetConnection.Connect.Success";

        /// <summary><c>connect</c> was refused (unknown application, not authorized).</summary>
        public const string ConnectRejected = "NetConnection.Connect.Rejected";

        /// <summary><c>publish</c> was accepted; media may follow.</summary>
        public const string PublishStart = "NetStream.Publish.Start";

        /// <summary><c>publish</c> was refused: the stream name is unauthorized, invalid or already being published.</summary>
        public const string PublishBadName = "NetStream.Publish.BadName";

        /// <summary>A publish ended.</summary>
        public const string UnpublishSuccess = "NetStream.Unpublish.Success";

        /// <summary>A call the server doesn't support (the <c>_error</c> of an unknown command).</summary>
        public const string CallFailed = "NetConnection.Call.Failed";

        /// <summary><c>play</c> failed (an ingest-only server refuses every <c>play</c>).</summary>
        public const string PlayFailed = "NetStream.Play.Failed";

        /// <summary><c>play</c> started.</summary>
        public const string PlayStart = "NetStream.Play.Start";

        /// <summary><c>play</c> reset the stream (sent before <see cref="PlayStart"/>).</summary>
        public const string PlayReset = "NetStream.Play.Reset";

        /// <summary><c>play</c> named a stream that doesn't exist.</summary>
        public const string PlayStreamNotFound = "NetStream.Play.StreamNotFound";
    }
}
