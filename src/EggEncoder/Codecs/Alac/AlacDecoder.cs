namespace EggEncoder.Codecs.Alac
{
    // Decodes a CAF file holding ALAC (Apple Lossless) audio. Supports mono and stereo, 16-bit or
    // 24-bit integer PCM -- AlacRiceCoder/AlacLpcPredictor/AlacFrameEncoder are already fully generic
    // over the sample bit depth (predictionBitsPerSample = BitDepth + channels - 1 widens the Rice
    // escape automatically), so no rescale is needed here; only the two validation checks and the
    // encoder's hardcoded BitDepth literal ever assumed 16. 20-bit is deliberately excluded: real
    // ALAC encoders split non-16-bit depths into a predicted/Rice-coded high portion plus a raw
    // "extra/wasted bits" low portion per sample (2-bit extraBitsBytes field, confirmed from ffmpeg's
    // alac.c/alacenc.c), and AlacFrameDecoder already rejects any nonzero extraBitsBytes rather than
    // guess at that combination's exact semantics without a verified reference. This codec always
    // writes extraBitsBytes=0 on encode (pushing the full bit depth straight through the Rice/LPC
    // pipeline, exactly like 16-bit already does), which is spec-valid and real-world-decodable, not
    // just a self-referential round-trip -- it only forgoes an optional compression optimization.
    // ffmpeg's own alacenc.c only ever emits 16-bit or 24-bit for exactly this reason (its encoder
    // doesn't support 20-bit or 32-bit either), which this mirrors. Decoding a third-party 24-bit
    // ALAC file that *does* use extraBitsBytes>0 (e.g. ffmpeg's own encoder defaults to 8) is out of
    // scope -- that combination mechanism couldn't be verified with confidence against an
    // authoritative source. See AlacFrameDecoder/AlacRiceCoder/AlacLpcPredictor for the actual codec,
    // and AlacFrameDecoder's doc comment for the stereo channel-pair element specifically.
    public static class AlacDecoder
    {
        public static AlacStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var reader = CafReader.Open(filePath);
            var config = reader.Config;

            if (config.NumChannels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' has {config.NumChannels} channels; only mono and stereo ALAC are supported");
            }

            if (config.BitDepth is not 16 and not 24)
            {
                throw new NotSupportedException($"'{filePath}' has {config.BitDepth}-bit samples; only 16-bit and 24-bit ALAC are supported");
            }

            while (reader.HasMorePackets)
            {
                var packetBytes = reader.ReadNextPacket();
                var samples = AlacFrameDecoder.DecodePacket(packetBytes, config);

                onBlockDecoded(samples, config.NumChannels, config.SampleRate, config.BitDepth, reader.TotalValidFrames);
            }

            return new AlacStreamInfo
            {
                Channels = config.NumChannels,
                SampleRate = config.SampleRate,
                BitsPerSample = config.BitDepth,
                TotalSamples = reader.TotalValidFrames
            };
        }
    }

    public class AlacStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
