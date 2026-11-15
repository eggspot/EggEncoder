using System.Buffers.Binary;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;

namespace EggEncoder.Containers.Avi
{
    internal sealed class AviSourceStream
    {
        private const int IdrSliceType = 5;
        private const int SpsType = 7;
        private const int PpsType = 8;

        private readonly uint _scale;
        private readonly uint _sampleSize;
        private readonly int _nalLengthSize;
        private readonly bool _annexB;
        private long _index;
        private long _bytes;

        private AviSourceStream(MediaTrackInfo info, uint scale, uint sampleSize, int nalLengthSize, bool annexB)
        {
            Info = info;
            _scale = scale;
            _sampleSize = sampleSize;
            _nalLengthSize = nalLengthSize;
            _annexB = annexB;
        }

        public MediaTrackInfo Info { get; }

        public static bool IsH264Format(string handler, ReadOnlySpan<byte> format) =>
            format.Length >= 40 && (IsH264(System.Text.Encoding.ASCII.GetString(format.Slice(16, 4))) || IsH264(handler));

        public static bool HasParameterSets(ReadOnlySpan<byte> annexB)
        {
            var units = SplitAnnexB(annexB);
            return units.Any(u => (u.Span[0] & 0x1F) == SpsType) && units.Any(u => (u.Span[0] & 0x1F) == PpsType);
        }

        public static AviSourceStream? TryCreateVideo(int index, string handler, ReadOnlySpan<byte> format, int rate, uint scale, ReadOnlySpan<byte> parameterSetsFromStream = default)
        {
            if (format.Length < 40)
            {
                return null;
            }

            var width = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
            var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(format[8..]));
            var compression = System.Text.Encoding.ASCII.GetString(format.Slice(16, 4));
            if (!IsH264(compression) && !IsH264(handler))
            {
                return null;
            }

            var extradata = format.Length > 40 ? format[40..] : parameterSetsFromStream;
            try
            {
                if (extradata.Length > 0 && extradata[0] == 1)
                {
                    var record = AvcDecoderConfigurationRecord.Parse(extradata);
                    return new AviSourceStream(Video(index, rate, extradata.ToArray(), width, height), scale, 0, record.NalLengthSize, annexB: false);
                }

                var units = SplitAnnexB(extradata);
                var sps = units.Where(u => (u.Span[0] & 0x1F) == SpsType).ToList();
                var pps = units.Where(u => (u.Span[0] & 0x1F) == PpsType).ToList();
                if (sps.Count == 0 || pps.Count == 0 || sps[0].Length < 4)
                {
                    return null;
                }

                var first = sps[0].Span;
                var avcC = new AvcDecoderConfigurationRecord(first[1], first[2], first[3], 4, sps, pps).ToArray();
                return new AviSourceStream(Video(index, rate, avcC, width, height), scale, 0, 4, annexB: true);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        public static AviSourceStream CreateAudio(int index, MediaCodec codec, ReadOnlyMemory<byte> configuration, int rate, uint scale, uint sampleSize, int sampleRate, int channels) =>
            new(new MediaTrackInfo(index, MediaKind.Audio, codec, rate, configuration, 0, Math.Max(0, sampleRate), Math.Max(0, channels)), scale, sampleSize, 0, annexB: false);

        public MediaPacket? TryCreatePacket(byte[] payload)
        {
            long time;
            if (Info.Kind == MediaKind.Video || _sampleSize == 0)
            {
                time = _index * _scale;
                _index++;
            }
            else
            {
                time = _bytes / _sampleSize * _scale;
                _bytes += payload.Length;
            }

            if (payload.Length == 0)
            {
                return null;
            }

            if (Info.Kind == MediaKind.Audio)
            {
                return new MediaPacket(Info.TrackId, time, time, true, 0, payload);
            }

            var lengthPrefixed = _annexB
                ? AnnexBConverter.ToLengthPrefixed(payload)
                : _nalLengthSize is > 0 and < 4 ? Mp4FileSource.WidenNalLengths(payload, _nalLengthSize) : payload;
            return new MediaPacket(Info.TrackId, time, time, HasIdrSlice(lengthPrefixed), 0, lengthPrefixed);
        }

        private static MediaTrackInfo Video(int index, int rate, byte[] avcC, int width, int height) =>
            new(index, MediaKind.Video, MediaCodec.H264, rate, avcC, 0, width: Math.Max(0, width), height: Math.Max(0, height));

        private static bool IsH264(string fourCc) => fourCc.ToUpperInvariant() is "H264" or "X264" or "AVC1" or "DAVC";

        private static List<ReadOnlyMemory<byte>> SplitAnnexB(ReadOnlySpan<byte> annexB)
        {
            var lengthPrefixed = AnnexBConverter.ToLengthPrefixed(annexB);
            var units = new List<ReadOnlyMemory<byte>>();
            var offset = 0;
            while (offset + 4 <= lengthPrefixed.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthPrefixed.AsSpan(offset));
                if (length <= 0 || length > lengthPrefixed.Length - offset - 4)
                {
                    break;
                }

                units.Add(lengthPrefixed.AsMemory(offset + 4, length));
                offset += 4 + length;
            }

            return units;
        }

        private static bool HasIdrSlice(byte[] lengthPrefixed)
        {
            var offset = 0;
            while (offset + 5 <= lengthPrefixed.Length)
            {
                var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(lengthPrefixed.AsSpan(offset)), int.MaxValue);
                if ((lengthPrefixed[offset + 4] & 0x1F) == IdrSliceType)
                {
                    return true;
                }

                if (length > lengthPrefixed.Length - offset - 4)
                {
                    break;
                }

                offset += 4 + length;
            }

            return false;
        }
    }
}
