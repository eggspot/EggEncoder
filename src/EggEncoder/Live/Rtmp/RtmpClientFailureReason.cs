namespace EggEncoder.Live.Rtmp
{
    /// <summary>Why an RTMP client stopped: what a caller (such as a restream output's reconnect policy) needs to decide whether to try again.</summary>
    public enum RtmpClientFailureReason
    {
        /// <summary>The TCP connection could not be made: the host didn't resolve, refused or was unreachable.</summary>
        ConnectFailed,

        /// <summary>The TLS handshake of an <c>rtmps://</c> connection failed, typically because the server's certificate wasn't trusted.</summary>
        TlsFailed,

        /// <summary>The server refused the request: an <c>_error</c> reply or an <c>onStatus</c> at level <c>error</c>, such as <see cref="RtmpStatusCodes.PublishBadName"/> for a wrong stream key; see <see cref="RtmpClientException.StatusCode"/>. Retrying with the same settings won't help.</summary>
        Rejected,

        /// <summary>A step took longer than its configured timeout.</summary>
        Timeout,

        /// <summary>The connection broke or the server closed it before the client was done.</summary>
        ConnectionLost,

        /// <summary>The server broke the RTMP protocol or one of the client's bounds on it.</summary>
        ProtocolError
    }
}
