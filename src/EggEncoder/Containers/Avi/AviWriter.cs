using System.Buffers.Binary;
using System.Text;
using EggEncoder.Media;

namespace EggEncoder.Containers.Avi
{
    /// <summary>
    /// Writes a packet stream as an AVI file, passed through without decoding: H.264 video (fourcc <c>H264</c>, Annex B
    /// with its parameter sets before every keyframe) and MP3 or AAC audio, one chunk per packet, interleaved in arrival
    /// order, with an <c>idx1</c> index. AVI has a constant frame rate and no presentation times: a video track's frame
    /// duration is the greatest common divisor of its decode-time steps, gaps are filled with empty chunks, and
    /// presentation times (B-frame reordering) are not stored. Chunks wait in a scratch stream until
    /// <see cref="Complete"/>, which writes the file in one forward pass. Files past 1 GiB are refused (no OpenDML index).
    /// </summary>
    public sealed class AviWriter : IMediaPacketSink, IDisposable
    {
        private const long MaxFileBytes = 1L << 30;
        private const int MaxStreams = 99;
        private const int KeyframeFlag = 0x10;
        private const int MaxFillerFactor = 10;

        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly Stream _scratch;
        private readonly bool _ownsScratch;
        private readonly List<Chunk> _chunks = new();
        private WriterStream[] _streams = [];
        private bool _opened;
        private bool _completed;
        private bool _disposed;

        /// <param name="destination">Where the AVI bytes go; written forward only, during <see cref="Complete"/>.</param>
        /// <param name="leaveOpen">Leave <paramref name="destination"/> open when this writer is disposed.</param>
        /// <param name="scratch">
        /// A readable, writable, seekable stream that holds chunk data until <see cref="Complete"/>; not disposed by this
        /// writer. <c>null</c> uses a temporary file that is deleted when the writer is disposed.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentException">The destination isn't writable, or the scratch stream can't read, write and seek.</exception>
        public AviWriter(Stream destination, bool leaveOpen = false, Stream? scratch = null)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable", nameof(destination));
            }

            if (scratch is not null && (!scratch.CanRead || !scratch.CanWrite || !scratch.CanSeek))
            {
                throw new ArgumentException("The scratch stream must be readable, writable and seekable", nameof(scratch));
            }

