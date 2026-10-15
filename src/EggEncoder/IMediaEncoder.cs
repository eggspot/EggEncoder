using EggEncoder.Results;

namespace EggEncoder
{
    public interface IMediaEncoder
    {
        Task<ProbeResult> Probe(string filePath);

        /// <summary>
        /// Same as <see cref="Probe(string)"/>, but observes <paramref name="cancellationToken"/>.
        /// <c>NativeEncoder</c>'s own implementation checks it once up front (before any decode
        /// work starts) -- for a large file, most of the time in <see cref="Probe(string)"/> is
        /// spent decoding for <c>ProbeResult.Waveform</c>/<c>PeakAmplitude</c>/etc, which this
        /// overload does not yet check the token against mid-decode (unlike
        /// <see cref="ConvertFile(string, string, System.Threading.CancellationToken)"/>/
        /// <see cref="CutFile(string, string, int, int, System.Threading.CancellationToken)"/>,
        /// which do -- see <c>docs/feature-roadmap.md</c> item 5 for that scope boundary). A
        /// default interface method so an existing implementer other than <c>NativeEncoder</c>
        /// doesn't need to change: the default just ignores the token and calls
        /// <see cref="Probe(string)"/>, the same "safe no-op unless overridden" shape
        /// <c>IPcmTransform.Flush()</c> already establishes in this project.
        /// </summary>
        Task<ProbeResult> Probe(string filePath, CancellationToken cancellationToken) => Probe(filePath);

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

        /// <summary>Same as <see cref="Probe(Stream, AudioFormat)"/>, but observes <paramref name="cancellationToken"/> -- see <see cref="Probe(string, System.Threading.CancellationToken)"/>'s own doc comment for the same "checked once up front, not mid-decode" scope.</summary>
        Task<ProbeResult> Probe(Stream source, AudioFormat format, CancellationToken cancellationToken) => Probe(source, format);

        Task ConvertFile(string sourceFilePath, string destFilePath);

        /// <summary>
        /// Same as <see cref="ConvertFile(string, string)"/>, but observes <paramref name="cancellationToken"/>
        /// throughout the actual decode/encode loop (checked once per decoded block, which -- every
        /// codec here decoding in frame-sized chunks on the order of tens of milliseconds of audio --
        /// means cancellation takes effect promptly, not just at the very start). A default interface
        /// method so an existing implementer other than <c>NativeEncoder</c> doesn't need to change:
        /// the default just ignores the token and calls <see cref="ConvertFile(string, string)"/>.
        /// </summary>
        Task ConvertFile(string sourceFilePath, string destFilePath, CancellationToken cancellationToken) => ConvertFile(sourceFilePath, destFilePath);

        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds);

        /// <summary>Same as <see cref="CutFile(string, string, int, int)"/>, but observes <paramref name="cancellationToken"/> the same per-block way <see cref="ConvertFile(string, string, System.Threading.CancellationToken)"/> does.</summary>
        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds, CancellationToken cancellationToken) => CutFile(sourceFilePath, destFilePath, startInSeconds, endInSeconds);
    }
}
