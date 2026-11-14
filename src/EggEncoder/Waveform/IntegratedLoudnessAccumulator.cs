namespace EggEncoder.Waveform
{
    internal sealed class IntegratedLoudnessAccumulator
    {
        private const double StepSeconds = 0.1;
        private const int StepsPerBlock = 4;
        private const double AbsoluteGateLufs = -70.0;
        private const double RelativeGateOffsetLu = -10.0;

        private readonly int _channels;
        private readonly bool _countsPartialFinalStep;
        private readonly KWeightingFilter[] _filters;
        private readonly int _samplesPerStep;
        private readonly double[] _stepSumOfSquares;
        private readonly Queue<double[]> _recentSteps = new();
        private readonly List<double> _blockEnergies = [];
        private int _samplesInStep;

        public IntegratedLoudnessAccumulator(int channels, int sampleRate, bool countsPartialFinalStep)
        {
            _channels = channels;
            _countsPartialFinalStep = countsPartialFinalStep;
            _filters = new KWeightingFilter[channels];
            for (var c = 0; c < channels; c++)
            {
                _filters[c] = new KWeightingFilter(sampleRate);
            }

            _samplesPerStep = Math.Max(1, (int)Math.Round(sampleRate * StepSeconds));
            _stepSumOfSquares = new double[channels];
        }

        public void AddSample(int channel, double normalizedSample)
        {
            var weighted = _filters[channel].Apply(normalizedSample);
            _stepSumOfSquares[channel] += weighted * weighted;
        }

        public void EndFrame()
        {
            _samplesInStep++;
            if (_samplesInStep >= _samplesPerStep)
            {
                FinishStep();
            }
        }

        public double? IntegratedLoudness()
        {
            if (_countsPartialFinalStep && _samplesInStep > 0)
            {
                FinishStep();
            }

            if (_blockEnergies.Count == 0)
            {
                return null;
            }

            var absoluteThreshold = Math.Pow(10.0, (AbsoluteGateLufs + 0.691) / 10.0);
            var absoluteGated = _blockEnergies.Where(e => e >= absoluteThreshold).ToList();
            if (absoluteGated.Count == 0)
            {
                return double.NegativeInfinity;
            }

            var relativeThreshold = absoluteGated.Average() * Math.Pow(10.0, RelativeGateOffsetLu / 10.0);
            var relativeGated = absoluteGated.Where(e => e >= relativeThreshold).ToList();
            var gatedMean = relativeGated.Count > 0 ? relativeGated.Average() : absoluteGated.Average();

            return (10.0 * Math.Log10(gatedMean)) - 0.691;
        }

        private void FinishStep()
        {
            var meanSquares = new double[_channels];
            for (var c = 0; c < _channels; c++)
            {
                meanSquares[c] = _stepSumOfSquares[c] / _samplesInStep;
            }

            _recentSteps.Enqueue(meanSquares);
            if (_recentSteps.Count > StepsPerBlock)
            {
                _recentSteps.Dequeue();
            }

            if (_recentSteps.Count == StepsPerBlock)
            {
                var blockEnergy = 0.0;
                foreach (var step in _recentSteps)
                {
                    for (var c = 0; c < _channels; c++)
                    {
                        blockEnergy += step[c] / StepsPerBlock;
                    }
                }

                _blockEnergies.Add(blockEnergy);
            }

            Array.Clear(_stepSumOfSquares);
            _samplesInStep = 0;
        }
    }
}
