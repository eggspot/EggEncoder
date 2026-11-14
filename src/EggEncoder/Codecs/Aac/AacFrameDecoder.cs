using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    internal sealed class AacFrameDecoder
    {
        public const int FrameSamples = 1024;

        private const int BlockSize = FrameSamples * 2;
        private const int ShortCoefficientCount = 128;
        private const int ShortBlockSize = ShortCoefficientCount * 2;
        private const int ShortWindowCount = 8;
        private const int ShortWindowStartOffset = 448;
        private const int LongStartSequence = 1;
        private const int EightShortSequence = 2;
        private const int LongStopSequence = 3;
        private const double LongKbdAlpha = 4.0;
        private const double ShortKbdAlpha = 6.0;
        private const int ZeroBandType = 0;
        private const int NoiseBandType = 13;
        private const int IntensityOutOfPhaseBandType = 14;
        private const int IntensityInPhaseBandType = 15;
        private const int MaxQuantizedMagnitude = 8191;
        private const int MaxTnsOrderLong = 12;
        private const int MaxTnsOrderShort = 7;
        private const int SbrExtensionType = 13;
        private const int SbrCrcExtensionType = 14;

        private static readonly double[] PowFourThirds = BuildPowFourThirds();

        private readonly AacBandTables _tables;
        private readonly int _outputChannels;
        private readonly AacChannelState[] _states;
        private readonly double[][] _longWindows;
        private readonly double[][] _shortWindows;
        private readonly Random _noise = new(12345);

        public AacFrameDecoder(int sampleRate, int channels)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"AAC with {channels} output channels is not supported; only mono and stereo are");
            }

            _tables = AacBandTables.ForSampleRate(sampleRate);
            _outputChannels = channels;
            _states = [new AacChannelState(), new AacChannelState()];
            _longWindows = [BuildSineWindow(BlockSize), BuildKbdWindow(LongKbdAlpha, FrameSamples)];
            _shortWindows = [BuildSineWindow(ShortBlockSize), BuildKbdWindow(ShortKbdAlpha, ShortCoefficientCount)];
        }

        public int[] DecodeFrame(BitReader reader)
        {
            var produced = 0;
            var timeDomain = new double[_outputChannels][];
            while (true)
            {
                var elementId = (int)reader.ReadBits(3);
                switch (elementId)
                {
                    case 0:
                    {
                        reader.SkipBits(4);
                        var ics = ReadIcs(reader, null);
                        ApplyTns(ics);
                        timeDomain[ClaimChannels(ref produced, 1)] = Synthesize(ics, _states[produced - 1]);
                        break;
                    }

                    case 1:
                        DecodeChannelPair(reader, timeDomain, ref produced);
                        break;

                    case 2:
                        throw new NotSupportedException("AAC coupling channel elements are not supported");

                    case 3:
                        reader.SkipBits(4);
                        ReadIcs(reader, null);
                        break;

                    case 4:
                        SkipDataStreamElement(reader);
                        break;

                    case 5:
                        AacProgramConfig.Skip(reader);
                        break;

                    case 6:
                        SkipFillElement(reader);
                        break;

                    default:
                        return Interleave(timeDomain, produced);
                }
            }
        }

        private int ClaimChannels(ref int produced, int count)
        {
            if (produced + count > _outputChannels)
            {
                throw new NotSupportedException($"This AAC stream carries more than the {_outputChannels} channel(s) its configuration declares; only mono and stereo are supported");
            }

            var first = produced;
            produced += count;
            return first;
        }

        private void DecodeChannelPair(BitReader reader, double[][] timeDomain, ref int produced)
        {
            reader.SkipBits(4);
            var commonWindow = reader.ReadBits(1) != 0;
            AacIcsInfo? info = null;
            var msMaskPresent = 0;
            bool[]? msUsed = null;
            if (commonWindow)
            {
                info = ReadIcsInfo(reader);
                msMaskPresent = (int)reader.ReadBits(2);
                if (msMaskPresent == 3)
                {
                    throw new InvalidDataException("AAC ms_mask_present 3 is reserved");
                }

                msUsed = new bool[info.GroupCount * info.MaxSfb];
                for (var i = 0; i < msUsed.Length; i++)
                {
                    msUsed[i] = msMaskPresent == 2 || (msMaskPresent == 1 && reader.ReadBits(1) != 0);
                }
            }

            var left = ReadIcs(reader, info);
            var right = ReadIcs(reader, info);
            if (commonWindow)
            {
                ApplyMidSide(left, right, msUsed!);
                ApplyIntensity(left, right, msMaskPresent == 1 ? msUsed : null);
            }

            ApplyTns(left);
            ApplyTns(right);
            var first = ClaimChannels(ref produced, 2);
            timeDomain[first] = Synthesize(left, _states[first]);
            timeDomain[first + 1] = Synthesize(right, _states[first + 1]);
        }

        private int[] Interleave(double[][] timeDomain, int produced)
        {
            if (produced == 0)
            {
                throw new InvalidDataException("AAC raw_data_block holds no audio channel element");
            }

            if (produced == 1 && _outputChannels == 2)
            {
                timeDomain[1] = timeDomain[0];
            }

            var output = new int[FrameSamples * _outputChannels];
            for (var channel = 0; channel < _outputChannels; channel++)
            {
                var samples = timeDomain[channel];
                for (var n = 0; n < FrameSamples; n++)
                {
                    output[(n * _outputChannels) + channel] = (int)Math.Round(Math.Clamp(samples[n], short.MinValue, short.MaxValue));
                }
            }

            return output;
        }

        private AacIcsInfo ReadIcsInfo(BitReader reader)
        {
            reader.SkipBits(1);
            var windowSequence = (int)reader.ReadBits(2);
            var windowShape = (int)reader.ReadBits(1);
            if (windowSequence == EightShortSequence)
            {
                var maxSfb = (int)reader.ReadBits(4);
                var groupLengths = new List<int> { 1 };
                for (var i = 0; i < 7; i++)
                {
                    if (reader.ReadBits(1) != 0)
                    {
                        groupLengths[^1]++;
                    }
                    else
                    {
                        groupLengths.Add(1);
                    }
                }

                if (maxSfb > _tables.ShortOffsets.Length - 1)
                {
                    throw new InvalidDataException("AAC max_sfb exceeds the number of short-window scalefactor bands");
                }

                return new AacIcsInfo(windowSequence, windowShape, maxSfb, groupLengths.ToArray(), _tables.ShortOffsets);
            }

            var longMaxSfb = (int)reader.ReadBits(6);
            if (reader.ReadBits(1) != 0)
            {
                throw new NotSupportedException("AAC prediction is not supported (and is not valid for AAC-LC)");
            }

            if (longMaxSfb > _tables.LongOffsets.Length - 1)
            {
                throw new InvalidDataException("AAC max_sfb exceeds the number of scalefactor bands for this sample rate");
            }

            return new AacIcsInfo(windowSequence, windowShape, longMaxSfb, [1], _tables.LongOffsets);
        }

        private AacIcs ReadIcs(BitReader reader, AacIcsInfo? commonInfo)
        {
            var globalGain = (int)reader.ReadBits(8);
            var info = commonInfo ?? ReadIcsInfo(reader);
            var bandTypes = ReadSectionData(reader, info);
            var scalefactors = ReadScalefactors(reader, info, bandTypes, globalGain);
            AacPulse? pulse = null;
            if (reader.ReadBits(1) != 0)
            {
                if (info.IsShort)
                {
                    throw new InvalidDataException("AAC pulse data is not allowed with short windows");
                }

                pulse = ReadPulse(reader, info);
            }

            var tns = reader.ReadBits(1) != 0 ? ReadTns(reader, info) : null;
            if (reader.ReadBits(1) != 0)
            {
                throw new NotSupportedException("AAC gain control (SSR profile) is not supported");
            }

            var spectrum = ReadSpectrum(reader, info, bandTypes, scalefactors, pulse);
            return new AacIcs(info, bandTypes, scalefactors, spectrum, tns);
        }

        private static int[] ReadSectionData(BitReader reader, AacIcsInfo info)
        {
            var sectionLengthBits = info.IsShort ? 3 : 5;
            var escape = (1 << sectionLengthBits) - 1;
            var bandTypes = new int[info.GroupCount * info.MaxSfb];
            for (var group = 0; group < info.GroupCount; group++)
            {
                var band = 0;
                while (band < info.MaxSfb)
                {
                    var bandType = (int)reader.ReadBits(4);
                    if (bandType == 12)
                    {
                        throw new InvalidDataException("Invalid AAC band type 12");
                    }

                    var length = 0;
                    int increment;
                    do
                    {
                        increment = (int)reader.ReadBits(sectionLengthBits);
                        length += increment;
                    } while (increment == escape);

                    if (length > info.MaxSfb - band)
                    {
                        throw new InvalidDataException("AAC section data runs past max_sfb");
                    }

                    for (var i = 0; i < length; i++)
                    {
                        bandTypes[(group * info.MaxSfb) + band + i] = bandType;
                    }

                    band += length;
                }
            }

            return bandTypes;
        }

        private static int[] ReadScalefactors(BitReader reader, AacIcsInfo info, int[] bandTypes, int globalGain)
        {
            const int noiseOffset = 90;
            const int noisePreambleBias = 256;
            var values = new int[bandTypes.Length];
            var scalefactor = globalGain;
            var noiseEnergy = globalGain - noiseOffset;
            var intensityPosition = 0;
            var firstNoiseBand = true;
            for (var i = 0; i < bandTypes.Length; i++)
            {
                switch (bandTypes[i])
                {
                    case ZeroBandType:
                        break;
                    case NoiseBandType:
                        noiseEnergy += firstNoiseBand
                            ? (int)reader.ReadBits(9) - noisePreambleBias
                            : AacTables.ScalefactorHuffman.Decode(reader) - AacTables.ScaleDiffZero;
                        firstNoiseBand = false;
                        values[i] = Math.Clamp(noiseEnergy, -100, 155);
                        break;
                    case IntensityOutOfPhaseBandType:
                    case IntensityInPhaseBandType:
                        intensityPosition += AacTables.ScalefactorHuffman.Decode(reader) - AacTables.ScaleDiffZero;
                        values[i] = intensityPosition;
                        break;
                    default:
                        scalefactor += AacTables.ScalefactorHuffman.Decode(reader) - AacTables.ScaleDiffZero;
                        if (scalefactor is < 0 or > 255)
                        {
                            throw new InvalidDataException($"AAC scalefactor {scalefactor} is out of range");
                        }

                        values[i] = scalefactor - 100;
                        break;
                }
            }

            return values;
        }

        private static AacPulse ReadPulse(BitReader reader, AacIcsInfo info)
        {
            var count = (int)reader.ReadBits(2) + 1;
            var startBand = (int)reader.ReadBits(6);
            if (startBand >= info.Offsets.Length - 1)
            {
                throw new InvalidDataException("AAC pulse_start_sfb is past the last scalefactor band");
            }

            var offsets = new int[count];
            var amplitudes = new int[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = (int)reader.ReadBits(5);
                amplitudes[i] = (int)reader.ReadBits(4);
            }

            return new AacPulse(info.Offsets[startBand], offsets, amplitudes);
        }

        private static AacTnsFilter[][] ReadTns(BitReader reader, AacIcsInfo info)
        {
            var windows = new AacTnsFilter[info.WindowCount][];
            for (var w = 0; w < info.WindowCount; w++)
            {
                var filterCount = (int)reader.ReadBits(info.IsShort ? 1 : 2);
                var filters = new AacTnsFilter[filterCount];
                var coefficientResolution = filterCount > 0 ? (int)reader.ReadBits(1) + 3 : 0;
                for (var f = 0; f < filterCount; f++)
                {
                    var length = (int)reader.ReadBits(info.IsShort ? 4 : 6);
                    var order = (int)reader.ReadBits(info.IsShort ? 3 : 5);
                    if (order > (info.IsShort ? MaxTnsOrderShort : MaxTnsOrderLong))
                    {
                        throw new InvalidDataException($"AAC TNS order {order} is above the AAC-LC maximum");
                    }

                    var reflection = Array.Empty<double>();
                    var downward = false;
                    if (order > 0)
                    {
                        downward = reader.ReadBits(1) != 0;
                        var compress = (int)reader.ReadBits(1);
                        var bits = coefficientResolution - compress;
                        reflection = new double[order];
                        var positiveScale = ((1 << (coefficientResolution - 1)) - 0.5) / (Math.PI / 2);
                        var negativeScale = ((1 << (coefficientResolution - 1)) + 0.5) / (Math.PI / 2);
                        for (var i = 0; i < order; i++)
                        {
                            var raw = (int)reader.ReadBits(bits);
                            var value = raw >= 1 << (bits - 1) ? raw - (1 << bits) : raw;
                            reflection[i] = Math.Sin(value / (value >= 0 ? positiveScale : negativeScale));
                        }
                    }

                    filters[f] = new AacTnsFilter(length, downward, ToLpc(reflection));
                }

                windows[w] = filters;
            }

            return windows;
        }

        internal static double[] ToLpc(double[] reflection)
        {
            var lpc = new double[reflection.Length];
            var previous = new double[reflection.Length];
            for (var m = 0; m < reflection.Length; m++)
            {
                Array.Copy(lpc, previous, m);
                for (var i = 0; i < m; i++)
                {
                    lpc[i] = previous[i] + (reflection[m] * previous[m - 1 - i]);
                }

                lpc[m] = reflection[m];
            }

            return lpc;
        }

        private double[][] ReadSpectrum(BitReader reader, AacIcsInfo info, int[] bandTypes, int[] scalefactors, AacPulse? pulse)
        {
            var windowLength = info.IsShort ? ShortCoefficientCount : FrameSamples;
            var quantized = new int[info.WindowCount][];
            for (var w = 0; w < info.WindowCount; w++)
            {
                quantized[w] = new int[windowLength];
            }

            var window = 0;
            for (var group = 0; group < info.GroupCount; group++)
            {
                for (var band = 0; band < info.MaxSfb; band++)
                {
                    var bandType = bandTypes[(group * info.MaxSfb) + band];
                    if (bandType is ZeroBandType or NoiseBandType or IntensityOutOfPhaseBandType or IntensityInPhaseBandType)
                    {
                        continue;
                    }

                    var codebook = AacTables.SpectralCodebooks[bandType - 1];
                    for (var w = 0; w < info.GroupLengths[group]; w++)
                    {
                        var target = quantized[window + w];
                        for (int position = info.Offsets[band]; position < info.Offsets[band + 1]; position += codebook.GroupSize)
                        {
                            ReadCodeword(reader, codebook, target, position);
                        }
                    }
                }

                window += info.GroupLengths[group];
            }

            if (pulse is not null)
            {
                var position = pulse.Start;
                for (var i = 0; i < pulse.Offsets.Length; i++)
                {
                    position += pulse.Offsets[i];
                    if (position >= windowLength)
                    {
                        throw new InvalidDataException("AAC pulse data runs past the spectrum");
                    }

                    quantized[0][position] += quantized[0][position] > 0 ? pulse.Amplitudes[i] : -pulse.Amplitudes[i];
                }
            }

            var spectrum = new double[info.WindowCount][];
            window = 0;
            for (var group = 0; group < info.GroupCount; group++)
            {
                for (var w = 0; w < info.GroupLengths[group]; w++)
                {
                    var coefficients = new double[windowLength];
                    var q = quantized[window + w];
                    for (var band = 0; band < info.MaxSfb; band++)
                    {
                        var index = (group * info.MaxSfb) + band;
                        var start = info.Offsets[band];
                        var end = info.Offsets[band + 1];
                        switch (bandTypes[index])
                        {
                            case ZeroBandType:
                            case IntensityOutOfPhaseBandType:
                            case IntensityInPhaseBandType:
                                break;
                            case NoiseBandType:
                                FillNoise(coefficients, start, end, Math.Pow(2, scalefactors[index] / 4.0), _noise);
                                break;
                            default:
                                var gain = Math.Pow(2, scalefactors[index] / 4.0);
                                for (var k = start; k < end; k++)
                                {
                                    var value = q[k];
                                    var magnitude = Math.Abs(value);
                                    var dequantized = magnitude < PowFourThirds.Length ? PowFourThirds[magnitude] : Math.Pow(magnitude, 4.0 / 3.0);
                                    coefficients[k] = (value < 0 ? -dequantized : dequantized) * gain;
                                }

                                break;
                        }
                    }

                    spectrum[window + w] = coefficients;
                }

                window += info.GroupLengths[group];
            }

            return spectrum;
        }

        private static void ReadCodeword(BitReader reader, SpectralCodebook codebook, int[] target, int position)
        {
            var flatIndex = codebook.Huffman.Decode(reader);
            var digitBase = codebook.SignedInTable ? (2 * codebook.Lav) + 1 : codebook.Lav + 1;
            Span<int> digits = stackalloc int[codebook.GroupSize];
            for (var j = codebook.GroupSize - 1; j >= 0; j--)
            {
                digits[j] = flatIndex % digitBase;
                flatIndex /= digitBase;
            }

            if (codebook.SignedInTable)
            {
                for (var j = 0; j < codebook.GroupSize; j++)
                {
                    target[position + j] = digits[j] - codebook.Lav;
                }

                return;
            }

            Span<bool> negative = stackalloc bool[codebook.GroupSize];
            for (var j = 0; j < codebook.GroupSize; j++)
            {
                negative[j] = digits[j] != 0 && reader.ReadBits(1) != 0;
            }

            for (var j = 0; j < codebook.GroupSize; j++)
            {
                var magnitude = digits[j];
                if (codebook.HasEscape && magnitude == codebook.Lav)
                {
                    magnitude = ReadEscape(reader);
                }

                target[position + j] = negative[j] ? -magnitude : magnitude;
            }
        }

        private static int ReadEscape(BitReader reader)
        {
            var prefixLength = 0;
            while (reader.ReadBits(1) == 1)
            {
                prefixLength++;
                if (prefixLength > 8)
                {
                    throw new InvalidDataException("AAC escape code prefix exceeds the maximum length");
                }
            }

            var bitCount = prefixLength + 4;
            return (1 << bitCount) + (int)reader.ReadBits(bitCount);
        }

        private static void FillNoise(double[] coefficients, int start, int end, double energyScale, Random noise)
        {
            var energy = 0.0;
            for (var k = start; k < end; k++)
            {
                coefficients[k] = (noise.NextDouble() * 2) - 1;
                energy += coefficients[k] * coefficients[k];
            }

            var scale = energyScale / Math.Sqrt(Math.Max(energy, 1e-12));
            for (var k = start; k < end; k++)
            {
                coefficients[k] *= scale;
            }
        }

        private static void ApplyMidSide(AacIcs left, AacIcs right, bool[] msUsed)
        {
            var info = left.Info;
            var window = 0;
            for (var group = 0; group < info.GroupCount; group++)
            {
                for (var band = 0; band < info.MaxSfb; band++)
                {
                    var index = (group * info.MaxSfb) + band;
                    if (msUsed[index] && left.BandTypes[index] == NoiseBandType && right.BandTypes[index] == NoiseBandType)
                    {
                        CopyNoise(left, right, info, group, window, band, index);
                        continue;
                    }

                    if (!msUsed[index] || right.BandTypes[index] >= NoiseBandType || left.BandTypes[index] == NoiseBandType)
                    {
                        continue;
                    }

                    for (var w = 0; w < info.GroupLengths[group]; w++)
                    {
                        var l = left.Spectrum[window + w];
                        var r = right.Spectrum[window + w];
                        for (int k = info.Offsets[band]; k < info.Offsets[band + 1]; k++)
                        {
                            var mid = l[k];
                            var side = r[k];
                            l[k] = mid + side;
                            r[k] = mid - side;
                        }
                    }
                }

                window += info.GroupLengths[group];
            }
        }

        private static void CopyNoise(AacIcs left, AacIcs right, AacIcsInfo info, int group, int window, int band, int index)
        {
            var ratio = Math.Pow(2, (right.Scalefactors[index] - left.Scalefactors[index]) / 4.0);
            for (var w = 0; w < info.GroupLengths[group]; w++)
            {
                var l = left.Spectrum[window + w];
                var r = right.Spectrum[window + w];
                for (int k = info.Offsets[band]; k < info.Offsets[band + 1]; k++)
                {
                    r[k] = l[k] * ratio;
                }
            }
        }

        private static void ApplyIntensity(AacIcs left, AacIcs right, bool[]? msUsed)
        {
            var info = right.Info;
            var window = 0;
            for (var group = 0; group < info.GroupCount; group++)
            {
                for (var band = 0; band < info.MaxSfb; band++)
                {
                    var index = (group * info.MaxSfb) + band;
                    var bandType = right.BandTypes[index];
                    if (bandType is not (IntensityOutOfPhaseBandType or IntensityInPhaseBandType))
                    {
                        continue;
                    }

                    var sign = bandType == IntensityInPhaseBandType ? 1.0 : -1.0;
                    if (msUsed is not null && msUsed[index])
                    {
                        sign = -sign;
                    }

                    var scale = sign * Math.Pow(0.5, right.Scalefactors[index] / 4.0);
                    for (var w = 0; w < info.GroupLengths[group]; w++)
                    {
                        var l = left.Spectrum[window + w];
                        var r = right.Spectrum[window + w];
                        for (int k = info.Offsets[band]; k < info.Offsets[band + 1]; k++)
                        {
                            r[k] = l[k] * scale;
                        }
                    }
                }

                window += info.GroupLengths[group];
            }
        }

        private void ApplyTns(AacIcs ics)
        {
            if (ics.Tns is null)
            {
                return;
            }

            var info = ics.Info;
            var maxBands = Math.Min(info.IsShort ? _tables.TnsMaxBandsShortWindow : _tables.TnsMaxBandsLongWindow, info.MaxSfb);
            for (var w = 0; w < info.WindowCount; w++)
            {
                var top = info.Offsets.Length - 1;
                var coefficients = ics.Spectrum[w];
                foreach (var filter in ics.Tns[w])
                {
                    var bottom = Math.Max(top - filter.Length, 0);
                    var order = filter.Lpc.Length;
                    if (order > 0)
                    {
                        var start = info.Offsets[Math.Min(bottom, maxBands)];
                        var end = info.Offsets[Math.Min(top, maxBands)];
                        var size = end - start;
                        if (size > 0)
                        {
                            var increment = filter.Downward ? -1 : 1;
                            var first = filter.Downward ? end - 1 : start;
                            for (var m = 0; m < size; m++)
                            {
                                var position = first + (m * increment);
                                var sum = coefficients[position];
                                for (var i = 1; i <= Math.Min(m, order); i++)
                                {
                                    sum -= filter.Lpc[i - 1] * coefficients[position - (i * increment)];
                                }

                                coefficients[position] = sum;
                            }
                        }
                    }

                    top = bottom;
                }
            }
        }

        private double[] Synthesize(AacIcs ics, AacChannelState state)
        {
            var info = ics.Info;
            var previousShape = state.PreviousWindowShape;
            var shape = info.WindowShape;
            var windowed = new double[BlockSize];
            if (info.IsShort)
            {
                for (var w = 0; w < ShortWindowCount; w++)
                {
                    var time = Mdct.Inverse(ics.Spectrum[w]);
                    var risingShape = w == 0 ? previousShape : shape;
                    var start = ShortWindowStartOffset + (w * ShortCoefficientCount);
                    for (var n = 0; n < ShortBlockSize; n++)
                    {
                        var window = n < ShortCoefficientCount ? _shortWindows[risingShape][n] : _shortWindows[shape][n];
                        windowed[start + n] += time[n] * window;
                    }
                }
            }
            else
            {
                var time = Mdct.Inverse(ics.Spectrum[0]);
                for (var n = 0; n < BlockSize; n++)
                {
                    windowed[n] = time[n] * LongWindowAt(info.WindowSequence, previousShape, shape, n);
                }
            }

            var output = new double[FrameSamples];
            for (var n = 0; n < FrameSamples; n++)
            {
                output[n] = windowed[n] + state.Overlap[n];
                state.Overlap[n] = windowed[FrameSamples + n];
            }

            state.PreviousWindowShape = shape;
            return output;
        }

        private double LongWindowAt(int windowSequence, int previousShape, int shape, int n)
        {
            if (n < FrameSamples)
            {
                if (windowSequence != LongStopSequence)
                {
                    return _longWindows[previousShape][n];
                }

                return n < ShortWindowStartOffset ? 0.0
                    : n < ShortWindowStartOffset + ShortCoefficientCount ? _shortWindows[previousShape][n - ShortWindowStartOffset]
                    : 1.0;
            }

            if (windowSequence != LongStartSequence)
            {
                return _longWindows[shape][n];
            }

            var offset = n - FrameSamples;
            return offset < ShortWindowStartOffset ? 1.0
                : offset < ShortWindowStartOffset + ShortCoefficientCount ? _shortWindows[shape][ShortCoefficientCount + offset - ShortWindowStartOffset]
                : 0.0;
        }

        private static void SkipDataStreamElement(BitReader reader)
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

        private static void SkipFillElement(BitReader reader)
        {
            var count = (int)reader.ReadBits(4);
            if (count == 15)
            {
                count += (int)reader.ReadBits(8) - 1;
            }

            if (count > 0)
            {
                var extensionType = (int)reader.PeekBits(4);
                if (extensionType is SbrExtensionType or SbrCrcExtensionType)
                {
                    throw new NotSupportedException("HE-AAC (SBR) is not supported; only AAC-LC is");
                }
            }

            reader.SkipBits(count * 8);
        }

        private static double[] BuildPowFourThirds()
        {
            var table = new double[MaxQuantizedMagnitude + 1];
            for (var i = 0; i < table.Length; i++)
            {
                table[i] = Math.Pow(i, 4.0 / 3.0);
            }

            return table;
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

        private static double[] BuildKbdWindow(double alpha, int halfLength)
        {
            var kernel = new double[halfLength + 1];
            var total = 0.0;
            for (var i = 0; i <= halfLength; i++)
            {
                var ratio = (2.0 * i / halfLength) - 1.0;
                kernel[i] = BesselI0(Math.PI * alpha * Math.Sqrt(1.0 - (ratio * ratio)));
                total += kernel[i];
            }

            var window = new double[halfLength * 2];
            var running = 0.0;
            for (var n = 0; n < halfLength; n++)
            {
                running += kernel[n];
                window[n] = Math.Sqrt(running / total);
                window[(2 * halfLength) - 1 - n] = window[n];
            }

            return window;
        }

        private static double BesselI0(double x)
        {
            var sum = 1.0;
            var term = 1.0;
            var halfX = x / 2;
            for (var k = 1; k < 60; k++)
            {
                term *= halfX / k;
                var squared = term * term;
                sum += squared;
                if (squared < sum * 1e-17)
                {
                    break;
                }
            }

            return sum;
        }

        private sealed class AacChannelState
        {
            public double[] Overlap { get; } = new double[FrameSamples];

            public int PreviousWindowShape { get; set; }
        }
    }

    internal sealed class AacIcsInfo(int windowSequence, int windowShape, int maxSfb, int[] groupLengths, ushort[] offsets)
    {
        public int WindowSequence { get; } = windowSequence;

        public int WindowShape { get; } = windowShape;

        public int MaxSfb { get; } = maxSfb;

        public int[] GroupLengths { get; } = groupLengths;

        public ushort[] Offsets { get; } = offsets;

        public bool IsShort => WindowSequence == 2;

        public int GroupCount => GroupLengths.Length;

        public int WindowCount => IsShort ? 8 : 1;
    }

    internal sealed record AacIcs(AacIcsInfo Info, int[] BandTypes, int[] Scalefactors, double[][] Spectrum, AacTnsFilter[][]? Tns);

    internal sealed record AacPulse(int Start, int[] Offsets, int[] Amplitudes);

    internal sealed record AacTnsFilter(int Length, bool Downward, double[] Lpc);
}
