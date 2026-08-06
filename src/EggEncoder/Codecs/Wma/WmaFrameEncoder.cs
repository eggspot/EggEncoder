using EggEncoder.Transform;

namespace EggEncoder.Codecs.Wma
{
    // Encodes one WMAv2 frame (per channel: windowed MDCT -> per-band exponents -> quantized,
    // Huffman/run-length-coded coefficients) into a packet payload matching exactly what
    // WmaDecoder.Decode expects: fixed block length, VLC-coded (not LSP) exponents, independently
    // coded channels (no mid/side stereo), no bit reservoir. Those are the only bitstream shapes
    // WmaDecoder supports, so this encoder never produces anything outside that subset -- every
    // frame it writes is decodable by WmaDecoder (verified by round-trip tests) and, since that
    // subset is a standards-compliant one signaled by explicit flags in the stream, by any
    // compliant WMA decoder.
    internal sealed class WmaFrameEncoder
    {
        private const int TargetQuantizedPeak = 20;
        private const int MaxExponentDeltaPerBand = 60;
        private const int InitialExponent = 36;

        private readonly int _channels;
        private readonly int _frameLengthBits;
        private readonly int _coefsEnd;
        private readonly ushort[] _exponentBands;
        private readonly double[] _window;

        private readonly double[][] _previousBlock;

        public WmaFrameEncoder(int channels, int sampleRate)
        {
            _channels = channels;
            _frameLengthBits = WmaTables.GetFrameLengthBits(sampleRate);
            FrameLength = 1 << _frameLengthBits;
            _coefsEnd = FrameLength - (FrameLength * 9 / 100);
            _exponentBands = WmaTables.BuildExponentBands(sampleRate, FrameLength);
            _window = WmaTables.BuildSineWindow(FrameLength);

            _previousBlock = new double[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                _previousBlock[channel] = new double[FrameLength];
            }
        }

        public int FrameLength { get; }

        // currentBlock[channel] must have exactly FrameLength samples (PCM, not yet windowed).
        public byte[] EncodeFrame(double[][] currentBlock)
        {
            var writer = new BitWriter();

            var coefficients = new double[_channels][];
            for (var channel = 0; channel < _channels; channel++)
            {
                coefficients[channel] = ForwardTransform(_previousBlock[channel], currentBlock[channel]);
            }

            // Phase 1: choose per-band exponents and each channel's desired gain, without
            // quantizing yet -- totalGain is a single per-frame value shared by every channel in
            // the bitstream, so it has to be finalized before any channel's coefficients are
            // quantized against it.
            var bandExponentIndices = new int[_channels][];
            var maxExponentValue = new double[_channels];
            var desiredTotalGain = 1;

            for (var channel = 0; channel < _channels; channel++)
            {
                (bandExponentIndices[channel], maxExponentValue[channel], var peakCoefficientMagnitude) = ChooseExponents(coefficients[channel]);
                desiredTotalGain = Math.Max(desiredTotalGain, SolveTotalGain(peakCoefficientMagnitude));
            }

            var coefficientBitWidth = WmaTables.TotalGainToBits(desiredTotalGain);

            WriteGain(writer, desiredTotalGain);

            if (_channels == 2)
            {
                writer.WriteBits(0, 1); // mid/side stereo: never used, matches WmaDecoder's only supported mode
            }

            for (var channel = 0; channel < _channels; channel++)
            {
                writer.WriteBits(1, 1); // channel always coded
            }

            // Phase 2: quantize every channel against the shared totalGain and write.
            for (var channel = 0; channel < _channels; channel++)
            {
                var quantized = QuantizeChannel(coefficients[channel], bandExponentIndices[channel], maxExponentValue[channel], desiredTotalGain, coefficientBitWidth);

                WriteExponents(writer, bandExponentIndices[channel]);
                WriteCoefficients(writer, quantized, coefficientBitWidth);
            }

            for (var channel = 0; channel < _channels; channel++)
            {
                _previousBlock[channel] = currentBlock[channel];
            }

            return writer.ToArray();
        }

        private double[] ForwardTransform(double[] previousBlock, double[] currentBlock)
        {
            var windowedInput = new double[FrameLength * 2];

            for (var n = 0; n < FrameLength; n++)
            {
                windowedInput[n] = previousBlock[n] * _window[n];
            }

            for (var n = 0; n < FrameLength; n++)
            {
                windowedInput[FrameLength + n] = currentBlock[n] * _window[FrameLength - 1 - n];
            }

            return Mdct.Forward(windowedInput);
        }