            _destination = destination;
            _leaveOpen = leaveOpen;
            _ownsScratch = scratch is null;
            _scratch = scratch ?? new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        }

        /// <summary>Starts the file for these tracks. Nothing is written to the destination until <see cref="Complete"/>.</summary>
        /// <exception cref="ArgumentException">An empty set, or a track whose configuration is missing or invalid.</exception>
        /// <exception cref="NotSupportedException">A codec other than H.264, MP3 or AAC, or more than 99 tracks.</exception>
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
                throw new ArgumentException("An AVI file needs at least one track.", nameof(tracks));
            }

            if (tracks.Tracks.Count > MaxStreams)
            {
                throw new NotSupportedException($"An AVI file holds at most {MaxStreams} streams.");
            }

            _streams = tracks.Tracks.Select((t, i) => WriterStream.Create(t, i)).ToArray();
            _opened = true;
        }

        /// <summary>Adds one packet; its data is copied to the scratch stream.</summary>
        /// <exception cref="ArgumentException">An unknown track, another config version, or a DTS not greater than the track's previous one.</exception>
        /// <exception cref="NotSupportedException">The file would pass 1 GiB.</exception>
        public void Write(MediaPacket packet)
        {
            ThrowIfNotWritable();
            var index = Array.FindIndex(_streams, s => s.Info.TrackId == packet.TrackId);
            if (index < 0)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this writer's track set.", nameof(packet));
            }

            var stream = _streams[index];
            if (packet.ConfigVersion != stream.Info.ConfigVersion)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has config version {packet.ConfigVersion}; the track's is {stream.Info.ConfigVersion}. An AVI file can't change configuration mid-stream.", nameof(packet));
            }

            if (stream.Dts.Count > 0 && packet.Dts <= stream.Dts[^1])
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, not greater than the track's previous {stream.Dts[^1]}.", nameof(packet));
            }

            var data = stream.ToChunkData(packet);
            if (_scratch.Position + data.Length + (16L * (_chunks.Count + 1)) > MaxFileBytes)
            {
                throw new NotSupportedException("AVI files larger than 1 GiB are not supported.");
            }

            stream.Dts.Add(packet.Dts);
            stream.LargestChunk = Math.Max(stream.LargestChunk, data.Length);
            stream.TotalBytes += data.Length;
            _chunks.Add(new Chunk(index, stream.Dts.Count - 1, stream.Info.Kind == MediaKind.Audio || packet.IsKeyframe, _scratch.Position, data.Length));
            _scratch.Write(data);
        }

        /// <summary>Writes the whole file to the destination and flushes it. Further writes throw.</summary>
        public void Complete()
        {
            ThrowIfNotWritable();
            foreach (var stream in _streams)
            {
                stream.PlanTiming();
            }

            var movi = PlanMovi();
            var headerList = BuildHeaderList();
            var moviSize = 4 + movi.Sum(e => 8L + e.Size + (e.Size & 1));
            var index = BuildIndex(movi);
            var riffSize = 4 + 8 + headerList.Length + 8 + moviSize + index.Length;
            if (riffSize + 8 > MaxFileBytes)
            {
                throw new NotSupportedException("AVI files larger than 1 GiB are not supported.");
            }

            _completed = true;
            Span<byte> header = stackalloc byte[12];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)riffSize);
            "AVI "u8.CopyTo(header[8..]);
            _destination.Write(header);
            WriteList("hdrl", headerList);
            Span<byte> moviHeader = stackalloc byte[12];
            "LIST"u8.CopyTo(moviHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(moviHeader[4..], (uint)moviSize);
            "movi"u8.CopyTo(moviHeader[8..]);
            _destination.Write(moviHeader);
            var buffer = new byte[64 * 1024];
            Span<byte> chunkHeader = stackalloc byte[8];
            foreach (var entry in movi)
            {
                Encoding.ASCII.GetBytes(entry.Id, chunkHeader);
                BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], (uint)entry.Size);
                _destination.Write(chunkHeader);
                if (entry.Size > 0)
                {
                    CopyScratch(entry.ScratchOffset, entry.Size, buffer);
                }

                if ((entry.Size & 1) == 1)
                {
                    _destination.WriteByte(0);
                }
            }

            _destination.Write(index);
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
            if (_ownsScratch)
            {
                _scratch.Dispose();
            }

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

        private List<MoviEntry> PlanMovi()
        {
            var entries = new List<MoviEntry>(_chunks.Count);
            var nextFrame = new long[_streams.Length];
            foreach (var chunk in _chunks)
            {
                var stream = _streams[chunk.Stream];
                var id = stream.ChunkId;
                if (stream.Info.Kind == MediaKind.Video)
                {
                    var frame = stream.FrameIndex(chunk.Ordinal);
                    while (nextFrame[chunk.Stream] < frame)
                    {
                        entries.Add(new MoviEntry(id, false, -1, 0));
                        nextFrame[chunk.Stream]++;
                    }

                    nextFrame[chunk.Stream] = frame + 1;
                }

                entries.Add(new MoviEntry(id, chunk.Keyframe, chunk.ScratchOffset, chunk.Size));
            }

            for (var i = 0; i < _streams.Length; i++)
            {
                _streams[i].ChunkCount = entries.Count(e => e.Id == _streams[i].ChunkId);
            }

            return entries;
        }

        private byte[] BuildHeaderList()
        {
            var video = Array.Find(_streams, s => s.Info.Kind == MediaKind.Video);
            var duration = _streams.Max(s => s.DurationSeconds);
            var list = new MemoryStream();
            var avih = new byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(avih, video is null ? 0 : (uint)Math.Round(1_000_000.0 * video.Scale / video.Rate));
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(4), (uint)Math.Min(uint.MaxValue, duration > 0 ? _streams.Sum(s => s.TotalBytes) / duration : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(12), 0x910);
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), (uint)(video?.ChunkCount ?? 0));
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), (uint)_streams.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(28), (uint)_streams.Max(s => s.LargestChunk));
            BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(32), video?.Width ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(36), video?.Height ?? 0);
            WriteChunk(list, "avih", avih);
            foreach (var stream in _streams)
            {
                var strl = new MemoryStream();
                strl.Write("strl"u8);
                WriteChunk(strl, "strh", stream.BuildStreamHeader());
                WriteChunk(strl, "strf", stream.BuildStreamFormat());
                WriteChunk(list, "LIST", strl.ToArray());
            }

            return list.ToArray();
        }

        private static byte[] BuildIndex(List<MoviEntry> movi)
        {
            var index = new byte[8 + (16 * movi.Count)];
            "idx1"u8.CopyTo(index);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), (uint)(16 * movi.Count));
            long offset = 4;
            for (var i = 0; i < movi.Count; i++)
            {
                var entry = index.AsSpan(8 + (16 * i));
                Encoding.ASCII.GetBytes(movi[i].Id, entry);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], movi[i].Keyframe ? (uint)KeyframeFlag : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)offset);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)movi[i].Size);
                offset += 8 + movi[i].Size + (movi[i].Size & 1);
            }

            return index;
        }

        private void WriteList(string type, byte[] body)
        {
            Span<byte> header = stackalloc byte[12];
            "LIST"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(body.Length + 4));
            Encoding.ASCII.GetBytes(type, header[8..]);
            _destination.Write(header);
            _destination.Write(body);
        }

        private static void WriteChunk(Stream target, string id, byte[] body)
        {
            Span<byte> header = stackalloc byte[8];
            Encoding.ASCII.GetBytes(id, header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)body.Length);
            target.Write(header);
            target.Write(body);
            if ((body.Length & 1) == 1)
            {
                target.WriteByte(0);
            }
        }

        private void CopyScratch(long offset, int size, byte[] buffer)
        {
            _scratch.Position = offset;
            while (size > 0)
            {
                var read = _scratch.Read(buffer, 0, Math.Min(size, buffer.Length));
                if (read == 0)
                {
                    throw new IOException("The scratch stream ended early");
                }

                _destination.Write(buffer, 0, read);
                size -= read;
            }
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

        private readonly record struct Chunk(int Stream, int Ordinal, bool Keyframe, long ScratchOffset, int Size);

        private readonly record struct MoviEntry(string Id, bool Keyframe, long ScratchOffset, int Size);

        private sealed class WriterStream
        {
            private readonly byte[] _parameterSets;
            private readonly int _samplesPerFrame;
            private long _frameDuration = 1;
            private long _firstDts;
            private bool _useOrdinals;

            private WriterStream(MediaTrackInfo info, int number, byte[] parameterSets, int width, int height, int samplesPerFrame)
            {
                Info = info;
                ChunkId = $"{number:D2}{(info.Kind == MediaKind.Video ? "dc" : "wb")}";
                _parameterSets = parameterSets;
                Width = width;
                Height = height;
                _samplesPerFrame = samplesPerFrame;
            }

            public MediaTrackInfo Info { get; }

            public string ChunkId { get; }

            public int Width { get; }

            public int Height { get; }

            public List<long> Dts { get; } = new();

            public int LargestChunk { get; set; }

            public long TotalBytes { get; set; }

            public int ChunkCount { get; set; }

            public long Scale { get; private set; } = 1;

            public long Rate { get; private set; } = 1;

            public double DurationSeconds => Dts.Count == 0 ? 0 : (double)ChunkCount * Scale / Rate;

            public static WriterStream Create(MediaTrackInfo info, int number)
            {
                switch (info.Codec)
                {
                    case MediaCodec.H264:
                        AvcDecoderConfigurationRecord record;
                        try
                        {
                            record = AvcDecoderConfigurationRecord.Parse(info.CodecConfiguration.Span);
                        }
                        catch (InvalidDataException error)
                        {
                            throw new ArgumentException($"Track {info.TrackId} has an invalid avcC record.", error);
                        }

                        var parameterSets = record.SequenceParameterSets.Concat(record.PictureParameterSets)
                            .SelectMany(unit => (byte[])[0, 0, 0, 1, .. unit.Span]).ToArray();
                        var (width, height) = (info.Width, info.Height);
                        if (width <= 0 || height <= 0)
                        {
                            try
                            {
                                var sps = H264SpsParser.Parse(record.SequenceParameterSets[0].Span);
                                (width, height) = (sps.Width, sps.Height);
                            }
                            catch (InvalidDataException error)
                            {
                                throw new ArgumentException($"Video track {info.TrackId} has no size and its SPS doesn't give one.", error);
                            }
                        }

                        return new WriterStream(info, number, parameterSets, width, height, 0);
                    case MediaCodec.Mp3:
                        return new WriterStream(info, number, [], 0, 0, info.SampleRate is > 0 and < 32000 ? 576 : 1152);
                    case MediaCodec.Aac:
                        if (info.CodecConfiguration.IsEmpty)
                        {
                            throw new ArgumentException($"AAC track {info.TrackId} has no AudioSpecificConfig.");
                        }

                        return new WriterStream(info, number, [], 0, 0, 1024);
                    default:
                        throw new NotSupportedException($"AVI writing doesn't support {info.Codec}.");
                }
            }

            public byte[] ToChunkData(MediaPacket packet)
            {
                if (Info.Kind != MediaKind.Video)
                {
                    return packet.Payload.ToArray();
                }

                var annexB = AnnexBConverter.ToAnnexB(packet.Payload.Span);
                return packet.IsKeyframe ? [.. _parameterSets, .. annexB] : annexB;
            }

            public void PlanTiming()
            {
                if (Info.Kind != MediaKind.Video)
                {
                    Scale = _samplesPerFrame;
                    Rate = Info.SampleRate > 0 ? Info.SampleRate : Info.Timescale;
                    return;
                }

                _firstDts = Dts.Count > 0 ? Dts[0] : 0;
                long divisor = 0;
                for (var i = 1; i < Dts.Count; i++)
                {
                    divisor = Gcd(divisor, Dts[i] - Dts[i - 1]);
                }

                var span = Dts.Count > 1 ? Dts[^1] - _firstDts : 0;
                _useOrdinals = divisor <= 0 || span / divisor > (long)MaxFillerFactor * Dts.Count;
                _frameDuration = _useOrdinals ? Math.Max(1, Dts.Count > 1 ? span / (Dts.Count - 1) : 1) : divisor;
                var common = Gcd(_frameDuration, Info.Timescale);
                Scale = _frameDuration / common;
                Rate = Info.Timescale / common;
            }

            public long FrameIndex(int ordinal) => _useOrdinals ? ordinal : (Dts[ordinal] - _firstDts) / _frameDuration;

            public byte[] BuildStreamHeader()
            {
                var header = new byte[56];
                Encoding.ASCII.GetBytes(Info.Kind == MediaKind.Video ? "vids" : "auds", header);
                if (Info.Kind == MediaKind.Video)
                {
                    "H264"u8.CopyTo(header.AsSpan(4));
                }

                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)Scale);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)Rate);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), (uint)ChunkCount);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), (uint)LargestChunk);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), -1);
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(52), (short)Math.Min(Width, short.MaxValue));
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(54), (short)Math.Min(Height, short.MaxValue));
                return header;
            }

            public byte[] BuildStreamFormat()
            {
                if (Info.Kind == MediaKind.Video)
                {
                    var format = new byte[40 + _parameterSets.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(format, 40);
                    BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(4), Width);
                    BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(8), Height);
                    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), 24);
                    "H264"u8.CopyTo(format.AsSpan(16));
                    BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(20), (uint)(Width * Height * 3));
                    _parameterSets.CopyTo(format, 40);
                    return format;
                }

                var extra = Info.Codec == MediaCodec.Aac ? Info.CodecConfiguration.ToArray() : BuildMp3Extra();
                var wave = new byte[18 + extra.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(wave, Info.Codec == MediaCodec.Aac ? (ushort)0xFF : (ushort)0x55);
                BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(2), (ushort)Math.Max(1, Info.Channels));
                BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)Rate);
                BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(8), (uint)(DurationSeconds > 0 ? TotalBytes / DurationSeconds : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(12), Info.Codec == MediaCodec.Aac ? (ushort)1024 : (ushort)1);
                BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(16), (ushort)extra.Length);
                extra.CopyTo(wave, 18);
                return wave;
            }

            private byte[] BuildMp3Extra()
            {
                var extra = new byte[12];
                BinaryPrimitives.WriteUInt16LittleEndian(extra, 1);
                BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(2), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(6), (ushort)Math.Min(ushort.MaxValue, ChunkCount > 0 ? TotalBytes / ChunkCount : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(8), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(10), 1393);
                return extra;
            }

            private static long Gcd(long a, long b)
            {
                a = Math.Abs(a);
                b = Math.Abs(b);
                while (b != 0)
                {
                    (a, b) = (b, a % b);
                }

                return a;
            }
        }
    }
}
