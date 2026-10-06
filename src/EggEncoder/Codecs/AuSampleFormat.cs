namespace EggEncoder.Codecs
{
    /// <summary>
    /// Selects the on-disk sample representation for an AU (<c>.au</c>) destination written by
    /// <see cref="Au.AuWriter.Create(string, int, int, int, long, AuSampleFormat)"/> and
    /// <see cref="AudioCutter.Convert(string, string, AuSampleFormat)"/>/<see cref="CutOptions.DestinationAuFormat"/>.
    /// Mirrors <see cref="WavSampleFormat"/>'s shape exactly (AU has no little-endian variant the way
    /// AIFC's <c>sowt</c> gives <see cref="AiffSampleFormat"/> one -- AU is always big-endian) but is its
    /// own type, named for the format it actually applies to, the same convention
    /// <see cref="AiffSampleFormat"/> already follows alongside <see cref="WavSampleFormat"/>.
    ///
    /// Internally, processing always stays <see langword="int"/>[]-based at this codebase's usual
    /// 32-bit native-range scale for <see cref="Float32"/> and <see cref="Float64"/> alike -- see
    /// <see cref="AiffSampleFormat"/>'s own remarks on why both require the pipeline/source bit depth to
    /// be 32, not 64.
    /// </summary>
    public enum AuSampleFormat
    {
        /// <summary>Signed integer PCM at the pipeline's/source's bit depth (8, 16, 24, or 32-bit). The long-standing default.</summary>
        Integer,

        /// <summary>32-bit IEEE float, big-endian. Requires the destination's bit depth to be 32.</summary>
        Float32,

        /// <summary>64-bit IEEE float, big-endian. Requires the destination's bit depth to be 32 -- see <see cref="AiffSampleFormat.Float64"/>'s remarks for why 64 is never the required value.</summary>
        Float64,

        /// <summary>G.711 mu-law companded PCM (8 bits on disk). Requires the destination's bit depth to be 16.</summary>
        MuLaw,

        /// <summary>G.711 A-law companded PCM (8 bits on disk). Requires the destination's bit depth to be 16.</summary>
        ALaw
    }
}
