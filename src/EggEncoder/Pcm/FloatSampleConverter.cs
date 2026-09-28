namespace EggEncoder.Pcm;

/// <summary>
/// Converts between normalized IEEE-float samples and this codebase's int PCM representation, at
/// application boundaries that don't go through a WAV file at all (e.g. samples captured from a
/// microphone API, or handed off to an audio library that expects <see langword="float"/>).
///
/// The pipeline itself (<see cref="IPcmTransform"/>) is exclusively <see langword="int"/>[]-based and
/// has no separate "float" bit depth. A normalized float sample (range -1.0..1.0) maps 1:1 onto this
/// codebase's 32-bit int native range (see <see cref="BitDepthFormatTransform"/>'s doc comment on that
/// convention) -- the exact scale <c>WavReader</c> already produces when it decodes a 32-bit IEEE float
/// WAV file, and the scale <c>WavWriter</c> expects when asked to write one
/// (<c>WavWriter.Create(..., isFloatFormat: true)</c>). This type is that same conversion, for samples
/// that arrive as (or need to become) an actual <see langword="float"/>[] rather than a WAV file.
///
/// AOT-safe, managed implementation.
/// </summary>
public static class FloatSampleConverter
{
    /// <summary>
    /// Converts normalized float samples (expected range -1.0..1.0) into 32-bit-native-range int PCM,
    /// ready to feed into a <see cref="PcmTransformPipeline"/> or an <c>AudioCutter</c> sink at
    /// <c>bitsPerSample: 32</c>.
    ///
    /// <para>
    /// Out-of-range values are clamped: <see cref="float.PositiveInfinity"/> and any value above 1.0
    /// clamp to 1.0 (<see cref="int.MaxValue"/>), <see cref="float.NegativeInfinity"/> and any value
    /// below -1.0 clamp to -1.0 (-<see cref="int.MaxValue"/>). <see cref="float.NaN"/> -- not a valid
    /// sample under any convention, but not impossible to receive from a synthesized or third-party
    /// source -- maps to 0 (digital silence) rather than propagating <see cref="double.NaN"/>'s
    /// unspecified <see langword="int"/> conversion result (a plain <c>(int)double.NaN</c> cast is
    /// undefined-ish by the C# spec and, in practice on .NET, evaluates to <see cref="int.MinValue"/> --
    /// full-scale noise, not silence).
    /// </para>
    /// </summary>
    public static int[] FromFloat(ReadOnlySpan<float> floatSamples)
    {
        var result = new int[floatSamples.Length];
        for (var i = 0; i < floatSamples.Length; i++)
        {
            result[i] = ClampToNativeInt32(floatSamples[i]);
        }

        return result;
    }

    // Shared by FromFloat and WavReader's float-WAV decode path (see WavReader.Float32ToInt32) so both
    // apply the identical NaN/Infinity/out-of-range convention.
    internal static int ClampToNativeInt32(float sample)
    {
        if (float.IsNaN(sample)) return 0;

        var clamped = Math.Clamp((double)sample, -1.0, 1.0);
        return (int)(clamped * int.MaxValue);
    }

    /// <summary>
    /// Converts int PCM at the given native bit depth (8, 16, 24, or 32 -- see
    /// <see cref="BitDepthFormatTransform.GetNativeRange"/>) into normalized float samples.
    /// </summary>
    public static float[] ToFloat(ReadOnlySpan<int> samples, int bitsPerSample)
    {
        var (_, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            result[i] = (float)(samples[i] / (double)maxValue);
        }

        return result;
    }
}
