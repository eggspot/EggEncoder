using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    public static class AacEncoder
    {
        public static void Encode(string outputFilePath, IReadOnlyList<short> pcmSamples, int channels, int sampleRate)
        {
            var sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            using var outputStream = File.Create(outputFilePath);
            using var frameEncoder = new AacFrameEncoder(channels, sampleRate, (payload, _, _) =>
            {
                outputStream.Write(AdtsFrameWriter.Wrap(payload, sampleRateIndex, channels));
            });

            foreach (var sample in pcmSamples)
            {
                frameEncoder.WriteSample(sample);
            }

            frameEncoder.Flush();
        }
    }

    // Encodes AAC-LC raw access units (no ADTS framing of its own) one PCM sample at a time, emitting
    // a frame via the caller-supplied callback as soon as a full 1024-sample block (plus the
    // previous block, for the 50%-overlap MDCT window) is available. This lets callers stream PCM in
    // and get compressed output out incrementally, instead of buffering an entire track in memory
    // before any encoding happens -- the ADTS-to-file callback below is just one such caller;
    // Streaming.Aac.AacStreamingEncoder (docs/streaming-plan.md item 1) is another, emitting the raw
    // access unit directly with no ADTS framing at all.
    internal sealed class AacFrameEncoder : IDisposable
    {
        internal const int CoefficientCount = 1024;
        private const int BlockSize = CoefficientCount * 2;
        private const int MaxScalefactorBand = 49;
        private const int TargetMaxQuantizedMagnitude = 8;

        private const uint SingleChannelElementSyntaxId = 0;
        private const uint DefaultGlobalGain = 100;
        private const uint OnlyLongSequence = 0;
        private const uint SineWindowShape = 0;
        private const uint PredictorNotPresent = 0;
        private const uint PulseDataNotPresent = 0;
        private const uint TnsNotPresent = 0;
        private const uint GainControlNotPresent = 0;

        private static readonly ushort[] _swbOffsets = AacTables.ScalefactorBandOffsets1024_44100Or48000;

        private readonly Action<byte[], int, long> _onFrameEncoded;
        private readonly double[] _window;

        private double[] _previousBlock = new double[CoefficientCount];
        private double[] _currentBlock = new double[CoefficientCount];
        private int _currentBlockCount;
        private long _frameIndex;

        /// <param name="channels">Must be 1 -- see <see cref="ValidateAndGetSampleRateIndex"/>.</param>
        /// <param name="sampleRate">Must be 44100 or 48000 -- see <see cref="ValidateAndGetSampleRateIndex"/>.</param>
        /// <param name="onFrameEncoded">
        /// Invoked once per encoded frame, synchronously, with the raw access unit bytes (no ADTS
        /// framing), the real sample count this frame represents (always <see cref="CoefficientCount"/>
        /// except for a final, zero-padded frame emitted by <see cref="Flush"/>), and a 0-based,
        /// monotonically increasing frame index.
        /// </param>
        public AacFrameEncoder(int channels, int sampleRate, Action<byte[], int, long> onFrameEncoded)
        {
            ValidateAndGetSampleRateIndex(channels, sampleRate);
            _onFrameEncoded = onFrameEncoded;
            _window = BuildSineWindow(BlockSize);
        }

        public void WriteSample(short sample)
        {
            _currentBlock[_currentBlockCount++] = sample;

            if (_currentBlockCount < CoefficientCount)
            {
                return;
            }

            EncodeAndEmitFrame();

            (_previousBlock, _currentBlock) = (_currentBlock, _previousBlock);
            Array.Clear(_currentBlock);
            _currentBlockCount = 0;
        }

        public void Flush()
        {
            if (_currentBlockCount == 0)
            {
                return;
            }

            EncodeAndEmitFrame();
            _currentBlockCount = 0;
        }

        public void Dispose()
        {
            // The output stream's lifetime is owned by the caller.
        }

        internal static int ValidateAndGetSampleRateIndex(int channels, int sampleRate)
        {
            if (channels != 1)
            {
                throw new NotSupportedException("Only mono AAC encoding is supported");
            }

            var sampleRateIndex = Array.IndexOf(AacTables.SampleRates, sampleRate);
            if (sampleRateIndex < 0)
            {
                throw new NotSupportedException($"Sample rate {sampleRate} is not a valid MPEG-4 AAC sample rate");
            }

            if (sampleRate is not 44100 and not 48000)
            {
                throw new NotSupportedException("Only 44100Hz and 48000Hz sample rates are supported");
            }

            return sampleRateIndex;
        }

        private void EncodeAndEmitFrame()
        {
            var windowedInput = new double[BlockSize];
            for (var n = 0; n < CoefficientCount; n++)
            {
                windowedInput[n] = _previousBlock[n] * _window[n];
            }

            for (var n = 0; n < CoefficientCount; n++)
            {
                windowedInput[CoefficientCount + n] = _currentBlock[n] * _window[CoefficientCount + n];
            }

            var coefficients = Mdct.Forward(windowedInput);
            var payload = EncodeSingleChannelElement(coefficients);

            _onFrameEncoded(payload, _currentBlockCount, _frameIndex++);
        }

        private static double[] BuildSineWindow(int blockSize)
        {
            var window = new double[blockSize];
            for (var n = 0; n < blockSize; n++)
            {
                window[n] = Math.Sin((Math.PI / blockSize) * (n + 0.5));
            }

            return window;
        }

        private static byte[] EncodeSingleChannelElement(double[] coefficients)
        {
            var writer = new BitWriter();

            writer.WriteBits(SingleChannelElementSyntaxId, 3);
            writer.WriteBits(0, 4);
            writer.WriteBits(DefaultGlobalGain, 8);
            writer.WriteBits(0, 1);
            writer.WriteBits(OnlyLongSequence, 2);
            writer.WriteBits(SineWindowShape, 1);
            writer.WriteBits((uint)MaxScalefactorBand, 6);
            writer.WriteBits(PredictorNotPresent, 1);

            var bandTypes = new int[MaxScalefactorBand];
            var quantizedBands = new int[MaxScalefactorBand][];
            var bandGains = new int[MaxScalefactorBand];

            for (var band = 0; band < MaxScalefactorBand; band++)
            {
                var bandStart = _swbOffsets[band];
                var bandEnd = _swbOffsets[band + 1];

                var maxMagnitude = 0.0;
                for (var i = bandStart; i < bandEnd; i++)
                {
                    maxMagnitude = Math.Max(maxMagnitude, Math.Abs(coefficients[i]));
                }

                if (maxMagnitude < 1e-9)
                {
                    bandTypes[band] = 0;
                    continue;
                }

                var gain = ChooseBandGain(maxMagnitude);
                var scale = Math.Pow(2, gain / 4.0);

                var quantized = new int[bandEnd - bandStart];
                var maxQuantizedMagnitude = 0;
                for (var i = 0; i < quantized.Length; i++)
                {
                    var magnitude = Math.Abs(coefficients[bandStart + i]);
                    var quantizedMagnitude = (int)Math.Round(Math.Pow(magnitude / scale, 3.0 / 4.0));
                    quantized[i] = Math.Sign(coefficients[bandStart + i]) * quantizedMagnitude;
                    maxQuantizedMagnitude = Math.Max(maxQuantizedMagnitude, quantizedMagnitude);
                }

                if (maxQuantizedMagnitude == 0)
                {
                    bandTypes[band] = 0;
                    continue;
                }

                bandTypes[band] = ChooseCodebookBandType(maxQuantizedMagnitude);
                quantizedBands[band] = quantized;
                bandGains[band] = gain;
            }

            WriteBandTypes(writer, bandTypes);
            WriteScalefactors(writer, bandTypes, bandGains);

            writer.WriteBits(PulseDataNotPresent, 1);
            writer.WriteBits(TnsNotPresent, 1);
            writer.WriteBits(GainControlNotPresent, 1);

            WriteSpectrum(writer, bandTypes, quantizedBands);

            writer.ByteAlign();

            return writer.ToArray();

            static int ChooseBandGain(double maxMagnitude)
            {
                var targetMagnitude = Math.Pow(TargetMaxQuantizedMagnitude, 4.0 / 3.0);
                var gain = (int)Math.Round(4 * Math.Log2(maxMagnitude / targetMagnitude));

                return Math.Clamp(gain, -155, 155);
            }

            static int ChooseCodebookBandType(int maxQuantizedMagnitude)
            {
                if (maxQuantizedMagnitude <= 1)
                {
                    return 2;
                }

                if (maxQuantizedMagnitude <= 4)
                {
                    return 6;
                }

                if (maxQuantizedMagnitude <= 7)
                {
                    return 8;
                }

                if (maxQuantizedMagnitude <= 12)
                {
                    return 10;
                }

                return 11;
            }

            static void WriteBandTypes(BitWriter writer, int[] bandTypes)
            {
                var band = 0;
                while (band < bandTypes.Length)
                {
                    var sectionBandType = bandTypes[band];
                    var sectionLength = 1;
                    while (band + sectionLength < bandTypes.Length && bandTypes[band + sectionLength] == sectionBandType)
                    {
                        sectionLength++;
                    }

                    writer.WriteBits((uint)sectionBandType, 4);

                    var remaining = sectionLength;
                    while (remaining >= 31)
                    {
                        writer.WriteBits(31, 5);
                        remaining -= 31;
                    }

                    writer.WriteBits((uint)remaining, 5);

                    band += sectionLength;
                }
            }

            static void WriteScalefactors(BitWriter writer, int[] bandTypes, int[] bandGains)
            {
                const int maxDelta = 60;

                var accumulator = 100;

                for (var band = 0; band < bandTypes.Length; band++)
                {
                    if (bandTypes[band] == 0)
                    {
                        continue;
                    }

                    var target = bandGains[band] + 100;
                    var delta = Math.Clamp(target - accumulator, -maxDelta, maxDelta);
                    accumulator += delta;

                    var code = delta + AacTables.ScaleDiffZero;
                    var (huffmanCode, length) = AacTables.ScalefactorHuffman.GetCode(code);
                    writer.WriteBits(huffmanCode, length);
                }
            }

            static void WriteSpectrum(BitWriter writer, int[] bandTypes, int[][] quantizedBands)
            {
                for (var band = 0; band < bandTypes.Length; band++)
                {
                    if (bandTypes[band] == 0)
                    {
                        continue;
                    }

                    var codebook = AacTables.SpectralCodebooks[bandTypes[band] - 1];
                    var quantized = quantizedBands[band];

                    for (var position = 0; position < quantized.Length; position += codebook.GroupSize)
                    {
                        WriteSpectralGroup(writer, codebook, quantized, position);
                    }

                    static void WriteSpectralGroup(BitWriter writer, SpectralCodebook codebook, int[] quantized, int position)
                    {
                        var digitBase = codebook.SignedInTable ? (2 * codebook.Lav) + 1 : codebook.Lav + 1;
                        var flatIndex = 0;
                        var clampedMagnitudes = new int[codebook.GroupSize];

                        for (var j = 0; j < codebook.GroupSize; j++)
                        {
                            var value = quantized[position + j];
                            int digit;

                            if (codebook.SignedInTable)
                            {
                                digit = value + codebook.Lav;
                            }
                            else
                            {
                                var magnitude = Math.Abs(value);
                                clampedMagnitudes[j] = magnitude;
                                digit = Math.Min(magnitude, codebook.Lav);
                            }

                            flatIndex = (flatIndex * digitBase) + digit;
                        }

                        var (huffmanCode, length) = codebook.Huffman.GetCode(flatIndex);
                        writer.WriteBits(huffmanCode, length);

                        if (codebook.SignedInTable)
                        {
                            return;
                        }

                        for (var j = 0; j < codebook.GroupSize; j++)
                        {
                            if (clampedMagnitudes[j] != 0)
                            {
                                writer.WriteBits(quantized[position + j] < 0 ? 0u : 1u, 1);
                            }
                        }

                        for (var j = 0; j < codebook.GroupSize; j++)
                        {
                            if (codebook.HasEscape && clampedMagnitudes[j] >= codebook.Lav)
                            {
                                WriteEscape(writer, clampedMagnitudes[j]);
                            }
                        }

                        static void WriteEscape(BitWriter writer, int magnitude)
                        {
                            var bitCount = 4;
                            while ((1 << (bitCount + 1)) <= magnitude)
                            {
                                bitCount++;
                            }

                            var prefixLength = bitCount - 4;
                            var suffix = magnitude - (1 << bitCount);

                            writer.WriteBits((1u << prefixLength) - 1, prefixLength);
                            writer.WriteBits(0, 1);
                            writer.WriteBits((uint)suffix, bitCount);
                        }
                    }
                }
            }
        }
    }
}
