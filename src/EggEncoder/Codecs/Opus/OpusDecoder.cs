using Concentus;
using Concentus.Structs;

namespace EggEncoder.Codecs.Opus
{
    // Decodes an OggOpus file. Supports mono and stereo, channel mapping family 0 only -- see
    // OpusEncoderSession's doc comment for why this always decodes at 48kHz regardless of what a
    // file's OpusHead claims as its "original" sample rate (that field is purely informational
    // per RFC 7845; Opus's actual internal/decodable rates are always one of 8/12/16/24/48kHz, and
    // 48kHz -- the highest -- is never lossy relative to what's actually stored).
    public static class OpusDecoder
    {
        private const int SampleRate = 48000;
        private const int MaxSamplesPerPacket = 5760; // 120ms at 48kHz, Opus's longest possible packet

        public static OpusStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var opusHead = ReadOpusHead(filePath);
            var totalSamples = CountTotalSamples(filePath, opusHead);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return DecodeCore(stream, filePath, opusHead, totalSamples, onBlockDecoded);
        }

        // Stream-based overload (docs/feature-roadmap.md item 1): reads the whole source into
        // memory up front, the same "read-fully-into-byte[]" shape FlacDecoder/AacDecoder/
        // WavPackDecoder's own Stream overloads already use, rather than requiring source.CanSeek
        // -- OggPageReader itself has no Seek/Position call anywhere (confirmed directly from its
        // own source), so this isn't strictly required for a single pass, but ReadOpusHead/
        // CountTotalSamples below each need their own independent pass from the very start of the
        // stream, and re-reading an arbitrary caller-owned source three times over isn't possible
        // without buffering it once first. Does not dispose source.
        public static OpusStreamInfo Decode(Stream source, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var buffered = new MemoryStream();
            source.CopyTo(buffered);
            var bytes = buffered.ToArray();

            const string displayName = "<stream>";
            var opusHead = ReadOpusHead(new MemoryStream(bytes), displayName);
            var totalSamples = CountTotalSamples(new MemoryStream(bytes), opusHead, displayName);

            using var stream = new MemoryStream(bytes);
            return DecodeCore(stream, displayName, opusHead, totalSamples, onBlockDecoded);
        }

        private static OpusStreamInfo DecodeCore(Stream stream, string displayName, OpusHead opusHead, long totalSamples, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var reader = new OggPageReader(stream);

            var tags = SkipHeaderPackets(reader, displayName);

            var decoder = OpusCodecFactory.CreateDecoder(SampleRate, opusHead.Channels);
            try
            {
                var outputBuffer = new short[MaxSamplesPerPacket * opusHead.Channels];
                var remainingPreSkip = opusHead.PreSkip;

                while (reader.ReadNextPacket() is { } packet)
                {
                    var decodedFrames = decoder.Decode(packet, outputBuffer, MaxSamplesPerPacket);

                    var skipFrames = Math.Min(remainingPreSkip, decodedFrames);
                    remainingPreSkip -= skipFrames;

                    var emitFrames = decodedFrames - skipFrames;
                    if (emitFrames > 0)
                    {
                        var block = outputBuffer.AsSpan(skipFrames * opusHead.Channels, emitFrames * opusHead.Channels);
                        var intBlock = new int[block.Length];
                        for (var i = 0; i < block.Length; i++)
                        {
                            intBlock[i] = block[i];
                        }

                        onBlockDecoded(intBlock, opusHead.Channels, SampleRate, 16, totalSamples);
                    }
                }

                return new OpusStreamInfo
                {
                    Channels = opusHead.Channels,
                    SampleRate = SampleRate,
                    BitsPerSample = 16,
                    TotalSamples = totalSamples,
                    Tags = tags.Count > 0 ? tags : null
                };
            }
            finally
            {
                decoder.Dispose();
            }
        }

        private static OpusHead ReadOpusHead(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return ReadOpusHead(stream, filePath);
        }

        private static OpusHead ReadOpusHead(Stream stream, string displayName)
        {
            using var reader = new OggPageReader(stream);

            var headPacket = reader.ReadNextPacket()
                ?? throw new InvalidDataException($"'{displayName}' is not a valid OggOpus file: no packets found");

            return OpusHeaderPackets.ParseOpusHead(headPacket, displayName);
        }

        // A cheap first pass: OpusPacketInfo.GetNumSamples reads only a packet's 1-byte TOC
        // (table of contents) to determine its duration, without actually decoding it -- enough
        // to compute an exact total sample count up front (for onBlockDecoded's totalSamples
        // parameter) without decoding the whole file twice.
        private static long CountTotalSamples(string filePath, OpusHead opusHead)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return CountTotalSamples(stream, opusHead, filePath);
        }

        private static long CountTotalSamples(Stream stream, OpusHead opusHead, string displayName)
        {
            using var reader = new OggPageReader(stream);

            _ = SkipHeaderPackets(reader, displayName);

            var totalSamplesIncludingPreSkip = 0L;
            while (reader.ReadNextPacket() is { } packet)
            {
                var samples = OpusPacketInfo.GetNumSamples(packet, SampleRate);
                if (samples < 0)
                {
                    throw new InvalidDataException($"'{displayName}' contains a malformed Opus packet");
                }

                totalSamplesIncludingPreSkip += samples;
            }

            return Math.Max(0, totalSamplesIncludingPreSkip - opusHead.PreSkip);
        }

        private static IReadOnlyDictionary<string, string> SkipHeaderPackets(OggPageReader reader, string displayName)
        {
            _ = reader.ReadNextPacket() ?? throw new InvalidDataException($"'{displayName}' is not a valid OggOpus file: missing 'OpusHead' packet");

            var tagsPacket = reader.ReadNextPacket()
                ?? throw new InvalidDataException($"'{displayName}' is not a valid OggOpus file: missing 'OpusTags' packet");
            return OpusHeaderPackets.ParseOpusTags(tagsPacket, displayName);
        }
    }

    public class OpusStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }

        public IReadOnlyDictionary<string, string>? Tags { get; init; }
    }
}
