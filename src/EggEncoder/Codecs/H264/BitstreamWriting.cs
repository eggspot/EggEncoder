using System.Numerics;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.H264;

internal static class ExpGolombWriter
{
    public static void WriteUe(this BitWriter writer, uint value)
    {
        var coded = (ulong)value + 1;
        var length = 64 - BitOperations.LeadingZeroCount(coded);
        writer.WriteBits(0, length - 1);
        writer.WriteBits((uint)coded, length);
    }

    public static void WriteSe(this BitWriter writer, int value) => writer.WriteUe(MapSigned(value));

    public static void WriteFlag(this BitWriter writer, bool value) => writer.WriteBits(value ? 1u : 0u, 1);

    public static void WriteTrailingBits(this BitWriter writer)
    {
        writer.WriteBits(1, 1);
        writer.ByteAlign();
    }

    public static int UeLength(uint value) => (2 * (31 - BitOperations.LeadingZeroCount(value + 1))) + 1;

    public static int SeLength(int value) => UeLength(MapSigned(value));

    private static uint MapSigned(int value) => value > 0 ? (uint)((2 * value) - 1) : (uint)(-2 * (long)value);
}

internal static class NalUnitWriter
{
    public static void AppendLengthPrefixed(List<byte> output, int header, ReadOnlySpan<byte> rbsp)
    {
        var lengthIndex = output.Count;
        output.AddRange([0, 0, 0, 0]);
        output.Add((byte)header);
        AppendEscaped(output, rbsp);
        var length = output.Count - lengthIndex - 4;
        output[lengthIndex] = (byte)(length >> 24);
        output[lengthIndex + 1] = (byte)(length >> 16);
        output[lengthIndex + 2] = (byte)(length >> 8);
        output[lengthIndex + 3] = (byte)length;
    }

    public static byte[] Build(int header, ReadOnlySpan<byte> rbsp)
    {
        var output = new List<byte>(rbsp.Length + 8) { (byte)header };
        AppendEscaped(output, rbsp);
        return [.. output];
    }

    private static void AppendEscaped(List<byte> output, ReadOnlySpan<byte> rbsp)
    {
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            output.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }

        if (zeros > 0)
        {
            output.Add(3);
        }
    }
}

internal static class CavlcWriter
{
    public static int WriteBlock(BitWriter writer, int nC, int maxNumCoeff, ReadOnlySpan<short> scanned)
    {
        Span<int> levels = stackalloc int[16];
        Span<int> positions = stackalloc int[16];
        var totalCoeff = 0;
        for (var i = maxNumCoeff - 1; i >= 0; i--)
        {
            if (scanned[i] != 0)
            {
                levels[totalCoeff] = scanned[i];
                positions[totalCoeff] = i;
                totalCoeff++;
            }
        }

        var trailingOnes = 0;
        while (trailingOnes < Math.Min(3, totalCoeff) && Math.Abs(levels[trailingOnes]) == 1)
        {
            trailingOnes++;
        }

        var (tokenBits, tokenLength) = CavlcTables.CoeffToken[CavlcTables.CoeffTokenTableIndex(nC)].CodeFor((totalCoeff << 2) | trailingOnes);
        writer.WriteBits(tokenBits, tokenLength);
        if (totalCoeff == 0)
        {
            return 0;
        }

        for (var i = 0; i < trailingOnes; i++)
        {
            writer.WriteBits(levels[i] < 0 ? 1u : 0u, 1);
        }

        var suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = trailingOnes; i < totalCoeff; i++)
        {
            var level = levels[i];
            var levelCode = level > 0 ? (2 * level) - 2 : (-2 * level) - 1;
            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode -= 2;
            }

            WriteLevel(writer, levelCode, suffixLength);
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(level) > 3 << (suffixLength - 1) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        var zerosLeft = positions[0] + 1 - totalCoeff;
        if (totalCoeff < maxNumCoeff)
        {
            var table = maxNumCoeff == 4 ? CavlcTables.TotalZerosChromaDc[totalCoeff - 1] : CavlcTables.TotalZeros4x4[totalCoeff - 1];
            var (bits, length) = table.CodeFor(zerosLeft);
            writer.WriteBits(bits, length);
        }

        for (var i = 0; i < totalCoeff - 1 && zerosLeft > 0; i++)
        {
            var run = positions[i] - positions[i + 1] - 1;
            var (bits, length) = CavlcTables.RunBefore[Math.Min(zerosLeft, 7) - 1].CodeFor(run);
            writer.WriteBits(bits, length);
            zerosLeft -= run;
        }

        return totalCoeff;
    }

    private static void WriteLevel(BitWriter writer, int levelCode, int suffixLength)
    {
        if (suffixLength == 0)
        {
            if (levelCode < 14)
            {
                WritePrefix(writer, levelCode);
                return;
            }

            if (levelCode < 30)
            {
                WritePrefix(writer, 14);
                writer.WriteBits((uint)(levelCode - 14), 4);
                return;
            }

            WriteEscape(writer, levelCode - 30);
            return;
        }

        if (levelCode >> suffixLength < 15)
        {
            WritePrefix(writer, levelCode >> suffixLength);
            writer.WriteBits((uint)(levelCode & ((1 << suffixLength) - 1)), suffixLength);
            return;
        }

        WriteEscape(writer, levelCode - (15 << suffixLength));
    }

    private static void WriteEscape(BitWriter writer, int remainder)
    {
        if (remainder < 4096)
        {
            WritePrefix(writer, 15);
            writer.WriteBits((uint)remainder, 12);
            return;
        }

        var prefix = 16;
        while (remainder - ((1 << (prefix - 3)) - 4096) >= 1 << (prefix - 3))
        {
            prefix++;
        }

        WritePrefix(writer, prefix);
        writer.WriteBits((uint)(remainder - ((1 << (prefix - 3)) - 4096)), prefix - 3);
    }

    private static void WritePrefix(BitWriter writer, int zeros)
    {
        writer.WriteBits(0, zeros);
        writer.WriteBits(1, 1);
    }
}
