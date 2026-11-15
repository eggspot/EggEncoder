using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

internal sealed class H264BitReader
{
    private const int Padding = 8;

    private readonly byte[] _data;
    private readonly long _bitLength;
    private readonly long _stopBit;
    private long _position;

    public H264BitReader(ReadOnlySpan<byte> rbsp)
    {
        _data = new byte[rbsp.Length + Padding];
        rbsp.CopyTo(_data);
        _bitLength = rbsp.Length * 8L;
        _stopBit = FindStopBit(rbsp);
    }

    public long Position => _position;

    public long BitsLeft => _bitLength - _position;

    public bool IsByteAligned => (_position & 7) == 0;

    public byte[] Buffer => _data;

    public int ByteLength => (int)(_bitLength >> 3);

    public static H264BitReader FromNalPayload(ReadOnlySpan<byte> payload) => new(RemoveEmulationPrevention(payload));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var value = (uint)(Peek64() >> (64 - count));
        Advance(count);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count) => count == 0 ? 0 : (uint)(Peek64() >> (64 - count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadFlag() => ReadBits(1) != 0;

    public void SkipBits(int count)
    {
        Advance(count);
    }

    public uint ReadUe()
    {
        var window = Peek64();
        var leadingZeros = BitOperations.LeadingZeroCount(window);
        if (leadingZeros > 31)
        {
            throw new InvalidDataException("H.264: an Exp-Golomb code is longer than 32 bits");
        }

        if (leadingZeros <= 16)
        {
            var length = (2 * leadingZeros) + 1;
            Advance(length);
            return (uint)((window >> (64 - length)) - 1);
        }

        Advance(leadingZeros + 1);
        return (uint)(((1UL << leadingZeros) | ReadBits(leadingZeros)) - 1);
    }

    public int ReadSe()
    {
        var code = ReadUe();
        var magnitude = (int)((code + 1L) >> 1);
        return (code & 1) != 0 ? magnitude : -magnitude;
    }

    public uint ReadUe(uint maximum, string field)
    {
        var value = ReadUe();
        if (value > maximum)
        {
            throw new InvalidDataException($"H.264: {field} is {value}, above its maximum of {maximum}");
        }

        return value;
    }

    public int ReadSe(int minimum, int maximum, string field)
    {
        var value = ReadSe();
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"H.264: {field} is {value}, outside {minimum}..{maximum}");
        }

        return value;
    }

    public uint ReadTe(uint range) => range > 1 ? ReadUe() : (ReadFlag() ? 0u : 1u);

    public void ByteAlign()
    {
        _position = (_position + 7) & ~7L;
    }

    public bool MoreRbspData() => _position < _stopBit;

    public int CountLeadingZeroBitsAndSkipOne()
    {
        var window = Peek64();
        var zeros = BitOperations.LeadingZeroCount(window);
        if (zeros >= 32)
        {
            throw new InvalidDataException("H.264: a run of zero bits is too long");
        }

        Advance(zeros + 1);
        return zeros;
    }

    public ReadOnlySpan<byte> ReadAlignedBytes(int count)
    {
        if ((_position & 7) != 0)
        {
            throw new InvalidOperationException("Not byte aligned");
        }

        var start = (int)(_position >> 3);
        Advance(count * 8);
        return _data.AsSpan(start, count);
    }

    internal static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var length = 0;
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = b == 0 ? zeros + 1 : 0;
            output[length++] = b;
        }

        return length == output.Length ? output : output.AsSpan(0, length).ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Peek64()
    {
        var byteIndex = (int)(_position >> 3);
        if (byteIndex > _data.Length - Padding)
        {
            throw new InvalidDataException("H.264: read past the end of a NAL unit");
        }

        return BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(byteIndex)) << (int)(_position & 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Advance(int count)
    {
        _position += count;
        if (_position > _bitLength)
        {
            throw new InvalidDataException("H.264: read past the end of a NAL unit");
        }
    }

    private static long FindStopBit(ReadOnlySpan<byte> rbsp)
    {
        for (var i = rbsp.Length - 1; i >= 0; i--)
        {
            if (rbsp[i] != 0)
            {
                return (i * 8L) + 7 - BitOperations.TrailingZeroCount(rbsp[i]);
            }
        }

        return 0;
    }
}
