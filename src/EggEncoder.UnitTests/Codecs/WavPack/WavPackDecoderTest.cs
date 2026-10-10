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
        public void Decode_WithNonStandardSampleRate_Should_Throw()
        {
            // A genuine reference-encoder-produced file at 37800 Hz, which isn't one of WavPack's
            // 15 standard-sample-rate-table entries, so its block header's own sample rate index
            // field is the reserved "non-standard rate" sentinel.
            var act = () => WavPackDecoder.Decode(_nonStandardRateFixturePath, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<NotSupportedException>();
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
