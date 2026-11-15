using System.Runtime.CompilerServices;
using EggEncoder.Media;

namespace EggEncoder.Containers.Matroska
{
    /// <summary>
    /// Reads a Matroska or WebM file (RFC 9559) as a <see cref="MediaTrackSet"/> plus <see cref="MediaPacket"/>s in file
    /// order (a video packet comes out up to 16 packets later, once its decode time is known), without decoding. H.264, H.265, AV1, VP9, AAC, MP3 and Opus tracks are read; other codecs and encrypted or
    /// compressed tracks are skipped and counted in <see cref="SkippedTrackCount"/>. Matroska stores presentation times
    /// only: video decode times are rebuilt from a 16-packet reorder window (negative at the start of a stream with
    /// B-frames), audio packets carry decode time = presentation time, and an audio track's codec delay (Opus pre-skip) is
    /// subtracted from its timestamps. H.264/H.265 payloads are returned with 4-byte length prefixes. The stream needs no
    /// seeking; clusters of unknown size (live recordings) are supported.
    /// </summary>
    public sealed class MatroskaFileSource : IAsyncDisposable, IDisposable
    {
        private const int MaxCodecPrivateBytes = 16 * 1024 * 1024;
        private const int MaxBlockBytes = 64 * 1024 * 1024;
        private const int MaxTracks = 128;
        private const int ReorderWindow = 16;

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly EbmlReader _reader;
        private readonly Dictionary<int, MatroskaTrack> _tracks;
        private readonly long _timestampScale;
        private bool _started;
        private bool _disposed;

        /// <summary>Reads the file's header, segment information and tracks from <paramref name="source"/>.</summary>
        /// <param name="source">A readable stream positioned at the start of the file.</param>
        /// <param name="leaveOpen">True to leave <paramref name="source"/> open when this reader is disposed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="InvalidDataException">The data is not Matroska/WebM, or its header or tracks are damaged.</exception>
        public MatroskaFileSource(Stream source, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            _stream = source;
            _leaveOpen = leaveOpen;
            _reader = new EbmlReader(source);
            try
            {
                DocType = ReadEbmlHeader();
                (_timestampScale, Duration, _tracks, SkippedTrackCount) = ReadSegmentHeader();
                Tracks = new MediaTrackSet(_tracks.Values.OrderBy(t => t.Info.TrackId).Select(t => t.Info).ToList());
            }
            catch
            {
                if (!leaveOpen)
                {
                    source.Dispose();
                }

                throw;
            }
        }

        /// <summary>The EBML document type: "matroska" or "webm".</summary>
        public string DocType { get; }

        /// <summary>The tracks this reader returns packets for, identified by their Matroska track numbers.</summary>
        public MediaTrackSet Tracks { get; }

        /// <summary>The segment duration the file declares, or zero when it declares none.</summary>
        public TimeSpan Duration { get; }

        /// <summary>Tracks left out: unsupported codecs, subtitles, and encrypted or compressed tracks.</summary>
        public int SkippedTrackCount { get; }

        /// <summary>Opens a Matroska or WebM file.</summary>
        /// <param name="path">The file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="InvalidDataException">The file is not Matroska/WebM, or its header or tracks are damaged.</exception>
        public static MatroskaFileSource Open(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return new MatroskaFileSource(File.OpenRead(path));
        }

        /// <summary>Reads the packets once, in file order. Can be enumerated only once.</summary>
        /// <exception cref="InvalidOperationException">The packets were already read.</exception>
        /// <exception cref="InvalidDataException">A cluster or block is damaged.</exception>
        public IEnumerable<MediaPacket> ReadPackets()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Matroska: the packets can be read only once");
            }

