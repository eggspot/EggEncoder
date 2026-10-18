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

                // The window-multiply + 8-way partial sum + 32x64 cosine matrix steps above have an
                // inherent gain of exactly 32x for a steady-state DC input -- NOT 16x, as an earlier
                // version of this comment claimed (that "16x" figure was confirmed only against this
                // project's own now-deleted self-decode scratch test, which paired this filter with
                // its own from-scratch synthesis counterpart; both apparently shared a matching,
                // independently-wrong gain assumption, so a purely self-consistent round-trip
                // "confirmed" the wrong number without either side being individually correct). The
                // true 32x factor was found and confirmed directly from the window-multiply/partial-
                // sum/matrix steps' own definitions (a steady-state constant C produces a raw,
                // pre-normalization sum of exactly 32*C here, verified with the project's own real
                // Mp3Tables.WindowCoefficients values, not assumed) while investigating why
                // Mp3SynthesisFilter -- independently cross-checked value-for-value against a real,
                // external, working MP3 decoder's own synthesis-filter source and matching exactly --
                // still reconstructed real encoded audio at exactly double the correct amplitude; see
                // docs/external-dependency-removal-plan.md Phase 1 sub-item 1a's own status note for
                // the full investigation. The matching decoder-side synthesis filter has no
                // corresponding scaling of its own, so this is the one place in the whole chain that
                // needs it -- normalizing here keeps a round-tripped DC signal at unity gain instead
                // of 32x too loud. This changes every encoded file's own internal scalar values
                // (global_gain ends up roughly 4 steps lower -- each step is a quarter-power-of-2 in
                // amplitude, ~1.505dB, so 4 of them is ~6.02dB, exactly the dB-equivalent of the 2x
                // amplitude reduction this represents; the quantizer's own binary search picks
                // whatever global_gain correctly represents the now-correctly-scaled input either
                // way) but does NOT change what a real decoder reports after decoding: every existing round-
                // trip test here already measures SNR after solving for the best-fit linear scale
                // between original and decoded audio (deliberately scale-invariant, to compare
                // *shape* fidelity independent of any absolute gain convention), which is exactly why
                // this real factor-of-2 gain bug was never caught by them.
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
