namespace EggEncoder.Codecs.WavPack
{
    // The post-decode sample scaling WP_ID_INT32_INFO's metadata (bytes 1-3) selects, on top of the
    // block header's own 5-bit left-shift field. The common case (byte 1 set) is a plain left-shift
    // with zero-filled low bits (And=0, Or=0). Bytes 2/3 select a variant that instead fills the
    // newly-shifted-in low bits with a copy of the sample's own low bit (And=1, Or=0, byte 3) or
    // with all-ones (Or=1, with And either on or off, byte 2) -- confirmed from a genuine reference-
    // encoder-produced fixture to occur even for ordinary 16-bit lossless content (not just hybrid
    // or >24-bit integer decode, which is what general WavPack documentation describes it for),
    // apparently to let the entropy/decorrelation domain carry one extra bit of precision for a
    // signal with true full-scale (e.g. int16.MaxValue) samples without that extra bit's own value
    // needing to be transmitted, since the reconstruction formula recovers it either from the
    // sample's own parity (And) or just assumes it's set (Or).
    internal readonly struct WavPackSampleScale
    {
        public static readonly WavPackSampleScale None = new(0, 0, 0);

        public int Shift { get; }

        public int And { get; }

        public int Or { get; }

        public WavPackSampleScale(int shift, int and, int or)
        {
            Shift = shift;
            And = and;
            Or = or;
        }

        public int Apply(int headerLeftShift, int sample)
        {
            var totalShift = headerLeftShift + Shift;
            if (totalShift <= 0)
            {
                return sample;
            }

            var bit = (sample & And) | Or;
            return ((sample + bit) << totalShift) - bit;
        }
    }
}
