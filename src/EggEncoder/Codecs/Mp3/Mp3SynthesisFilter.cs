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
    //
    // Unlike the encoder's own analysis filter (which documents an empirically-confirmed 16x gain
    // it corrects for with its own /16.0), this synthesis filter applies no extra scaling at all --
    // the standard decoder-side formula is unity gain on its own, which is why
    // Mp3PolyphaseFilter's own doc comment specifically calls out that "the matching decoder-side
    // synthesis filter has no corresponding 1/16 of its own."
    //
    // KNOWN OPEN ISSUE (docs/external-dependency-removal-plan.md Phase 1 sub-item 1a): this type's
    // own correctness is NOT yet independently verified end-to-end. Mp3HybridAndSynthesisRoundTripTest
    // (currently [Fact(Skip=...)]) reconstructs a real sine tone through the full
    // Mp3AliasReduction.Decode -> Mp3HybridFilter -> frequency inversion -> Synthesize chain at
    // roughly -6 to -9dB SNR, not the >20dB a lossless path should give. Mp3HybridFilter itself is
    // independently confirmed correct in isolation (Mp3HybridFilterTest, a pure TDAC round trip
    // with no polyphase filtering involved at all), which narrows the remaining bug to this type's
    // own matrixing/U-vector construction and/or its interaction with frequency inversion -- see
    // that skipped test's own doc comment for exactly what's been ruled out so far. The U-vector
    // construction below (U[64i+j]=V[128i+j], U[64i+32+j]=V[128i+96+j]) is the best-corroborated of
    // two candidate formulas found across independent sources during research (the other uses
    // V[128i+32+j] for the second half); swapping to that alternative was tried and did not fix
    // the SNR either, so the bug is not simply "the wrong one of these two candidates."
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
