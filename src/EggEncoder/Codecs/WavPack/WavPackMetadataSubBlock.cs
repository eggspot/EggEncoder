namespace EggEncoder.Codecs.WavPack
{
    // One metadata sub-block within a WavPack block, per the format spec section 3.0: a 1-byte id
    // (bit 0x80 = the size field is 3 bytes instead of 1; bit 0x40 = the actual data is one byte
    // shorter than the word count implies, i.e. an odd byte length; bits 0x3f = the function id),
    // then the size (in 16-bit words), then that many words of data.
    internal readonly struct WavPackMetadataSubBlock
    {
        public const int IdDecorrTerms = 0x02;
        public const int IdDecorrWeights = 0x03;
        public const int IdDecorrSamples = 0x04;
        public const int IdEntropyVars = 0x05;
        public const int IdInt32Info = 0x09;
        public const int IdWvBitstream = 0x0A;
        public const int IdSampleRate = 0x27;

        public required int FunctionId { get; init; }

        public required ArraySegment<byte> Data { get; init; }

        public static IEnumerable<WavPackMetadataSubBlock> ReadAll(byte[] data, int start, int end)
        {
            var p = start;
            while (p < end)
            {
                var id = data[p];
                var functionId = id & 0x3F;
                var isLarge = (id & 0x80) != 0;
                var isOdd = (id & 0x40) != 0;
                var headerLength = isLarge ? 4 : 2;

                if (p + headerLength > end)
                {
                    throw new InvalidDataException("A WavPack block's metadata ends in the middle of a sub-block's own id/size header -- the file is corrupt or truncated.");
                }

                int wordCount;
                int dataStart;
                if (isLarge)
                {
                    wordCount = data[p + 1] | (data[p + 2] << 8) | (data[p + 3] << 16);
                    dataStart = p + 4;
                }
                else
                {
                    wordCount = data[p + 1];
                    dataStart = p + 2;
                }

                var byteCount = (wordCount * 2) - (isOdd ? 1 : 0);
                if (dataStart + (wordCount * 2) > end)
                {
                    throw new InvalidDataException("A WavPack block's metadata sub-block declares a size that extends past the block's own metadata region -- the file is corrupt or truncated.");
                }

                yield return new WavPackMetadataSubBlock
                {
                    FunctionId = functionId,
                    Data = new ArraySegment<byte>(data, dataStart, byteCount),
                };

                p = dataStart + (wordCount * 2);
            }
        }
    }
}
