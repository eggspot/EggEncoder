namespace EggEncoder.Pcm;

/// <summary>
/// Rescales samples between bit depths at a pipeline boundary (e.g. a 24-bit source feeding a
/// 16-bit destination format).
///
/// Every decoder/writer pair in this codebase (see <c>WavReader</c>/<c>WavWriter</c>) already
/// represents a sample as a signed value sign-extended to its own bit depth's native range —
/// 8-bit is -128..127, 16-bit is -32768..32767, 24-bit is -8388608..8388607, and 32-bit (int or
/// IEEE float) spans the full <see cref="int"/> range. There is no separate "normalized" scale;
/// this transform only converts between those native ranges, by left-shifting to widen (exact,
/// no precision loss) or by scaled, rounded division to narrow.
///
/// AOT-safe, managed implementation.
/// </summary>
public sealed class BitDepthFormatTransform : IPcmTransform
{
    private readonly int _fromBits;
    private readonly int _toBits;

    /// <param name="fromBits">Bit depth of samples this transform receives (8, 16, 24, or 32).</param>
    /// <param name="toBits">Bit depth of samples this transform produces (8, 16, 24, or 32).</param>
    public BitDepthFormatTransform(int fromBits, int toBits)
    {
        ValidateBitDepth(fromBits, nameof(fromBits));
        ValidateBitDepth(toBits, nameof(toBits));

        _fromBits = fromBits;
        _toBits = toBits;
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => 0;     // passthrough — preserves input channels
    public int OutputBitsPerSample => _toBits;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (bitsPerSample != _fromBits)
            throw new ArgumentException($"Expected {_fromBits}-bit input but received {bitsPerSample}-bit", nameof(bitsPerSample));

        if (_fromBits == _toBits) return (buffer, frameCount);

        var n = frameCount * channels;

        if (_toBits > _fromBits)
        {
            var shift = _toBits - _fromBits;
            for (var i = 0; i < n; i++)
                buffer[i] <<= shift;
        }
        else
        {
            var shift = _fromBits - _toBits;
            var divisor = 1L << shift;
            var half = divisor / 2;
            var (min, max) = GetNativeRange(_toBits);
            for (var i = 0; i < n; i++)
            {
                // Widen to long first: value + half can overflow int32 when value is near int.MaxValue.
                long value = buffer[i];
                var rounded = value >= 0 ? (value + half) / divisor : -((-value + half) / divisor);
                buffer[i] = (int)Math.Clamp(rounded, min, max);
            }
        }

        return (buffer, frameCount);
    }

    /// <summary>
    /// The native (min, max) range for a given bit depth in this codebase's convention: signed,
    /// sign-extended to that bit depth (see this type's doc comment). Shared with
    /// <see cref="EggEncoder.Codecs.AudioCutter.Mix"/>, which needs the same range to clamp a mixed-down sum.
    /// </summary>
    public static (long Min, long Max) GetNativeRange(int bits)
    {
        ValidateBitDepth(bits, nameof(bits));
        return (-(1L << (bits - 1)), (1L << (bits - 1)) - 1);
    }

    private static void ValidateBitDepth(int bits, string paramName)
    {
        if (bits is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(paramName, bits, "Bit depth must be 8, 16, 24, or 32");
    }
}
