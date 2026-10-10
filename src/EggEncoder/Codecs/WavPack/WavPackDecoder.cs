namespace EggEncoder.Codecs.WavPack
{
    // Pure managed WavPack (.wv) decoder, implemented from the official WavPack 4/5 binary file
    // format specification (https://www.wavpack.com/WavPack5FileFormat.pdf) for the block/header/
    // metadata container, plus the actual decorrelation and entropy-coding algorithms -- which
    // that same official document does not describe at all -- reconstructed from FFmpeg's own
    // independently-written WavPack decoder (libavcodec/wavpack.c/.h, LGPL 2.1+), studied at arm's
    // length (never copied: every formula here is re-derived/re-expressed independently, and
    // FFmpeg's own precomputed exp2 mantissa table is instead computed directly from its
    // mathematical definition in WavPackExp2) per the owner's explicit sign-off on this specific
    // approach; see docs/managed-codec-rewrite-plan.md item 4 and THIRD-PARTY-NOTICES.md.
    //
    // Scoped to mono/stereo, 16/24-bit lossless integer PCM only, matching this project's existing
    // convention and the previous native-backed implementation's own contract exactly -- lossy/
    // hybrid, floating-point, and more-than-stereo (multi-block-per-frame) WavPack are all rejected
    // rather than silently mishandled.
    public static class WavPackDecoder
    {
        public static WavPackStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(filePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Failed to open '{filePath}' as WavPack: {e.Message}", e);
            }

            if (data.Length < WavPackBlockHeader.ByteLength)
            {
                throw new InvalidDataException($"Failed to open '{filePath}' as WavPack: the file is too short to contain even one block header.");
            }

            var offset = 0;
            WavPackBlockHeader? firstHeader = null;
            int channels = 0, bitsPerSample = 0, sampleRate = 0;
            long totalSamples = 0;

            while (offset + WavPackBlockHeader.ByteLength <= data.Length)
            {
                WavPackBlockHeader header;
                try
                {
                    header = WavPackBlockHeader.Parse(data, offset);
                }
                catch (InvalidDataException) when (firstHeader is not null)
                {
                    // Trailing, non-block data (e.g. an APEv2 tag) after the last real block is
                    // normal and must be tolerated, not treated as corruption.
                    break;
                }

                var blockEnd = offset + 8 + (int)header.CkSize;
                if (blockEnd > data.Length)
                {
                    throw new InvalidDataException($"'{filePath}' ended in the middle of a WavPack block -- the file is likely truncated.");
                }

                if (firstHeader is null)
                {
                    firstHeader = header;

                    if (!header.IsStandaloneMonoOrStereoBlock)
                    {
                        throw new NotSupportedException($"'{filePath}' splits more than 2 channels across multiple per-frame blocks; only mono and stereo WavPack are supported");
                    }

                    channels = header.IsMono ? 1 : 2;

                    if (header.IsFloat)
                    {
                        throw new NotSupportedException($"'{filePath}' is floating-point WavPack; only lossless integer WavPack is supported");
                    }

                    if (header.IsHybrid)
                    {
                        throw new NotSupportedException($"'{filePath}' is hybrid (lossy) WavPack; only lossless WavPack is supported");
                    }

                    bitsPerSample = header.BitsPerSample;
                    if (bitsPerSample != 16 && bitsPerSample != 24)
                    {
                        throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 16-bit and 24-bit WavPack are supported");
                    }

                    sampleRate = header.StandardSampleRate ?? FindNonStandardSampleRate(data, offset + WavPackBlockHeader.ByteLength, blockEnd)
                        ?? throw new NotSupportedException($"'{filePath}' has a non-standard sample rate but is missing the metadata that would specify it");
                    totalSamples = header.TotalSamples;
                }

                var channelSamples = WavPackBlockDecoder.Decode(data, offset + WavPackBlockHeader.ByteLength, blockEnd, header, out var actualCrc, out var extraShift);
                if (actualCrc != header.Crc)
                {
                    throw new InvalidDataException($"'{filePath}' has a WavPack block CRC mismatch (computed 0x{actualCrc:x8}, recorded 0x{header.Crc:x8}) -- the file is corrupt or truncated.");
                }

                var blockSamples = (int)header.BlockSamples;
                var interleaved = new int[blockSamples * channels];
                var shift = header.LeftShift + extraShift;
                for (var i = 0; i < blockSamples; i++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        var sample = channelSamples[c][i];
                        if (shift > 0)
                        {
                            sample <<= shift;
                        }

                        interleaved[(i * channels) + c] = sample;
                    }
                }

                onBlockDecoded(interleaved, channels, sampleRate, bitsPerSample, totalSamples);

                offset = blockEnd;
            }

            if (firstHeader is null)
            {
                throw new InvalidDataException($"Failed to open '{filePath}' as WavPack: no valid block header found.");
            }

            return new WavPackStreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                TotalSamples = totalSamples,
            };
        }

        // The block header's own 4-bit sample-rate field only covers the 15 standard rates; a
        // non-standard rate (e.g. 1000 Hz) is signaled by that field being the reserved "custom"
        // value (WavPackBlockHeader.StandardSampleRate returning null) with the real rate carried
        // instead in a WP_ID_SAMPLE_RATE metadata sub-block as a plain 24-bit little-endian value
        // in Hz, no scaling.
        private static int? FindNonStandardSampleRate(byte[] data, int metadataStart, int metadataEnd)
        {
            foreach (var subBlock in WavPackMetadataSubBlock.ReadAll(data, metadataStart, metadataEnd))
            {
                if (subBlock.FunctionId == WavPackMetadataSubBlock.IdSampleRate)
                {
                    var d = subBlock.Data;
                    return d[0] | (d[1] << 8) | (d[2] << 16);
                }
            }

            return null;
        }
    }

    public class WavPackStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
