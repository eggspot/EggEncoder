using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mpeg2
{
    internal sealed class Mpeg2SliceDecoder
    {
        private const int FieldMotion = 1;
        private const int FrameMotion = 2;
        private const int DualPrime = 3;

        private readonly Mpeg2Sequence _sequence;
        private readonly Mpeg2PictureHeader _header;
        private readonly Mpeg2Picture _picture;
        private readonly Mpeg2Picture? _forward;
        private readonly Mpeg2Picture? _backward;
        private readonly int[,,] _pmv = new int[2, 2, 2];
        private readonly int[,,] _vectors = new int[2, 2, 2];
        private readonly int[,] _fieldSelect = new int[2, 2];
        private readonly int[] _dcPredictor = new int[3];
        private readonly int[] _coefficients = new int[64];
        private readonly int[] _samples = new int[64];
        private readonly byte[][] _prediction = [new byte[256], new byte[64], new byte[64]];
        private readonly byte[][] _secondPrediction = [new byte[256], new byte[64], new byte[64]];
        private BitReader _reader = null!;
        private int _quantiserScaleCode;
        private int _previousFlags;
        private int _previousMotionType = FrameMotion;

        public Mpeg2SliceDecoder(Mpeg2Sequence sequence, Mpeg2PictureHeader header, Mpeg2Picture picture, Mpeg2Picture? forward, Mpeg2Picture? backward)
        {
            _sequence = sequence;
            _header = header;
            _picture = picture;
            _forward = forward;
            _backward = backward;
        }

        public void Decode(BitReader reader, int startCode)
        {
            _reader = reader;
            var row = startCode - 1;
            if (_sequence.Height > 2800)
            {
                row += (int)reader.ReadBits(3) << 7;
            }

            if (row >= _sequence.MacroblockHeight)
            {
                throw new InvalidDataException("MPEG-2: a slice starts below the picture");
            }

            _quantiserScaleCode = (int)reader.ReadBits(5);
            if (_quantiserScaleCode == 0)
            {
                throw new InvalidDataException("MPEG-2: quantiser_scale_code 0");
            }

            if (reader.ReadBits(1) == 1)
            {
                reader.SkipBits(1 + 1 + 7);
                while (reader.ReadBits(1) == 1)
                {
                    reader.SkipBits(8);
                }
            }

            ResetDcPredictors();
            Array.Clear(_pmv);
            var width = _sequence.MacroblockWidth;
            var count = width * _sequence.MacroblockHeight;
            var address = (row * width) - 1;
            var first = true;
            do
            {
                var increment = 0;
                while (true)
                {
                    var code = Mpeg2Tables.AddressIncrement.Read(reader);
                    if (code == Mpeg2Tables.MacroblockEscape)
                    {
                        increment += 33;
                        continue;
                    }

                    increment += code;
                    break;
                }

                if (!first)
                {
                    for (var skipped = address + 1; skipped < address + increment; skipped++)
                    {
                        if (skipped >= count)
                        {
                            throw new InvalidDataException("MPEG-2: skipped macroblocks run past the picture");
                        }

                        SkipMacroblock(skipped);
                    }
                }

                address += increment;
                if (address >= count)
                {
                    throw new InvalidDataException("MPEG-2: a macroblock address is past the picture");
                }

                DecodeMacroblock(address);
                first = false;
            }
            while (reader.RemainingBits >= 23 && reader.PeekBits(23) != 0);
        }

        private void ResetDcPredictors()
        {
            var reset = 1 << (_header.IntraDcPrecision + 7);
            _dcPredictor[0] = reset;
            _dcPredictor[1] = reset;
            _dcPredictor[2] = reset;
        }

        private void SkipMacroblock(int address)
        {
            ResetDcPredictors();
            if (_header.Type == Mpeg2PictureType.Predicted)
            {
                Array.Clear(_pmv);
                Array.Clear(_vectors);
                Predict(address, Mpeg2Tables.MotionForward, FrameMotion);
                WritePrediction(address);
                _previousFlags = Mpeg2Tables.MotionForward;
                _previousMotionType = FrameMotion;
                return;
            }

            if ((_previousFlags & Mpeg2Tables.Intra) != 0)
            {
                throw new InvalidDataException("MPEG-2: a skipped macroblock follows an intra macroblock in a B picture");
            }

            Predict(address, _previousFlags, _previousMotionType);
            WritePrediction(address);
        }

        private void DecodeMacroblock(int address)
        {
            var reader = _reader;
            var flags = (_header.Type switch
            {
                Mpeg2PictureType.Intra => Mpeg2Tables.MacroblockTypeI,
                Mpeg2PictureType.Predicted => Mpeg2Tables.MacroblockTypeP,
                _ => Mpeg2Tables.MacroblockTypeB
            }).Read(reader);
            var intra = (flags & Mpeg2Tables.Intra) != 0;
            var motionType = FrameMotion;
            if ((flags & (Mpeg2Tables.MotionForward | Mpeg2Tables.MotionBackward)) != 0 && !_header.FramePredFrameDct)
            {
                motionType = (int)reader.ReadBits(2);
                if (motionType == DualPrime)
                {
                    throw new NotSupportedException("MPEG-2: dual-prime prediction is not supported");
                }

                if (motionType == 0)
                {
                    throw new InvalidDataException("MPEG-2: reserved frame_motion_type");
                }
            }

            var fieldDct = !_header.FramePredFrameDct && (intra || (flags & Mpeg2Tables.Pattern) != 0) && reader.ReadBits(1) == 1;
            if ((flags & Mpeg2Tables.Quant) != 0)
            {
                _quantiserScaleCode = (int)reader.ReadBits(5);
                if (_quantiserScaleCode == 0)
                {
                    throw new InvalidDataException("MPEG-2: quantiser_scale_code 0");
                }
            }

            var concealment = intra && _header.ConcealmentMotionVectors;
            if ((flags & Mpeg2Tables.MotionForward) != 0 || concealment)
            {
                ReadMotionVectors(0, concealment ? FrameMotion : motionType);
            }

            if ((flags & Mpeg2Tables.MotionBackward) != 0)
            {
                ReadMotionVectors(1, motionType);
            }

            if (concealment)
            {
                reader.SkipBits(1);
            }

            var pattern = intra ? 63 : (flags & Mpeg2Tables.Pattern) != 0 ? Mpeg2Tables.CodedBlockPattern.Read(reader) : 0;
            if (intra)
            {
                if (!concealment)
                {
                    Array.Clear(_pmv);
                }

                for (var block = 0; block < 6; block++)
                {
                    DecodeBlock(block, intra: true);
                    Place(address, block, fieldDct, add: false);
                }

                _previousFlags = flags;
                return;
            }

            ResetDcPredictors();
            if (_header.Type == Mpeg2PictureType.Predicted && (flags & Mpeg2Tables.MotionForward) == 0)
            {
                Array.Clear(_pmv);
                Array.Clear(_vectors);
                flags |= Mpeg2Tables.MotionForward;
                motionType = FrameMotion;
            }

            Predict(address, flags, motionType);
            WritePrediction(address);
            for (var block = 0; block < 6; block++)
            {
                if ((pattern & (32 >> block)) == 0)
                {
                    continue;
                }

                DecodeBlock(block, intra: false);
                Place(address, block, fieldDct, add: true);
            }

            _previousFlags = flags & ~Mpeg2Tables.Quant;
            _previousMotionType = motionType;
        }

        private void ReadMotionVectors(int s, int motionType)
        {
            if (motionType == FrameMotion)
            {
                ReadVector(0, s, field: false);
                for (var t = 0; t < 2; t++)
                {
                    _pmv[1, s, t] = _pmv[0, s, t];
                    _vectors[1, s, t] = _vectors[0, s, t];
                }

                return;
            }

            for (var r = 0; r < 2; r++)
            {
                _fieldSelect[r, s] = (int)_reader.ReadBits(1);
                ReadVector(r, s, field: true);
            }
        }

        private void ReadVector(int r, int s, bool field)
        {
            for (var t = 0; t < 2; t++)
            {
                var fCode = _header.FCode[s, t];
                var rSize = fCode - 1;
                var code = Mpeg2Tables.MotionCode.Read(_reader);
                var residual = rSize > 0 && code != 0 ? (int)_reader.ReadBits(rSize) : 0;
                var f = 1 << rSize;
                var delta = rSize == 0 || code == 0 ? code : ((Math.Abs(code) - 1) * f) + residual + 1;
                if (code < 0 && rSize > 0)
                {
                    delta = -delta;
                }

                var halved = field && t == 1;
                var prediction = halved ? _pmv[r, s, t] >> 1 : _pmv[r, s, t];
                var vector = prediction + delta;
                var low = -16 * f;
                var high = (16 * f) - 1;
                var range = 32 * f;
                if (vector < low)
                {
                    vector += range;
                }
                else if (vector > high)
                {
                    vector -= range;
                }

                _vectors[r, s, t] = vector;
                _pmv[r, s, t] = halved ? vector * 2 : vector;
            }
        }

        private void DecodeBlock(int block, bool intra)
        {
            var reader = _reader;
            Array.Clear(_coefficients);
            var scan = _header.AlternateScan ? Mpeg2Tables.AlternateScan : Mpeg2Tables.ZigZag;
            var n = 0;
            if (intra)
            {
                var component = block < 4 ? 0 : block - 3;
                var size = (component == 0 ? Mpeg2Tables.DcSizeLuminance : Mpeg2Tables.DcSizeChrominance).Read(reader);
                var differential = 0;
                if (size > 0)
                {
                    var bits = (int)reader.ReadBits(size);
                    differential = bits < (1 << (size - 1)) ? bits - (1 << size) + 1 : bits;
                }

                _dcPredictor[component] += differential;
                _coefficients[0] = _dcPredictor[component] * (8 >> _header.IntraDcPrecision);
                n = 1;
            }

            var table = intra && _header.IntraVlcFormat ? Mpeg2Tables.CoefficientsOne : Mpeg2Tables.CoefficientsZero;
            var quantiserScale = _header.NonLinearQuantiser ? Mpeg2Tables.NonLinearQuantiserScale[_quantiserScaleCode] : _quantiserScaleCode * 2;
            var matrix = intra ? _sequence.IntraMatrix : _sequence.NonIntraMatrix;
            var first = !intra;
            while (true)
            {
                int run;
                int level;
                if (first && reader.PeekBits(1) == 1)
                {
                    reader.SkipBits(1);
                    run = 0;
                    level = reader.ReadBits(1) == 1 ? -1 : 1;
                }
                else
                {
                    var code = table.Read(reader);
                    if (code == Mpeg2Tables.EndOfBlock)
                    {
                        break;
                    }

                    if (code == Mpeg2Tables.Escape)
                    {
                        run = (int)reader.ReadBits(6);
                        level = reader.ReadSignedBits(12);
                        if (level is 0 or -2048)
                        {
                            throw new InvalidDataException("MPEG-2: an escaped coefficient level is forbidden");
                        }
                    }
                    else
                    {
                        run = code >> 8;
                        level = code & 0xFF;
                        if (reader.ReadBits(1) == 1)
                        {
                            level = -level;
                        }
                    }
                }

                first = false;
                n += run;
                if (n > 63)
                {
                    throw new InvalidDataException("MPEG-2: a block holds more than 64 coefficients");
                }

                var position = scan[n];
                var k = intra ? 0 : Math.Sign(level);
                var value = (((2 * level) + k) * matrix[position] * quantiserScale) / 32;
                _coefficients[position] = Math.Clamp(value, -2048, 2047);
                n++;
            }

            if (intra)
            {
                _coefficients[0] = Math.Clamp(_coefficients[0], -2048, 2047);
            }

            var sum = 0;
            for (var i = 0; i < 64; i++)
            {
                sum += _coefficients[i];
            }

            if ((sum & 1) == 0)
            {
                _coefficients[63] += (_coefficients[63] & 1) != 0 ? -1 : 1;
            }

            Mpeg2Idct.Inverse(_coefficients, _samples);
        }

        private void Place(int address, int block, bool fieldDct, bool add)
        {
            var width = _sequence.MacroblockWidth;
            var mbX = address % width;
            var mbY = address / width;
            byte[] plane;
            int stride;
            int origin;
            int rowStep;
            if (block < 4)
            {
                plane = _picture.Planes[0];
                stride = _picture.LumaWidth;
                var x = (mbX * 16) + ((block & 1) * 8);
                if (fieldDct)
                {
                    origin = ((mbY * 16) + (block >> 1)) * stride + x;
                    rowStep = 2 * stride;
                }
                else
                {
                    origin = ((mbY * 16) + ((block >> 1) * 8)) * stride + x;
                    rowStep = stride;
                }
            }
            else
            {
                plane = _picture.Planes[block - 3];
                stride = _picture.ChromaWidth;
                origin = (mbY * 8 * stride) + (mbX * 8);
                rowStep = stride;
            }

            for (var y = 0; y < 8; y++)
            {
                var row = origin + (y * rowStep);
                for (var x = 0; x < 8; x++)
                {
                    var sample = _samples[(y * 8) + x] + (add ? plane[row + x] : 0);
                    plane[row + x] = (byte)Math.Clamp(sample, 0, 255);
                }
            }
        }

        private void Predict(int address, int flags, int motionType)
        {
            var forward = (flags & Mpeg2Tables.MotionForward) != 0;
            var backward = (flags & Mpeg2Tables.MotionBackward) != 0;
            if (forward)
            {
                PredictFrom(_forward ?? throw new InvalidDataException("MPEG-2: forward prediction without a reference"), 0, address, motionType, _prediction);
            }

            if (backward)
            {
                var target = forward ? _secondPrediction : _prediction;
                PredictFrom(_backward ?? throw new InvalidDataException("MPEG-2: backward prediction without a reference"), 1, address, motionType, target);
                if (forward)
                {
                    for (var plane = 0; plane < 3; plane++)
                    {
                        var a = _prediction[plane];
                        var b = _secondPrediction[plane];
                        for (var i = 0; i < a.Length; i++)
                        {
                            a[i] = (byte)((a[i] + b[i] + 1) >> 1);
                        }
                    }
                }
            }
        }

        private void PredictFrom(Mpeg2Picture reference, int s, int address, int motionType, byte[][] target)
        {
            var width = _sequence.MacroblockWidth;
            var mbX = address % width;
            var mbY = address / width;
            if (motionType == FrameMotion)
            {
                var vx = _vectors[0, s, 0];
                var vy = _vectors[0, s, 1];
                PredictBlock(reference, 0, -1, (mbX * 16 * 2) + vx, (mbY * 16 * 2) + vy, 16, 16, target[0], 0, 16);
                var cx = vx / 2;
                var cy = vy / 2;
                for (var plane = 1; plane < 3; plane++)
                {
                    PredictBlock(reference, plane, -1, (mbX * 8 * 2) + cx, (mbY * 8 * 2) + cy, 8, 8, target[plane], 0, 8);
                }

                return;
            }

            for (var r = 0; r < 2; r++)
            {
                var vx = _vectors[r, s, 0];
                var vy = _vectors[r, s, 1];
                var parity = _fieldSelect[r, s];
                PredictBlock(reference, 0, parity, (mbX * 16 * 2) + vx, (mbY * 8 * 2) + vy, 16, 8, target[0], r * 16, 32);
                var cx = vx / 2;
                var cy = vy / 2;
                for (var plane = 1; plane < 3; plane++)
                {
                    PredictBlock(reference, plane, parity, (mbX * 8 * 2) + cx, (mbY * 4 * 2) + cy, 8, 4, target[plane], r * 8, 16);
                }
            }
        }

        private static void PredictBlock(Mpeg2Picture reference, int planeIndex, int parity, int halfX, int halfY, int width, int height, byte[] target, int targetOffset, int targetStride)
        {
            var plane = reference.Planes[planeIndex];
            var planeWidth = reference.Width(planeIndex);
            var planeHeight = reference.Height(planeIndex);
            var lines = parity < 0 ? planeHeight : planeHeight / 2;
            var x0 = halfX >> 1;
            var y0 = halfY >> 1;
            var halfHorizontal = (halfX & 1) != 0;
            var halfVertical = (halfY & 1) != 0;
            for (var y = 0; y < height; y++)
            {
                var row0 = RowOffset(y0 + y);
                var row1 = halfVertical ? RowOffset(y0 + y + 1) : row0;
                for (var x = 0; x < width; x++)
                {
                    var c0 = Math.Clamp(x0 + x, 0, planeWidth - 1);
                    var c1 = halfHorizontal ? Math.Clamp(x0 + x + 1, 0, planeWidth - 1) : c0;
                    int value = (halfHorizontal, halfVertical) switch
                    {
                        (false, false) => plane[row0 + c0],
                        (true, false) => (plane[row0 + c0] + plane[row0 + c1] + 1) >> 1,
                        (false, true) => (plane[row0 + c0] + plane[row1 + c0] + 1) >> 1,
                        _ => (plane[row0 + c0] + plane[row0 + c1] + plane[row1 + c0] + plane[row1 + c1] + 2) >> 2
                    };
                    target[targetOffset + (y * targetStride) + x] = (byte)value;
                }
            }

            int RowOffset(int line)
            {
                var clamped = Math.Clamp(line, 0, lines - 1);
                var frameRow = parity < 0 ? clamped : (clamped * 2) + parity;
                return frameRow * planeWidth;
            }

        }

        private void WritePrediction(int address)
        {
            var width = _sequence.MacroblockWidth;
            var mbX = address % width;
            var mbY = address / width;
            for (var plane = 0; plane < 3; plane++)
            {
                var size = plane == 0 ? 16 : 8;
                var stride = _picture.Width(plane);
                var destination = _picture.Planes[plane];
                var source = _prediction[plane];
                for (var y = 0; y < size; y++)
                {
                    source.AsSpan(y * size, size).CopyTo(destination.AsSpan(((mbY * size) + y) * stride + (mbX * size)));
                }
            }
        }
    }
}
