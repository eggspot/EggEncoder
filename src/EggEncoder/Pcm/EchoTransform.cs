namespace EggEncoder.Pcm;

/// <summary>
/// Feedback delay / echo: a classic comb-filter delay line, one per channel. Each output sample is
/// the dry input plus a scaled copy of the input from <c>delaySeconds</c> ago (the "wet" signal,
/// scaled by <c>wetGain</c>); <c>feedback</c> controls how much of each repeat feeds back into the
/// delay line to produce the next, progressively quieter repeat.
///
/// Fixes <c>channels</c> (and <c>sampleRate</c>, to convert <c>delaySeconds</c> into a frame count)
/// at construction, the same way <see cref="BiquadTransform"/>/<see cref="FirFilterTransform"/> do,
/// since the delay line itself is per-channel state sized up front -- unlike the
/// channel-count-agnostic <see cref="CompressorTransform"/>/<see cref="NoiseGateTransform"/>/
/// <see cref="LimiterTransform"/> family, which only ever tracks a single scalar envelope.
///
/// Implements <see cref="Flush"/>: an echo keeps repeating, decaying by <c>feedback</c> each time,
/// for as long as the delay line holds anything -- so the real tail of a stream extends past its
/// last <see cref="Apply"/> call, exactly the kind of lookahead-holding <see cref="Flush"/> exists
/// for (see <see cref="ResamplingTransform"/>, the only other transform here that needs it, for a
/// different reason -- withheld lookahead rather than a decaying tail). The tail drained is a
/// deterministic, finite number of frames: enough repeats for the loudest remaining echo to decay
/// below -60dB, capped at <see cref="MaxTailSeconds"/> so a <c>feedback</c> value very close to (but
/// still under) 1.0 can't produce an unreasonably long tail.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class EchoTransform : IPcmTransform
{
    /// <summary>Upper bound on <c>delaySeconds</c>, keeping the delay line's own memory footprint bounded and the frame-count conversion well within <see cref="int"/> range at any real sample rate.</summary>
    public const double MaxDelaySeconds = 60.0;

    /// <summary>Hard ceiling on the tail <see cref="Flush"/> will ever produce, regardless of how slowly <c>feedback</c> decays.</summary>
    public const double MaxTailSeconds = 30.0;

    private const double DecayThreshold = 0.001; // -60dB: the point at which the loudest remaining repeat is considered inaudible

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _delayFrames;
    private readonly double _feedback;
    private readonly double _wetGain;

    private readonly int[] _delayLine; // circular buffer, interleaved, length == _delayFrames * _channels
    private int _writePosition;
    private int _lastBitsPerSample;

    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="channels">Number of interleaved channels this transform will process (must match actual input).</param>
    /// <param name="delaySeconds">Time between the dry signal and its first echo, in seconds. Must be &gt; 0 and &lt;= <see cref="MaxDelaySeconds"/> (rounds to at least 1 frame).</param>
    /// <param name="feedback">Fraction of each repeat that feeds back into the delay line to produce the next, quieter one. Must be in [0, 1) -- 1.0 or more would never decay, so <see cref="Flush"/> could never finish draining it.</param>
    /// <param name="wetGain">Linear gain applied to each echo before it's added to the dry signal. Must be finite and &gt;= 0.</param>
    public EchoTransform(int sampleRate, int channels, double delaySeconds, double feedback, double wetGain)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive");
        if (double.IsNaN(delaySeconds) || delaySeconds <= 0 || delaySeconds > MaxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds), delaySeconds, $"Delay time must be > 0 and <= {MaxDelaySeconds} seconds");
        if (!double.IsFinite(feedback) || feedback < 0 || feedback >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(feedback), feedback, "Feedback must be a finite number in [0, 1)");
        if (!double.IsFinite(wetGain) || wetGain < 0)
            throw new ArgumentOutOfRangeException(nameof(wetGain), wetGain, "Wet gain must be a finite number >= 0");

        _sampleRate = sampleRate;
        _channels = channels;
        _delayFrames = Math.Max(1, (int)Math.Round(delaySeconds * sampleRate));
        _feedback = feedback;
        _wetGain = wetGain;
        _delayLine = new int[_delayFrames * channels];
    }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;

    // True whenever feedback > 0: Apply() itself always returns exactly the frame count it was
    // given, but Flush() then adds a genuine decaying tail beyond that -- so the total output
    // (Apply + Flush combined) exceeds the total input, and a caller can't predict it from the input
    // count alone. Mirrors ResamplingTransform's own conditional CanChangeFrameCount (_ratio != 1.0)
    // rather than an unconditional true: with feedback == 0, Flush() always returns an empty tail
    // (see Flush below), so total output truly does equal total input in that specific case.
    public bool CanChangeFrameCount => _feedback > 0;

    /// <summary>Reset the delay line back to silence (e.g. to start a new stream).</summary>
    public void Reset()
    {
        Array.Clear(_delayLine);
        _writePosition = 0;
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (sampleRate != _sampleRate)
            throw new ArgumentException($"Expected {_sampleRate} Hz but received {sampleRate} Hz", nameof(sampleRate));
        if (channels != _channels)
            throw new ArgumentException($"Expected {_channels} channels but received {channels}", nameof(channels));

        _lastBitsPerSample = bitsPerSample;

        if (frameCount <= 0)
            return (buffer, frameCount);

        ApplyCore(buffer, frameCount, bitsPerSample);
        return (buffer, frameCount);
    }

    public (int[] buffer, int frameCount) Flush()
    {
        if (_feedback <= 0 || _lastBitsPerSample <= 0)
        {
            // feedback == 0 means every repeat is already silent by the very next frame -- there's
            // no tail to drain. _lastBitsPerSample == 0 means Apply() was never actually called with
            // any real frames (an empty stream), so the delay line never held anything either.
            return (Array.Empty<int>(), 0);
        }

        // long arithmetic here, not int: with feedback close to 1.0 (e.g. 0.999999 -- a realistic
        // request for a long, slowly-decaying tail, not a contrived extreme), repeatsToDecay alone
        // can reach the millions, and multiplying that by a large _delayFrames (a long MaxDelaySeconds
        // at a high sample rate) overflows int32 by several orders of magnitude before Math.Min ever
        // gets a chance to clamp it down to the still-int-safe maxTailFrames.
        var repeatsToDecay = (long)Math.Ceiling(Math.Log(DecayThreshold) / Math.Log(_feedback));
        var maxTailFrames = (long)(MaxTailSeconds * _sampleRate);
        var tailFrames = (int)Math.Min(maxTailFrames, repeatsToDecay * (long)_delayFrames);

        var silence = new int[tailFrames * _channels];
        ApplyCore(silence, tailFrames, _lastBitsPerSample);
        return (silence, tailFrames);
    }

    private void ApplyCore(int[] buffer, int frameCount, int bitsPerSample)
    {
        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var baseIndex = frame * _channels;
            var delayBase = _writePosition * _channels;

            for (var channel = 0; channel < _channels; channel++)
            {
                var delayed = _delayLine[delayBase + channel];
                var input = buffer[baseIndex + channel];

                // Feed the delay line: the fresh input plus a feedback-scaled copy of what's already
                // there -- the classic feedback comb filter.
                _delayLine[delayBase + channel] = (int)Math.Clamp(input + (_feedback * delayed), minValue, maxValue);

                // Output: the dry signal plus the wet (delayed) signal, scaled independently of feedback.
                buffer[baseIndex + channel] = (int)Math.Clamp(input + (_wetGain * delayed), minValue, maxValue);
            }

            _writePosition = (_writePosition + 1) % _delayFrames;
        }
    }
}
