namespace EggEncoder.Waveform
{
    // The ITU-R BS.1770-4 ("K-weighting") pre-filter EBU R128 loudness measurement requires before
    // any mean-square/gating computation: a high-shelf stage (approximating head diffraction, a
    // few dB of boost above ~1.7kHz) cascaded with a high-pass stage (approximating the ear's
    // reduced sensitivity to very low frequencies). The standard's own Annex 1 only tabulates exact
    // coefficients at 48kHz; this instead derives them at any sample rate via the same tan-prewarped
    // bilinear-transform formula the reference libebur128 implementation uses (f0/G/Q constants and
    // the Vb/Vh exponent below confirmed directly against libebur128's own published source, not
    // taken from memory -- see docs/feature-roadmap.md item 3's own status note), which that
    // project's own test suite confirms reproduces the standard's 48kHz table to within floating-
    // point rounding. Implemented as two cascaded Direct-Form-II-Transposed biquads (the same
    // structural pattern as Pcm.BiquadTransform, though with K-weighting's own tan-based coefficient
    // formula rather than Pcm.BiquadTransform's RBJ-cookbook one, since the two parameterizations
    // aren't interchangeable and only the tan-based one is verified to match the standard) rather
    // than algebraically pre-combining them into one fourth-order section -- mathematically
    // equivalent, simpler to implement correctly, and the conventional choice for numerical
    // stability. One instance holds the running filter state for exactly one audio channel; a
    // multi-channel signal needs one instance per channel.
    internal sealed class KWeightingFilter
    {
        // Stage 1 (high-shelf): approximates head diffraction.
        private const double ShelfF0 = 1681.974450955533;
        private const double ShelfGainDb = 3.999843853973347;
        private const double ShelfQ = 0.7071752369554196;
        private const double ShelfVbExponent = 0.4996667741545416;

        // Stage 2 (high-pass): approximates the ear's reduced low-frequency sensitivity (RLB weighting).
        private const double HighPassF0 = 38.13547087602444;
        private const double HighPassQ = 0.5003270373238773;

        private readonly double _shelfB0;
        private readonly double _shelfB1;
        private readonly double _shelfB2;
        private readonly double _shelfA1;
        private readonly double _shelfA2;
        private readonly double _highPassB0;
        private readonly double _highPassB1;
        private readonly double _highPassB2;
        private readonly double _highPassA1;
        private readonly double _highPassA2;

        private double _shelfW1;
        private double _shelfW2;
        private double _highPassW1;
        private double _highPassW2;

        public KWeightingFilter(int sampleRate)
        {
            var k1 = Math.Tan(Math.PI * ShelfF0 / sampleRate);
            var vh = Math.Pow(10.0, ShelfGainDb / 20.0);
            var vb = Math.Pow(vh, ShelfVbExponent);
            var shelfA0 = 1.0 + (k1 / ShelfQ) + (k1 * k1);
            _shelfB0 = (vh + (vb * k1 / ShelfQ) + (k1 * k1)) / shelfA0;
            _shelfB1 = 2.0 * ((k1 * k1) - vh) / shelfA0;
            _shelfB2 = (vh - (vb * k1 / ShelfQ) + (k1 * k1)) / shelfA0;
            _shelfA1 = 2.0 * ((k1 * k1) - 1.0) / shelfA0;
            _shelfA2 = (1.0 - (k1 / ShelfQ) + (k1 * k1)) / shelfA0;

            var k2 = Math.Tan(Math.PI * HighPassF0 / sampleRate);
            var highPassA0 = 1.0 + (k2 / HighPassQ) + (k2 * k2);
            _highPassB0 = 1.0 / highPassA0;
            _highPassB1 = -2.0 / highPassA0;
            _highPassB2 = 1.0 / highPassA0;
            _highPassA1 = 2.0 * ((k2 * k2) - 1.0) / highPassA0;
            _highPassA2 = (1.0 - (k2 / HighPassQ) + (k2 * k2)) / highPassA0;
        }

        // x is a normalized sample in [-1.0, 1.0] (this project's usual float-domain convention --
        // see FloatSampleConverter); returns the K-weighted result at the same scale.
        public double Apply(double x)
        {
            var shelfOut = (_shelfB0 * x) + _shelfW1;
            _shelfW1 = (_shelfB1 * x) - (_shelfA1 * shelfOut) + _shelfW2;
            _shelfW2 = (_shelfB2 * x) - (_shelfA2 * shelfOut);

            var highPassOut = (_highPassB0 * shelfOut) + _highPassW1;
            _highPassW1 = (_highPassB1 * shelfOut) - (_highPassA1 * highPassOut) + _highPassW2;
            _highPassW2 = (_highPassB2 * shelfOut) - (_highPassA2 * highPassOut);

            return highPassOut;
        }
    }
}
