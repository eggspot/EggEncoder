using System.Security.Cryptography;
using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    // Pure managed FLAC encoder, implemented from RFC 9639 -- FIXED predictors only (see
    // FlacFrameEncoder's own doc comment), no code derived from libFLAC's own source (see
    // docs/managed-codec-rewrite-plan.md). This is an MVP: fully spec-compliant and fully
    // decodable, just not yet compression-ratio-competitive with libFLAC's own LPC search (that's
    // plan item 3, a follow-up).
    public static class FlacEncoder
    {
        private const int FramesPerBlock = 4096;
        public const uint DefaultCompressionLevel = 5;

        public static void Encode(string sourceWavFilePath, string destFlacFilePath, uint compressionLevel = DefaultCompressionLevel, IReadOnlyDictionary<string, string>? tags = null)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destFlacFilePath, wavReader.Channels, wavReader.BitsPerSample, wavReader.SampleRate, compressionLevel, tags);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        // compressionLevel is accepted (and kept in the public signature, matching the native
        // encoder this replaces) but currently unused -- this MVP has only one encoding strategy
        // (FIXED predictors, single Rice partition). It'll start meaning something once item 3
        // (LPC search) gives this encoder more than one quality/speed tradeoff to pick between.
        public static FlacEncoderSession OpenSession(string destFlacFilePath, int channels, int bitsPerSample, int sampleRate, uint compressionLevel = DefaultCompressionLevel, IReadOnlyDictionary<string, string>? tags = null)
        {
            _ = compressionLevel;

            var commentListBytes = ValidateAndGetCommentListBytes(destFlacFilePath, channels, bitsPerSample, tags);

            var destStream = File.Create(destFlacFilePath);
            try
            {
                return FlacEncoderSession.Open(destStream, channels, bitsPerSample, sampleRate, commentListBytes, leaveOpen: false);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a FLAC destination from an already-open <see cref="Stream"/> instead of a file
        /// path -- for a destination that isn't a local file (an HTTP request body, blob storage,
        /// an in-memory buffer). Unlike <see cref="Wav.WavWriter"/>'s/<see cref="Aiff.AiffWriter"/>'s/
        /// <see cref="Au.AuWriter"/>'s own <c>Create(Stream, ...)</c> overloads, this genuinely requires
        /// <paramref name="destStream"/> to be seekable: <see cref="FlacEncoderSession.Finish"/>
        /// seeks back to a fixed byte offset to patch STREAMINFO's provisional totalSamples/MD5/
        /// block-size-range fields with their real values once they're known, exactly the way the
        /// path-based overload's own <see cref="FileStream"/> destination already does -- a
        /// non-seekable stream (e.g. an HTTP response body) cannot support that patch-up at all, so
        /// this throws <see cref="ArgumentException"/> up front rather than failing confusingly
        /// partway through <see cref="FlacEncoderSession.Finish"/>. Does NOT dispose
        /// <paramref name="destStream"/> -- the caller opened it, so the caller is responsible for
        /// disposing it.
        /// </summary>
        public static FlacEncoderSession OpenSession(Stream destStream, int channels, int bitsPerSample, int sampleRate, uint compressionLevel = DefaultCompressionLevel, IReadOnlyDictionary<string, string>? tags = null)
        {
            ArgumentNullException.ThrowIfNull(destStream);

            if (!destStream.CanSeek)
            {
                throw new ArgumentException("FlacEncoderSession requires a seekable stream -- Finish() seeks back to patch STREAMINFO with the real totalSamples/MD5 once they're known.", nameof(destStream));
            }

            _ = compressionLevel;

            var commentListBytes = ValidateAndGetCommentListBytes("<stream>", channels, bitsPerSample, tags);

            return FlacEncoderSession.Open(destStream, channels, bitsPerSample, sampleRate, commentListBytes, leaveOpen: true);
        }

        // Shared by both OpenSession overloads so path-based and Stream-based destinations can
        // never drift apart on what's a valid channel count/bit depth/tag set -- flacFilePathForErrors
        // is "<stream>" for the Stream-based overload, the same placeholder WavWriter/AiffWriter/
        // AuWriter's own Stream-based Create overloads already use for their error messages.
        private static byte[]? ValidateAndGetCommentListBytes(string flacFilePathForErrors, int channels, int bitsPerSample, IReadOnlyDictionary<string, string>? tags)
        {
            if (channels < 1)
            {
                throw new NotSupportedException($"'{flacFilePathForErrors}' requests {channels} channels; FLAC requires at least 1.");
            }

            // STREAMINFO's bits-per-sample field is 5 bits wide, storing (bitsPerSample - 1) --
            // silently wrapping an out-of-range value into that field would write a *different*,
            // wrong-but-valid-looking bit depth rather than failing, so this is checked explicitly.
            if (bitsPerSample is < 1 or > 32)
            {
                throw new NotSupportedException($"'{flacFilePathForErrors}' requests {bitsPerSample} bits per sample; FLAC's STREAMINFO can only represent 1-32.");
            }

            // Validate (and build) the Vorbis comment bytes before creating/touching the destination
            // at all -- an invalid tag field name should fail loudly up front, not after a partial
            // file (or a partially-written caller-owned stream) already exists (same precedent as
            // Mp3Encoder.OpenSession's own tag validation).
            var commentListBytes = tags is { Count: > 0 } ? VorbisCommentWriter.BuildCommentListBytes(tags) : null;

            // The VORBIS_COMMENT metadata block's own length field is 24 bits wide, the same
            // "silently wrapping into a different, wrong-but-valid-looking value instead of
            // failing" risk the bits-per-sample check above guards against -- checked explicitly
            // for the same reason.
            const int MaxMetadataBlockLength = (1 << 24) - 1;
            if (commentListBytes is { Length: > MaxMetadataBlockLength })
            {
                throw new NotSupportedException($"'{flacFilePathForErrors}' requests a VORBIS_COMMENT block of {commentListBytes.Length} bytes; FLAC's metadata block length field can only represent up to {MaxMetadataBlockLength}.");
            }

            return commentListBytes;
        }
    }

    public sealed class FlacEncoderSession : IAudioSink
    {
        // 4096 frames per block matches this codebase's own AacEncoderSession/AlacEncoderSession/
        // TtaEncoderSession convention (and FlacEncoder.Encode's own pre-existing FramesPerBlock).
        private const int BlockSize = 4096;

        // 'fLaC' (4 bytes) + the STREAMINFO metadata block header (1 bit last + 7 bits type + 24
        // bits length = 4 bytes) -- STREAMINFO's own 34-byte payload always starts exactly here,
        // since this encoder never writes any other metadata block.
        private const int StreamInfoPayloadOffset = 8;

        private readonly Stream _destStream;
        private readonly bool _leaveOpen;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _sampleRate;
        private readonly int[] _pendingInterleaved;
        private readonly IncrementalHash _md5;

        private int _pendingFrameCount;
        private long _totalSamples;
        private long _frameIndex;
        private int _lastBlockSize;
        private bool _disposed;

        private FlacEncoderSession(Stream destStream, bool leaveOpen, int channels, int bitsPerSample, int sampleRate)
        {
            _destStream = destStream;
            _leaveOpen = leaveOpen;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _sampleRate = sampleRate;
            _pendingInterleaved = new int[BlockSize * channels];
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }

        // leaveOpen mirrors WavWriter/AiffWriter/AuWriter's own Stream-based Create overloads:
        // false for the path-based OpenSession (this type owns the FileStream it opened, so Dispose
        // closes it), true for the Stream-based one (the caller opened destStream, so the caller is
        // responsible for disposing it).
        internal static FlacEncoderSession Open(Stream destStream, int channels, int bitsPerSample, int sampleRate, byte[]? commentListBytes, bool leaveOpen)
        {
            var writer = new BitWriter();
            writer.WriteBits('f', 8);
            writer.WriteBits('L', 8);
            writer.WriteBits('a', 8);
            writer.WriteBits('C', 8);
            writer.WriteBits(commentListBytes is null ? 1u : 0u, 1); // isLast -- false only when a VORBIS_COMMENT block follows below
            writer.WriteBits(0, 7); // block type 0: STREAMINFO
            writer.WriteBits(34, 24); // STREAMINFO's payload is always exactly 34 bytes

            // A provisional STREAMINFO: totalSamples/MD5 aren't known until Finish(), and
            // min/maxBlockSize optimistically assume at least one full-size block will be written.
            // All of these get overwritten with their real values once Finish() seeks back here --
            // nothing reads this file while it's only partially written, so the provisional values
            // are never actually observed by a decoder.
            WriteStreamInfoPayload(writer, channels, bitsPerSample, sampleRate, BlockSize, BlockSize, totalSamples: 0, md5: new byte[16]);

            if (commentListBytes is not null)
            {
                writer.WriteBits(1, 1); // isLast -- this encoder never writes any metadata block after this one
                writer.WriteBits(4, 7); // block type 4: VORBIS_COMMENT
                writer.WriteBits((uint)commentListBytes.Length, 24);
                foreach (var b in commentListBytes)
                {
                    writer.WriteBits(b, 8);
                }
            }

            destStream.Write(writer.ToArray());

            return new FlacEncoderSession(destStream, leaveOpen, channels, bitsPerSample, sampleRate);
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            var offset = 0;
            while (offset < frameCount)
            {
                var take = Math.Min(frameCount - offset, BlockSize - _pendingFrameCount);
                var sourceSpan = buffer.AsSpan(offset * _channels, take * _channels);
                sourceSpan.CopyTo(_pendingInterleaved.AsSpan(_pendingFrameCount * _channels, take * _channels));

                FlacPcmMd5.Append(_md5, sourceSpan, _bitsPerSample);

                _pendingFrameCount += take;
                _totalSamples += take;
                offset += take;

                if (_pendingFrameCount == BlockSize)
                {
                    FlushPendingBlock();
                }
            }
        }

        public void Finish()
        {
            if (_pendingFrameCount > 0)
            {
                FlushPendingBlock();
            }

            var md5 = _md5.GetHashAndReset();

            // RFC 9639 section 4.1/8.2: every frame but the last MUST use exactly the nominal block
            // size (and that size MUST be >=16); the *last* frame is explicitly exempt from that
            // 16-sample floor, since it has to match whatever audio length is actually left over.
            // This encoder always uses exactly BlockSize for every non-last frame, so:
            //  - 2+ frames total: min=max=BlockSize is exactly right (every non-last frame used it,
            //    and it's never smaller than the possibly-shorter last frame).
            //  - exactly 1 frame: that frame is simultaneously the first and the last, so its own
            //    (possibly <16) actual size is the only correct value for both fields.
            //  - 0 frames (an empty source): no real content to report; BlockSize is an arbitrary
            //    but harmless placeholder, same as the provisional header this overwrites.
            int minBlockSize, maxBlockSize;
            if (_frameIndex == 1)
            {
                minBlockSize = maxBlockSize = _lastBlockSize;
            }
            else
            {
                minBlockSize = maxBlockSize = BlockSize;
            }

            var writer = new BitWriter();
            WriteStreamInfoPayload(writer, _channels, _bitsPerSample, _sampleRate, minBlockSize, maxBlockSize, _totalSamples, md5);

            _destStream.Seek(StreamInfoPayloadOffset, SeekOrigin.Begin);
            _destStream.Write(writer.ToArray());
            _destStream.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!_leaveOpen)
            {
                _destStream.Dispose();
            }
        }

        private void FlushPendingBlock()
        {
            var channelSamples = new int[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                channelSamples[c] = new int[_pendingFrameCount];
            }

            for (var i = 0; i < _pendingFrameCount; i++)
            {
                var baseIndex = i * _channels;
                for (var c = 0; c < _channels; c++)
                {
                    channelSamples[c][i] = _pendingInterleaved[baseIndex + c];
                }
            }

            var frameBytes = FlacFrameEncoder.EncodeFrame(channelSamples, _pendingFrameCount, _bitsPerSample, _frameIndex);
            _destStream.Write(frameBytes);

            _frameIndex++;
            _lastBlockSize = _pendingFrameCount;
            _pendingFrameCount = 0;
        }

        // internal rather than private so test code (and Streaming.Flac.FlacStreamingEncoder's own
        // conformance tests specifically, which must build a real STREAMINFO out-of-band to make
        // its own raw frames decodable at all -- see that type's own doc comment) can reuse this
        // exact bit layout rather than duplicating it.
        internal static void WriteStreamInfoPayload(BitWriter writer, int channels, int bitsPerSample, int sampleRate, int minBlockSize, int maxBlockSize, long totalSamples, byte[] md5)
        {
            writer.WriteBits((uint)minBlockSize, 16);
            writer.WriteBits((uint)maxBlockSize, 16);
            writer.WriteBits(0, 24); // minimum frame size -- RFC 9639 allows 0 for "unknown", never read by FlacDecoder anyway
            writer.WriteBits(0, 24); // maximum frame size -- likewise
            writer.WriteBits((uint)sampleRate, 20);
            writer.WriteBits((uint)(channels - 1), 3);
            writer.WriteBits((uint)(bitsPerSample - 1), 5);
            writer.WriteBits((uint)(totalSamples >> 32), 4);
            writer.WriteBits((uint)totalSamples, 32);
            foreach (var b in md5)
            {
                writer.WriteBits(b, 8);
            }
        }
    }
}
