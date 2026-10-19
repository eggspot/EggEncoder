namespace EggEncoder.Codecs.WavPack
{
    // Encodes one WavPack block's audio payload -- the inverse of WavPackBlockDecoder.Decode. Runs
    // a fixed 5-pass decorrelation cascade (terms 18, 18, 2, 17, 3, all delta=2) applied
    // independently per channel (no joint stereo, no cross-channel terms), with decorrelation
    // weights and entropy medians always starting fresh (seeded to 0) for every block -- valid
    // since WavPackBlockDecoder never requires WP_ID_DECORR_WEIGHTS/WP_ID_DECORR_SAMPLES to be
    // present at all (a term's weight/history simply default to 0, a legitimate cold start) and
    // this project's own block-independent design (see WavPackEncoderSession) never carries state
    // across a block boundary anyway.
    //
    // This exact 5-term cascade (not a guess -- see docs/managed-codec-rewrite-plan.md item 5a's
    // own status note) is what the real reference `wavpack` CLI's own fastest mode (`-x0`) writes
    // into WP_ID_DECORR_TERMS, confirmed by encoding a real fixture with it and reading that
    // metadata sub-block back with this project's own WavPackBlockHeader/WavPackMetadataSubBlock --
    // not reverse-engineered from documentation, since the format spec explicitly does not
    // describe the decorrelation algorithm itself. Replacing item 5's original single-term MVP
    // with this cascade did NOT close the real `wvunpack` CLI's own compatibility gap, though --
    // measured directly before/after on a representative content matrix, the result is a genuine
    // mixed bag (some content that used to be rejected outright now partially decodes; some
    // content that used to partially decode now gets rejected outright), which points away from
    // the decorrelation cascade itself as the real remaining blocker -- see that same status note
    // for the full before/after data and the more likely remaining suspect (the entropy coder).
    internal static class WavPackBlockEncoder
    {
        // Mirrors the real reference encoder's own fastest-mode term list exactly (see this type's
        // own doc comment for how that was confirmed, not assumed) -- WavPackBlockDecoder already
        // supports every one of these terms for decode, so no new decode-side work was needed.
        // internal (not private) so WavPackBlockEncoderTest can assert the exact term/delta list
        // directly rather than only inferring it indirectly from a round trip.
        internal static readonly (int Term, int Delta)[] TermCascade =
        [
            (18, 2),
            (18, 2),
            (2, 2),
            (17, 2),
            (3, 2),
        ];

        public static (byte[] DecorrTerms, byte[] EntropyVars, byte[] Bitstream, uint Crc) Encode(int[][] channelSamples, int channels)
        {
            var blockSamples = channelSamples[0].Length;
            var passes = new WavPackDecorrPass[TermCascade.Length];
            for (var p = 0; p < TermCascade.Length; p++)
            {
                passes[p] = new WavPackDecorrPass { Term = TermCascade[p].Term, Delta = TermCascade[p].Delta };
            }

            var symbols = new List<(int Channel, int Value)>(blockSamples * channels);
            var crc = WavPackCrc.Seed;
            var pos = 0;

            if (channels == 1)
            {
                for (var i = 0; i < blockSamples; i++)
                {
                    var sample = channelSamples[0][i];
                    crc = WavPackCrc.Append(crc, sample);

                    var value = sample;
                    foreach (var pass in passes)
                    {
                        value = EncodeMono(pass, pos, value);
                    }

                    symbols.Add((0, value));
                    pos = (pos + 1) & 7;
                }
            }
            else
            {
                for (var i = 0; i < blockSamples; i++)
                {
                    var left = channelSamples[0][i];
                    var right = channelSamples[1][i];
                    crc = WavPackCrc.Append(crc, left);
                    crc = WavPackCrc.Append(crc, right);

                    foreach (var pass in passes)
                    {
                        (left, right) = EncodeStereo(pass, pos, left, right);
                    }

                    symbols.Add((0, left));
                    symbols.Add((1, right));
                    pos = (pos + 1) & 7;
                }
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

            // WP_ID_DECORR_TERMS lists passes in the order they were applied during encoding (see
            // WavPackBlockDecoder.ParseDecorrTerms's own doc comment -- it reverses this same byte
            // order back into its own apply-order array), so passes[0..] is written directly, not
            // reversed.
            var decorrTerms = new byte[passes.Length];
            for (var p = 0; p < passes.Length; p++)
            {
                decorrTerms[p] = (byte)(((passes[p].Term + 5) & 0x1F) | (passes[p].Delta << 5));
            }

            return (decorrTerms, entropyVars, writer.Finish(), crc);
        }

        // The algebraic inverse of WavPackBlockDecoder.ApplyMono: takes a real sample, returns the
        // residual this pass contributes toward the final bitstream value, and advances this
        // pass's own weight/history state exactly the way the matching decode call would from the
        // residual this produces -- so a real decoder replaying this block's own bitstream ends up
        // with bit-identical state at every step. internal (not private) so
        // WavPackBlockEncoderTest can exercise its own default/unsupported-term branch directly --
        // TermCascade above never actually produces one, so no round trip through Encode can reach
        // it.
        internal static int EncodeMono(WavPackDecorrPass pass, int pos, int sample)
        {
            switch (pass.Term)
            {
                case >= 1 and <= 8:
                {
                    var source = pass.SamplesA[pos];
                    var residual = sample - WavPackDecorrPass.ApplyWeight(pass.WeightA, source);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, source, residual);
                    pass.SamplesA[(pos + pass.Term) & 7] = sample;
                    return residual;
                }

                case 17 or 18:
                {
                    var source = WavPackDecorrPass.TwoTapSource(pass.Term, pass.SamplesA);
                    var residual = sample - WavPackDecorrPass.ApplyWeight(pass.WeightA, source);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, source, residual);
                    pass.SamplesA[1] = pass.SamplesA[0];
                    pass.SamplesA[0] = sample;
                    return residual;
                }

                default:
                    throw new NotSupportedException($"Decorrelation term {pass.Term} is not supported by this encoder's cascade.");
            }
        }

        // The algebraic inverse of WavPackBlockDecoder.ApplyStereo's own terms 1-8/17/18 branches
        // (the only ones this encoder's cascade ever uses -- the cross-channel terms -1/-2/-3 are
        // joint-stereo-only and this encoder never writes IsJointStereo, so they're intentionally
        // not implemented here).
        internal static (int Left, int Right) EncodeStereo(WavPackDecorrPass pass, int pos, int left, int right)
        {
            switch (pass.Term)
            {
                case >= 1 and <= 8:
                {
                    var sourceA = pass.SamplesA[pos];
                    var residualA = left - WavPackDecorrPass.ApplyWeight(pass.WeightA, sourceA);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, sourceA, residualA);
                    pass.SamplesA[(pos + pass.Term) & 7] = left;

                    var sourceB = pass.SamplesB[pos];
                    var residualB = right - WavPackDecorrPass.ApplyWeight(pass.WeightB, sourceB);
                    pass.WeightB = WavPackDecorrPass.UpdateWeight(pass.WeightB, pass.Delta, sourceB, residualB);
                    pass.SamplesB[(pos + pass.Term) & 7] = right;

                    return (residualA, residualB);
                }

                case 17 or 18:
                {
                    var sourceA = WavPackDecorrPass.TwoTapSource(pass.Term, pass.SamplesA);
                    var residualA = left - WavPackDecorrPass.ApplyWeight(pass.WeightA, sourceA);
                    pass.WeightA = WavPackDecorrPass.UpdateWeight(pass.WeightA, pass.Delta, sourceA, residualA);
                    pass.SamplesA[1] = pass.SamplesA[0];
                    pass.SamplesA[0] = left;

                    var sourceB = WavPackDecorrPass.TwoTapSource(pass.Term, pass.SamplesB);
                    var residualB = right - WavPackDecorrPass.ApplyWeight(pass.WeightB, sourceB);
                    pass.WeightB = WavPackDecorrPass.UpdateWeight(pass.WeightB, pass.Delta, sourceB, residualB);
                    pass.SamplesB[1] = pass.SamplesB[0];
                    pass.SamplesB[0] = right;

                    return (residualA, residualB);
                }

                default:
                    throw new NotSupportedException($"Decorrelation term {pass.Term} is not supported by this encoder's cascade.");
            }
        }
    }
}
