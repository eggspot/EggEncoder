namespace EggEncoder.UnitTests.TestUtilities
{
    /// <summary>
    /// Hand-builds minimal, valid (or deliberately invalid) FLAC byte streams bit by bit, for cases
    /// no accessible real encoder can produce (8-bit or 32-bit FLAC -- ffmpeg's own FLAC encoder
    /// caps at 24-bit, confirmed empirically while authoring this) or that need an exact malformed
    /// byte to test a specific validation path. Every frame built here uses only CONSTANT subframes
    /// (RFC 9639 section 9.2.2) -- the simplest subframe type, with no residual/prediction at all --
    /// since the point of these fixtures is exercising FlacDecoder's header/metadata/CRC/stereo
    /// handling, not its residual or LPC decoding (already covered end-to-end by the real
    /// ffmpeg-produced fixtures in FlacFfmpegCrossCheckTest).
    /// </summary>
    internal static class FlacFileBuilder
    {
        /// <summary>
        /// Builds a single-frame FLAC file with one CONSTANT subframe per channel, all using
        /// independent (non-stereo-decorrelated) channel assignment. STREAMINFO's MD5 is left as
        /// all zeros (the spec's own "not computed" convention), so FlacDecoder's MD5 check is a
        /// no-op for every fixture built here -- hand-computing a real MD5 would add nothing to what
        /// these fixtures are actually testing.
        /// </summary>
        public static byte[] BuildConstantFrame(int channels, int bitsPerSample, int sampleRate, int blockSize, int[] constantValuePerChannel, long? declaredTotalSamples = null, byte[]? md5Signature = null)
        {
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels, bitsPerSample, sampleRate, maxBlockSize: blockSize, totalSamples: declaredTotalSamples ?? blockSize, minBlockSize: blockSize, md5Signature: md5Signature);

            var frameStart = writer.ByteLength;
            WriteFrameHeaderBits(writer, channels, bitsPerSample, blockSize, channelAssignment: channels - 1);
            WriteCrc8Placeholder(writer, frameStart);

            for (var channel = 0; channel < channels; channel++)
            {
                WriteConstantSubframe(writer, bitsPerSample, constantValuePerChannel[channel]);
            }

            writer.AlignToByte();
            WriteCrc16Placeholder(writer, frameStart);

            return writer.ToArray();
        }

        /// <summary>
        /// Builds just a STREAMINFO block (plus the 'fLaC' marker) -- for metadata-level validation
        /// that's checked before any frame is ever parsed. <paramref name="isLast"/> is <c>false</c>
        /// when the caller is going to append more metadata blocks of its own afterward (STREAMINFO
        /// marked "last" would make the decoder stop reading metadata right after it and try to
        /// parse whatever follows as a frame instead).
        /// </summary>
        public static byte[] BuildStreamInfoOnly(int channels, int bitsPerSample, int sampleRate, int minBlockSize, int maxBlockSize, bool isLast = true)
        {
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels, bitsPerSample, sampleRate, maxBlockSize, totalSamples: maxBlockSize, minBlockSize, isLast: isLast);
            return writer.ToArray();
        }

        /// <summary>
        /// Builds a single-frame, 2-channel FLAC file using the left-side (or mid-side) channel
        /// assignment at 32 bits per sample specifically, so the side channel's subframe is the
        /// 33-bit-wide case (<c>DecodeSubframeBody64</c>) -- unreachable by any real encoder fixture,
        /// since ffmpeg's own FLAC encoder caps at 24-bit.
        /// </summary>
        public static byte[] BuildWideSideChannelFrame(int sampleRate, int blockSize, bool midSide, int leftOrMidValue, int sideValue)
        {
            const int bitsPerSample = 32;
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels: 2, bitsPerSample, sampleRate, blockSize, totalSamples: blockSize);

            var frameStart = writer.ByteLength;
            // Channel assignment 8 = left/side, 10 = mid/side (RFC 9639 section 9.1.3).
            WriteFrameHeaderBits(writer, channels: 2, bitsPerSample, blockSize, channelAssignment: midSide ? 10 : 8);
            WriteCrc8Placeholder(writer, frameStart);

            WriteConstantSubframe(writer, bitsPerSample, leftOrMidValue); // channel 0: left (or mid)
            WriteConstantSubframe(writer, bitsPerSample + 1, sideValue); // channel 1: the 33-bit side

            writer.AlignToByte();
            WriteCrc16Placeholder(writer, frameStart);

            return writer.ToArray();
        }

        /// <summary>
        /// Builds a frame with a caller-supplied header's third/fourth raw bytes, stopping right
        /// after the fourth byte with no frame number, subframes or CRCs -- enough to reach (and
        /// exercise) every reserved-value check in <c>FlacFrameDecoder.ReadFrameHeader</c>, every one
        /// of which throws before any of that later content would ever be read.
        /// </summary>
        public static byte[] BuildFrameHeaderOnly(int channels, int bitsPerSample, int sampleRate, int blockSize, byte secondByte, byte thirdByte, byte fourthByte)
        {
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels, bitsPerSample, sampleRate, blockSize, totalSamples: blockSize);
            writer.WriteBits(0xFF, 8);
            writer.WriteBits(secondByte, 8);
            writer.WriteBits(thirdByte, 8);
            writer.WriteBits(fourthByte, 8);
            return writer.ToArray();
        }

        /// <summary>
        /// Builds a complete, otherwise-valid single-frame file (correct CRC-8 and CRC-16) whose
        /// subframe content is entirely supplied by <paramref name="writeSubframes"/> -- for
        /// validation paths inside subframe/residual decoding that need a header and CRCs to
        /// actually be reached.
        /// </summary>
        public static byte[] BuildFrameWithCustomSubframes(int channels, int bitsPerSample, int sampleRate, int blockSize, int channelAssignment, Action<BitWriter> writeSubframes, int? rawBitsPerSampleCode = null)
        {
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels, bitsPerSample, sampleRate, blockSize, totalSamples: blockSize);

            var frameStart = writer.ByteLength;
            WriteFrameHeaderBits(writer, channels, bitsPerSample, blockSize, channelAssignment, (uint)(rawBitsPerSampleCode ?? 0));
            WriteCrc8Placeholder(writer, frameStart);

            writeSubframes(writer);

            writer.AlignToByte();
            WriteCrc16Placeholder(writer, frameStart);
            return writer.ToArray();
        }

        /// <summary>
        /// Builds a frame with a valid first four header bytes (fixed block size, mono, 16-bit
        /// STREAMINFO) followed directly by the caller's own raw frame-number bytes -- enough to
        /// reach (and exercise) <c>FlacFrameDecoder</c>'s frame/sample-number coding validation,
        /// which throws before anything past it (the explicit block-size bytes, CRC-8, subframes)
        /// would ever be read.
        /// </summary>
        public static byte[] BuildFrameWithRawFrameNumberBytes(byte[] frameNumberBytes)
        {
            var writer = new BitWriter();
            WriteStreamInfo(writer, channels: 1, bitsPerSample: 16, sampleRate: 44100, maxBlockSize: 192, totalSamples: 192);
            writer.WriteBits(0xFF, 8);
            writer.WriteBits(0xF8, 8); // fixed block size
            writer.WriteBits(0x70, 8); // blockSizeCode=7 (16-bit explicit follows, never reached), sampleRateCode=0
            writer.WriteBits(0x00, 8); // mono, bits-per-sample from STREAMINFO
            foreach (var b in frameNumberBytes)
            {
                writer.WriteBits(b, 8);
            }

            return writer.ToArray();
        }

        /// <summary>Writes a single CONSTANT subframe (RFC 9639 section 9.2.2) -- exposed for <see cref="BuildFrameWithCustomSubframes"/> callers that need some channels ordinary and only one channel exercising something unusual.</summary>
        public static void WriteConstantSubframePublic(BitWriter writer, int bitsPerSample, int value) => WriteConstantSubframe(writer, bitsPerSample, value);

        public static byte[] BuildWithMetadataHeader(int blockType, int blockLength, bool isLast = true)
        {
            var writer = new BitWriter();
            WriteMagicAndMetadataHeader(writer, blockType, blockLength, isLast);
            return writer.ToArray();
        }

        /// <summary>
        /// Builds just a metadata block header (no leading 'fLaC' magic) -- for appending a second
        /// metadata block after bytes from <see cref="BuildStreamInfoOnly"/>, which already wrote
        /// the magic once; unlike <see cref="BuildWithMetadataHeader"/>, this is not a standalone file.
        /// </summary>
        public static byte[] BuildMetadataBlockHeaderOnly(int blockType, int blockLength, bool isLast = true)
        {
            var writer = new BitWriter();
            WriteMagicAndMetadataHeaderOnly(writer, blockType, blockLength, isLast);
            return writer.ToArray();
        }

        public static byte[] PrependId3v2Tag(byte[] flacBytes, int taggedPayloadLength)
        {
            var writer = new BitWriter();
            writer.WriteBits(0x494433, 24); // "ID3"
            writer.WriteBits(0x0400, 16); // version 2.4.0 (arbitrary, not validated by the decoder)
            writer.WriteBits(0, 8); // flags: no footer
            writer.WriteBits((uint)((taggedPayloadLength >> 21) & 0x7F), 8);
            writer.WriteBits((uint)((taggedPayloadLength >> 14) & 0x7F), 8);
            writer.WriteBits((uint)((taggedPayloadLength >> 7) & 0x7F), 8);
            writer.WriteBits((uint)(taggedPayloadLength & 0x7F), 8);
            for (var i = 0; i < taggedPayloadLength; i++)
            {
                writer.WriteBits(0, 8); // tag payload content is never read
            }

            var tag = writer.ToArray();
            var combined = new byte[tag.Length + flacBytes.Length];
            tag.CopyTo(combined, 0);
            flacBytes.CopyTo(combined, tag.Length);
            return combined;
        }

        private static void WriteStreamInfo(BitWriter writer, int channels, int bitsPerSample, int sampleRate, int maxBlockSize, long totalSamples, int? minBlockSize = null, byte[]? md5Signature = null, bool isLast = true)
        {
            writer.WriteBits('f', 8);
            writer.WriteBits('L', 8);
            writer.WriteBits('a', 8);
            writer.WriteBits('C', 8);

            WriteMagicAndMetadataHeaderOnly(writer, blockType: 0, blockLength: 34, isLast);

            writer.WriteBits((uint)(minBlockSize ?? maxBlockSize), 16);
            writer.WriteBits((uint)maxBlockSize, 16);
            writer.WriteBits(0, 24); // minimum frame size -- not read
            writer.WriteBits(0, 24); // maximum frame size -- not read
            writer.WriteBits((uint)sampleRate, 20);
            writer.WriteBits((uint)(channels - 1), 3);
            writer.WriteBits((uint)(bitsPerSample - 1), 5);
            writer.WriteBits((uint)(totalSamples >> 32), 4);
            writer.WriteBits((uint)totalSamples, 32);
            for (var i = 0; i < 16; i++)
            {
                writer.WriteBits(md5Signature?[i] ?? 0, 8); // all-zero MD5 is the spec's own "not computed" convention
            }
        }

        private static void WriteMagicAndMetadataHeader(BitWriter writer, int blockType, int blockLength, bool isLast)
        {
            writer.WriteBits('f', 8);
            writer.WriteBits('L', 8);
            writer.WriteBits('a', 8);
            writer.WriteBits('C', 8);
            WriteMagicAndMetadataHeaderOnly(writer, blockType, blockLength, isLast);
        }

        private static void WriteMagicAndMetadataHeaderOnly(BitWriter writer, int blockType, int blockLength, bool isLast)
        {
            writer.WriteBits(isLast ? 1u : 0u, 1);
            writer.WriteBits((uint)blockType, 7);
            writer.WriteBits((uint)blockLength, 24);
        }

        private static void WriteFrameHeaderBits(BitWriter writer, int channels, int bitsPerSample, int blockSize, int channelAssignment, uint rawBitsPerSampleCode = 0)
        {
            writer.WriteBits(0xFF, 8); // sync code, first 8 bits
            writer.WriteBits(0xF8, 8); // sync code remainder (6 bits) + reserved(0) + fixed-block-size(0)

            var blockSizeCode = blockSize switch
            {
                192 => 1u,
                576 => 2u,
                1152 => 3u,
                2304 => 4u,
                4608 => 5u,
                _ => 7u, // "uncommon, 16-bit follows" -- always valid regardless of the actual value
            };

            var sampleRateCode = 0u; // "from STREAMINFO" -- always valid, never needs explicit bits

            writer.WriteBits((blockSizeCode << 4) | sampleRateCode, 8);
            writer.WriteBits(((uint)channelAssignment << 4) | (rawBitsPerSampleCode << 1), 8);
            writer.WriteBits(0, 8); // frame number 0 (fixed-block-size stream): a single byte, top bit clear

            if (blockSizeCode == 7)
            {
                writer.WriteBits((uint)(blockSize - 1), 16);
            }

            _ = channels; // channels is implied by channelAssignment and verified by the decoder against STREAMINFO
        }

        private static void WriteCrc8Placeholder(BitWriter writer, int frameStartByte)
        {
            writer.AlignToByte();
            var headerBytes = writer.ToArray().AsSpan(frameStartByte);
            writer.WriteBits(Crc8(headerBytes), 8);
        }

        private static void WriteCrc16Placeholder(BitWriter writer, int frameStartByte)
        {
            var frameBytes = writer.ToArray().AsSpan(frameStartByte);
            writer.WriteBits(Crc16(frameBytes), 16);
        }

        private static void WriteConstantSubframe(BitWriter writer, int bitsPerSample, int value)
        {
            writer.WriteBits(0, 1); // reserved
            writer.WriteBits(0, 6); // subframe type 0 = CONSTANT
            writer.WriteBits(0, 1); // no wasted bits
            WriteSignedBits(writer, value, bitsPerSample);
        }

        private static void WriteSignedBits(BitWriter writer, long value, int bitCount)
        {
            if (bitCount <= 32)
            {
                writer.WriteBits((uint)value & Mask(bitCount), bitCount);
                return;
            }

            writer.WriteBits((uint)(value >> 32) & Mask(bitCount - 32), bitCount - 32);
            writer.WriteBits((uint)value, 32);
        }

        private static uint Mask(int bitCount) => bitCount == 32 ? 0xFFFFFFFFu : (1u << bitCount) - 1;

        private static byte Crc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0;
            foreach (var b in data)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
                }
            }

            return crc;
        }

        private static ushort Crc16(ReadOnlySpan<byte> data)
        {
            ushort crc = 0;
            foreach (var b in data)
            {
                crc ^= (ushort)(b << 8);
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
                }
            }

            return crc;
        }

        public sealed class BitWriter
        {
            private readonly List<byte> _bytes = [];
            private uint _currentByte;
            private int _bitsInCurrentByte;

            public int ByteLength => _bytes.Count;

            public void WriteBits(uint value, int count)
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    var bit = (value >> i) & 1;
                    _currentByte = (_currentByte << 1) | bit;
                    _bitsInCurrentByte++;
                    if (_bitsInCurrentByte == 8)
                    {
                        _bytes.Add((byte)_currentByte);
                        _currentByte = 0;
                        _bitsInCurrentByte = 0;
                    }
                }
            }

            public void WriteUnary(uint zeros)
            {
                WriteBits(0, (int)zeros);
                WriteBits(1, 1);
            }

            public void AlignToByte()
            {
                if (_bitsInCurrentByte > 0)
                {
                    WriteBits(0, 8 - _bitsInCurrentByte);
                }
            }

            public byte[] ToArray()
            {
                if (_bitsInCurrentByte != 0)
                {
                    throw new InvalidOperationException("The FLAC test fixture builder was asked for its bytes while not byte-aligned -- call AlignToByte() first.");
                }

                return [.. _bytes];
            }
        }
    }
}
