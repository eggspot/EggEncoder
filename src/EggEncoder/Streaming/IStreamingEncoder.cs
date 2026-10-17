namespace EggEncoder.Streaming
{
    /// <summary>
    /// Push-style incremental audio encoder: PCM in via <see cref="Write"/>, one encoded access unit
    /// out per call to the <see cref="EncodedAudioFrameCallback"/> supplied at construction, emitted
    /// as soon as a full codec-native frame's worth of samples has arrived. No end-of-file or
    /// seekability requirement on either side -- the counterpart to this project's existing
    /// whole-file encoder sessions (e.g. <see cref="Codecs.Aac.AacEncoderSession"/>) for a caller
    /// that wants the encoded bytes themselves, not a destination <see cref="Stream"/> or file
    /// written on its behalf (a container muxer, which needs each access unit individually to build
    /// its own sample tables, is the motivating case). See docs/streaming-plan.md item 1.
    /// </summary>
    public interface IStreamingEncoder : IDisposable
    {
        int Channels { get; }

        int SampleRate { get; }

        /// <summary>The nominal, fixed number of samples per encoded frame (e.g. 1024 for AAC-LC).</summary>
        int SamplesPerFrame { get; }

        /// <summary>
        /// Encodes as many complete frames as <paramref name="interleavedSamples"/> allows, buffering
        /// any remainder internally for the next call (or for <see cref="Complete"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
        void Write(ReadOnlySpan<int> interleavedSamples);

        /// <summary>
        /// Encodes whatever partial frame remains buffered as one final, possibly-short frame, then
        /// marks this session complete. Safe to call with nothing buffered (emits no frame in that
        /// case). Safe to call more than once -- every call after the first is a no-op.
        /// </summary>
        void Complete();
    }
}
