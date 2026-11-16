using System.Buffers.Binary;
using EggEncoder.Media;

namespace EggEncoder.Containers.Matroska
{
    /// <summary>The flavour of Matroska file <see cref="MatroskaWriter"/> writes.</summary>
    public enum MatroskaDocType
    {
        /// <summary>A Matroska file (<c>.mkv</c>, <c>.mka</c>): every codec the writer supports.</summary>
        Matroska,

        /// <summary>A WebM file: VP9 or AV1 video and Opus audio only.</summary>
        WebM
    }

    /// <summary>
    /// Writes a packet stream as a Matroska or WebM file (RFC 9559), passed through without decoding: H.264, H.265, AV1
    /// and VP9 video, AAC, MP3 and Opus audio. The file is written forward as packets arrive: a new cluster starts at each
    /// keyframe of the first video track, after five seconds or five megabytes, or when a block time no longer fits the
    /// cluster. When the destination can seek, <see cref="Complete"/> also writes cues for the first video track's
    /// keyframes and patches in the segment size and duration; otherwise the segment has unknown size, as in a live
    /// recording. Timestamps are written in milliseconds. A mid-stream configuration change is refused.
    /// </summary>
    public sealed class MatroskaWriter : IMediaPacketSink, IDisposable
    {
        private const long MillisecondTimescale = 1000;
        private const long MaxClusterMilliseconds = 5000;
        private const int MaxClusterBytes = 5 * 1024 * 1024;
        private const int OpusSampleRate = 48000;

        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly MatroskaDocType _docType;
        private readonly List<(long Time, long ClusterPosition, int Track)> _cues = new();
        private WriterTrack[] _tracks = [];
        private EbmlWriter? _cluster;
        private long _clusterTime;
        private long _segmentDataStart;
        private long _durationPosition = -1;
        private long _endTime;
        private int _cueTrack = -1;
        private bool _opened;
        private bool _completed;
        private bool _disposed;

        /// <param name="destination">Where the file goes; written forward, and patched at the end when it can seek.</param>
        /// <param name="docType">Matroska or WebM.</param>
        /// <param name="leaveOpen">Leave <paramref name="destination"/> open when this writer is disposed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> isn't writable.</exception>
        public MatroskaWriter(Stream destination, MatroskaDocType docType = MatroskaDocType.Matroska, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable", nameof(destination));
            }

            _destination = destination;
            _docType = docType;
            _leaveOpen = leaveOpen;
        }

