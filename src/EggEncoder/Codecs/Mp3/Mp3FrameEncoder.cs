using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // Encodes one complete MPEG-1 Layer III CBR frame (1152 PCM samples per channel, two 576-sample
    // granules) from interleaved 16-bit-range PCM. Baseline scope per
    // docs/managed-codec-rewrite-plan.md item 6's own design: long blocks only (no block switching),
    // independent (non-joint) stereo, no bit-reservoir borrowing (main_data_begin is always 0 -- a
    // granule's own Huffman data always fits entirely within its own frame's main data region, with
    // any leftover bits written as zero ancillary padding), and a single Huffman table for an
    // entire granule's big_values region (see Mp3Quantizer's own doc comment).
    internal sealed class Mp3FrameEncoder
    {
        private const int GranuleCount = 2;
        private const int GranuleSamples = 576;
        private const int SubbandCount = 32;
        private const int PolyphaseTicksPerGranule = GranuleSamples / SubbandCount;
        private const int CoefficientsPerSubband = 18;
        private const int MdctWindowSize = CoefficientsPerSubband * 2;

        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bitRateKbps;
        private readonly int _sampleRateIndex;
        private readonly int _bitrateIndex;
        private readonly int _sideInfoBytes;
        private readonly double[] _sineWindow;
        private readonly Mp3PolyphaseFilter[] _filters;
        private readonly double[][][] _subbandHistory;

        private double _paddingAccumulator;

        public Mp3FrameEncoder(int channels, int sampleRate, int bitRateKbps)
        {
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

        // interleavedSamples must contain exactly 1152 * channels samples, already scaled to the
        // 16-bit-range PCM this encoder (like the real format) expects.
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

            var (frameLengthBytes, padding) = NextFrameLength();
            var mainDataBytes = frameLengthBytes - 4 - _sideInfoBytes;
            var segmentCount = GranuleCount * _channels;
            var bitsPerSegment = (mainDataBytes * 8) / segmentCount;

            var plans = new Mp3Quantizer.Plan[GranuleCount, _channels];
            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    plans[gr, ch] = Mp3Quantizer.FindPlan(granuleXr[gr][ch], bitsPerSegment);
                }
            }

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

        // internal (not private) so Mp3DifferentialTest can exercise the multi-granule integration
        // directly rather than only through the full EncodeFrame -> bitstream -> decode round trip.
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
                Array.Copy(coefficients, 0, xr, sb * CoefficientsPerSubband, CoefficientsPerSubband);

                Array.Copy(perSubband[sb], history, CoefficientsPerSubband);
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

        private void WriteHeader(BitWriter writer, int padding)
        {
            writer.WriteBits(0x7FF, 11); // sync word
            writer.WriteBits(0b11, 2); // MPEG version 1
            writer.WriteBits(0b01, 2); // Layer III
            writer.WriteBits(1, 1); // protection_bit = 1 (no CRC)
            writer.WriteBits((uint)_bitrateIndex, 4);
            writer.WriteBits((uint)_sampleRateIndex, 2);
            writer.WriteBits((uint)padding, 1);
            writer.WriteBits(0, 1); // private_bit
            writer.WriteBits(_channels == 1 ? 0b11u : 0b00u, 2); // mode: mono, or stereo (independent)
            writer.WriteBits(0, 2); // mode_extension (unused outside joint stereo)
            writer.WriteBits(0, 1); // copyright
            writer.WriteBits(1, 1); // original
            writer.WriteBits(0, 2); // emphasis
        }

        private void WriteSideInfo(BitWriter writer, Mp3Quantizer.Plan[,] plans)
        {
            writer.WriteBits(0, 9); // main_data_begin -- always 0, this encoder never borrows from the bit reservoir
            writer.WriteBits(0, _channels == 1 ? 5 : 3); // private_bits

            for (var ch = 0; ch < _channels; ch++)
            {
                writer.WriteBits(0, 4); // scfsi[ch][0..3] -- every granule always carries its own fresh (zero-width) scalefactors
            }

            for (var gr = 0; gr < GranuleCount; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    var plan = plans[gr, ch];
                    writer.WriteBits((uint)plan.BitCount, 12); // part2_3_length (scalefactors are always 0 bits in this baseline)
                    writer.WriteBits((uint)plan.BigValues, 9);
                    writer.WriteBits((uint)plan.GlobalGain, 8);
                    writer.WriteBits(0, 4); // scalefac_compress = 0 -> slen1=slen2=0
                    writer.WriteBits(0, 1); // window_switching_flag = 0 (long blocks only)
                    writer.WriteBits((uint)plan.TableSelect, 5); // table_select[0]
                    writer.WriteBits((uint)plan.TableSelect, 5); // table_select[1]
                    writer.WriteBits((uint)plan.TableSelect, 5); // table_select[2] -- same table for every region, see Mp3Quantizer
                    writer.WriteBits(0, 4); // region0_count
                    writer.WriteBits(0, 3); // region1_count
                    writer.WriteBits(0, 1); // preflag
                    writer.WriteBits(0, 1); // scalefac_scale
                    writer.WriteBits(0, 1); // count1table_select -- irrelevant, this baseline never uses the count1 region
                }
            }
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
