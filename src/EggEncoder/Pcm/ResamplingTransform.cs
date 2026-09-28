using EggEncoder.Codecs;

namespace EggEncoder.Pcm;

/// <summary>
/// Sample-rate conversion using a polyphase, Kaiser-windowed-sinc filter. AOT-safe, managed
/// implementation with no external dependencies (no reflection, no native call).
///
/// <para>
/// <b>Algorithm.</b> Each output sample is a weighted sum of a fixed number of neighboring source
/// samples (<see cref="FilterHalfWidth"/> on each side of the output's fractional source position),
/// weighted by a windowed-sinc lowpass kernel with cutoff at min(source, target) Nyquist -- this both
/// reconstructs the band-limited source signal (for upsampling) and anti-aliases it (for downsampling)
/// before resampling, unlike naive linear interpolation which does neither. The kernel is precomputed
/// once per instance into a polyphase table (<see cref="PhaseCount"/> fractional-position "phases", each
/// holding one full set of tap weights), so resampling itself is a table lookup plus a dot product --
/// no trigonometry or Bessel-function evaluation in the per-sample hot path. When downsampling, the
/// filter's half-width is scaled up by 1/cutoff (capped at <see cref="MaxEffectiveHalfWidth"/>) so the
/// anti-aliasing transition band stays roughly constant relative to the *target* Nyquist rather than
/// narrowing as the ratio shrinks; the cap bounds both the one-time table-build cost and the per-sample
/// convolution cost for extreme downsampling ratios, at the cost of a wider (less sharp) transition band
/// for those ratios than the formula would otherwise ask for.
/// </para>
///
/// <para>
/// <b>Streaming correctness.</b> Unlike a filter that needs the whole signal in memory, this transform
/// only needs <see cref="FilterHalfWidth"/> source frames of lookahead around each output position, so it
/// buffers a small, self-trimming window of source history across <see cref="Apply"/> calls (not the
/// whole stream) rather than processing each block in isolation. An output frame is only ever emitted
/// once its full tap window -- both past and future context -- has actually arrived; frames whose
/// lookahead extends past the current block wait for a later <see cref="Apply"/> call (or
/// <see cref="Flush"/>) instead of falling back to duplicating an edge sample the way a naive per-block
/// implementation would. A frame count is tracked as a cumulative floor(sourceFramesSoFar * ratio) rather
/// than an independent per-block round(), so splitting the same source into any combination of blocks
/// produces the exact same total, converging exactly on <see cref="Flush"/> to round(totalSourceFrames *
/// ratio) -- there is no per-block-boundary rounding drift to document, unlike the earlier
/// linear-interpolation implementation.
/// </para>
///
/// <para>
/// <b>End of stream.</b> Because output is held back until its full tap window has arrived, the very
/// last few output frames of a stream can never gather real "future" context -- there is none. Call
/// <see cref="Flush"/> once, after the last <see cref="Apply"/> call, to drain them; taps that would read
/// past the last real source frame clamp to that last frame instead (the same "hold the edge" fallback
/// the old implementation used for every block boundary, now used only for the true end of the stream).
/// <see cref="PcmTransformPipeline.Flush"/> cascades this through an entire pipeline and
/// <see cref="EggEncoder.Codecs.AudioCutter"/>'s pipeline-aware <c>Convert</c>/<c>Cut</c> overloads call
/// it automatically. Calling <see cref="Apply"/> again after <see cref="Flush"/> throws --
/// once drained, this instance is done.
/// </para>
///
/// <para>
/// <b>Known limitations.</b> (1) The continuous fractional source position is quantized to
/// <see cref="PhaseCount"/> discrete phases for the table lookup; the resulting timing error is a small
/// fraction of a sample period and standard practice for a polyphase resampler, but means this is not a
/// mathematically exact band-limited interpolator. (2) At the very start of a stream, and for the first
/// <see cref="FilterHalfWidth"/> source frames, taps that would read before source frame 0 clamp to
/// frame 0 rather than reading (nonexistent) prior context -- a symmetric edge effect to the end-of-stream
/// behavior above. (3) The downsampling half-width scale-up is capped at
/// <see cref="MaxEffectiveHalfWidth"/> taps, so very large downsampling ratios (e.g. 100:1) get a wider
/// transition band than the ratio would ideally call for, trading some stopband attenuation for bounded
/// memory and CPU cost.
/// </para>
/// </summary>
public sealed class ResamplingTransform : IPcmTransform
{
    /// <summary>Default filter half-width (taps on each side of the output position) at cutoff == 1 (no downsampling scale-up).</summary>
    public const int DefaultFilterHalfWidth = 32;

    /// <summary>Upper bound on the downsampling-scaled effective half-width, to cap table size and per-sample cost for extreme ratios.</summary>
    private const int MaxEffectiveHalfWidth = 256;

    /// <summary>Number of discrete fractional-position phases in the precomputed polyphase table.</summary>
    private const int PhaseCount = 1024;

