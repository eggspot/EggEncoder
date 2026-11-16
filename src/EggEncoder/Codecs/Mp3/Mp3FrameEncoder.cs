using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    internal sealed class Mp3FrameEncoder
    {
        private const int GranuleCount = 2;
        private const int GranuleSamples = 576;
        private const int SubbandCount = 32;
        private const int PolyphaseTicksPerGranule = GranuleSamples / SubbandCount;
        private const int CoefficientsPerSubband = 18;
        private const int MdctWindowSize = CoefficientsPerSubband * 2;
        private const double ForwardMdctScale = 2.0 / CoefficientsPerSubband;

        private const double PcmNormalizationScale = 1.0 / 32768.0;

        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bitRateKbps;
        private readonly int _sampleRateIndex;
        private readonly int? _vbrQuality;
        private readonly int _bitrateIndex;
        private readonly int _sideInfoBytes;
        private readonly int[] _bandStarts;
        private int _streamingGainHint = NoGainHint;
        private readonly double[] _sineWindow;
        private readonly Mp3PolyphaseFilter[] _filters;
        private static readonly double[] MdctTable = Mdct.DirectTable(CoefficientsPerSubband)!;

        private readonly double[][][] _subbandHistory;
        private readonly GranuleScratch[] _scratch;

        private double _paddingAccumulator;

        internal const int VbrQualityCount = 10;
        internal const int NoGainHint = -1;

        private const double VbrEnergyFloorLog2 = -20;

        private static readonly double[] VbrRelativeGains = [172, 174, 176, 178, 180, 183, 186, 189, 192, 195];

        public Mp3FrameEncoder(int channels, int sampleRate, int bitRateKbps, int? vbrQuality = null)
        {
            _vbrQuality = vbrQuality;
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"MP3 encoding only supports mono or stereo, not {channels} channels");
            }

            _channels = channels;
            _sampleRate = sampleRate;
            _bitRateKbps = bitRateKbps;

            _sampleRateIndex = Array.IndexOf(Mp3Probe.Mpeg1SampleRates, sampleRate);
            if (_sampleRateIndex < 0 || Mp3Probe.Mpeg1SampleRates[_sampleRateIndex] < 0)
            {
                throw new NotSupportedException($"MP3 encoding only supports 32000/44100/48000 Hz, not {sampleRate} Hz");
            }

            _bitrateIndex = Array.IndexOf(Mp3Probe.Mpeg1Layer3BitrateKbps, bitRateKbps);
            if (_bitrateIndex <= 0 || Mp3Probe.Mpeg1Layer3BitrateKbps[_bitrateIndex] < 0)
            {
                throw new NotSupportedException($"'{bitRateKbps}' is not a valid MPEG-1 Layer III bitrate");
            }

            _sideInfoBytes = channels == 1 ? 17 : 32;
            _bandStarts = Mp3DecoderTables.LongBandStarts[_sampleRateIndex];
            _sineWindow = BuildSineWindow(MdctWindowSize);

            _filters = new Mp3PolyphaseFilter[channels];
            _scratch = new GranuleScratch[channels];
            for (var c = 0; c < channels; c++)
            {
                _scratch[c] = new GranuleScratch();
            }

            _subbandHistory = new double[channels][][];
            for (var c = 0; c < channels; c++)
            {
                _filters[c] = new Mp3PolyphaseFilter();
                _subbandHistory[c] = new double[SubbandCount][];
                for (var sb = 0; sb < SubbandCount; sb++)
                {
                    _subbandHistory[c][sb] = new double[CoefficientsPerSubband];
                }
            }
        }

        internal readonly record struct AnalyzedFrame(double[][][] GranuleXr, int FrameLengthBytes, int Padding);

        public byte[] EncodeFrame(ReadOnlySpan<int> interleavedSamples) => Encode(Analyze(interleavedSamples), ref _streamingGainHint);

        internal AnalyzedFrame Analyze(ReadOnlySpan<int> interleavedSamples)
        {
            var granuleXr = new double[GranuleCount][][];
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                granuleXr[gr] = new double[_channels][];
                for (var ch = 0; ch < _channels; ch++)
                {
                    granuleXr[gr][ch] = ComputeGranuleCoefficients(interleavedSamples, gr, ch);
                }
            }

            if (_vbrQuality is not null)
            {
                return new AnalyzedFrame(granuleXr, 0, 0);
            }

            var (frameLengthBytes, padding) = NextFrameLength();
            return new AnalyzedFrame(granuleXr, frameLengthBytes, padding);
        }

        internal AnalyzedFrame[] AnalyzeFrames(int[] samples, int offset, int frameCount, int maxDegreeOfParallelism)
        {
            var frameLength = GranuleCount * GranuleSamples * _channels;
            var granuleXr = new double[frameCount][][][];
            for (var f = 0; f < frameCount; f++)
            {
                granuleXr[f] = new double[GranuleCount][][];
                for (var gr = 0; gr < GranuleCount; gr++)
                {
                    granuleXr[f][gr] = new double[_channels][];
                }
            }

            void AnalyzeChannel(int channel)
            {
                for (var f = 0; f < frameCount; f++)
                {
                    var frameSamples = samples.AsSpan(offset + (f * frameLength), frameLength);
                    for (var gr = 0; gr < GranuleCount; gr++)
                    {
                        granuleXr[f][gr][channel] = ComputeGranuleCoefficients(frameSamples, gr, channel);
                    }
                }
            }

            if (_channels == 1 || maxDegreeOfParallelism == 1)
            {
                for (var channel = 0; channel < _channels; channel++)
                {
                    AnalyzeChannel(channel);
                }
            }
            else
            {
                Parallel.For(0, _channels, AnalyzeChannel);
            }

            var frames = new AnalyzedFrame[frameCount];
            for (var f = 0; f < frameCount; f++)
            {
                var (frameLengthBytes, padding) = _vbrQuality is null ? NextFrameLength() : (0, 0);
                frames[f] = new AnalyzedFrame(granuleXr[f], frameLengthBytes, padding);
            }

            return frames;
        }

        internal byte[] Encode(AnalyzedFrame frame)
        {
            var noHint = NoGainHint;
            return Encode(frame, ref noHint);
        }

        internal byte[] Encode(AnalyzedFrame frame, ref int gainHint)
        {
            var midSide = _channels == 2 && PrefersMidSide(frame.GranuleXr);
            var granuleXr = midSide ? ToMidSide(frame.GranuleXr) : frame.GranuleXr;
            int frameLengthBytes;
            int bitrateIndex;
            Mp3Quantizer.Plan[] plans;
            if (_vbrQuality is { } quality)
            {
                (frameLengthBytes, plans, bitrateIndex) = ChooseVbrFrame(granuleXr, quality, ref gainHint);
            }
            else
            {
                frameLengthBytes = frame.FrameLengthBytes;
                bitrateIndex = _bitrateIndex;
                plans = PlanFrame(granuleXr, frameLengthBytes, ref gainHint);
            }

            var mainDataBytes = frameLengthBytes - 4 - _sideInfoBytes;

            var writer = new BitWriter();
            WriteHeader(writer, frame.Padding, bitrateIndex, midSide);
            WriteSideInfo(writer, plans);

            var usedBits = 0;
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    Mp3Quantizer.Write(writer, plans[(gr * _channels) + ch], _bandStarts);
                    usedBits += plans[(gr * _channels) + ch].BitCount;
                }
            }

            var totalMainDataBits = mainDataBytes * 8;
            var ancillaryBits = totalMainDataBits - usedBits;
            if (ancillaryBits > 0)
            {
                writer.WriteBits(0, ancillaryBits);
            }

            return writer.ToArray();
        }

        internal double[] ComputeGranuleCoefficients(ReadOnlySpan<int> interleavedSamples, int granule, int channel)
        {
            var granuleOffset = (granule * GranuleSamples * _channels) + channel;
            var scratch = _scratch[channel];
            var perSubband = scratch.PerSubband;
            var tick = scratch.Tick;
            for (var t = 0; t < PolyphaseTicksPerGranule; t++)
            {
                for (var s = 0; s < SubbandCount; s++)
                {
                    var sampleIndex = granuleOffset + (((t * SubbandCount) + s) * _channels);
                    tick[s] = interleavedSamples[sampleIndex];
                }

                var subbandSamples = _filters[channel].Analyze(tick);
                for (var sb = 0; sb < SubbandCount; sb++)
                {
                    perSubband[(sb * PolyphaseTicksPerGranule) + t] = subbandSamples[sb];
                }
            }

            for (var sb = 1; sb < SubbandCount; sb += 2)
            {
                for (var t = 1; t < PolyphaseTicksPerGranule; t += 2)
                {
                    perSubband[(sb * PolyphaseTicksPerGranule) + t] = -perSubband[(sb * PolyphaseTicksPerGranule) + t];
                }
            }

            var xr = new double[GranuleSamples];
            var windowed = scratch.Windowed;
            var coefficients = scratch.Coefficients;
            for (var sb = 0; sb < SubbandCount; sb++)
            {
                var history = _subbandHistory[channel][sb];
                var current = perSubband.AsSpan(sb * PolyphaseTicksPerGranule, PolyphaseTicksPerGranule);
                for (var i = 0; i < CoefficientsPerSubband; i++)
                {
                    windowed[i] = history[i] * _sineWindow[i];
                    windowed[CoefficientsPerSubband + i] = current[i] * _sineWindow[CoefficientsPerSubband + i];
                }

                Mdct.DirectForwardFromTable(windowed, coefficients, MdctTable);

                for (var k = 0; k < CoefficientsPerSubband; k++)
                {
                    coefficients[k] *= ForwardMdctScale;
                }

                Array.Copy(coefficients, 0, xr, sb * CoefficientsPerSubband, CoefficientsPerSubband);

                current.CopyTo(history);
            }

            for (var i = 0; i < GranuleSamples; i++)
            {
                xr[i] *= PcmNormalizationScale;
            }

            Mp3AliasReduction.Encode(xr);

            return xr;
        }

        private (int FrameLengthBytes, int Padding) NextFrameLength()
        {
            var exactLength = 144000.0 * _bitRateKbps / _sampleRate;
            var baseLength = (int)exactLength;
            _paddingAccumulator += exactLength - baseLength;

            if (_paddingAccumulator >= 1.0)
            {
                _paddingAccumulator -= 1.0;
                return (baseLength + 1, 1);
            }

            return (baseLength, 0);
        }

        internal void WriteHeader(BitWriter writer, int padding, int bitrateIndex, bool midSide = false)
        {
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)bitrateIndex, 4);
            writer.WriteBits((uint)_sampleRateIndex, 2);
            writer.WriteBits((uint)padding, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(_channels == 1 ? 0b11u : 0b01u, 2);
            writer.WriteBits(midSide ? 0b10u : 0u, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 2);
        }

        internal void WriteSideInfo(BitWriter writer, Mp3Quantizer.Plan[] plans)
        {
            writer.WriteBits(0, 9);
            writer.WriteBits(0, _channels == 1 ? 5 : 3);

            for (var ch = 0; ch < _channels; ch++)
            {
                writer.WriteBits(0, 4);
            }

            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    var plan = plans[(gr * _channels) + ch];
                    writer.WriteBits((uint)plan.BitCount, 12);
                    writer.WriteBits((uint)plan.BigValues, 9);
                    writer.WriteBits((uint)plan.GlobalGain, 8);
                    writer.WriteBits(0, 4);
                    writer.WriteBits(0, 1);
                    writer.WriteBits((uint)plan.Division.Table0, 5);
                    writer.WriteBits((uint)plan.Division.Table1, 5);
                    writer.WriteBits((uint)plan.Division.Table2, 5);
                    writer.WriteBits((uint)plan.Division.Region0Count, 4);
                    writer.WriteBits((uint)plan.Division.Region1Count, 3);
                    writer.WriteBits(0, 1);
                    writer.WriteBits(0, 1);
                    writer.WriteBits((uint)plan.Count1Table, 1);
                }
            }
        }

        private const string InfoTagVersionString = "LavfEgg\0\0";

        internal byte[] BuildInfoTagFrame(int frameCount, int byteCount, int startPadSamples, int endPadSamples, byte[]? seekTable = null)
        {
            var frameLengthBytes = (144000 * _bitRateKbps) / _sampleRate;

            var writer = new BitWriter();
            WriteHeader(writer, padding: 0, _bitrateIndex);
            writer.WriteBits(0, _sideInfoBytes * 8);

            foreach (var c in seekTable is null ? "Info" : "Xing")
            {
                writer.WriteBits(c, 8);
            }

            writer.WriteBits(seekTable is null ? 0x03u : 0x07u, 32);
            writer.WriteBits((uint)frameCount, 32);
            writer.WriteBits((uint)byteCount, 32);
            if (seekTable is not null)
            {
                foreach (var entry in seekTable)
                {
                    writer.WriteBits(entry, 8);
                }
            }

            if (InfoTagVersionString.Length != 9)
            {
                throw new InvalidOperationException($"InfoTagVersionString must be exactly 9 characters, was {InfoTagVersionString.Length}");
            }

            foreach (var c in InfoTagVersionString)
            {
                writer.WriteBits(c, 8);
            }

            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 32);
            writer.WriteBits(0, 16);
            writer.WriteBits(0, 16);
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);

            var delays = ((uint)startPadSamples << 12) | ((uint)endPadSamples & 0xFFF);
            writer.WriteBits(delays, 24);

            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 16);
            writer.WriteBits((uint)byteCount, 32);
            writer.WriteBits(0, 16);
            writer.WriteBits(0, 16);

            var writtenBytes = writer.BitPosition / 8;
            var remainingBytes = frameLengthBytes - writtenBytes;
            if (remainingBytes > 0)
            {
                writer.WriteBits(0, remainingBytes * 8);
            }

            return writer.ToArray();
        }

        private (int FrameLengthBytes, Mp3Quantizer.Plan[] Plans, int BitrateIndex) ChooseVbrFrame(double[][][] granuleXr, int quality, ref int gainHint)
        {
            var segments = Segments(granuleXr);
            var caches = NewCaches(segments.Length);
            var plans = new Mp3Quantizer.Plan[segments.Length];
            var needed = 0;
            for (var s = 0; s < segments.Length; s++)
            {
                var energy = 0.0;
                foreach (var value in segments[s])
                {
                    energy += value * value;
                }

                var target = VbrRelativeGains[quality] + (2 * Math.Max(Math.Log2(energy), VbrEnergyFloorLog2));
                var gain = Math.Clamp((int)Math.Floor(target), 0, Mp3Quantizer.MaxGlobalGain);
                plans[s] = CodablePlan(segments[s], caches[s], gain);
                needed += plans[s].BitCount;
            }

            var lastIndex = Array.FindLastIndex(Mp3Probe.Mpeg1Layer3BitrateKbps, rate => rate > 0);
            for (var index = 1; index <= lastIndex; index++)
            {
                var kbps = Mp3Probe.Mpeg1Layer3BitrateKbps[index];
                if (kbps <= 0)
                {
                    continue;
                }

                var frameLengthBytes = (144000 * kbps) / _sampleRate;
                var budget = MainDataBits(frameLengthBytes);
                if (needed <= budget)
                {
                    return (frameLengthBytes, Spend(segments, caches, plans, budget), index);
                }
            }

            var largestFrame = (144000 * Mp3Probe.Mpeg1Layer3BitrateKbps[lastIndex]) / _sampleRate;
            return (largestFrame, PlanFrame(granuleXr, largestFrame, ref gainHint), lastIndex);
        }

        private Mp3Quantizer.Plan[] PlanFrame(double[][][] granuleXr, int frameLengthBytes, ref int gainHint)
        {
            var segments = Segments(granuleXr);
            var caches = NewCaches(segments.Length);
            var budget = MainDataBits(frameLengthBytes);
            var low = 0;
            var high = Mp3Quantizer.MaxGlobalGain;
            Mp3Quantizer.Plan[]? best = null;
            if (gainHint != NoGainHint)
            {
                var start = Math.Clamp(gainHint, 0, Mp3Quantizer.MaxGlobalGain);
                if (PlansAt(segments, caches, start, budget) is { } fits)
                {
                    best = fits;
                    high = start - 1;
                    for (var step = 1; high >= 0; step *= 2)
                    {
                        var probe = Math.Max(0, start - step);
                        if (PlansAt(segments, caches, probe, budget) is not { } finer)
                        {
                            low = probe + 1;
                            break;
                        }

                        best = finer;
                        high = probe - 1;
                    }
                }
                else
                {
                    low = start + 1;
                    for (var step = 1; low <= high; step *= 2)
                    {
                        var probe = Math.Min(Mp3Quantizer.MaxGlobalGain, start + step);
                        if (PlansAt(segments, caches, probe, budget) is { } coarser)
                        {
                            best = coarser;
                            high = probe - 1;
                            break;
                        }

                        low = probe + 1;
                    }
                }
            }

            while (low <= high)
            {
                var gain = (low + high) / 2;
                if (PlansAt(segments, caches, gain, budget) is { } fitting)
                {
                    best = fitting;
                    high = gain - 1;
                }
                else
                {
                    low = gain + 1;
                }
            }

            if (best is null)
            {
                gainHint = NoGainHint;
                best = new Mp3Quantizer.Plan[segments.Length];
                for (var s = 0; s < segments.Length; s++)
                {
                    best[s] = CodablePlan(segments[s], caches[s], Mp3Quantizer.MaxGlobalGain);
                }

                return best;
            }

            gainHint = best[0].GlobalGain;
            return Spend(segments, caches, best, budget);
        }

        private int MainDataBits(int frameLengthBytes) => (frameLengthBytes - 4 - _sideInfoBytes) * 8;

        private double[][] Segments(double[][][] granuleXr)
        {
            var segments = new double[GranuleCount * _channels][];
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    segments[(gr * _channels) + ch] = granuleXr[gr][ch];
                }
            }

            return segments;
        }

        private static Mp3Quantizer.PlanCache[] NewCaches(int count)
        {
            var caches = new Mp3Quantizer.PlanCache[count];
            for (var s = 0; s < count; s++)
            {
                caches[s] = new Mp3Quantizer.PlanCache();
            }

            return caches;
        }

        private Mp3Quantizer.Plan[]? PlansAt(double[][] segments, Mp3Quantizer.PlanCache[] caches, int gain, int budget)
        {
            var plans = new Mp3Quantizer.Plan[segments.Length];
            var total = 0;
            for (var s = 0; s < segments.Length; s++)
            {
                var limit = Math.Min(budget - total, Mp3Quantizer.MaxSegmentBits);
                if (caches[s].Get(segments[s], gain, limit, _bandStarts) is not { } plan || plan.BitCount > limit)
                {
                    return null;
                }

                plans[s] = plan;
                total += plan.BitCount;
            }

            return plans;
        }

        private Mp3Quantizer.Plan CodablePlan(double[] segment, Mp3Quantizer.PlanCache cache, int gain)
        {
            for (; gain <= Mp3Quantizer.MaxGlobalGain; gain++)
            {
                if (cache.Get(segment, gain, int.MaxValue, _bandStarts) is { } plan)
                {
                    var best = Mp3Quantizer.WithBestDivision(plan, _bandStarts);
                    if (best.BitCount <= Mp3Quantizer.MaxSegmentBits)
                    {
                        return best;
                    }
                }
            }

            return Mp3Quantizer.EmptyPlan(Mp3Quantizer.MaxGlobalGain);
        }

        private Mp3Quantizer.Plan[] Spend(double[][] segments, Mp3Quantizer.PlanCache[] caches, Mp3Quantizer.Plan[] plans, int budget)
        {
            var used = 0;
            for (var s = 0; s < plans.Length; s++)
            {
                plans[s] = Mp3Quantizer.WithBestDivision(plans[s], _bandStarts);
                used += plans[s].BitCount;
            }

            var done = new bool[plans.Length];
            for (var s = 0; s < plans.Length; s++)
            {
                done[s] = Array.TrueForAll(segments[s], value => value == 0);
            }

            var progress = true;
            while (progress)
            {
                progress = false;
                for (var s = 0; s < plans.Length; s++)
                {
                    var current = plans[s];
                    if (done[s])
                    {
                        continue;
                    }

                    if (current.GlobalGain == 0 || caches[s].Get(segments[s], current.GlobalGain - 1, int.MaxValue, _bandStarts) is not { } finer)
                    {
                        done[s] = true;
                        continue;
                    }

                    var candidate = Mp3Quantizer.WithRegions(finer, current.Division.Region0Count, current.Division.Region1Count, _bandStarts);

                    if (used + candidate.BitCount - current.BitCount <= budget && candidate.BitCount <= Mp3Quantizer.MaxSegmentBits)
                    {
                        used += candidate.BitCount - current.BitCount;
                        plans[s] = candidate;
                        progress = true;
                    }
                    else
                    {
                        done[s] = true;
                    }
                }
            }

            return plans;
        }

        private static double[] BuildSineWindow(int size)
        {
            var window = new double[size];
            for (var n = 0; n < size; n++)
            {
                window[n] = Math.Sin((Math.PI / size) * (n + 0.5));
            }

            return window;
        }

        private const double MidSideSideToMidRatio = 0.5;

        private static bool PrefersMidSide(double[][][] granuleXr)
        {
            var mid = 0.0;
            var side = 0.0;
            foreach (var granule in granuleXr)
            {
                var left = granule[0];
                var right = granule[1];
                for (var i = 0; i < left.Length; i++)
                {
                    var sum = left[i] + right[i];
                    var difference = left[i] - right[i];
                    mid += sum * sum;
                    side += difference * difference;
                }
            }

            return side <= mid * MidSideSideToMidRatio;
        }

        private static double[][][] ToMidSide(double[][][] granuleXr)
        {
            var result = new double[granuleXr.Length][][];
            for (var gr = 0; gr < granuleXr.Length; gr++)
            {
                var left = granuleXr[gr][0];
                var right = granuleXr[gr][1];
                var mid = new double[left.Length];
                var side = new double[left.Length];
                for (var i = 0; i < left.Length; i++)
                {
                    mid[i] = (left[i] + right[i]) * Math.Sqrt(0.5);
                    side[i] = (left[i] - right[i]) * Math.Sqrt(0.5);
                }

                result[gr] = [mid, side];
            }

            return result;
        }

        private sealed class GranuleScratch
        {
            public int[] Tick { get; } = new int[SubbandCount];

            public double[] PerSubband { get; } = new double[SubbandCount * PolyphaseTicksPerGranule];

            public double[] Windowed { get; } = new double[MdctWindowSize];

            public double[] Coefficients { get; } = new double[CoefficientsPerSubband];
        }
    }
}