        /// <summary>Writes the file header and the tracks.</summary>
        /// <exception cref="ArgumentException">An empty set, or a track whose configuration is missing or invalid.</exception>
        /// <exception cref="NotSupportedException">A codec the format can't carry (WebM: anything but VP9, AV1 and Opus).</exception>
        public void Open(MediaTrackSet tracks)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(tracks);
            if (_opened)
            {
                throw new InvalidOperationException("The writer is already open");
            }

            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("A Matroska file needs at least one track.", nameof(tracks));
            }

            var writerTracks = new WriterTrack[tracks.Tracks.Count];
            for (var i = 0; i < writerTracks.Length; i++)
            {
                writerTracks[i] = WriterTrack.Create(tracks.Tracks[i], i + 1, _docType);
            }

            _tracks = writerTracks;
            _cueTrack = Array.FindIndex(writerTracks, t => t.Info.Kind == MediaKind.Video);
            WriteHeader();
            _opened = true;
        }

        /// <summary>Adds one packet; a finished cluster is written out first.</summary>
        /// <exception cref="ArgumentException">An unknown track, another config version, or a DTS not greater than the track's previous one.</exception>
        public void Write(MediaPacket packet)
        {
            ThrowIfNotWritable();
            var index = Array.FindIndex(_tracks, t => t.Info.TrackId == packet.TrackId);
            if (index < 0)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this writer's track set.", nameof(packet));
            }

            var track = _tracks[index];
            if (packet.ConfigVersion != track.Info.ConfigVersion)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has config version {packet.ConfigVersion}; the track's is {track.Info.ConfigVersion}. A Matroska file can't change configuration mid-stream.", nameof(packet));
            }

            if (track.LastDts is { } last && packet.Dts <= last)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, not greater than the track's previous {last}.", nameof(packet));
            }

            track.LastDts = packet.Dts;
            var time = MediaTimestamp.Rescale(packet.Pts, track.Info.Timescale, (int)MillisecondTimescale) + track.CodecDelayMilliseconds;
            var keyframe = track.Info.Kind == MediaKind.Audio || packet.IsKeyframe;
            var startsCluster = _cluster is null
                || (index == _cueTrack && keyframe)
                || time - _clusterTime is > short.MaxValue or < short.MinValue
                || time - _clusterTime > MaxClusterMilliseconds
                || _cluster.Length > MaxClusterBytes;
            if (startsCluster)
            {
                FlushCluster();
                _clusterTime = Math.Max(0, time);
                _cluster = new EbmlWriter();
                _cluster.Unsigned(MatroskaIds.ClusterTimestamp, (ulong)_clusterTime);
                if (time - _clusterTime is > short.MaxValue or < short.MinValue)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a timestamp ({time} ms) Matroska can't place in a cluster.", nameof(packet));
                }
            }

            if (index == _cueTrack && keyframe)
            {
                _cues.Add((time, -1, track.Number));
            }

            var relative = (short)(time - _clusterTime);
            var header = new byte[4];
            header[0] = (byte)(0x80 | track.Number);
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(1), relative);
            header[3] = keyframe ? (byte)0x80 : (byte)0;
            _cluster!.WriteId(MatroskaIds.SimpleBlock);
            _cluster.WriteSize(header.Length + packet.Payload.Length);
            _cluster.Raw(header);
            _cluster.Raw(packet.Payload.Span);
            _endTime = Math.Max(_endTime, time);
        }

        /// <summary>Writes the last cluster, and when the destination can seek, the cues, segment size and duration.</summary>
        public void Complete()
        {
            ThrowIfNotWritable();
            FlushCluster();
            _completed = true;
            if (_destination.CanSeek)
            {
                WriteCues();
                var end = _destination.Position;
                _destination.Position = _segmentDataStart - EbmlWriter.PatchableSizeLength;
                _destination.Write(EbmlWriter.Size(end - _segmentDataStart, EbmlWriter.PatchableSizeLength));
                Span<byte> duration = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(duration, _endTime);
                _destination.Position = _durationPosition;
                _destination.Write(duration);
                _destination.Position = end;
            }

            _destination.Flush();
        }

        /// <inheritdoc />
        public ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open(tracks);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask WriteAsync(MediaPacket packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(packet);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Complete();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_leaveOpen)
            {
                _destination.Dispose();
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void WriteHeader()
        {
            var ebml = new EbmlWriter();
            ebml.Unsigned(0x4286, 1);
            ebml.Unsigned(0x42F7, 1);
            ebml.Unsigned(0x42F2, 4);
            ebml.Unsigned(0x42F3, 8);
            ebml.Text(MatroskaIds.DocType, _docType == MatroskaDocType.WebM ? "webm" : "matroska");
            ebml.Unsigned(0x4287, 4);
            ebml.Unsigned(0x4285, 2);
            var header = new EbmlWriter();
            header.Master(MatroskaIds.Ebml, ebml);
            header.WriteId(MatroskaIds.Segment);
            header.WriteUnknownSize();
            _destination.Write(header.ToArray());
            _segmentDataStart = _destination.CanSeek ? _destination.Position : header.Length;

            var info = new EbmlWriter();
            info.Unsigned(MatroskaIds.TimestampScale, 1_000_000);
            info.Text(0x4D80, "EggEncoder");
            info.Text(0x5741, "EggEncoder");
            info.Float(MatroskaIds.Duration, 0);
            var infoElement = new EbmlWriter();
            infoElement.Master(MatroskaIds.Info, info);
            var infoBytes = infoElement.ToArray();
            if (_destination.CanSeek)
            {
                _durationPosition = _destination.Position + infoBytes.Length - 8;
            }

            _destination.Write(infoBytes);

            var tracks = new EbmlWriter();
            foreach (var track in _tracks)
            {
                tracks.Master(MatroskaIds.TrackEntry, track.BuildEntry());
            }

            var tracksElement = new EbmlWriter();
            tracksElement.Master(MatroskaIds.Tracks, tracks);
            _destination.Write(tracksElement.ToArray());
        }

        private void FlushCluster()
        {
            if (_cluster is null)
            {
                return;
            }

            var position = (_destination.CanSeek ? _destination.Position : 0) - _segmentDataStart;
            for (var i = _cues.Count - 1; i >= 0 && _cues[i].ClusterPosition < 0; i--)
            {
                _cues[i] = (_cues[i].Time, position, _cues[i].Track);
            }

            var element = new EbmlWriter();
            element.Master(MatroskaIds.Cluster, _cluster);
            _destination.Write(element.ToArray());
            _cluster = null;
        }

        private void WriteCues()
        {
            if (_cues.Count == 0)
            {
                return;
            }

            var cues = new EbmlWriter();
            foreach (var (time, position, track) in _cues)
            {
                var positions = new EbmlWriter();
                positions.Unsigned(0xF7, (ulong)track);
                positions.Unsigned(0xF1, (ulong)position);
                var point = new EbmlWriter();
                point.Unsigned(0xB3, (ulong)Math.Max(0, time));
                point.Master(0xB7, positions);
                cues.Master(0xBB, point);
            }

            var element = new EbmlWriter();
            element.Master(MatroskaIds.Cues, cues);
            _destination.Write(element.ToArray());
        }

        private void ThrowIfNotWritable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_opened)
            {
                throw new InvalidOperationException("Open the writer before writing packets");
            }

            if (_completed)
            {
                throw new InvalidOperationException("The writer is complete");
            }
        }

        private sealed class WriterTrack
        {
            private WriterTrack(MediaTrackInfo info, int number, string codecId, ReadOnlyMemory<byte> codecPrivate, int width, int height, long codecDelayNanoseconds)
            {
                Info = info;
                Number = number;
                CodecId = codecId;
                CodecPrivate = codecPrivate;
                Width = width;
                Height = height;
                CodecDelayNanoseconds = codecDelayNanoseconds;
                CodecDelayMilliseconds = (long)Math.Round(codecDelayNanoseconds / 1_000_000.0, MidpointRounding.AwayFromZero);
            }

            public MediaTrackInfo Info { get; }

            public int Number { get; }

            public string CodecId { get; }

            public ReadOnlyMemory<byte> CodecPrivate { get; }

            public int Width { get; }

            public int Height { get; }

            public long CodecDelayNanoseconds { get; }

            public long CodecDelayMilliseconds { get; }

            public long? LastDts { get; set; }

            public static WriterTrack Create(MediaTrackInfo info, int number, MatroskaDocType docType)
            {
                if (number > 126)
                {
                    throw new NotSupportedException("A Matroska file written by this writer holds at most 126 tracks.");
                }

                if (docType == MatroskaDocType.WebM && info.Codec is not (MediaCodec.Vp9 or MediaCodec.Av1 or MediaCodec.Opus))
                {
                    throw new NotSupportedException($"WebM can't carry {info.Codec}; write a Matroska file instead.");
                }

                switch (info.Codec)
                {
                    case MediaCodec.H264:
                        var avc = Parse(() => AvcDecoderConfigurationRecord.Parse(info.CodecConfiguration.Span), info);
                        var (avcWidth, avcHeight) = Size(info, () => H264SpsParser.Parse(avc.SequenceParameterSets[0].Span) is var sps ? (sps.Width, sps.Height) : default);
                        return new WriterTrack(info, number, "V_MPEG4/ISO/AVC", (avc with { NalLengthSize = 4 }).ToArray(), avcWidth, avcHeight, 0);
                    case MediaCodec.H265:
                        var hevc = Parse(() => HevcDecoderConfigurationRecord.Parse(info.CodecConfiguration.Span), info);
                        var (hevcWidth, hevcHeight) = Size(info, () => H265SpsParser.Parse(hevc.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType)[0].Span) is var sps ? (sps.Width, sps.Height) : default);
                        return new WriterTrack(info, number, "V_MPEGH/ISO/HEVC", (hevc with { NalLengthSize = 4 }).ToArray(), hevcWidth, hevcHeight, 0);
                    case MediaCodec.Av1:
                        return new WriterTrack(info, number, "V_AV1", info.CodecConfiguration, RequireSize(info.Width, info), RequireSize(info.Height, info), 0);
                    case MediaCodec.Vp9:
                        return new WriterTrack(info, number, "V_VP9", ReadOnlyMemory<byte>.Empty, RequireSize(info.Width, info), RequireSize(info.Height, info), 0);
                    case MediaCodec.Aac:
                        if (info.CodecConfiguration.IsEmpty)
                        {
                            throw new ArgumentException($"AAC track {info.TrackId} has no AudioSpecificConfig.");
                        }

                        return new WriterTrack(info, number, "A_AAC", info.CodecConfiguration, 0, 0, 0);
                    case MediaCodec.Mp3:
                        return new WriterTrack(info, number, "A_MPEG/L3", ReadOnlyMemory<byte>.Empty, 0, 0, 0);
                    case MediaCodec.Opus:
                        var head = info.CodecConfiguration.Span;
                        if (head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8))
                        {
                            throw new ArgumentException($"Opus track {info.TrackId} has no OpusHead configuration.");
                        }

                        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
                        return new WriterTrack(info, number, "A_OPUS", info.CodecConfiguration, 0, 0, preSkip * 1_000_000_000L / OpusSampleRate);
                    default:
                        throw new NotSupportedException($"Matroska writing doesn't support {info.Codec}.");
                }
            }

            public EbmlWriter BuildEntry()
            {
                var entry = new EbmlWriter();
                entry.Unsigned(MatroskaIds.TrackNumber, (ulong)Number);
                entry.Unsigned(0x73C5, (ulong)Number);
                entry.Unsigned(MatroskaIds.TrackType, Info.Kind == MediaKind.Video ? 1UL : 2UL);
                entry.Unsigned(0x9C, 0);
                entry.Text(MatroskaIds.CodecId, CodecId);
                if (!CodecPrivate.IsEmpty)
                {
                    entry.Binary(MatroskaIds.CodecPrivate, CodecPrivate.Span);
                }

                if (CodecDelayNanoseconds > 0)
                {
                    entry.Unsigned(MatroskaIds.CodecDelay, (ulong)CodecDelayNanoseconds);
                    entry.Unsigned(0x56BB, 80_000_000);
                }

                if (Info.Kind == MediaKind.Video)
                {
                    var video = new EbmlWriter();
                    video.Unsigned(MatroskaIds.PixelWidth, (ulong)Width);
                    video.Unsigned(MatroskaIds.PixelHeight, (ulong)Height);
                    entry.Master(MatroskaIds.Video, video);
                }
                else
                {
                    var audio = new EbmlWriter();
                    var sampleRate = Info.Codec == MediaCodec.Opus ? OpusSampleRate : Info.SampleRate;
                    if (sampleRate > 0)
                    {
                        audio.Float(MatroskaIds.SamplingFrequency, sampleRate);
                    }

                    audio.Unsigned(MatroskaIds.Channels, (ulong)Math.Max(1, Info.Channels));
                    entry.Master(MatroskaIds.Audio, audio);
                }

                return entry;
            }

            private static T Parse<T>(Func<T> parse, MediaTrackInfo info)
            {
                try
                {
                    return parse();
                }
                catch (InvalidDataException error)
                {
                    throw new ArgumentException($"Track {info.TrackId} has an invalid {info.Codec} configuration.", error);
                }
            }

            private static (int Width, int Height) Size(MediaTrackInfo info, Func<(int Width, int Height)> fromSps)
            {
                if (info.Width > 0 && info.Height > 0)
                {
                    return (info.Width, info.Height);
                }

                try
                {
                    var size = fromSps();
                    return (RequireSize(size.Width, info), RequireSize(size.Height, info));
                }
                catch (Exception error) when (error is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                {
                    throw new ArgumentException($"Video track {info.TrackId} has no size and its parameter sets don't give one.", error);
                }
            }

            private static int RequireSize(int value, MediaTrackInfo info) =>
                value > 0 ? value : throw new ArgumentException($"Video track {info.TrackId} has no width or height.");
        }
    }
}
