namespace EggEncoder.Codecs.H264;

internal static class CavlcResidual
{
    public static int ReadBlock(H264BitReader reader, int nC, int maxNumCoeff, Span<short> coefficients)
    {
        var token = CavlcTables.CoeffToken[CavlcTables.CoeffTokenTableIndex(nC)].Read(reader);
        var totalCoeff = token >> 2;
        var trailingOnes = token & 3;
        if (totalCoeff == 0)
        {
            return 0;
        }

        if (totalCoeff > maxNumCoeff)
        {
            throw new InvalidDataException("H.264: a block has more coefficients than positions");
        }

        Span<int> levels = stackalloc int[16];
        Span<int> runs = stackalloc int[16];
        var suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = 0; i < totalCoeff; i++)
        {
            if (i < trailingOnes)
            {
                levels[i] = reader.ReadFlag() ? -1 : 1;
                continue;
            }

            var prefix = reader.CountLeadingZeroBitsAndSkipOne();
            var levelCode = Math.Min(15, prefix) << suffixLength;
            if (suffixLength > 0 || prefix >= 14)
            {
                var suffixSize = prefix == 14 && suffixLength == 0 ? 4 : prefix >= 15 ? prefix - 3 : suffixLength;
                if (suffixSize > 0)
                {
                    levelCode += (int)reader.ReadBits(suffixSize);
                }
            }

            if (prefix >= 15 && suffixLength == 0)
            {
                levelCode += 15;
            }

            if (prefix >= 16)
            {
                levelCode += (1 << (prefix - 3)) - 4096;
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            levels[i] = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(levels[i]) > 3 << (suffixLength - 1) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        var zerosLeft = 0;
        if (totalCoeff < maxNumCoeff)
        {
            zerosLeft = maxNumCoeff == 4
                ? CavlcTables.TotalZerosChromaDc[totalCoeff - 1].Read(reader)
                : CavlcTables.TotalZeros4x4[totalCoeff - 1].Read(reader);
        }

        if (zerosLeft > maxNumCoeff - totalCoeff)
        {
            throw new InvalidDataException("H.264: total_zeros is larger than the block");
        }

        for (var i = 0; i < totalCoeff - 1; i++)
        {
            if (zerosLeft > 0)
            {
                var run = CavlcTables.RunBefore[Math.Min(zerosLeft, 7) - 1].Read(reader);
                if (run > zerosLeft)
                {
                    throw new InvalidDataException("H.264: run_before is larger than the zeros left");
                }

                runs[i] = run;
                zerosLeft -= run;
            }
            else
            {
                runs[i] = 0;
            }
        }

        runs[totalCoeff - 1] = zerosLeft;
        var position = -1;
        for (var i = totalCoeff - 1; i >= 0; i--)
        {
            position += runs[i] + 1;
            coefficients[position] = (short)Math.Clamp(levels[i], short.MinValue, short.MaxValue);
        }

        return totalCoeff;
    }
}
