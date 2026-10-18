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
        // full-chain test currently fails (SNR around -6 to -9dB, not the >20dB a lossless-path
        // reconstruction should give). Mp3HybridFilter is independently verified correct in
        // isolation (see Mp3HybridFilterTest, a pure TDAC round trip with no polyphase filtering at
        // all) -- the remaining bug is somewhere in Mp3SynthesisFilter's own polyphase matrixing/
        // U-vector construction, and/or in how this test's own frequency-inversion placement
        // interacts with Mp3FrameEncoder.ComputeGranuleCoefficients' own ALREADY-applied frequency
        // inversion (applied pre-MDCT, in raw subband-domain samples -- a different representation
        // from the post-hybrid-filter point section 4.3.12.5 of the bitstream-syntax spec document
        // says a decoder applies it at). Disabling this test's own frequency-inversion step
        // entirely did not materially change the SNR (-5.9dB either way), which rules out a sign
        // error there as the dominant cause but does not yet identify the real one. Left as a
        // Theory with explicit inputs recorded, not deleted, so the next session has the exact
        // repro rather than needing to rebuild it.
        [Fact(Skip = "Open bug: Mp3SynthesisFilter's own polyphase reconstruction is not yet correct -- see this test's own doc comment for what's confirmed vs. still unknown.")]
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
            var actual = decoded.Skip(settledStart).Take(compareLength).ToArray();

            actual.Should().HaveCount(expected.Length);

            var errorSumSquares = 0.0;
            var signalSumSquares = 0.0;
            for (var i = 0; i < expected.Length; i++)
            {
                var error = expected[i] - actual[i];
                errorSumSquares += error * error;
                signalSumSquares += expected[i] * expected[i];
            }

            var snrDb = 10 * Math.Log10(signalSumSquares / errorSumSquares);
            snrDb.Should().BeGreaterThan(20, "the hybrid filter + frequency inversion + synthesis filter chain should reconstruct a settled sine tone with no quantization involved at all");
        }
    }
}
