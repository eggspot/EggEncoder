using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Opus
{
    // Reads an Ogg bitstream (RFC 3533) and reassembles it into complete logical packets,
    // transparently handling packets that span more than one page (a run of 255-valued lacing
    // values with no terminating <255 value before the page ends). Scoped to a single logical
    // stream -- multiplexed Ogg files carrying more than one serial number are out of scope, which
    // is never the case for a real .opus file (always exactly one audio logical stream).
    internal sealed class OggPageReader : IDisposable
    {
        private const uint CapturePattern = 0x5367674Fu; // "OggS" read as a little-endian uint32

        private readonly Stream _stream;
        private readonly List<byte[]> _pendingFragments = [];

        private byte[] _pageSegmentData = [];
        private int[] _pageLacingValues = [];
        private int[] _pageSegmentOffsets = [];
        private int _segmentIndex;
        private bool _streamEnded;

        public OggPageReader(Stream stream)
        {
            _stream = stream;
        }

        // Returns the next complete packet, or null once the stream has no more packets left.
        public byte[]? ReadNextPacket()
        {
            while (true)
            {
                if (_segmentIndex >= _pageLacingValues.Length)
                {
                    if (_streamEnded || !TryReadNextPage())
                    {
                        if (_pendingFragments.Count > 0)
                        {
                            throw new InvalidDataException("Ogg stream ended mid-packet (truncated final page)");
                        }

                        return null;
                    }

                    continue;
                }

                var runStartOffset = _pageSegmentOffsets[_segmentIndex];
                var runLength = 0;
                var terminated = false;

                while (_segmentIndex < _pageLacingValues.Length)
                {
                    var lacingValue = _pageLacingValues[_segmentIndex];
                    runLength += lacingValue;
                    _segmentIndex++;

                    if (lacingValue < 255)
                    {
                        terminated = true;
                        break;
                    }
                }

                _pendingFragments.Add(_pageSegmentData.AsSpan(runStartOffset, runLength).ToArray());

                if (terminated)
                {
                    var packet = Concat(_pendingFragments);
                    _pendingFragments.Clear();
                    return packet;
                }

                // The run hit the end of this page's segment table without a terminator -- the
                // packet continues into the next page. Loop back around to read it.
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private bool TryReadNextPage()
        {
            var header = new byte[27];
            var bytesRead = _stream.ReadAtLeast(header, 27, throwOnEndOfStream: false);

            if (bytesRead == 0)
            {
                _streamEnded = true;
                return false;
            }

            if (bytesRead != 27)
            {
                throw new InvalidDataException("Ogg stream ended with a truncated page header");
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CapturePattern)
            {
                throw new InvalidDataException("Ogg page is missing its 'OggS' capture pattern");
            }

            var declaredCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4));

            var pageSegmentCount = header[26];
            var lacingBytes = new byte[pageSegmentCount];
            _stream.ReadExactly(lacingBytes);

            var lacingValues = new int[pageSegmentCount];
            var segmentOffsets = new int[pageSegmentCount];
            var totalDataLength = 0;

            for (var i = 0; i < pageSegmentCount; i++)
            {
                lacingValues[i] = lacingBytes[i];
                segmentOffsets[i] = totalDataLength;
                totalDataLength += lacingBytes[i];
            }

            var segmentData = new byte[totalDataLength];
            _stream.ReadExactly(segmentData);

            header.AsSpan(22, 4).Clear();
            var computedCrc = OggCrc32.Compute(Concat([header, lacingBytes, segmentData]));
            if (computedCrc != declaredCrc)
            {
                throw new InvalidDataException("Ogg page fails its own CRC32 check");
            }

            _pageLacingValues = lacingValues;
            _pageSegmentOffsets = segmentOffsets;
            _pageSegmentData = segmentData;
            _segmentIndex = 0;

            return true;
        }

        private static byte[] Concat(List<byte[]> fragments)
        {
            if (fragments.Count == 1)
            {
                return fragments[0];
            }

            var totalLength = 0;
            foreach (var fragment in fragments)
            {
                totalLength += fragment.Length;
            }

            var result = new byte[totalLength];
            var offset = 0;
            foreach (var fragment in fragments)
            {
                fragment.CopyTo(result, offset);
                offset += fragment.Length;
            }

            return result;
        }
    }
}
