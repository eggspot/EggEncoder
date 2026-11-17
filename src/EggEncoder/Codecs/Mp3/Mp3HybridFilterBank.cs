using System.Numerics;

namespace EggEncoder.Codecs.Mp3
{
    internal sealed class Mp3HybridFilterBank
    {
        internal const int GranuleSamples = 576;

        private const int SubbandCount = Mp3PolyphaseFilter.SubbandCount;
        private const int CoefficientsPerSubband = 18;
        private const int WindowLength = CoefficientsPerSubband * 2;
        private const int HalfCoefficients = CoefficientsPerSubband / 2;
        private const double OutputScale = 2.0 / CoefficientsPerSubband / 32768.0;

        private static readonly float[] SineWindow = BuildSineWindow();
        private static readonly float[] Dct4Table = BuildDct4Table();

        private readonly Mp3PolyphaseFilter _polyphase = new();
        private readonly float[] _subbandSamples = new float[WindowLength * SubbandCount];
        private readonly float[] _folded = new float[CoefficientsPerSubband * SubbandCount];
        private readonly float[] _coefficients = new float[CoefficientsPerSubband * SubbandCount];

        public double[] Analyze(ReadOnlySpan<int> interleavedSamples, int stride)
        {
            var current = _subbandSamples.AsSpan(CoefficientsPerSubband * SubbandCount);
            for (var t = 0; t < CoefficientsPerSubband; t++)
            {
                var tick = current.Slice(t * SubbandCount, SubbandCount);
                _polyphase.Analyze(interleavedSamples[(t * SubbandCount * stride)..], stride, tick);
                if ((t & 1) == 1)
                {
                    for (var sb = 1; sb < SubbandCount; sb += 2)
                    {
                        tick[sb] = -tick[sb];
                    }
                }
            }

            FoldWindowed(_subbandSamples, _folded);
            Dct4(_folded, _coefficients);
            current.CopyTo(_subbandSamples);

            var xr = new double[GranuleSamples];
            for (var k = 0; k < CoefficientsPerSubband; k++)
            {
                var row = _coefficients.AsSpan(k * SubbandCount, SubbandCount);
                for (var sb = 0; sb < SubbandCount; sb++)
                {
                    xr[(sb * CoefficientsPerSubband) + k] = row[sb];
                }
            }

            Mp3AliasReduction.Encode(xr);
            return xr;
        }

        internal static void FoldWindowed(ReadOnlySpan<float> samples, Span<float> folded)
        {
            var lanes = Vector<float>.Count;
            for (var m = 0; m < HalfCoefficients; m++)
            {
                var left = (CoefficientsPerSubband + HalfCoefficients - 1) - m;
                var right = CoefficientsPerSubband + HalfCoefficients + m;
                var leftWeight = new Vector<float>(-SineWindow[left]);
                var rightWeight = new Vector<float>(-SineWindow[right]);
                for (var sb = 0; sb < SubbandCount; sb += lanes)
                {
                    var value = (new Vector<float>(samples[((left * SubbandCount) + sb)..]) * leftWeight)
                        + (new Vector<float>(samples[((right * SubbandCount) + sb)..]) * rightWeight);
                    value.CopyTo(folded[((m * SubbandCount) + sb)..]);
                }
            }

            for (var j = 0; j < HalfCoefficients; j++)
            {
                var mirrored = CoefficientsPerSubband - 1 - j;
                var weight = new Vector<float>(SineWindow[j]);
                var mirroredWeight = new Vector<float>(-SineWindow[mirrored]);
                for (var sb = 0; sb < SubbandCount; sb += lanes)
                {
                    var value = (new Vector<float>(samples[((j * SubbandCount) + sb)..]) * weight)
                        + (new Vector<float>(samples[((mirrored * SubbandCount) + sb)..]) * mirroredWeight);
                    value.CopyTo(folded[(((HalfCoefficients + j) * SubbandCount) + sb)..]);
                }
            }
        }

        internal static void Dct4(ReadOnlySpan<float> folded, Span<float> coefficients)
        {
            var lanes = Vector<float>.Count;
            for (var sb = 0; sb < SubbandCount; sb += lanes)
            {
                for (var k = 0; k < CoefficientsPerSubband; k += 2)
                {
                    var even = Vector<float>.Zero;
                    var odd = Vector<float>.Zero;
                    for (var m = 0; m < CoefficientsPerSubband; m++)
                    {
                        var input = new Vector<float>(folded[((m * SubbandCount) + sb)..]);
                        even += input * new Vector<float>(Dct4Table[(m * CoefficientsPerSubband) + k]);
                        odd += input * new Vector<float>(Dct4Table[(m * CoefficientsPerSubband) + k + 1]);
                    }

                    even.CopyTo(coefficients[((k * SubbandCount) + sb)..]);
                    odd.CopyTo(coefficients[(((k + 1) * SubbandCount) + sb)..]);
                }
            }
        }

        private static float[] BuildSineWindow()
        {
            var window = new float[WindowLength];
            for (var n = 0; n < WindowLength; n++)
            {
                window[n] = (float)Math.Sin((Math.PI / WindowLength) * (n + 0.5));
            }

            return window;
        }

        private static float[] BuildDct4Table()
        {
            var table = new float[CoefficientsPerSubband * CoefficientsPerSubband];
            for (var m = 0; m < CoefficientsPerSubband; m++)
            {
                for (var k = 0; k < CoefficientsPerSubband; k++)
                {
                    table[(m * CoefficientsPerSubband) + k] = (float)(Math.Cos((Math.PI / CoefficientsPerSubband) * (m + 0.5) * (k + 0.5)) * OutputScale);
                }
            }

            return table;
        }
    }
}
