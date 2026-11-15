using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;

namespace EggEncoder.Containers.Matroska
{
    internal sealed class MatroskaTrackEntry
    {
        public long Number { get; set; }

        public int Type { get; set; }

        public string CodecId { get; set; } = "";

        public byte[]? CodecPrivate { get; set; }

        public long CodecDelayNanoseconds { get; set; }

        public long DefaultDurationNanoseconds { get; set; }

        public bool Encoded { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int SampleRate { get; set; }

        public int Channels { get; set; }
    }

    internal sealed class MatroskaTrack
    {
        private const long NanosecondsPerSecond = 1_000_000_000;

        private readonly MatroskaTrackEntry _entry;
        private readonly MediaKind _kind;
        private readonly MediaCodec _codec;
        private readonly ReadOnlyMemory<byte> _configuration;
        private readonly int _nalLengthSize;
        private readonly Queue<(long Pts, bool Keyframe, ReadOnlyMemory<byte> Payload)> _pending = new();
        private readonly PriorityQueue<long, long> _pendingTimes = new();
        private long _tickFactor = 1;
        private long _defaultDurationTicks;
        private long _codecDelayTicks;
        private long? _decodeShift;
        private MediaTrackInfo? _info;

        private MatroskaTrack(MatroskaTrackEntry entry, MediaKind kind, MediaCodec codec, ReadOnlyMemory<byte> configuration, int nalLengthSize)
        {
            _entry = entry;
            _kind = kind;
            _codec = codec;
            _configuration = configuration;
            _nalLengthSize = nalLengthSize;
        }

        public int Number => (int)_entry.Number;

        public MediaTrackInfo Info => _info ?? throw new InvalidOperationException("Matroska: the track isn't configured yet");

        public static MatroskaTrack? TryCreate(MatroskaTrackEntry entry)
        {
            if (entry.Number is <= 0 or > int.MaxValue || entry.Encoded)
            {
                return null;
            }

            var codecPrivate = entry.CodecPrivate ?? [];
            try
            {
                return entry.CodecId switch
                {
                    "V_MPEG4/ISO/AVC" when codecPrivate.Length > 0 =>
                        new MatroskaTrack(entry, MediaKind.Video, MediaCodec.H264, codecPrivate, AvcDecoderConfigurationRecord.Parse(codecPrivate).NalLengthSize),
                    "V_MPEGH/ISO/HEVC" when codecPrivate.Length > 0 =>
                        new MatroskaTrack(entry, MediaKind.Video, MediaCodec.H265, codecPrivate, HevcDecoderConfigurationRecord.Parse(codecPrivate).NalLengthSize),
                    "V_AV1" => new MatroskaTrack(entry, MediaKind.Video, MediaCodec.Av1, codecPrivate, 0),
                    "V_VP9" => new MatroskaTrack(entry, MediaKind.Video, MediaCodec.Vp9, ReadOnlyMemory<byte>.Empty, 0),
                    "A_AAC" when codecPrivate.Length > 0 => new MatroskaTrack(entry, MediaKind.Audio, MediaCodec.Aac, codecPrivate, 0),
                    _ when entry.CodecId.StartsWith("A_AAC", StringComparison.Ordinal) && entry.SampleRate > 0 && entry.Channels > 0 =>
                        new MatroskaTrack(entry, MediaKind.Audio, MediaCodec.Aac, Mp4EsdsParser.BuildAudioSpecificConfig(entry.Channels, entry.SampleRate), 0),
                    "A_MPEG/L3" => new MatroskaTrack(entry, MediaKind.Audio, MediaCodec.Mp3, ReadOnlyMemory<byte>.Empty, 0),
                    "A_OPUS" => new MatroskaTrack(entry, MediaKind.Audio, MediaCodec.Opus, codecPrivate, 0),
                    _ => null
                };
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        public void Configure(long timestampScale)
        {
            int timescale;
            if (NanosecondsPerSecond % timestampScale == 0)
            {
                timescale = (int)(NanosecondsPerSecond / timestampScale);
                _tickFactor = 1;
            }
            else
            {
                timescale = (int)NanosecondsPerSecond;
                _tickFactor = timestampScale;
            }

            _defaultDurationTicks = _entry.DefaultDurationNanoseconds * timescale / NanosecondsPerSecond;
            _codecDelayTicks = _kind == MediaKind.Audio
                ? (long)Math.Round(_entry.CodecDelayNanoseconds * (double)timescale / NanosecondsPerSecond, MidpointRounding.AwayFromZero)
                : 0;
            _info = new MediaTrackInfo(
                Number,
                _kind,
                _codec,
                timescale,
                _configuration,
                0,
                _kind == MediaKind.Audio ? _entry.SampleRate : 0,
                _kind == MediaKind.Audio ? _entry.Channels : 0,
                _kind == MediaKind.Video ? _entry.Width : 0,
                _kind == MediaKind.Video ? _entry.Height : 0);
        }

        public void AddFrames(IReadOnlyList<ReadOnlyMemory<byte>> frames, long blockTime, bool keyframe, Queue<MediaPacket> output, int reorderWindow)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var pts = PresentationTime(blockTime, i);
                var payload = _nalLengthSize is > 0 and < 4 ? Mp4FileSource.WidenNalLengths(frames[i].ToArray(), _nalLengthSize) : frames[i];
                if (_kind == MediaKind.Audio)
                {
                    output.Enqueue(new MediaPacket(Number, pts, pts, true, 0, payload));
                    continue;
                }

                _pending.Enqueue((pts, keyframe && i == 0, payload));
                _pendingTimes.Enqueue(pts, pts);
                if (_pending.Count > reorderWindow)
                {
                    EmitOldest(output);
                }
            }
        }

        private long PresentationTime(long blockTime, int index)
        {
            try
            {
                return checked((blockTime * _tickFactor) + (index * _defaultDurationTicks) - _codecDelayTicks);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("Matroska: a block timestamp is out of range");
            }
        }

        public void Flush(Queue<MediaPacket> output)
        {
            while (_pending.Count > 0)
            {
                EmitOldest(output);
            }
        }

        private void EmitOldest(Queue<MediaPacket> output)
        {
            _decodeShift ??= ComputeDecodeShift();
            var (pts, keyframe, payload) = _pending.Dequeue();
            var dts = _pendingTimes.Dequeue() - _decodeShift.Value;
            output.Enqueue(new MediaPacket(Number, pts, dts, keyframe, 0, payload));
        }

        private long ComputeDecodeShift()
        {
            var sorted = _pending.Select(p => p.Pts).Order().ToArray();
            long shift = 0;
            var index = 0;
            foreach (var packet in _pending)
            {
                shift = Math.Max(shift, sorted[index++] - packet.Pts);
            }

            return shift;
        }
    }
}
