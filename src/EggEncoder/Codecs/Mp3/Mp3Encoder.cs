using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.Mp3
{
    // Pure managed MPEG-1 Layer III encoder, clean-room from the published ISO/IEC 11172-3 standard
    // text (see Mp3Tables/Mp3HuffmanTables/Mp3PolyphaseFilter/Mp3AliasReduction/Mp3Quantizer/
    // Mp3FrameEncoder's own doc comments for the per-stage provenance) -- no LAME, GroovyCodecs, or
    // any other encoder's source was consulted, per docs/managed-codec-rewrite-plan.md item 6.
    //
    // A deliberately simple CBR-only baseline: long blocks only (no block switching/transient
    // handling), independent (non-joint) stereo, no psychoacoustic model (global_gain alone is
    // searched to fit each granule's own fixed bit budget), and no bit-reservoir borrowing across
    // frames -- matching this project's own "correctness first, not yet compression-competitive"
    // precedent (see FlacEncoder's own items 2/3 history, and the WavPack encoder's own
    // single-decorrelation-term MVP choice). Correctness is the bar: every frame this encoder
    // writes decodes cleanly via this project's own Mp3Decoder (NLayer).
    public static class Mp3Encoder
    {
        public const int DefaultBitRateKbps = 320;
        private const int FramesPerBlock = 4096;

        /// <summary>
        /// This encoder's own fixed structural lookahead, in samples, as written into the
        /// de-facto-standard "LAME tag" encoder-delay field every <see cref="OpenSession"/> output
        /// now carries (see <see cref="Mp3EncoderSession"/>/<see cref="Mp3FrameEncoder"/>'s own doc
        /// comments for the byte layout, and <c>THIRD-PARTY-NOTICES.md</c>'s MP3 LAME-tag entry for
        /// where this value was verified). Composed of two pieces, both intrinsic to this encoder's
        /// own design, not configurable: <c>1152</c> (one full frame) for the dedicated Info-tag
        /// header frame every output file now starts with, plus <c>528</c> for this encoder's own
        /// one-granule MDCT history warm-up (<see cref="Mp3FrameEncoder"/>'s own per-channel
        /// <c>_subbandHistory</c>/<see cref="Mp3PolyphaseFilter"/>'s own 512-sample analysis-filter
        /// history, both starting all-zero). The combined value (1680) was confirmed, not assumed,
        /// by empirically measuring this exact encoder+decoder pair's own real round-trip delay via
        /// normalized cross-correlation, cross-checked against an independent real `ffmpeg` decode
        /// of the same file (both agreed exactly), rather than derived purely from the two pieces'
        /// own theoretical values added together.
        /// </summary>
        public const int EncoderDelaySamples = 1680;

        public static void Encode(string sourceWavFilePath, string destMp3FilePath, int bitRateKbps = DefaultBitRateKbps, IReadOnlyDictionary<string, string>? tags = null)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destMp3FilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, bitRateKbps, tags);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        public static Mp3EncoderSession OpenSession(string destMp3FilePath, int channels, int sampleRate, int bitsPerSample, int bitRateKbps = DefaultBitRateKbps, IReadOnlyDictionary<string, string>? tags = null)
        {
            // Validate (and build) the tag bytes before touching the destination file at all -- an
            // invalid tag id should fail loudly up front, not after a partial file is already on
            // disk.
            var tagBytes = tags is { Count: > 0 } ? Id3v2Writer.BuildTagBytes(tags) : null;

            var destStream = File.Create(destMp3FilePath);
            if (tagBytes is not null)
            {
                destStream.Write(tagBytes);
            }

            return new Mp3EncoderSession(destStream, channels, sampleRate, bitsPerSample, bitRateKbps);
        }
    }

    public sealed class Mp3EncoderSession : IAudioSink
    {
        private const int FrameSamples = 1152;

        private readonly FileStream _destStream;
        private readonly int _channels;
        private readonly int _bitsPerSampleShift;
        private readonly Mp3FrameEncoder _frameEncoder;
        private readonly List<int> _pendingInterleaved = [];
        private readonly long _infoTagFrameOffset;

        private int _frameCount;
        private long _totalByteCount;
        private bool _finished;
        private bool _disposed;

        internal Mp3EncoderSession(FileStream destStream, int channels, int sampleRate, int bitsPerSample, int bitRateKbps)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSampleShift = bitsPerSample - 16;
            _frameEncoder = new Mp3FrameEncoder(channels, sampleRate, bitRateKbps);

            // Reserve the dedicated Info-tag header frame (see Mp3FrameEncoder.BuildInfoTagFrame's
            // own doc comment) up front, as a placeholder -- Finish() seeks back and patches it with
            // the real frame/byte counts and delay/padding values once they're known, the same
            // provisional-header-then-patch pattern FlacEncoderSession.Finish() already established
            // for STREAMINFO.
            _infoTagFrameOffset = destStream.Position;
            var placeholderInfoTagFrame = _frameEncoder.BuildInfoTagFrame(frameCount: 0, byteCount: 0, startPadSamples: 0, endPadSamples: 0);
            _destStream.Write(placeholderInfoTagFrame);
            _frameCount = 1;
            _totalByteCount = placeholderInfoTagFrame.Length;
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var sampleCount = frameCount * _channels;
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = buffer[i];
                _pendingInterleaved.Add(_bitsPerSampleShift > 0 ? sample >> _bitsPerSampleShift : sample);
            }

            while (_pendingInterleaved.Count >= FrameSamples * _channels)
            {
                EncodeFrame();
            }
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            // The encoder's own polyphase/hybrid filterbank needs exactly 1152 samples per channel
            // per frame -- silence-pad the final, shorter-than-a-frame tail rather than
            // special-casing a partial frame. A few dozen milliseconds of trailing silence is
            // the normal cost of any block-based encoder's own fixed frame size, not specific to
            // this implementation.
            var remainingFrames = _pendingInterleaved.Count / _channels;
            var naturalPaddingFrames = remainingFrames > 0 ? FrameSamples - remainingFrames : 0;
            if (naturalPaddingFrames > 0)
            {
                for (var i = 0; i < naturalPaddingFrames * _channels; i++)
                {
                    _pendingInterleaved.Add(0);
                }

                EncodeFrame();
            }

            // One additional, unconditional frame of pure silence -- confirmed necessary, not just
            // a precaution, by direct measurement: this encoder's own one-granule MDCT history
            // (Mp3FrameEncoder's own _subbandHistory) needs one full frame of "future" context to
            // fully flush the true tail of real audio into a decodable position. Without this, the
            // last ~1 frame's worth of genuinely real input samples are never recoverable from the
            // bitstream at all (confirmed by decoding with and without this flush frame and
            // comparing against the original signal sample-by-sample, not just by reasoning about
            // the filter structure). See Mp3Encoder.EncoderDelaySamples' own doc comment.
            for (var i = 0; i < FrameSamples * _channels; i++)
            {
                _pendingInterleaved.Add(0);
            }

            EncodeFrame();

            // Encoder-delay/padding metadata, per the de-facto "LAME tag" convention (see
            // Mp3FrameEncoder.BuildInfoTagFrame's own doc comment). startPad is this encoder's own
            // fixed structural lookahead. endPad follows from requiring realSamples ==
            // totalFrames*FrameSamples - startPad - endPad to hold for every file this encoder
            // writes (confirmed against a real ffmpeg+libmp3lame reference file's own exact field
            // values, which satisfy the identical identity): since totalFrames*FrameSamples exceeds
            // realSamples by exactly naturalPaddingFrames (the partial-frame pad above) plus two
            // more whole frames (the dedicated Info-tag frame and the mandatory flush frame, both
            // FrameSamples long), endPad is that same excess minus whatever startPad's own leading
            // skip already accounts for.
            const int infoTagAndFlushFrameOverhead = 2 * FrameSamples;
            var startPad = Mp3Encoder.EncoderDelaySamples;
            var endPad = naturalPaddingFrames + infoTagAndFlushFrameOverhead - startPad;
            var finalInfoTagFrame = _frameEncoder.BuildInfoTagFrame(_frameCount, (int)_totalByteCount, startPad, endPad);

            _destStream.Seek(_infoTagFrameOffset, SeekOrigin.Begin);
            _destStream.Write(finalInfoTagFrame);
            _destStream.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _destStream.Dispose();
        }

        private void EncodeFrame()
        {
            var frameBytes = _frameEncoder.EncodeFrame(CollectionsMarshal.AsSpan(_pendingInterleaved)[..(FrameSamples * _channels)]);
            _destStream.Write(frameBytes);
            _pendingInterleaved.RemoveRange(0, FrameSamples * _channels);
            _frameCount++;
            _totalByteCount += frameBytes.Length;
        }
    }
}
