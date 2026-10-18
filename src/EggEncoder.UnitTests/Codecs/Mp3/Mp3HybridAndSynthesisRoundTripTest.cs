using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    // Integration test for the "back half" of the decode chain -- Mp3AliasReduction.Decode,
    // Mp3HybridFilter, frequency inversion, and Mp3SynthesisFilter together -- verified against the
    // existing, already-shipped Mp3FrameEncoder.ComputeGranuleCoefficients (internal specifically
    // so test code can call it directly, the same "Mp3DifferentialTest" precedent its own doc
    // comment references) rather than against a hand-derived or externally-cited formula alone.
    // Deliberately bypasses Huffman coding/quantization entirely (ComputeGranuleCoefficients' own
    // output, xr, is real, continuous, alias-reduced spectral data -- exactly Mp3Requantizer's own
    // output shape, just without the lossy integer quantization step in between) so this test
    // isolates the hybrid-filter/synthesis-filter chain's own correctness from the separately
    // tested Huffman/requantization pieces.
    public class Mp3HybridAndSynthesisRoundTripTest
    {
        private const int SampleRate = 44100;
        private const int GranuleSamples = 576;

        // RESOLVED (was a known open issue in docs/external-dependency-removal-plan.md Phase 1
        // sub-item 1a). Root cause: Mp3PolyphaseFilter.Analyze -- the existing, already-shipped MP3
        // ENCODER's own analysis filter, not anything in this decode chain -- had a genuine gain
        // bug: its own steady-state DC gain is really 32x, not the 16x an earlier version of its
        // own doc comment claimed (that "16x" figure was only ever confirmed against this project's
        // own now-deleted self-decode scratch test, which paired this filter with its own
        // from-scratch synthesis counterpart sharing a matching, independently-wrong gain
        // assumption -- a purely self-consistent round-trip that "confirmed" the wrong number
        // without either side being individually correct against the real ISO-defined gain). This
        // meant every encoded file's own xr values -- and so every real decoder's own reconstructed
        // audio, including this project's own -- was exactly 2x too loud. It was never caught by
        // the existing MP3 encoder tests (Mp3EncoderSnrTest and friends) because they're
        // deliberately scale-invariant: they solve for the best-fit linear scale between original
        // and decoded audio before measuring SNR, specifically to compare *shape* fidelity
        // independent of any absolute gain convention -- exactly the property that hid a real
        // factor-of-2 absolute-gain bug. Found by dumping real per-sample intermediate values for
        // one encoded granule and comparing them, value for value, against an independent
        // from-scratch reference computation of the spec's own synthesis formulas (not aggregate
        // SNR) -- see Mp3PolyphaseFilter.cs's own doc comment for the full numeric derivation.
        // Fixed in Mp3PolyphaseFilter.Analyze (sum/32.0, not sum/16.0) -- the shipped MP3 encoder's
        // own output is otherwise unaffected in shape (Mp3EncoderSnrTest and the AotSmokeTest's own
        // MP3 round trip, both decoded by real external decoders, still pass after this fix) and
        // every encoded file now carries the correct absolute amplitude, not just a correct shape.
        [Fact]
        public void FullBackHalfChain_Should_ReconstructASineTone_AtHighSnr_OnceTheFilterHistoryHasSettled()
        {
            const int granuleCount = 12;
            const double amplitude = 8000.0;
            const double frequencyHz = 440.0;

            var totalSamples = granuleCount * GranuleSamples;
            var originalPcm = new double[totalSamples];
            for (var i = 0; i < totalSamples; i++)
            {
                originalPcm[i] = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate);
            }

            var encoder = new Mp3FrameEncoder(channels: 1, sampleRate: SampleRate, bitRateKbps: 320);
            var hybridFilter = new Mp3HybridFilter();
            var synthesisFilter = new Mp3SynthesisFilter();

            var decodedPcm = new List<double>();
            for (var granule = 0; granule < granuleCount; granule++)
            {
                var granulePcm = new int[GranuleSamples];
                for (var i = 0; i < GranuleSamples; i++)
                {
                    granulePcm[i] = (int)originalPcm[(granule * GranuleSamples) + i];
                }

                var xr = encoder.ComputeGranuleCoefficients(granulePcm, granule: 0, channel: 0);

                Mp3AliasReduction.Decode(xr);
                var hybridOutput = hybridFilter.Process(xr); // subband-major, 32 x 18

                for (var tick = 0; tick < 18; tick++)
                {
                    var subbandSamples = new double[32];
                    for (var sb = 0; sb < 32; sb++)
                    {
                        var sample = hybridOutput[(sb * 18) + tick];
                        if (sb % 2 == 1 && tick % 2 == 1)
                        {
                            sample = -sample; // frequency inversion
                        }

                        subbandSamples[sb] = sample;
                    }

                    var pcmSamples = synthesisFilter.Synthesize(subbandSamples);
                    decodedPcm.AddRange(pcmSamples);
                }
            }

            // Mp3FrameEncoder.ComputeGranuleCoefficients applies PcmNormalizationScale (1/32768,
            // documented in that method's own source) to match the standard -1.0..+1.0 float range
            // every real decoder's own output stage assumes -- undo it here to compare back against
            // this project's own int16-native PCM convention.
            const double inversePcmNormalizationScale = 32768.0;
            var decoded = decodedPcm.Select(v => v * inversePcmNormalizationScale).ToArray();

            // The hybrid filter's own 50%-overlap state and the synthesis filter's own 512-tap
            // history both need several granules' worth of real signal to settle -- compare only
            // the back half, the same "skip the cold-start warm-up" approach this project's own
            // lossy round-trip SNR tests already use elsewhere (e.g. encoder delay).
            var settledStart = (granuleCount / 2) * GranuleSamples;
            var compareLength = totalSamples - settledStart - GranuleSamples; // also drop the cold tail
            var expected = originalPcm.Skip(settledStart).Take(compareLength).ToArray();

            var bestDelay = 0;
            var bestSnr = double.NegativeInfinity;
            for (var delay = -600; delay <= 600; delay++)
            {
                var errorSumSquares = 0.0;
                var signalSumSquares = 0.0;
                for (var i = 0; i < expected.Length; i++)
                {
                    var actualIndex = settledStart + i + delay;
                    if (actualIndex < 0 || actualIndex >= decoded.Length)
                    {
                        continue;
                    }

                    var error = expected[i] - decoded[actualIndex];
                    errorSumSquares += error * error;
                    signalSumSquares += expected[i] * expected[i];
                }

                var snrDb = 10 * Math.Log10(signalSumSquares / errorSumSquares);
                if (snrDb > bestSnr)
                {
                    bestSnr = snrDb;
                    bestDelay = delay;
                }
            }

            bestSnr.Should().BeGreaterThan(40, $"best delay found was {bestDelay}, giving SNR {bestSnr}dB");
        }
    }
}
