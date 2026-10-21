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
        /// disposing it. Every defined <see cref="AudioFormat"/> can be probed from a <see cref="Stream"/>
        /// (see <c>docs/feature-roadmap.md</c> item 1); an undefined value throws
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

        /// <summary>
        /// Same as <see cref="ConvertFile(string, string)"/>, but between a source <see cref="Stream"/>
        /// and a destination <see cref="Stream"/> -- for either side that isn't a local file (an HTTP
        /// response body, blob storage, an in-memory buffer). Neither stream is disposed by this call --
        /// the caller opened them, so the caller is responsible for disposing them. Unlike the path-based
        /// overload, <paramref name="sourceFormat"/>/<paramref name="destFormat"/> must be given
        /// explicitly, the same reason <see cref="Probe(Stream, AudioFormat)"/> needs one. Not every
        /// <see cref="AudioFormat"/> is supported yet on either side (see <c>docs/feature-roadmap.md</c>
        /// item 1 for status); an unsupported one throws <see cref="NotSupportedException"/>.
        /// </summary>
        Task ConvertFile(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat);

        /// <summary>Same as <see cref="ConvertFile(Stream, AudioFormat, Stream, AudioFormat)"/>, but observes <paramref name="cancellationToken"/> once per decoded block, the same way <see cref="ConvertFile(string, string, System.Threading.CancellationToken)"/> does. A default interface method so an existing implementer other than <c>NativeEncoder</c> doesn't need to change.</summary>
        Task ConvertFile(Stream source, AudioFormat sourceFormat, Stream dest, AudioFormat destFormat, CancellationToken cancellationToken) => ConvertFile(source, sourceFormat, dest, destFormat);

        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds);

        /// <summary>Same as <see cref="CutFile(string, string, int, int)"/>, but observes <paramref name="cancellationToken"/> the same per-block way <see cref="ConvertFile(string, string, System.Threading.CancellationToken)"/> does.</summary>
        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds, CancellationToken cancellationToken) => CutFile(sourceFilePath, destFilePath, startInSeconds, endInSeconds);

        /// <summary>
        /// Same as <see cref="CutFile(string, string, int, int)"/>, but between a source <see cref="Stream"/>
        /// and a destination <see cref="Stream"/> -- see <see cref="ConvertFile(Stream, AudioFormat, Stream, AudioFormat)"/>'s
        /// own doc comment for the ownership/format-parameter rationale, which applies identically here.
        /// Returns whether a file was produced (the requested range may be entirely outside the source's
        /// duration), the same as the path-based overload.
        /// </summary>
        Task<bool> CutFile(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat);

        /// <summary>Same as <see cref="CutFile(Stream, AudioFormat, int, int, Stream, AudioFormat)"/>, but observes <paramref name="cancellationToken"/> once per decoded block. A default interface method so an existing implementer other than <c>NativeEncoder</c> doesn't need to change.</summary>
        Task<bool> CutFile(Stream source, AudioFormat sourceFormat, int startInSeconds, int endInSeconds, Stream dest, AudioFormat destFormat, CancellationToken cancellationToken) => CutFile(source, sourceFormat, startInSeconds, endInSeconds, dest, destFormat);
    }
}