        // Chooses one exponent index (a PowTable entry) per critical band, tracking that band's own
        // peak coefficient magnitude -- directly analogous to AAC's per-band scale factor. Delta
        // between consecutive bands is clamped to what the shared scalefactor Huffman table can
        // encode, exactly mirroring AacEncoder's scalefactor delta clamp.
        //
        // PeakCoefficientMagnitude is the TRUE (unclamped) largest |coefficient| in the channel.
        // Mdct.Forward has no 1/N normalization, so raw coefficient magnitudes for typical PCM
        // amplitudes routinely exceed PowTable's range (max ~866000) by an order of magnitude or
        // more -- MaxExponentValue is deliberately clamped to that range (it becomes a real
        // bitstream field, decode reconstructs the identical clamped value from the exponent codes
        // it reads), but SolveTotalGain needs the true peak: in the quantization formula the
        // clamped exponent value cancels out algebraically for the peak band, so totalGain must be
        // solved from the actual coefficient magnitude, not the clamped stand-in for it.
        private (int[] BandExponentIndices, double MaxExponentValue, double PeakCoefficientMagnitude) ChooseExponents(double[] coefficients)
        {
            var bandCount = _exponentBands.Length;
            var bandExponentIndices = new int[bandCount];
            var maxExponentValue = 0.0;
            var peakCoefficientMagnitude = 0.0;
            var position = 0;
            var lastExponentIndex = InitialExponent;

            for (var band = 0; band < bandCount; band++)
            {
                var bandLength = _exponentBands[band];
                var bandEnd = Math.Min(position + bandLength, _coefsEnd);

                var bandMax = 0.0;
                for (var i = position; i < bandEnd; i++)
                {
                    bandMax = Math.Max(bandMax, Math.Abs(coefficients[i]));
                }

                peakCoefficientMagnitude = Math.Max(peakCoefficientMagnitude, bandMax);

                var desiredExponentIndex = MagnitudeToExponentIndex(bandMax);
                var clampedDelta = Math.Clamp(desiredExponentIndex - lastExponentIndex, -MaxExponentDeltaPerBand, MaxExponentDeltaPerBand);
                lastExponentIndex += clampedDelta;
                bandExponentIndices[band] = lastExponentIndex;

                var exponentValue = WmaTables.PowTable[Math.Clamp(lastExponentIndex + 60, 0, WmaTables.PowTable.Length - 1)];
                maxExponentValue = Math.Max(maxExponentValue, exponentValue);

                position += bandLength;
            }

            if (maxExponentValue <= 0)
            {
                maxExponentValue = WmaTables.PowTable[60];
            }

            if (peakCoefficientMagnitude <= 0)
            {
                peakCoefficientMagnitude = maxExponentValue;
            }

            return (bandExponentIndices, maxExponentValue, peakCoefficientMagnitude);
        }

        // Solves for the totalGain that makes this channel's true peak coefficient quantize to
        // TargetQuantizedPeak -- mirrors WmaDecoder's mult = 10^(totalGain*0.05) / maxExponentValue
        // / (FrameLength/2). For the peak band, exponentValue == maxExponentValue, so it cancels
        // out of quantized = coefficient / (exponentValue * mult) algebraically -- meaning totalGain
        // must be solved from the peak band's true (unclamped) coefficient magnitude, not from
        // maxExponentValue itself (which is clamped to PowTable's representable range and can be
        // far smaller than the true peak for full-scale PCM input).
        private int SolveTotalGain(double peakCoefficientMagnitude)
        {
            var totalGain = (int)Math.Round(20 * Math.Log10(peakCoefficientMagnitude * (FrameLength / 2.0) / TargetQuantizedPeak)) + 1;

            return Math.Clamp(totalGain, 1, 4000);
        }

