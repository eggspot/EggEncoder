using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    internal sealed class AacFrameEncoder : IDisposable
    {
        internal const int CoefficientCount = 1024;
        internal const int DefaultBitRatePerChannel = 96_000;

        private const int BlockSize = CoefficientCount * 2;
        private const int MaxBitsPerChannelFrame = 6144;
        private const int MaxScalefactor = 255;
        private const int MaxScalefactorDelta = 60;
        private const int MaxQuantized = 8191;
        private const double QuantizationRounding = 0.4054;
        private const double FullScaleDecibels = 96.0;
        private const uint SingleChannelElementId = 0;
        private const uint ChannelPairElementId = 1;
        private const uint EndElementId = 7;
        private const int EscapeCodebook = 11;
        private const double SpectrumScale = 2.0;
        private const double ToneMaskingDecibels = 26.0;
        private const double NoiseMaskingDecibels = 8.0;
        private const double NoiseSubstitutionFlatness = 0.6;
        private const double WhiteNoiseFlatness = 0.28;
        private const int NoiseSubstitutionStartHertz = 4000;
        private const int NoiseBandType = 13;
        private const int NoiseEnergyOffset = 90;
        private const int NoisePreambleBias = 256;

        private static readonly int[] FirstCodebookForMagnitude = [0, 1, 3, 5, 5, 7, 7, 7, 9, 9, 9, 9, 9];

        private readonly Action<byte[], int, long> _onFrameEncoded;
        private readonly int _channels;
        private readonly ushort[] _offsets;
        private readonly int _maxSfb;
        private readonly int _noiseStartBand;
        private readonly double[] _window;
        private readonly double[] _bandHearingThreshold;
        private readonly int _averageFrameBits;
        private readonly int _maxReservoirBits;
        private readonly double[][] _previousBlocks;
        private readonly double[][] _currentBlocks;
        private int _currentFrameCount;
        private int _sampleIndexInFrame;
        private int _reservoirBits;
        private long _frameIndex;

        /// <param name="channels">1 or 2.</param>
        /// <param name="sampleRate">A standard MPEG-4 AAC sample rate.</param>
        /// <param name="onFrameEncoded">
        /// Invoked once per frame with the raw access unit (no ADTS), the number of real samples per
        /// channel it holds (1024 except a final zero-padded one from <see cref="Flush"/>) and a
        /// 0-based frame index.
        /// </param>
        /// <param name="bitRate">Target bit rate for all channels together, in bits per second; 0 uses 96 kb/s per channel.</param>
        public AacFrameEncoder(int channels, int sampleRate, Action<byte[], int, long> onFrameEncoded, int bitRate = 0)
        {
            ValidateAndGetSampleRateIndex(channels, sampleRate);
            if (bitRate < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bitRate), bitRate, "The bit rate must not be negative");
            }

            var totalBitRate = bitRate == 0 ? DefaultBitRatePerChannel * channels : bitRate;
            _onFrameEncoded = onFrameEncoded;
            _channels = channels;
            _offsets = AacBandTables.ForSampleRate(sampleRate).LongOffsets;
            _averageFrameBits = (int)Math.Clamp((long)totalBitRate * CoefficientCount / sampleRate, 64L * channels, (long)MaxBitsPerChannelFrame * channels);
            _maxReservoirBits = (MaxBitsPerChannelFrame * channels) - _averageFrameBits;
            _maxSfb = CountBandsBelow(Bandwidth(totalBitRate / channels, sampleRate), sampleRate);
            _window = new double[BlockSize];
            for (var n = 0; n < BlockSize; n++)
            {
                _window[n] = Math.Sin(Math.PI / BlockSize * (n + 0.5));
            }

            _bandHearingThreshold = BuildHearingThreshold(sampleRate);
            _noiseStartBand = CountBandsBelow(NoiseSubstitutionStartHertz, sampleRate);
            _previousBlocks = new double[channels][];
            _currentBlocks = new double[channels][];
            for (var c = 0; c < channels; c++)
            {
                _previousBlocks[c] = new double[CoefficientCount];
                _currentBlocks[c] = new double[CoefficientCount];
            }
        }

        public void WriteSample(short sample)
        {
            _currentBlocks[_sampleIndexInFrame % _channels][_currentFrameCount] = sample;
            _sampleIndexInFrame++;
            if (_sampleIndexInFrame % _channels != 0)
            {
                return;
            }

            _currentFrameCount++;
            if (_currentFrameCount < CoefficientCount)
            {
                return;
            }

            EncodeAndEmitFrame();
            for (var c = 0; c < _channels; c++)
            {
                (_previousBlocks[c], _currentBlocks[c]) = (_currentBlocks[c], _previousBlocks[c]);
                Array.Clear(_currentBlocks[c]);
            }

            _currentFrameCount = 0;
            _sampleIndexInFrame = 0;
        }

        public void Flush()
        {
            if (_currentFrameCount == 0)
            {
                return;
            }

            EncodeAndEmitFrame();
            _currentFrameCount = 0;
            _sampleIndexInFrame = 0;
        }

        public void Dispose()
        {
        }

        internal static int ValidateAndGetSampleRateIndex(int channels, int sampleRate)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException("Only mono and stereo AAC encoding are supported");
            }

            var sampleRateIndex = Array.IndexOf(AacTables.SampleRates, sampleRate);
            if (sampleRateIndex < 0)
            {
                throw new NotSupportedException($"Sample rate {sampleRate} is not a valid MPEG-4 AAC sample rate");
            }

            return sampleRateIndex;
        }

        internal static int Bandwidth(int bitRatePerChannel, int sampleRate)
        {
            var bandwidth = bitRatePerChannel switch
            {
                >= 128_000 => 20_000,
                >= 96_000 => 17_000,
                >= 64_000 => 14_000,
                >= 48_000 => 12_000,
                >= 32_000 => 10_000,
                _ => 7_000
            };

            return Math.Min(bandwidth, sampleRate / 2);
        }

        private int CountBandsBelow(int bandwidth, int sampleRate)
        {
            var limit = (int)((long)bandwidth * BlockSize / sampleRate);
            var bands = 0;
            while (bands < _offsets.Length - 1 && _offsets[bands + 1] <= limit)
            {
                bands++;
            }

            return Math.Max(bands, 1);
        }

        private double[] BuildHearingThreshold(int sampleRate)
        {
            var fullScale = new double[BlockSize];
            var frequency = 1000.0;
            for (var n = 0; n < BlockSize; n++)
            {
                fullScale[n] = short.MaxValue * Math.Sin(2 * Math.PI * frequency * n / sampleRate) * _window[n];
            }

            var spectrum = Mdct.Forward(fullScale);
            var fullScaleEnergy = 0.0;
            foreach (var coefficient in spectrum)
            {
                fullScaleEnergy += coefficient * coefficient * SpectrumScale * SpectrumScale;
            }

            var thresholds = new double[_offsets.Length - 1];
            for (var band = 0; band < thresholds.Length; band++)
            {
                var lowest = double.MaxValue;
                for (int k = _offsets[band]; k < _offsets[band + 1]; k++)
                {
                    var hertz = Math.Max((k + 0.5) * sampleRate / BlockSize, 20.0) / 1000.0;
                    var decibels = (3.64 * Math.Pow(hertz, -0.8)) - (6.5 * Math.Exp(-0.6 * Math.Pow(hertz - 3.3, 2))) + (0.001 * Math.Pow(hertz, 4));
                    lowest = Math.Min(lowest, decibels);
                }

                thresholds[band] = fullScaleEnergy * Math.Pow(10, (lowest - FullScaleDecibels) / 10) * (_offsets[band + 1] - _offsets[band]);
            }

            return thresholds;
        }

        private void EncodeAndEmitFrame()
        {
            var spectra = new double[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                var windowed = new double[BlockSize];
                for (var n = 0; n < CoefficientCount; n++)
                {
                    windowed[n] = _previousBlocks[c][n] * _window[n];
                    windowed[CoefficientCount + n] = _currentBlocks[c][n] * _window[CoefficientCount + n];
                }

                spectra[c] = Mdct.Forward(windowed);
                for (var k = 0; k < CoefficientCount; k++)
                {
                    spectra[c][k] *= SpectrumScale;
                }
            }

            var thresholds = new double[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                thresholds[c] = AllowedNoise(spectra[c]);
            }

            var midSide = _channels == 2 ? ChooseMidSide(spectra[0], spectra[1]) : null;
            if (midSide is not null)
            {
                ApplyMidSide(spectra[0], spectra[1], midSide);
                for (var band = 0; band < _maxSfb; band++)
                {
                    if (midSide[band])
                    {
                        var shared = Math.Min(thresholds[0][band], thresholds[1][band]) / 2;
                        thresholds[0][band] = shared;
                        thresholds[1][band] = shared;
                    }
                }
            }

            var noise = new int?[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                noise[c] = NoiseBands(spectra[c], midSide);
            }

            var budget = Math.Min(_averageFrameBits + (_reservoirBits / 2), MaxBitsPerChannelFrame * _channels);
            var plan = FindFinestFitting(spectra, thresholds, noise, budget);
            var payload = Write(plan, midSide);
            _reservoirBits = Math.Clamp(_reservoirBits + _averageFrameBits - (payload.Length * 8), 0, _maxReservoirBits);
            _onFrameEncoded(payload, _currentFrameCount, _frameIndex++);
        }

        private bool[] ChooseMidSide(double[] left, double[] right)
        {
            var useMidSide = new bool[_maxSfb];
            for (var band = 0; band < _maxSfb; band++)
            {
                double energyLeft = 0, energyRight = 0, energySide = 0;
                for (int k = _offsets[band]; k < _offsets[band + 1]; k++)
                {
                    energyLeft += left[k] * left[k];
                    energyRight += right[k] * right[k];
                    var side = (left[k] - right[k]) / 2;
                    energySide += side * side;
                }

                useMidSide[band] = energySide < 0.1 * Math.Min(energyLeft, energyRight);
            }

            return useMidSide;
        }

        private void ApplyMidSide(double[] left, double[] right, bool[] useMidSide)
        {
            for (var band = 0; band < _maxSfb; band++)
            {
                if (!useMidSide[band])
                {
                    continue;
                }

                for (int k = _offsets[band]; k < _offsets[band + 1]; k++)
                {
                    var mid = (left[k] + right[k]) / 2;
                    var side = (left[k] - right[k]) / 2;
                    left[k] = mid;
                    right[k] = side;
                }
            }
        }

        private int?[] NoiseBands(double[] spectrum, bool[]? midSide)
        {
            var noise = new int?[_maxSfb];
            for (var band = _noiseStartBand; band < _maxSfb; band++)
            {
                if (midSide is not null && midSide[band])
                {
                    continue;
                }

                var energy = 0.0;
                var logSum = 0.0;
                var width = _offsets[band + 1] - _offsets[band];
                for (int k = _offsets[band]; k < _offsets[band + 1]; k++)
                {
                    var power = spectrum[k] * spectrum[k];
                    energy += power;
                    logSum += Math.Log(power + 1e-9);
                }

                if (energy <= _bandHearingThreshold[band] || Math.Exp(logSum / width) / (energy / width) / WhiteNoiseFlatness < NoiseSubstitutionFlatness)
                {
                    continue;
                }

                noise[band] = (int)Math.Round(2 * Math.Log2(energy));
            }

            return noise;
        }

        private FramePlan FindFinestFitting(double[][] spectra, double[][] thresholds, int?[][] noise, int budget)
        {
            var low = -64;
            var high = 128;
            var best = Plan(spectra, thresholds, noise, high);
            if (best.Bits > budget)
            {
                return best;
            }

            while (high - low > 1)
            {
                var middle = (low + high) / 2;
                var candidate = Plan(spectra, thresholds, noise, middle);
                if (candidate.Bits <= budget)
                {
                    high = middle;
                    best = candidate;
                }
                else
                {
                    low = middle;
                }
            }

            return best;
        }

        private double[] AllowedNoise(double[] spectrum)
        {
            var thresholds = new double[_maxSfb];
            for (var band = 0; band < _maxSfb; band++)
            {
                var energy = 0.0;
                var logSum = 0.0;
                var width = _offsets[band + 1] - _offsets[band];
                for (int k = _offsets[band]; k < _offsets[band + 1]; k++)
                {
                    var power = spectrum[k] * spectrum[k];
                    energy += power;
                    logSum += Math.Log(power + 1e-9);
                }

                var flatness = energy <= 0 ? 1.0 : Math.Exp(logSum / width) / (energy / width);
                var maskingDecibels = NoiseMaskingDecibels + ((ToneMaskingDecibels - NoiseMaskingDecibels) * (1 - Math.Clamp(flatness / WhiteNoiseFlatness, 0, 1)));
                thresholds[band] = Math.Max(energy * Math.Pow(10, -maskingDecibels / 10), _bandHearingThreshold[band]);
            }

            return thresholds;
        }

        private FramePlan Plan(double[][] spectra, double[][] thresholds, int?[][] noise, int offset)
        {
            var channels = new ChannelPlan[_channels];
            var bits = _channels == 2 ? 3 + 4 + 1 + 11 + 2 + _maxSfb : 3 + 4 + 11;
            for (var c = 0; c < _channels; c++)
            {
                channels[c] = PlanChannel(spectra[c], thresholds[c], noise[c], offset);
                bits += channels[c].Bits;
            }

            return new FramePlan(channels, bits + 3 + 7);
        }

        private ChannelPlan PlanChannel(double[] spectrum, double[] thresholds, int?[] noise, int offset)
        {
            var scalefactors = new int[_maxSfb];
            var codebooks = new int[_maxSfb];
            var quantized = new int[CoefficientCount];
            var previous = -1;
            for (var band = 0; band < _maxSfb; band++)
            {
                if (noise[band] is not null)
                {
                    codebooks[band] = NoiseBandType;
                    continue;
                }

                var start = _offsets[band];
                var end = _offsets[band + 1];
                var width = end - start;
                var target = 100 + (int)Math.Round(2 * Math.Log2(Math.Max(thresholds[band] / width, 1e-9))) + offset;
                var peak = 0.0;
                for (var k = start; k < end; k++)
                {
                    peak = Math.Max(peak, Math.Abs(spectrum[k]));
                }

                var minimum = peak <= 0 ? 0 : (int)Math.Ceiling(100 + (4 * Math.Log2(peak / Math.Pow(MaxQuantized, 4.0 / 3.0))));
                var scalefactor = Math.Clamp(Math.Max(target, minimum), 0, MaxScalefactor);
                if (previous >= 0)
                {
                    scalefactor = Math.Clamp(scalefactor, previous - MaxScalefactorDelta, previous + MaxScalefactorDelta);
                    scalefactor = Math.Max(scalefactor, minimum);
                }

                var inverseGain = Math.Pow(2, -(scalefactor - 100) / 4.0);
                var maxMagnitude = 0;
                for (var k = start; k < end; k++)
                {
                    var magnitude = (int)Math.Min(MaxQuantized, Math.Floor(Math.Pow(Math.Abs(spectrum[k]) * inverseGain, 0.75) + QuantizationRounding));
                    quantized[k] = spectrum[k] < 0 ? -magnitude : magnitude;
                    maxMagnitude = Math.Max(maxMagnitude, magnitude);
                }

                if (maxMagnitude == 0)
                {
                    codebooks[band] = 0;
                    continue;
                }

                if (previous >= 0 && Math.Abs(scalefactor - previous) > MaxScalefactorDelta)
                {
                    codebooks[band] = 0;
                    Array.Clear(quantized, start, width);
                    continue;
                }

                scalefactors[band] = scalefactor;
                codebooks[band] = BestCodebook(quantized, start, end, maxMagnitude);
                previous = scalefactor;
            }

            var globalGain = FirstScalefactor(codebooks, scalefactors);
            var noiseEnergy = globalGain - NoiseEnergyOffset;
            var firstNoise = true;
            for (var band = 0; band < _maxSfb; band++)
            {
                if (codebooks[band] != NoiseBandType)
                {
                    continue;
                }

                var wanted = Math.Clamp(noise[band]!.Value, -100, 155);
                noiseEnergy = firstNoise
                    ? Math.Clamp(wanted, noiseEnergy - NoisePreambleBias, noiseEnergy + NoisePreambleBias - 1)
                    : Math.Clamp(wanted, noiseEnergy - MaxScalefactorDelta, noiseEnergy + MaxScalefactorDelta);
                scalefactors[band] = noiseEnergy;
                firstNoise = false;
            }

            var bits = 8 + SectionBits(codebooks) + ScalefactorBits(codebooks, scalefactors, globalGain) + 3;
            for (var band = 0; band < _maxSfb; band++)
            {
                if (codebooks[band] is not 0 and not NoiseBandType)
                {
                    bits += SpectralBits(AacTables.SpectralCodebooks[codebooks[band] - 1], quantized, _offsets[band], _offsets[band + 1]);
                }
            }

            return new ChannelPlan(scalefactors, codebooks, quantized, bits);
        }

        private static int BestCodebook(int[] quantized, int start, int end, int maxMagnitude)
        {
            if (maxMagnitude > 12)
            {
                return EscapeCodebook;
            }

            var best = -1;
            var bestBits = int.MaxValue;
            for (var codebook = FirstCodebookForMagnitude[maxMagnitude]; codebook <= EscapeCodebook; codebook++)
            {
                var bits = SpectralBits(AacTables.SpectralCodebooks[codebook - 1], quantized, start, end);
                if (bits < bestBits)
                {
                    bestBits = bits;
                    best = codebook;
                }
            }

            return best;
        }

        private static int SpectralBits(SpectralCodebook codebook, int[] quantized, int start, int end)
        {
            var bits = 0;
            for (var position = start; position < end; position += codebook.GroupSize)
            {
                bits += codebook.Huffman.GetCode(FlatIndex(codebook, quantized, position)).Length;
                if (codebook.SignedInTable)
                {
                    continue;
                }

                for (var j = 0; j < codebook.GroupSize; j++)
                {
                    var magnitude = Math.Abs(quantized[position + j]);
                    if (magnitude != 0)
                    {
                        bits++;
                    }

                    if (codebook.HasEscape && magnitude >= codebook.Lav)
                    {
                        var exponent = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)magnitude);
                        bits += (2 * exponent) - 3;
                    }
                }
            }

            return bits;
        }

        private static int FlatIndex(SpectralCodebook codebook, int[] quantized, int position)
        {
            var digitBase = codebook.SignedInTable ? (2 * codebook.Lav) + 1 : codebook.Lav + 1;
            var index = 0;
            for (var j = 0; j < codebook.GroupSize; j++)
            {
                var value = quantized[position + j];
                var digit = codebook.SignedInTable ? value + codebook.Lav : Math.Min(Math.Abs(value), codebook.Lav);
                index = (index * digitBase) + digit;
            }

            return index;
        }

        private static int SectionBits(int[] codebooks)
        {
            var bits = 0;
            var band = 0;
            while (band < codebooks.Length)
            {
                var length = 1;
                while (band + length < codebooks.Length && codebooks[band + length] == codebooks[band])
                {
                    length++;
                }

                bits += 4 + (5 * ((length / 31) + 1));
                band += length;
            }

            return bits;
        }

        private static int FirstScalefactor(int[] codebooks, int[] scalefactors)
        {
            for (var band = 0; band < codebooks.Length; band++)
            {
                if (codebooks[band] is not 0 and not NoiseBandType)
                {
                    return scalefactors[band];
                }
            }

            return 100;
        }

        private static int ScalefactorBits(int[] codebooks, int[] scalefactors, int globalGain)
        {
            var bits = 0;
            var previous = globalGain;
            var previousNoise = globalGain - NoiseEnergyOffset;
            var firstNoise = true;
            for (var band = 0; band < codebooks.Length; band++)
            {
                if (codebooks[band] == 0)
                {
                    continue;
                }

                if (codebooks[band] == NoiseBandType)
                {
                    bits += firstNoise ? 9 : AacTables.ScalefactorHuffman.GetCode(scalefactors[band] - previousNoise + AacTables.ScaleDiffZero).Length;
                    previousNoise = scalefactors[band];
                    firstNoise = false;
                    continue;
                }

                bits += AacTables.ScalefactorHuffman.GetCode(scalefactors[band] - previous + AacTables.ScaleDiffZero).Length;
                previous = scalefactors[band];
            }

            return bits;
        }

        private byte[] Write(FramePlan plan, bool[]? midSide)
        {
            var writer = new BitWriter();
            if (_channels == 1)
            {
                writer.WriteBits(SingleChannelElementId, 3);
                writer.WriteBits(0, 4);
                WriteChannel(writer, plan.Channels[0], writeIcsInfo: true);
            }
            else
            {
                writer.WriteBits(ChannelPairElementId, 3);
                writer.WriteBits(0, 4);
                writer.WriteBits(1, 1);
                WriteIcsInfo(writer);
                writer.WriteBits(1, 2);
                for (var band = 0; band < _maxSfb; band++)
                {
                    writer.WriteBits(midSide![band] ? 1u : 0u, 1);
                }

                WriteChannel(writer, plan.Channels[0], writeIcsInfo: false);
                WriteChannel(writer, plan.Channels[1], writeIcsInfo: false);
            }

            writer.WriteBits(EndElementId, 3);
            writer.ByteAlign();
            return writer.ToArray();
        }

        private void WriteIcsInfo(BitWriter writer)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits((uint)_maxSfb, 6);
            writer.WriteBits(0, 1);
        }

        private void WriteChannel(BitWriter writer, ChannelPlan channel, bool writeIcsInfo)
        {
            var firstScalefactor = FirstScalefactor(channel.Codebooks, channel.Scalefactors);

            writer.WriteBits((uint)firstScalefactor, 8);
            if (writeIcsInfo)
            {
                WriteIcsInfo(writer);
            }

            var band2 = 0;
            while (band2 < _maxSfb)
            {
                var length = 1;
                while (band2 + length < _maxSfb && channel.Codebooks[band2 + length] == channel.Codebooks[band2])
                {
                    length++;
                }

                writer.WriteBits((uint)channel.Codebooks[band2], 4);
                var remaining = length;
                while (remaining >= 31)
                {
                    writer.WriteBits(31, 5);
                    remaining -= 31;
                }

                writer.WriteBits((uint)remaining, 5);
                band2 += length;
            }

            var previous = firstScalefactor;
            var previousNoise = firstScalefactor - NoiseEnergyOffset;
            var firstNoise = true;
            for (var band = 0; band < _maxSfb; band++)
            {
                if (channel.Codebooks[band] == 0)
                {
                    continue;
                }

                if (channel.Codebooks[band] == NoiseBandType)
                {
                    if (firstNoise)
                    {
                        writer.WriteBits((uint)(channel.Scalefactors[band] - previousNoise + NoisePreambleBias), 9);
                    }
                    else
                    {
                        var (noiseCode, noiseLength) = AacTables.ScalefactorHuffman.GetCode(channel.Scalefactors[band] - previousNoise + AacTables.ScaleDiffZero);
                        writer.WriteBits(noiseCode, noiseLength);
                    }

                    previousNoise = channel.Scalefactors[band];
                    firstNoise = false;
                    continue;
                }

                var (code, codeLength) = AacTables.ScalefactorHuffman.GetCode(channel.Scalefactors[band] - previous + AacTables.ScaleDiffZero);
                writer.WriteBits(code, codeLength);
                previous = channel.Scalefactors[band];
            }

            writer.WriteBits(0, 3);
            for (var band = 0; band < _maxSfb; band++)
            {
                if (channel.Codebooks[band] is 0 or NoiseBandType)
                {
                    continue;
                }

                var codebook = AacTables.SpectralCodebooks[channel.Codebooks[band] - 1];
                for (int position = _offsets[band]; position < _offsets[band + 1]; position += codebook.GroupSize)
                {
                    WriteCodeword(writer, codebook, channel.Quantized, position);
                }
            }
        }

        private static void WriteCodeword(BitWriter writer, SpectralCodebook codebook, int[] quantized, int position)
        {
            var (code, length) = codebook.Huffman.GetCode(FlatIndex(codebook, quantized, position));
            writer.WriteBits(code, length);
            if (codebook.SignedInTable)
            {
                return;
            }

            for (var j = 0; j < codebook.GroupSize; j++)
            {
                if (quantized[position + j] != 0)
                {
                    writer.WriteBits(quantized[position + j] < 0 ? 1u : 0u, 1);
                }
            }

            for (var j = 0; j < codebook.GroupSize; j++)
            {
                var magnitude = Math.Abs(quantized[position + j]);
                if (codebook.HasEscape && magnitude >= codebook.Lav)
                {
                    var exponent = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)magnitude);
                    var prefixLength = exponent - 4;
                    writer.WriteBits((1u << prefixLength) - 1, prefixLength);
                    writer.WriteBits(0, 1);
                    writer.WriteBits((uint)(magnitude - (1 << exponent)), exponent);
                }
            }
        }

        private sealed record ChannelPlan(int[] Scalefactors, int[] Codebooks, int[] Quantized, int Bits);

        private sealed record FramePlan(ChannelPlan[] Channels, int Bits);
    }
}
