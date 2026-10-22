using EggEncoder.Codecs;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    public static class AacDecoder
    {
        private const int CoefficientCount = 1024;

        public static AacStreamInfo Decode(string aacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var fileBytes = File.ReadAllBytes(aacFilePath);
            return Decode(fileBytes, onBlockDecoded);
        }

        /// <summary>
        /// Decodes an AAC (ADTS) source from an already-open <see cref="Stream"/> instead of a
        /// file path -- for a source that isn't a local file (an HTTP response body, blob storage,
        /// an in-memory buffer). No <see cref="Stream.CanSeek"/> requirement: like
        /// <see cref="Flac.FlacDecoder"/>, this reads the whole source into memory up front, so
        /// any forward-readable stream works, seekable or not. Does NOT take ownership of
        /// <paramref name="stream"/> -- the caller opened it, so the caller is responsible for
        /// disposing it; this call never closes it.
        /// </summary>
        public static AacStreamInfo Decode(Stream stream, AudioBlockDecodedCallback onBlockDecoded)
        {
            ArgumentNullException.ThrowIfNull(stream);

            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);

            return Decode(memoryStream.ToArray(), onBlockDecoded);
        }

        private static AacStreamInfo Decode(byte[] fileBytes, AudioBlockDecodedCallback onBlockDecoded)
        {
            var totalSamples = CountTotalSamples(fileBytes);

            var reader = new BitReader(fileBytes);
            var frameDecoder = new AacFrameDecoder();

            var channels = 0;
            var sampleRate = 0;

            while (reader.RemainingBits >= 56)
            {
                var header = ParseAdtsHeader(reader);
                channels = header.Channels;
                sampleRate = header.SampleRate;

                var frameEndBitPosition = header.FrameStartBitPosition + (header.FrameLength * 8);

                var outputFrame = frameDecoder.DecodeFrame(reader, channels);
                if (reader.BitPosition > frameEndBitPosition)
                {
                    throw new InvalidDataException("AAC: the raw_data_block runs past the ADTS frame_length");
                }

                onBlockDecoded(outputFrame, channels, sampleRate, 16, totalSamples);

                reader.SkipToBitPosition(frameEndBitPosition);
            }

            return new AacStreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = 16,
                TotalSamples = totalSamples
            };

            static long CountTotalSamples(byte[] fileBytes)
            {
                var reader = new BitReader(fileBytes);
                var totalFrames = 0L;

                while (reader.RemainingBits >= 56)
                {
                    var header = ParseAdtsHeader(reader);
                    totalFrames++;
                    reader.SkipToBitPosition(header.FrameStartBitPosition + (header.FrameLength * 8));
                }

                return totalFrames * CoefficientCount;
            }
        }

        private static AdtsHeader ParseAdtsHeader(BitReader reader)
        {
            var frameStartBitPosition = reader.BitPosition;

            if (reader.ReadBits(12) != 0xFFF)
            {
                throw new InvalidDataException("Missing ADTS sync word");
            }

            reader.SkipBits(1);
            reader.SkipBits(2);
            var protectionAbsent = reader.ReadBits(1);
            var profile = reader.ReadBits(2);
            var samplingFrequencyIndex = (int)reader.ReadBits(4);

            if (samplingFrequencyIndex >= AacTables.SampleRates.Length)
            {
                throw new InvalidDataException($"Invalid ADTS sampling_frequency_index {samplingFrequencyIndex}");
            }

            reader.SkipBits(1);
            var channelConfig = (int)reader.ReadBits(3);
            reader.SkipBits(1);
            reader.SkipBits(1);
            reader.SkipBits(1);
            reader.SkipBits(1);
            var frameLength = (int)reader.ReadBits(13);
            reader.SkipBits(11);
            var numRawDataBlocks = reader.ReadBits(2);

            if (profile != 1)
            {
                throw new NotSupportedException($"AAC profile {profile + 1} is not supported; only AAC-LC (profile 2, encoded as 1) is supported");
            }

            if (numRawDataBlocks != 0)
            {
                throw new NotSupportedException("ADTS frames with multiple raw_data_blocks are not supported");
            }

            if (channelConfig is not 1 and not 2)
            {
                throw new NotSupportedException($"Channel configuration {channelConfig} is not supported; only mono (1) and stereo (2) are supported");
            }

            if (protectionAbsent == 0)
            {
                reader.SkipBits(16);
            }

            // frame_length counts the header too, so anything shorter can't hold one.
            if (frameStartBitPosition + (frameLength * 8) < reader.BitPosition)
            {
                throw new InvalidDataException($"Invalid ADTS frame_length {frameLength}: shorter than the frame's own header");
            }

            return new AdtsHeader
            {
                Channels = channelConfig,
                SampleRate = AacTables.SampleRates[samplingFrequencyIndex],
                FrameLength = frameLength,
                FrameStartBitPosition = frameStartBitPosition
            };
        }

        private readonly struct AdtsHeader
        {
            public int Channels { get; init; }

            public int SampleRate { get; init; }

            public int FrameLength { get; init; }

            public int FrameStartBitPosition { get; init; }
        }
    }

    public class AacStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
