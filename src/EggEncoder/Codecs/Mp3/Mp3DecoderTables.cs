namespace EggEncoder.Codecs.Mp3;

internal static class Mp3DecoderTables
{
    public static readonly int[][] LongBandWidths =
    [
        [4, 4, 4, 4, 4, 4, 6, 6, 8, 8, 10, 12, 16, 20, 24, 28, 34, 42, 50, 54, 76, 158],
        [4, 4, 4, 4, 4, 4, 6, 6, 6, 8, 10, 12, 16, 18, 22, 28, 34, 40, 46, 54, 54, 192],
        [4, 4, 4, 4, 4, 4, 6, 6, 8, 10, 12, 16, 20, 24, 30, 38, 46, 56, 68, 84, 102, 26],
        [6, 6, 6, 6, 6, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 38, 46, 52, 60, 68, 58, 54],
        [6, 6, 6, 6, 6, 6, 8, 10, 12, 14, 16, 18, 22, 26, 32, 38, 46, 54, 62, 70, 76, 36],
        [6, 6, 6, 6, 6, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 38, 46, 52, 60, 68, 58, 54],
        [6, 6, 6, 6, 6, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 38, 46, 52, 60, 68, 58, 54],
        [6, 6, 6, 6, 6, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 38, 46, 52, 60, 68, 58, 54],
        [12, 12, 12, 12, 12, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 76, 90, 2, 2, 2, 2, 2]
    ];

    public static readonly int[][] ShortBandWidths =
    [
        [4, 4, 4, 4, 6, 8, 10, 12, 14, 18, 22, 30, 56],
        [4, 4, 4, 4, 6, 6, 10, 12, 14, 16, 20, 26, 66],
        [4, 4, 4, 4, 6, 8, 12, 16, 20, 26, 34, 42, 12],
        [4, 4, 4, 6, 6, 8, 10, 14, 18, 26, 32, 42, 18],
        [4, 4, 4, 6, 8, 10, 12, 14, 18, 24, 32, 44, 12],
        [4, 4, 4, 6, 8, 10, 12, 14, 18, 24, 30, 40, 18],
        [4, 4, 4, 6, 8, 10, 12, 14, 18, 24, 30, 40, 18],
        [4, 4, 4, 6, 8, 10, 12, 14, 18, 24, 30, 40, 18],
        [8, 8, 8, 12, 16, 20, 24, 28, 36, 2, 2, 2, 26]
    ];

    public static readonly int[][] LongBandStarts = Starts(LongBandWidths);

    public static readonly int[][] ShortBandStarts = Starts(ShortBandWidths);

