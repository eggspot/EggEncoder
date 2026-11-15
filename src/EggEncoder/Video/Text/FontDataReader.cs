using System.Buffers.Binary;

namespace EggEncoder.Video.Text;

internal readonly struct FontDataReader
{
    private readonly byte[] _data;

    public FontDataReader(byte[] data, int start, int length, string table)
    {
        if (start < 0 || length < 0 || (long)start + length > data.Length)
        {
            throw new InvalidDataException($"Font: the '{table}' table lies outside the file");
        }

        _data = data;
        Start = start;
        Length = length;
        Table = table;
    }

    public int Start { get; }

    public int Length { get; }

    public string Table { get; }

    public FontDataReader Slice(int offset, string? what = null)
    {
        Require(offset, 0);
        return new FontDataReader(_data, Start + offset, Length - offset, what ?? Table);
    }

    public byte U8(int offset)
    {
        Require(offset, 1);
        return _data[Start + offset];
    }

    public sbyte I8(int offset) => (sbyte)U8(offset);

    public ushort U16(int offset)
    {
        Require(offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Start + offset));
    }

    public short I16(int offset)
    {
        Require(offset, 2);
        return BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(Start + offset));
    }

    public uint U32(int offset)
    {
        Require(offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Start + offset));
    }

    public int Offset32(int offset)
    {
        var value = U32(offset);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"Font: an offset in the '{Table}' table is too large");
        }

        return (int)value;
    }

    public double F2Dot14(int offset) => I16(offset) / 16384.0;

    public void Require(int offset, int size)
    {
        if (offset < 0 || size < 0 || (long)offset + size > Length)
        {
            throw new InvalidDataException($"Font: the '{Table}' table is truncated");
        }
    }
}
