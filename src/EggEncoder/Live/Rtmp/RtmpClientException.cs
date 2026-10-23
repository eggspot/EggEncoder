namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// An RTMP client failed and has closed its connection. <see cref="Reason"/> says why, and
    /// <see cref="StatusCode"/> carries the server's status code when it refused something. The
    /// message never contains the stream key or the URL it came in. Cancelling through a caller's
    /// <see cref="CancellationToken"/> throws <see cref="OperationCanceledException"/> instead.
    /// </summary>
    public sealed class RtmpClientException : IOException
    {
        /// <summary>Creates the exception.</summary>
        public RtmpClientException(RtmpClientFailureReason reason, string message, string? statusCode = null, Exception? innerException = null)
            : base(message, innerException)
        {
            Reason = reason;
            StatusCode = statusCode;
        }

        /// <summary>Why the client stopped.</summary>
        public RtmpClientFailureReason Reason { get; }

        /// <summary>The <c>code</c> of the server's refusal (see <see cref="RtmpStatusCodes"/>), when <see cref="Reason"/> is <see cref="RtmpClientFailureReason.Rejected"/> and the server sent one.</summary>
        public string? StatusCode { get; }
    }
}
