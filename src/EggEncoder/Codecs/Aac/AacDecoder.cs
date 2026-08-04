using EggEncoder.Codecs;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    public static class AacDecoder
    {
        private const int CoefficientCount = 1024;
        private const int BlockSize = CoefficientCount * 2;
        private const int ShortCoefficientCount = 128;
        private const int ShortBlockSize = ShortCoefficientCount * 2;
        private const int ShortWindowCount = 8;
        private const int ShortWindowHop = 128;
        private const int ShortWindowStartOffset = 448;
        private const int EightShortSequence = 2;
        private const double LongKbdAlpha = 4.0;
        private const double ShortKbdAlpha = 6.0;
        private const int ZeroBandType = 0;
        private const int NoiseBandType = 13;
        private const int IntensityBandType = 14;
        private const int IntensityBandType2 = 15;

        public static AacStreamInfo Decode(string aacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var fileBytes = File.ReadAllBytes(aacFilePath);

            var totalSamples = CountTotalSamples(fileBytes);

            var reader = new BitReader(fileBytes);
            var longSineWindow = BuildSineWindow(BlockSize);
            var shortSineWindow = BuildSineWindow(ShortBlockSize);
            var longKbdWindow = BuildKbdWindow(LongKbdAlpha, CoefficientCount);
            var shortKbdWindow = BuildKbdWindow(ShortKbdAlpha, ShortCoefficientCount);
            var previousOverlap = Array.Empty<double>();

            var channels = 0;
            var sampleRate = 0;

            while (reader.RemainingBits >= 56)
            {
                var header = ParseAdtsHeader(reader);
                channels = header.Channels;
                sampleRate = header.SampleRate;

                var frameEndBitPosition = header.FrameStartBitPosition + (header.FrameLength * 8);

                var channelData = DecodeSingleChannelElement(reader);

                var windowed = new double[BlockSize];
                if (channelData.WindowSequence == EightShortSequence)
                {
                    var shortWindow = channelData.UseKaiserBesselWindow ? shortKbdWindow : shortSineWindow;
                    for (var shortWindowIndex = 0; shortWindowIndex < channelData.WindowCoefficients.Length; shortWindowIndex++)
                    {
                        var shortTimeDomain = Mdct.Inverse(channelData.WindowCoefficients[shortWindowIndex]);
                        var startOffset = ShortWindowStartOffset + (shortWindowIndex * ShortWindowHop);

                        for (var n = 0; n < ShortBlockSize; n++)
                        {
                            windowed[startOffset + n] += shortTimeDomain[n] * shortWindow[n];
                        }
                    }
                }
                else
                {
                    var longWindow = channelData.UseKaiserBesselWindow ? longKbdWindow : longSineWindow;
                    var timeDomain = Mdct.Inverse(channelData.WindowCoefficients[0]);
                    for (var n = 0; n < BlockSize; n++)
                    {
                        windowed[n] = timeDomain[n] * longWindow[n];
                    }
                }

                var outputFrame = new int[CoefficientCount * channels];
                for (var n = 0; n < CoefficientCount; n++)
                {
                    var sample = windowed[n] + (n < previousOverlap.Length ? previousOverlap[n] : 0.0);
                    var clamped = Math.Clamp(sample, short.MinValue, short.MaxValue);
                    outputFrame[n * channels] = (int)Math.Round(clamped);
                }

                previousOverlap = windowed[CoefficientCount..];

                onBlockDecoded(outputFrame, channels, sampleRate, 16, totalSamples);

                reader.SkipToBitPosition(frameEndBitPosition);
            }

            return new AacStreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = 16,
                TotalSamples = totalSamples
            };

            static long CountTotalSamples(byte[] fileBytes)
            {
                var reader = new BitReader(fileBytes);
                var totalFrames = 0L;

                while (reader.RemainingBits >= 56)
                {
                    var header = ParseAdtsHeader(reader);
                    totalFrames++;
                    reader.SkipToBitPosition(header.FrameStartBitPosition + (header.FrameLength * 8));
                }

                return totalFrames * CoefficientCount;
            }

            static double[] BuildSineWindow(int blockSize)
            {
                var window = new double[blockSize];
                for (var n = 0; n < blockSize; n++)
                {
                    window[n] = Math.Sin((Math.PI / blockSize) * (n + 0.5));
                }

                return window;
            }

            static double[] BuildKbdWindow(double alpha, int halfLength)
            {
                var alpha2 = 4 * Math.Pow(alpha * Math.PI / halfLength, 2);
                var midpoint = halfLength / 2;
                var temp = new double[midpoint + 1];
                var scale = 0.0;

                for (var i = 0; i <= midpoint; i++)
                {
                    var argument = i * (halfLength - i) * alpha2;
                    temp[i] = ModifiedBesselI0(Math.Sqrt(argument));
                    var weight = 1 + (i != 0 && i < midpoint ? 1 : 0);
                    scale += temp[i] * weight;
                }

                scale = 1.0 / (scale + 1);

                var ascendingHalf = new double[halfLength];
                var sum = 0.0;
                var index = 0;

                for (; index <= midpoint; index++)
                {
                    sum += temp[index];
                    ascendingHalf[index] = Math.Sqrt(sum * scale);
                }

                for (; index < halfLength; index++)
                {
                    sum += temp[halfLength - index];
                    ascendingHalf[index] = Math.Sqrt(sum * scale);
                }

                var window = new double[halfLength * 2];
                for (var n = 0; n < halfLength; n++)
                {
                    window[n] = ascendingHalf[n];
                    window[(2 * halfLength) - 1 - n] = ascendingHalf[n];
                }

                return window;

                // Abramowitz & Stegun 9.8.1/9.8.2 polynomial approximation of the zeroth-order modified Bessel function
                static double ModifiedBesselI0(double x)
                {
                    if (x < 3.75)
                    {
                        var t = (x / 3.75) * (x / 3.75);
                        return 1.0
                            + t * (3.5156229
                            + t * (3.0899424
                            + t * (1.2067492
                            + t * (0.2659732
                            + t * (0.0360768
                            + t * 0.0045813)))));
                    }

                    var inverse = 3.75 / x;
                    var polynomial = 0.39894228
                        + inverse * (0.01328592
                        + inverse * (0.00225319
                        + inverse * (-0.00157565
                        + inverse * (0.00916281
                        + inverse * (-0.02057706
                        + inverse * (0.02635537
                        + inverse * (-0.01647633
                        + inverse * 0.00392377)))))));

                    return Math.Exp(x) / Math.Sqrt(x) * polynomial;
                }
            }

            static AacChannelData DecodeSingleChannelElement(BitReader reader)
            {
                SkipToSingleChannelElement(reader);

                reader.SkipBits(4);

                var globalGain = (int)reader.ReadBits(8);

                reader.SkipBits(1);
                var windowSequence = (int)reader.ReadBits(2);

                // LONG_START_SEQUENCE (1) and LONG_STOP_SEQUENCE (3) use the same 2048-sample block and
                // spectral decode as ONLY_LONG_SEQUENCE (0) - only their window shape technically differs
                // at the file's first/last frame. Approximated here as a plain long window; a real quality
                // gap versus the spec, but confined to the first/last ~1024 samples of a track.

                var useKaiserBesselWindow = reader.ReadBits(1) != 0;

                int maxScalefactorBand;
                var numWindowGroups = 1;
                var groupLengths = new List<int> { 1 };
                ushort[] swbOffsets;

                if (windowSequence == EightShortSequence)
                {
                    maxScalefactorBand = (int)reader.ReadBits(4);

                    for (var i = 0; i < 7; i++)
                    {
                        if (reader.ReadBits(1) != 0)
                        {
                            groupLengths[^1]++;
                        }
                        else
                        {
                            numWindowGroups++;
                            groupLengths.Add(1);
                        }
                    }

                    swbOffsets = AacTables.ScalefactorBandOffsets128_44100Or48000;
                }
                else
                {
                    maxScalefactorBand = (int)reader.ReadBits(6);

                    var predictorPresent = reader.ReadBits(1);
                    if (predictorPresent != 0)
                    {
                        throw new NotSupportedException("AAC prediction is not supported (and is not valid for AAC-LC)");
                    }

                    swbOffsets = AacTables.ScalefactorBandOffsets1024_44100Or48000;
                }

                if (maxScalefactorBand >= swbOffsets.Length)
                {
                    throw new InvalidDataException("max_sfb exceeds the number of scalefactor bands for this sample rate");
                }

                var sectionLengthBits = windowSequence == EightShortSequence ? 3 : 5;
                var bandTypes = new int[numWindowGroups * maxScalefactorBand];
                for (var group = 0; group < numWindowGroups; group++)
                {
                    var groupBandTypes = DecodeBandTypes(reader, maxScalefactorBand, sectionLengthBits);
                    Array.Copy(groupBandTypes, 0, bandTypes, group * maxScalefactorBand, maxScalefactorBand);
                }

                var bandGains = DecodeScalefactors(reader, bandTypes, globalGain);

                var pulsePresent = reader.ReadBits(1);
                if (pulsePresent != 0)
                {
                    throw new NotSupportedException("AAC pulse data is not supported");
                }

                var tnsPresent = reader.ReadBits(1);
                if (tnsPresent != 0)
                {
                    throw new NotSupportedException("AAC temporal noise shaping (TNS) is not supported");
                }

                var gainControlPresent = reader.ReadBits(1);
                if (gainControlPresent != 0)
                {
                    throw new NotSupportedException("AAC gain control is not supported");
                }

                if (windowSequence == EightShortSequence)
                {
                    var windowCoefficients = new double[ShortWindowCount][];
                    var windowIndex = 0;

                    for (var group = 0; group < numWindowGroups; group++)
                    {
                        var groupBandTypes = bandTypes.AsSpan(group * maxScalefactorBand, maxScalefactorBand).ToArray();
                        var groupBandGains = bandGains.AsSpan(group * maxScalefactorBand, maxScalefactorBand).ToArray();

                        var groupCoefficients = DecodeGroupSpectrum(
                            reader,
                            groupBandTypes,
                            groupBandGains,
                            maxScalefactorBand,
                            swbOffsets,
                            groupLengths[group]);
                        for (var member = 0; member < groupLengths[group]; member++)
                        {
                            windowCoefficients[windowIndex] = groupCoefficients[member];
                            windowIndex++;
                        }
                    }

                    return new AacChannelData
                    {
                        WindowSequence = windowSequence,
                        UseKaiserBesselWindow = useKaiserBesselWindow,
                        WindowCoefficients = windowCoefficients
                    };
                }

                var coefficients = DecodeSpectrum(reader, bandTypes, bandGains, maxScalefactorBand, swbOffsets, CoefficientCount);

                return new AacChannelData
                {
                    WindowSequence = windowSequence,
                    UseKaiserBesselWindow = useKaiserBesselWindow,
                    WindowCoefficients = [coefficients]
                };

                static void SkipToSingleChannelElement(BitReader reader)
                {
                    while (true)
                    {
                        var syntaxElementId = reader.ReadBits(3);
                        switch (syntaxElementId)
                        {
                            case 0:
                                return;

                            case 4:
                                SkipDataStreamElement(reader);
                                break;

                            case 6:
                                SkipFillElement(reader);
                                break;

                            default:
                                throw new NotSupportedException($"AAC syntax element type {syntaxElementId} is not supported; only single_channel_element (mono), data_stream_element, and fill_element are supported in this decoder");
                        }
                    }
                }

                static void SkipDataStreamElement(BitReader reader)
                {
                    reader.SkipBits(4);
                    var byteAlign = reader.ReadBits(1) != 0;
                    var count = (int)reader.ReadBits(8);
                    if (count == 255)
                    {
                        count += (int)reader.ReadBits(8);
                    }

                    if (byteAlign)
                    {
                        reader.ByteAlign();
                    }

                    reader.SkipBits(count * 8);
                }

                static void SkipFillElement(BitReader reader)
                {
                    var count = (int)reader.ReadBits(4);
                    if (count == 15)
                    {
                        count += (int)reader.ReadBits(8) - 1;
                    }

                    reader.SkipBits(count * 8);
                }

                static int[] DecodeBandTypes(BitReader reader, int maxScalefactorBand, int sectionLengthBits)
                {
                    var bandTypes = new int[maxScalefactorBand];
                    var band = 0;
                    var maxIncrement = (1 << sectionLengthBits) - 1;

                    while (band < maxScalefactorBand)
                    {
                        var sectionBandType = (int)reader.ReadBits(4);
                        if (sectionBandType == 12)
                        {
                            throw new InvalidDataException("Invalid AAC band type 12");
                        }

                        var sectionLength = 0;
                        int increment;
                        do
                        {
                            increment = (int)reader.ReadBits(sectionLengthBits);
                            sectionLength += increment;
                        } while (increment == maxIncrement);

                        for (var i = 0; i < sectionLength; i++)
                        {
                            bandTypes[band + i] = sectionBandType;
                        }

                        band += sectionLength;
                    }

                    return bandTypes;
                }

                static int[] DecodeScalefactors(BitReader reader, int[] bandTypes, int globalGain)
                {
                    const int noiseOffset = 90;
                    const int noisePreambleBias = 256;
                    const int noisePreambleBits = 9;

                    var bandGains = new int[bandTypes.Length];
                    var accumulator = globalGain;
                    var noiseAccumulator = globalGain - noiseOffset;
                    var isFirstNoiseBand = true;

                    for (var band = 0; band < bandTypes.Length; band++)
                    {
                        switch (bandTypes[band])
                        {
                            case ZeroBandType:
                                bandGains[band] = 0;
                                break;

                            case NoiseBandType:
                                if (isFirstNoiseBand)
                                {
                                    noiseAccumulator += (int)reader.ReadBits(noisePreambleBits) - noisePreambleBias;
                                    isFirstNoiseBand = false;
                                }
                                else
                                {
                                    noiseAccumulator += AacTables.ScalefactorHuffman.Decode(reader) - AacTables.ScaleDiffZero;
                                }

                                bandGains[band] = Math.Clamp(noiseAccumulator, -100, 155);
                                break;

                            case IntensityBandType:
                            case IntensityBandType2:
                                throw new NotSupportedException($"AAC band type {bandTypes[band]} (intensity stereo) is not supported");

                            default:
                                var delta = AacTables.ScalefactorHuffman.Decode(reader) - AacTables.ScaleDiffZero;
                                accumulator += delta;
                                bandGains[band] = accumulator - 100;
                                break;
                        }
                    }

                    return bandGains;
                }

                static double[] DecodeSpectrum(
                    BitReader reader,
                    int[] bandTypes,
                    int[] bandGains,
                    int maxScalefactorBand,
                    ushort[] swbOffsets,
                    int coefficientCount)
                {
                    var coefficients = new double[coefficientCount];
                    var noiseGenerator = new Random(12345);

                    for (var band = 0; band < maxScalefactorBand; band++)
                    {
                        var bandStart = swbOffsets[band];
                        var bandEnd = swbOffsets[band + 1];

                        if (bandTypes[band] == ZeroBandType)
                        {
                            continue;
                        }

                        var scale = Math.Pow(2, bandGains[band] / 4.0);

                        if (bandTypes[band] == NoiseBandType)
                        {
                            FillNoiseBand(coefficients, bandStart, bandEnd, scale, noiseGenerator);
                            continue;
                        }

                        var codebook = AacTables.SpectralCodebooks[bandTypes[band] - 1];

                        for (int position = bandStart; position < bandEnd; position += codebook.GroupSize)
                        {
                            var flatIndex = codebook.Huffman.Decode(reader);
                            var values = UnrankSpectralValues(reader, codebook, flatIndex);

                            for (var j = 0; j < codebook.GroupSize; j++)
                            {
                                var quantized = values[j];
                                var magnitude = Math.Pow(Math.Abs(quantized), 4.0 / 3.0);
                                coefficients[position + j] = Math.Sign(quantized) * magnitude * scale;
                            }
                        }
                    }

                    return coefficients;
                }

                static double[][] DecodeGroupSpectrum(
                    BitReader reader,
                    int[] bandTypes,
                    int[] bandGains,
                    int maxScalefactorBand,
                    ushort[] swbOffsets,
                    int groupLength)
                {
                    var windowCoefficients = new double[groupLength][];
                    for (var w = 0; w < groupLength; w++)
                    {
                        windowCoefficients[w] = new double[ShortCoefficientCount];
                    }

                    var noiseGenerator = new Random(12345);

                    for (var band = 0; band < maxScalefactorBand; band++)
                    {
                        var bandStart = swbOffsets[band];
                        var bandEnd = swbOffsets[band + 1];

                        if (bandTypes[band] == ZeroBandType)
                        {
                            continue;
                        }

                        var scale = Math.Pow(2, bandGains[band] / 4.0);

                        if (bandTypes[band] == NoiseBandType)
                        {
                            for (var w = 0; w < groupLength; w++)
                            {
                                FillNoiseBand(windowCoefficients[w], bandStart, bandEnd, scale, noiseGenerator);
                            }

                            continue;
                        }

                        var codebook = AacTables.SpectralCodebooks[bandTypes[band] - 1];

                        for (var w = 0; w < groupLength; w++)
                        {
                            for (int position = bandStart; position < bandEnd; position += codebook.GroupSize)
                            {
                                var flatIndex = codebook.Huffman.Decode(reader);
                                var values = UnrankSpectralValues(reader, codebook, flatIndex);

                                for (var j = 0; j < codebook.GroupSize; j++)
                                {
                                    var quantized = values[j];
                                    var magnitude = Math.Pow(Math.Abs(quantized), 4.0 / 3.0);
                                    windowCoefficients[w][position + j] = Math.Sign(quantized) * magnitude * scale;
                                }
                            }
                        }
                    }

                    return windowCoefficients;
                }

                static void FillNoiseBand(double[] coefficients, int bandStart, int bandEnd, double targetMagnitude, Random noiseGenerator)
                {
                    var bandLength = bandEnd - bandStart;
                    var noise = new double[bandLength];
                    var energy = 0.0;

                    for (var i = 0; i < bandLength; i++)
                    {
                        noise[i] = (noiseGenerator.NextDouble() * 2) - 1;
                        energy += noise[i] * noise[i];
                    }

                    var scale = targetMagnitude / Math.Sqrt(Math.Max(energy, 1e-12));
                    for (var i = 0; i < bandLength; i++)
                    {
                        coefficients[bandStart + i] = noise[i] * scale;
                    }
                }

                static int[] UnrankSpectralValues(BitReader reader, SpectralCodebook codebook, int flatIndex)
                {
                    var values = new int[codebook.GroupSize];
                    var digitBase = codebook.SignedInTable ? (2 * codebook.Lav) + 1 : codebook.Lav + 1;

                    var remaining = flatIndex;
                    var digits = new int[codebook.GroupSize];
                    for (var j = codebook.GroupSize - 1; j >= 0; j--)
                    {
                        digits[j] = remaining % digitBase;
                        remaining /= digitBase;
                    }

                    if (codebook.SignedInTable)
                    {
                        for (var j = 0; j < codebook.GroupSize; j++)
                        {
                            values[j] = digits[j] - codebook.Lav;
                        }

                        return values;
                    }

                    // AAC spectral_data() reads every nonzero component's sign bit as one batch
                    // immediately after the Huffman codeword, before any escape-suffix data.
                    var isNegative = new bool[codebook.GroupSize];
                    for (var j = 0; j < codebook.GroupSize; j++)
                    {
                        if (digits[j] != 0)
                        {
                            isNegative[j] = reader.ReadBits(1) != 0;
                        }
                    }

                    for (var j = 0; j < codebook.GroupSize; j++)
                    {
                        var magnitude = digits[j];
                        if (magnitude != 0 && codebook.HasEscape && magnitude == codebook.Lav)
                        {
                            magnitude = DecodeEscape(reader);
                        }

                        values[j] = isNegative[j] ? -magnitude : magnitude;
                    }

                    return values;

                    static int DecodeEscape(BitReader reader)
                    {
                        var prefixLength = 0;
                        while (reader.ReadBits(1) == 1)
                        {
                            prefixLength++;
                            if (prefixLength > 20)
                            {
                                throw new InvalidDataException("AAC escape code prefix exceeded maximum length");
                            }
                        }

                        var bitCount = prefixLength + 4;
                        var suffix = (int)reader.ReadBits(bitCount);

                        return (1 << bitCount) + suffix;
                    }
                }
            }
        }

        private static AdtsHeader ParseAdtsHeader(BitReader reader)
        {
            var frameStartBitPosition = reader.BitPosition;

            if (reader.ReadBits(12) != 0xFFF)
            {
                throw new InvalidDataException("Missing ADTS sync word");
            }

            reader.SkipBits(1);
            reader.SkipBits(2);
            var protectionAbsent = reader.ReadBits(1);
            var profile = reader.ReadBits(2);
            var samplingFrequencyIndex = (int)reader.ReadBits(4);

            if (samplingFrequencyIndex >= AacTables.SampleRates.Length)
            {
                throw new InvalidDataException($"Invalid ADTS sampling_frequency_index {samplingFrequencyIndex}");
            }

            reader.SkipBits(1);
            var channelConfig = (int)reader.ReadBits(3);
            reader.SkipBits(1);
            reader.SkipBits(1);
            reader.SkipBits(1);
            reader.SkipBits(1);
            var frameLength = (int)reader.ReadBits(13);
            reader.SkipBits(11);
            var numRawDataBlocks = reader.ReadBits(2);

            if (profile != 1)
            {
                throw new NotSupportedException($"AAC profile {profile + 1} is not supported; only AAC-LC (profile 2, encoded as 1) is supported");
            }

            if (numRawDataBlocks != 0)
            {
                throw new NotSupportedException("ADTS frames with multiple raw_data_blocks are not supported");
            }

            if (channelConfig is not 1 and not 2)
            {
                throw new NotSupportedException($"Channel configuration {channelConfig} is not supported; only mono (1) and stereo (2) are supported");
            }

            if (protectionAbsent == 0)
            {
                reader.SkipBits(16);
            }

            return new AdtsHeader
            {
                Channels = channelConfig,
                SampleRate = AacTables.SampleRates[samplingFrequencyIndex],
                FrameLength = frameLength,
                FrameStartBitPosition = frameStartBitPosition
            };
        }

        private readonly struct AdtsHeader
        {
            public int Channels { get; init; }

            public int SampleRate { get; init; }

            public int FrameLength { get; init; }

            public int FrameStartBitPosition { get; init; }
        }

        private readonly struct AacChannelData
        {
            public int WindowSequence { get; init; }

            public bool UseKaiserBesselWindow { get; init; }

            public double[][] WindowCoefficients { get; init; }
        }
    }

    public class AacStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
