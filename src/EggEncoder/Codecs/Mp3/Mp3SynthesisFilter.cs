namespace EggEncoder.Codecs.Mp3
{
    // The decode-side counterpart to Mp3PolyphaseFilter's own analysis filter -- both the forward
    // and inverse directions of the same prototype window (Mp3Tables.WindowCoefficients). Per the
    // bitstream-syntax document's own section 4.3.12.6 ("Synthesis subband filter"): 32 subband
    // samples are matrixed into 64 values V[i] (the N[i][k]=cos((16+i)(2k+1)pi/64) matrix, i=0..63,
    // k=0..31 -- the same cosine-matrix family Mp3PolyphaseFilter.BuildMatrix already uses for its
    // own, differently-indexed analysis direction), shifted into the front of a 1024-element
    // sliding history V (newest 64 at the front), reduced to a 512-element vector U, windowed by D
    // (Mp3Tables.WindowCoefficients again), and summed down to 32 output samples.
    internal sealed class Mp3SynthesisFilter
    {
        private const int SubbandCount = 32;
        private const int MatrixOutputCount = 64;
        private const int VectorLength = 1024;
        private const int UUnitCount = 512;

        private static readonly double[] _matrix = BuildMatrix();

        private readonly double[] _v = new double[VectorLength];

        public double[] Synthesize(ReadOnlySpan<double> subbandSamples)
        {
            if (subbandSamples.Length != SubbandCount)
            {
                throw new ArgumentException($"Synthesize expects exactly {SubbandCount} subband samples per call", nameof(subbandSamples));
            }

            var vNew = new double[MatrixOutputCount];
            for (var i = 0; i < MatrixOutputCount; i++)
            {
                var sum = 0.0;
                for (var k = 0; k < SubbandCount; k++)
                {
                    sum += _matrix[(i * SubbandCount) + k] * subbandSamples[k];
                }

                vNew[i] = sum;
            }

            Array.Copy(_v, 0, _v, MatrixOutputCount, VectorLength - MatrixOutputCount);
            Array.Copy(vNew, 0, _v, 0, MatrixOutputCount);

            var u = new double[UUnitCount];
            for (var block = 0; block < 8; block++)
            {
                for (var j = 0; j < 32; j++)
                {
                    u[(64 * block) + j] = _v[(128 * block) + j];
                    u[(64 * block) + 32 + j] = _v[(128 * block) + 96 + j];
                }
            }

            for (var i = 0; i < UUnitCount; i++)
            {
                u[i] *= Mp3Tables.WindowCoefficients[i];
            }

            var output = new double[SubbandCount];
            for (var i = 0; i < SubbandCount; i++)
            {
                var sum = 0.0;
                for (var j = 0; j < 16; j++)
                {
                    sum += u[i + (32 * j)];
                }

                output[i] = sum;
            }

            return output;
        }

        private static double[] BuildMatrix()
        {
            var matrix = new double[MatrixOutputCount * SubbandCount];
            for (var i = 0; i < MatrixOutputCount; i++)
            {
                for (var k = 0; k < SubbandCount; k++)
                {
                    matrix[(i * SubbandCount) + k] = Math.Cos((16 + i) * ((2 * k) + 1) * Math.PI / 64.0);
                }
            }

            return matrix;
        }
    }
}