    public static readonly byte[] Pretab = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0];

    public static readonly byte[] Slen1 = [0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4];

    public static readonly byte[] Slen2 = [0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3];

    public static readonly byte[,,] LsfBandCounts =
    {
        { { 6, 5, 5, 5 }, { 9, 9, 9, 9 }, { 6, 9, 9, 9 } },
        { { 6, 5, 7, 3 }, { 9, 9, 12, 6 }, { 6, 9, 12, 6 } },
        { { 11, 10, 0, 0 }, { 18, 18, 0, 0 }, { 15, 18, 0, 0 } },
        { { 7, 7, 7, 0 }, { 12, 12, 12, 0 }, { 6, 15, 12, 0 } },
        { { 6, 6, 6, 3 }, { 12, 9, 9, 6 }, { 6, 12, 9, 6 } },
        { { 8, 8, 5, 0 }, { 15, 12, 9, 0 }, { 6, 18, 9, 0 } }
    };

    public const int GainOffset = 400;

    public static readonly float[] Power43 = BuildPower43();

    public static readonly float[] QuarterPowers = BuildQuarterPowers();

    public static readonly float[] AliasCs = Array.ConvertAll(Mp3Tables.AliasCs, value => (float)value);

    public static readonly float[] AliasCa = Array.ConvertAll(Mp3Tables.AliasCa, value => (float)value);

    public static readonly float[] LongImdctColumns = BuildLongImdctColumns();

    public static readonly float[] ShortImdctColumns = BuildImdctColumns(12, 6, 24, 6);

    public static readonly float[][] LongWindows = BuildLongWindows();

    public static readonly float[] ShortWindow = BuildShortWindow();

    public static readonly float[] SynthesisColumns = BuildSynthesisColumns();

    public static readonly float[] SynthesisWindow = Array.ConvertAll(Mp3Tables.WindowCoefficients, value => (float)value);

    public static readonly (float Left, float Right)[] Mpeg1Intensity = BuildMpeg1Intensity();

    public static readonly (float Left, float Right)[][] LsfIntensity = [BuildLsfIntensity(Math.Pow(2, -0.25)), BuildLsfIntensity(Math.Pow(2, -0.5))];

    private static int[][] Starts(int[][] widths)
    {
        var result = new int[widths.Length][];
        for (var rate = 0; rate < widths.Length; rate++)
        {
            result[rate] = new int[widths[rate].Length + 1];
            for (var band = 0; band < widths[rate].Length; band++)
            {
                result[rate][band + 1] = result[rate][band] + widths[rate][band];
            }
        }

        return result;
    }

    private static float[] BuildPower43()
    {
        var table = new float[8207];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        }

        return table;
    }

    private static float[] BuildQuarterPowers()
    {
        var table = new float[GainOffset + 64];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(2, (i - GainOffset) / 4.0);
        }

        return table;
    }

    private static float[] BuildLongImdctColumns()
    {
        var columns = new float[18 * 20];
        for (var k = 0; k < 18; k++)
        {
            for (var i = 0; i < 9; i++)
            {
                columns[(k * 20) + i] = (float)Math.Cos(Math.PI / 72 * ((2 * i) + 19) * ((2 * k) + 1));
                columns[(k * 20) + 10 + i] = (float)Math.Cos(Math.PI / 72 * ((2 * (i + 18)) + 19) * ((2 * k) + 1));
            }
        }

        return columns;
    }

    private static float[] BuildImdctColumns(int outputs, int inputs, int denominator, int shift)
    {
        var columns = new float[outputs * inputs];
        for (var k = 0; k < inputs; k++)
        {
            for (var i = 0; i < outputs; i++)
            {
                columns[(k * outputs) + i] = (float)Math.Cos(Math.PI / denominator * ((2 * i) + 1 + shift) * ((2 * k) + 1));
            }
        }

        return columns;
    }

    private static float[][] BuildLongWindows()
    {
        var windows = new float[4][];
        for (var type = 0; type < 4; type++)
        {
            windows[type] = new float[36];
        }

        for (var i = 0; i < 36; i++)
        {
            windows[0][i] = (float)Math.Sin(Math.PI / 36 * (i + 0.5));
        }

        for (var i = 0; i < 18; i++)
        {
            windows[1][i] = windows[0][i];
            windows[3][i + 18] = windows[0][i + 18];
        }

        for (var i = 18; i < 24; i++)
        {
            windows[1][i] = 1;
        }

        for (var i = 24; i < 30; i++)
        {
            windows[1][i] = (float)Math.Sin(Math.PI / 12 * (i - 18 + 0.5));
        }

        for (var i = 6; i < 12; i++)
        {
            windows[3][i] = (float)Math.Sin(Math.PI / 12 * (i - 6 + 0.5));
        }

        for (var i = 12; i < 18; i++)
        {
            windows[3][i] = 1;
        }

        return windows;
    }

    private static float[] BuildShortWindow()
    {
        var window = new float[12];
        for (var i = 0; i < 12; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 12 * (i + 0.5));
        }

        return window;
    }

    private static float[] BuildSynthesisColumns()
    {
        var columns = new float[32 * 32];
        for (var k = 0; k < 32; k++)
        {
            for (var i = 0; i < 32; i++)
            {
                columns[(k * 32) + i] = (float)Math.Cos(i * ((2 * k) + 1) * Math.PI / 64);
            }
        }

        return columns;
    }

    private static (float Left, float Right)[] BuildMpeg1Intensity()
    {
        var table = new (float, float)[7];
        for (var position = 0; position < 6; position++)
        {
            var ratio = Math.Tan(position * Math.PI / 12);
            table[position] = ((float)(ratio / (1 + ratio)), (float)(1 / (1 + ratio)));
        }

        table[6] = (1, 0);
        return table;
    }

    private static (float Left, float Right)[] BuildLsfIntensity(double io)
    {
        var table = new (float, float)[64];
        for (var position = 0; position < table.Length; position++)
        {
            table[position] = position == 0
                ? (1, 1)
                : position % 2 == 1
                    ? ((float)Math.Pow(io, (position + 1) / 2), 1)
                    : (1, (float)Math.Pow(io, position / 2));
        }

        return table;
    }
}
