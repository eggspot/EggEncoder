namespace EggEncoder.Codecs
{
    /// <summary>
    /// Selects the on-disk sample representation for an AIFF/AIFC destination written by
    /// <see cref="AiffWriter.Create(string, int, int, int, long, AiffSampleFormat)"/> and
    /// <see cref="AudioCutter.Convert(string, string, AiffSampleFormat)"/>/<see cref="CutOptions.DestinationAiffFormat"/>.
    ///
    /// <see cref="Integer"/> (the default) writes a plain FORM/AIFF container -- byte-for-byte what
    /// this project has always written, before AIFC existed here. Every other value writes a
    /// FORM/AIFC container instead (with the mandatory FVER chunk and an extended COMM chunk carrying
    /// the compression type), regardless of which of the <c>.aiff</c>/<c>.aif</c>/<c>.aifc</c>
    /// extensions the destination file actually has -- the same "the enum picks the on-disk shape,
    /// independent of which exact extension spelling was used" convention <see cref="WavSampleFormat"/>
    /// already follows for WAV.
    ///
    /// Internally, processing always stays <see langword="int"/>[]-based at this codebase's usual
    /// 32-bit native-range scale (see <see cref="EggEncoder.Pcm.FloatSampleConverter"/>'s doc comment)
    /// for <see cref="Float32"/> and <see cref="Float64"/> alike -- a 64-bit on-disk sample is strictly
    /// a wider/more precise encoding of that same -1.0..1.0 range, not a different logical bit depth
    /// this project's <see langword="int"/>[] model has any representation for, so both require the
    /// pipeline/source bit depth to be 32, exactly like <see cref="Float32"/>.
    /// </summary>
    public enum AiffSampleFormat
    {
        /// <summary>Big-endian signed integer PCM at the pipeline's/source's bit depth (8, 16, 24, or 32-bit). The long-standing default; writes plain FORM/AIFF.</summary>
        Integer,

        /// <summary>Little-endian signed integer PCM ('sowt') at the pipeline's/source's bit depth (8, 16, 24, or 32-bit) -- otherwise identical to <see cref="Integer"/>. Writes FORM/AIFC.</summary>
        LittleEndianInteger,

        /// <summary>32-bit IEEE float, big-endian ('fl32'). Requires the destination's bit depth to be 32. Writes FORM/AIFC.</summary>
        Float32,

        /// <summary>64-bit IEEE float, big-endian ('fl64'). Requires the destination's bit depth to be 32 -- see this type's own remarks for why 64 is never the required value. Writes FORM/AIFC.</summary>
        Float64,

        /// <summary>G.711 mu-law companded PCM ('ulaw', 8 bits on disk). Requires the destination's bit depth to be 16. Writes FORM/AIFC.</summary>
        MuLaw,

        /// <summary>G.711 A-law companded PCM ('alaw', 8 bits on disk). Requires the destination's bit depth to be 16. Writes FORM/AIFC.</summary>
        ALaw
    }
}
