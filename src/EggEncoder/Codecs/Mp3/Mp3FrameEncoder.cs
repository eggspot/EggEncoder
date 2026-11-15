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
        private readonly int _tagBitrateIndex;
        private readonly int? _vbrTargetGain;
        private int _bitrateIndex;
        private readonly int _sideInfoBytes;
        private readonly double[] _sineWindow;
        private readonly Mp3PolyphaseFilter[] _filters;
        private readonly double[][][] _subbandHistory;

        private double _paddingAccumulator;

        public Mp3FrameEncoder(int channels, int sampleRate, int bitRateKbps, int? vbrTargetGain = null)
        {
            _vbrTargetGain = vbrTargetGain;
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

            _tagBitrateIndex = _bitrateIndex;
            _sideInfoBytes = channels == 1 ? 17 : 32;
            _sineWindow = BuildSineWindow(MdctWindowSize);

            _filters = new Mp3PolyphaseFilter[channels];
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

        public byte[] EncodeFrame(ReadOnlySpan<int> interleavedSamples)
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

            int frameLengthBytes;
            int padding;
            Mp3Quantizer.Plan[,] plans;
            if (_vbrTargetGain is { } targetGain)
            {
                (frameLengthBytes, plans) = ChooseVbrFrame(granuleXr, targetGain);
                padding = 0;
            }
            else
            {
                (frameLengthBytes, padding) = NextFrameLength();
                plans = PlanFrame(granuleXr, frameLengthBytes);
            }

            var mainDataBytes = frameLengthBytes - 4 - _sideInfoBytes;

            var writer = new BitWriter();
            WriteHeader(writer, padding);
            WriteSideInfo(writer, plans);

            var usedBits = 0;
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    Mp3Quantizer.Write(writer, plans[gr, ch]);
                    usedBits += plans[gr, ch].BitCount;
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
            var perSubband = new double[SubbandCount][];
            for (var sb = 0; sb < SubbandCount; sb++)
            {
                perSubband[sb] = new double[PolyphaseTicksPerGranule];
            }

            var tick = new int[SubbandCount];
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
                    perSubband[sb][t] = subbandSamples[sb];
                }
            }

            for (var sb = 1; sb < SubbandCount; sb += 2)
            {
                for (var t = 1; t < PolyphaseTicksPerGranule; t += 2)
                {
                    perSubband[sb][t] = -perSubband[sb][t];
                }
            }

            var xr = new double[GranuleSamples];
            var windowed = new double[MdctWindowSize];
            for (var sb = 0; sb < SubbandCount; sb++)
            {
                var history = _subbandHistory[channel][sb];
                for (var i = 0; i < CoefficientsPerSubband; i++)
                {
                    windowed[i] = history[i] * _sineWindow[i];
                    windowed[CoefficientsPerSubband + i] = perSubband[sb][i] * _sineWindow[CoefficientsPerSubband + i];
                }

                var coefficients = Mdct.Forward(windowed);

                for (var k = 0; k < CoefficientsPerSubband; k++)
                {
                    coefficients[k] *= ForwardMdctScale;
                }

                Array.Copy(coefficients, 0, xr, sb * CoefficientsPerSubband, CoefficientsPerSubband);

                Array.Copy(perSubband[sb], history, CoefficientsPerSubband);
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

        internal void WriteHeader(BitWriter writer, int padding)
        {
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)_bitrateIndex, 4);
            writer.WriteBits((uint)_sampleRateIndex, 2);
            writer.WriteBits((uint)padding, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(_channels == 1 ? 0b11u : 0b00u, 2);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 2);
        }

        internal void WriteSideInfo(BitWriter writer, Mp3Quantizer.Plan[,] plans)
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
                    var plan = plans[gr, ch];
                    writer.WriteBits((uint)plan.BitCount, 12);
                    writer.WriteBits((uint)plan.BigValues, 9);
                    writer.WriteBits((uint)plan.GlobalGain, 8);
                    writer.WriteBits(0, 4);
                    writer.WriteBits(0, 1);
                    writer.WriteBits((uint)plan.TableSelect, 5);
                    writer.WriteBits((uint)plan.TableSelect, 5);
                    writer.WriteBits((uint)plan.TableSelect, 5);
                    writer.WriteBits(0, 4);
                    writer.WriteBits(0, 3);
                    writer.WriteBits(0, 1);
                    writer.WriteBits(0, 1);
                    writer.WriteBits(0, 1);
                }
            }
        }

        private const string InfoTagVersionString = "EggEncdr\0";

        internal byte[] BuildInfoTagFrame(int frameCount, int byteCount, int startPadSamples, int endPadSamples, byte[]? seekTable = null)
        {
            var frameLengthBytes = (144000 * _bitRateKbps) / _sampleRate;
            _bitrateIndex = _tagBitrateIndex;

            var writer = new BitWriter();
            WriteHeader(writer, padding: 0);
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

        private (int FrameLengthBytes, Mp3Quantizer.Plan[,] Plans) ChooseVbrFrame(double[][][] granuleXr, int targetGain)
        {
            var lastIndex = Array.FindLastIndex(Mp3Probe.Mpeg1Layer3BitrateKbps, rate => rate > 0);
            for (var index = 1; index <= lastIndex; index++)
            {
                var kbps = Mp3Probe.Mpeg1Layer3BitrateKbps[index];
                if (kbps <= 0)
                {
                    continue;
                }

                var frameLengthBytes = (144000 * kbps) / _sampleRate;
                var plans = PlanFrame(granuleXr, frameLengthBytes);
                var fits = true;
                foreach (var plan in plans)
                {
                    fits &= plan.GlobalGain <= targetGain;
                }

                if (fits || index == lastIndex)
                {
                    _bitrateIndex = index;
                    return (frameLengthBytes, plans);
                }
            }

            throw new InvalidOperationException("MP3 VBR: no bitrate was tried");
        }

        private Mp3Quantizer.Plan[,] PlanFrame(double[][][] granuleXr, int frameLengthBytes)
        {
            var mainDataBytes = frameLengthBytes - 4 - _sideInfoBytes;
            var bitsPerSegment = (mainDataBytes * 8) / (GranuleCount * _channels);
            var plans = new Mp3Quantizer.Plan[GranuleCount, _channels];
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    plans[gr, ch] = Mp3Quantizer.FindPlan(granuleXr[gr][ch], bitsPerSegment);
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
    }
}
