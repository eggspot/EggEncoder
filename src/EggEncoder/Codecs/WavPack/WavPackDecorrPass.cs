namespace EggEncoder.Codecs.WavPack
{
    // One cascaded decorrelation (prediction) pass, per channel. Term meanings:
    //  - 1..8: predict from the Nth-previous output of this pass's own history, addressed via a
    //    shared rotating cursor into an 8-slot buffer (see WavPackFrameDecoder).
    //  - 17, 18: two fixed 2-tap linear-extrapolation predictors over this pass's last two outputs.
    //  - -1, -2, -3: stereo-only cross-channel predictors (the "source" channel for the prediction
    //    is the *other* channel, not this pass's own history).
    internal sealed class WavPackDecorrPass
    {
        public required int Term { get; init; }

        public required int Delta { get; init; }

        public int WeightA { get; set; }

        public int WeightB { get; set; }

        // Used as an 8-slot circular buffer for terms 1-8 (indices 0-7), or as a 2-slot "last two
        // outputs" register for terms 17/18 (indices 0-1), or as a single-slot "other channel's
        // last value" register for cross terms (index 0 only).
        public int[] SamplesA { get; } = new int[8];

        public int[] SamplesB { get; } = new int[8];

        // Scales a prediction source by this pass's own weight -- the shared +512 rounding, 10-bit
        // (1024 = unity gain) fixed-point convention every term uses.
        public static int ApplyWeight(int weight, int source) => (int)(((long)weight * source + 512) >> 10);

        public static int RestoreWeight(sbyte storedByte)
        {
            var weight = storedByte * 8;
            if (weight > 0)
            {
                weight += (weight + 64) >> 7;
            }

            return weight;
        }

        // The same sign-sign LMS adaptation for every term; only the cross-channel terms (-1/-2/-3)
        // clamp the result (see UpdateWeightClamped) -- terms 1-8/17/18 are deliberately left
        // unclamped.
        public static int UpdateWeight(int weight, int delta, int source, int residual)
        {
            if (source == 0 || residual == 0)
            {
                return weight;
            }

            return (source < 0) == (residual < 0) ? weight + delta : weight - delta;
        }

        public static int UpdateWeightClamped(int weight, int delta, int source, int residual)
        {
            var updated = UpdateWeight(weight, delta, source, residual);
            return Math.Clamp(updated, -1024, 1024);
        }
    }
}
