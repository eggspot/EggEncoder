using EggEncoder.Results;

namespace EggEncoder
{
    public interface IMediaEncoder
    {
        Task<ProbeResult> Probe(string filePath);

        /// <summary>
        /// Probes an audio source from an already-open <see cref="Stream"/> instead of a file path --
        /// for a source that isn't a local file (an HTTP response body, blob storage, an in-memory
        /// buffer). Unlike the path-based overload, <paramref name="format"/> must be given explicitly
        /// since a <see cref="Stream"/> has no file extension to dispatch on. Does NOT take ownership
        /// of <paramref name="source"/> -- the caller opened it, so the caller is responsible for
        /// disposing it. Not every <see cref="AudioFormat"/> is supported yet from a <see cref="Stream"/>
        /// (see <c>docs/feature-roadmap.md</c> item 1 for status); an unsupported one throws
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        Task<ProbeResult> Probe(Stream source, AudioFormat format);

        Task ConvertFile(string sourceFilePath, string destFilePath);

        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds);
    }
}
