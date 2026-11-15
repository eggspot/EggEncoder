namespace EggEncoder.UnitTests.TestUtilities;

public sealed class BaselineJpegDecoder
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    private readonly Dictionary<int, int[]> _quantization = [];
    private readonly Dictionary<int, Huffman> _huffman = [];
    private Component[] _components = [];
    private byte[] _data = [];
    private int _position;
    private int _bits;
    private int _bitCount;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool SawJfif { get; private set; }

    public IReadOnlyDictionary<int, int[]> QuantizationTablesInZigZagOrder => _quantization;

    public byte[][] Planes { get; private set; } = [];

    public (int Width, int Height)[] PlaneSizes { get; private set; } = [];

    public static BaselineJpegDecoder Decode(byte[] file)
    {
        var decoder = new BaselineJpegDecoder { _data = file };
        decoder.Run();
        return decoder;
    }

    private void Run()
    {
        if (_data[0] != 0xFF || _data[1] != 0xD8)
        {
            throw new InvalidDataException("No SOI");
        }

        _position = 2;
        while (true)
        {
            if (_data[_position] != 0xFF)
            {
                throw new InvalidDataException("Expected a marker");
            }

            var marker = _data[_position + 1];
            _position += 2;
            if (marker == 0xD9)
            {
                return;
            }

            var length = (_data[_position] << 8) | _data[_position + 1];
            var segment = _data.AsSpan(_position + 2, length - 2);
            _position += length;
            switch (marker)
            {
                case 0xE0:
                    SawJfif = segment[..5].SequenceEqual("JFIF\0"u8);
                    break;
                case 0xDB:
                    ReadQuantization(segment);
                    break;
                case 0xC0:
                    ReadFrame(segment);
                    break;
                case 0xC4:
                    ReadHuffman(segment);
                    break;
                case 0xDA:
                    ReadScan(segment);
                    break;
                default:
                    if (marker is >= 0xC1 and <= 0xCF)
                    {
                        throw new NotSupportedException($"Marker {marker:X2} is not baseline");
                    }

                    break;
            }
        }
    }

    private void ReadQuantization(ReadOnlySpan<byte> segment)
    {
        var index = 0;
        while (index < segment.Length)
        {
            if (segment[index] >> 4 != 0)
            {
                throw new NotSupportedException("16-bit quantisation table");
            }

            var table = new int[64];
            for (var i = 0; i < 64; i++)
            {
                table[i] = segment[index + 1 + i];
            }

            _quantization[segment[index] & 15] = table;
            index += 65;
        }
    }

    private void ReadFrame(ReadOnlySpan<byte> segment)
    {
        if (segment[0] != 8)
        {
            throw new NotSupportedException("Precision other than 8");
        }

        Height = (segment[1] << 8) | segment[2];
        Width = (segment[3] << 8) | segment[4];
        _components = new Component[segment[5]];
        for (var i = 0; i < _components.Length; i++)
        {
            _components[i] = new Component(segment[6 + (i * 3)], segment[7 + (i * 3)] >> 4, segment[7 + (i * 3)] & 15, segment[8 + (i * 3)]);
        }
    }

    private void ReadHuffman(ReadOnlySpan<byte> segment)
    {
        var index = 0;
        while (index < segment.Length)
        {
            var id = segment[index];
            var counts = segment.Slice(index + 1, 16).ToArray();
            var total = counts.Sum(c => c);
            var values = segment.Slice(index + 17, total).ToArray();
            _huffman[id] = new Huffman(counts, values);
            index += 17 + total;
        }
    }

    private void ReadScan(ReadOnlySpan<byte> segment)
    {
        var count = segment[0];
        var order = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var id = segment[1 + (i * 2)];
            var component = _components.Single(c => c.Id == id);
            component.DcTable = segment[2 + (i * 2)] >> 4;
            component.AcTable = segment[2 + (i * 2)] & 15;
            order[i] = component;
        }

        var maxH = _components.Max(c => c.H);
        var maxV = _components.Max(c => c.V);
        var mcuColumns = (Width + (8 * maxH) - 1) / (8 * maxH);
        var mcuRows = (Height + (8 * maxV) - 1) / (8 * maxV);
        foreach (var component in _components)
        {
            component.BlockColumns = mcuColumns * component.H;
            component.BlockRows = mcuRows * component.V;
            component.Samples = new byte[component.BlockColumns * 8 * component.BlockRows * 8];
        }

        _bits = 0;
        _bitCount = 0;
        Span<int> block = stackalloc int[64];
        for (var row = 0; row < mcuRows; row++)
        {
            for (var column = 0; column < mcuColumns; column++)
            {
                foreach (var component in order)
                {
                    for (var v = 0; v < component.V; v++)
                    {
                        for (var h = 0; h < component.H; h++)
                        {
                            DecodeBlock(component, block);
                            Store(component, block, (column * component.H) + h, (row * component.V) + v);
                        }
                    }
                }
            }
        }

        while (!(_data[_position] == 0xFF && _data[_position + 1] != 0))
        {
            _position++;
        }

        Planes = new byte[_components.Length][];
        PlaneSizes = new (int, int)[_components.Length];
        for (var i = 0; i < _components.Length; i++)
        {
            var component = _components[i];
            var width = (Width * component.H + maxH - 1) / maxH;
            var height = (Height * component.V + maxV - 1) / maxV;
            var plane = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                Array.Copy(component.Samples, y * component.BlockColumns * 8, plane, y * width, width);
            }

            Planes[i] = plane;
            PlaneSizes[i] = (width, height);
        }
    }

    private void DecodeBlock(Component component, Span<int> block)
    {
        block.Clear();
        var quantization = _quantization[component.QuantizationTable];
        var size = DecodeSymbol(_huffman[component.DcTable]);
        component.PreviousDc += Extend(ReadBits(size), size);
        block[0] = component.PreviousDc * quantization[0];
        for (var k = 1; k < 64;)
        {
            var symbol = DecodeSymbol(_huffman[0x10 | component.AcTable]);
            var run = symbol >> 4;
            var length = symbol & 15;
            if (length == 0)
            {
                if (run == 15)
                {
                    k += 16;
                    continue;
                }

                break;
            }

            k += run;
            block[ZigZag[k]] = Extend(ReadBits(length), length) * quantization[k];
            k++;
        }
    }

    private static void Store(Component component, Span<int> block, int blockX, int blockY)
    {
        Span<double> output = stackalloc double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0.0;
                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        var cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                        var cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                        sum += cu * cv * block[(v * 8) + u] * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * Math.Cos(((2 * y) + 1) * v * Math.PI / 16);
                    }
                }

                output[(y * 8) + x] = (sum / 4) + 128;
            }
        }

        var stride = component.BlockColumns * 8;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                component.Samples[(((blockY * 8) + y) * stride) + (blockX * 8) + x] = (byte)Math.Clamp(Math.Round(output[(y * 8) + x]), 0, 255);
            }
        }
    }

    private int DecodeSymbol(Huffman table)
    {
        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            code = (code << 1) | ReadBit();
            if (table.TryGet(code, length, out var value))
            {
                return value;
            }
        }

        throw new InvalidDataException("Bad Huffman code");
    }

    private int ReadBits(int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    private int ReadBit()
    {
        if (_bitCount == 0)
        {
            var b = _data[_position++];
            if (b == 0xFF)
            {
                if (_data[_position] != 0)
                {
                    throw new InvalidDataException("Marker inside entropy-coded data");
                }

                _position++;
            }

            _bits = b;
            _bitCount = 8;
        }

        _bitCount--;
        return (_bits >> _bitCount) & 1;
    }

    private static int Extend(int value, int size) => size == 0 ? 0 : value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;

    private sealed class Component(int id, int h, int v, int quantizationTable)
    {
        public int Id { get; } = id;

        public int H { get; } = h;

        public int V { get; } = v;

        public int QuantizationTable { get; } = quantizationTable;

        public int DcTable { get; set; }

        public int AcTable { get; set; }

        public int PreviousDc { get; set; }

        public int BlockColumns { get; set; }

        public int BlockRows { get; set; }

        public byte[] Samples { get; set; } = [];
    }

    private sealed class Huffman
    {
        private readonly Dictionary<(int Code, int Length), int> _codes = [];

        public Huffman(byte[] counts, byte[] values)
        {
            var code = 0;
            var index = 0;
            for (var length = 1; length <= 16; length++)
            {
                for (var i = 0; i < counts[length - 1]; i++)
                {
                    _codes[(code, length)] = values[index++];
                    code++;
                }

                code <<= 1;
            }
        }

        public bool TryGet(int code, int length, out int value) => _codes.TryGetValue((code, length), out value);
    }
}
