using EggEncoder.Codecs.Flac;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacDecoderTest
    {
        // ---------------------------------------------------------------- happy path: structural cases no real encoder reaches

        [Fact]
        public void Decode_8BitMonoConstantSubframe_Should_ReproduceTheConstantValue()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 8, sampleRate: 8000, blockSize: 16, constantValuePerChannel: [42]);

            var (info, samples) = DecodeTemp(bytes);

            info.Channels.Should().Be(1);
            info.BitsPerSample.Should().Be(8);
            info.SampleRate.Should().Be(8000);
            info.TotalSamples.Should().Be(16);
            samples.Should().Equal(Enumerable.Repeat(42, 16));
        }

        [Fact]
        public void Decode_32BitStereoIndependentConstantSubframes_Should_ReproduceEachChannelsValue()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 2, bitsPerSample: 32, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [100000, -200000]);

            var (info, samples) = DecodeTemp(bytes);

            info.BitsPerSample.Should().Be(32);
            var expected = new List<int>();
            for (var i = 0; i < 16; i++)
            {
                expected.Add(100000);
                expected.Add(-200000);
            }

            samples.Should().Equal(expected);
        }

        [Fact]
        public void Decode_32BitLeftSideStereo_Should_RestoreTheRightChannelFromTheWide33BitSideValue()
        {
            // ffmpeg's own FLAC encoder caps at 24-bit (confirmed empirically while authoring this
            // decoder), so this 33-bit-wide side-channel path (FlacFrameDecoder.DecodeSubframeBody64)
            // is unreachable by any real-encoder fixture -- this is the only coverage it has.
            var bytes = FlacFileBuilder.BuildWideSideChannelFrame(sampleRate: 44100, blockSize: 16, midSide: false, leftOrMidValue: 1_000_000_000, sideValue: 123_456);

            var (info, samples) = DecodeTemp(bytes);

            info.Channels.Should().Be(2);
            info.BitsPerSample.Should().Be(32);
            var expectedRight = (int)(1_000_000_000L - 123_456L);
            var expected = new List<int>();
            for (var i = 0; i < 16; i++)
            {
                expected.Add(1_000_000_000);
                expected.Add(expectedRight);
            }

            samples.Should().Equal(expected);
        }

        [Fact]
        public void Decode_32BitMidSideStereo_Should_RestoreBothChannelsFromTheWide33BitSideValue()
        {
            var bytes = FlacFileBuilder.BuildWideSideChannelFrame(sampleRate: 44100, blockSize: 16, midSide: true, leftOrMidValue: 500_000, sideValue: 10_000);

            var (info, samples) = DecodeTemp(bytes);

            info.Channels.Should().Be(2);
            var mid = (500_000L << 1) | (10_000L & 1);
            var expectedLeft = (int)((mid + 10_000L) >> 1);
            var expectedRight = (int)((mid - 10_000L) >> 1);
            var expected = new List<int>();
            for (var i = 0; i < 16; i++)
            {
                expected.Add(expectedLeft);
                expected.Add(expectedRight);
            }

            samples.Should().Equal(expected);
        }

        [Fact]
        public void Decode_MonoLpcSubframeWithNarrowAccumulator_Should_RestoreTheArithmeticSequence()
        {
            // FlacFrameDecoder.NeedsWideLpcAccumulator picks a 64-bit accumulator whenever
            // bitsPerSample + precision + order-bits could overflow a 32-bit running sum; every real
            // ffmpeg-produced fixture this file's own FlacFfmpegCrossCheckTest fixtures use (16/24-bit
            // audio) turned out, when checked, to always land on that wide-accumulator path, leaving
            // the plain 32-bit accumulator (RestoreLpcPrediction) with no coverage at all -- so this
            // hand-built, deliberately small (8-bit, order 1, precision 2) LPC subframe exists solely
            // to exercise that narrower path. Coefficient 1 with shift 0 and a constant residual of 2
            // makes the predicted sequence a simple, hand-verifiable arithmetic progression.
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(
                channels: 1,
                bitsPerSample: 8,
                sampleRate: 44100,
                blockSize: 16,
                channelAssignment: 0,
                writeSubframes: writer =>
                {
                    writer.WriteBits(0x40, 8); // subframe header: LPC order 1 (type 32), no wasted bits
                    writer.WriteBits(ToUnsigned(3, 8), 8); // warmup sample
                    writer.WriteBits(1, 4); // precision code 1 -> precision = 2 bits
                    writer.WriteBits(0, 5); // shift = 0
                    writer.WriteBits(ToUnsigned(1, 2), 2); // coefficient = 1
                    writer.WriteBits(0, 2); // residual coding method 0 (4-bit Rice parameters)
                    writer.WriteBits(0, 4); // partition order 0 (a single partition)
                    writer.WriteBits(15, 4); // the method-0 escape code (raw, unencoded residuals)
                    writer.WriteBits(4, 5); // raw residual width = 4 bits
                    for (var i = 0; i < 15; i++)
                    {
                        writer.WriteBits(ToUnsigned(2, 4), 4); // residual = 2 for every remaining sample
                    }
                });

            var (info, samples) = DecodeTemp(bytes);

            info.BitsPerSample.Should().Be(8);
            samples.Should().Equal(Enumerable.Range(0, 16).Select(i => 3 + (2 * i)));
        }

        [Fact]
        public void Decode_WithId3v2TagBeforeTheStreamMarker_Should_SkipItAndDecodeNormally()
        {
            var flac = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7]);
            var tagged = FlacFileBuilder.PrependId3v2Tag(flac, taggedPayloadLength: 20);

            var (info, samples) = DecodeTemp(tagged);

            info.Channels.Should().Be(1);
            samples.Should().Equal(Enumerable.Repeat(7, 16));
        }

        [Fact]
        public void Decode_WithTrailingDataAfterAllPromisedSamplesArrived_Should_IgnoreItAndNotThrow()
        {
            var flac = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7]);
            var withTrailingJunk = flac.Concat(new byte[] { 0x54, 0x41, 0x47 }).ToArray(); // "TAG" (an ID3v1-style marker, never actually parsed)

            var (info, samples) = DecodeTemp(withTrailingJunk);

            info.TotalSamples.Should().Be(16);
            samples.Should().Equal(Enumerable.Repeat(7, 16));
        }

        [Fact]
        public void Decode_WithStreamInfoBlockSizeBelowSixteen_Should_Succeed_ForALastFrameOnlyStream()
        {
            // RFC 9639 section 4.1 exempts a stream's last block from the usual 16-sample minimum
            // ("to be able to match the length of the encoded audio without using padding") -- when
            // the whole stream is just one (therefore also last) frame, STREAMINFO's own block size
            // range legitimately reports that frame's true, possibly-tiny size.
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 3, constantValuePerChannel: [7]);

            var (info, samples) = DecodeTemp(bytes);

            info.TotalSamples.Should().Be(3);
            samples.Should().Equal(7, 7, 7);
        }

        // ---------------------------------------------------------------- STREAMINFO / metadata validation

        [Fact]
        public void Decode_WithoutFlacMarker_Should_Throw()
        {
            var bytes = new byte[] { (byte)'n', (byte)'o', (byte)'p', (byte)'e', 0, 0, 0, 0 };

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*fLaC*");
        }

        [Fact]
        public void Decode_WhenFirstMetadataBlockIsNotStreamInfo_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildWithMetadataHeader(blockType: 4, blockLength: 0); // VORBIS_COMMENT first, not STREAMINFO

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*STREAMINFO*");
        }

        [Theory]
        [InlineData(0)] // a second STREAMINFO
        [InlineData(127)] // the reserved "invalid" type
        public void Decode_WithDisallowedSecondMetadataBlockType_Should_Throw(int secondBlockType)
        {
            var streamInfo = FlacFileBuilder.BuildStreamInfoOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, minBlockSize: 16, maxBlockSize: 16, isLast: false);
            var secondHeader = FlacFileBuilder.BuildMetadataBlockHeaderOnly(secondBlockType, blockLength: 0, isLast: true);
            var bytes = streamInfo.Concat(secondHeader).ToArray();

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*disallowed metadata block type*");
        }

        [Fact]
        public void Decode_WithStreamInfoSampleRateZero_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildStreamInfoOnly(channels: 1, bitsPerSample: 16, sampleRate: 0, minBlockSize: 16, maxBlockSize: 16);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*sample rate*");
        }

        [Theory]
        [InlineData(0, 0)] // a meaningless zero block size (a short-but-nonzero last-frame-only stream is legitimate per RFC 9639 section 4.1, so this decoder no longer rejects maxBlockSize<16 on its own)
        [InlineData(32, 16)] // minimum > maximum
        public void Decode_WithInvalidStreamInfoBlockSizeRange_Should_Throw(int minBlockSize, int maxBlockSize)
        {
            var bytes = FlacFileBuilder.BuildStreamInfoOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, minBlockSize, maxBlockSize);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*block size range*");
        }

        [Fact]
        public void Decode_WhenDecodedPcmMd5DoesNotMatchStreamInfo_Should_Throw()
        {
            var wrongMd5 = new byte[16];
            wrongMd5[0] = 0xAB; // any non-zero byte makes this a real (and, here, deliberately wrong) MD5
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7], md5Signature: wrongMd5);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*MD5*");
        }

        [Fact]
        public void Decode_WhenFewerSamplesArriveThanStreamInfoDeclares_Should_Throw()
        {
            // STREAMINFO promises 32 samples; the file provides exactly one 16-sample frame and then
            // simply ends -- a clean stop, not a truncation mid-frame, so this must be caught by the
            // explicit sample-count check rather than bubbling up as an EndOfStreamException.
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7], declaredTotalSamples: 32);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*32*");
        }

        [Fact]
        public void Decode_TruncatedMidFrame_Should_ThrowInvalidDataException_NotALowerLevelException()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7]);
            var truncated = bytes[..^2]; // cut off the last two bytes, mid-subframe

            var act = () => DecodeTemp(truncated);

            act.Should().Throw<InvalidDataException>().WithMessage("*truncated*");
        }

        // ---------------------------------------------------------------- frame header validation

        [Fact]
        public void Decode_WithReservedBitSetAfterFrameSyncCode_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xFA, thirdByte: 0x70, fourthByte: 0x00);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*reserved bit*");
        }

        [Fact]
        public void Decode_WithReservedBlockSizeCodeZero_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xF8, thirdByte: 0x00, fourthByte: 0x00);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*block size code*");
        }

        [Fact]
        public void Decode_WithInvalidSampleRateCodeFifteen_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xF8, thirdByte: 0x7F, fourthByte: 0x00);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*sample rate code*");
        }

        [Theory]
        [InlineData(11)]
        [InlineData(15)]
        public void Decode_WithReservedChannelAssignmentCode_Should_Throw(int channelAssignment)
        {
            var fourthByte = (byte)(channelAssignment << 4);
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xF8, thirdByte: 0x70, fourthByte: fourthByte);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*channel assignment*");
        }

        [Fact]
        public void Decode_WithReservedBitsPerSampleCodeThree_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xF8, thirdByte: 0x70, fourthByte: 0x06);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*bits-per-sample code*");
        }

        [Fact]
        public void Decode_WithReservedBitSetAfterBitsPerSampleCode_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameHeaderOnly(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, secondByte: 0xF8, thirdByte: 0x70, fourthByte: 0x01);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*reserved bit*");
        }

        [Fact]
        public void Decode_WithInvalidFrameNumberFirstByte_Should_Throw()
        {
            // 0xFE's "6 extra bytes" meaning is only valid for a variable-block-size stream; this
            // frame declares fixed block size (the usual case), making 0xFE invalid here.
            var bytes = FlacFileBuilder.BuildFrameWithRawFrameNumberBytes([0xFE]);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*frame/sample number*");
        }

        [Fact]
        public void Decode_WithInvalidFrameNumberContinuationByte_Should_Throw()
        {
            // 0xC0's top 3 bits (110) promise exactly 1 continuation byte, whose own top 2 bits must
            // be "10"; 0x00 violates that.
            var bytes = FlacFileBuilder.BuildFrameWithRawFrameNumberBytes([0xC0, 0x00]);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*frame/sample number*");
        }

        [Fact]
        public void Decode_WithFrameChannelCountNotMatchingStreamInfo_Should_Throw()
        {
            // STREAMINFO declares 2 channels; channelAssignment 0 (mono, independent) implies 1.
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 2, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: _ => { });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*channel(s)*");
        }

        [Fact]
        public void Decode_WithFrameBitsPerSampleNotMatchingStreamInfo_Should_Throw()
        {
            // STREAMINFO declares 24-bit; rawBitsPerSampleCode 4 means "16-bit" (the frame header's
            // own table), a mismatch.
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 24, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: _ => { }, rawBitsPerSampleCode: 4);

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*bits per sample*");
        }

        [Fact]
        public void Decode_WithFrameCrc8Mismatch_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7]);
            CorruptByte(bytes, FindFrameCrc8ByteIndex(bytes));

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*CRC-8*");
        }

        [Fact]
        public void Decode_WithFrameCrc16Mismatch_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, constantValuePerChannel: [7]);
            CorruptByte(bytes, bytes.Length - 1); // the last byte is always part of the CRC-16

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*CRC-16*");
        }

        // ---------------------------------------------------------------- subframe / residual validation

        [Fact]
        public void Decode_WithFirstBitOfSubframeHeaderSet_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w => w.WriteBits(0x80, 8));

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*first bit*");
        }

        [Fact]
        public void Decode_WithWastedBitsNotLessThanSubframeBitsPerSample_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x01, 8); // CONSTANT, wasted-bits flag set
                w.WriteUnary(15); // unary 15 -> wastedBits = 16, not less than the 16-bit subframe depth
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*wasted bits*");
        }

        [Fact]
        public void Decode_WithReservedSubframeType_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w => w.WriteBits(0x04, 8)); // type 2: reserved

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*reserved*");
        }

        [Fact]
        public void Decode_WithLpcPredictorOrderLargerThanBlockSize_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w => w.WriteBits(0x7E, 8)); // LPC order 32 > block size 16

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*predictor order*");
        }

        [Fact]
        public void Decode_WithLpcCoefficientPrecisionFifteen_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x40, 8); // LPC order 1
                w.WriteBits(0, 16); // 1 warm-up sample
                w.WriteBits(15, 4); // precision code 15: reserved
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*precision*");
        }

        [Fact]
        public void Decode_WithNegativeLpcShift_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x40, 8); // LPC order 1
                w.WriteBits(0, 16); // 1 warm-up sample
                w.WriteBits(0, 4); // precision code 0 -> precision 1
                w.WriteBits(0b10000, 5); // signed 5-bit shift with the top bit set: -16
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*shift*");
        }

        [Theory]
        [InlineData(2u)]
        [InlineData(3u)]
        public void Decode_WithReservedResidualCodingMethod_Should_Throw(uint method)
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x10, 8); // FIXED order 0 -- no warm-up samples needed
                w.WriteBits(method, 2);
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*residual coding method*");
        }

        [Fact]
        public void Decode_WithRicePartitionSizeNotEvenlyDividingBlockSize_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x10, 8); // FIXED order 0
                w.WriteBits(0, 2); // residual method 0 (4-bit Rice parameters)
                w.WriteBits(5, 4); // partition order 5 -> 32 partitions of size 16>>5 = 0, which can't reconstruct 16
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*Rice partition order*");
        }

        [Fact]
        public void Decode_WithRicePartitionSizeNotLargerThanPredictorOrder_Should_Throw()
        {
            var bytes = FlacFileBuilder.BuildFrameWithCustomSubframes(channels: 1, bitsPerSample: 16, sampleRate: 44100, blockSize: 16, channelAssignment: 0, writeSubframes: w =>
            {
                w.WriteBits(0x18, 8); // FIXED order 4
                for (var i = 0; i < 4; i++)
                {
                    w.WriteBits(0, 16); // 4 warm-up samples
                }

                w.WriteBits(0, 2); // residual method 0
                w.WriteBits(2, 4); // partition order 2 -> 4 partitions of size 16>>2 = 4, not larger than order 4
            });

            var act = () => DecodeTemp(bytes);

            act.Should().Throw<InvalidDataException>().WithMessage("*Rice partition order*");
        }

        // ---------------------------------------------------------------- helpers

        private static (FlacStreamInfo Info, int[] Samples) DecodeTemp(byte[] bytes)
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, bytes);
                return FlacTestDecoder.DecodeAll(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void CorruptByte(byte[] bytes, int index) => bytes[index] ^= 0xFF;

        private static uint ToUnsigned(int value, int bitCount) => (uint)value & ((1u << bitCount) - 1);

        // The CRC-8 byte is always the 9th byte of the frame: 2 (sync) + 2 (block/sample-rate,
        // channel/bps) + 1 (frame number, 0x00 for every fixture this file builds) + up to 2
        // (uncommon block size, always present here since BuildConstantFrame always uses
        // blockSizeCode 7 for any non-table block size) = 6 header bytes before it, plus the
        // STREAMINFO block (4 + 4 + 34 = 42 bytes) and the 'fLaC' marker are already in `bytes`
        // ahead of the frame -- rather than re-deriving that offset by hand here too, this finds it
        // the same way the decoder does: the CRC-8 is the last byte before the first subframe, which
        // for a 16-bit mono CONSTANT subframe is exactly 3 bytes before the end of an all-CONSTANT,
        // single-channel, 16-bit frame. Kept simple and explicit rather than clever.
        private static int FindFrameCrc8ByteIndex(byte[] constantMono16BitFrameBytes) =>
            constantMono16BitFrameBytes.Length - 2 /* CRC-16 */ - 3 /* 16-bit CONSTANT subframe: 1 header byte + 2 value bytes */ - 1 /* the CRC-8 byte itself */;
    }
}
