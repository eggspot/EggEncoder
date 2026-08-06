using EggEncoder.Codecs;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Wma
{
    public static class WmaDecoder
    {
        public static WmaStreamInfo Decode(string wmaFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var streamProperties = AsfContainerReader.Read(wmaFilePath, out var framePayloads);

            if (streamProperties.Channels is not 1 and not 2)
            {
                throw new NotSupportedException($"WMA channel count {streamProperties.Channels} is not supported; only mono and stereo are supported");
            }

            if (streamProperties.ExtraData.Length < 6)
            {
                throw new InvalidDataException("WMAv2 extra data must be at least 6 bytes");
            }

            var flags2 = (ushort)(streamProperties.ExtraData[4] | (streamProperties.ExtraData[5] << 8));
            var useExpVlc = (flags2 & 0x0001) != 0;
            var useBitReservoir = (flags2 & 0x0002) != 0;
            var useVariableBlockLength = (flags2 & 0x0004) != 0;

            if (useBitReservoir)
            {
                throw new NotSupportedException("WMA bit reservoir (superframe cross-packet bit borrowing) is not supported");
            }

            if (useVariableBlockLength)
            {
                throw new NotSupportedException("WMA variable block length is not supported");
            }

            if (!useExpVlc)
            {
                throw new NotSupportedException("WMA LSP-coded exponents are not supported; only Huffman-coded (VLC) exponents are supported");
            }

            var frameLengthBits = WmaTables.GetFrameLengthBits(streamProperties.SampleRate);
            var frameLength = 1 << frameLengthBits;
            var coefsEnd = frameLength - (frameLength * 9 / 100);
            var exponentBands = WmaTables.BuildExponentBands(streamProperties.SampleRate, frameLength);
            var window = WmaTables.BuildSineWindow(frameLength);
            var (runTable, levelTable) = WmaTables.BuildCoefficientRunLevelTables();

            var channels = streamProperties.Channels;
            var previousOverlap = new double[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                previousOverlap[channel] = new double[frameLength];
            }

            var totalSamples = (long)framePayloads.Count * frameLength;

            foreach (var payload in framePayloads)
            {
                var reader = new BitReader(payload);
                var channelCoefficients = DecodeFrame(
                    reader,
                    channels,
                    frameLength,
                    frameLengthBits,
                    coefsEnd,
                    exponentBands,
                    runTable,
                    levelTable);

                var outputFrame = new int[frameLength * channels];
                for (var channel = 0; channel < channels; channel++)
                {
                    var timeDomain = Mdct.Inverse(channelCoefficients[channel]);
                    var windowed = new double[frameLength * 2];
                    for (var n = 0; n < frameLength; n++)
                    {
                        windowed[n] = timeDomain[n] * window[n];
                    }

                    for (var n = 0; n < frameLength; n++)
                    {
                        windowed[frameLength + n] = timeDomain[frameLength + n] * window[frameLength - 1 - n];
                    }

                    for (var n = 0; n < frameLength; n++)
                    {
                        var sample = windowed[n] + previousOverlap[channel][n];
                        var clamped = Math.Clamp(sample, short.MinValue, short.MaxValue);
                        outputFrame[(n * channels) + channel] = (int)Math.Round(clamped);
                    }

                    previousOverlap[channel] = windowed[frameLength..];
                }

                onBlockDecoded(outputFrame, channels, streamProperties.SampleRate, 16, totalSamples);
            }

            return new WmaStreamInfo
            {
                Channels = channels,
                SampleRate = streamProperties.SampleRate,
                BitsPerSample = 16,
                TotalSamples = totalSamples
            };

            static double[][] DecodeFrame(
                BitReader reader,
                int channels,
                int frameLength,
                int frameLengthBits,
                int coefsEnd,
                ushort[] exponentBands,
                int[] runTable,
                int[] levelTable
                )
            {
                var totalGain = 1;
                int gainIncrement;
                do
                {
                    gainIncrement = (int)reader.ReadBits(7);
                    totalGain += gainIncrement;
                } while (gainIncrement == 127);

                var coefficientBitWidth = TotalGainToBits(totalGain);

                if (channels == 2 && reader.ReadBits(1) != 0)
                {
                    throw new NotSupportedException("WMA mid/side stereo coding is not supported");
                }

                var channelCoded = new bool[channels];
                for (var channel = 0; channel < channels; channel++)
                {
                    channelCoded[channel] = reader.ReadBits(1) != 0;
                }

                var numberOfCoefficients = coefsEnd;
                var exponents = new double[channels][];
                var maxExponent = new double[channels];
                var quantizedCoefficients = new int[channels][];

                for (var channel = 0; channel < channels; channel++)
                {
                    exponents[channel] = new double[frameLength];
                    quantizedCoefficients[channel] = new int[frameLength];

                    if (!channelCoded[channel])
                    {
                        continue;
                    }

                    maxExponent[channel] = DecodeExponents(reader, exponentBands, exponents[channel]);
                    DecodeCoefficients(
                        reader,
                        quantizedCoefficients[channel],
                        runTable,
                        levelTable,
                        numberOfCoefficients,
                        frameLength,
                        frameLengthBits,
                        coefficientBitWidth);
                }

                var channelCoefficients = new double[channels][];
                for (var channel = 0; channel < channels; channel++)
                {
                    channelCoefficients[channel] = new double[frameLength];

                    if (!channelCoded[channel])
                    {
                        continue;
                    }

                    var mult = Math.Pow(10, totalGain * 0.05) / maxExponent[channel] / (frameLength / 2.0);

                    for (var i = 0; i < numberOfCoefficients; i++)
                    {
                        channelCoefficients[channel][i] = quantizedCoefficients[channel][i] * exponents[channel][i] * mult;
                    }
                }

                return channelCoefficients;

                static int TotalGainToBits(int totalGain)
                {
                    if (totalGain < 15)
                    {
                        return 13;
                    }

                    if (totalGain < 32)
                    {
                        return 12;
                    }

                    if (totalGain < 40)
                    {
                        return 11;
                    }

                    if (totalGain < 45)
                    {
                        return 10;
                    }

                    return 9;
                }

                static double DecodeExponents(BitReader reader, ushort[] exponentBands, double[] exponents)
                {
                    var lastExponent = 36;
                    var maxScale = 0.0;
                    var position = 0;

                    foreach (var bandLength in exponentBands)
                    {
                        var code = (int)Aac.AacTables.ScalefactorHuffman.Decode(reader);
                        lastExponent += code - Aac.AacTables.ScaleDiffZero;

                        var value = WmaTables.PowTable[lastExponent + 60];
                        if (value > maxScale)
                        {
                            maxScale = value;
                        }

                        for (var i = 0; i < bandLength && position < exponents.Length; i++, position++)
                        {
                            exponents[position] = value;
                        }
                    }

                    return maxScale;
                }

                static void DecodeCoefficients(
                    BitReader reader,
                    int[] coefficients,
                    int[] runTable,
                    int[] levelTable,
                    int numberOfCoefficients,
                    int blockLength,
                    int frameLengthBits,
                    int coefficientBitWidth
                    )
                {
                    var coefficientMask = blockLength - 1;

                    for (var offset = 0; offset < numberOfCoefficients; offset++)
                    {
                        var code = (int)WmaTables.Coef4Huffman.Decode(reader);

                        if (code > 1)
                        {
                            offset += runTable[code];
                            var isPositive = reader.ReadBits(1) != 0;
                            var level = levelTable[code];
                            coefficients[offset & coefficientMask] = isPositive ? level : -level;
                            continue;
                        }

                        if (code == 1)
                        {
                            break;
                        }

                        var escapedLevel = (int)reader.ReadBits(coefficientBitWidth);
                        offset += (int)reader.ReadBits(frameLengthBits);
                        var escapedIsPositive = reader.ReadBits(1) != 0;
                        coefficients[offset & coefficientMask] = escapedIsPositive ? escapedLevel : -escapedLevel;
                    }
                }
            }
        }
    }

    public class WmaStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
