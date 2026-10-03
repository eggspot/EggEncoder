namespace EggEncoder.Codecs.Tta
{
    // TTA's final (decode) / first (encode) fixed-order predictor stage. Confirmed against ffmpeg's
    // tta.c/ttaenc.c: PRED(x,k) = (int32_t)((((uint64_t)x << k) - x) >> k) -- algebraically equivalent
    // to floor(x*(2^k-1)/2^k), i.e. an arithmetic (sign-extending) right shift by k of (x<<k - x). C's
    // version routes through uint64_t purely to dodge signed-overflow UB on the left shift; plain
    // signed `long` arithmetic in C# gives the identical result for any value that doesn't itself
    // overflow 64 bits (true for 16-bit audio with headroom to spare), so this skips that detour.
    // k=5 is specifically ffmpeg's constant for 16-bit audio; other bit depths use a different k,
    // out of scope here (this codec is 16-bit-only, matching ALAC's scope decision).
    //
    // Resets fresh (predictor=0) at the start of every frame.
    internal sealed class TtaFixedPredictor
    {
        private const int Shift = 5;

        private int _predictor;

        public int Decode(int value)
        {
            value += Predict(_predictor);
            _predictor = value;
            return value;
        }

        public int Encode(int value)
        {
            var temp = value;
            value -= Predict(_predictor);
            _predictor = temp;
            return value;
        }

        private static int Predict(int x) => (int)((((long)x << Shift) - x) >> Shift);
    }
}
