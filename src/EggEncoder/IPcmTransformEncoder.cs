using EggEncoder.Codecs;
using EggEncoder.Pcm;

namespace EggEncoder
{
    /// <summary>
    /// Optional PCM-processing capabilities (resampling, gain/normalization, channel remixing,
    /// bit-depth conversion, fades, mixing, concatenation) layered on top of <see cref="IMediaEncoder"/>.
    ///
    /// Kept as a separate interface, implemented by <see cref="NativeEncoder"/> alongside
    /// <see cref="IMediaEncoder"/>, so existing <see cref="IMediaEncoder"/> consumers and its DI
    /// registration are unaffected.
    /// </summary>
    public interface IPcmTransformEncoder
    {
        /// <summary>Same as <see cref="IMediaEncoder.ConvertFile"/>, but runs <paramref name="pipeline"/> over every decoded block first.</summary>
        Task ConvertFile(string sourceFilePath, string destFilePath, PcmTransformPipeline pipeline);

        /// <summary>Same as <see cref="IMediaEncoder.CutFile"/>, but applies an optional fade and transform pipeline to the retained range; returns whether a file was produced.</summary>
        Task<bool> CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds, CutOptions options);

        /// <summary>Mixes two or more same-format sources, with per-input gain, into one destination file.</summary>
        Task MixFiles(IReadOnlyList<MixInput> inputs, string destFilePath);

        /// <summary>Concatenates two or more same-format source files, in order, into one destination file.</summary>
        Task ConcatenateFiles(IReadOnlyList<string> sourceFilePaths, string destFilePath);
    }
}
