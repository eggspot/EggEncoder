namespace EggEncoder.Waveform
{
    public sealed class WaveformCalculator
    {
        public const int DefaultWindowCount = 200;

        private readonly int _channels;
        private readonly int _maxAmplitude;
        private readonly long _samplesPerWindow;
        private readonly List<double> _windowPeaks = [];

        private int _currentWindowPeak;
        private long _samplesInCurrentWindow;

        public WaveformCalculator(long totalSamplesPerChannel, int channels, int bitsPerSample, int windowCount = DefaultWindowCount)
        {
            if (bitsPerSample is < 1 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Bits per sample must be between 1 and 32");
            }

            _channels = channels;
            _maxAmplitude = 1 << (bitsPerSample - 1);
            _samplesPerWindow = Math.Max(1, totalSamplesPerChannel / windowCount);
        }

        public void AddBlock(ReadOnlySpan<int> interleavedSamples)
        {
            if (interleavedSamples.Length % _channels != 0)
            {
                throw new ArgumentException($"Block length {interleavedSamples.Length} is not a whole number of {_channels}-channel frames", nameof(interleavedSamples));
            }

            for (var frameStart = 0; frameStart < interleavedSamples.Length; frameStart += _channels)
            {
                var framePeak = 0;
                for (var channel = 0; channel < _channels; channel++)
                {
                    framePeak = Math.Max(framePeak, Math.Abs(interleavedSamples[frameStart + channel]));
                }

                _currentWindowPeak = Math.Max(_currentWindowPeak, framePeak);
                _samplesInCurrentWindow++;

                if (_samplesInCurrentWindow >= _samplesPerWindow)
                {
                    FinalizeCurrentWindow();
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

        private void FinalizeCurrentWindow()
        {
            _windowPeaks.Add((double)_currentWindowPeak / _maxAmplitude);
            _currentWindowPeak = 0;
            _samplesInCurrentWindow = 0;
        }
    }
}