    /// <summary>Target Kaiser-window stopband attenuation, in dB (Kaiser's empirical design formula).</summary>
    private const double TargetStopbandAttenuationDb = 80.0;

    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly int _channels;
    private readonly double _ratio; // output frames per input frame

    private readonly int _halfWidth;      // effective half-width (post downsampling scale-up), 0 when ratio == 1.0
    private readonly int _kernelLength;   // 2 * _halfWidth
    private readonly double[][] _polyphaseTable = []; // [phase][tap], phase in [0, PhaseCount)

    // Cross-block state: a small, self-trimming window of source history, indexed in absolute
    // (whole-stream) frame numbers so positions never need rebasing between Apply() calls.
    private int[] _history = [];
    private long _historyStartFrame;   // global source-frame index of _history's first frame
    private int _historyFrameCount;
    private long _totalSourceFrames;   // cumulative source frames ever appended, across all Apply() calls
    private long _framesEmitted;       // cumulative output frames produced, across Apply() and Flush()
    private double _nextOutputPos;     // absolute (whole-stream) fractional source position of the next output frame
    private bool _flushed;

    /// <param name="sourceRate">Source sample rate in Hz.</param>
    /// <param name="targetRate">Target sample rate in Hz.</param>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <param name="filterHalfWidth">
    /// Filter half-width in source-sample taps at cutoff == 1 (no downsampling scale-up); see this
    /// type's doc comment. The default (<see cref="DefaultFilterHalfWidth"/>) is a reasonable
    /// medium-quality choice for general-purpose resampling.
    /// </param>
    public ResamplingTransform(int sourceRate, int targetRate, int channels, int filterHalfWidth = DefaultFilterHalfWidth)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate), sourceRate, "Sample rates must be positive");
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate), targetRate, "Sample rates must be positive");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive");
        if (filterHalfWidth <= 0) throw new ArgumentOutOfRangeException(nameof(filterHalfWidth), filterHalfWidth, "Filter half-width must be positive");

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _channels = channels;
        _ratio = (double)targetRate / sourceRate;

        if (_ratio != 1.0)
        {
            var cutoff = Math.Min(1.0, _ratio);
            _halfWidth = cutoff < 1.0
                ? Math.Min(MaxEffectiveHalfWidth, (int)Math.Ceiling(filterHalfWidth / cutoff))
                : filterHalfWidth;
            _kernelLength = 2 * _halfWidth;
            _polyphaseTable = BuildPolyphaseTable(_halfWidth, cutoff, ComputeKaiserBeta(TargetStopbandAttenuationDb));
        }
    }

    /// <summary>The effective filter half-width actually used (after any downsampling scale-up/cap); see this type's doc comment.</summary>
    public int FilterHalfWidth => _halfWidth;

    public int OutputSampleRate => _targetRate;
    public int OutputChannels => _channels;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => _ratio != 1.0;

    /// <summary>Resets all cross-block state (history, stream position, flushed flag). Call at the start of a new stream.</summary>
    public void Reset()
    {
        _history = [];
        _historyStartFrame = 0;
        _historyFrameCount = 0;
        _totalSourceFrames = 0;
        _framesEmitted = 0;
        _nextOutputPos = 0.0;
        _flushed = false;
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (channels != _channels)
            throw new ArgumentException($"Expected {_channels} channels but received {channels}", nameof(channels));
        if (sampleRate != _sourceRate)
            throw new ArgumentException($"Expected {_sourceRate} Hz but received {sampleRate} Hz", nameof(sampleRate));
        if (_ratio == 1.0) return (buffer, frameCount);
        if (_flushed)
            throw new InvalidOperationException($"{nameof(ResamplingTransform)}.{nameof(Apply)} was called after {nameof(Flush)}; this instance is done and cannot accept more input.");
        if (frameCount <= 0) return ([], 0);

        AppendHistory(buffer, frameCount);
        return Produce(isFlush: false);
    }

    public (int[] buffer, int frameCount) Flush()
    {
        if (_ratio == 1.0)
        {
            _flushed = true;
            return ([], 0);
        }

        if (_flushed) return ([], 0);
        _flushed = true;
        return Produce(isFlush: true);
    }

    private void AppendHistory(int[] block, int frameCount)
    {
        var newFrameCount = _historyFrameCount + frameCount;
        var combined = new int[newFrameCount * _channels];
        Array.Copy(_history, 0, combined, 0, _historyFrameCount * _channels);
        Array.Copy(block, 0, combined, _historyFrameCount * _channels, frameCount * _channels);
        _history = combined;
        _historyFrameCount = newFrameCount;
        _totalSourceFrames += frameCount;
    }

    private (int[] buffer, int frameCount) Produce(bool isFlush)
    {
        var lastAvailableGlobalIdx = _totalSourceFrames - 1;
        var targetTotalFrames = isFlush ? (long)Math.Round(_totalSourceFrames * _ratio) : long.MaxValue;
        var outputs = new List<int>();

        while (true)
        {
            if (isFlush && _framesEmitted >= targetTotalFrames) break;

            var basePos = Math.Floor(_nextOutputPos);
            var lastNeededIdx = (long)basePos + _halfWidth;

            if (!isFlush && lastNeededIdx > lastAvailableGlobalIdx) break;
            if (isFlush && lastAvailableGlobalIdx < 0) break; // nothing was ever appended; nothing to flush

            var frac = _nextOutputPos - basePos;
            var phase = Math.Min(PhaseCount - 1, (int)Math.Round(frac * PhaseCount));
            var weights = _polyphaseTable[phase];

            for (var ch = 0; ch < _channels; ch++)
            {
                var acc = 0.0;
                for (var k = 0; k < _kernelLength; k++)
                {
                    var srcIdx = (long)basePos + (k - _halfWidth + 1);
                    if (srcIdx < 0) srcIdx = 0;
                    else if (isFlush && srcIdx > lastAvailableGlobalIdx) srcIdx = lastAvailableGlobalIdx;

                    var localIdx = srcIdx - _historyStartFrame;
                    acc += _history[(localIdx * _channels) + ch] * weights[k];
                }

                outputs.Add((int)Math.Clamp(Math.Round(acc), int.MinValue, int.MaxValue));
            }

            _nextOutputPos += 1.0 / _ratio;
            _framesEmitted++;
        }

        TrimHistory();

        return ([.. outputs], outputs.Count / _channels);
    }

    // Drops source history strictly before what any future output frame could still need, bounding
    // memory to O(FilterHalfWidth) regardless of stream length.
    private void TrimHistory()
    {
        var keepFromGlobalIdx = (long)Math.Floor(_nextOutputPos) - _halfWidth + 1;
        if (keepFromGlobalIdx <= _historyStartFrame) return;

        var dropFrames = (int)Math.Min(keepFromGlobalIdx - _historyStartFrame, _historyFrameCount);
        if (dropFrames <= 0) return;

        var remaining = _historyFrameCount - dropFrames;
        var trimmed = new int[remaining * _channels];
        Array.Copy(_history, dropFrames * _channels, trimmed, 0, remaining * _channels);
        _history = trimmed;
        _historyStartFrame += dropFrames;
        _historyFrameCount = remaining;
    }

    private static double[][] BuildPolyphaseTable(int halfWidth, double cutoff, double kaiserBeta)
    {
        var kernelLength = 2 * halfWidth;
        var table = new double[PhaseCount][];

        for (var phase = 0; phase < PhaseCount; phase++)
        {
            var frac = (double)phase / PhaseCount;
            var row = new double[kernelLength];
            var sum = 0.0;

            for (var k = 0; k < kernelLength; k++)
            {
                var offset = k - halfWidth + 1;
                var x = offset - frac;
                var weight = Kernel(x, cutoff, halfWidth, kaiserBeta);
                row[k] = weight;
                sum += weight;
            }

            // Rescale to exact unity DC gain: corrects the small ripple that a finite-length window
            // otherwise leaves in the passband.
            if (Math.Abs(sum) > 1e-12)
            {
                for (var k = 0; k < kernelLength; k++) row[k] /= sum;
            }

            table[phase] = row;
        }

        return table;
    }

    // Windowed-sinc lowpass kernel: h(x) = cutoff * sinc(cutoff * x), the discrete-time lowpass filter
    // with cutoff frequency (cutoff / 2) cycles/sample, windowed by a Kaiser window over [-halfWidth, halfWidth].
    private static double Kernel(double x, double cutoff, int halfWidth, double kaiserBeta)
    {
        var window = KaiserWindow(x / halfWidth, kaiserBeta);
        if (window == 0.0) return 0.0;

        if (Math.Abs(x) < 1e-9) return cutoff * window;

        var piX = Math.PI * x;
        var sinc = Math.Sin(piX * cutoff) / piX;
        return sinc * window;
    }

    private static double KaiserWindow(double xNormalized, double beta)
    {
        if (xNormalized <= -1.0 || xNormalized >= 1.0) return 0.0;

        var arg = beta * Math.Sqrt(1.0 - (xNormalized * xNormalized));
        return BesselI0(arg) / BesselI0(beta);
    }

    // Modified Bessel function of the first kind, order 0, via its power series -- sufficient precision
    // for filter-design purposes (the series converges rapidly for the beta values Kaiser windows use).
    private static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var quarterXSquared = x * x / 4.0;

        for (var k = 1; k <= 50; k++)
        {
            term *= quarterXSquared / (k * k);
            sum += term;
            if (term < sum * 1e-16) break;
        }

        return sum;
    }

    // Kaiser's empirical design formula relating a target stopband attenuation to the window's beta
    // parameter (Oppenheim & Schafer, "Discrete-Time Signal Processing").
    private static double ComputeKaiserBeta(double stopbandAttenuationDb)
    {
        if (stopbandAttenuationDb > 50.0) return 0.1102 * (stopbandAttenuationDb - 8.7);
        if (stopbandAttenuationDb >= 21.0) return (0.5842 * Math.Pow(stopbandAttenuationDb - 21.0, 0.4)) + (0.07886 * (stopbandAttenuationDb - 21.0));
        return 0.0;
    }
}
