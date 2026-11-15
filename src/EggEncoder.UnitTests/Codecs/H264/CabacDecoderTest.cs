using EggEncoder.Codecs.H264;
using EggEncoder.Transform;
using EggEncoder.Video;
using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class CabacDecoderTest
    {
        [Fact]
        public void Decoder_Should_Read_Back_Every_Bin_The_Encoding_Process_Of_The_Standard_Wrote()
        {
            var random = new Random(7);
            var operations = new List<(int Kind, int Context, int Bin)>();
            for (var i = 0; i < 20000; i++)
            {
                var kind = random.Next(10) switch
                {
                    < 7 => 0,
                    < 9 => 1,
                    _ => 2
                };
                var context = random.Next(CabacTables.ContextCount - 1);
                if (context == 276)
                {
                    context = 0;
                }

                var bin = kind == 2 ? 0 : random.Next(100) < (context % 5 == 0 ? 95 : 40) ? 1 : 0;
                operations.Add((kind, context, bin));
            }

            var writer = new BitWriter();
            var encoder = new CabacEncoder();
            encoder.InitializeContexts(SliceType.P, 1, 30);
            encoder.Start(writer);
            foreach (var (kind, context, bin) in operations)
            {
                switch (kind)
                {
                    case 0:
                        encoder.EncodeDecision(context, bin);
                        break;
                    case 1:
                        encoder.EncodeBypass(bin);
                        break;
                    default:
                        encoder.EncodeTerminate(0);
                        break;
                }
            }

            encoder.EncodeTerminate(1);
            writer.ByteAlign();
            var data = writer.ToArray();

            var decoder = new CabacDecoder();
            decoder.InitializeContexts(SliceType.P, 1, 30);
            decoder.Start(data, data.Length, 0);
            var decoded = new List<(int, int, int)>();
            foreach (var (kind, context, _) in operations)
            {
                var bin = kind switch
                {
                    0 => decoder.DecodeDecision(context),
                    1 => decoder.DecodeBypass(),
                    _ => decoder.DecodeTerminate()
                };
                decoded.Add((kind, context, bin));
            }

            decoded.Should().Equal(operations);
            decoder.DecodeTerminate().Should().Be(1);
            decoder.BitPosition.Should().Be((data.Length * 8) - System.Numerics.BitOperations.TrailingZeroCount((uint)data[^1]));
        }

        [Fact]
        public void Pcm_Macroblocks_In_A_Cabac_Slice_Should_Restart_The_Engine_After_Their_Samples()
        {
            static byte Sample(int mb, int i) => (byte)((mb * 37) + (i * 11));
            var stream = Sps(2, 2, profile: 77).Concat(Pps(cabac: true)).Concat(CabacPcmPicture(2, 2, Sample)).ToArray();
            var frames = new List<VideoFrame>();
            using (var decoder = new H264Decoder())
            {
                decoder.Decode(stream, TimeSpan.Zero, frames.Add);
                decoder.Flush(frames.Add);
            }

            frames.Should().ContainSingle();
            using var frame = frames[0];
            for (var mb = 0; mb < 4; mb++)
            {
                var x = (mb % 2) * 16;
                var y = (mb / 2) * 16;
                for (var row = 0; row < 16; row++)
                {
                    frame.GetRow(VideoPlane.Y, y + row).Slice(x, 16).ToArray().Should().Equal(Enumerable.Range(row * 16, 16).Select(i => Sample(mb, i)));
                }

                for (var row = 0; row < 8; row++)
                {
                    frame.GetRow(VideoPlane.U, (y / 2) + row).Slice(x / 2, 8).ToArray().Should().Equal(Enumerable.Range(256 + (row * 8), 8).Select(i => Sample(mb, i)));
                    frame.GetRow(VideoPlane.V, (y / 2) + row).Slice(x / 2, 8).ToArray().Should().Equal(Enumerable.Range(320 + (row * 8), 8).Select(i => Sample(mb, i)));
                }
            }
        }

        [Fact]
        public void Damaged_Cabac_Streams_Should_Only_Throw_InvalidData_Or_NotSupported()
        {
            var original = Fixture("cabac_p_multiref.h264");
            var random = new Random(11);
            for (var round = 0; round < 300; round++)
            {
                var bytes = (byte[])original.Clone();
                if (round % 3 == 0)
                {
                    bytes = bytes[..random.Next(bytes.Length)];
                }
                else
                {
                    for (var flip = 0; flip < 1 + random.Next(4); flip++)
                    {
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                    }
                }

                try
                {
                    DecodeToMd5(bytes);
                }
                catch (InvalidDataException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        private static byte[] CabacPcmPicture(int widthInMbs, int heightInMbs, Func<int, int, byte> sample) =>
            Nal(0x65, w =>
            {
                Ue(w, 0);
                Ue(w, 7);
                Ue(w, 0);
                w.WriteBits(0, 4);
                Ue(w, 0);
                w.WriteBits(0, 1);
                w.WriteBits(0, 1);
                Se(w, 0);
                Ue(w, 0);
                Se(w, 0);
                Se(w, 0);
                while (w.BitPosition % 8 != 0)
                {
                    w.WriteBits(1, 1);
                }

                var encoder = new CabacEncoder();
                encoder.InitializeContexts(SliceType.I, 0, 26);
                encoder.Start(w);
                var count = widthInMbs * heightInMbs;
                for (var mb = 0; mb < count; mb++)
                {
                    var neighbours = (mb % widthInMbs > 0 ? 1 : 0) + (mb >= widthInMbs ? 1 : 0);
                    encoder.EncodeDecision(3 + neighbours, 1);
                    encoder.EncodeTerminate(1);
                    w.ByteAlign();
                    for (var i = 0; i < 384; i++)
                    {
                        w.WriteBits(sample(mb, i), 8);
                    }

                    encoder.Start(w);
                    encoder.EncodeTerminate(mb == count - 1 ? 1 : 0);
                }
            }, appendStopBit: false);
    }
}
