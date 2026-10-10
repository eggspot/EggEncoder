namespace EggEncoder.Codecs.WavPack
{
    // Decodes the audio payload of one WavPack block: builds the cascaded decorrelation passes and
    // entropy coder state from the block's own metadata sub-blocks, then runs the shared
    // (interleaved, for stereo) entropy-decode + decorrelation-restore + joint-stereo-reconstruct
    // pipeline sample by sample.
    internal static class WavPackBlockDecoder
    {
        public static int[][] Decode(byte[] data, int metadataStart, int metadataEnd, WavPackBlockHeader header, out uint crc, out int extraShift)
        {
            var channels = header.IsMono ? 1 : 2;
            // A "false stereo" block reports a stereo output channel count in its header, but its
            // actual decorrelation/entropy bitstream data covers only one channel -- every metadata
            // sub-block below is sized accordingly, and that single decoded channel is duplicated
            // into both outputs afterward, rather than this block genuinely carrying two channels
            // of distinct audio data.
            var internalChannels = header.IsFalseStereo ? 1 : channels;
            var blockSamples = (int)header.BlockSamples;

            WavPackDecorrPass[]? passes = null;
            WavPackEntropyDecoder? entropy = null;
            WavPackBitReader? bitstream = null;
            extraShift = 0;

            foreach (var subBlock in WavPackMetadataSubBlock.ReadAll(data, metadataStart, metadataEnd))
            {
                switch (subBlock.FunctionId)
                {
                    case WavPackMetadataSubBlock.IdDecorrTerms:
                        passes = ParseDecorrTerms(subBlock.Data);
                        break;
                    case WavPackMetadataSubBlock.IdDecorrWeights:
                        ParseDecorrWeights(subBlock.Data, passes!, internalChannels);
                        break;
                    case WavPackMetadataSubBlock.IdDecorrSamples:
                        ParseDecorrSamples(subBlock.Data, passes!, internalChannels);
                        break;
                    case WavPackMetadataSubBlock.IdEntropyVars:
                        entropy = ParseEntropyVars(subBlock.Data, internalChannels);
                        break;
                    case WavPackMetadataSubBlock.IdInt32Info:
                        extraShift = ParseInt32InfoShift(subBlock.Data);
                        break;
                    case WavPackMetadataSubBlock.IdWvBitstream:
                        bitstream = new WavPackBitReader(subBlock.Data.Array!, subBlock.Data.Offset, subBlock.Data.Offset + subBlock.Data.Count);
                        break;
                }
            }

            if (passes is null || entropy is null || bitstream is null)
            {
                throw new InvalidDataException("A WavPack block is missing its decorrelation terms, entropy variables, or bitstream metadata sub-block.");
            }

            var outputs = new int[channels][];
            for (var c = 0; c < channels; c++)
            {
                outputs[c] = new int[blockSamples];
            }

            crc = WavPackCrc.Seed;
            var pos = 0;

            if (internalChannels == 1)
            {
                for (var i = 0; i < blockSamples; i++)
                {
                    var value = entropy.DecodeValue(bitstream, 0);
                    foreach (var pass in passes)
                    {
                        value = ApplyMono(pass, pos, value);
                    }

                    outputs[0][i] = value;
                    crc = WavPackCrc.Append(crc, value);
                    pos = (pos + 1) & 7;
                }

                if (header.IsFalseStereo)
                {
                    Array.Copy(outputs[0], outputs[1], blockSamples);
                }
            }
            else
            {
                for (var i = 0; i < blockSamples; i++)
                {
                    var left = entropy.DecodeValue(bitstream, 0);
                    var right = entropy.DecodeValue(bitstream, 1);

                    foreach (var pass in passes)
                    {
                        (left, right) = ApplyStereo(pass, pos, left, right);
                    }

                    if (header.IsJointStereo)
                    {
                        right -= left >> 1;
                        left += right;
                    }

                    outputs[0][i] = left;
                    outputs[1][i] = right;
                    crc = WavPackCrc.Append(crc, left);
                    crc = WavPackCrc.Append(crc, right);
                    pos = (pos + 1) & 7;
                }
            }

            return outputs;
        }

        private static int ApplyMono(WavPackDecorrPass pass, int pos, int value)
        {
            switch (pass.Term)
            {
                case >= 1 and <= 8:
                {
                    var source = pass.SamplesA[pos];
                    var output = value + WavPackDecorrPass.ApplyWeight(pass.WeightA, source);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, source, value);
                    pass.SamplesA[(pos + pass.Term) & 7] = output;
                    return output;
                }

                case 17 or 18:
                {
                    var source = TwoTapSource(pass.Term, pass.SamplesA);
                    var output = value + WavPackDecorrPass.ApplyWeight(pass.WeightA, source);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, source, value);
                    pass.SamplesA[1] = pass.SamplesA[0];
                    pass.SamplesA[0] = output;
                    return output;
                }

                default:
                    throw new InvalidDataException($"A mono WavPack block uses cross-channel decorrelation term {pass.Term}, which is only valid for stereo.");
            }
        }

        private static (int Left, int Right) ApplyStereo(WavPackDecorrPass pass, int pos, int left, int right)
        {
            switch (pass.Term)
            {
                case >= 1 and <= 8:
                {
                    var sourceA = pass.SamplesA[pos];
                    var outputA = left + WavPackDecorrPass.ApplyWeight(pass.WeightA, sourceA);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, sourceA, left);
                    pass.SamplesA[(pos + pass.Term) & 7] = outputA;

                    var sourceB = pass.SamplesB[pos];
                    var outputB = right + WavPackDecorrPass.ApplyWeight(pass.WeightB, sourceB);
                    pass.WeightB = WavPackDecorrPass.UpdateWeight(pass.WeightB, pass.Delta, sourceB, right);
                    pass.SamplesB[(pos + pass.Term) & 7] = outputB;

                    return (outputA, outputB);
                }

                case 17 or 18:
                {
                    var sourceA = TwoTapSource(pass.Term, pass.SamplesA);
                    var outputA = left + WavPackDecorrPass.ApplyWeight(pass.WeightA, sourceA);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, sourceA, left);
                    pass.SamplesA[1] = pass.SamplesA[0];
                    pass.SamplesA[0] = outputA;

                    var sourceB = TwoTapSource(pass.Term, pass.SamplesB);
                    var outputB = right + WavPackDecorrPass.ApplyWeight(pass.WeightB, sourceB);
                    pass.WeightB = WavPackDecorrPass.UpdateWeight(pass.WeightB, pass.Delta, sourceB, right);
                    pass.SamplesB[1] = pass.SamplesB[0];
                    pass.SamplesB[0] = outputB;

                    return (outputA, outputB);
                }

                case -1:
                {
                    var prevRight = pass.SamplesA[0];
                    var outputLeft = left + WavPackDecorrPass.ApplyWeight(pass.WeightA, prevRight);
                    pass.WeightA = WavPackDecorrPass.UpdateWeightClamped(pass.WeightA, pass.Delta, prevRight, left);

                    var outputRight = right + WavPackDecorrPass.ApplyWeight(pass.WeightB, outputLeft);
                    pass.WeightB = WavPackDecorrPass.UpdateWeightClamped(pass.WeightB, pass.Delta, outputLeft, right);

                    pass.SamplesA[0] = outputRight;
                    return (outputLeft, outputRight);
                }

                case -2:
                {
                    var prevLeft = pass.SamplesB[0];
                    var outputRight = right + WavPackDecorrPass.ApplyWeight(pass.WeightB, prevLeft);
                    pass.WeightB = WavPackDecorrPass.UpdateWeightClamped(pass.WeightB, pass.Delta, prevLeft, right);

                    var outputLeft = left + WavPackDecorrPass.ApplyWeight(pass.WeightA, outputRight);
                    pass.WeightA = WavPackDecorrPass.UpdateWeightClamped(pass.WeightA, pass.Delta, outputRight, left);

                    pass.SamplesB[0] = outputLeft;
                    return (outputLeft, outputRight);
                }

                case -3:
                {
                    var prevLeft = pass.SamplesB[0];
                    var outputRight = right + WavPackDecorrPass.ApplyWeight(pass.WeightB, prevLeft);
                    pass.WeightB = WavPackDecorrPass.UpdateWeightClamped(pass.WeightB, pass.Delta, prevLeft, right);

                    var prevRight = pass.SamplesA[0]; // the OLD value, read before this pass overwrites it below
                    var outputLeft = left + WavPackDecorrPass.ApplyWeight(pass.WeightA, prevRight);
                    pass.WeightA = WavPackDecorrPass.UpdateWeightClamped(pass.WeightA, pass.Delta, prevRight, left);

                    pass.SamplesA[0] = outputRight;
                    pass.SamplesB[0] = outputLeft;
                    return (outputLeft, outputRight);
                }

                default:
                    throw new InvalidDataException($"Unsupported WavPack decorrelation term {pass.Term}.");
            }
        }

        // Term 17: a simple 2-sample linear extrapolation. Term 18: the same, damped by half.
        private static int TwoTapSource(int term, int[] samples) =>
            term == 17
                ? (2 * samples[0]) - samples[1]
                : ((3 * samples[0]) - samples[1]) >> 1;

        // WP_ID_DECORR_TERMS lists passes in the order they were applied during *encoding*; decode
        // must undo them in the opposite order, so the metadata's own byte order is reversed into
        // the array decode walks forward (array index 0 = first pass undone).
        private static WavPackDecorrPass[] ParseDecorrTerms(ArraySegment<byte> data)
        {
            var count = data.Count;
            var passes = new WavPackDecorrPass[count];
            for (var i = 0; i < count; i++)
            {
                var b = data[i];
                var term = (b & 0x1F) - 5;
                var delta = b >> 5;
                passes[count - 1 - i] = new WavPackDecorrPass { Term = term, Delta = delta };
            }

            return passes;
        }

        // Bounded by the sub-block's own declared size, NOT by the number of passes -- like
        // ParseDecorrSamples below, a real encoder commonly only carries weights for the first few
        // passes, and any pass this loop never reaches simply keeps its default weight of 0. A
        // trailing byte that can't form one more complete (2-byte, for stereo) pass is dropped
        // rather than read as a partial/mismatched pass.
        private static void ParseDecorrWeights(ArraySegment<byte> data, WavPackDecorrPass[] passes, int channels)
        {
            var bytesPerPass = channels == 2 ? 2 : 1;
            var coveredPasses = data.Count / bytesPerPass;
            var p = 0;

            for (var i = passes.Length - 1; i >= 0 && i >= passes.Length - coveredPasses; i--)
            {
                var pass = passes[i];
                pass.WeightA = WavPackDecorrPass.RestoreWeight((sbyte)data[p++]);
                if (channels == 2)
                {
                    pass.WeightB = WavPackDecorrPass.RestoreWeight((sbyte)data[p++]);
                }
            }
        }

        // Bounded by the sub-block's own declared size, NOT by the number of passes -- a real
        // encoder commonly only has room (or need) to carry history for the first few passes
        // across a block boundary, and any pass this loop never reaches simply keeps zeroed
        // history (matching a freshly allocated WavPackDecorrPass), not an error.
        private static void ParseDecorrSamples(ArraySegment<byte> data, WavPackDecorrPass[] passes, int channels)
        {
            var size = data.Count;
            var p = 0;

            for (var i = passes.Length - 1; i >= 0; i--)
            {
                var pass = passes[i];
                var valuesPerChannel = pass.Term switch
                {
                    >= 1 and <= 8 => pass.Term,
                    17 or 18 => 2,
                    _ => 1,
                };

                var byteCount = valuesPerChannel * 2 * channels;
                if (p + byteCount > size)
                {
                    break;
                }

                for (var v = 0; v < valuesPerChannel; v++)
                {
                    pass.SamplesA[v] = WavPackExp2.Expand(ReadInt16Le(data, p));
                    p += 2;
                }

                if (channels == 2)
                {
                    for (var v = 0; v < valuesPerChannel; v++)
                    {
                        pass.SamplesB[v] = WavPackExp2.Expand(ReadInt16Le(data, p));
                        p += 2;
                    }
                }
            }
        }

        private static WavPackEntropyDecoder ParseEntropyVars(ArraySegment<byte> data, int channels)
        {
            var entropy = new WavPackEntropyDecoder(channels);
            var p = 0;
            for (var c = 0; c < channels; c++)
            {
                for (var m = 0; m < 3; m++)
                {
                    entropy.SeedMedian(c, m, WavPackExp2.Expand(ReadInt16Le(data, p)));
                    p += 2;
                }
            }

            return entropy;
        }

        // WP_ID_INT32_INFO's 4-byte payload: byte 0 is an "extra bits" count used only for
        // integers wider than 24 bits (out of scope for this 16/24-bit-only decoder, so ignored
        // here); bytes 1-3 each independently carry an additional post-decode left-shift amount,
        // applied on top of (not replacing) the block header's own 5-bit left-shift field -- the
        // last of the three that's nonzero wins, since a real encoder only ever populates one.
        private static int ParseInt32InfoShift(ArraySegment<byte> data)
        {
            var shift = 0;
            for (var i = 1; i <= 3; i++)
            {
                if (data[i] != 0)
                {
                    shift = data[i];
                }
            }

            return shift;
        }

        private static short ReadInt16Le(ArraySegment<byte> data, int offset) => (short)(data[offset] | (data[offset + 1] << 8));
    }
}
