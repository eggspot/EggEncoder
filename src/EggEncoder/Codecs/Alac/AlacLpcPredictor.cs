namespace EggEncoder.Codecs.Alac
{
    // ALAC's adaptive FIR predictor. The coefficients aren't fixed for a frame: both the encoder and
    // the decoder mutate the same coefficient array, sample by sample, using an identical sign-sign
    // adaptation step driven by the sign of each residual -- so Reconstruct (decode) and Analyze
    // (encode) MUST share this exact arithmetic (AdaptCoefficients below), not two independently
    // written copies, or the two sides' coefficient arrays would silently drift apart mid-frame.
    //
    // coefficients is mutated in place by both methods; callers must pass a fresh per-frame copy
    // (ALAC resets prediction state at the start of every frame -- nothing carries across frames).
    internal static class AlacLpcPredictor
    {
        public static int[] Reconstruct(int[] residuals, int sampleCount, int order, int quantization, int[] coefficients)
        {
            var samples = new int[sampleCount];

            if (order == 0)
            {
                Array.Copy(residuals, samples, sampleCount);
                return samples;
            }

            samples[0] = residuals[0];
            for (var i = 1; i < Math.Min(order, sampleCount); i++)
            {
                samples[i] = samples[i - 1] + residuals[i];
            }

            for (var i = order; i < sampleCount; i++)
            {
                var basePoint = samples[i - order];
                var predicted = basePoint + Predict(samples, i, order, quantization, coefficients, basePoint);

                var residual = residuals[i];
                samples[i] = predicted + residual;

                AdaptCoefficients(coefficients, order, samples, i, basePoint, residual, quantization);
            }

            return samples;
        }

        public static int[] Analyze(int[] samples, int sampleCount, int order, int quantization, int[] coefficients)
        {
            var residuals = new int[sampleCount];

            if (order == 0)
            {
                Array.Copy(samples, residuals, sampleCount);
                return residuals;
            }

            residuals[0] = samples[0];
            for (var i = 1; i < Math.Min(order, sampleCount); i++)
            {
                residuals[i] = samples[i] - samples[i - 1];
            }

            for (var i = order; i < sampleCount; i++)
            {
                var basePoint = samples[i - order];
                var predicted = basePoint + Predict(samples, i, order, quantization, coefficients, basePoint);

                var residual = samples[i] - predicted;
                residuals[i] = residual;

                AdaptCoefficients(coefficients, order, samples, i, basePoint, residual, quantization);
            }

            return residuals;
        }

        private static int Predict(int[] samples, int i, int order, int quantization, int[] coefficients, int basePoint)
        {
            var sum = 0L;
            for (var j = 0; j < order; j++)
            {
                sum += (long)(samples[i - order + j] - basePoint) * coefficients[j];
            }

            return (int)((sum + (1L << (quantization - 1))) >> quantization);
        }

        private static void AdaptCoefficients(int[] coefficients, int order, int[] samples, int i, int basePoint, int residual, int quantization)
        {
            var sign = Math.Sign(residual);
            if (sign == 0)
            {
                return;
            }

            for (var j = order - 1; j >= 0 && residual * sign > 0; j--)
            {
                var v = basePoint - samples[i - order + j];
                var s = Math.Sign(v) * sign;
                coefficients[j] -= s;
                v *= s;
                residual -= (v >> quantization) * (order - j);
            }
        }
    }
}
