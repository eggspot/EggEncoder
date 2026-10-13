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
        Float32,

        /// <summary>G.711 mu-law companded PCM (8 bits on disk). Requires the destination's bit depth to be 16 (the input scale the companding formula expects).</summary>
        MuLaw,

        /// <summary>G.711 A-law companded PCM (8 bits on disk). Requires the destination's bit depth to be 16 (the input scale the companding formula expects).</summary>
        ALaw,

        /// <summary>
        /// IMA ADPCM (<c>WAVE_FORMAT_IMA_ADPCM</c>, 4 bits on disk). Requires the destination's bit
        /// depth to be 16 (the input scale its quantizer expects) and mono or stereo only, matching
        /// <c>WavReader</c>'s own decode-side restriction. Unlike <see cref="MuLaw"/>/<see cref="ALaw"/>,
        /// this is block-structured (a fixed number of frames per block, buffered internally until a
        /// full block is ready to encode, with the final block padded if needed) rather than one sample
        /// at a time -- see <c>ImaAdpcmEncoder</c>.
        /// </summary>
        ImaAdpcm,

        /// <summary>
        /// MS ADPCM (<c>WAVE_FORMAT_ADPCM</c>, 4 bits on disk). Requires the destination's bit depth
        /// to be 16 (the input scale its quantizer expects) and mono or stereo only, matching
        /// <c>WavReader</c>'s own decode-side restriction. Block-structured the same way
        /// <see cref="ImaAdpcm"/> is, but a genuinely different algorithm -- linear prediction from a
        /// coefficient pair written into the file's own <c>fmt</c> chunk extension, rather than
        /// <see cref="ImaAdpcm"/>'s universal fixed step table -- see <c>MsAdpcmEncoder</c>.
        /// </summary>
        MsAdpcm,

        /// <summary>
        /// Yamaha ADPCM (<c>WAVE_FORMAT_YAMAHA_ADPCM</c>, 4 bits on disk). Requires the destination's
        /// bit depth to be 16 (the input scale its quantizer expects) and mono or stereo only,
        /// matching <c>WavReader</c>'s own decode-side restriction. Unlike <see cref="ImaAdpcm"/>/
        /// <see cref="MsAdpcm"/>, this format has no block structure at all -- predictor/step state
        /// carries continuously for the whole stream, with no per-block header or
        /// <c>wSamplesPerBlock</c> 'fmt' chunk extension -- see <c>YamahaAdpcmEncoder</c>.
        /// </summary>
        YamahaAdpcm
    }
}
