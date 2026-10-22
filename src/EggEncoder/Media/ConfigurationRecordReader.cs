using System.Buffers.Binary;

namespace EggEncoder.Media
{
    // Bounds-checked big-endian reader shared by AvcDecoderConfigurationRecord and
    // HevcDecoderConfigurationRecord: every count and length is checked against the bytes left,
    // never trusted on its own, and any overrun is InvalidDataException.
    internal ref struct ConfigurationRecordReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly string _what;
        private int _position;

        public ConfigurationRecordReader(ReadOnlySpan<byte> data, string what)
        {
            _data = data;
            _what = what;
            _position = 0;
        }

        public readonly bool AtEnd => _position >= _data.Length;

        public int ReadByte()
        {
            Require(1);
            return _data[_position++];
        }

        public int ReadUInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_position..]);
            _position += 2;
            return value;
        }

        public ulong ReadBigEndian(int byteCount)
        {
            Require(byteCount);
            var value = 0UL;
            for (var i = 0; i < byteCount; i++)
            {
                value = (value << 8) | _data[_position++];
            }

            return value;
        }

        // `count` NAL units, each a 16-bit length then that many bytes; copied out.
        public IReadOnlyList<ReadOnlyMemory<byte>> ReadNalUnits(int count)
        {
            // Each NAL unit takes at least 3 bytes (a 2-byte length and a non-empty body), so a
            // count the remaining bytes can't hold is rejected before anything is allocated for it.
            if ((long)count * 3 > _data.Length - _position)
            {
                throw new InvalidDataException($"{_what} is truncated: {count} NAL units can't fit in the {_data.Length - _position} bytes left");
            }

            var nalUnits = new ReadOnlyMemory<byte>[count];
            for (var i = 0; i < count; i++)
            {
                var length = ReadUInt16();
                if (length == 0)
                {
                    throw new InvalidDataException($"{_what}: zero-length NAL unit");
                }

                Require(length);
                nalUnits[i] = _data.Slice(_position, length).ToArray();
                _position += length;
            }

            return nalUnits;
        }

        private readonly void Require(int byteCount)
        {
            if (_data.Length - _position < byteCount)
            {
                throw new InvalidDataException($"{_what} is truncated at offset {_position}");
            }
        }
    }
}
