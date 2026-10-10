namespace EggEncoder.Codecs.WavPack
{
    // Writes one complete, standalone WavPack block (32-byte header + metadata sub-blocks +
    // compressed bitstream) for a chunk of interleaved PCM samples -- the inverse of
    // WavPackBlockHeader.Parse/WavPackMetadataSubBlock.ReadAll/WavPackBlockDecoder.Decode. Always a
    // single combined block covering all channels (both "initial" and "final" in its own
    // sequence), never WavPack's multi-channel block-splitting feature -- this project's own
    // decoder has to tolerate that shape (see WavPackDecoder.cs's own doc comment on why), but
    // nothing requires an encoder to produce it.
    internal static class WavPackBlockWriter
    {
        public static byte[] WriteBlock(int[][] channelSamples, int channels, int bitsPerSample, int sampleRate, long blockIndex, long totalSamples)
        {
            var blockSamples = channelSamples[0].Length;
            var (decorrTerms, entropyVars, bitstream, crc) = WavPackBlockEncoder.Encode(channelSamples, channels);

            var metadata = new List<byte>();
            WriteSubBlock(metadata, WavPackMetadataSubBlock.IdDecorrTerms, decorrTerms);
            WriteSubBlock(metadata, WavPackMetadataSubBlock.IdEntropyVars, entropyVars);

            var sampleRateIndex = Array.IndexOf(WavPackBlockHeader.StandardSampleRates, sampleRate);
            if (sampleRateIndex < 0)
            {
                sampleRateIndex = 0xF;
                WriteSubBlock(metadata, WavPackMetadataSubBlock.IdSampleRate, [(byte)sampleRate, (byte)(sampleRate >> 8), (byte)(sampleRate >> 16)]);
            }

            WriteSubBlock(metadata, WavPackMetadataSubBlock.IdWvBitstream, bitstream);

            var bytesPerSample = bitsPerSample / 8;
            var magnitude = bitsPerSample - 1;
            uint flags = (uint)(bytesPerSample - 1)
                | (channels == 1 ? 0x4u : 0)
                | ((uint)magnitude << 18)
                | ((uint)sampleRateIndex << 23)
                | 0x800 // initial block of sequence
                | 0x1000; // final block of sequence

            var block = new List<byte>(32 + metadata.Count);
            block.AddRange("wvpk"u8.ToArray());
            var ckSizeOffset = block.Count;
            block.AddRange(new byte[4]); // ckSize, patched below once known
            WriteUInt16(block, 0x410);
            block.Add((byte)(blockIndex >> 32));
            block.Add((byte)(totalSamples >> 32));
            WriteUInt32(block, (uint)totalSamples);
            WriteUInt32(block, (uint)blockIndex);
            WriteUInt32(block, (uint)blockSamples);
            WriteUInt32(block, flags);
            WriteUInt32(block, crc);
            block.AddRange(metadata);

            var ckSize = (uint)(block.Count - 8);
            block[ckSizeOffset] = (byte)ckSize;
            block[ckSizeOffset + 1] = (byte)(ckSize >> 8);
            block[ckSizeOffset + 2] = (byte)(ckSize >> 16);
            block[ckSizeOffset + 3] = (byte)(ckSize >> 24);

            return [.. block];
        }

        private static void WriteSubBlock(List<byte> output, int functionId, byte[] data)
        {
            var isOdd = data.Length % 2 != 0;
            var wordCount = (data.Length + 1) / 2;
            var isLarge = wordCount > 255;

            output.Add((byte)(functionId | (isOdd ? 0x40 : 0) | (isLarge ? 0x80 : 0)));
            if (isLarge)
            {
                output.Add((byte)wordCount);
                output.Add((byte)(wordCount >> 8));
                output.Add((byte)(wordCount >> 16));
            }
            else
            {
                output.Add((byte)wordCount);
            }

            output.AddRange(data);
            if (isOdd)
            {
                output.Add(0);
            }
        }

        private static void WriteUInt16(List<byte> output, ushort value)
        {
            output.Add((byte)value);
            output.Add((byte)(value >> 8));
        }

        private static void WriteUInt32(List<byte> output, uint value)
        {
            output.Add((byte)value);
            output.Add((byte)(value >> 8));
            output.Add((byte)(value >> 16));
            output.Add((byte)(value >> 24));
        }
    }
}
