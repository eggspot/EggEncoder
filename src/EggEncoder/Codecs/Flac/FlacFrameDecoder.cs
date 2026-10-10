using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    // Decodes one FLAC frame (header, every subframe, residual, prediction restore, stereo
    // decorrelation restore, CRC-16 verification) per RFC 9639 sections 9.1-9.3, into per-channel
    // int32 buffers the caller then interleaves. Stateful across frames only in that it reuses its
    // own pre-sized buffers (channels/bitsPerSample/maxBlockSize are fixed for the whole stream, per
    // STREAMINFO) -- mirrors this codebase's own AlacFrameDecoder/TtaFrameDecoder shape.
    internal sealed class FlacFrameDecoder
    {
        private const int ChannelAssignmentLeftSide = 8;
        private const int ChannelAssignmentRightSide = 9;
        private const int ChannelAssignmentMidSide = 10;

        private static readonly int[] _bitsPerSampleTable = [0, 8, 12, -1, 16, 20, 24, 32];

        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _maxBlockSize;
        private readonly int[][] _samples;
        private readonly long[] _wideSide; // the 33-bit side channel of 32-bit stereo audio only
        private readonly int[] _coefficients = new int[32];

        public FlacFrameDecoder(int channels, int bitsPerSample, int maxBlockSize)
        {
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _maxBlockSize = maxBlockSize;

            _samples = new int[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                _samples[channel] = new int[maxBlockSize];
            }

            _wideSide = channels == 2 ? new long[maxBlockSize] : [];
        }

        /// <summary>Decoded samples, one array per channel, each of length <see cref="DecodeFrame"/>'s own return value.</summary>
        public int[][] Samples => _samples;

        /// <summary>
        /// Decodes the frame starting at <paramref name="reader"/>'s current (byte-aligned) position
        /// and returns its block size (samples per channel). <paramref name="fileBytes"/> is the same
        /// backing array <paramref name="reader"/> was constructed over -- needed directly for CRC-8/
        /// CRC-16, which are computed over raw bytes, not through the bit-level reader.
        /// </summary>
        public int DecodeFrame(byte[] fileBytes, BitReader reader)
        {
            var frameStartByte = reader.BitPosition / 8;
            var blockSize = ReadFrameHeader(fileBytes, reader, frameStartByte, out var assignment);

            for (var channel = 0; channel < _channels; channel++)
            {
                var isSide = (assignment == ChannelAssignmentLeftSide && channel == 1)
                             || (assignment == ChannelAssignmentRightSide && channel == 0)
                             || (assignment == ChannelAssignmentMidSide && channel == 1);
                DecodeSubframe(reader, blockSize, _bitsPerSample + (isSide ? 1 : 0), channel, isSide);
            }

            reader.ByteAlign();
            var footerStartByte = reader.BitPosition / 8;
            var expectedCrc16 = (ushort)reader.ReadBits(16);
            var actualCrc16 = FlacCrc.Crc16(fileBytes, frameStartByte, footerStartByte);
            if (actualCrc16 != expectedCrc16)
            {
                throw new InvalidDataException($"FLAC frame footer CRC-16 mismatch (computed 0x{actualCrc16:X4}, recorded 0x{expectedCrc16:X4}) -- the file is corrupt or truncated.");
            }

            Decorrelate(assignment, blockSize);
            return blockSize;
        }

        // ---------------------------------------------------------------- frame header

        private int ReadFrameHeader(byte[] fileBytes, BitReader reader, int frameStartByte, out int channelAssignment)
        {
            var sync = reader.ReadBits(8);
            var second = reader.ReadBits(8);
            if (sync != 0xFF || (second & 0xFC) != 0xF8)
            {
                throw new InvalidDataException("Expected a FLAC frame sync code but found none -- the file is corrupt, truncated, or this is trailing data after the last frame.");
            }

            if ((second & 0x02) != 0)
            {
                throw new InvalidDataException("The reserved bit after the FLAC frame sync code is not 0.");
            }

            var variableBlockSize = (second & 0x01) != 0;

            var third = reader.ReadBits(8);
            var fourth = reader.ReadBits(8);
            var blockSizeCode = third >> 4;
            var sampleRateCode = third & 0x0F;
            channelAssignment = (int)(fourth >> 4);
            var bitsPerSampleCode = (fourth >> 1) & 0x07;

            if (blockSizeCode == 0)
            {
                throw new InvalidDataException("The FLAC frame header block size code is the reserved value 0.");
            }

            if (sampleRateCode == 15)
            {
                throw new InvalidDataException("The FLAC frame header sample rate code is the invalid value 15.");
            }

            if (channelAssignment > ChannelAssignmentMidSide)
            {
                throw new InvalidDataException($"The FLAC frame header channel assignment code {channelAssignment} is reserved.");
            }

            if (bitsPerSampleCode == 3)
            {
                throw new InvalidDataException("The FLAC frame header bits-per-sample code is the reserved value 3.");
            }

            if ((fourth & 0x01) != 0)
            {
                throw new InvalidDataException("The reserved bit after the FLAC frame header bits-per-sample code is not 0.");
            }

            // Frame or sample number: a UTF-8-style variable-length coded value (RFC 9639 section
            // 9.1.5) whose own byte count we don't otherwise need -- only that it's well-formed and
            // consumed, since STREAMINFO's own total-sample-count is this decoder's source of truth.
            SkipCodedNumber(reader, variableBlockSize);

            var blockSize = ReadBlockSize(reader, blockSizeCode);
            SkipSampleRateBits(reader, sampleRateCode);

            var expectedCrc8 = (byte)reader.ReadBits(8);
            var headerEndByte = reader.BitPosition / 8;
            var actualCrc8 = FlacCrc.Crc8(fileBytes, frameStartByte, headerEndByte - 1);
            if (actualCrc8 != expectedCrc8)
            {
                throw new InvalidDataException($"FLAC frame header CRC-8 mismatch (computed 0x{actualCrc8:X2}, recorded 0x{expectedCrc8:X2}) -- the file is corrupt or truncated.");
            }

            var frameChannels = channelAssignment >= ChannelAssignmentLeftSide ? 2 : channelAssignment + 1;
            if (frameChannels != _channels)
            {
                throw new InvalidDataException($"A FLAC frame declares {frameChannels} channel(s) but STREAMINFO declares {_channels}.");
            }

            var frameBitsPerSample = bitsPerSampleCode == 0 ? _bitsPerSample : _bitsPerSampleTable[bitsPerSampleCode];
            if (frameBitsPerSample != _bitsPerSample)
            {
                throw new InvalidDataException($"A FLAC frame declares {frameBitsPerSample} bits per sample but STREAMINFO declares {_bitsPerSample}.");
            }

            if (blockSize > _maxBlockSize)
            {
                throw new InvalidDataException($"A FLAC frame's block size {blockSize} exceeds STREAMINFO's own maximum block size {_maxBlockSize}.");
            }

            return blockSize;
        }

        private static void SkipCodedNumber(BitReader reader, bool variableBlockSize)
        {
            var first = reader.ReadBits(8);
            int extraBytes;
            if ((first & 0x80) == 0)
            {
                extraBytes = 0;
            }
            else if ((first & 0xE0) == 0xC0)
            {
                extraBytes = 1;
            }
            else if ((first & 0xF0) == 0xE0)
            {
                extraBytes = 2;
            }
            else if ((first & 0xF8) == 0xF0)
            {
                extraBytes = 3;
            }
            else if ((first & 0xFC) == 0xF8)
            {
                extraBytes = 4;
            }
            else if ((first & 0xFE) == 0xFC)
            {
                extraBytes = 5;
            }
            else if (first == 0xFE && variableBlockSize)
            {
                extraBytes = 6;
            }
            else
            {
                throw new InvalidDataException("The FLAC frame/sample number coding is invalid.");
            }

            for (var i = 0; i < extraBytes; i++)
            {
                var continuationByte = reader.ReadBits(8);
                if ((continuationByte & 0xC0) != 0x80)
                {
                    throw new InvalidDataException("The FLAC frame/sample number coding is invalid.");
                }
            }
        }

        private static int ReadBlockSize(BitReader reader, uint blockSizeCode)
        {
            if (blockSizeCode == 1)
            {
                return 192;
            }

            if (blockSizeCode <= 5)
            {
                return 576 << ((int)blockSizeCode - 2);
            }

            if (blockSizeCode == 6)
            {
                return (int)reader.ReadBits(8) + 1;
            }

            if (blockSizeCode == 7)
            {
                return (int)reader.ReadBits(16) + 1;
            }

            return 256 << ((int)blockSizeCode - 8);
        }

        private static void SkipSampleRateBits(BitReader reader, uint sampleRateCode)
        {
            // The sample rate here never overrides STREAMINFO's own (RFC 9639 explicitly allows an
            // encoder to write one anyway, purely informational) -- these bits only need to be
            // consumed so the CRC-8 below covers them and the following bits land correctly.
            var bits = sampleRateCode switch
            {
                12 => 8,
                13 or 14 => 16,
                _ => 0,
            };

            if (bits > 0)
            {
                reader.ReadBits(bits);
            }
        }

        // ---------------------------------------------------------------- subframe

        private void DecodeSubframe(BitReader reader, int blockSize, int subframeBitsPerSample, int channel, bool isSide)
        {
            var header = reader.ReadBits(8);
            if ((header & 0x80) != 0)
            {
                throw new InvalidDataException("The first bit of a FLAC subframe header is not 0.");
            }

            var type = (int)((header >> 1) & 0x3F);
            var wastedBits = 0;
            if ((header & 0x01) != 0)
            {
                wastedBits = (int)reader.ReadUnary() + 1;
                if (wastedBits >= subframeBitsPerSample)
                {
                    throw new InvalidDataException($"A FLAC subframe declares {wastedBits} wasted bits, which is not less than its own {subframeBitsPerSample}-bit depth.");
                }
            }

            var codedBitsPerSample = subframeBitsPerSample - wastedBits;

            // Only the side channel of 32-bit stereo audio ever needs more than 32 bits (it's a
            // 33-bit value); that one path runs in 64 bits throughout.
            if (isSide && _bitsPerSample == 32)
            {
                var wide = _wideSide.AsSpan(0, blockSize);
                DecodeSubframeBody64(reader, type, blockSize, codedBitsPerSample, channel, wide);
                if (wastedBits > 0)
                {
                    for (var i = 0; i < wide.Length; i++)
                    {
                        wide[i] <<= wastedBits;
                    }
                }

                return;
            }

            var output = _samples[channel].AsSpan(0, blockSize);
            DecodeSubframeBody32(reader, type, blockSize, codedBitsPerSample, output);
            if (wastedBits > 0)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = (int)((uint)output[i] << wastedBits);
                }
            }
        }

        private void DecodeSubframeBody32(BitReader reader, int type, int blockSize, int bitsPerSample, Span<int> output)
        {
            if (type == 0)
            {
                output.Fill(reader.ReadSignedBits(bitsPerSample));
                return;
            }

            if (type == 1)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = reader.ReadSignedBits(bitsPerSample);
                }

                return;
            }

            if (type is >= 8 and <= 12)
            {
                var order = type - 8;
                CheckPredictorOrder(order, blockSize);
                ReadWarmupSamples(reader, order, bitsPerSample, output);
                ReadResidual(reader, blockSize, order, output);
                RestoreFixedPrediction(order, output);
                return;
            }

            if (type >= 32)
            {
                var order = type - 31;
                CheckPredictorOrder(order, blockSize);
                ReadWarmupSamples(reader, order, bitsPerSample, output);
                var (precision, shift) = ReadLpcCoefficients(reader, order);
                ReadResidual(reader, blockSize, order, output);
                var coefficients = _coefficients.AsSpan(0, order);
                if (NeedsWideLpcAccumulator(bitsPerSample, order, precision))
                {
                    RestoreLpcPredictionWide(coefficients, shift, output);
                }
                else
                {
                    RestoreLpcPrediction(coefficients, shift, output);
                }

                return;
            }

            throw new InvalidDataException($"FLAC subframe type {type} is reserved.");
        }

        private void DecodeSubframeBody64(BitReader reader, int type, int blockSize, int bitsPerSample, int channel, Span<long> output)
        {
            if (type == 0)
            {
                output.Fill(ReadSignedBits64(reader, bitsPerSample));
                return;
            }

            if (type == 1)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = ReadSignedBits64(reader, bitsPerSample);
                }

                return;
            }

            if (type is >= 8 and <= 12 or >= 32)
            {
                var isFixed = type <= 12;
                var order = isFixed ? type - 8 : type - 31;
                CheckPredictorOrder(order, blockSize);
                for (var i = 0; i < order; i++)
                {
                    output[i] = ReadSignedBits64(reader, bitsPerSample);
                }

                var shift = 0;
                if (isFixed)
                {
                    GetFixedCoefficients(order).CopyTo(_coefficients);
                }
                else
                {
                    (_, shift) = ReadLpcCoefficients(reader, order);
                }

                // The residual itself always fits in int32 (RFC 9639's own bit-depth table tops out
                // at 32), so it's decoded into this channel's own ordinary int buffer as pure scratch
                // space -- that buffer isn't this channel's real content at this point (this path
                // only runs when isSide is true, so the real, 33-bit values live in `output`
                // instead), and it's always this *same* channel's own buffer, never a different
                // channel's -- which is what makes it safe to reuse even though another channel's
                // buffer may already hold real decoded samples this frame needs later (e.g. the
                // left/right channel a left-side/right-side frame reconstructs from the side value).
                var residualScratch = _samples[channel].AsSpan(0, blockSize);
                ReadResidual(reader, blockSize, order, residualScratch);
                RestoreLpcPredictionWide(_coefficients.AsSpan(0, order), shift, residualScratch, output);
                return;
            }

            throw new InvalidDataException($"FLAC subframe type {type} is reserved.");
        }

        private static void ReadWarmupSamples(BitReader reader, int order, int bitsPerSample, Span<int> output)
        {
            for (var i = 0; i < order; i++)
            {
                output[i] = reader.ReadSignedBits(bitsPerSample);
            }
        }

        private static long ReadSignedBits64(BitReader reader, int bitCount)
        {
            if (bitCount <= 32)
            {
                return reader.ReadSignedBits(bitCount);
            }

            var high = (ulong)reader.ReadBits(bitCount - 32);
            var low = (ulong)reader.ReadBits(32);
            var value = (high << 32) | low;
            var shift = 64 - bitCount;
            return (long)(value << shift) >> shift;
        }

        private static void CheckPredictorOrder(int order, int blockSize)
        {
            if (order > blockSize)
            {
                throw new InvalidDataException($"A FLAC predictor order of {order} is larger than its own frame's block size {blockSize}.");
            }
        }

        private (int Precision, int Shift) ReadLpcCoefficients(BitReader reader, int order)
        {
            var precision = (int)reader.ReadBits(4);
            if (precision == 15)
            {
                throw new InvalidDataException("A FLAC LPC subframe's coefficient precision is the invalid value 15.");
            }

            precision++;

            var shift = reader.ReadSignedBits(5);
            if (shift < 0)
            {
                throw new InvalidDataException($"A FLAC LPC subframe's shift is negative ({shift}); RFC 9639 requires a non-negative shift.");
            }

            for (var i = 0; i < order; i++)
            {
                _coefficients[i] = reader.ReadSignedBits(precision);
            }

            return (precision, shift);
        }

        private static bool NeedsWideLpcAccumulator(int bitsPerSample, int order, int precision)
        {
            var orderBits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)order);
            return bitsPerSample + precision + orderBits > 32;
        }

        private static ReadOnlySpan<int> GetFixedCoefficients(int order) => order switch
        {
            0 => [],
            1 => [1],
            2 => [2, -1],
            3 => [3, -3, 1],
            _ => [4, -6, 4, -1],
        };

        // ---------------------------------------------------------------- residual (RFC 9639 section 9.2.7)

        private static void ReadResidual(BitReader reader, int blockSize, int predictorOrder, Span<int> output)
        {
            var method = reader.ReadBits(2);
            if (method > 1)
            {
                throw new InvalidDataException($"FLAC residual coding method {method} is reserved.");
            }

            var parameterBits = method == 0 ? 4 : 5;
            var escapeCode = method == 0 ? 15 : 31;
            var partitionOrder = (int)reader.ReadBits(4);
            var partitionCount = 1 << partitionOrder;
            var partitionSize = blockSize >> partitionOrder;

            if (partitionSize << partitionOrder != blockSize || partitionSize <= predictorOrder)
            {
                throw new InvalidDataException($"FLAC Rice partition order {partitionOrder} is incompatible with block size {blockSize} and predictor order {predictorOrder}.");
            }

            var position = predictorOrder;
            for (var partition = 0; partition < partitionCount; partition++)
            {
                var count = partition == 0 ? partitionSize - predictorOrder : partitionSize;
                var part = output.Slice(position, count);
                position += count;

                var riceParameter = (int)reader.ReadBits(parameterBits);
                if (riceParameter == escapeCode)
                {
                    var rawWidth = (int)reader.ReadBits(5);
                    if (rawWidth == 0)
                    {
                        part.Clear();
                    }
                    else
                    {
                        for (var i = 0; i < part.Length; i++)
                        {
                            part[i] = reader.ReadSignedBits(rawWidth);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < part.Length; i++)
                    {
                        part[i] = ReadRiceResidual(reader, riceParameter);
                    }
                }
            }
        }

        private static int ReadRiceResidual(BitReader reader, int riceParameter)
        {
            var quotient = reader.ReadUnary();
            var remainder = riceParameter > 0 ? reader.ReadBits(riceParameter) : 0;
            var folded = (quotient << riceParameter) | remainder;
            return (int)(folded >> 1) ^ -(int)(folded & 1);
        }

        // ---------------------------------------------------------------- prediction restore (residual -> samples, in place)

        private static void RestoreFixedPrediction(int order, Span<int> samples)
        {
            switch (order)
            {
                case 1:
                    for (var i = 1; i < samples.Length; i++)
                    {
                        samples[i] += samples[i - 1];
                    }

                    break;
                case 2:
                    for (var i = 2; i < samples.Length; i++)
                    {
                        samples[i] += (2 * samples[i - 1]) - samples[i - 2];
                    }

                    break;
                case 3:
                    for (var i = 3; i < samples.Length; i++)
                    {
                        samples[i] += (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3];
                    }

                    break;
                case 4:
                    for (var i = 4; i < samples.Length; i++)
                    {
                        samples[i] += (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4];
                    }

                    break;
            }
        }

        private static void RestoreLpcPrediction(ReadOnlySpan<int> coefficients, int shift, Span<int> samples)
        {
            var order = coefficients.Length;
            for (var i = order; i < samples.Length; i++)
            {
                var sum = 0;
                for (var j = 0; j < order; j++)
                {
                    sum += coefficients[j] * samples[i - 1 - j];
                }

                samples[i] += sum >> shift;
            }
        }

        private static void RestoreLpcPredictionWide(ReadOnlySpan<int> coefficients, int shift, Span<int> samples)
        {
            var order = coefficients.Length;
            for (var i = order; i < samples.Length; i++)
            {
                var sum = 0L;
                for (var j = 0; j < order; j++)
                {
                    sum += (long)coefficients[j] * samples[i - 1 - j];
                }

                samples[i] += (int)(sum >> shift);
            }
        }

        private static void RestoreLpcPredictionWide(ReadOnlySpan<int> coefficients, int shift, ReadOnlySpan<int> residual, Span<long> samples)
        {
            var order = coefficients.Length;
            for (var i = order; i < samples.Length; i++)
            {
                var sum = 0L;
                for (var j = 0; j < order; j++)
                {
                    sum += coefficients[j] * samples[i - 1 - j];
                }

                samples[i] = residual[i] + (sum >> shift);
            }
        }

        // ---------------------------------------------------------------- stereo decorrelation restore (RFC 9639 section 4.2)

        private void Decorrelate(int channelAssignment, int blockSize)
        {
            if (channelAssignment < ChannelAssignmentLeftSide)
            {
                return;
            }

            var sideChannel = channelAssignment == ChannelAssignmentRightSide ? 0 : 1;
            var side = _wideSide.AsSpan(0, blockSize);
            if (_bitsPerSample < 32)
            {
                var narrowSide = _samples[sideChannel];
                for (var i = 0; i < side.Length; i++)
                {
                    side[i] = narrowSide[i];
                }
            }

            var left = _samples[0].AsSpan(0, blockSize);
            var right = _samples[1].AsSpan(0, blockSize);
            switch (channelAssignment)
            {
                case ChannelAssignmentLeftSide:
                    for (var i = 0; i < left.Length; i++)
                    {
                        right[i] = (int)(left[i] - side[i]);
                    }

                    break;
                case ChannelAssignmentRightSide:
                    for (var i = 0; i < right.Length; i++)
                    {
                        left[i] = (int)(right[i] + side[i]);
                    }

                    break;
                default:
                    for (var i = 0; i < left.Length; i++)
                    {
                        var sideValue = side[i];
                        var mid = ((long)left[i] << 1) | (sideValue & 1);
                        left[i] = (int)((mid + sideValue) >> 1);
                        right[i] = (int)((mid - sideValue) >> 1);
                    }

                    break;
            }
        }
    }
}
