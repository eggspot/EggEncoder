using EggEncoder.Transform;

namespace EggEncoder.Codecs.Tta
{
    // Decodes a TTA (True Audio) file. Supports mono and stereo, 16-bit integer PCM only -- see
    // TtaFrameDecoder's doc comment for the per-sample-interleaved stereo decorrelation this relies
    // on, and TtaFixedPredictor's for why 16-bit specifically (its shift constant is bit-depth
    // dependent; other depths are out of scope here, matching ALAC's scope decision).
    public static class TtaDecoder
    {
        public static TtaStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var reader = TtaReader.Open(filePath);

            if (reader.Channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' has {reader.Channels} channels; only mono and stereo TTA are supported");
            }

            if (reader.BitsPerSample != 16)
            {
                throw new NotSupportedException($"'{filePath}' has {reader.BitsPerSample}-bit samples; only 16-bit TTA is supported");
            }

            var remainingSamples = reader.TotalSamples;

            while (reader.HasMoreFrames)
            {
                var frameBytes = reader.ReadNextFrame();
                var samplesThisFrame = (int)Math.Min(remainingSamples, reader.FrameLength);

                var bitReader = new BitReader(frameBytes);
                var samples = TtaFrameDecoder.DecodeFrame(bitReader, reader.Channels, samplesThisFrame);

                onBlockDecoded(samples, reader.Channels, reader.SampleRate, reader.BitsPerSample, reader.TotalSamples);

                remainingSamples -= samplesThisFrame;
            }

            return new TtaStreamInfo
            {
                Channels = reader.Channels,
                SampleRate = reader.SampleRate,
                BitsPerSample = reader.BitsPerSample,
                TotalSamples = reader.TotalSamples
            };
        }
    }

    public class TtaStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
