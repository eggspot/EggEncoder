using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.Codecs.Mpeg2
{
    internal enum Mpeg2PictureType
    {
        Intra = 1,
        Predicted = 2,
        Bidirectional = 3
    }

    internal sealed class Mpeg2Sequence
    {
        private const int MaxDimension = 16384;

        private static readonly (int Numerator, int Denominator)[] FrameRates =
        [
            (0, 1), (24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1)
        ];

        private int _horizontalSize;
        private int _verticalSize;
        private int _frameRateCode;
        private int? _matrixCoefficients;

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool HasExtension { get; private set; }

        public bool ProgressiveSequence { get; private set; } = true;

        public VideoFrameRate? FrameRate { get; private set; }

        public byte[] IntraMatrix { get; private set; } = (byte[])Mpeg2Tables.DefaultIntraMatrix.Clone();

        public byte[] NonIntraMatrix { get; private set; } = Enumerable.Repeat((byte)16, 64).ToArray();

        public int MacroblockWidth => (Width + 15) / 16;

        public int MacroblockHeight => ProgressiveSequence ? (Height + 15) / 16 : 2 * ((Height + 31) / 32);

        public VideoColorMatrix ColorMatrix => _matrixCoefficients switch
        {
            1 => VideoColorMatrix.Bt709,
            5 or 6 => VideoColorMatrix.Bt601,
            _ => Height >= 720 ? VideoColorMatrix.Bt709 : VideoColorMatrix.Bt601
        };

        public static Mpeg2Sequence ParseHeader(BitReader reader)
        {
            var sequence = new Mpeg2Sequence
            {
                _horizontalSize = (int)reader.ReadBits(12),
                _verticalSize = (int)reader.ReadBits(12)
            };
            reader.SkipBits(4);
            sequence._frameRateCode = (int)reader.ReadBits(4);
            reader.SkipBits(18 + 1 + 10 + 1);
            if (reader.ReadBits(1) == 1)
            {
                sequence.IntraMatrix = ReadMatrix(reader);
            }

            if (reader.ReadBits(1) == 1)
            {
                sequence.NonIntraMatrix = ReadMatrix(reader);
            }

            sequence.UpdateSize(0, 0);
            sequence.UpdateFrameRate(0, 0);
            return sequence;
        }

        public void ParseSequenceExtension(BitReader reader)
        {
            reader.SkipBits(8);
            ProgressiveSequence = reader.ReadBits(1) == 1;
            var chromaFormat = (int)reader.ReadBits(2);
            if (chromaFormat != 1)
            {
                throw new NotSupportedException("MPEG-2: only 4:2:0 video is supported");
            }

            var horizontalExtension = (int)reader.ReadBits(2);
            var verticalExtension = (int)reader.ReadBits(2);
            reader.SkipBits(12 + 1 + 8 + 1);
            var rateNumerator = (int)reader.ReadBits(2);
            var rateDenominator = (int)reader.ReadBits(5);
            UpdateSize(horizontalExtension, verticalExtension);
            UpdateFrameRate(rateNumerator, rateDenominator);
            HasExtension = true;
        }

        public void ParseDisplayExtension(BitReader reader)
        {
            reader.SkipBits(3);
            if (reader.ReadBits(1) == 1)
            {
                reader.SkipBits(16);
                _matrixCoefficients = (int)reader.ReadBits(8);
            }
        }

        public void ParseQuantMatrixExtension(BitReader reader)
        {
            if (reader.ReadBits(1) == 1)
            {
                IntraMatrix = ReadMatrix(reader);
            }

            if (reader.ReadBits(1) == 1)
            {
                NonIntraMatrix = ReadMatrix(reader);
            }
        }

        private static byte[] ReadMatrix(BitReader reader)
        {
            var matrix = new byte[64];
            for (var i = 0; i < 64; i++)
            {
                var value = (byte)reader.ReadBits(8);
                if (value == 0)
                {
                    throw new InvalidDataException("MPEG-2: a quantiser matrix holds a zero weight");
                }

                matrix[Mpeg2Tables.ZigZag[i]] = value;
            }

            return matrix;
        }

        private void UpdateSize(int horizontalExtension, int verticalExtension)
        {
            Width = (horizontalExtension << 12) | _horizontalSize;
            Height = (verticalExtension << 12) | _verticalSize;
            if (Width is <= 0 or > MaxDimension || Height is <= 0 or > MaxDimension)
            {
                throw new InvalidDataException($"MPEG-2: invalid picture size {Width}x{Height}");
            }
        }

        private void UpdateFrameRate(int numeratorExtension, int denominatorExtension)
        {
            if (_frameRateCode is <= 0 or >= 9)
            {
                FrameRate = null;
                return;
            }

            var (numerator, denominator) = FrameRates[_frameRateCode];
            numerator *= numeratorExtension + 1;
            denominator *= denominatorExtension + 1;
            var common = (int)System.Numerics.BigInteger.GreatestCommonDivisor(numerator, denominator);
            FrameRate = new VideoFrameRate(numerator / common, denominator / common);
        }
    }

    internal sealed class Mpeg2PictureHeader
    {
        public Mpeg2PictureType Type { get; private set; }

        public int[,] FCode { get; } = new int[2, 2];

        public int IntraDcPrecision { get; private set; }

        public int PictureStructure { get; private set; } = 3;

        public bool FramePredFrameDct { get; private set; } = true;

        public bool ConcealmentMotionVectors { get; private set; }

        public bool NonLinearQuantiser { get; private set; }

        public bool IntraVlcFormat { get; private set; }

        public bool AlternateScan { get; private set; }

        public bool HasCodingExtension { get; private set; }

        public static Mpeg2PictureHeader Parse(BitReader reader)
        {
            reader.SkipBits(10);
            var type = (int)reader.ReadBits(3);
            if (type is < 1 or > 3)
            {
                throw type == 4
                    ? new NotSupportedException("MPEG-2: D pictures are not supported")
                    : new InvalidDataException($"MPEG-2: invalid picture coding type {type}");
            }

            reader.SkipBits(16);
            return new Mpeg2PictureHeader { Type = (Mpeg2PictureType)type };
        }

        public void ParseCodingExtension(BitReader reader)
        {
            for (var s = 0; s < 2; s++)
            {
                for (var t = 0; t < 2; t++)
                {
                    FCode[s, t] = (int)reader.ReadBits(4);
                }
            }

            IntraDcPrecision = (int)reader.ReadBits(2);
            PictureStructure = (int)reader.ReadBits(2);
            reader.SkipBits(1);
            FramePredFrameDct = reader.ReadBits(1) == 1;
            ConcealmentMotionVectors = reader.ReadBits(1) == 1;
            NonLinearQuantiser = reader.ReadBits(1) == 1;
            IntraVlcFormat = reader.ReadBits(1) == 1;
            AlternateScan = reader.ReadBits(1) == 1;
            if (PictureStructure == 0)
            {
                throw new InvalidDataException("MPEG-2: picture_structure 0 is reserved");
            }

            for (var s = 0; s < 2; s++)
            {
                var used = s == 0 ? Type != Mpeg2PictureType.Intra : Type == Mpeg2PictureType.Bidirectional;
                for (var t = 0; t < 2; t++)
                {
                    if (used && FCode[s, t] is < 1 or > 9)
                    {
                        throw new InvalidDataException("MPEG-2: an invalid motion vector f_code");
                    }
                }
            }

            HasCodingExtension = true;
        }
    }
}
