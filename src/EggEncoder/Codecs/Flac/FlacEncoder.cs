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

        public static void Encode(string sourceWavFilePath, string destFlacFilePath, uint compressionLevel = DefaultCompressionLevel)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destFlacFilePath, wavReader.Channels, wavReader.BitsPerSample, wavReader.SampleRate, compressionLevel);

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
        public static FlacEncoderSession OpenSession(string destFlacFilePath, int channels, int bitsPerSample, int sampleRate, uint compressionLevel = DefaultCompressionLevel)
        {
            _ = compressionLevel;

            if (channels < 1)
            {
                throw new NotSupportedException($"'{destFlacFilePath}' requests {channels} channels; FLAC requires at least 1.");
            }

            // STREAMINFO's bits-per-sample field is 5 bits wide, storing (bitsPerSample - 1) --
            // silently wrapping an out-of-range value into that field would write a *different*,
            // wrong-but-valid-looking bit depth rather than failing, so this is checked explicitly.
            if (bitsPerSample is < 1 or > 32)
            {
                throw new NotSupportedException($"'{destFlacFilePath}' requests {bitsPerSample} bits per sample; FLAC's STREAMINFO can only represent 1-32.");
            }

            var destStream = File.Create(destFlacFilePath);
            try
            {
                return FlacEncoderSession.Open(destStream, channels, bitsPerSample, sampleRate);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
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

        private readonly FileStream _destStream;
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

        private FlacEncoderSession(FileStream destStream, int channels, int bitsPerSample, int sampleRate)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _sampleRate = sampleRate;
            _pendingInterleaved = new int[BlockSize * channels];
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }

        internal static FlacEncoderSession Open(FileStream destStream, int channels, int bitsPerSample, int sampleRate)
        {
            var writer = new BitWriter();
            writer.WriteBits('f', 8);
            writer.WriteBits('L', 8);
            writer.WriteBits('a', 8);
            writer.WriteBits('C', 8);
            writer.WriteBits(1, 1); // isLast -- this encoder never writes a second metadata block
            writer.WriteBits(0, 7); // block type 0: STREAMINFO
            writer.WriteBits(34, 24); // STREAMINFO's payload is always exactly 34 bytes

            // A provisional STREAMINFO: totalSamples/MD5 aren't known until Finish(), and
            // min/maxBlockSize optimistically assume at least one full-size block will be written.
            // All of these get overwritten with their real values once Finish() seeks back here --
            // nothing reads this file while it's only partially written, so the provisional values
            // are never actually observed by a decoder.
            WriteStreamInfoPayload(writer, channels, bitsPerSample, sampleRate, BlockSize, BlockSize, totalSamples: 0, md5: new byte[16]);
            destStream.Write(writer.ToArray());

            return new FlacEncoderSession(destStream, channels, bitsPerSample, sampleRate);
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
            _destStream.Dispose();
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

        private static void WriteStreamInfoPayload(BitWriter writer, int channels, int bitsPerSample, int sampleRate, int minBlockSize, int maxBlockSize, long totalSamples, byte[] md5)
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
