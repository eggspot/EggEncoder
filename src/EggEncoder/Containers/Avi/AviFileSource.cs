using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;

namespace EggEncoder.Containers.Avi
{
    /// <summary>
    /// Reads an AVI file (RIFF, including OpenDML <c>AVIX</c> extensions) as a <see cref="MediaTrackSet"/> plus
    /// <see cref="MediaPacket"/>s in file order, without decoding. H.264 video (fourcc <c>H264</c>/<c>X264</c> with Annex B
    /// data, or <c>avc1</c> with an avcC record) and MP3 or AAC audio are read; other streams are skipped and counted in
    /// <see cref="SkippedTrackCount"/>. When an H.264 stream's format has no parameter sets (as ffmpeg writes them), a
    /// seekable source is searched for the first chunk carrying SPS and PPS; a non-seekable one skips that stream. AVI
    /// stores no presentation times: a video packet's decode and presentation times
    /// are both its frame number times the stream's scale, in a timescale of the stream's rate, and empty chunks
    /// (dropped frames) advance the frame number without producing a packet. H.264 payloads are returned with 4-byte
    /// length prefixes; a video packet is a keyframe when it holds an IDR slice. Track ids are the AVI stream numbers.
    /// The stream needs no seeking.
    /// </summary>
    public sealed class AviFileSource : IAsyncDisposable, IDisposable
    {
        private const int MaxHeaderBytes = 16 * 1024 * 1024;
        private const int MaxChunkBytes = 64 * 1024 * 1024;
        private const int MaxStreams = 100;
        private const int MaxParameterSetSearchChunks = 256;
        private const int WaveFormatMp3 = 0x55;
        private const int WaveFormatAac = 0xFF;

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly Dictionary<int, AviSourceStream> _streams = new();
        private readonly List<(int Index, string Handler, byte[] Format, int Rate, uint Scale)> _streamsNeedingParameterSets = new();
        private bool _started;
        private bool _disposed;

        /// <summary>Reads the file's headers from <paramref name="source"/>.</summary>
        /// <param name="source">A readable stream positioned at the start of the file.</param>
        /// <param name="leaveOpen">True to leave <paramref name="source"/> open when this reader is disposed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="InvalidDataException">The data is not AVI, or its headers are damaged.</exception>
        public AviFileSource(Stream source, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            _stream = source;
            _leaveOpen = leaveOpen;
            try
            {
                ReadHeaders();
                Tracks = new MediaTrackSet(_streams.Values.OrderBy(s => s.Info.TrackId).Select(s => s.Info).ToList());
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

        /// <summary>The tracks this reader returns packets for.</summary>
        public MediaTrackSet Tracks { get; }

        /// <summary>Streams left out: other codecs, or H.264 without parameter sets in its header.</summary>
        public int SkippedTrackCount { get; private set; }

        /// <summary>The frame size the main header declares.</summary>
        public (int Width, int Height) FrameSize { get; private set; }

        /// <summary>Opens an AVI file.</summary>
        /// <param name="path">The file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="InvalidDataException">The file is not AVI, or its headers are damaged.</exception>
        public static AviFileSource Open(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return new AviFileSource(File.OpenRead(path));
        }

        /// <summary>Reads the packets once, in file order. Can be enumerated only once.</summary>
        /// <exception cref="InvalidOperationException">The packets were already read.</exception>
        /// <exception cref="InvalidDataException">The media data is damaged.</exception>
        public IEnumerable<MediaPacket> ReadPackets()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("AVI: the packets can be read only once");
            }

            _started = true;
            return ReadMedia();
        }

        /// <summary>Reads the packets once, in file order.</summary>
        /// <param name="cancellationToken">Stops the reading.</param>
        /// <exception cref="InvalidOperationException">The packets were already read.</exception>
        /// <exception cref="InvalidDataException">The media data is damaged.</exception>
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

        private void ReadHeaders()
        {
            Span<byte> riff = stackalloc byte[12];
            ReadExactly(riff);
            if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("AVI "u8))
            {
                throw new InvalidDataException("AVI: the file doesn't start with a RIFF AVI header");
            }

            while (TryReadChunkHeader(out var id, out var size))
            {
                if (id == "LIST" && size >= 4)
                {
                    var listType = ReadFourCc();
                    if (listType == "hdrl")
                    {
                        ReadHeaderList(ReadBytes(size - 4, MaxHeaderBytes));
                        continue;
                    }

                    if (listType == "movi")
                    {
                        ResolveParameterSetsFromStream();
                        return;
                    }

                    Skip(size - 4);
                    continue;
                }

                Skip(size);
            }

