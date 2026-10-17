using EggEncoder.Codecs.Aac;

namespace EggEncoder.Streaming.Aac
{
    /// <summary>
    /// Push-style incremental AAC-LC encoder -- the <see cref="IStreamingEncoder"/> counterpart to
    /// <see cref="AacEncoderSession"/>, emitting each encoded access unit (no ADTS framing) via a
    /// callback instead of writing ADTS-framed bytes to one destination <see cref="Stream"/>.
    /// Internally reuses <see cref="AacFrameEncoder"/>'s own MDCT/quantization logic unchanged --
    /// only how each encoded frame is delivered differs from <see cref="AacEncoderSession"/>. Scoped
    /// to mono AAC-LC, 44100Hz/48000Hz only, the exact same scope <see cref="AacEncoderSession"/>
    /// already has. See docs/streaming-plan.md item 1.
    /// </summary>
    public sealed class AacStreamingEncoder : IStreamingEncoder
    {
        /// <summary>
        /// This encoder's fixed, one-frame MDCT lookahead: the very first emitted frame windows an
        /// all-zero "previous block" against the first <see cref="SamplesPerFrame"/> real samples, so
        /// every frame's true presentation time is one frame behind its encode order alone. Confirmed
        /// empirically (see <c>AacEncoderTest.Encode_ThenDecode_Should_ReconstructToneSignal_UpToScale</c>'s
        /// own cross-correlation, which only reaches an acceptable SNR once this exact offset is
        /// compensated for) rather than assumed from the MDCT architecture description alone -- see
        /// docs/streaming-plan.md "Prerequisites."
        /// </summary>
        public const int EncoderDelaySamples = AacFrameEncoder.CoefficientCount;

        private readonly AacFrameEncoder _frameEncoder;
        private readonly EncodedAudioFrameCallback _onFrameEncoded;
        private bool _completed;
        private bool _disposed;

        private AacStreamingEncoder(int channels, int sampleRate, EncodedAudioFrameCallback onFrameEncoded)
        {
            Channels = channels;
            SampleRate = sampleRate;
            _onFrameEncoded = onFrameEncoded;
            _frameEncoder = new AacFrameEncoder(channels, sampleRate, EmitFrame);
        }

        /// <exception cref="ArgumentNullException"><paramref name="onFrameEncoded"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not 44100 or 48000.</exception>
        public static IStreamingEncoder Create(int channels, int sampleRate, EncodedAudioFrameCallback onFrameEncoded)
        {
            ArgumentNullException.ThrowIfNull(onFrameEncoded);
            AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            return new AacStreamingEncoder(channels, sampleRate, onFrameEncoded);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int SamplesPerFrame => AacFrameEncoder.CoefficientCount;

        public void Write(ReadOnlySpan<int> interleavedSamples)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot write more samples after Complete() has been called.");
            }

            foreach (var sample in interleavedSamples)
            {
                _frameEncoder.WriteSample((short)sample);
            }
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _frameEncoder.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frameEncoder.Dispose();
        }

        private void EmitFrame(byte[] payload, int sampleCount, long frameIndex)
        {
            _onFrameEncoded(new EncodedAudioFrame(payload, sampleCount, frameIndex));
        }
    }
}