            _started = true;
            return ReadAll();
        }

        /// <summary>Reads the packets once, in file order, yielding between clusters.</summary>
        /// <param name="cancellationToken">Stops the reading.</param>
        /// <exception cref="InvalidOperationException">The packets were already read.</exception>
        /// <exception cref="InvalidDataException">A cluster or block is damaged.</exception>
        public async IAsyncEnumerable<MediaPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var packet in ReadPackets())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return packet;
                await Task.Yield();
            }
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
                _stream.Dispose();
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private string ReadEbmlHeader()
        {
            if (!_reader.TryReadHeader(out var id, out var size) || id != MatroskaIds.Ebml || size < 0)
            {
                throw new InvalidDataException("Matroska: the file doesn't start with an EBML header");
            }

            var end = _reader.Position + size;
            var docType = "matroska";
            while (_reader.Position < end && _reader.TryReadHeader(out var child, out var childSize))
            {
                if (child == MatroskaIds.DocType)
                {
                    docType = _reader.ReadString(childSize, 64);
                }
                else
                {
                    _reader.Skip(childSize);
                }
            }

            if (docType is not ("matroska" or "webm"))
            {
                throw new InvalidDataException($"Matroska: unsupported document type '{docType}'");
            }

            return docType;
        }

        private (long TimestampScale, TimeSpan Duration, Dictionary<int, MatroskaTrack> Tracks, int Skipped) ReadSegmentHeader()
        {
            if (!_reader.TryReadHeader(out var id, out _) || id != MatroskaIds.Segment)
            {
                throw new InvalidDataException("Matroska: the EBML header isn't followed by a segment");
            }

            long timestampScale = 1_000_000;
            double duration = 0;
            Dictionary<int, MatroskaTrack>? tracks = null;
            var skipped = 0;
            while (_reader.TryReadHeader(out var child, out var size))
            {
                switch (child)
                {
                    case MatroskaIds.Info:
                        (timestampScale, duration) = ReadInfo(size);
                        break;
                    case MatroskaIds.Tracks:
                        (tracks, skipped) = ReadTracks(size);
                        break;
                    case MatroskaIds.Cluster:
                        _reader.PushBack(child, size);
                        return Finish();
                    default:
                        _reader.Skip(size);
                        break;
                }
            }

            return Finish();

            (long, TimeSpan, Dictionary<int, MatroskaTrack>, int) Finish()
            {
                if (tracks is null)
                {
                    throw new InvalidDataException("Matroska: the segment has no tracks before its first cluster");
                }

                if (timestampScale <= 0)
                {
                    throw new InvalidDataException("Matroska: the timestamp scale is not positive");
                }

                var ticks = duration > 0 && double.IsFinite(duration) ? duration * timestampScale / 100.0 : 0;
                var span = ticks > 0 && ticks < TimeSpan.MaxValue.Ticks ? TimeSpan.FromTicks((long)ticks) : TimeSpan.Zero;
                foreach (var track in tracks.Values)
                {
                    track.Configure(timestampScale);
                }

                return (timestampScale, span, tracks, skipped);
            }
        }

        private (long TimestampScale, double Duration) ReadInfo(long size)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            long scale = 1_000_000;
            double duration = 0;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                switch (id)
                {
                    case MatroskaIds.TimestampScale:
                        scale = (long)Math.Min(_reader.ReadUnsigned(childSize), long.MaxValue);
                        break;
                    case MatroskaIds.Duration:
                        duration = _reader.ReadFloat(childSize);
                        break;
                    default:
                        _reader.Skip(childSize);
                        break;
                }
            }

            return (scale, duration);
        }

        private (Dictionary<int, MatroskaTrack> Tracks, int Skipped) ReadTracks(long size)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            var tracks = new Dictionary<int, MatroskaTrack>();
            var skipped = 0;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                if (id != MatroskaIds.TrackEntry)
                {
                    _reader.Skip(childSize);
                    continue;
                }

                var entry = ReadTrackEntry(childSize);
                if (tracks.Count + skipped >= MaxTracks)
                {
                    throw new InvalidDataException($"Matroska: the file declares more than {MaxTracks} tracks");
                }

                if (entry is null || tracks.ContainsKey(entry.Number))
                {
                    skipped++;
                    continue;
                }

                tracks[entry.Number] = entry;
            }

            return (tracks, skipped);
        }

        private MatroskaTrack? ReadTrackEntry(long size)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            var entry = new MatroskaTrackEntry();
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                switch (id)
                {
                    case MatroskaIds.TrackNumber:
                        entry.Number = (long)Math.Min(_reader.ReadUnsigned(childSize), long.MaxValue);
                        break;
                    case MatroskaIds.TrackType:
                        entry.Type = (int)Math.Min(_reader.ReadUnsigned(childSize), int.MaxValue);
                        break;
                    case MatroskaIds.CodecId:
                        entry.CodecId = _reader.ReadString(childSize, 256);
                        break;
                    case MatroskaIds.CodecPrivate:
                        entry.CodecPrivate = _reader.ReadBytes(childSize, MaxCodecPrivateBytes);
                        break;
                    case MatroskaIds.CodecDelay:
                        entry.CodecDelayNanoseconds = (long)Math.Min(_reader.ReadUnsigned(childSize), long.MaxValue);
                        break;
                    case MatroskaIds.DefaultDuration:
                        entry.DefaultDurationNanoseconds = (long)Math.Min(_reader.ReadUnsigned(childSize), long.MaxValue);
                        break;
                    case MatroskaIds.ContentEncodings:
                        entry.Encoded = true;
                        _reader.Skip(childSize);
                        break;
                    case MatroskaIds.Video:
                        ReadVideo(childSize, entry);
                        break;
                    case MatroskaIds.Audio:
                        ReadAudio(childSize, entry);
                        break;
                    default:
                        _reader.Skip(childSize);
                        break;
                }
            }

            return MatroskaTrack.TryCreate(entry);
        }

        private void ReadVideo(long size, MatroskaTrackEntry entry)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                switch (id)
                {
                    case MatroskaIds.PixelWidth:
                        entry.Width = (int)Math.Min(_reader.ReadUnsigned(childSize), int.MaxValue);
                        break;
                    case MatroskaIds.PixelHeight:
                        entry.Height = (int)Math.Min(_reader.ReadUnsigned(childSize), int.MaxValue);
                        break;
                    default:
                        _reader.Skip(childSize);
                        break;
                }
            }
        }

        private void ReadAudio(long size, MatroskaTrackEntry entry)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                switch (id)
                {
                    case MatroskaIds.SamplingFrequency:
                        var frequency = _reader.ReadFloat(childSize);
                        entry.SampleRate = frequency is > 0 and < int.MaxValue ? (int)Math.Round(frequency) : 0;
                        break;
                    case MatroskaIds.Channels:
                        entry.Channels = (int)Math.Min(_reader.ReadUnsigned(childSize), int.MaxValue);
                        break;
                    default:
                        _reader.Skip(childSize);
                        break;
                }
            }
        }

        private IEnumerable<MediaPacket> ReadAll()
        {
            var output = new Queue<MediaPacket>();
            while (_reader.TryReadHeader(out var id, out var size))
            {
                if (id != MatroskaIds.Cluster)
                {
                    if (size == EbmlReader.UnknownSize)
                    {
                        throw new InvalidDataException("Matroska: only clusters may have an unknown size");
                    }

                    _reader.Skip(size);
                    continue;
                }

                ReadCluster(size, output);
                while (output.TryDequeue(out var packet))
                {
                    yield return packet;
                }
            }

            foreach (var track in _tracks.Values)
            {
                track.Flush(output);
            }

            while (output.TryDequeue(out var packet))
            {
                yield return packet;
            }
        }

        private void ReadCluster(long size, Queue<MediaPacket> output)
        {
            var end = size == EbmlReader.UnknownSize ? long.MaxValue : _reader.Position + size;
            long clusterTime = 0;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                if (size == EbmlReader.UnknownSize && MatroskaIds.IsTopLevel(id))
                {
                    _reader.PushBack(id, childSize);
                    return;
                }

                switch (id)
                {
                    case MatroskaIds.ClusterTimestamp:
                        clusterTime = (long)Math.Min(_reader.ReadUnsigned(childSize), long.MaxValue / 4);
                        break;
                    case MatroskaIds.SimpleBlock:
                        ReadBlock(childSize, clusterTime, null, output);
                        break;
                    case MatroskaIds.BlockGroup:
                        ReadBlockGroup(childSize, clusterTime, output);
                        break;
                    default:
                        if (childSize == EbmlReader.UnknownSize)
                        {
                            throw new InvalidDataException("Matroska: a cluster child has an unknown size");
                        }

                        _reader.Skip(childSize);
                        break;
                }
            }
        }

        private void ReadBlockGroup(long size, long clusterTime, Queue<MediaPacket> output)
        {
            RequireKnownSize(size);
            var end = _reader.Position + size;
            byte[]? block = null;
            var referenced = false;
            while (_reader.Position < end && _reader.TryReadHeader(out var id, out var childSize))
            {
                switch (id)
                {
                    case MatroskaIds.Block:
                        block = _reader.ReadBytes(childSize, MaxBlockBytes);
                        break;
                    case MatroskaIds.ReferenceBlock:
                        referenced = true;
                        _reader.Skip(childSize);
                        break;
                    default:
                        _reader.Skip(childSize);
                        break;
                }
            }

            if (block is not null)
            {
                ParseBlock(block, clusterTime, !referenced, output);
            }
        }

        private void ReadBlock(long size, long clusterTime, bool? keyframe, Queue<MediaPacket> output)
        {
            var block = _reader.ReadBytes(size, MaxBlockBytes);
            ParseBlock(block, clusterTime, keyframe, output);
        }

        private void ParseBlock(byte[] block, long clusterTime, bool? keyframe, Queue<MediaPacket> output)
        {
            var span = block.AsSpan();
            var trackNumber = ReadBlockVint(span, out var used);
            if (span.Length < used + 3)
            {
                throw new InvalidDataException("Matroska: a block is shorter than its header");
            }

            var relative = (short)((span[used] << 8) | span[used + 1]);
            var flags = span[used + 2];
            var offset = used + 3;
            if (trackNumber > int.MaxValue || !_tracks.TryGetValue((int)trackNumber, out var track))
            {
                return;
            }

            var isKeyframe = keyframe ?? (flags & 0x80) != 0;
            var frames = MatroskaLacing.Split(block, offset, (flags >> 1) & 3);
            track.AddFrames(frames, clusterTime + relative, isKeyframe, output, ReorderWindow);
        }

        private static long ReadBlockVint(ReadOnlySpan<byte> span, out int length)
        {
            if (span.IsEmpty || span[0] == 0)
            {
                throw new InvalidDataException("Matroska: a block's track number is invalid");
            }

            length = System.Numerics.BitOperations.LeadingZeroCount((uint)span[0]) - 23;
            if (span.Length < length)
            {
                throw new InvalidDataException("Matroska: a block's track number is cut short");
            }

            long value = span[0] & ((1 << (8 - length)) - 1);
            for (var i = 1; i < length; i++)
            {
                value = (value << 8) | span[i];
            }

            return value;
        }

        private static void RequireKnownSize(long size)
        {
            if (size < 0)
            {
                throw new InvalidDataException("Matroska: a header element has an unknown size");
            }
        }
    }
}
