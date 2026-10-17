using Concentus;
using Concentus.Enums;

namespace EggEncoder.Streaming.Opus
{
    /// <summary>
    /// Push-style incremental Opus encoder -- the <see cref="IStreamingEncoder"/> counterpart to
    /// <see cref="Codecs.Opus.OpusEncoderSession"/>, emitting each encoded packet (no Ogg page
    /// framing) via a callback instead of writing Ogg-paged bytes to one destination file. Same
    /// scope as <see cref="Codecs.Opus.OpusEncoderSession"/>: fixed at 48kHz, mono or stereo,
    /// 16-bit PCM, channel mapping family 0. See docs/streaming-plan.md item 8.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Aac.AacStreamingEncoder"/>, every <see cref="EncodedAudioFrame.SampleCount"/>
    /// this encoder reports is always <see cref="SamplesPerFrame"/> (960), including the final
    /// frame <see cref="Complete"/> flushes -- Opus has no bitstream-level "short final frame"
    /// concept the way AAC-LC does (every encoded Opus packet is exactly one fixed frame
    /// duration); the real-vs-padding distinction for a partial tail only matters at the container
    /// level (an OggOpus file's own <c>pre_skip</c>/end trim), which is out of scope for this raw
    /// packet-level encoder, the same way it was out of scope for item 2's own fMP4 muxer relative
    /// to <see cref="Aac.AacStreamingEncoder"/>.
    /// </remarks>
    public sealed class OpusStreamingEncoder : IStreamingEncoder
    {
        private const int FixedSampleRate = 48000;
        private const int FrameLength = 960; // 20ms at 48kHz, one of Opus's fixed valid frame durations

        private readonly IOpusEncoder _encoder;
        private readonly EncodedAudioFrameCallback _onFrameEncoded;
        private readonly short[] _pendingSamples;
        private readonly byte[] _outputBuffer = new byte[4000]; // Opus packets are at most 1275 bytes; comfortably oversized

        private int _pendingCount;
        private long _frameIndex;
        private bool _completed;
        private bool _disposed;

        private OpusStreamingEncoder(int channels, EncodedAudioFrameCallback onFrameEncoded)
        {
            Channels = channels;
            _onFrameEncoded = onFrameEncoded;
            _encoder = OpusCodecFactory.CreateEncoder(FixedSampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            _pendingSamples = new short[FrameLength * channels];
        }

        /// <summary>This encoder's own fixed internal lookahead, in samples -- unlike AAC-LC's own fixed <c>1024</c>, Opus's lookahead is a per-instance value from the underlying encoder, not a project-wide constant.</summary>
        public int EncoderDelaySamples => _encoder.Lookahead;

        /// <exception cref="ArgumentNullException"><paramref name="onFrameEncoded"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1 or 2, or <paramref name="sampleRate"/> is not 48000.</exception>
        public static IStreamingEncoder Create(int channels, int sampleRate, EncodedAudioFrameCallback onFrameEncoded)
        {
            ArgumentNullException.ThrowIfNull(onFrameEncoded);

            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"{channels} channels requested; only mono and stereo Opus encoding is supported");
            }

            if (sampleRate != FixedSampleRate)
            {
                throw new NotSupportedException($"A {sampleRate}Hz source was requested; Opus encoding here is fixed at {FixedSampleRate}Hz -- resample first via ResamplingTransform");
            }

            return new OpusStreamingEncoder(channels, onFrameEncoded);
        }

        public int Channels { get; }

        public int SampleRate => FixedSampleRate;

        public int SamplesPerFrame => FrameLength;

        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
        public void Write(ReadOnlySpan<int> interleavedSamples)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot write more samples after Complete() has been called.");
            }

            BufferSamples(interleavedSamples);
        }

        private void BufferSamples(ReadOnlySpan<int> interleavedSamples)
        {
            var offset = 0;
            while (offset < interleavedSamples.Length)
            {
                var samplesToCopy = Math.Min(_pendingSamples.Length - _pendingCount, interleavedSamples.Length - offset);

                for (var i = 0; i < samplesToCopy; i++)
                {
                    _pendingSamples[_pendingCount + i] = (short)interleavedSamples[offset + i];
                }

                _pendingCount += samplesToCopy;
                offset += samplesToCopy;

                if (_pendingCount == _pendingSamples.Length)
                {
                    EncodeAndEmitFrame();
                }
            }
        }

        /// <summary>
        /// Drains the encoder's own fixed internal lookahead (feeding it through as if it were
        /// real input, the only way to push the genuine tail of the real input out of the
        /// encoder's internal state) and, if anything is still buffered afterward, zero-pads and
        /// encodes one final frame. Safe to call with nothing ever written (emits no frame in that
        /// case). Safe to call more than once -- every call after the first is a no-op.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            if (_pendingCount == 0 && _frameIndex == 0)
            {
                return;
            }

            var lookahead = _encoder.Lookahead;
            if (lookahead > 0)
            {
                BufferSamples(new int[lookahead * Channels]);
            }

            if (_pendingCount > 0)
            {
                Array.Clear(_pendingSamples, _pendingCount, _pendingSamples.Length - _pendingCount);
                EncodeAndEmitFrame();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encoder.Dispose();
        }

        private void EncodeAndEmitFrame()
        {
            var encodedLength = _encoder.Encode(_pendingSamples, FrameLength, _outputBuffer, _outputBuffer.Length);
            _pendingCount = 0;

            _onFrameEncoded(new EncodedAudioFrame(_outputBuffer.AsSpan(0, encodedLength), FrameLength, _frameIndex++));
        }
    }
}
