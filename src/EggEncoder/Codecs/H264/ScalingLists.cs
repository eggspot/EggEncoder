namespace EggEncoder.Codecs.H264;

internal static class ScalingLists
{
    public static readonly byte[] Flat4x4 = Enumerable.Repeat((byte)16, 16).ToArray();

    public static readonly byte[] Flat8x8 = Enumerable.Repeat((byte)16, 64).ToArray();

    public static readonly byte[] Default4x4Intra = [6, 13, 13, 20, 20, 20, 28, 28, 28, 28, 32, 32, 32, 37, 37, 42];

    public static readonly byte[] Default4x4Inter = [10, 14, 14, 20, 20, 20, 24, 24, 24, 24, 27, 27, 27, 30, 30, 34];

    public static readonly byte[] Default8x8Intra =
    [
        6, 10, 10, 13, 11, 13, 16, 16, 16, 16, 18, 18, 18, 18, 18, 23,
        23, 23, 23, 23, 23, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27,
        27, 27, 27, 27, 29, 29, 29, 29, 29, 29, 29, 31, 31, 31, 31, 31,
        31, 33, 33, 33, 33, 33, 36, 36, 36, 36, 38, 38, 38, 40, 40, 42
    ];

    public static readonly byte[] Default8x8Inter =
    [
        9, 13, 13, 15, 13, 15, 17, 17, 17, 17, 19, 19, 19, 19, 19, 21,
        21, 21, 21, 21, 21, 22, 22, 22, 22, 22, 22, 22, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27, 27,
        27, 28, 28, 28, 28, 28, 30, 30, 30, 30, 32, 32, 32, 33, 33, 35
    ];

    public static byte[][] FlatSet() =>
    [
        Flat4x4, Flat4x4, Flat4x4, Flat4x4, Flat4x4, Flat4x4,
        Flat8x8, Flat8x8, Flat8x8, Flat8x8, Flat8x8, Flat8x8
    ];

    public static byte[]?[] ReadExplicit(H264BitReader reader, int count)
    {
        var lists = new byte[]?[12];
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadFlag())
            {
                var (list, useDefault) = ReadList(reader, i < 6 ? 16 : 64);
                lists[i] = useDefault ? DefaultFor(i) : list;
            }
        }

        return lists;
    }

    public static byte[][] Resolve(byte[]?[] explicitLists, byte[][]? fallbackForFirstLists)
    {
        var lists = new byte[12][];
        for (var i = 0; i < 12; i++)
        {
            lists[i] = explicitLists[i] ?? i switch
            {
                0 or 3 or 6 or 7 => fallbackForFirstLists?[i] ?? DefaultFor(i),
                8 or 9 or 10 or 11 => lists[i - 2],
                _ => lists[i - 1]
            };
        }

        return lists;
    }

    private static (byte[] List, bool UseDefault) ReadList(H264BitReader reader, int size)
    {
        var list = new byte[size];
        var last = 8;
        var next = 8;
        var useDefault = false;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
            {
                var delta = reader.ReadSe(-128, 127, "delta_scale");
                next = (last + delta + 256) % 256;
                useDefault = j == 0 && next == 0;
            }

            list[j] = (byte)(next == 0 ? last : next);
            last = list[j];
        }

        return (list, useDefault);
    }

    private static byte[] DefaultFor(int index) => index switch
    {
        < 3 => Default4x4Intra,
        < 6 => Default4x4Inter,
        6 or 8 or 10 => Default8x8Intra,
        _ => Default8x8Inter
    };
}
