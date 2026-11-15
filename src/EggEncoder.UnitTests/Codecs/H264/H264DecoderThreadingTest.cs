using EggEncoder.Codecs.H264;
using EggEncoder.Video;
using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264DecoderThreadingTest
    {
        public static TheoryData<string> Fixtures()
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Codecs", "H264"), "*.h264").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_On_Several_Threads_Should_Match_The_Calling_Thread_Frame_For_Frame(string fixture)
        {
            var bytes = Fixture(fixture);
            var expected = DecodeWithTimestamps(bytes, 1);

            expected.Should().NotBeEmpty();
            foreach (var threads in new[] { 2, 3, 8 })
            {
                DecodeWithTimestamps(bytes, threads).Should().Equal(expected, $"{threads} threads must give the same frames");
            }
        }

        [Fact]
        public void Damaged_Streams_On_Several_Threads_Should_Only_Throw_InvalidData_Or_NotSupported()
        {
            var original = Fixture("high_b.h264");
            var random = new Random(7);
            for (var round = 0; round < 120; round++)
            {
                var bytes = (byte[])original.Clone();
                for (var flips = 0; flips < 4; flips++)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }

                using var decoder = new H264Decoder(null, 4);
                foreach (var unit in AccessUnits(bytes))
                {
                    Tolerate(() => decoder.Decode(unit, TimeSpan.Zero, f => f.Dispose()));
                }

                Tolerate(() => decoder.Flush(f => f.Dispose()));
            }
        }

        [Fact]
        public void A_Failed_Picture_Should_Throw_And_Decoding_Should_Resume_At_The_Next_Idr()
        {
            var damaged = AccessUnits(Fixture("p_basic.h264"));
            var clean = Fixture("high_b.h264");
            var expected = DecodeToMd5(clean, 1);
            using var decoder = new H264Decoder(null, 4);

            var act = () =>
            {
                for (var i = 0; i < damaged.Count; i++)
                {
                    var unit = i == 2 ? damaged[i][..(damaged[i].Length * 3 / 5)] : damaged[i];
                    decoder.Decode(unit, TimeSpan.Zero, f => f.Dispose());
                }

                decoder.Flush(f => f.Dispose());
            };

            act.Should().Throw<InvalidDataException>();
            var hashes = new List<string>();
            void CollectHash(VideoFrame frame)
            {
                hashes.Add(Md5(frame));
                frame.Dispose();
            }

            foreach (var unit in AccessUnits(clean))
            {
                decoder.Decode(unit, TimeSpan.Zero, CollectHash);
            }

            decoder.Flush(CollectHash);
            hashes.Should().Equal(expected);
        }

        [Fact]
        public void Dispose_While_Pictures_Are_Decoding_Should_Not_Throw()
        {
            var units = AccessUnits(Fixture("high_b.h264"));
            for (var stop = 1; stop <= units.Count; stop++)
            {
                var decoder = new H264Decoder(null, 8);
                foreach (var unit in units.Take(stop))
                {
                    decoder.Decode(unit, TimeSpan.Zero, f => f.Dispose());
                }

                decoder.Invoking(d => d.Dispose()).Should().NotThrow();
            }
        }

        [Fact]
        public void Flush_On_Several_Threads_Should_Output_Every_Frame()
        {
            var units = AccessUnits(Fixture("p_basic.h264"));
            using var decoder = new H264Decoder(null, 4);
            var count = 0;
            foreach (var unit in units)
            {
                decoder.Decode(unit, TimeSpan.Zero, f =>
                {
                    count++;
                    f.Dispose();
                });
            }

            decoder.Flush(f =>
            {
                count++;
                f.Dispose();
            });

            count.Should().Be(DecodeToMd5(Fixture("p_basic.h264"), 1).Count);
        }

        private static List<string> DecodeWithTimestamps(byte[] annexB, int threads)
        {
            using var decoder = new H264Decoder(null, threads);
            var frames = new List<string>();
            var index = 0;
            foreach (var unit in AccessUnits(annexB))
            {
                decoder.Decode(unit, TimeSpan.FromMilliseconds(40 * index++), f => Collect(frames, f));
            }

            decoder.Flush(f => Collect(frames, f));
            return frames;
        }

        private static void Collect(List<string> frames, VideoFrame frame)
        {
            frames.Add($"{frame.Timestamp.TotalMilliseconds}:{Md5(frame)}");
            frame.Dispose();
        }

        private static void Tolerate(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }
}
