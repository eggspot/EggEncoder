using System.Numerics;

namespace EggEncoder.Codecs.Mp3
{
    internal sealed class Mp3PolyphaseFilter
    {
        internal const int SubbandCount = 32;

        private const int HistoryLength = 512;
        private const int PartialSumCount = 64;
        private const int BufferLength = HistoryLength * 8;
        private const int FoldedCount = 32;

        private static readonly float[] TransposedMatrix = BuildTransposedMatrix();
        private static readonly float[] Window = Array.ConvertAll(Mp3Tables.WindowCoefficients, value => (float)value);

        private readonly float[] _samples = new float[BufferLength];
        private readonly float[] _partialSums = new float[PartialSumCount];
        private readonly float[] _folded = new float[FoldedCount];
        private int _newest = BufferLength - HistoryLength;

        public void Analyze(ReadOnlySpan<int> pcmSamples, int stride, Span<float> subbandSamples)
        {
            if (pcmSamples.Length < ((SubbandCount - 1) * stride) + 1 || subbandSamples.Length < SubbandCount)
            {
                throw new ArgumentException($"Analyze expects {SubbandCount} PCM samples and room for {SubbandCount} subband samples", nameof(pcmSamples));
            }

            if (_newest < SubbandCount)
            {
                Array.Copy(_samples, _newest, _samples, BufferLength - HistoryLength + SubbandCount, HistoryLength - SubbandCount);
                _newest = BufferLength - HistoryLength + SubbandCount;
            }

            _newest -= SubbandCount;
            for (var i = 0; i < SubbandCount; i++)
            {
                _samples[_newest + SubbandCount - 1 - i] = pcmSamples[i * stride];
            }

            PartialSums(_samples.AsSpan(_newest, HistoryLength), _partialSums);
            Fold(_partialSums, _folded);
            Matrix(_folded, subbandSamples);
        }

        private static void PartialSums(ReadOnlySpan<float> history, Span<float> partialSums)
        {
            var lanes = Vector<float>.Count;
            for (var i = 0; i < PartialSumCount; i += lanes)
            {
                var sum = Vector<float>.Zero;
                for (var j = 0; j < 8; j++)
                {
                    var index = i + (PartialSumCount * j);
                    sum += new Vector<float>(history[index..]) * new Vector<float>(Window.AsSpan(index));
                }

                sum.CopyTo(partialSums[i..]);
            }
        }

        private static void Fold(ReadOnlySpan<float> partialSums, Span<float> folded)
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

        private static void Matrix(ReadOnlySpan<float> folded, Span<float> subbandSamples)
        {
            var lanes = Vector<float>.Count;
            for (var k = 0; k < SubbandCount; k += lanes)
            {
                var sum = Vector<float>.Zero;
                for (var j = 0; j < FoldedCount; j++)
                {
                    sum += new Vector<float>(TransposedMatrix.AsSpan((j * SubbandCount) + k)) * new Vector<float>(folded[j]);
                }

                sum.CopyTo(subbandSamples[k..]);
            }
        }

        private static float[] BuildTransposedMatrix()
        {
            var matrix = new float[FoldedCount * SubbandCount];
            for (var k = 0; k < SubbandCount; k++)
            {
                for (var j = 0; j < FoldedCount; j++)
                {
                    matrix[(j * SubbandCount) + k] = (float)(Math.Cos(((2 * k) + 1) * j * Math.PI / 64.0) / 32);
                }
            }

            return matrix;
        }
    }
}
