using System.Buffers.Binary;

namespace EggEncoder.UnitTests.Containers.Flv
{
    // Builds synthetic FLV byte streams for the branches the ffmpeg fixtures don't reach.
    internal sealed class FlvTestBuilder
    {
        // A real 64x64 x264 avcC and AAC-LC 44.1 kHz mono AudioSpecificConfig, from h264_aac.flv.
        public static readonly byte[] AvcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        public static readonly byte[] AacConfig = Convert.FromHexString("120856e500");

        private readonly MemoryStream _stream = new();
        private uint _lastTagSize;

        public FlvTestBuilder(byte flags = 0x05, uint dataOffset = 9)
        {
            _stream.Write([(byte)'F', (byte)'L', (byte)'V', 1, flags]);
            Span<byte> offset = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(offset, dataOffset);
            _stream.Write(offset);
            for (var i = 9; i < dataOffset; i++)
            {
                _stream.WriteByte(0);
            }

            PreviousTagSize();
        }

        public FlvTestBuilder Tag(byte tagType, uint timestamp, params byte[] body)
        {
            var header = new byte[11];
            header[0] = tagType;
            header[1] = (byte)(body.Length >> 16);
            header[2] = (byte)(body.Length >> 8);
            header[3] = (byte)body.Length;
            header[4] = (byte)(timestamp >> 16);
            header[5] = (byte)(timestamp >> 8);
            header[6] = (byte)timestamp;
            header[7] = (byte)(timestamp >> 24);
            _stream.Write(header);
            _stream.Write(body);
            _lastTagSize = (uint)(11 + body.Length);
            return PreviousTagSize();
        }

        public FlvTestBuilder Video(uint timestamp, params byte[] body) => Tag(9, timestamp, body);

        public FlvTestBuilder Audio(uint timestamp, params byte[] body) => Tag(8, timestamp, body);

        public FlvTestBuilder AvcSequenceHeader(uint timestamp = 0, byte[]? avcC = null) =>
            Video(timestamp, [0x17, 0, 0, 0, 0, .. avcC ?? AvcC]);

        public FlvTestBuilder AvcFrame(uint timestamp, bool keyframe, int compositionTime, params byte[] payload) =>
            Video(timestamp, [(byte)(keyframe ? 0x17 : 0x27), 1, (byte)(compositionTime >> 16), (byte)(compositionTime >> 8), (byte)compositionTime, .. payload]);

        public FlvTestBuilder AacSequenceHeader(uint timestamp = 0, byte[]? config = null) =>
            Audio(timestamp, [0xAF, 0, .. config ?? AacConfig]);

        public FlvTestBuilder AacFrame(uint timestamp, params byte[] payload) => Audio(timestamp, [0xAF, 1, .. payload]);

        public FlvTestBuilder Raw(params byte[] bytes)
        {
            _stream.Write(bytes);
            return this;
        }

        private FlvTestBuilder PreviousTagSize()
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, _lastTagSize);
            _stream.Write(size);
            return this;
        }

        public FlvTestBuilder WithoutTrailingPreviousTagSize()
        {
            _stream.SetLength(_stream.Length - 4);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
