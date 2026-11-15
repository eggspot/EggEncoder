using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mpeg2
{
    internal sealed class Mpeg2Vlc
    {
        private readonly int[] _values;
        private readonly byte[] _lengths;

        private Mpeg2Vlc(int bits, int[] values, byte[] lengths)
        {
            Bits = bits;
            _values = values;
            _lengths = lengths;
        }

        public int Bits { get; }

        public static Mpeg2Vlc Build(IReadOnlyList<(string Code, int Value)> codes)
        {
            var bits = codes.Max(c => c.Code.Replace(" ", "").Length);
            var values = new int[1 << bits];
            var lengths = new byte[1 << bits];
            foreach (var (code, value) in codes)
            {
                var clean = code.Replace(" ", "");
                var prefix = Convert.ToInt32(clean, 2) << (bits - clean.Length);
                var count = 1 << (bits - clean.Length);
                for (var i = 0; i < count; i++)
                {
                    if (lengths[prefix + i] != 0)
                    {
                        throw new InvalidOperationException($"MPEG-2 VLC table: code {code} overlaps another code");
                    }

                    values[prefix + i] = value;
                    lengths[prefix + i] = (byte)clean.Length;
                }
            }

            return new Mpeg2Vlc(bits, values, lengths);
        }

        public int Read(BitReader reader)
        {
            var available = Math.Min(Bits, reader.RemainingBits);
            var window = (int)reader.PeekBits(available) << (Bits - available);
            var length = _lengths[window];
            if (length == 0 || length > available)
            {
                throw new InvalidDataException("MPEG-2: an invalid variable-length code");
            }

            reader.SkipBits(length);
            return _values[window];
        }
    }
}
