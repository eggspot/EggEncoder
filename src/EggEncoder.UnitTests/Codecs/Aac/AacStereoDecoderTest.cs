using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacStereoDecoderTest
    {
        private const int MaxSampleDifference = 3;

        [Fact]
        public void Decode_StereoAdts_Should_Match_Ffmpeg_Within_Tolerance()
        {
            var samples = new List<int>();
            var info = AacDecoder.Decode(Fixture("stereo_44100.aac"), (block, _, _, _, _) => samples.AddRange(block));

            info.Channels.Should().Be(2);
            info.SampleRate.Should().Be(44100);
            AssertMatches(samples, ReadPcm(Fixture("stereo_44100_groundtruth.pcm")), offset: 0);
        }

        [Theory]
        [InlineData(8000)]
        [InlineData(16000)]
        [InlineData(24000)]
        [InlineData(32000)]
        [InlineData(64000)]
        [InlineData(96000)]
        public void Decode_MonoAtEverySampleRateTable_Should_Match_Ffmpeg_Within_Tolerance(int sampleRate)
        {
            var samples = new List<int>();
            var info = AacDecoder.Decode(Fixture($"mono_{sampleRate}.aac"), (block, _, _, _, _) => samples.AddRange(block));

            info.SampleRate.Should().Be(sampleRate);
            AssertMatches(samples, ReadPcm(Fixture($"mono_{sampleRate}_groundtruth.pcm")), offset: 0);
        }

        [Fact]
        public void Decode_StereoMp4_Should_Return_Two_Channels_Matching_Ffmpeg_After_Priming()
        {
            var samples = new List<int>();
            var info = MovDecoder.Decode(Path.GetFullPath("Codecs/Mov/stereo_48000.mp4"), (block, channels, _, _, _) =>
            {
                channels.Should().Be(2);
                samples.AddRange(block);
            });

            info.Channels.Should().Be(2);
            AssertMatches(samples, ReadPcm(Path.GetFullPath("Codecs/Mov/stereo_48000_groundtruth.pcm")), offset: 1024 * 2);
        }

        [Fact]
        public void Decode_NoiseSubstitution_Should_Keep_The_Energy_Of_Ffmpegs_Decode()
        {
            var samples = new List<int>();
            AacDecoder.Decode(Fixture("pns_48000.aac"), (block, _, _, _, _) => samples.AddRange(block));

            var expected = ReadPcm(Fixture("pns_48000_groundtruth.pcm"));
            var ratio = Rms(samples) / Rms(expected);
            ratio.Should().BeInRange(0.9, 1.1);
        }

        [Fact]
        public async Task NativeEncoderProbe_StereoMp4_Should_Report_Two_Channels()
        {
            var encoder = new NativeEncoder(Microsoft.Extensions.Logging.Abstractions.NullLogger<NativeEncoder>.Instance, enableLogging: false);

            var result = await encoder.Probe(Path.GetFullPath("Codecs/Mov/stereo_48000.mp4"));

            result.Channels.Should().Be(2);
            result.SampleRate.Should().Be(48000);
        }

        [Fact]
        public void DecodeFrame_PulseData_Should_Equal_The_Same_Values_Coded_Directly()
        {
            var withPulse = Decode(Sce(pulse: true, tupleIndex: 40));
            var direct = Decode(Sce(pulse: false, tupleIndex: 37));

            withPulse.Should().Equal(direct);
            direct.Should().Contain(s => s != 0);
        }

        [Fact]
        public void DecodeFrame_Tns_Should_Filter_The_Spectrum_With_The_Decoded_Lattice()
        {
            var plain = Decode(Sce(pulse: false, tupleIndex: 67));
            var filtered = Decode(Sce(pulse: false, tupleIndex: 67, tnsCoefficient: 2));

            var reflection = Math.Sin(2 / ((4 - 0.5) / (Math.PI / 2)));
            var gain = Math.Pow(2, (150 - 100) / 4.0);
            var spectrum = new double[1024];
            spectrum[0] = gain;
            spectrum[1] = -reflection * gain;
            spectrum[2] = reflection * reflection * gain;
            spectrum[3] = -reflection * reflection * reflection * gain;
            var expected = FirstHalfWindowed(spectrum);
            filtered.Zip(expected).Should().OnlyContain(pair => Math.Abs(pair.First - pair.Second) <= 1);
            filtered.Should().NotEqual(plain);
            filtered.Should().Contain(s => s != 0);
        }

        [Fact]
        public void DecodeFrame_IntensityStereoWithMsUsed_Should_Invert_The_Copied_Left_Channel()
        {
            var output = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Cpe(msMaskPresent: 1, msUsed: true, rightBandType: 15)));

            var left = output.Where((_, i) => i % 2 == 0).ToArray();
            var right = output.Where((_, i) => i % 2 == 1).ToArray();
            left.Should().Contain(s => s != 0);
            right.Should().Equal(left.Select(s => -s));
        }

        [Fact]
        public void DecodeFrame_IntensityStereoWithoutMsUsed_Should_Copy_The_Left_Channel()
        {
            var output = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Cpe(msMaskPresent: 1, msUsed: false, rightBandType: 15)));

            output.Where((_, i) => i % 2 == 1).Should().Equal(output.Where((_, i) => i % 2 == 0));
        }

        [Fact]
        public void DecodeFrame_MidSideOnEveryBand_Should_Put_Mid_Plus_Side_Left_And_Mid_Minus_Side_Right()
        {
            var output = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Cpe(msMaskPresent: 2, msUsed: false, rightBandType: 1)));
            var mono = Decode(Sce(pulse: false, tupleIndex: 67));

            output.Where((_, i) => i % 2 == 1).Should().OnlyContain(s => s == 0);
            output.Where((_, i) => i % 2 == 0).Zip(mono).Should().OnlyContain(pair => Math.Abs(pair.First - (2 * pair.Second)) <= 1);
        }

        [Fact]
        public void DecodeFrame_NoiseInBothChannels_Should_Be_Independent_Unless_MsUsed()
        {
            var independent = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Cpe(msMaskPresent: 0, msUsed: false, rightBandType: 13, leftBandType: 13)));
            var shared = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Cpe(msMaskPresent: 2, msUsed: false, rightBandType: 13, leftBandType: 13)));

            independent.Where((_, i) => i % 2 == 1).Should().NotEqual(independent.Where((_, i) => i % 2 == 0));
            independent.Should().Contain(s => s != 0);
            shared.Where((_, i) => i % 2 == 1).Should().Equal(shared.Where((_, i) => i % 2 == 0));
        }

        [Fact]
        public void DecodeFrame_TruncatedAdtsFrame_Should_Throw_InvalidData()
        {
            var bytes = File.ReadAllBytes(Fixture("stereo_44100.aac"))[..200];

            var act = () => AacDecoder.Decode(new MemoryStream(bytes), (_, _, _, _, _) => { });

            act.Should().Throw<InvalidDataException>();
        }

        [Theory]
        [InlineData(44100, 4)]
        [InlineData(44000, 4)]
        [InlineData(100000, 0)]
        [InlineData(7350, 11)]
        [InlineData(10000, 10)]
        public void NearestIndex_Should_Map_Explicit_Rates_To_The_Nearest_Table(int sampleRate, int expected)
        {
            AacBandTables.NearestIndex(sampleRate).Should().Be(expected);
        }

        [Fact]
        public void ToLpc_Should_Step_Up_Reflection_Coefficients()
        {
            AacFrameDecoder.ToLpc([0.5]).Should().Equal(0.5);
            AacFrameDecoder.ToLpc([0.5, 0.25]).Should().Equal(0.5 + (0.25 * 0.5), 0.25);
        }

        [Fact]
        public void DecodeFrame_MonoStreamIntoStereoDecoder_Should_Duplicate_The_Channel()
        {
            var mono = Decode(Sce(pulse: false, tupleIndex: 67));
            var stereo = new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(Sce(pulse: false, tupleIndex: 67)));

            stereo.Where((_, i) => i % 2 == 0).Should().Equal(mono);
            stereo.Where((_, i) => i % 2 == 1).Should().Equal(mono);
        }

        [Fact]
        public void DecodeFrame_SkippedElements_Should_Not_Change_The_Audio()
        {
            var plain = Decode(Sce(pulse: false, tupleIndex: 67));
            var withExtras = Decode(Sce(pulse: false, tupleIndex: 67, prefix: writer =>
            {
                writer.WriteBits(4, 3);
                writer.WriteBits(0, 4);
                writer.WriteBits(0, 1);
                writer.WriteBits(2, 8);
                writer.WriteBits(0xABCD, 16);
                writer.WriteBits(6, 3);
                writer.WriteBits(1, 4);
                writer.WriteBits(0, 8);
                writer.WriteBits(3, 3);
                writer.WriteBits(0, 4);
                WriteSilentIcs(writer);
            }));

            withExtras.Should().Equal(plain);
        }

        [Fact]
        public void DecodeFrame_ProgramConfigElement_Should_Be_Skipped()
        {
            var plain = Decode(Sce(pulse: false, tupleIndex: 67));
            var withPce = Decode(Sce(pulse: false, tupleIndex: 67, prefix: writer =>
            {
                writer.WriteBits(5, 3);
                writer.WriteBits(0, 4);
                writer.WriteBits(1, 2);
                writer.WriteBits(4, 4);
                writer.WriteBits(1, 4);
                writer.WriteBits(0, 4);
                writer.WriteBits(0, 4);
                writer.WriteBits(0, 2);
                writer.WriteBits(0, 3);
                writer.WriteBits(0, 4);
                writer.WriteBits(1, 1);
                writer.WriteBits(0, 4);
                writer.WriteBits(0, 1);
                writer.WriteBits(1, 1);
                writer.WriteBits(1, 3);
                writer.WriteBits(0, 1);
                writer.WriteBits(0, 4);
                writer.ByteAlign();
                writer.WriteBits(1, 8);
                writer.WriteBits((uint)'x', 8);
            }));

            withPce.Should().Equal(plain);
        }

        [Fact]
        public void DecodeFrame_SbrFillElement_Should_Be_Refused()
        {
            var stream = Sce(pulse: false, tupleIndex: 40, prefix: writer =>
            {
                writer.WriteBits(6, 3);
                writer.WriteBits(1, 4);
                writer.WriteBits(13, 4);
                writer.WriteBits(0, 4);
            });

            var act = () => Decode(stream);

            act.Should().Throw<NotSupportedException>().WithMessage("*SBR*");
        }

        [Theory]
        [InlineData(2u)]
        public void DecodeFrame_CouplingChannel_Should_Be_Refused(uint elementId)
        {
            var writer = new BitWriter();
            writer.WriteBits(elementId, 3);
            writer.WriteBits(0, 32);

            var act = () => new AacFrameDecoder(44100, 1).DecodeFrame(new BitReader(writer.ToArray()));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void DecodeFrame_TwoChannelElementsIntoMono_Should_Be_Refused()
        {
            var stream = Sce(pulse: false, tupleIndex: 40, prefix: writer =>
            {
                writer.WriteBits(0, 3);
                writer.WriteBits(0, 4);
                WriteSilentIcs(writer);
            });

            var act = () => Decode(stream);

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void DecodeFrame_EmptyBlock_Should_Throw_InvalidData()
        {
            var writer = new BitWriter();
            writer.WriteBits(7, 3);
            writer.WriteBits(0, 32);

            var act = () => new AacFrameDecoder(44100, 1).DecodeFrame(new BitReader(writer.ToArray()));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void DecodeFrame_ReservedMidSideMode_Should_Throw_InvalidData()
        {
            var writer = new BitWriter();
            writer.WriteBits(1, 3);
            writer.WriteBits(0, 4);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 6);
            writer.WriteBits(0, 1);
            writer.WriteBits(3, 2);
            writer.WriteBits(0, 32);

            var act = () => new AacFrameDecoder(44100, 2).DecodeFrame(new BitReader(writer.ToArray()));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void DecodeFrame_PulseWithShortWindows_Should_Throw_InvalidData()
        {
            var writer = new BitWriter();
            writer.WriteBits(0, 3);
            writer.WriteBits(0, 4);
            writer.WriteBits(100, 8);
            writer.WriteBits(0, 1);
            writer.WriteBits(2, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 4);
            writer.WriteBits(0x7F, 7);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 32);

            var act = () => new AacFrameDecoder(44100, 1).DecodeFrame(new BitReader(writer.ToArray()));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Constructor_Should_Refuse_More_Than_Two_Channels_And_Unknown_Rates()
        {
            ((Action)(() => new AacFrameDecoder(44100, 6))).Should().Throw<NotSupportedException>();
            ((Action)(() => new AacFrameDecoder(0, 2))).Should().Throw<NotSupportedException>();
        }

        private static int[] Decode(byte[] stream) => new AacFrameDecoder(44100, 1).DecodeFrame(new BitReader(stream));

        private static byte[] Sce(bool pulse, int tupleIndex, int? tnsCoefficient = null, Action<BitWriter>? prefix = null)
        {
            var writer = new BitWriter();
            prefix?.Invoke(writer);
            writer.WriteBits(0, 3);
            writer.WriteBits(0, 4);
            writer.WriteBits(150, 8);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 6);
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 4);
            writer.WriteBits(1, 5);
            var (scaleCode, scaleLength) = AacTables.ScalefactorHuffman.GetCode(AacTables.ScaleDiffZero);
            writer.WriteBits(scaleCode, scaleLength);
            writer.WriteBits(pulse ? 1u : 0u, 1);
            if (pulse)
            {
                writer.WriteBits(0, 2);
                writer.WriteBits(0, 6);
                writer.WriteBits(2, 5);
                writer.WriteBits(1, 4);
            }

            writer.WriteBits(tnsCoefficient is null ? 0u : 1u, 1);
            if (tnsCoefficient is { } coefficient)
            {
                writer.WriteBits(1, 2);
                writer.WriteBits(0, 1);
                writer.WriteBits(49, 6);
                writer.WriteBits(1, 5);
                writer.WriteBits(0, 1);
                writer.WriteBits(0, 1);
                writer.WriteBits((uint)coefficient, 3);
            }

            writer.WriteBits(0, 1);
            var (code, length) = AacTables.SpectralCodebooks[0].Huffman.GetCode(tupleIndex);
            writer.WriteBits(code, length);
            writer.WriteBits(7, 3);
            writer.WriteBits(0, 32);
            return writer.ToArray();
        }

        private static byte[] Cpe(int msMaskPresent, bool msUsed, int rightBandType, int leftBandType = 1)
        {
            var writer = new BitWriter();
            writer.WriteBits(1, 3);
            writer.WriteBits(0, 4);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 6);
            writer.WriteBits(0, 1);
            writer.WriteBits((uint)msMaskPresent, 2);
            if (msMaskPresent == 1)
            {
                writer.WriteBits(msUsed ? 1u : 0u, 1);
            }

            WriteSingleBandIcs(writer, leftBandType);
            WriteSingleBandIcs(writer, rightBandType);
            writer.WriteBits(7, 3);
            writer.WriteBits(0, 32);
            return writer.ToArray();
        }

        private static void WriteSingleBandIcs(BitWriter writer, int bandType)
        {
            writer.WriteBits(150, 8);
            writer.WriteBits((uint)bandType, 4);
            writer.WriteBits(1, 5);
            if (bandType == 13)
            {
                writer.WriteBits(246, 9);
            }
            else
            {
                var (scaleCode, scaleLength) = AacTables.ScalefactorHuffman.GetCode(AacTables.ScaleDiffZero);
                writer.WriteBits(scaleCode, scaleLength);
            }

            writer.WriteBits(0, 3);
            if (bandType == 1)
            {
                var (code, length) = AacTables.SpectralCodebooks[0].Huffman.GetCode(67);
                writer.WriteBits(code, length);
            }
        }

        private static void WriteSilentIcs(BitWriter writer)
        {
            writer.WriteBits(100, 8);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 6);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
        }

        private static int[] FirstHalfWindowed(double[] spectrum)
        {
            var time = Mdct.Inverse(spectrum);
            var output = new int[1024];
            for (var n = 0; n < 1024; n++)
            {
                var window = Math.Sin(Math.PI / 2048 * (n + 0.5));
                output[n] = (int)Math.Round(Math.Clamp(time[n] * window, short.MinValue, short.MaxValue));
            }

            return output;
        }

        private static void AssertMatches(List<int> decoded, short[] expected, int offset)
        {
            decoded.Count.Should().BeGreaterThanOrEqualTo(offset + expected.Length);
            var worst = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(decoded[offset + i] - expected[i]));
            }

            worst.Should().BeLessThanOrEqualTo(MaxSampleDifference);
            expected.Should().Contain(s => Math.Abs(s) > 1000);
        }

        private static double Rms(IReadOnlyList<int> samples) => Math.Sqrt(samples.Average(s => (double)s * s));

        private static double Rms(short[] samples) => Math.Sqrt(samples.Average(s => (double)s * s));

        private static short[] ReadPcm(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples;
        }

        private static string Fixture(string name) => Path.GetFullPath(Path.Combine("Codecs/Aac", name));
    }
}
