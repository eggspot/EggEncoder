using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            ref var newest = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_samples), _newest + SubbandCount - 1);
            ref var pcm = ref MemoryMarshal.GetReference(pcmSamples);
            for (var i = 0; i < SubbandCount; i++)
            {
                Unsafe.Subtract(ref newest, i) = Unsafe.Add(ref pcm, i * stride);
            }

            PartialSums(_samples.AsSpan(_newest, HistoryLength), _partialSums);
            Fold(_partialSums, _folded);
            Matrix(_folded, subbandSamples);
        }

        private static void PartialSums(ReadOnlySpan<float> history, Span<float> partialSums)
        {
            var lanes = Vector<float>.Count;
            ref var historyStart = ref MemoryMarshal.GetReference(history);
            ref var windowStart = ref MemoryMarshal.GetArrayDataReference(Window);
            ref var output = ref MemoryMarshal.GetReference(partialSums);
            if (lanes == 8)
            {
                PartialSumsEightLanes(ref historyStart, ref windowStart, ref output);
                return;
            }

            for (var i = 0; i < PartialSumCount; i += lanes)
            {
                var sum = Vector<float>.Zero;
                for (nuint j = 0; j < 8; j++)
                {
                    var index = (nuint)i + (PartialSumCount * j);
                    sum += Vector.LoadUnsafe(ref historyStart, index) * Vector.LoadUnsafe(ref windowStart, index);
                }

                sum.StoreUnsafe(ref output, (nuint)i);
            }
        }

        private static void PartialSumsEightLanes(ref float history, ref float window, ref float output)
        {
            var sum0 = Vector<float>.Zero;
            var sum1 = Vector<float>.Zero;
            var sum2 = Vector<float>.Zero;
            var sum3 = Vector<float>.Zero;
            var sum4 = Vector<float>.Zero;
            var sum5 = Vector<float>.Zero;
            var sum6 = Vector<float>.Zero;
            var sum7 = Vector<float>.Zero;
            for (nuint j = 0; j < 8; j++)
            {
                var row = PartialSumCount * j;
                sum0 += Vector.LoadUnsafe(ref history, row) * Vector.LoadUnsafe(ref window, row);
                sum1 += Vector.LoadUnsafe(ref history, row + 8) * Vector.LoadUnsafe(ref window, row + 8);
                sum2 += Vector.LoadUnsafe(ref history, row + 16) * Vector.LoadUnsafe(ref window, row + 16);
                sum3 += Vector.LoadUnsafe(ref history, row + 24) * Vector.LoadUnsafe(ref window, row + 24);
                sum4 += Vector.LoadUnsafe(ref history, row + 32) * Vector.LoadUnsafe(ref window, row + 32);
                sum5 += Vector.LoadUnsafe(ref history, row + 40) * Vector.LoadUnsafe(ref window, row + 40);
                sum6 += Vector.LoadUnsafe(ref history, row + 48) * Vector.LoadUnsafe(ref window, row + 48);
                sum7 += Vector.LoadUnsafe(ref history, row + 56) * Vector.LoadUnsafe(ref window, row + 56);
            }

            sum0.StoreUnsafe(ref output, 0);
            sum1.StoreUnsafe(ref output, 8);
            sum2.StoreUnsafe(ref output, 16);
            sum3.StoreUnsafe(ref output, 24);
            sum4.StoreUnsafe(ref output, 32);
            sum5.StoreUnsafe(ref output, 40);
            sum6.StoreUnsafe(ref output, 48);
            sum7.StoreUnsafe(ref output, 56);
        }

        private static void Fold(ReadOnlySpan<float> partialSums, Span<float> folded)
        {
            ref var sums = ref MemoryMarshal.GetReference(partialSums);
            ref var output = ref MemoryMarshal.GetReference(folded);
            output = Unsafe.Add(ref sums, 16);
            for (var j = 1; j <= 16; j++)
            {
                Unsafe.Add(ref output, j) = Unsafe.Add(ref sums, 16 + j) + Unsafe.Add(ref sums, 16 - j);
            }

            for (var j = 17; j < FoldedCount; j++)
            {
                Unsafe.Add(ref output, j) = Unsafe.Add(ref sums, 16 + j) - Unsafe.Add(ref sums, 80 - j);
            }
        }

        private static void Matrix(ReadOnlySpan<float> folded, Span<float> subbandSamples)
        {
            var lanes = Vector<float>.Count;
            ref var input = ref MemoryMarshal.GetReference(folded);
            ref var matrix = ref MemoryMarshal.GetArrayDataReference(TransposedMatrix);
            ref var output = ref MemoryMarshal.GetReference(subbandSamples);
            if (lanes == 8)
            {
                var sum0 = Vector<float>.Zero;
                var sum1 = Vector<float>.Zero;
                var sum2 = Vector<float>.Zero;
                var sum3 = Vector<float>.Zero;
                for (nuint j = 0; j < FoldedCount; j++)
                {
                    var value = new Vector<float>(Unsafe.Add(ref input, (nint)j));
                    var row = j * SubbandCount;
                    sum0 += Vector.LoadUnsafe(ref matrix, row) * value;
                    sum1 += Vector.LoadUnsafe(ref matrix, row + 8) * value;
                    sum2 += Vector.LoadUnsafe(ref matrix, row + 16) * value;
                    sum3 += Vector.LoadUnsafe(ref matrix, row + 24) * value;
                }

                sum0.StoreUnsafe(ref output, 0);
                sum1.StoreUnsafe(ref output, 8);
                sum2.StoreUnsafe(ref output, 16);
                sum3.StoreUnsafe(ref output, 24);
                return;
            }

            for (var k = 0; k < SubbandCount; k += lanes)
            {
                var sum = Vector<float>.Zero;
                for (var j = 0; j < FoldedCount; j++)
                {
                    sum += Vector.LoadUnsafe(ref matrix, (nuint)((j * SubbandCount) + k)) * new Vector<float>(Unsafe.Add(ref input, j));
                }

                sum.StoreUnsafe(ref output, (nuint)k);
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
