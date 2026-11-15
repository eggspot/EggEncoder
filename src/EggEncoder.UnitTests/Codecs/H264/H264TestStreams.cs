using System.Security.Cryptography;
using EggEncoder.Codecs.H264;
using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.UnitTests.Codecs.H264
{
    internal static class H264TestStreams
    {
        public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Codecs", "H264", name));

        public static List<byte[]> AccessUnits(byte[] annexB)
        {
            var units = new List<byte[]>();
            var current = new List<byte>();
            var hasSlice = false;
            foreach (var (start, end) in NalUnits(annexB).Where(n => n.End > n.Start))
            {
                var type = annexB[start] & 0x1F;
                var isSlice = type is 1 or 5;
                var startsPicture = isSlice && end > start + 1 && (annexB[start + 1] & 0x80) != 0;
                if (hasSlice && (startsPicture || type is 6 or 7 or 8 or 9))
                {
                    units.Add([.. current]);
                    current.Clear();
                    hasSlice = false;
                }

                current.AddRange([0, 0, 0, 1]);
                current.AddRange(annexB.AsSpan(start, end - start).ToArray());
                hasSlice |= isSlice;
            }

            if (current.Count > 0)
            {
                units.Add([.. current]);
            }

            return units;
        }

        public static IEnumerable<(int Start, int End)> NalUnits(byte[] data)
        {
            var starts = new List<int>();
            for (var i = 0; i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    starts.Add(i + 3);
                    i += 2;
                }
            }

            for (var k = 0; k < starts.Count; k++)
            {
                var end = k + 1 < starts.Count ? starts[k + 1] - 3 : data.Length;
                while (end > starts[k] && data[end - 1] == 0)
                {
                    end--;
                }

                yield return (starts[k], end);
            }
        }

        public static List<string> DecodeToMd5(byte[] annexB) => DecodeToMd5(annexB, 0);

        public static List<string> DecodeToMd5(byte[] annexB, int maxDegreeOfParallelism)
        {
            using var decoder = new H264Decoder(null, maxDegreeOfParallelism);
            var hashes = new List<string>();
            var index = 0;
            foreach (var unit in AccessUnits(annexB))
            {
                decoder.Decode(unit, TimeSpan.FromMilliseconds(40 * index++), frame =>
                {
                    hashes.Add(Md5(frame));
                    frame.Dispose();
                });
            }

            decoder.Flush(frame =>
            {
                hashes.Add(Md5(frame));
                frame.Dispose();
            });
            return hashes;
        }

        public static string Md5(VideoFrame frame)
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            for (var y = 0; y < frame.Height; y++)
            {
                md5.AppendData(frame.GetRow(VideoPlane.Y, y));
            }

            foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
            {
                for (var y = 0; y < frame.ChromaHeight; y++)
                {
                    md5.AppendData(frame.GetRow(plane, y));
                }
            }

            return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        }

        public static byte[] Nal(int header, Action<BitWriter> body, bool appendStopBit = true)
        {
            var writer = new BitWriter();
            body(writer);
            if (appendStopBit)
            {
                writer.WriteBits(1, 1);
            }

            writer.ByteAlign();
            var rbsp = writer.ToArray();
            var output = new List<byte> { 0, 0, 0, 1, (byte)header };
            var zeros = 0;
            foreach (var b in rbsp)
            {
                if (zeros >= 2 && b <= 3)
                {
                    output.Add(3);
                    zeros = 0;
                }

                output.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }

            return [.. output];
        }

        public static void Ue(BitWriter writer, uint value)
        {
            var coded = value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(coded);
            writer.WriteBits(0, length - 1);
            writer.WriteBits(coded, length);
        }

        public static void Se(BitWriter writer, int value) => Ue(writer, value > 0 ? (uint)((2 * value) - 1) : (uint)(-2 * value));

        public static byte[] Sps(int widthInMbs, int heightInMbs, int profile = 66, int bitDepthLuma = 8, bool frameMbsOnly = true, int chromaFormat = 1) =>
            Nal(0x67, w =>
            {
                w.WriteBits((uint)profile, 8);
                w.WriteBits(0, 8);
                w.WriteBits(30, 8);
                Ue(w, 0);
                if (profile == 100)
                {
                    Ue(w, (uint)chromaFormat);
                    Ue(w, (uint)(bitDepthLuma - 8));
                    Ue(w, 0);
                    w.WriteBits(0, 1);
                    w.WriteBits(0, 1);
                }

                Ue(w, 0);
                Ue(w, 2);
                Ue(w, 1);
                w.WriteBits(0, 1);
                Ue(w, (uint)(widthInMbs - 1));
                Ue(w, (uint)(heightInMbs - 1));
                w.WriteBits(frameMbsOnly ? 1u : 0u, 1);
                if (!frameMbsOnly)
                {
                    w.WriteBits(0, 1);
                }

                w.WriteBits(1, 1);
                w.WriteBits(0, 1);
                w.WriteBits(0, 1);
            });

        public static byte[] Pps(bool cabac = false) =>
            Nal(0x68, w =>
            {
                Ue(w, 0);
                Ue(w, 0);
                w.WriteBits(cabac ? 1u : 0u, 1);
                w.WriteBits(0, 1);
                Ue(w, 0);
                Ue(w, 0);
                Ue(w, 0);
                w.WriteBits(0, 1);
                w.WriteBits(0, 2);
                Se(w, 0);
                Se(w, 0);
                Se(w, 0);
                w.WriteBits(1, 1);
                w.WriteBits(0, 1);
                w.WriteBits(0, 1);
            });

        public static byte[] PcmPicture(int widthInMbs, int heightInMbs, int frame, Func<int, bool> isPcm, Func<int, int, byte> sample) =>
            Nal(0x65, w =>
            {
                Ue(w, 0);
                Ue(w, 7);
                Ue(w, 0);
                w.WriteBits(0, 4);
                Ue(w, (uint)frame);
                w.WriteBits(0, 1);
                w.WriteBits(0, 1);
                Se(w, 0);
                Ue(w, 0);
                Se(w, 0);
                Se(w, 0);
                var pcm = new bool[widthInMbs * heightInMbs];
                for (var mb = 0; mb < pcm.Length; mb++)
                {
                    pcm[mb] = isPcm(mb);
                    if (pcm[mb])
                    {
                        Ue(w, 25);
                        w.ByteAlign();
                        for (var i = 0; i < 384; i++)
                        {
                            w.WriteBits(sample(mb, i), 8);
                        }

                        continue;
                    }

                    Ue(w, 3);
                    Ue(w, 0);
                    Se(w, 0);
                    var mbX = mb % widthInMbs;
                    var mbY = mb / widthInMbs;
                    int? left = mbX > 0 ? (pcm[mb - 1] ? 16 : 0) : null;
                    int? top = mbY > 0 ? (pcm[mb - widthInMbs] ? 16 : 0) : null;
                    var nC = left is { } a && top is { } b ? (a + b + 1) >> 1 : left ?? top ?? 0;
                    var code = nC < 2 ? "1" : nC < 4 ? "11" : nC < 8 ? "1111" : "000011";
                    foreach (var bit in code)
                    {
                        w.WriteBits(bit == '1' ? 1u : 0u, 1);
                    }
                }
            });
    }
}
