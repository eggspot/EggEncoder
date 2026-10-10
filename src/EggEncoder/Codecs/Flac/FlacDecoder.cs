using System.Security.Cryptography;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    // Pure managed FLAC decoder, implemented from RFC 9639 -- no code derived from libFLAC's own
    // source (see docs/managed-codec-rewrite-plan.md). Reads the whole file into memory and parses
    // it via the shared Transform.BitReader, the same convention AacDecoder already uses for a
    // fully-loaded bitstream-based codec.
    public static class FlacDecoder
    {
        private const int MetadataTypeStreamInfo = 0;
        private const int MetadataTypeInvalid = 127;
        private const int StreamInfoPayloadLength = 34;

        public static FlacStreamInfo Decode(string flacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            try
            {
                return DecodeCore(flacFilePath, onBlockDecoded);
            }
            catch (EndOfStreamException e)
            {
                // BitReader signals running out of bytes with its own stream-level exception type;
                // every other container reader in this codebase (TtaReader, Mp4EsdsParser,
                // AsfContainerReader) surfaces truncation as InvalidDataException instead, so this
                // wraps it to match rather than leaking a lower-level type callers wouldn't expect.
                throw new InvalidDataException($"'{flacFilePath}' ended in the middle of its metadata or a frame -- the file is likely truncated.", e);
            }
        }

        private static FlacStreamInfo DecodeCore(string flacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var fileBytes = File.ReadAllBytes(flacFilePath);
            var reader = new BitReader(fileBytes);

            var metadata = ReadMetadata(flacFilePath, reader);
            var frameDecoder = new FlacFrameDecoder(metadata.Channels, metadata.BitsPerSample, metadata.MaxBlockSize);

            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var samplesDecoded = 0L;
            var interleavedBuffer = Array.Empty<int>();

            while (reader.RemainingBits >= 8)
            {
                int blockSize;
                try
                {
                    blockSize = frameDecoder.DecodeFrame(fileBytes, reader);
                }
                catch (InvalidDataException) when (samplesDecoded == metadata.TotalSamples && metadata.TotalSamples > 0)
                {
                    // Every sample STREAMINFO promised has already arrived, so whatever is left
                    // (an ID3v1 tag some tagging tool appended, stray padding, etc.) is trailing
                    // data after the real stream, not a corrupt frame -- stop cleanly. The reader's
                    // own position no longer matters once this method returns, so there's nothing
                    // to rewind (and BitReader.SkipToBitPosition only supports moving forward anyway).
                    break;
                }

                var requiredLength = blockSize * metadata.Channels;
                if (interleavedBuffer.Length < requiredLength)
                {
                    interleavedBuffer = new int[requiredLength];
                }

                Interleave(frameDecoder.Samples, blockSize, metadata.Channels, interleavedBuffer);
                FlacPcmMd5.Append(md5, interleavedBuffer.AsSpan(0, requiredLength), metadata.BitsPerSample);

                onBlockDecoded(new ReadOnlySpan<int>(interleavedBuffer, 0, requiredLength), metadata.Channels, metadata.SampleRate, metadata.BitsPerSample, metadata.TotalSamples);
                samplesDecoded += blockSize;
            }

            if (metadata.TotalSamples > 0 && samplesDecoded != metadata.TotalSamples)
            {
                throw new InvalidDataException($"'{flacFilePath}' decoded {samplesDecoded} samples but STREAMINFO declares {metadata.TotalSamples} -- the file is likely truncated.");
            }

            var actualMd5 = md5.GetHashAndReset();
            if (metadata.Md5Signature.AsSpan().IndexOfAnyExcept((byte)0) >= 0 && !actualMd5.AsSpan().SequenceEqual(metadata.Md5Signature))
            {
                throw new InvalidDataException($"'{flacFilePath}' decoded to audio whose MD5 ({Convert.ToHexString(actualMd5)}) does not match STREAMINFO's own MD5 ({Convert.ToHexString(metadata.Md5Signature)}) -- the file is corrupt.");
            }

            return new FlacStreamInfo
            {
                Channels = metadata.Channels,
                SampleRate = metadata.SampleRate,
                BitsPerSample = metadata.BitsPerSample,
                TotalSamples = metadata.TotalSamples
            };
        }

        private static void Interleave(int[][] channelSamples, int blockSize, int channels, int[] destination)
        {
            for (var frame = 0; frame < blockSize; frame++)
            {
                var baseIndex = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    destination[baseIndex + channel] = channelSamples[channel][frame];
                }
            }
        }

        private static FlacMetadata ReadMetadata(string flacFilePath, BitReader reader)
        {
            SkipOptionalId3v2Tag(flacFilePath, reader);

            if (reader.ReadBits(8) != 'f' || reader.ReadBits(8) != 'L' || reader.ReadBits(8) != 'a' || reader.ReadBits(8) != 'C')
            {
                throw new InvalidDataException($"'{flacFilePath}' is not a valid FLAC stream: missing the 'fLaC' marker.");
            }

            FlacMetadata? streamInfo = null;
            var isLastMetadataBlock = false;
            while (!isLastMetadataBlock)
            {
                isLastMetadataBlock = reader.ReadBits(1) != 0;
                var blockType = (int)reader.ReadBits(7);
                var blockLength = (int)reader.ReadBits(24);

                if (streamInfo is null)
                {
                    if (blockType != MetadataTypeStreamInfo || blockLength != StreamInfoPayloadLength)
                    {
                        throw new InvalidDataException($"'{flacFilePath}' does not start its metadata with a valid STREAMINFO block (type {blockType}, length {blockLength}).");
                    }

                    streamInfo = ReadStreamInfoBlock(flacFilePath, reader);
                    continue;
                }

                if (blockType is MetadataTypeStreamInfo or MetadataTypeInvalid)
                {
                    throw new InvalidDataException($"'{flacFilePath}' has a disallowed metadata block type {blockType} (a second STREAMINFO, or the invalid value 127).");
                }

                reader.SkipBits(blockLength * 8);
            }

            return streamInfo!;
        }

        private static void SkipOptionalId3v2Tag(string flacFilePath, BitReader reader)
        {
            // Some tagging tools prepend an ID3v2 tag before the FLAC stream marker, even though
            // it's not part of the format -- tolerated the same way a real-world-robust decoder
            // (e.g. dr_flac) already is, rather than rejecting an otherwise-valid file outright.
            if (reader.RemainingBits < 24 || reader.PeekBits(24) != 0x494433) // "ID3"
            {
                return;
            }

            reader.SkipBits(24);
            reader.SkipBits(16); // version (2 bytes)
            var flags = reader.ReadBits(8);
            var sizeBytes = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                sizeBytes[i] = (byte)reader.ReadBits(8);
                if ((sizeBytes[i] & 0x80) != 0)
                {
                    throw new InvalidDataException($"'{flacFilePath}' has an ID3v2 tag whose size field is not a synchsafe integer.");
                }
            }

            var size = (sizeBytes[0] << 21) | (sizeBytes[1] << 14) | (sizeBytes[2] << 7) | sizeBytes[3];
            var footerBytes = (flags & 0x10) != 0 ? 10 : 0;
            reader.SkipBits((size + footerBytes) * 8);
        }

        private static FlacMetadata ReadStreamInfoBlock(string flacFilePath, BitReader reader)
        {
            var minBlockSize = (int)reader.ReadBits(16);
            var maxBlockSize = (int)reader.ReadBits(16);
            reader.SkipBits(24); // minimum frame size -- not needed for decoding
            reader.SkipBits(24); // maximum frame size -- not needed for decoding
            var sampleRate = (int)reader.ReadBits(20);
            var channels = (int)reader.ReadBits(3) + 1;
            var bitsPerSample = (int)reader.ReadBits(5) + 1;
            var totalSamplesHigh = reader.ReadBits(4);
            var totalSamplesLow = reader.ReadBits(32);
            var totalSamples = ((long)totalSamplesHigh << 32) | totalSamplesLow;
            var md5Signature = new byte[16];
            for (var i = 0; i < md5Signature.Length; i++)
            {
                md5Signature[i] = (byte)reader.ReadBits(8);
            }

            if (sampleRate == 0)
            {
                throw new InvalidDataException($"'{flacFilePath}' has a STREAMINFO sample rate of 0.");
            }

            // RFC 9639 section 4.1 explicitly exempts a stream's last block from the usual 16-sample
            // minimum ("to be able to match the length of the encoded audio without using padding"),
            // and when a stream's only frame is also its last frame, STREAMINFO's own maxBlockSize
            // legitimately reports that frame's true (possibly <16) size -- which this decoder has
            // no way to rule out from STREAMINFO alone, before any frame has actually been read. So
            // only a block size of 0 (meaningless; even a 1-sample last block is valid) or an
            // inverted range is rejected here.
            if (maxBlockSize <= 0 || minBlockSize > maxBlockSize)
            {
                throw new InvalidDataException($"'{flacFilePath}' has an invalid STREAMINFO block size range ({minBlockSize}-{maxBlockSize}).");
            }

            return new FlacMetadata
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                TotalSamples = totalSamples,
                MaxBlockSize = maxBlockSize,
                Md5Signature = md5Signature
            };
        }

        private sealed class FlacMetadata
        {
            public required int Channels { get; init; }

            public required int SampleRate { get; init; }

            public required int BitsPerSample { get; init; }

            public required long TotalSamples { get; init; }

            public required int MaxBlockSize { get; init; }

            public required byte[] Md5Signature { get; init; }
        }
    }

    public class FlacStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
