namespace EggEncoder.Codecs
{
    /// <summary>
    /// Selects the on-disk sample representation for a WAV destination written by
    /// <see cref="AudioCutter.Convert(string, string, WavSampleFormat)"/> and its pipeline/<c>Mix</c>/
    /// <c>Concatenate</c> counterparts. Only meaningful for a <c>.wav</c> destination -- every other
    /// destination format is always its own fixed encoding, so requesting <see cref="Float32"/> against
    /// a non-WAV destination throws <see cref="NotSupportedException"/>.
    ///
    /// Internally, processing always stays <see langword="int"/>[]-based at this codebase's usual
    /// 32-bit native-range scale (see <see cref="EggEncoder.Pcm.FloatSampleConverter"/>'s doc comment);
    /// this selects only how <c>WavWriter</c> encodes that int data on write, or (for a WAV source) how
    /// <c>WavReader</c> already decodes it on read -- decoding a float WAV source has always worked
    /// transparently through <c>Convert</c>/<c>Cut</c>/<c>Mix</c>/<c>Concatenate</c>, regardless of this
    /// destination-side setting.
    /// </summary>
    public enum WavSampleFormat
    {
        /// <summary>Integer PCM at the pipeline's/source's bit depth (8, 16, 24, or 32-bit). The long-standing default.</summary>
        Integer,

        /// <summary>32-bit IEEE float. Requires the destination's bit depth to be 32 (the pipeline/source's own, or overridden with a <see cref="EggEncoder.Pcm.BitDepthFormatTransform"/> to 32).</summary>
        Float32
    }
}
