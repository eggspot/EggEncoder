using System.Net;
using EggEncoder.Live.Rtmp.Amf0;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// A publisher's request to publish, as <see cref="RtmpServerOptions.AuthorizePublishAsync"/> sees
    /// it: for <c>rtmp://host:1935/live/abc123</c>, <see cref="AppName"/> is <c>live</c> and
    /// <see cref="StreamKey"/> is <c>abc123</c>. Both have already passed the server's length and
    /// character checks.
    /// </summary>
    /// <remarks>The stream key is a secret: <see cref="ToString"/> leaves it out, and so do the server's exception messages.</remarks>
    public sealed class RtmpPublishRequest
    {
        internal RtmpPublishRequest(
            string appName,
            string streamKey,
            string publishingType,
            EndPoint? remoteEndPoint,
            IReadOnlyList<KeyValuePair<string, Amf0Value>> connectProperties)
        {
            AppName = appName;
            StreamKey = streamKey;
            PublishingType = publishingType;
            RemoteEndPoint = remoteEndPoint;
            ConnectProperties = connectProperties;
        }

        /// <summary>The application name from <c>connect</c> (the first path segment of the URL).</summary>
        public string AppName { get; }

        /// <summary>The stream name from <c>publish</c>: the stream key, exactly as sent (any <c>?query</c> included).</summary>
        public string StreamKey { get; }

        /// <summary>The publishing type from <c>publish</c>: <c>live</c> (what OBS and ffmpeg send), <c>record</c> or <c>append</c>; empty when the publisher sent none.</summary>
        public string PublishingType { get; }

        /// <summary>The publisher's address, when the transport knows it.</summary>
        public EndPoint? RemoteEndPoint { get; }

        /// <summary>The properties of the publisher's <c>connect</c> command object (<c>app</c>, <c>tcUrl</c>, <c>flashVer</c>, ...).</summary>
        public IReadOnlyList<KeyValuePair<string, Amf0Value>> ConnectProperties { get; }

        /// <summary>The <c>flashVer</c> the publisher announced (e.g. <c>FMLE/3.0 (compatible; obs-studio/30.0)</c>), or <c>null</c>.</summary>
        public string? FlashVersion => FindString("flashVer");

        /// <summary>The <c>tcUrl</c> the publisher connected to, or <c>null</c>.</summary>
        public string? TcUrl => FindString("tcUrl");

        /// <inheritdoc />
        public override string ToString() => $"publish of app '{AppName}' from {RemoteEndPoint?.ToString() ?? "an unknown address"}";

        private string? FindString(string name)
        {
            foreach (var (key, value) in ConnectProperties)
            {
                if (key == name)
                {
                    return value.Type is Amf0Type.String or Amf0Type.LongString ? value.String : null;
                }
            }

            return null;
        }
    }
}
