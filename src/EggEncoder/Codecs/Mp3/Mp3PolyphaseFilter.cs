using System.Numerics;

namespace EggEncoder.Codecs.Mp3
{
    internal sealed class Mp3PolyphaseFilter
    {
        private const int HistoryLength = 512;
        private const int PartialSumCount = 64;
        private const int SubbandCount = 32;
        private const int BufferLength = HistoryLength * 8;
        private const int FoldedCount = 32;

        private static readonly double[] TransposedMatrix = BuildTransposedMatrix();

        private readonly double[] _samples = new double[BufferLength];
        private readonly double[] _partialSums = new double[PartialSumCount];
        private readonly double[] _folded = new double[FoldedCount];
        private readonly double[] _subbandSamples = new double[SubbandCount];
        private int _newest = BufferLength - HistoryLength;

        public double[] Analyze(ReadOnlySpan<int> pcmSamples)
        {
            if (pcmSamples.Length != SubbandCount)
            {
                throw new ArgumentException($"Analyze expects exactly {SubbandCount} PCM samples per call", nameof(pcmSamples));
            }

            if (_newest < SubbandCount)
            {
                Array.Copy(_samples, _newest, _samples, BufferLength - HistoryLength + SubbandCount, HistoryLength - SubbandCount);
                _newest = BufferLength - HistoryLength + SubbandCount;
            }

            _newest -= SubbandCount;
            for (var i = 0; i < SubbandCount; i++)
            {
                _samples[_newest + SubbandCount - 1 - i] = pcmSamples[i];
            }

            PartialSums(_samples.AsSpan(_newest, HistoryLength), Mp3Tables.WindowCoefficients, _partialSums);
            Fold(_partialSums, _folded);
            Matrix(_folded, _subbandSamples);
            return _subbandSamples;
        }

        private static void PartialSums(ReadOnlySpan<double> history, ReadOnlySpan<double> window, Span<double> partialSums)
        {
            var lanes = Vector<double>.Count;
            var i = 0;
            if (Vector.IsHardwareAccelerated && PartialSumCount % lanes == 0)
            {
                for (; i < PartialSumCount; i += lanes)
                {
                    var sum = Vector<double>.Zero;
                    for (var j = 0; j < 8; j++)
                    {
                        var index = i + (PartialSumCount * j);
                        sum += new Vector<double>(history[index..]) * new Vector<double>(window[index..]);
                    }

                    sum.CopyTo(partialSums[i..]);
                }
            }

            for (; i < PartialSumCount; i++)
            {
                var sum = 0.0;
                for (var j = 0; j < 8; j++)
                {
                    var index = i + (PartialSumCount * j);
                    sum += history[index] * window[index];
                }

                partialSums[i] = sum;
            }
        }

        private static void Fold(ReadOnlySpan<double> partialSums, Span<double> folded)
        {
            folded[0] = partialSums[16];
            for (var j = 1; j <= 16; j++)
            {
                folded[j] = partialSums[16 + j] + partialSums[16 - j];
            }

            for (var j = 17; j < FoldedCount; j++)
            {
                folded[j] = partialSums[16 + j] - partialSums[80 - j];
            }
        }

        private static void Matrix(ReadOnlySpan<double> folded, Span<double> subbandSamples)
        {
            var lanes = Vector<double>.Count;
            var k = 0;
            if (Vector.IsHardwareAccelerated && SubbandCount % lanes == 0)
            {
                var divisor = new Vector<double>(32.0);
                for (; k < SubbandCount; k += lanes)
                {
                    var sum = Vector<double>.Zero;
                    for (var j = 0; j < FoldedCount; j++)
                    {
                        sum += new Vector<double>(TransposedMatrix.AsSpan((j * SubbandCount) + k)) * new Vector<double>(folded[j]);
                    }

                    (sum / divisor).CopyTo(subbandSamples[k..]);
                }
            }

            for (; k < SubbandCount; k++)
            {
                var sum = 0.0;
                for (var j = 0; j < FoldedCount; j++)
                {
                    sum += TransposedMatrix[(j * SubbandCount) + k] * folded[j];
                }

                subbandSamples[k] = sum / 32.0;
            }
        }

        private static double[] BuildTransposedMatrix()
        {
            var matrix = new double[FoldedCount * SubbandCount];
            for (var k = 0; k < SubbandCount; k++)
            {
                for (var j = 0; j < FoldedCount; j++)
                {
                    matrix[(j * SubbandCount) + k] = Math.Cos(((2 * k) + 1) * j * Math.PI / 64.0);
                }
            }

            return matrix;
        }
    }
}
