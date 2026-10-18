using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // Decodes one granule/channel's big_values + count1 Huffman-coded data into 576 signed
    // quantized integers (is[l]) -- the mirror image of Mp3Quantizer.Write, reusing the exact same
    // Mp3HuffmanTables tables and x*width+y (big_values) / v*8+w*4+x*2+y (count1) flattening
    // convention those tables' own doc comments already establish, per the bitstream-syntax
    // document's own Table 6 ("Bitstream of huffmancodebits") and section 4.3.8 ("Huffman
    // decoding"). Long blocks only -- region0_count/region1_count are read as explicitly
    // transmitted side-info fields here (window_switching_flag==0), not the short/mixed-block
    // implicit values section 4.2.4's own region0_count semantics describe for that case (see
    // docs/external-dependency-removal-plan.md Phase 1 sub-item 1c).
    internal static class Mp3HuffmanDecoder
    {
        private const int CoefficientCount = 576;

        /// <summary>
        /// <paramref name="reader"/> must already be positioned at the start of this granule/
        /// channel's own Huffman-coded data (immediately after its scalefactors). On return, the
        /// reader is positioned exactly at <paramref name="part23EndBitPosition"/> -- the bitstream-
        /// syntax document's own "any huffmancodebits left over are stuffing bits, discarded"
        /// convention, so the next granule/channel's own data starts at the right place regardless
        /// of whether this one's own count1 region ended early (576 lines reached) or exactly at
        /// the budget boundary.
        /// </summary>
        public static int[] Decode(BitReader reader, int part23EndBitPosition, int bigValues, int[] tableSelect, int region0Count, int region1Count, int count1TableSelect, (int IndexOfStart, int IndexOfEnd)[] bands)
        {
            var quantized = new int[CoefficientCount];

            var region0EndLine = bands[Math.Min(region0Count, bands.Length - 1)].IndexOfEnd;
            var region1EndLine = bands[Math.Min(region0Count + 1 + region1Count, bands.Length - 1)].IndexOfEnd;

            var lineIndex = 0;
            for (var pair = 0; pair < bigValues; pair++)
            {
                var region = lineIndex <= region0EndLine ? 0 : lineIndex <= region1EndLine ? 1 : 2;
                var (table, width, linbits) = Mp3HuffmanTables.GetBigValueTable(tableSelect[region]);
                var maxDirect = width - 1;

                var flatIndex = table.Decode(reader);
                var x = flatIndex / width;
                var y = flatIndex % width;

                if (x == maxDirect && linbits > 0)
                {
                    x += (int)reader.ReadBits(linbits);
                }

                if (x != 0 && reader.ReadBits(1) == 1)
                {
                    x = -x;
                }

                if (y == maxDirect && linbits > 0)
                {
                    y += (int)reader.ReadBits(linbits);
                }

                if (y != 0 && reader.ReadBits(1) == 1)
                {
                    y = -y;
                }

                quantized[lineIndex] = x;
                quantized[lineIndex + 1] = y;
                lineIndex += 2;
            }

            var count1Table = count1TableSelect == 1 ? Mp3HuffmanTables.TableB : Mp3HuffmanTables.TableA;
            while (reader.BitPosition < part23EndBitPosition && lineIndex + 4 <= CoefficientCount)
            {
                var flatIndex = count1Table.Decode(reader);
                var v = (flatIndex >> 3) & 1;
                var w = (flatIndex >> 2) & 1;
                var x = (flatIndex >> 1) & 1;
                var y = flatIndex & 1;

                if (v != 0 && reader.ReadBits(1) == 1)
                {
                    v = -v;
                }

                if (w != 0 && reader.ReadBits(1) == 1)
                {
                    w = -w;
                }

                if (x != 0 && reader.ReadBits(1) == 1)
                {
                    x = -x;
                }

                if (y != 0 && reader.ReadBits(1) == 1)
                {
                    y = -y;
                }

                quantized[lineIndex] = v;
                quantized[lineIndex + 1] = w;
                quantized[lineIndex + 2] = x;
                quantized[lineIndex + 3] = y;
                lineIndex += 4;
            }

            reader.SkipToBitPosition(part23EndBitPosition);

            return quantized;
        }
    }
}
