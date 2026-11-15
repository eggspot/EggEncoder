namespace EggEncoder.Codecs.Jpeg;

internal sealed class JpegEntropyWriter
{
    private readonly Stream _output;
    private readonly byte[] _buffer = new byte[8192];
    private int _count;
    private uint _bits;
    private int _bitCount;

    public JpegEntropyWriter(Stream output)
    {
        _output = output;
    }

    public void WriteBits(int value, int length)
    {
        _bits = (_bits << length) | ((uint)value & ((1u << length) - 1));
        _bitCount += length;
        while (_bitCount >= 8)
        {
            _bitCount -= 8;
            var b = (byte)(_bits >> _bitCount);
            Put(b);
            if (b == 0xFF)
            {
                Put(0);
            }
        }
    }

    public void WriteBlock(ReadOnlySpan<short> zigzag, ref int previousDc, JpegHuffmanCode dc, JpegHuffmanCode ac)
    {
        var difference = zigzag[0] - previousDc;
        previousDc = zigzag[0];
        WriteValue(difference, dc, 0);
        var run = 0;
        for (var i = 1; i < 64; i++)
        {
            var level = zigzag[i];
            if (level == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                WriteBits(ac.Codes[0xF0], ac.Lengths[0xF0]);
                run -= 16;
            }

            WriteValue(level, ac, run << 4);
            run = 0;
        }

        if (run > 0)
        {
            WriteBits(ac.Codes[0], ac.Lengths[0]);
        }
    }

    public void Finish()
    {
        if (_bitCount > 0)
        {
            WriteBits((1 << (8 - _bitCount)) - 1, 8 - _bitCount);
        }

        _output.Write(_buffer, 0, _count);
        _count = 0;
    }

    private void WriteValue(int value, JpegHuffmanCode code, int runPrefix)
    {
        var magnitude = Math.Abs(value);
        var size = magnitude == 0 ? 0 : 32 - int.LeadingZeroCount(magnitude);
        var symbol = runPrefix | size;
        WriteBits(code.Codes[symbol], code.Lengths[symbol]);
        if (size > 0)
        {
            WriteBits(value < 0 ? value - 1 : value, size);
        }
    }

    private void Put(byte value)
    {
        if (_count == _buffer.Length)
        {
            _output.Write(_buffer, 0, _count);
            _count = 0;
        }

        _buffer[_count++] = value;
    }
}
