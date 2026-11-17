using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3HybridFilterBankTest
    {
        private const int Subbands = 32;
        private const int Coefficients = 18;
        private const int WindowLength = 36;

        [Fact]
        public void Folded_Mdct_Should_Match_The_Direct_Definition_In_Every_Subband()
        {
            var random = new Random(7);
            var samples = new float[WindowLength * Subbands];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)((random.NextDouble() * 2) - 1);
            }

            var folded = new float[Coefficients * Subbands];
            var coefficients = new float[Coefficients * Subbands];
            Mp3HybridFilterBank.FoldWindowed(samples, folded);
            Mp3HybridFilterBank.Dct4(folded, coefficients);

            var scale = 2.0 / Coefficients / 32768.0;
            for (var sb = 0; sb < Subbands; sb++)
            {
                var windowed = new double[WindowLength];
                for (var n = 0; n < WindowLength; n++)
                {
                    windowed[n] = samples[(n * Subbands) + sb] * Math.Sin((Math.PI / WindowLength) * (n + 0.5));
                }

                var expected = Mdct.Forward(windowed);
                for (var k = 0; k < Coefficients; k++)
                {
                    ((double)coefficients[(k * Subbands) + sb]).Should().BeApproximately(expected[k] * scale, 1e-9, $"subband {sb}, coefficient {k}");
                }
            }
        }

        [Fact]
        public void A_Tone_Should_Land_In_The_Subband_That_Holds_Its_Frequency()
        {
            const int sampleRate = 44100;
            const double frequency = 5000;
            var bank = new Mp3HybridFilterBank();
            var pcm = new int[Mp3HybridFilterBank.GranuleSamples * 4];
            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = (int)(16000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            }

            double[] xr = [];
            for (var granule = 0; granule < 4; granule++)
            {
                xr = bank.Analyze(pcm.AsSpan(granule * Mp3HybridFilterBank.GranuleSamples), stride: 1);
            }

            var energy = new double[Subbands];
            for (var i = 0; i < xr.Length; i++)
            {
                energy[i / Coefficients] += xr[i] * xr[i];
            }

            var expectedSubband = (int)(frequency / (sampleRate / 2.0) * Subbands);
            Array.IndexOf(energy, energy.Max()).Should().Be(expectedSubband);
            energy[expectedSubband].Should().BeGreaterThan(0.9 * energy.Sum());
        }
    }
}
