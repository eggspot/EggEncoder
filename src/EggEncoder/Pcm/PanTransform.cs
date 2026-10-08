namespace EggEncoder.Pcm;

/// <summary>
/// Adjusts stereo balance/pan: scales the left and right channels independently from a single
/// pan value in [-1.0, 1.0] (-1.0 = full left, 0.0 = center, +1.0 = full right). Stereo
/// (2-channel) input only.
/// AOT-safe, managed, allocation-free (in-place mutation) -- same shape as <see cref="VolumeTransform"/>.
/// </summary>
public sealed class PanTransform : IPcmTransform
{
    private readonly double _leftGain;
    private readonly double _rightGain;

    /// <param name="pan">-1.0 (full left) to +1.0 (full right); 0.0 is center.</param>
    /// <param name="law">How gain is distributed across the pan range (see <see cref="PanLaw"/>).</param>
    public PanTransform(double pan, PanLaw law = PanLaw.Linear)
    {
        if (pan < -1.0 || pan > 1.0)
            throw new ArgumentOutOfRangeException(nameof(pan), pan, "Pan must be between -1.0 and 1.0");

        Pan = pan;
        Law = law;

        switch (law)
        {
            case PanLaw.Linear:
                // Balance-style: the channel being panned TOWARD stays at unity gain; only the
                // opposite channel is attenuated, reaching silence at the extremes. Center (pan=0)
                // is a true no-op for both channels -- no volume dip, unlike EqualPower's center.
                _leftGain = pan <= 0 ? 1.0 : 1.0 - pan;
                _rightGain = pan >= 0 ? 1.0 : 1.0 + pan;
                break;

            case PanLaw.EqualPower:
                // Constant-power pan: left^2 + right^2 == 1 at every point, including center (where
                // each channel is attenuated by -3dB, cos(pi/4) = sin(pi/4) ~= 0.707) -- the
                // convention a true pan pot uses in most DAWs, avoiding the perceived loudness bump
                // Linear's own unity-gain center would otherwise have for genuinely panned (not just
                // balance-adjusted) content.
                var theta = (pan + 1.0) * Math.PI / 4.0;
                _leftGain = Math.Cos(theta);
                _rightGain = Math.Sin(theta);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(law), law, "Unsupported pan law");
        }
    }

    /// <summary>The pan value this transform was constructed with.</summary>
    public double Pan { get; }

    /// <summary>The pan law this transform was constructed with.</summary>
    public PanLaw Law { get; }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        // Validated before the no-op fast path below, even though a pan of exactly 0.0 under
        // PanLaw.Linear would otherwise be a true identity transform for any channel count --
        // silently passing through a mono buffer at pan=0 would hide a real pipeline
        // misconfiguration (this transform only ever makes sense for stereo) rather than surfacing
        // it, the same validate-first precedent ChannelRemixTransform.Apply already establishes.
        if (channels != 2)
            throw new ArgumentException($"PanTransform requires stereo (2-channel) input, but received {channels}", nameof(channels));

        if (_leftGain == 1.0 && _rightGain == 1.0) return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var baseIndex = frame * 2;
            buffer[baseIndex] = (int)Math.Clamp((double)buffer[baseIndex] * _leftGain, minValue, maxValue);
            buffer[baseIndex + 1] = (int)Math.Clamp((double)buffer[baseIndex + 1] * _rightGain, minValue, maxValue);
        }

        return (buffer, frameCount);
    }
}

/// <summary>How <see cref="PanTransform"/> distributes gain across its pan range.</summary>
public enum PanLaw
{
    /// <summary>Balance-style: only the opposite channel is attenuated; the channel panned toward stays at unity gain. No volume dip at center.</summary>
    Linear,

    /// <summary>Constant-power: left^2 + right^2 == 1 everywhere, including a -3dB-per-channel center. Matches how a true stereo pan pot behaves in most DAWs.</summary>
    EqualPower
}
