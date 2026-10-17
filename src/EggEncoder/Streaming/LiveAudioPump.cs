namespace EggEncoder.Streaming
{
    /// <summary>
    /// Feeds PCM from a genuinely live source (a microphone callback, an incoming network stream)
    /// into an <see cref="IStreamingEncoder"/> on the source's own schedule, rather than in neat,
    /// exactly-on-time blocks. <see cref="PushSamples"/>'s optional <c>captureTimestamp</c> (see
    /// its own doc comment) lets a <see cref="DriftPolicy.SilenceFill"/> pump detect and close a
    /// real gap in arrival; with no timestamp supplied at all, this is a pure, zero-overhead
    /// pass-through regardless of policy. See docs/streaming-plan.md item 6.
    /// </summary>
    public sealed class LiveAudioPump
    {
        private readonly IStreamingEncoder _encoder;
        private readonly DriftPolicy _driftPolicy;
        private readonly double _silenceFillThresholdSeconds;

        private DateTimeOffset? _firstCaptureTimestamp;
        private long _actualFrameCount;

        /// <param name="encoder">Not owned/disposed by this pump -- the caller still owns its lifetime.</param>
        /// <param name="driftPolicy">Defaults to <see cref="DriftPolicy.Ignore"/>.</param>
        /// <param name="silenceFillThresholdSeconds">
        /// How far actual-so-far must fall behind wall-clock-implied-so-far, in seconds, before
        /// <see cref="DriftPolicy.SilenceFill"/> treats it as a real gap rather than ordinary
        /// scheduling jitter. Ignored under <see cref="DriftPolicy.Ignore"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="silenceFillThresholdSeconds"/> is not positive.</exception>
        public LiveAudioPump(IStreamingEncoder encoder, DriftPolicy driftPolicy = DriftPolicy.Ignore, double silenceFillThresholdSeconds = 0.1)
        {
            ArgumentNullException.ThrowIfNull(encoder);
            if (silenceFillThresholdSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(silenceFillThresholdSeconds), "Threshold must be positive.");
            }

            _encoder = encoder;
            _driftPolicy = driftPolicy;
            _silenceFillThresholdSeconds = silenceFillThresholdSeconds;
        }

        /// <param name="interleavedSamples">Forwarded to the wrapped encoder's own <see cref="IStreamingEncoder.Write"/>.</param>
        /// <param name="captureTimestamp">
        /// When the real source captured <paramref name="interleavedSamples"/>. Required for
        /// <see cref="DriftPolicy.SilenceFill"/> to do anything at all -- omitting it (the default)
        /// means this call is pure pass-through, under either policy, since drift detection needs a
        /// real clock to compare against and silently inventing one would be worse than not
        /// detecting drift at all. The very first call with a timestamp establishes the pump's own
        /// reference point; no gap is ever measured against that first call.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="interleavedSamples"/>'s length is not a whole multiple of <see cref="IStreamingEncoder.Channels"/>.</exception>
        public void PushSamples(ReadOnlySpan<int> interleavedSamples, DateTimeOffset? captureTimestamp = null)
        {
            var channels = _encoder.Channels;
            if (interleavedSamples.Length % channels != 0)
            {
                throw new ArgumentException($"Interleaved sample count ({interleavedSamples.Length}) must be a whole multiple of the channel count ({channels}).", nameof(interleavedSamples));
            }

            if (captureTimestamp is { } timestamp && _driftPolicy == DriftPolicy.SilenceFill)
            {
                if (_firstCaptureTimestamp is null)
                {
                    _firstCaptureTimestamp = timestamp;
                }
                else
                {
                    FillGapIfAny(timestamp, channels);
                }
            }

            _encoder.Write(interleavedSamples);
            _actualFrameCount += interleavedSamples.Length / channels;
        }

        private void FillGapIfAny(DateTimeOffset captureTimestamp, int channels)
        {
            var elapsedSeconds = (captureTimestamp - _firstCaptureTimestamp!.Value).TotalSeconds;
            var expectedFrameCount = (long)(elapsedSeconds * _encoder.SampleRate);
            var gapFrames = expectedFrameCount - _actualFrameCount;
            var gapSeconds = gapFrames / (double)_encoder.SampleRate;

            if (gapSeconds <= _silenceFillThresholdSeconds)
            {
                return;
            }

            _encoder.Write(new int[gapFrames * channels]);
            _actualFrameCount += gapFrames;
        }
    }
}
