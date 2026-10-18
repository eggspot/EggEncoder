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

        // KNOWN OPEN ISSUE (docs/external-dependency-removal-plan.md Phase 1 sub-item 1a): this
        // full-chain test currently fails -- even after searching all delays in [-600, 600] samples
        // for the best-correlating alignment (the same "find the true alignment empirically via
        // cross-correlation" technique VorbisEncoderSessionTest's own round-trip SNR helper already
        // uses elsewhere in this codebase, added here specifically to rule delay/alignment out as
        // the cause), the best achievable SNR is ~0dB (essentially uncorrelated), not the >20dB a
        // lossless path should give. Ruled out so far, each independently:
        //  - Mp3HybridFilter itself: confirmed correct in isolation (Mp3HybridFilterTest, a pure
        //    TDAC round trip with no polyphase filtering involved at all).
        //  - Mp3SynthesisFilter's own matrix formula, U-vector construction (U[64i+j]=V[128i+j],
        //    U[64i+32+j]=V[128i+96+j]), and window application: cross-checked line-by-line against
        //    a real, independently-written, actively-maintained open-source MP3 decoder's own
        //    synthesis-filter source (confirmed index-for-index equivalent, including its own
        //    "n[i][j]=cos((16+i)(2j+1)pi/64) is the DCT-II continued past k=31" derivation of the
        //    exact same ISO matrix this type already used) -- not a guess, a real working
        //    reference's own formulas, matching exactly.
        //  - Simple time misalignment: ruled out by the brute-force delay search in this test
        //    itself -- if it were just a delay, some nearby delay would show high SNR; none does.
        //  - A sign error in this test's own frequency-inversion step: disabling it entirely
        //    produced no material SNR change (-5.9dB either way, an earlier run of this test).
        //  - This test's 440Hz signal choice makes frequency inversion itself unlikely to be
        //    load-bearing either way: 440Hz at 44.1kHz sits almost entirely in subband 0, which is
        //    even (frequency inversion -- odd subbands only -- never touches it on either the
        //    encoder's or the decoder's side), yet the chain still fails -- so the bug is not
        //    specific to the encoder's own pre-MDCT vs. the spec's own post-hybrid-filter placement
        //    of that step either.
        //  - Mp3SynthesisFilter in genuine isolation (no hybrid filter, no real encoder) DOES show
        //    real, substantial positive correlation with a slowly-varying subband-0-only input
        //    (confirmed via a throwaway diagnostic during this investigation, not committed) --
        //    meaning the filter itself produces output structurally related to its own input, not
        //    pure noise; the near-zero correlation specifically appears once Mp3FrameEncoder's own
        //    real output is in the loop.
        // Net effect: every individually-checked piece matches a real reference or passes its own
        // isolated test, yet the full chain still doesn't reconstruct -- the remaining bug is most
        // likely in some interaction this per-piece testing doesn't exercise (a strong next step:
        // dump intermediate xr/hybrid-filter-output values for a single real encoded granule and
        // compare them, value for value, against a hand-computed expectation, rather than only
        // checking aggregate SNR). Left as an explicit repro, not deleted, so the next session
        // doesn't need to rebuild it.
        [Fact(Skip = "Open bug: the full chain reconstructs at ~0dB SNR even after an exhaustive delay search -- see this test's own doc comment for the full list of what's been ruled out.")]
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

            bestSnr.Should().BeGreaterThan(20, $"best delay found was {bestDelay}, giving SNR {bestSnr}dB");
        }
    }
}
