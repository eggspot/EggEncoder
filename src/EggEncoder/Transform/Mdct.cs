namespace EggEncoder.Transform
{
    public static class Mdct
    {
        public static double[] Forward(double[] samples)
        {
            var sampleCount = samples.Length;
            if (sampleCount % 2 != 0)
            {
                throw new ArgumentException("MDCT input length must be even", nameof(samples));
            }

            var coefficientCount = sampleCount / 2;
            var coefficients = new double[coefficientCount];

            for (var k = 0; k < coefficientCount; k++)
            {
                var sum = 0.0;
                for (var n = 0; n < sampleCount; n++)
                {
                    sum += samples[n] * Math.Cos((Math.PI / coefficientCount) * (n + 0.5 + (coefficientCount / 2.0)) * (k + 0.5));
                }

                coefficients[k] = sum;
            }

            return coefficients;
        }

        public static double[] Inverse(double[] coefficients)
        {
            var coefficientCount = coefficients.Length;
            var sampleCount = coefficientCount * 2;
            var samples = new double[sampleCount];
            var normalization = 1.0 / coefficientCount;

            for (var n = 0; n < sampleCount; n++)
            {
                var sum = 0.0;
                for (var k = 0; k < coefficientCount; k++)
                {
                    sum += coefficients[k] * Math.Cos((Math.PI / coefficientCount) * (n + 0.5 + (coefficientCount / 2.0)) * (k + 0.5));
                }

                samples[n] = sum * normalization;
            }

            return samples;
        }
    }
}