            throw new InvalidDataException("AVI: the file has no movi list");
        }

        private void ReadHeaderList(byte[] list)
        {
            var offset = 0;
            var streamIndex = 0;
            while (offset + 8 <= list.Length)
            {
                var id = FourCc(list, offset);
                var size = ReadSize(list, offset + 4);
                var body = Slice(list, offset + 8, size);
                if (id == "avih" && body.Length >= 40)
                {
                    FrameSize = (BinaryPrimitives.ReadInt32LittleEndian(body.Span[32..]), BinaryPrimitives.ReadInt32LittleEndian(body.Span[36..]));
                }
                else if (id == "LIST" && body.Length >= 4 && FourCc(body.Span, 0) == "strl")
                {
                    if (streamIndex >= MaxStreams)
                    {
                        throw new InvalidDataException($"AVI: more than {MaxStreams} streams");
                    }

                    var stream = ReadStreamList(body[4..].ToArray(), streamIndex);
                    if (stream is not null)
                    {
                        _streams[streamIndex] = stream;
                    }
                    else if (!_streamsNeedingParameterSets.Any(s => s.Index == streamIndex))
                    {
                        SkippedTrackCount++;
                    }

                    streamIndex++;
                }

                offset += 8 + size + (size & 1);
            }
        }

        private AviSourceStream? ReadStreamList(byte[] list, int index)
        {
            ReadOnlyMemory<byte> header = default;
            ReadOnlyMemory<byte> format = default;
            var offset = 0;
            while (offset + 8 <= list.Length)
            {
                var id = FourCc(list, offset);
                var size = ReadSize(list, offset + 4);
                var body = Slice(list, offset + 8, size);
                if (id == "strh")
                {
                    header = body;
                }
                else if (id == "strf")
                {
                    format = body;
                }

                offset += 8 + size + (size & 1);
            }

            if (header.Length < 48 || format.IsEmpty)
            {
                return null;
            }

            var h = header.Span;
            var type = FourCc(h, 0);
            var handler = FourCc(h, 4);
            var scale = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            var rate = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            var sampleSize = BinaryPrimitives.ReadUInt32LittleEndian(h[44..]);
            if (scale == 0 || rate == 0)
            {
                return null;
            }

            (scale, rate) = Reduce(scale, rate);
            if (rate > int.MaxValue)
            {
                return null;
            }

            if (type == "vids" && format.Length == 40 && AviSourceStream.IsH264Format(handler, format.Span))
            {
                _streamsNeedingParameterSets.Add((index, handler, format.ToArray(), (int)rate, scale));
                return null;
            }

            return type switch
            {
                "vids" => AviSourceStream.TryCreateVideo(index, handler, format.Span, (int)rate, scale),
                "auds" => TryCreateAudio(index, format.Span, (int)rate, scale, sampleSize),
                _ => null
            };
        }

        private void ResolveParameterSetsFromStream()
        {
            if (_streamsNeedingParameterSets.Count == 0)
            {
                return;
            }

            var pending = _streamsNeedingParameterSets.ToDictionary(s => $"{s.Index:D2}dc");
            if (_stream.CanSeek)
            {
                var start = _stream.Position;
                for (var chunk = 0; chunk < MaxParameterSetSearchChunks && pending.Count > 0 && TryReadChunkHeader(out var id, out var size); chunk++)
                {
                    if (id is "LIST" or "RIFF")
                    {
                        if (size < 4)
                        {
                            break;
                        }

                        var listType = ReadFourCc();
                        if (id != "RIFF" && listType is not ("movi" or "rec "))
                        {
                            Skip(size - 4);
                        }

                        continue;
                    }

                    if (pending.TryGetValue(id, out var candidate) && size <= MaxChunkBytes)
                    {
                        var payload = ReadBytes(size, MaxChunkBytes);
                        SkipPadding(size);
                        if (AviSourceStream.HasParameterSets(payload)
                            && AviSourceStream.TryCreateVideo(candidate.Index, candidate.Handler, candidate.Format, candidate.Rate, candidate.Scale, payload) is { } stream)
                        {
                            _streams[candidate.Index] = stream;
                            pending.Remove(id);
                        }

                        continue;
                    }

                    Skip(size + (size & 1));
                }

                _stream.Position = start;
            }

            SkippedTrackCount += pending.Count;
        }

