using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class CafReaderTest
    {
        private static readonly AlacSpecificConfig Config = new()
        {
            FrameLength = 4096,
            BitDepth = 16,
            Pb = 40,
            Mb = 10,
            Kb = 14,
            NumChannels = 1,
            MaxRun = 255,
            SampleRate = 44100
        };

        [Fact]
        public void Open_Missing_CaffHeader_Should_Throw()
        {
            var filePath = WriteBytes("NOTC"u8.ToArray());
            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_TruncatedDescChunk_Should_Throw()
        {
            // The 'desc' chunk header declares a full 32-byte CAFAudioFormat, but the file ends partway
            // through it -- BinaryReader.ReadChars silently returns a short array at EOF rather than
            // throwing, so ParseDescChunk's own length check is what catches this, not an I/O exception.
            var bytes = BuildFileHeader()
                .Concat(BuildChunk("desc", new byte[8])) // only the 8-byte mSampleRate, nothing else
                .ToArray();
            var filePath = WriteBytes(bytes);

            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*malformed*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_NonAlacDescFormat_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("lpcm"))
                .ToArray());

            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_DescChunk_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader().ToArray());
            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*'desc'*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_KukiChunk_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .ToArray());

            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*'kuki'*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_PaktChunk_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildKukiChunk())
                .ToArray());

            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*'pakt'*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_DataChunk_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildKukiChunk())
                .Concat(BuildPaktChunk([]))
                .ToArray());

            try
            {
                var act = () => CafReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*'data'*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_UnknownIntermediateChunk_Should_SkipIt()
        {
            var packet = new byte[] { 1, 2, 3 };
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildChunk("free", new byte[16]))
                .Concat(BuildKukiChunk())
                .Concat(BuildPaktChunk([packet.Length]))
                .Concat(BuildDataChunk(packet, useUnknownSize: false))
                .ToArray());

            try
            {
                using var reader = CafReader.Open(filePath);
                reader.ReadNextPacket().Should().Equal(packet);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_DataChunkSizeMarkedUnknown_Should_StillReadPackets()
        {
            var packet = new byte[] { 10, 20, 30, 40 };
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildKukiChunk())
                .Concat(BuildPaktChunk([packet.Length]))
                .Concat(BuildDataChunk(packet, useUnknownSize: true))
                .ToArray());

            try
            {
                using var reader = CafReader.Open(filePath);
                reader.ReadNextPacket().Should().Equal(packet);
                reader.HasMorePackets.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextPacket_When_NoPacketsRemain_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildKukiChunk())
                .Concat(BuildPaktChunk([]))
                .Concat(BuildDataChunk([], useUnknownSize: false))
                .ToArray());

            try
            {
                using var reader = CafReader.Open(filePath);
                var act = () => reader.ReadNextPacket();
                act.Should().ThrowExactly<InvalidOperationException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextPacket_When_DataChunkIsTruncated_Should_Throw()
        {
            var filePath = WriteBytes(BuildFileHeader()
                .Concat(BuildDescChunk("alac"))
                .Concat(BuildKukiChunk())
                .Concat(BuildPaktChunk([100])) // declares a 100-byte packet
                .Concat(BuildDataChunk(new byte[10], useUnknownSize: false)) // but only 10 bytes follow
                .ToArray());

            try
            {
                using var reader = CafReader.Open(filePath);
                var act = () => reader.ReadNextPacket();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static string WriteBytes(byte[] bytes)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"caf_reader_{Guid.NewGuid():N}.caf");
            File.WriteAllBytes(filePath, bytes);
            return filePath;
        }

        private static byte[] BuildFileHeader()
        {
            var bytes = new byte[8];
            "caff"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 0);
            return bytes;
        }

        private static byte[] BuildChunk(string type, byte[] payload, long? sizeOverride = null)
        {
            var header = new byte[12];
            Encoding.ASCII.GetBytes(type).CopyTo(header, 0);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4), sizeOverride ?? payload.Length);
            return header.Concat(payload).ToArray();
        }

        private static byte[] BuildDescChunk(string formatId)
        {
            var payload = new byte[32];
            BinaryPrimitives.WriteInt64BigEndian(payload, BitConverter.DoubleToInt64Bits(44100.0));
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(payload, 0, 8);
            }

            Encoding.ASCII.GetBytes(formatId).CopyTo(payload, 8);
            return BuildChunk("desc", payload);
        }

        private static byte[] BuildKukiChunk()
        {
            var cookie = new byte[AlacSpecificConfig.EncodedSize];
            Config.WriteTo(cookie);
            return BuildChunk("kuki", cookie);
        }

        private static byte[] BuildPaktChunk(IReadOnlyList<int> packetSizes)
        {
            var sizeBytes = new List<byte>();
            foreach (var size in packetSizes)
            {
                var remaining = size;
                var groups = new List<byte> { (byte)(remaining & 0x7F) };
                remaining >>= 7;
                while (remaining > 0)
                {
                    groups.Add((byte)(0x80 | (remaining & 0x7F)));
                    remaining >>= 7;
                }

                for (var i = groups.Count - 1; i >= 0; i--)
                {
                    sizeBytes.Add(groups[i]);
                }
            }

            var payload = new byte[24 + sizeBytes.Count];
            BinaryPrimitives.WriteInt64BigEndian(payload, packetSizes.Count);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), 0);
            sizeBytes.CopyTo(payload, 24);

            return BuildChunk("pakt", payload);
        }

        private static byte[] BuildDataChunk(byte[] packetBytes, bool useUnknownSize)
        {
            var payload = new byte[4 + packetBytes.Length];
            packetBytes.CopyTo(payload, 4);

            return BuildChunk("data", payload, useUnknownSize ? -1 : null);
        }
    }
}
