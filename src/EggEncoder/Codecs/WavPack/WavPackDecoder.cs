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
    // convention and the previous native-backed implementation's own contract exactly -- lossy,
    // hybrid, floating-point, and genuinely-more-than-stereo WavPack are all rejected rather than
    // silently mishandled. A real encoder given no explicit channel layout (e.g. this project's own
    // native WavPackEncoderSession, which never sets WavpackConfig's ChannelMask) emits each
    // "unassigned" channel as its own single-channel block within a multi-block sequence rather
    // than one combined block -- confirmed directly against the official reference encoder -- so a
    // mono or stereo *stream* can still legitimately span 1 (the common case) or 2 (this one)
    // per-frame blocks; only a genuinely-more-than-2-channel sequence is rejected.
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

                if (!header.IsInitialBlockOfSequence)
                {
                    throw new InvalidDataException($"'{filePath}' has a WavPack block that isn't the start of its own per-frame sequence where one was expected -- the file is corrupt or truncated.");
                }

                var groupChannelSamples = new List<int[]>();
                var groupBitsPerSample = 0;
                var groupSampleRate = 0;
                var groupTotalSamples = 0L;
                var blockSamples = (int)header.BlockSamples;
                var isFirstGroupInFile = firstHeader is null;

                while (true)
                {
                    if ((int)header.BlockSamples != blockSamples)
                    {
                        throw new InvalidDataException($"'{filePath}' has a WavPack block whose sample count doesn't match the other blocks in its own per-frame sequence -- the file is corrupt.");
                    }

                    var blockEnd = offset + 8 + (int)header.CkSize;
                    if (blockEnd > data.Length)
                    {
                        throw new InvalidDataException($"'{filePath}' ended in the middle of a WavPack block -- the file is likely truncated.");
                    }

                    if (firstHeader is null)
                    {
                        firstHeader = header;

                        if (header.IsFloat)
                        {
                            throw new NotSupportedException($"'{filePath}' is floating-point WavPack; only lossless integer WavPack is supported");
                        }

                        if (header.IsHybrid)
                        {
                            throw new NotSupportedException($"'{filePath}' is hybrid (lossy) WavPack; only lossless WavPack is supported");
                        }

                        groupBitsPerSample = header.BitsPerSample;
                        if (groupBitsPerSample != 16 && groupBitsPerSample != 24)
                        {
                            throw new NotSupportedException($"'{filePath}' has {groupBitsPerSample}-bit samples; only 16-bit and 24-bit WavPack are supported");
                        }

                        groupSampleRate = header.StandardSampleRate ?? FindNonStandardSampleRate(data, offset + WavPackBlockHeader.ByteLength, blockEnd)
                            ?? throw new NotSupportedException($"'{filePath}' has a non-standard sample rate but is missing the metadata that would specify it");
                        groupTotalSamples = header.TotalSamples;
                    }
                    // Every block after the true first one (whether a sibling in this same
                    // per-frame sequence or the start of a later one) must match the format the
                    // very first block established -- per spec, "the first block... determines the
                    // format of the entire file" -- rather than being silently decoded as if it
                    // still had the first block's own (by now stale) format.
                    else if (header.IsFloat != firstHeader.IsFloat || header.IsHybrid != firstHeader.IsHybrid || header.BitsPerSample != firstHeader.BitsPerSample)
                    {
                        throw new InvalidDataException($"'{filePath}' has a WavPack block whose format doesn't match the file's first block -- the file is corrupt (format can't change mid-stream).");
                    }

                    var blockChannelSamples = WavPackBlockDecoder.Decode(data, offset + WavPackBlockHeader.ByteLength, blockEnd, header, out var actualCrc, out var scale);
                    if (actualCrc != header.Crc)
                    {
                        throw new InvalidDataException($"'{filePath}' has a WavPack block CRC mismatch (computed 0x{actualCrc:x8}, recorded 0x{header.Crc:x8}) -- the file is corrupt or truncated.");
                    }

                    foreach (var channelData in blockChannelSamples)
                    {
                        for (var i = 0; i < channelData.Length; i++)
                        {
                            channelData[i] = scale.Apply(header.LeftShift, channelData[i]);
                        }

                        groupChannelSamples.Add(channelData);
                    }

                    if (groupChannelSamples.Count > 2)
                    {
                        throw new NotSupportedException($"'{filePath}' has more than 2 channels split across its per-frame block sequence; only mono and stereo WavPack are supported");
                    }

                    if (header.IsFinalBlockOfSequence)
                    {
                        offset = blockEnd;
                        break;
                    }

                    offset = blockEnd;
                    if (offset + WavPackBlockHeader.ByteLength > data.Length)
                    {
                        throw new InvalidDataException($"'{filePath}' ended in the middle of a multi-block WavPack per-frame sequence -- the file is likely truncated.");
                    }

                    header = WavPackBlockHeader.Parse(data, offset);
                }

                if (isFirstGroupInFile)
                {
                    channels = groupChannelSamples.Count;
                    bitsPerSample = groupBitsPerSample;
                    sampleRate = groupSampleRate;
                    totalSamples = groupTotalSamples;
                }
                else if (groupChannelSamples.Count != channels)
                {
                    throw new InvalidDataException($"'{filePath}' has a WavPack per-frame block sequence with {groupChannelSamples.Count} channels, but the file started with {channels} -- the channel count can't change mid-stream.");
                }

                var interleaved = new int[blockSamples * channels];
                for (var i = 0; i < blockSamples; i++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        interleaved[(i * channels) + c] = groupChannelSamples[c][i];
                    }
                }

                onBlockDecoded(interleaved, channels, sampleRate, bitsPerSample, totalSamples);
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
                    if (d.Count < 3)
                    {
                        throw new InvalidDataException($"A WavPack block's WP_ID_SAMPLE_RATE metadata is {d.Count} bytes, but this sub-block always carries exactly 3.");
                    }

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
