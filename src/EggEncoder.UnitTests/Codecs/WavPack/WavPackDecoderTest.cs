using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackDecoderTest
    {
        private static readonly string _stereoFixturePath = Path.GetFullPath("Codecs/WavPack/sample_ffmpeg.wv");
        private static readonly string _threeChannelFixturePath = Path.GetFullPath("Codecs/WavPack/sample_3channel.wv");
        private static readonly string _floatFixturePath = Path.GetFullPath("Codecs/WavPack/sample_float.wv");
        private static readonly string _eightBitFixturePath = Path.GetFullPath("Codecs/WavPack/sample_8bit.wv");
        private static readonly string _hybridFixturePath = Path.GetFullPath("Codecs/WavPack/sample_hybrid_wavpack.wv");
        private static readonly string _nonStandardRateFixturePath = Path.GetFullPath("Codecs/WavPack/sample_nonstandard_rate_wavpack.wv");
        private static readonly string _monoFixturePath = Path.GetFullPath("Codecs/WavPack/sample_mono_ffmpeg.wv");
        private static readonly string _splitMonoBlocksFixturePath = Path.GetFullPath("Codecs/WavPack/sample_split_mono_blocks_wavpack.wv");
        private static readonly string _fullScaleFixturePath = Path.GetFullPath("Codecs/WavPack/sample_fullscale_wavpack.wv");

        [Fact]
        public void Decode_StereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var decoded = new List<int>();

            var streamInfo = WavPackDecoder.Decode(_stereoFixturePath, (block, channels, sampleRate, bitsPerSample, _) =>
            {
                channels.Should().Be(2);
                sampleRate.Should().Be(44100);
                bitsPerSample.Should().Be(16);
                decoded.AddRange(block.ToArray());
            });

            streamInfo.Channels.Should().Be(2);
            streamInfo.SampleRate.Should().Be(44100);
            streamInfo.BitsPerSample.Should().Be(16);
            decoded.Should().NotBeEmpty();
        }

        [Fact]
        public void Decode_Should_Invoke_The_Callback_MultipleTimes_With_Consistent_Metadata()
        {
            var callbackCount = 0;
            long? firstReportedTotal = null;
            var totalDecodedFrames = 0L;

            var streamInfo = WavPackDecoder.Decode(_stereoFixturePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                callbackCount++;
                channels.Should().Be(2);
                sampleRate.Should().Be(44100);
                bitsPerSample.Should().Be(16);

                firstReportedTotal ??= totalSamples;
                totalSamples.Should().Be(firstReportedTotal, "every callback should report the same, already-known total");

                totalDecodedFrames += block.Length / channels;
            });

            callbackCount.Should().BeGreaterThan(1);
            totalDecodedFrames.Should().Be(streamInfo.TotalSamples);
        }

        [Fact]
        public void Decode_WithUnsupportedChannelCount_Should_Throw()
        {
            // WavPackEncoderSession.OpenSession itself blocks anything but mono/stereo, so to exercise
            // WavPackDecoder's own matching guard this uses a genuine 3-channel .wv file produced by
            // ffmpeg's own (independent) WavPack encoder -- a real file the official libwavpack can
            // open and report NumChannels == 3 for, rather than a hand-corrupted byte that might fail
            // for an unrelated reason before ever reaching this decoder's own check.
            var act = () => WavPackDecoder.Decode(_threeChannelFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithFloatingPointSamples_Should_Throw()
        {
            // Same rationale as the 3-channel case above: a genuine ffmpeg-produced floating-point
            // WavPack file, to exercise this decoder's lossless-integer-only guard for real.
            var act = () => WavPackDecoder.Decode(_floatFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithUnsupportedBitDepth_Should_Throw()
        {
            // Same rationale again: a genuine ffmpeg-produced 8-bit WavPack file (ffmpeg's wavpack
            // encoder supports u8p/s16p/s32p/fltp), to exercise this decoder's 16/24-bit-only guard
            // for real -- this is the one branch in WavPackDecoder.Decode that no other test here
            // reaches, since every other fixture and every round-trip test is 16 or 24-bit.
            var act = () => WavPackDecoder.Decode(_eightBitFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithHybridLossyFile_Should_Throw()
        {
            // A genuine reference-encoder-produced hybrid (lossy) WavPack file (ffmpeg's own
            // wavpack encoder has no hybrid mode at all, so this fixture was produced with the
            // official wavpack CLI's -b<n> option instead), to exercise this decoder's
            // lossless-only guard for real.
            var act = () => WavPackDecoder.Decode(_hybridFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Decode_WithNonStandardSampleRateButNoRateMetadata_Should_Throw()
        {
            // A genuine reference-encoder-produced 37800 Hz file -- not one of WavPack's 15
            // standard-sample-rate-table entries, so its block header's own sample-rate-index field
            // is the reserved "non-standard, consult metadata" sentinel -- but with its own
            // WP_ID_SAMPLE_RATE metadata sub-block (the thing that normally supplies the real rate
            // for this exact case) stripped out, to exercise the genuine error path: a well-formed
            // header promising a non-standard rate that never actually shows up.
            var fullBytes = File.ReadAllBytes(_nonStandardRateFixturePath);
            var withoutRateMetadata = RemoveSampleRateMetadataSubBlock(fullBytes);

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_norate_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, withoutRateMetadata);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        // Walks the first block's own metadata sub-blocks (per the WavPack format spec's id/size
        // envelope) and splices out the WP_ID_SAMPLE_RATE (0x27) one, patching the block's own
        // ckSize down by the removed byte count so the result is still a structurally valid block.
        private static byte[] RemoveSampleRateMetadataSubBlock(byte[] data)
        {
            const int sampleRateId = 0x27;
            var ckSize = BitConverter.ToUInt32(data, 4);
            var blockEnd = 8 + (int)ckSize;

            var p = 32;
            while (p < blockEnd)
            {
                var idByte = data[p];
                var functionId = idByte & 0x3F;
                var isLarge = (idByte & 0x80) != 0;
                var headerLength = isLarge ? 4 : 2;
                var wordCount = isLarge ? (data[p + 1] | (data[p + 2] << 8) | (data[p + 3] << 16)) : data[p + 1];
                var subBlockLength = headerLength + (wordCount * 2);

                if (functionId == sampleRateId)
                {
                    var result = new byte[data.Length - subBlockLength];
                    Array.Copy(data, 0, result, 0, p);
                    Array.Copy(data, p + subBlockLength, result, p, data.Length - p - subBlockLength);
                    BitConverter.GetBytes(ckSize - (uint)subBlockLength).CopyTo(result, 4);
                    return result;
                }

                p += subBlockLength;
            }

            throw new InvalidOperationException("Fixture did not contain a WP_ID_SAMPLE_RATE sub-block to remove.");
        }

        [Fact]
        public void Decode_WithFileTruncatedMidBlock_Should_Throw()
        {
            var fullBytes = File.ReadAllBytes(_monoFixturePath);
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_truncated_{Guid.NewGuid():N}.wv");
            try
            {
                // Keep the full 32-byte block header (so it parses and reports a real total-sample
                // count) but cut off partway through the metadata/bitstream payload it declares.
                File.WriteAllBytes(filePath, fullBytes[..48]);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("truncated");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithCrcMismatch_Should_Throw()
        {
            var corruptBytes = File.ReadAllBytes(_monoFixturePath);

            // Flip a bit well inside the first block's bitstream payload (past the 32-byte header
            // and its metadata sub-blocks) so the file still parses structurally but decodes to
            // the wrong samples, tripping the block's own recorded CRC check.
            corruptBytes[100] ^= 0xFF;

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_crc_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, corruptBytes);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("CRC mismatch");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithTrailingNonBlockData_Should_Tolerate_It()
        {
            // A trailing APEv2 tag (or similar) after the last real WavPack block is normal in
            // real-world files and must not be treated as corruption, as long as at least one
            // real block was already found.
            var originalBytes = File.ReadAllBytes(_monoFixturePath);
            var withTrailingGarbage = new byte[originalBytes.Length + 16];
            originalBytes.CopyTo(withTrailingGarbage, 0);
            for (var i = 0; i < 16; i++)
            {
                withTrailingGarbage[originalBytes.Length + i] = 0xAB;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_trailing_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, withTrailingGarbage);

                var decoded = new List<int>();
                var streamInfo = WavPackDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.TotalSamples.Should().Be(13230);
                decoded.Should().HaveCount(13230);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMalformedInt32InfoLength_Should_Throw()
        {
            // sample_fullscale_wavpack.wv carries a genuine WP_ID_INT32_INFO sub-block (confirmed at
            // byte offset 168); marking it "odd length" (the format's own mask for "one byte shorter
            // than declared") shrinks its data from the required 4 bytes to 3, without disturbing
            // any other sub-block's own position.
            var corruptBytes = File.ReadAllBytes(_fullScaleFixturePath);
            const int int32InfoIdOffset = 168;
            corruptBytes[int32InfoIdOffset] |= 0x40;

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_int32info_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, corruptBytes);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("WP_ID_INT32_INFO");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithNonInitialBlockWhereSequenceStartExpected_Should_Throw()
        {
            // Takes a genuine single standalone block (the first block of sample_mono_ffmpeg.wv) and
            // appends a second copy of it with its own "initial block of sequence" flag bit cleared,
            // to exercise the guard against a block sequence starting mid-stream with something
            // other than a real sequence-start block (a malformed/corrupt file, since a well-formed
            // one always closes every sequence with a final-flagged block before the next one
            // starts).
            var fullBytes = File.ReadAllBytes(_monoFixturePath);
            var firstBlockCkSize = BitConverter.ToUInt32(fullBytes, 4);
            var firstBlockEnd = 8 + (int)firstBlockCkSize;
            var firstBlock = fullBytes[..firstBlockEnd];

            var secondBlock = (byte[])firstBlock.Clone();
            secondBlock[25] &= 0xF7; // clear bit 11 (the "initial block of sequence" flag) of the flags field

            var combined = new byte[firstBlock.Length + secondBlock.Length];
            firstBlock.CopyTo(combined, 0);
            secondBlock.CopyTo(combined, firstBlock.Length);

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_noninitial_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, combined);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("isn't the start of its own per-frame sequence");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithFileTruncatedBetweenSequenceBlocks_Should_Throw()
        {
            // Keeps only the first (initial, non-final) block of sample_split_mono_blocks_wavpack.wv's
            // two-block stereo sequence, so the file ends cleanly after a whole block but mid-sequence
            // -- distinct from Decode_WithFileTruncatedMidBlock_Should_Throw, which cuts off inside a
            // single block's own declared size.
            var fullBytes = File.ReadAllBytes(_splitMonoBlocksFixturePath);
            var firstBlockCkSize = BitConverter.ToUInt32(fullBytes, 4);
            var firstBlockEnd = 8 + (int)firstBlockCkSize;

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_midsequence_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, fullBytes[..firstBlockEnd]);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("multi-block WavPack per-frame sequence");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMismatchedBlockSamplesWithinSequence_Should_Throw()
        {
            // sample_split_mono_blocks_wavpack.wv is a genuine reference-encoder-produced file whose
            // stereo pair is split into two single-channel blocks (initial + final) in the same
            // per-frame sequence, both declaring 4 samples. Corrupting the second block's own
            // declared sample count exercises the guard against a sequence whose sibling blocks
            // disagree on how many samples they each cover.
            var corruptBytes = File.ReadAllBytes(_splitMonoBlocksFixturePath);
            const int secondBlockSamplesOffset = 190 + 20;
            BitConverter.GetBytes(5u).CopyTo(corruptBytes, secondBlockSamplesOffset);

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_blocksamples_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, corruptBytes);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("sample count doesn't match");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithChannelCountChangingMidStream_Should_Throw()
        {
            // Concatenates a genuine 2-channel-via-split-mono-blocks sequence with a genuine
            // 1-channel sequence (sample_mono_ffmpeg.wv) to exercise the guard against a later
            // per-frame block sequence in the same file reporting a different total channel count
            // than the one the file started with.
            var firstFileBytes = File.ReadAllBytes(_splitMonoBlocksFixturePath);
            var secondFileBytes = File.ReadAllBytes(_monoFixturePath);
            var combined = new byte[firstFileBytes.Length + secondFileBytes.Length];
            firstFileBytes.CopyTo(combined, 0);
            secondFileBytes.CopyTo(combined, firstFileBytes.Length);

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_channelchange_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, combined);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().Contain("channel count can't change mid-stream");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMissingOrMalformedFile_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_malformed_{Guid.NewGuid():N}.wv");
            try
            {
                File.WriteAllBytes(filePath, [1, 2, 3, 4]);

                var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

                // Asserting only the exception type would leave the actual message text unverified --
                // a bug that always threw the same bare "as WavPack: " prefix regardless of the real
                // failure reason would still pass a type-only check.
                act.Should().ThrowExactly<InvalidDataException>()
                    .Which.Message.Should().NotEndWith("as WavPack: ", "the exception message should include a real, non-empty reason");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithNonExistentFile_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_decoder_missing_{Guid.NewGuid():N}.wv");

            var act = () => WavPackDecoder.Decode(filePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().NotEndWith("as WavPack: ", "the exception message should include a real, non-empty reason");
        }
    }
}
