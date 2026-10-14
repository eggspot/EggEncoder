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

        // The MP3 bitstream itself carries no explicit PCM-scale convention, but every real
        // decoder's own int16 output stage (confirmed directly in NLayer's ReadSamplesImpl:
        // `(int)Math.Round(32767.5f * sample - 0.5f)`) assumes the hybrid filterbank's own
        // reconstructed float samples sit in the standard normalized -1.0..+1.0 range (full scale
        // == 1.0), NOT this project's own int16-native PCM convention every OTHER codec here uses
        // directly. Confirmed empirically: feeding raw int16-range PCM (e.g. amplitude 8000)
        // straight into the polyphase filter without this scale-down produces a transmitted xr
        // ~32768x too loud, which a real decoder's own *32767.5 int16 rescale then overflows/wraps
        // catastrophically (not a graceful clip -- NLayer's int16 packing is a raw, unclamped
        // `value % 256` / `value / 256`), producing output that looks like exponentially growing
        // noise as signal magnitude increases, not a bounded scale error. The polyphase/MDCT/alias
        // reduction chain is entirely linear, so scaling the final xr once here is mathematically
        // identical to scaling the source PCM before analysis.
        private const double PcmNormalizationScale = 1.0 / 32768.0;

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

            // Frequency inversion -- negate every odd subband's sample at every odd tick index
            // within this granule. A real decoder always applies this (on its own IMDCT output,
            // not the raw subband values) to correct for the hybrid filterbank's own frequency
            // response; the encoder must apply the exact same correction here, on the SAME
            // (subband, tick) indices, before these values are used as MDCT input or become next
            // granule's own history -- confirmed necessary (not optional) by direct measurement
            // against a from-scratch, independently re-derived encoder tested against NLayer's own
            // real decode algorithms: omitting this step holds SNR to roughly -5dB across 50Hz-
            // 1000Hz test tones; including it restores SNR to 96-99dB uniformly.
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

                // Transform.Mdct.Inverse (shared with AAC/WMA) divides its own cosine sum by
                // CoefficientsPerSubband (18) -- a normalization convention a real decoder's own
                // IMDCT does NOT apply (confirmed by direct numerical comparison against NLayer's
                // own decode-side IMDCT: Mdct.Inverse(C) == NLayerImdct(C) / 18 for every basis
                // vector). Mdct.Forward has no compensating factor, so coefficients must be
                // pre-divided here -- otherwise every real decoder reconstructs 18x too loud
                // (this project's own self-decode tests don't catch it, since they use
                // Mdct.Forward and Mdct.Inverse together, where the same factor cancels out).
                for (var k = 0; k < CoefficientsPerSubband; k++)
                {
                    coefficients[k] /= CoefficientsPerSubband;
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

        // internal (not private) so BuildInfoTagFrame below can write a structurally valid frame
        // header for its own dedicated Info-tag frame, reusing this exact bitrate/sample-rate/
        // channel-mode encoding rather than duplicating it.
        internal void WriteHeader(BitWriter writer, int padding)
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

        // The 9-byte "encoder short version string" this method's own Info-tag frame carries,
        // identifying this project's own encoder -- deliberately NOT "LAME"/"Lavf"/"Lavc", the
        // three exact strings FFmpeg's own libavformat/mp3dec.c gates its delay/padding parsing on
        // (confirmed directly from that source): writing one of those would falsely claim to BE
        // that tool. A real, accepted consequence is that FFmpeg's own demuxer won't itself honor
        // this encoder's delay/padding fields for automatic gapless trimming; this project's own
        // Mp3Probe reads them regardless of version string for exactly that reason. Null-terminated
        // for safety even though a reader that already null-terminates its own fixed buffer first
        // (as FFmpeg's own reader does) doesn't strictly need it.
        private const string InfoTagVersionString = "EggEncdr\0";

        // Builds the dedicated "Info" header frame every Mp3EncoderSession output now starts with:
        // a structurally real, decodable MPEG frame (the same header shape WriteHeader above
        // writes, zero side info, no padding bit) whose own payload is not real audio but the
        // de-facto-standard "LAME tag" -- an Xing-style VBR-header extension real-world encoders
        // (LAME itself, and FFmpeg's own Lavc/Lavf muxers) use to carry gapless-playback delay/
        // padding metadata. The byte layout here was confirmed field-for-field against FFmpeg's own
        // real libavformat/mp3dec.c source (its mp3_parse_info_tag function), and cross-checked
        // against a real ffmpeg+libmp3lame-produced reference file's own raw bytes -- see
        // THIRD-PARTY-NOTICES.md for the exact provenance and what was verified versus
        // independently reimplemented. Called once with placeholder zero values when a session
        // opens (reserving this frame's own exact, fixed byte length up front) and once more with
        // the real values once Finish() knows them, patched in place over the placeholder -- the
        // same provisional-header-then-patch shape FlacEncoderSession.Finish() already uses for
        // STREAMINFO.
        //
        // Scoped deliberately to just the fields this feature needs (frame/byte count, for the
        // standard's own realSamples = frames*spf - startPad - endPad identity to hold -- confirmed
        // against the real reference file above -- and the delay/padding fields themselves): the
        // TOC (Xing's own seek table) and VBR-quality flags are not set, since this encoder is
        // CBR-only and gains nothing from either (CBR seeking is already exact without a TOC).
        // ReplayGain, the "encoder" version-tag FFmpeg's own demuxer exports, and the Music/Info Tag
        // CRC-16 fields this format also defines are written as zero/absent rather than guessed:
        // FFmpeg's own decode-side code never gates the delay/padding fields on any of those
        // (confirmed from source), so nothing this feature needs depends on them, and writing a
        // value for a field this implementation hasn't actually verified would be dishonest.
        internal byte[] BuildInfoTagFrame(int frameCount, int byteCount, int startPadSamples, int endPadSamples)
        {
            var frameLengthBytes = (144000 * _bitRateKbps) / _sampleRate;

            var writer = new BitWriter();
            WriteHeader(writer, padding: 0);
            writer.WriteBits(0, _sideInfoBytes * 8);

            foreach (var c in "Info")
            {
                writer.WriteBits(c, 8);
            }

            const uint flags = 0x01 | 0x02; // frame count present + byte count present
            writer.WriteBits(flags, 32);
            writer.WriteBits((uint)frameCount, 32);
            writer.WriteBits((uint)byteCount, 32);

            // Exactly 9 bytes, not "however long the string literal happens to be" -- every field
            // after this one is at a fixed offset the real format (and this project's own
            // Mp3Probe read side) assumes, so a wrong length here would silently shift and corrupt
            // every field that follows, including the delay/padding this whole feature is for.
            if (InfoTagVersionString.Length != 9)
            {
                throw new InvalidOperationException($"InfoTagVersionString must be exactly 9 characters, was {InfoTagVersionString.Length}");
            }

            foreach (var c in InfoTagVersionString)
            {
                writer.WriteBits(c, 8);
            }

            writer.WriteBits(0, 8); // Info Tag revision + VBR method
            writer.WriteBits(0, 8); // lowpass filter value
            writer.WriteBits(0, 32); // ReplayGain peak -- not computed, see this method's own doc comment
            writer.WriteBits(0, 16); // Radio ReplayGain -- not computed
            writer.WriteBits(0, 16); // Audiophile ReplayGain -- not computed
            writer.WriteBits(0, 8); // encoding flags + ATH type
            writer.WriteBits(0, 8); // ABR bitrate / minimal bitrate

            var delays = ((uint)startPadSamples << 12) | ((uint)endPadSamples & 0xFFF);
            writer.WriteBits(delays, 24);

            writer.WriteBits(0, 8); // misc
            writer.WriteBits(0, 8); // MP3 gain
            writer.WriteBits(0, 16); // preset and surround info
            writer.WriteBits((uint)byteCount, 32); // music length -- matches byte count, confirmed against the real reference file
            writer.WriteBits(0, 16); // Music CRC -- not computed, see this method's own doc comment
            writer.WriteBits(0, 16); // Info Tag CRC -- not computed, see this method's own doc comment

            var writtenBytes = writer.BitPosition / 8;
            var remainingBytes = frameLengthBytes - writtenBytes;
            if (remainingBytes > 0)
            {
                writer.WriteBits(0, remainingBytes * 8);
            }

            return writer.ToArray();
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
