using EggEncoder.Codecs.Flac;

namespace EggEncoder.Streaming.Flac
{
    /// <summary>
    /// Push-style incremental FLAC encoder -- the <see cref="IStreamingEncoder"/> counterpart to
    /// <see cref="FlacEncoderSession"/>, emitting each encoded frame via a callback instead of
    /// writing bytes to one destination file. Same codec scope as <see cref="FlacEncoderSession"/>:
    /// any channel count, 1-32 bits per sample. See docs/streaming-plan.md item 8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Aac.AacStreamingEncoder"/>/<see cref="Opus.OpusStreamingEncoder"/>/
    /// <see cref="Mp3.Mp3StreamingEncoder"/>, this encoder's raw frames are genuinely <b>not</b>
    /// independently decodable: <see cref="FlacFrameEncoder"/>'s own frame header (unchanged here)
    /// always writes sample-rate-code 0 and bits-per-sample-code 0 ("get both from STREAMINFO")
    /// rather than FLAC's own available explicit escape codes, so a decoder needs a real STREAMINFO
    /// block to know either value at all -- a genuine, pre-existing characteristic of this
    /// project's own frame encoder, not something introduced here. Building that STREAMINFO (a
    /// whole-stream, container-level concept: total duration, a whole-stream MD5, min/max block
    /// size) is deliberately out of scope for this raw packet-level encoder, the same "container
    /// concerns belong to a downstream muxer" boundary every other <see cref="IStreamingEncoder"/>
    /// already draws -- a caller that needs a real, playable FLAC stream must construct one itself
    /// using <see cref="Channels"/>/<see cref="SampleRate"/>/this type's own <c>bitsPerSample</c>
    /// (not itself exposed by <see cref="IStreamingEncoder"/>, so a caller must already know it,
    /// the same value it passed to <see cref="Create"/>).
    /// </para>
    /// <para>
    /// Also unlike the other three: FLAC's FIXED predictors (this project's own encoder uses no
    /// other kind -- see <see cref="FlacFrameEncoder"/>'s own doc comment) are purely causal,
    /// looking only at already-encoded samples within the same block, never "future" samples --
    /// so there is no encoder lookahead to drain at all (<see cref="EncoderDelaySamples"/> is
    /// always <c>0</c>), and FLAC's own frame format natively allows a final block shorter than
    /// the nominal size with no padding, unlike AAC-LC/Opus/MP3's own fixed-or-padded frame sizes.
    /// <see cref="Complete"/> therefore just flushes whatever's left pending, nothing more.
    /// </para>
    /// </remarks>
    public sealed class FlacStreamingEncoder : IStreamingEncoder
    {
        // Matches FlacEncoderSession's own BlockSize exactly -- the same "4096 frames per block"
        // convention this codebase's own various encoder sessions already share.
        private const int BlockSize = 4096;

        /// <summary>Always <c>0</c> -- FIXED predictors are purely causal, so there is no lookahead to drain (see this type's own doc comment).</summary>
        public const int EncoderDelaySamples = 0;

        private readonly int _bitsPerSample;
        private readonly EncodedAudioFrameCallback _onFrameEncoded;
        private readonly int[] _pendingInterleaved;

        private int _pendingFrameCount;
        private long _frameIndex;
        private bool _completed;

        private FlacStreamingEncoder(int channels, int sampleRate, int bitsPerSample, EncodedAudioFrameCallback onFrameEncoded)
        {
            Channels = channels;
            SampleRate = sampleRate;
            _bitsPerSample = bitsPerSample;
            _onFrameEncoded = onFrameEncoded;
            _pendingInterleaved = new int[BlockSize * channels];
        }

        /// <exception cref="ArgumentNullException"><paramref name="onFrameEncoded"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is less than 1, or <paramref name="bitsPerSample"/> is not 1-32.</exception>
        public static IStreamingEncoder Create(int channels, int sampleRate, int bitsPerSample, EncodedAudioFrameCallback onFrameEncoded)
        {
            ArgumentNullException.ThrowIfNull(onFrameEncoded);

            if (channels < 1)
            {
                throw new NotSupportedException($"{channels} channels requested; FLAC requires at least 1.");
            }

            // Matches FlacEncoder.OpenSession's own check exactly: STREAMINFO's bits-per-sample
            // field is 5 bits wide, storing (bitsPerSample - 1) -- an out-of-range value here would
            // silently wrap into a different, wrong-but-valid-looking bit depth in any STREAMINFO a
            // caller later builds from this same value, rather than failing.
            if (bitsPerSample is < 1 or > 32)
            {
                throw new NotSupportedException($"{bitsPerSample} bits per sample requested; FLAC's STREAMINFO can only represent 1-32.");
            }

            return new FlacStreamingEncoder(channels, sampleRate, bitsPerSample, onFrameEncoded);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int SamplesPerFrame => BlockSize;

        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
        public void Write(ReadOnlySpan<int> interleavedSamples)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot write more samples after Complete() has been called.");
            }

            var frameCount = interleavedSamples.Length / Channels;
            var sourceOffset = 0;
            while (sourceOffset < frameCount)
            {
                var take = Math.Min(frameCount - sourceOffset, BlockSize - _pendingFrameCount);
                interleavedSamples.Slice(sourceOffset * Channels, take * Channels)
                    .CopyTo(_pendingInterleaved.AsSpan(_pendingFrameCount * Channels, take * Channels));

                _pendingFrameCount += take;
                sourceOffset += take;

                if (_pendingFrameCount == BlockSize)
                {
                    FlushPendingBlock();
                }
            }
        }

        /// <summary>
        /// Encodes whatever partial block remains buffered as one final, possibly-short frame --
        /// FLAC's own frame format allows this natively, with no padding needed at all (unlike
        /// AAC-LC/Opus/MP3). Safe to call with nothing ever written (emits no frame in that case).
        /// Safe to call more than once -- every call after the first is a no-op.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            if (_pendingFrameCount > 0)
            {
                FlushPendingBlock();
            }
        }

        public void Dispose()
        {
            // Holds no unmanaged/disposable resource of its own.
        }

        private void FlushPendingBlock()
        {
            var channelSamples = new int[Channels][];
            for (var c = 0; c < Channels; c++)
            {
                channelSamples[c] = new int[_pendingFrameCount];
            }

            for (var i = 0; i < _pendingFrameCount; i++)
            {
                var baseIndex = i * Channels;
                for (var c = 0; c < Channels; c++)
                {
                    channelSamples[c][i] = _pendingInterleaved[baseIndex + c];
                }
            }

            var frameBytes = FlacFrameEncoder.EncodeFrame(channelSamples, _pendingFrameCount, _bitsPerSample, _frameIndex);
            var sampleCount = _pendingFrameCount;
            _pendingFrameCount = 0;

            _onFrameEncoded(new EncodedAudioFrame(frameBytes, sampleCount, _frameIndex++));
        }
    }
}
