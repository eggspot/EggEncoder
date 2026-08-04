namespace EggEncoder.Transform
{
    public sealed class HuffmanTable
    {
        private readonly (uint Code, int Length)[] _entries;
        private readonly int _maxLength;

        public HuffmanTable(uint[] codes, byte[] lengths)
        {
            if (codes.Length != lengths.Length)
            {
                throw new ArgumentException("Codes and lengths must have the same count", nameof(lengths));
            }

            _entries = new (uint Code, int Length)[codes.Length];
            for (var i = 0; i < codes.Length; i++)
            {
                _entries[i] = (codes[i], lengths[i]);
            }

            _maxLength = 0;
            foreach (var entry in _entries)
            {
                _maxLength = Math.Max(_maxLength, entry.Length);
            }

            VerifyKraftEquality();
        }

        public (uint Code, int Length) GetCode(int index) => _entries[index];

        public int Decode(BitReader reader)
        {
            for (var length = 1; length <= _maxLength; length++)
            {
                if (reader.RemainingBits < length)
                {
                    break;
                }

                var candidate = reader.PeekBits(length);
                for (var i = 0; i < _entries.Length; i++)
                {
                    if (_entries[i].Length == length && _entries[i].Code == candidate)
                    {
                        reader.SkipBits(length);
                        return i;
                    }
                }
            }

            throw new InvalidDataException("No matching Huffman codeword found in bitstream");
        }

        private void VerifyKraftEquality()
        {
            var sum = 0.0;
            foreach (var entry in _entries)
            {
                if (entry.Length <= 0)
                {
                    throw new InvalidDataException("Huffman table contains a non-positive code length");
                }

                sum += Math.Pow(2, -entry.Length);
            }

            if (Math.Abs(sum - 1.0) > 1e-9)
            {
                throw new InvalidDataException($"Huffman table fails Kraft equality (sum={sum}); table is incomplete or malformed");
            }
        }
    }
}
