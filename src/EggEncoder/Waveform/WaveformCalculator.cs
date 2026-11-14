namespace EggEncoder.Waveform
{
    public sealed class WaveformCalculator
    {
        public const int DefaultWindowCount = 200;

        private readonly int _channels;
        private readonly long _maxAmplitude;
        private readonly long _samplesPerWindow;
        private readonly List<double> _windowPeaks = [];

        private long _currentWindowPeak;
        private long _samplesInCurrentWindow;

        // Tracked directly per-sample here (not derived from _windowPeaks afterward) so both are
        // always fully up to date after any AddBlock call, independent of window finalization --
        // GetNormalizedPeakAmplitude/GetNormalizedRmsLevel need no flushing step, unlike
        // GetNormalizedWindows' own pending-window flush.
        private long _overallPeakAbsoluteSample;
        private double _sumOfSquares;
        private long _totalSampleCount;

        private readonly IntegratedLoudnessAccumulator? _loudness;

        /// <param name="totalSamplesPerChannel">Total frame count this instance will eventually see, used only to size peak-window granularity.</param>
        /// <param name="channels">Interleaved channel count every <see cref="AddBlock"/> call must be a whole multiple of.</param>
        /// <param name="bitsPerSample">Native bit depth of the samples being added (1-32), used to normalize peak/RMS to 0.0-1.0.</param>
        /// <param name="windowCount">How many peak windows <see cref="GetNormalizedWindows"/> should divide the signal into.</param>
        /// <param name="sampleRate">
        /// When given, also enables <see cref="GetIntegratedLoudness"/> (ITU-R BS.1770-4 / EBU R128
        /// integrated loudness, in LUFS) -- omitted (the default), that method always returns
        /// <c>null</c> rather than silently measuring at a wrong, guessed sample rate.
        /// </param>
        public WaveformCalculator(long totalSamplesPerChannel, int channels, int bitsPerSample, int windowCount = DefaultWindowCount, int? sampleRate = null)
        {
            if (bitsPerSample is < 1 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Bits per sample must be between 1 and 32");
            }

            _channels = channels;

            // 1L, not 1 -- for bitsPerSample=32 (the one genuinely reachable case, since the
            // constructor's own check above caps bitsPerSample at 32), 1 << 31 as a 32-bit int
            // overflows to int.MinValue (a negative denominator every normalization below would
            // then silently divide by), since the true value 2147483648 doesn't fit in Int32 at
            // all. Promoting to long first computes the correct value with room to spare.
            _maxAmplitude = 1L << (bitsPerSample - 1);
            _samplesPerWindow = Math.Max(1, totalSamplesPerChannel / windowCount);

            if (sampleRate is > 0)
            {
                _loudness = new IntegratedLoudnessAccumulator(channels, sampleRate.Value, countsPartialFinalStep: true);
            }
        }

        public void AddBlock(ReadOnlySpan<int> interleavedSamples)
        {
            if (interleavedSamples.Length % _channels != 0)
            {
                throw new ArgumentException($"Block length {interleavedSamples.Length} is not a whole number of {_channels}-channel frames", nameof(interleavedSamples));
            }

            for (var frameStart = 0; frameStart < interleavedSamples.Length; frameStart += _channels)
            {
                long framePeak = 0;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var sample = interleavedSamples[frameStart + channel];

                    // Promoted to long before Math.Abs: a 32-bit source can legitimately decode a
                    // sample of exactly int.MinValue (e.g. raw bytes 00 00 00 80 in a 32-bit PCM
                    // WAV), and Math.Abs(int.MinValue) throws OverflowException since -int.MinValue
                    // (2147483648) doesn't fit back into int32 -- confirmed by actually triggering
                    // it against this method before this fix, not assumed. Math.Abs(long) has no
                    // such edge case here since 2147483648 fits comfortably in int64.
                    var absSample = Math.Abs((long)sample);
                    framePeak = Math.Max(framePeak, absSample);

                    _sumOfSquares += (double)sample * sample;

                    _loudness?.AddSample(channel, sample / (double)_maxAmplitude);
                }

                _totalSampleCount += _channels;
                _overallPeakAbsoluteSample = Math.Max(_overallPeakAbsoluteSample, framePeak);

                _currentWindowPeak = Math.Max(_currentWindowPeak, framePeak);
                _samplesInCurrentWindow++;

                if (_samplesInCurrentWindow >= _samplesPerWindow)
                {
                    FinalizeCurrentWindow();
                }

                _loudness?.EndFrame();
            }
        }

        // Total samples aren't always evenly divisible by the window count, so the trailing
        // partial window (flushed here) can push the result one window past DefaultWindowCount.
        public List<double> GetNormalizedWindows()
        {
            if (_samplesInCurrentWindow > 0)
            {
                FinalizeCurrentWindow();
            }

            return _windowPeaks;
        }

        // The single loudest absolute sample value across every channel and every block seen so
        // far, normalized to this calculator's own bit-depth scale (0.0..1.0) -- equal to (not
        // derived from) the maximum any one of GetNormalizedWindows' own per-window peaks could
        // ever report, since both ultimately bottom out at the same per-frame peak computed above.
        public double GetNormalizedPeakAmplitude() => (double)_overallPeakAbsoluteSample / _maxAmplitude;

        // Root-mean-square level across every individual sample (every channel, not just each
        // frame's own loudest channel) seen so far, normalized the same way GetNormalizedPeakAmplitude
        // is. 0.0 before any block has been added, rather than the NaN a 0/0 division would produce.
        public double GetNormalizedRmsLevel()
        {
            if (_totalSampleCount == 0)
            {
                return 0.0;
            }

            var rootMeanSquare = Math.Sqrt(_sumOfSquares / _totalSampleCount);
            return rootMeanSquare / _maxAmplitude;
        }

        /// <summary>
        /// Integrated loudness (ITU-R BS.1770-4 / EBU R128), in LUFS, across every block seen so
        /// far -- <c>null</c> if this instance's constructor wasn't given a <c>sampleRate</c>
        /// (loudness was never requested), or if fewer than 400ms of audio was ever added (too
        /// short to form even one gating block). A real measurement can still legitimately be
        /// <see cref="double.NegativeInfinity"/> for audio that's entirely at or below the
        /// standard's own -70 LUFS absolute silence gate -- that's a true result, not a sentinel
        /// for "couldn't measure", so it's returned as-is rather than folded into the null case.
        /// Includes any pending partial step the same way <see cref="GetNormalizedWindows"/>
        /// flushes its own trailing partial window, so this can be called at any point, not only
        /// after a final full 100ms step.
        /// </summary>
        public double? GetIntegratedLoudness() => _loudness?.IntegratedLoudness();

        private void FinalizeCurrentWindow()
        {
            _windowPeaks.Add((double)_currentWindowPeak / _maxAmplitude);
            _currentWindowPeak = 0;
            _samplesInCurrentWindow = 0;
        }
    }
}
