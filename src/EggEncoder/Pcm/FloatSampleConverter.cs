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
    /// Converts normalized float samples (expected range -1.0..1.0; out-of-range values are clamped,
    /// matching how <c>WavReader</c> decodes a float WAV) into 32-bit-native-range int PCM, ready to
    /// feed into a <see cref="PcmTransformPipeline"/> or an <c>AudioCutter</c> sink at
    /// <c>bitsPerSample: 32</c>.
    /// </summary>
    public static int[] FromFloat(ReadOnlySpan<float> floatSamples)
    {
        var result = new int[floatSamples.Length];
        for (var i = 0; i < floatSamples.Length; i++)
        {
            var clamped = Math.Clamp((double)floatSamples[i], -1.0, 1.0);
            result[i] = (int)(clamped * int.MaxValue);
        }

        return result;
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
