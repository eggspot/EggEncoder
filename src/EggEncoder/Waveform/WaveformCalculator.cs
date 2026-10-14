namespace EggEncoder.Waveform
{
    public sealed class WaveformCalculator
    {
        public const int DefaultWindowCount = 200;

        // EBU R128 / ITU-R BS.1770-4's own gating-block geometry: 400ms blocks, 75% overlap (a
        // 100ms step -- exactly a quarter of the block, so a block is always exactly 4 steps with
        // no drift, regardless of how that rounds to a whole sample count at a given sample rate).
        private const double LoudnessStepSeconds = 0.1;
        private const int LoudnessStepsPerBlock = 4;
        private const double AbsoluteGateLufs = -70.0;
        private const double RelativeGateOffsetLu = -10.0;

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

        // Null (not an empty array) when no sampleRate was given -- GetIntegratedLoudness can then
        // report "not requested" (null) rather than silently measuring nothing, the same
        // opt-in-via-constructor-parameter shape IsFloatFormat-style capability flags elsewhere in
        // this codebase use.
        private readonly KWeightingFilter[]? _kWeightingFilters;
        private readonly int _samplesPerLoudnessStep;
        private readonly double[]? _currentStepSumOfSquaresPerChannel;
        private readonly Queue<double[]> _recentStepSumsOfSquares = new();
        private readonly List<double> _blockEnergies = [];
        private int _samplesInCurrentLoudnessStep;

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
                _kWeightingFilters = new KWeightingFilter[channels];
                for (var c = 0; c < channels; c++)
                {
                    _kWeightingFilters[c] = new KWeightingFilter(sampleRate.Value);
                }

                _samplesPerLoudnessStep = Math.Max(1, (int)Math.Round(sampleRate.Value * LoudnessStepSeconds));
                _currentStepSumOfSquaresPerChannel = new double[channels];
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

                    if (_kWeightingFilters is not null)
                    {
                        var normalizedSample = sample / (double)_maxAmplitude;
                        var weighted = _kWeightingFilters[channel].Apply(normalizedSample);
                        _currentStepSumOfSquaresPerChannel![channel] += weighted * weighted;
                    }
                }

                _totalSampleCount += _channels;
                _overallPeakAbsoluteSample = Math.Max(_overallPeakAbsoluteSample, framePeak);

                _currentWindowPeak = Math.Max(_currentWindowPeak, framePeak);
                _samplesInCurrentWindow++;

                if (_samplesInCurrentWindow >= _samplesPerWindow)
                {
                    FinalizeCurrentWindow();
                }

                if (_kWeightingFilters is not null)
                {
                    _samplesInCurrentLoudnessStep++;
                    if (_samplesInCurrentLoudnessStep >= _samplesPerLoudnessStep)
                    {
                        FinalizeCurrentLoudnessStep();
                    }
                }
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
        public double? GetIntegratedLoudness()
        {
            if (_kWeightingFilters is null)
            {
                return null;
            }

            if (_samplesInCurrentLoudnessStep > 0)
            {
                FinalizeCurrentLoudnessStep();
            }

            if (_blockEnergies.Count == 0)
            {
                return null;
            }

            var absoluteThresholdEnergy = Math.Pow(10.0, (AbsoluteGateLufs + 0.691) / 10.0);
            var absoluteGated = _blockEnergies.Where(e => e >= absoluteThresholdEnergy).ToList();
            if (absoluteGated.Count == 0)
            {
                return double.NegativeInfinity;
            }

            var relativeThresholdEnergy = absoluteGated.Average() * Math.Pow(10.0, RelativeGateOffsetLu / 10.0);
            var relativeGated = absoluteGated.Where(e => e >= relativeThresholdEnergy).ToList();

            // By construction the mean of absoluteGated is always >= relativeThresholdEnergy
            // (it's 10dB *above* that threshold), so at least one block always survives both
            // gates -- this fallback exists only as a defensive guard, never expected to trigger.
            var gatedMeanEnergy = relativeGated.Count > 0 ? relativeGated.Average() : absoluteGated.Average();

            return (10.0 * Math.Log10(gatedMeanEnergy)) - 0.691;
        }

        private void FinalizeCurrentLoudnessStep()
        {
            // A per-channel mean-square for this one 100ms step -- each channel's own share of
            // _samplesPerLoudnessStep frames, squared and summed by AddBlock above, divided by that
            // same frame count. A trailing partial step (flushed by GetIntegratedLoudness, the same
            // way GetNormalizedWindows flushes its own trailing partial window) divides by however
            // many frames it actually got instead, rather than skewing toward silence.
            var stepMeanSquares = new double[_channels];
            for (var c = 0; c < _channels; c++)
            {
                stepMeanSquares[c] = _currentStepSumOfSquaresPerChannel![c] / _samplesInCurrentLoudnessStep;
            }

            _recentStepSumsOfSquares.Enqueue(stepMeanSquares);
            if (_recentStepSumsOfSquares.Count > LoudnessStepsPerBlock)
            {
                _recentStepSumsOfSquares.Dequeue();
            }

            if (_recentStepSumsOfSquares.Count == LoudnessStepsPerBlock)
            {
                // Channel weight is 1.0 for every channel this project supports (mono/stereo) --
                // BS.1770's own >1.0 weights only apply to surround-specific channel positions
                // this codebase's mono/stereo scope never reaches.
                var blockEnergy = 0.0;
                foreach (var step in _recentStepSumsOfSquares)
                {
                    for (var c = 0; c < _channels; c++)
                    {
                        blockEnergy += step[c] / LoudnessStepsPerBlock;
                    }
                }

                _blockEnergies.Add(blockEnergy);
            }

            Array.Clear(_currentStepSumOfSquaresPerChannel!);
            _samplesInCurrentLoudnessStep = 0;
        }

        private void FinalizeCurrentWindow()
        {
            _windowPeaks.Add((double)_currentWindowPeak / _maxAmplitude);
            _currentWindowPeak = 0;
            _samplesInCurrentWindow = 0;
        }
    }
}
