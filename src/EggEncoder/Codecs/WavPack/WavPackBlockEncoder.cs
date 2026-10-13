namespace EggEncoder.Codecs.WavPack
{
    // Encodes one WavPack block's audio payload -- the inverse of WavPackBlockDecoder.Decode. An
    // MVP scope, deliberately simple rather than compression-competitive (matching this project's
    // own established "correctness first" precedent for a from-scratch encoder -- see
    // FlacEncoder's items 2/3 history): a single, fixed decorrelation term (1, a plain first-order
    // predictor) applied independently per channel (no joint stereo, no cross-channel terms, no
    // multi-term cascade), with decorrelation weights and entropy medians always starting fresh
    // (seeded to 0) for every block -- valid since WavPackBlockDecoder never requires
    // WP_ID_DECORR_WEIGHTS/WP_ID_DECORR_SAMPLES to be present at all (a term's weight/history
    // simply default to 0, a legitimate cold start) and this project's own block-independent
    // design (see WavPackEncoderSession) never carries state across a block boundary anyway.
    internal static class WavPackBlockEncoder
    {
        private const int Term = 1;
        private const int Delta = 2;

        public static (byte[] DecorrTerms, byte[] EntropyVars, byte[] Bitstream, uint Crc) Encode(int[][] channelSamples, int channels)
        {
            var blockSamples = channelSamples[0].Length;
            var pass = new WavPackDecorrPass { Term = Term, Delta = Delta };

            var symbols = new List<(int Channel, int Value)>(blockSamples * channels);
            var crc = WavPackCrc.Seed;
            var pos = 0;

            for (var i = 0; i < blockSamples; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var sample = channelSamples[c][i];
                    crc = WavPackCrc.Append(crc, sample);

                    var source = c == 0 ? pass.SamplesA[pos] : pass.SamplesB[pos];
                    var residual = sample - WavPackDecorrPass.ApplyWeight(c == 0 ? pass.WeightA : pass.WeightB, source);
                    var newWeight = WavPackDecorrPass.UpdateWeight(c == 0 ? pass.WeightA : pass.WeightB, pass.Delta, source, residual);

                    if (c == 0)
                    {
                        pass.WeightA = newWeight;
                        pass.SamplesA[(pos + Term) & 7] = sample;
                    }
                    else
                    {
                        pass.WeightB = newWeight;
                        pass.SamplesB[(pos + Term) & 7] = sample;
                    }

                    symbols.Add((c, residual));
                }

                pos = (pos + 1) & 7;
            }

            var sums = new long[channels];
            var counts = new int[channels];
            foreach (var (channel, value) in symbols)
            {
                sums[channel] += Math.Abs((long)value);
                counts[channel]++;
            }

            var entropy = new WavPackEntropyEncoder(channels);
            var entropyVars = new byte[channels * 3 * 2];
            for (var c = 0; c < channels; c++)
            {
                // Seed every median to this channel's own average residual magnitude (scaled by 16,
                // inverting Band's own (m>>4)+1 so Band(seed) roughly equals that average) instead
                // of the mathematically-legal-but-decoder-hostile cold start of 0 -- see
                // WavPackEntropyEncoder.SeedMedian's own doc comment for why.
                var seed = counts[c] > 0 ? (sums[c] / counts[c]) * 16 : 0;

                // The decoder only ever reconstructs this seed via Expand(Compress(seed)) -- its
                // own lossy round trip -- so the encoder's in-memory starting median must match
                // that reconstructed value exactly, not the unrounded measurement, or the two
                // sides' median trackers diverge from the very first symbol onward.
                var seedBytes = WavPackExp2.Compress(seed);
                entropy.SeedMedian(c, WavPackExp2.Expand(seedBytes));
                entropyVars[(c * 6) + 0] = (byte)seedBytes;
                entropyVars[(c * 6) + 1] = (byte)(seedBytes >> 8);
                entropyVars[(c * 6) + 2] = (byte)seedBytes;
                entropyVars[(c * 6) + 3] = (byte)(seedBytes >> 8);
                entropyVars[(c * 6) + 4] = (byte)seedBytes;
                entropyVars[(c * 6) + 5] = (byte)(seedBytes >> 8);
            }

            var writer = new WavPackBitWriter();
            entropy.Write(writer, symbols);

            var decorrTerms = new byte[] { (byte)(((Term + 5) & 0x1F) | (Delta << 5)) };

            return (decorrTerms, entropyVars, writer.Finish(), crc);
        }
    }
}
