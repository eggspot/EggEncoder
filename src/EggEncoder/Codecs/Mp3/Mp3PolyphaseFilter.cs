namespace EggEncoder.Codecs.Mp3
{
    // The 32-subband polyphase analysis filter shared by Layer I/II/III -- the forward direction of
    // the same prototype window (Mp3Tables.WindowCoefficients) a decoder's own synthesis filter
    // uses in reverse. Maintains a 512-sample sliding history of the most recent PCM input (newest
    // sample at index 0) across calls, consuming 32 new samples and producing 32 new subband
    // samples per call -- the standard cosine-modulated analysis matrixing (window multiply, an
    // 8-way partial sum down to 64 values, then a 32x64 cosine matrix) rather than a duplicate,
    // independently-derived transform.
    internal sealed class Mp3PolyphaseFilter
    {
        private const int HistoryLength = 512;
        private const int PartialSumCount = 64;
        private const int SubbandCount = 32;

        private readonly double[] _history = new double[HistoryLength];
        private readonly double[] _matrix = BuildMatrix();

        public double[] Analyze(ReadOnlySpan<int> pcmSamples)
        {
            if (pcmSamples.Length != SubbandCount)
            {
                throw new ArgumentException($"Analyze expects exactly {SubbandCount} PCM samples per call", nameof(pcmSamples));
            }

            Array.Copy(_history, 0, _history, SubbandCount, HistoryLength - SubbandCount);
            for (var i = 0; i < SubbandCount; i++)
            {
                _history[SubbandCount - 1 - i] = pcmSamples[i];
            }

            var windowed = new double[HistoryLength];
            for (var i = 0; i < HistoryLength; i++)
            {
                windowed[i] = _history[i] * Mp3Tables.WindowCoefficients[i];
            }

            var partialSums = new double[PartialSumCount];
            for (var i = 0; i < PartialSumCount; i++)
            {
                var sum = 0.0;
                for (var j = 0; j < 8; j++)
                {
                    sum += windowed[i + (PartialSumCount * j)];
                }

                partialSums[i] = sum;
            }

            var subbandSamples = new double[SubbandCount];
            for (var k = 0; k < SubbandCount; k++)
            {
                var sum = 0.0;
                for (var i = 0; i < PartialSumCount; i++)
                {
                    sum += _matrix[(k * PartialSumCount) + i] * partialSums[i];
                }

                subbandSamples[k] = sum / 32.0;
            }

            return subbandSamples;
        }

        private static double[] BuildMatrix()
        {
            var matrix = new double[SubbandCount * PartialSumCount];
            for (var k = 0; k < SubbandCount; k++)
            {
                for (var i = 0; i < PartialSumCount; i++)
                {
                    matrix[(k * PartialSumCount) + i] = Math.Cos(((2 * k) + 1) * (i - 16) * Math.PI / 64.0);
                }
            }

            return matrix;
        }
    }
}
