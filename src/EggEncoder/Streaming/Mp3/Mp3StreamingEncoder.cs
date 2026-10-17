using System.Runtime.InteropServices;
using EggEncoder.Codecs.Mp3;

namespace EggEncoder.Streaming.Mp3
{
    /// <summary>
    /// Push-style incremental MP3 encoder -- the <see cref="IStreamingEncoder"/> counterpart to
    /// <see cref="Mp3EncoderSession"/>, emitting each encoded MPEG frame (self-delimiting, no extra
    /// container framing needed -- unlike AAC's own ADTS) via a callback instead of writing bytes
    /// to one destination file. Same codec scope as <see cref="Mp3EncoderSession"/>: mono or
    /// stereo, 32000/44100/48000Hz, CBR only. See docs/streaming-plan.md item 8.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Mp3EncoderSession"/>'s own file output, this encoder writes no dedicated
    /// "Info tag" (LAME-tag) header frame and no ID3v2 tag at all -- both are container/file-level
    /// conventions (gapless-playback metadata meant to be read back from a complete, seekable
    /// file) with no equivalent for a raw, open-ended frame stream. <see cref="Mp3Encoder.EncoderDelaySamples"/>
    /// (the same fixed <c>1680</c>-sample value <see cref="Mp3EncoderSession"/> itself writes into
    /// that tag) remains the right value for a muxer here to use directly. Unlike
    /// <see cref="Opus.OpusStreamingEncoder"/>, this encoder reports each frame's own *real* sample
    /// count rather than always the nominal <see cref="SamplesPerFrame"/> -- tracking it costs
    /// nothing extra here, the same way it already doesn't for <see cref="Aac.AacStreamingEncoder"/>,
    /// since <see cref="Mp3EncoderSession.Finish"/>'s own existing design already computes the real
    /// remaining count before padding it. <see cref="Complete"/>'s own mandatory flush frame (see
    /// its own doc comment) is purely synthetic and reports a real sample count of <c>0</c>.
    /// </remarks>
    public sealed class Mp3StreamingEncoder : IStreamingEncoder
    {
        private const int FrameSamples = 1152;

        private readonly Mp3FrameEncoder _frameEncoder;
        private readonly EncodedAudioFrameCallback _onFrameEncoded;
        private readonly List<int> _pendingInterleaved = [];

        private long _frameIndex;
        private bool _completed;

        private Mp3StreamingEncoder(int channels, int sampleRate, Mp3FrameEncoder frameEncoder, EncodedAudioFrameCallback onFrameEncoded)
        {
            Channels = channels;
            SampleRate = sampleRate;
            _frameEncoder = frameEncoder;
            _onFrameEncoded = onFrameEncoded;
        }

        /// <exception cref="ArgumentNullException"><paramref name="onFrameEncoded"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1 or 2, <paramref name="sampleRate"/> is not 32000/44100/48000, or <paramref name="bitRateKbps"/> is not a valid MPEG-1 Layer III bitrate.</exception>
        public static IStreamingEncoder Create(int channels, int sampleRate, EncodedAudioFrameCallback onFrameEncoded, int bitRateKbps = Mp3Encoder.DefaultBitRateKbps)
        {
            ArgumentNullException.ThrowIfNull(onFrameEncoded);

            var frameEncoder = new Mp3FrameEncoder(channels, sampleRate, bitRateKbps);

            return new Mp3StreamingEncoder(channels, sampleRate, frameEncoder, onFrameEncoded);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int SamplesPerFrame => FrameSamples;

        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
        public void Write(ReadOnlySpan<int> interleavedSamples)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot write more samples after Complete() has been called.");
            }

            foreach (var sample in interleavedSamples)
            {
                _pendingInterleaved.Add(sample);
            }

            while (_pendingInterleaved.Count >= FrameSamples * Channels)
            {
                EmitFrame(realSampleCount: FrameSamples);
            }
        }

        /// <summary>
        /// Pads and encodes whatever partial frame remains buffered (reporting its own true,
        /// possibly-short real sample count), then -- as long as anything was ever written --
        /// unconditionally encodes one further, purely synthetic frame of silence: this encoder's
        /// own one-granule MDCT history needs one full frame of "future" context to fully flush
        /// the true tail of real audio into a decodable position, confirmed necessary (not just a
        /// precaution) by the same direct measurement <see cref="Mp3EncoderSession.Finish"/>'s own
        /// doc comment describes. Safe to call with nothing ever written (emits no frame in that
        /// case). Safe to call more than once -- every call after the first is a no-op.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            if (_pendingInterleaved.Count == 0 && _frameIndex == 0)
            {
                return;
            }

            var remainingFrames = _pendingInterleaved.Count / Channels;
            if (remainingFrames > 0)
            {
                PadPendingTo(FrameSamples);
                EmitFrame(realSampleCount: remainingFrames);
            }

            PadPendingTo(FrameSamples);
            EmitFrame(realSampleCount: 0);
        }

        public void Dispose()
        {
            // Holds no unmanaged/disposable resource of its own -- Mp3FrameEncoder has none either.
        }

        private void PadPendingTo(int frameSamples)
        {
            var targetCount = frameSamples * Channels;
            for (var i = _pendingInterleaved.Count; i < targetCount; i++)
            {
                _pendingInterleaved.Add(0);
            }
        }

        private void EmitFrame(int realSampleCount)
        {
            var frameBytes = _frameEncoder.EncodeFrame(CollectionsMarshal.AsSpan(_pendingInterleaved)[..(FrameSamples * Channels)]);
            _pendingInterleaved.RemoveRange(0, FrameSamples * Channels);

            _onFrameEncoded(new EncodedAudioFrame(frameBytes, realSampleCount, _frameIndex++));
        }
    }
}