        private int[] QuantizeChannel(double[] coefficients, int[] bandExponentIndices, double maxExponentValue, int totalGain, int coefficientBitWidth)
        {
            var exponentValuePerPosition = new double[FrameLength];
            var position = 0;
            for (var band = 0; band < _exponentBands.Length; band++)
            {
                var bandLength = _exponentBands[band];
                var exponentValue = WmaTables.PowTable[Math.Clamp(bandExponentIndices[band] + 60, 0, WmaTables.PowTable.Length - 1)];

                for (var i = position; i < position + bandLength && i < FrameLength; i++)
                {
                    exponentValuePerPosition[i] = exponentValue;
                }

                position += bandLength;
            }

            var mult = Math.Pow(10, totalGain * 0.05) / maxExponentValue / (FrameLength / 2.0);
            var maxLevel = (1 << (coefficientBitWidth - 1)) - 1;

            var quantized = new int[FrameLength];
            for (var i = 0; i < _coefsEnd; i++)
            {
                if (exponentValuePerPosition[i] <= 0)
                {
                    continue;
                }

                var value = (int)Math.Round(coefficients[i] / (exponentValuePerPosition[i] * mult));
                quantized[i] = Math.Clamp(value, -maxLevel, maxLevel);
            }

            return quantized;
        }

        private static int MagnitudeToExponentIndex(double magnitude)
        {
            if (magnitude <= 0)
            {
                return -60;
            }

            var index = (int)Math.Round(16 * Math.Log10(magnitude));

            return Math.Clamp(index, -60, 95);
        }

        private static void WriteGain(BitWriter writer, int totalGain)
        {
            var remaining = totalGain - 1;
            while (remaining >= 127)
            {
                writer.WriteBits(127, 7);
                remaining -= 127;
            }

            writer.WriteBits((uint)remaining, 7);
        }

        private static void WriteExponents(BitWriter writer, int[] bandExponentIndices)
        {
            var lastExponent = InitialExponent;

            foreach (var exponentIndex in bandExponentIndices)
            {
                var code = exponentIndex - lastExponent + Aac.AacTables.ScaleDiffZero;
                lastExponent = exponentIndex;

                var (huffmanCode, length) = Aac.AacTables.ScalefactorHuffman.GetCode(code);
                writer.WriteBits(huffmanCode, length);
            }
        }

        // Decode's coefficient loop is `for (offset=0; offset<numberOfCoefficients; offset++)` --
        // it reads a stop code (symbol 1) ONLY if it's still short of numberOfCoefficients when it
        // goes to read the next symbol. If the last real (run, level) symbol's cumulative offset
        // lands exactly on numberOfCoefficients, decode's loop condition fails and it never reads
        // another symbol at all. So a stop code must be written ONLY when this scan runs out of
        // nonzero coefficients before reaching _coefsEnd; writing it unconditionally after the loop
        // (as an always-present trailer) leaves a stray, unconsumed symbol in the bitstream whenever
        // the last coefficient before _coefsEnd is itself nonzero -- desyncing everything the reader
        // decodes after this channel (the next channel's exponents, or the next frame).
        private void WriteCoefficients(BitWriter writer, int[] quantized, int coefficientBitWidth)
        {
            var position = 0;

            while (position < _coefsEnd)
            {
                var runStart = position;
                while (position < _coefsEnd && quantized[position] == 0)
                {
                    position++;
                }

                if (position >= _coefsEnd)
                {
                    var (stopCode, stopLength) = WmaTables.Coef4Huffman.GetCode(1);
                    writer.WriteBits(stopCode, stopLength);
                    return;
                }

                var run = position - runStart;
                var level = Math.Abs(quantized[position]);
                var isPositive = quantized[position] > 0;
                var index = WmaTables.FindCoefficientIndex(run, level);

                if (index >= 0)
                {
                    var (huffmanCode, length) = WmaTables.Coef4Huffman.GetCode(index);
                    writer.WriteBits(huffmanCode, length);
                    writer.WriteBits(isPositive ? 1u : 0u, 1);
                }
                else
                {
                    var (escapeCode, escapeLength) = WmaTables.Coef4Huffman.GetCode(0);
                    writer.WriteBits(escapeCode, escapeLength);
                    writer.WriteBits((uint)Math.Min(level, (1 << coefficientBitWidth) - 1), coefficientBitWidth);
                    writer.WriteBits((uint)run, _frameLengthBits);
                    writer.WriteBits(isPositive ? 1u : 0u, 1);
                }

                position++;
            }

            // position landed exactly on _coefsEnd via a real (run, level)/escape symbol -- decode's
            // loop condition will fail on its own before reading another symbol, so no trailing stop
            // code belongs here.
        }

    }
}
