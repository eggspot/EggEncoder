namespace EggEncoder.Streaming
{
    /// <summary>
    /// One codec-native encoded access unit, with no container/transport framing applied (no ADTS,
    /// no fMP4 box, no MPEG-TS packetization -- those are added downstream by a muxer). A
    /// <c>ref struct</c> so emitting a frame through <see cref="EncodedAudioFrameCallback"/> never
    /// requires an allocation beyond the encoder's own payload buffer; a callback that needs the
    /// bytes to outlive its own call (every container muxer does) must copy <see cref="Payload"/>
    /// into its own buffer before returning. See docs/streaming-plan.md "Architecture overview."
    /// </summary>
    public readonly ref struct EncodedAudioFrame
    {
        public EncodedAudioFrame(ReadOnlySpan<byte> payload, int sampleCount, long frameIndex)
        {
            Payload = payload;
            SampleCount = sampleCount;
            FrameIndex = frameIndex;
        }

        /// <summary>The raw access unit bytes -- no container/transport framing.</summary>
        public ReadOnlySpan<byte> Payload { get; }

        /// <summary>
        /// The real number of PCM samples this frame represents. Equal to the encoder's own
        /// <see cref="IStreamingEncoder.SamplesPerFrame"/> for every frame except a final,
        /// zero-padded one emitted by <see cref="IStreamingEncoder.Complete"/>, which reports
        /// however many real samples were actually buffered.
        /// </summary>
        public int SampleCount { get; }

        /// <summary>0-based, monotonically increasing per encoder session.</summary>
        public long FrameIndex { get; }
    }

    /// <summary>Invoked once per encoded frame, synchronously, from within the <see cref="IStreamingEncoder"/> call that completed it.</summary>
    public delegate void EncodedAudioFrameCallback(EncodedAudioFrame frame);
}
