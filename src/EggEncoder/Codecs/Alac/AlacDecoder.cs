namespace EggEncoder.Codecs.Alac
{
    // Decodes a CAF file holding ALAC (Apple Lossless) audio. Supports mono and stereo, 16-bit
    // integer PCM only: 20/24-bit depths need an extra post-prediction rescale this hasn't
    // implemented with confidence (no real-world ALAC fixture was available to verify either
    // against) -- reasonable follow-up scope, not implemented here. See
    // AlacFrameDecoder/AlacRiceCoder/AlacLpcPredictor for the actual codec, and AlacFrameDecoder's
    // doc comment for the stereo channel-pair element specifically.
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

            if (config.BitDepth != 16)
            {
                throw new NotSupportedException($"'{filePath}' has {config.BitDepth}-bit samples; only 16-bit ALAC is supported");
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
