using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    // Scratch, stage-by-stage differential verification: each real internal class's own output is
    // compared, numerically, against an independently-written reference implementation of the same
    // formula (not a copy of the production code, a fresh re-derivation) for a deterministic test
    // signal. Not a permanent test -- deleted once the encoder is fixed.
    public class Mp3DifferentialTest
    {
        [Fact]
        public void PolyphaseFilter_Should_MatchIndependentReferenceImplementation()
        {
            var sampleRate = 44100;
            var pcm = new int[512 + 320];
            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var filter = new Mp3PolyphaseFilter();
            double[] lastReal = null!;
            for (var offset = 0; offset + 32 <= pcm.Length; offset += 32)
            {
                lastReal = filter.Analyze(pcm.AsSpan(offset, 32));
            }

            // Independent reference: maintain our OWN 512-sample history buffer (newest at index 0,
            // matching Mp3PolyphaseFilter's own documented convention) and the same matrixing
            // formula, written fresh here rather than calling into Mp3PolyphaseFilter at all.
            var history = new double[512];
            var window = Mp3Tables.WindowCoefficients;
            for (var offset = 0; offset + 32 <= pcm.Length; offset += 32)
            {
                var shifted = new double[512];
                Array.Copy(history, 0, shifted, 32, 480);
                for (var i = 0; i < 32; i++)
                {
                    shifted[31 - i] = pcm[offset + i];
                }

                history = shifted;
            }

            var windowed = new double[512];
            for (var i = 0; i < 512; i++)
            {
                windowed[i] = history[i] * window[i];
            }

            var partial = new double[64];
            for (var i = 0; i < 64; i++)
            {
                double sum = 0;
                for (var j = 0; j < 8; j++)
                {
                    sum += windowed[i + (64 * j)];
                }

                partial[i] = sum;
            }

            var reference = new double[32];
            for (var k = 0; k < 32; k++)
            {
                double sum = 0;
                for (var i = 0; i < 64; i++)
                {
                    sum += Math.Cos(((2 * k) + 1) * (i - 16) * Math.PI / 64.0) * partial[i];
                }

                reference[k] = sum / 16.0;
            }

            for (var k = 0; k < 32; k++)
            {
                var diff = Math.Abs(lastReal[k] - reference[k]);
                Assert.True(diff < 1e-6, $"subband {k}: real={lastReal[k]}, reference={reference[k]}, diff={diff}");
            }
        }

        [Fact]
        public void Mdct_Should_MatchIndependentReferenceImplementation()
        {
            var rng = new Random(42);
            var input = new double[36];
            for (var i = 0; i < 36; i++)
            {
                input[i] = rng.NextDouble() * 2 - 1;
            }

            var real = Mdct.Forward(input);

            var reference = new double[18];
            for (var k = 0; k < 18; k++)
            {
                double sum = 0;
                for (var n = 0; n < 36; n++)
                {
                    sum += input[n] * Math.Cos((Math.PI / 18.0) * (n + 0.5 + 9.0) * (k + 0.5));
                }

                reference[k] = sum;
            }

            for (var k = 0; k < 18; k++)
            {
                var diff = Math.Abs(real[k] - reference[k]);
                Assert.True(diff < 1e-9, $"coeff {k}: real={real[k]}, reference={reference[k]}");
            }
        }

        [Fact]
        public void AliasReduction_Should_MatchIndependentInverseDerivation()
        {
            var rng = new Random(7);
            var xr = new double[576];
            for (var i = 0; i < 576; i++)
            {
                xr[i] = rng.NextDouble() * 1000 - 500;
            }

            var real = (double[])xr.Clone();
            Mp3AliasReduction.Encode(real);

            // Independent reference: fresh derivation of the exact inverse of the spec's own decode
            // pseudocode via the orthogonal-matrix transpose property, re-typed here (not calling
            // Mp3AliasReduction or Mp3Tables.AliasCs/AliasCa at all).
            var c = new[] { -0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037 };
            var reference = (double[])xr.Clone();
            for (var sb = 1; sb < 32; sb++)
            {
                for (var i = 0; i < 8; i++)
                {
                    var cs = 1.0 / Math.Sqrt(1 + (c[i] * c[i]));
                    var ca = c[i] / Math.Sqrt(1 + (c[i] * c[i]));
                    var lowerIndex = (18 * sb) - 1 - i;
                    var upperIndex = (18 * sb) + i;
                    var lower = reference[lowerIndex];
                    var upper = reference[upperIndex];
                    reference[lowerIndex] = (lower * cs) + (upper * ca);
                    reference[upperIndex] = (upper * cs) - (lower * ca);
                }
            }

            for (var i = 0; i < 576; i++)
            {
                var diff = Math.Abs(real[i] - reference[i]);
                Assert.True(diff < 1e-9, $"index {i}: real={real[i]}, reference={reference[i]}");
            }

            // Also verify this is genuinely the INVERSE of the decoder's own documented forward
            // butterfly (apply decode's own formula to our output and recover the original xr).
            var decoded = (double[])real.Clone();
            for (var sb = 1; sb < 32; sb++)
            {
                for (var i = 0; i < 8; i++)
                {
                    var cs = 1.0 / Math.Sqrt(1 + (c[i] * c[i]));
                    var ca = c[i] / Math.Sqrt(1 + (c[i] * c[i]));
                    var lowerIndex = (18 * sb) - 1 - i;
                    var upperIndex = (18 * sb) + i;
                    var lower = decoded[lowerIndex];
                    var upper = decoded[upperIndex];
                    decoded[lowerIndex] = (lower * cs) - (upper * ca);
                    decoded[upperIndex] = (upper * cs) + (lower * ca);
                }
            }

            for (var i = 0; i < 576; i++)
            {
                var diff = Math.Abs(decoded[i] - xr[i]);
                Assert.True(diff < 1e-6, $"round-trip index {i}: decoded={decoded[i]}, original={xr[i]}");
            }
        }

        // Integration check: this exercises Mp3FrameEncoder's own private
        // ComputeGranuleCoefficients (via reflection, since it's private) across SEVERAL
        // consecutive granules -- the one thing the three isolated stage tests above don't cover,
        // since each of those calls its own stage exactly once. A fresh, independently-written
        // multi-granule simulation (18 polyphase ticks per granule, 18-sample MDCT history
        // threaded across granules, alias reduction) is compared against it sample-for-sample.
        [Fact]
        public void FrameEncoder_MultiGranuleIntegration_Should_MatchIndependentSimulation()
        {
            const int sampleRate = 44100;
            const int channels = 1;
            var frameEncoder = new Mp3FrameEncoder(channels, sampleRate, 128);

            var totalGranules = 4;
            var pcm = new int[576 * totalGranules];
            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            // Reference: independent re-implementation threading history across granules.
            var window = Mp3Tables.WindowCoefficients;
            var sineWindow = new double[36];
            for (var n = 0; n < 36; n++)
            {
                sineWindow[n] = Math.Sin((Math.PI / 36) * (n + 0.5));
            }

            var refHistoryPcm = new double[512];
            var refSubbandHistory = new double[32][];
            for (var sb = 0; sb < 32; sb++)
            {
                refSubbandHistory[sb] = new double[18];
            }

            var c = new[] { -0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037 };

            for (var granule = 0; granule < totalGranules; granule++)
            {
                var perSubband = new double[32][];
                for (var sb = 0; sb < 32; sb++)
                {
                    perSubband[sb] = new double[18];
                }

                for (var t = 0; t < 18; t++)
                {
                    var shifted = new double[512];
                    Array.Copy(refHistoryPcm, 0, shifted, 32, 480);
                    for (var i = 0; i < 32; i++)
                    {
                        shifted[31 - i] = pcm[(granule * 576) + (t * 32) + i];
                    }

                    refHistoryPcm = shifted;

                    var windowed = new double[512];
                    for (var i = 0; i < 512; i++)
                    {
                        windowed[i] = refHistoryPcm[i] * window[i];
                    }

                    var partial = new double[64];
                    for (var i = 0; i < 64; i++)
                    {
                        double sum = 0;
                        for (var j = 0; j < 8; j++)
                        {
                            sum += windowed[i + (64 * j)];
                        }

                        partial[i] = sum;
                    }

                    for (var sb = 0; sb < 32; sb++)
                    {
                        double sum = 0;
                        for (var i = 0; i < 64; i++)
                        {
                            sum += Math.Cos(((2 * sb) + 1) * (i - 16) * Math.PI / 64.0) * partial[i];
                        }

                        perSubband[sb][t] = sum / 16.0;
                    }
                }

                var refXr = new double[576];
                for (var sb = 0; sb < 32; sb++)
                {
                    var hist = refSubbandHistory[sb];
                    var windowedMdct = new double[36];
                    for (var i = 0; i < 18; i++)
                    {
                        windowedMdct[i] = hist[i] * sineWindow[i];
                        windowedMdct[18 + i] = perSubband[sb][i] * sineWindow[18 + i];
                    }

                    var coeffs = new double[18];
                    for (var k = 0; k < 18; k++)
                    {
                        double sum = 0;
                        for (var n = 0; n < 36; n++)
                        {
                            sum += windowedMdct[n] * Math.Cos((Math.PI / 18.0) * (n + 0.5 + 9.0) * (k + 0.5));
                        }

                        coeffs[k] = sum;
                    }

                    Array.Copy(coeffs, 0, refXr, sb * 18, 18);
                    Array.Copy(perSubband[sb], refSubbandHistory[sb], 18);
                }

                for (var sb = 1; sb < 32; sb++)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var cs = 1.0 / Math.Sqrt(1 + (c[i] * c[i]));
                        var ca = c[i] / Math.Sqrt(1 + (c[i] * c[i]));
                        var lowerIndex = (18 * sb) - 1 - i;
                        var upperIndex = (18 * sb) + i;
                        var lower = refXr[lowerIndex];
                        var upper = refXr[upperIndex];
                        refXr[lowerIndex] = (lower * cs) + (upper * ca);
                        refXr[upperIndex] = (upper * cs) - (lower * ca);
                    }
                }

                // ComputeGranuleCoefficients expects a FULL 1152-sample (2-granule) frame span and a
                // granule index of 0 or 1 selecting within it -- matching exactly how EncodeFrame
                // itself calls it, not a single already-sliced-out granule.
                var frameWithinSpan = granule / 2;
                var granuleWithinFrame = granule % 2;
                var frameSpan = pcm.AsSpan(frameWithinSpan * 1152, 1152);
                var realXr = frameEncoder.ComputeGranuleCoefficients(frameSpan, granuleWithinFrame, 0);

                for (var i = 0; i < 576; i++)
                {
                    var diff = Math.Abs(realXr[i] - refXr[i]);
                    Assert.True(diff < 1e-6, $"granule {granule} index {i}: real={realXr[i]}, reference={refXr[i]}, diff={diff}");
                }
            }
        }
    }
}
