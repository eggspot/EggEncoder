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

    // The serializing counterpart of ConfigurationRecordReader.
    internal sealed class ConfigurationRecordWriter
    {
        private readonly MemoryStream _buffer = new();
        private readonly string _what;

        public ConfigurationRecordWriter(string what)
        {
            _what = what;
        }

        public void WriteByte(int value)
        {
            if (value is < 0 or > 0xFF)
            {
                throw new InvalidOperationException($"{_what}: byte field value {value} is out of range");
            }

            _buffer.WriteByte((byte)value);
        }

        public void WriteUInt16(int value)
        {
            if (value is < 0 or > 0xFFFF)
            {
                throw new InvalidOperationException($"{_what}: 16-bit field value {value} is out of range");
            }

            _buffer.WriteByte((byte)(value >> 8));
            _buffer.WriteByte((byte)value);
        }

        public void WriteBigEndian(ulong value, int byteCount)
        {
            for (var i = byteCount - 1; i >= 0; i--)
            {
                _buffer.WriteByte((byte)(value >> (8 * i)));
            }
        }

        // A count (in the low `countBits` bits of one byte, OR'd with `countPrefix`), then each
        // NAL unit as a 16-bit length and its bytes.
        public void WriteNalUnits(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits, int countBits, int countPrefix, int minimumCount)
        {
            ArgumentNullException.ThrowIfNull(nalUnits);
            var maxCount = (1 << countBits) - 1;
            if (nalUnits.Count < minimumCount || nalUnits.Count > maxCount)
            {
                throw new InvalidOperationException($"{_what}: NAL unit count {nalUnits.Count} must be {minimumCount}-{maxCount}");
            }

            WriteByte(countPrefix | nalUnits.Count);
            WriteNalUnitList(nalUnits);
        }

        // Each NAL unit as a 16-bit length and its bytes, no count.
        public void WriteNalUnitList(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits)
        {
            foreach (var nalUnit in nalUnits)
            {
                if (nalUnit.Length is 0 or > 0xFFFF)
                {
                    throw new InvalidOperationException($"{_what}: NAL unit length {nalUnit.Length} must be 1-65535");
                }

                WriteUInt16(nalUnit.Length);
                _buffer.Write(nalUnit.Span);
            }
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}
