using System.Buffers.Binary;
using EggEncoder.Video;

namespace EggEncoder.Codecs.Jpeg;

/// <summary>
/// A baseline JPEG (JFIF) encoder for single frames: full-range BT.601 Y'CbCr, 4:2:0, the standard
/// Huffman tables of ITU-T T.81 Annex K.
/// </summary>
/// <remarks>
/// Quality follows ffmpeg's <c>-q:v</c> scale (1 best, 31 worst): one quantisation table for all
/// three components, the DC entry fixed at 8 and every other entry the MPEG-1 default intra matrix
/// times the scale divided by 8, rounded down, as ffmpeg's JPEG encoder writes it. ffmpeg also
/// builds image-specific Huffman tables, so its files are a little smaller at the same scale.
/// </remarks>
public static class JpegEncoder
{
    /// <summary>The scale used when none is given: ffmpeg <c>-q:v 3</c>.</summary>
    public const int DefaultQuantizerScale = 3;

    private static readonly JpegHuffmanCode LuminanceDc = new(JpegTables.LuminanceDcBits, JpegTables.DcValues);
    private static readonly JpegHuffmanCode ChrominanceDc = new(JpegTables.ChrominanceDcBits, JpegTables.DcValues);
    private static readonly JpegHuffmanCode LuminanceAc = new(JpegTables.LuminanceAcBits, JpegTables.LuminanceAcValues);
    private static readonly JpegHuffmanCode ChrominanceAc = new(JpegTables.ChrominanceAcBits, JpegTables.ChrominanceAcValues);
    private static readonly float[] Cosines = BuildCosines();

    /// <summary>Encodes <paramref name="frame"/> as a JPEG file and returns its bytes.</summary>
    /// <param name="frame">The picture; any range and matrix, converted to full-range BT.601.</param>
    /// <param name="quantizerScale">ffmpeg's <c>-q:v</c>, 1 to 31.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantizerScale"/> is outside 1 to 31.</exception>
    public static byte[] Encode(VideoFrame frame, int quantizerScale = DefaultQuantizerScale)
    {
        using var output = new MemoryStream();
        Encode(frame, output, quantizerScale);
        return output.ToArray();
    }