        private static AviSourceStream? TryCreateAudio(int index, ReadOnlySpan<byte> format, int rate, uint scale, uint sampleSize)
        {
            if (format.Length < 16)
            {
                return null;
            }

            var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
            var sampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(format[4..]), int.MaxValue);
            var extra = format.Length >= 18 ? format[18..Math.Min(format.Length, 18 + BinaryPrimitives.ReadUInt16LittleEndian(format[16..]))] : [];
            return tag switch
            {
                WaveFormatMp3 => AviSourceStream.CreateAudio(index, MediaCodec.Mp3, ReadOnlyMemory<byte>.Empty, rate, scale, sampleSize, sampleRate, channels),
                WaveFormatAac when extra.Length >= 2 => AviSourceStream.CreateAudio(index, MediaCodec.Aac, extra.ToArray(), rate, scale, sampleSize, sampleRate, channels),
                _ => null
            };
        }

        private IEnumerable<MediaPacket> ReadMedia()
        {
            while (TryReadChunkHeader(out var id, out var size))
            {
                if (id is "LIST" or "RIFF")
                {
                    if (size < 4)
                    {
                        throw new InvalidDataException("AVI: a list is shorter than its type");
                    }

                    var listType = ReadFourCc();
                    if (id == "RIFF" || listType is "movi" or "rec ")
                    {
                        continue;
                    }

                    Skip(size - 4);
                    continue;
                }

                if (id.Length == 4 && char.IsAsciiDigit(id[0]) && char.IsAsciiDigit(id[1]) && id[2..] is "dc" or "db" or "wb"
                    && _streams.TryGetValue(((id[0] - '0') * 10) + (id[1] - '0'), out var stream))
                {
                    var payload = ReadBytes(size, MaxChunkBytes);
                    SkipPadding(size);
                    if (stream.TryCreatePacket(payload) is { } packet)
                    {
                        yield return packet;
                    }

                    continue;
                }

                Skip(size + (size & 1));
            }
        }

        private bool TryReadChunkHeader(out string id, out long size)
        {
            Span<byte> header = stackalloc byte[8];
            var first = _stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (first == 0)
            {
                id = "";
                size = 0;
                return false;
            }

            if (first < header.Length)
            {
                throw new InvalidDataException("AVI: a chunk header is cut short");
            }

            id = FourCc(header, 0);
            size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            return true;
        }

        private string ReadFourCc()
        {
            Span<byte> bytes = stackalloc byte[4];
            ReadExactly(bytes);
            return FourCc(bytes, 0);
        }

        private byte[] ReadBytes(long size, int limit)
        {
            if (size > limit)
            {
                throw new InvalidDataException($"AVI: a chunk of {size} bytes is larger than allowed");
            }

            var bytes = new byte[size];
            ReadExactly(bytes);
            return bytes;
        }

        private void SkipPadding(long size)
        {
            if ((size & 1) == 1)
            {
                Skip(1);
            }
        }

        private void Skip(long size)
        {
            if (size <= 0)
            {
                return;
            }

            if (_stream.CanSeek)
            {
                if (_stream.Position + size > _stream.Length)
                {
                    throw new InvalidDataException("AVI: a chunk runs past the end of the file");
                }

                _stream.Seek(size, SeekOrigin.Current);
                return;
            }

            var buffer = new byte[(int)Math.Min(size, 81920)];
            while (size > 0)
            {
                var chunk = (int)Math.Min(size, buffer.Length);
                ReadExactly(buffer.AsSpan(0, chunk));
                size -= chunk;
            }
        }

        private void ReadExactly(Span<byte> buffer)
        {
            if (_stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            {
                throw new InvalidDataException("AVI: the file ends inside a chunk");
            }

        }

        private static (uint Scale, uint Rate) Reduce(uint scale, uint rate)
        {
            var a = scale;
            var b = rate;
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return (scale / a, rate / a);
        }

        private static string FourCc(ReadOnlySpan<byte> bytes, int offset) =>
            string.Create(4, bytes.Slice(offset, 4).ToArray(), (chars, source) =>
            {
                for (var i = 0; i < 4; i++)
                {
                    chars[i] = (char)source[i];
                }
            });

        private static int ReadSize(byte[] list, int offset)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(offset));
            if (size > list.Length - offset - 4)
            {
                throw new InvalidDataException("AVI: a header chunk runs past its list");
            }

            return (int)size;
        }

        private static ReadOnlyMemory<byte> Slice(byte[] list, int offset, int size) => list.AsMemory(offset, size);
    }
}