    /// <summary>Encodes <paramref name="frame"/> as a JPEG file into <paramref name="destination"/>, which stays open.</summary>
    /// <param name="frame">The picture; any range and matrix, converted to full-range BT.601.</param>
    /// <param name="destination">Where the file is written, from its current position.</param>
    /// <param name="quantizerScale">ffmpeg's <c>-q:v</c>, 1 to 31.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantizerScale"/> is outside 1 to 31.</exception>
    /// <exception cref="ArgumentException">The frame is larger than 65,535 pixels in either direction.</exception>
    public static void Encode(VideoFrame frame, Stream destination, int quantizerScale = DefaultQuantizerScale)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantizerScale, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantizerScale, 31);
        if (frame.Width > 65535 || frame.Height > 65535)
        {
            throw new ArgumentException("A JPEG picture is at most 65,535 pixels wide and high", nameof(frame));
        }

        var table = JpegTables.QuantizationTable(quantizerScale);
        var planes = JpegPlanes.From(frame);
        WriteHeaders(destination, frame.Width, frame.Height, table);
        WriteScan(destination, planes, table);
        destination.Write([0xFF, 0xD9]);
    }

    private static void WriteHeaders(Stream output, int width, int height, byte[] table)
    {
        output.Write([0xFF, 0xD8]);
        output.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        Span<byte> quantization = stackalloc byte[69];
        quantization[0] = 0xFF;
        quantization[1] = 0xDB;
        BinaryPrimitives.WriteUInt16BigEndian(quantization[2..], 67);
        quantization[4] = 0;
        for (var i = 0; i < 64; i++)
        {
            quantization[5 + i] = table[JpegTables.ZigZag[i]];
        }

        output.Write(quantization);
        Span<byte> frameHeader = stackalloc byte[19];
        frameHeader[0] = 0xFF;
        frameHeader[1] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(frameHeader[2..], 17);
        frameHeader[4] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(frameHeader[5..], (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frameHeader[7..], (ushort)width);
        frameHeader[9] = 3;
        frameHeader[10] = 1;
        frameHeader[11] = 0x22;
        frameHeader[12] = 0;
        frameHeader[13] = 2;
        frameHeader[14] = 0x11;
        frameHeader[15] = 0;
        frameHeader[16] = 3;
        frameHeader[17] = 0x11;
        frameHeader[18] = 0;
        output.Write(frameHeader);
        WriteHuffmanTable(output, 0x00, JpegTables.LuminanceDcBits, JpegTables.DcValues);
        WriteHuffmanTable(output, 0x10, JpegTables.LuminanceAcBits, JpegTables.LuminanceAcValues);
        WriteHuffmanTable(output, 0x01, JpegTables.ChrominanceDcBits, JpegTables.DcValues);
        WriteHuffmanTable(output, 0x11, JpegTables.ChrominanceAcBits, JpegTables.ChrominanceAcValues);
        output.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
    }

    private static void WriteHuffmanTable(Stream output, byte classAndId, byte[] bits, byte[] values)
    {
        var length = 2 + 1 + 16 + values.Length;
        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, classAndId]);
        output.Write(bits);
        output.Write(values);
    }

    private static void WriteScan(Stream output, JpegPlanes planes, byte[] table)
    {
        var writer = new JpegEntropyWriter(output);
        Span<float> samples = stackalloc float[64];
        Span<float> coefficients = stackalloc float[64];
        Span<short> zigzag = stackalloc short[64];
        Span<float> reciprocals = stackalloc float[64];
        for (var i = 0; i < 64; i++)
        {
            reciprocals[i] = 1f / table[i];
        }

        var previousY = 0;
        var previousCb = 0;
        var previousCr = 0;
        var columns = (planes.Width + 15) / 16;
        var rows = (planes.Height + 15) / 16;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                for (var block = 0; block < 4; block++)
                {
                    var x = (column * 16) + ((block & 1) * 8);
                    var y = (row * 16) + ((block >> 1) * 8);
                    planes.Load(planes.Luma, planes.Width, planes.Height, x, y, samples);
                    Transform(samples, coefficients, reciprocals, zigzag);
                    writer.WriteBlock(zigzag, ref previousY, LuminanceDc, LuminanceAc);
                }

                planes.Load(planes.Cb, planes.ChromaWidth, planes.ChromaHeight, column * 8, row * 8, samples);
                Transform(samples, coefficients, reciprocals, zigzag);
                writer.WriteBlock(zigzag, ref previousCb, ChrominanceDc, ChrominanceAc);
                planes.Load(planes.Cr, planes.ChromaWidth, planes.ChromaHeight, column * 8, row * 8, samples);
                Transform(samples, coefficients, reciprocals, zigzag);
                writer.WriteBlock(zigzag, ref previousCr, ChrominanceDc, ChrominanceAc);
            }
        }

        writer.Finish();
    }

    private static void Transform(Span<float> samples, Span<float> coefficients, ReadOnlySpan<float> reciprocals, Span<short> zigzag)
    {
        Span<float> rows = stackalloc float[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var x = 0; x < 8; x++)
                {
                    sum += Cosines[(u * 8) + x] * samples[(y * 8) + x];
                }

                rows[(y * 8) + u] = sum;
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++)
                {
                    sum += Cosines[(v * 8) + y] * rows[(y * 8) + u];
                }

                coefficients[(v * 8) + u] = sum;
            }
        }

        for (var i = 0; i < 64; i++)
        {
            var position = JpegTables.ZigZag[i];
            zigzag[i] = (short)MathF.Round(coefficients[position] * reciprocals[position], MidpointRounding.AwayFromZero);
        }
    }

    private static float[] BuildCosines()
    {
        var cosines = new float[64];
        for (var u = 0; u < 8; u++)
        {
            var scale = u == 0 ? 1 / Math.Sqrt(2) : 1;
            for (var x = 0; x < 8; x++)
            {
                cosines[(u * 8) + x] = (float)(scale / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16));
            }
        }

        return cosines;
    }
}
