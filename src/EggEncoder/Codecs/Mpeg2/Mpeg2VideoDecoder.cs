using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.Codecs.Mpeg2
{
    /// <summary>
    /// Decodes an MPEG-2 video elementary stream (ISO/IEC 13818-2, Main profile, 4:2:0, 8-bit) into
    /// <see cref="VideoFrame"/>s in display order, entirely in managed code. Feed the stream in chunks of any size; a
    /// picture is decoded once the next picture, group or sequence start code arrives, and gets the timestamp of the
    /// chunk its picture start code came in. Frame pictures are decoded, progressive or interlaced (frame and field
    /// prediction, frame and field DCT); field pictures, dual-prime prediction, scalable extensions and MPEG-1 streams are
    /// refused with <see cref="NotSupportedException"/>. Damaged data throws <see cref="InvalidDataException"/>. The
    /// inverse DCT is a double-precision reference transform, so output matches other decoders to within the IDCT
    /// accuracy the standard allows, not bit for bit. Not thread-safe: use one decoder per stream.
    /// </summary>
    public sealed class Mpeg2VideoDecoder : IDisposable
    {
        private const int MaxBufferedBytes = 64 * 1024 * 1024;
        private const int PictureStartCode = 0x00;
        private const int UserDataStartCode = 0xB2;
        private const int SequenceHeaderCode = 0xB3;
        private const int ExtensionStartCode = 0xB5;
        private const int SequenceEndCode = 0xB7;
        private const int GroupStartCode = 0xB8;

        private readonly VideoFramePool _pool;
        private readonly List<byte> _buffer = new();
        private readonly List<(long Offset, TimeSpan Timestamp)> _chunkTimes = new();
        private long _bufferStart;
        private Mpeg2Sequence? _sequence;
        private Mpeg2PictureHeader? _pictureHeader;
        private Mpeg2Picture? _current;
        private Mpeg2Picture? _olderReference;
        private Mpeg2Picture? _newerReference;
        private bool _newerReferenceOutput = true;
        private bool _disposed;

        /// <param name="pool">Where output frames are rented; <see cref="VideoFramePool.Shared"/> when null.</param>
        public Mpeg2VideoDecoder(VideoFramePool? pool = null)
        {
            _pool = pool ?? VideoFramePool.Shared;
        }

        /// <summary>Display width of the stream, or 0 before its sequence header.</summary>
        public int Width => _sequence?.Width ?? 0;

        /// <summary>Display height of the stream, or 0 before its sequence header.</summary>
        public int Height => _sequence?.Height ?? 0;

        /// <summary>The frame rate the sequence header declares, if any.</summary>
        public VideoFrameRate? FrameRate => _sequence?.FrameRate;

        /// <summary>Decodes the pictures this chunk completes. Frames come out in display order and belong to the receiver.</summary>
        /// <param name="chunk">The next bytes of the elementary stream.</param>
        /// <param name="timestamp">The presentation time of the picture whose start code is in this chunk.</param>
        /// <param name="output">Receives decoded frames.</param>
        /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The decoder was disposed.</exception>
        /// <exception cref="InvalidDataException">The data is damaged.</exception>
        /// <exception cref="NotSupportedException">The stream uses an unsupported feature.</exception>
        public void Decode(ReadOnlySpan<byte> chunk, TimeSpan timestamp, Action<VideoFrame> output)
        {
            ArgumentNullException.ThrowIfNull(output);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_buffer.Count + chunk.Length > MaxBufferedBytes)
            {
                throw new InvalidDataException("MPEG-2: more than 64 MB without a start code that completes a unit");
            }

            _chunkTimes.Add((_bufferStart + _buffer.Count, timestamp));
            _buffer.AddRange(chunk);
            ProcessUnits(output, final: false);
        }

        /// <summary>Decodes everything still buffered and outputs the frames held for reordering. Call it at the end of the stream.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
        /// <exception cref="InvalidDataException">The data is damaged.</exception>
        public void Flush(Action<VideoFrame> output)
        {
            ArgumentNullException.ThrowIfNull(output);
            ObjectDisposedException.ThrowIf(_disposed, this);
            ProcessUnits(output, final: true);
            FinishPicture(output);
            OutputHeldReference(output);
        }

        /// <inheritdoc />
        public void Dispose() => _disposed = true;

        private void ProcessUnits(Action<VideoFrame> output, bool final)
        {
            var data = _buffer.ToArray();
            var starts = FindStartCodes(data);
            var consumed = 0;
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1] : final ? data.Length : -1;
                if (end < 0)
                {
                    break;
                }

                var unitStart = starts[i];
                try
                {
                    HandleUnit(data, unitStart, end, output);
                }
                catch (EndOfStreamException error)
                {
                    throw new InvalidDataException("MPEG-2: a unit ends before its syntax does", error);
                }

                consumed = end;
            }

            if (final)
            {
                consumed = data.Length;
            }
            else if (starts.Count == 0)
            {
                consumed = Math.Max(0, data.Length - 3);
            }
            else if (consumed == 0)
            {
                consumed = starts[0];
            }

            _buffer.RemoveRange(0, consumed);
            _bufferStart += consumed;
            var current = _chunkTimes.FindLastIndex(t => t.Offset <= _bufferStart);
            if (current > 0)
            {
                _chunkTimes.RemoveRange(0, current);
            }
        }

        private static List<int> FindStartCodes(byte[] data)
        {
            var starts = new List<int>();
            for (var i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    starts.Add(i);
                    i += 3;
                }
            }

            return starts;
        }

        private void HandleUnit(byte[] data, int start, int end, Action<VideoFrame> output)
        {
            var code = data[start + 3];
            var payload = new byte[end - start - 4 + 8];
            Array.Copy(data, start + 4, payload, 0, end - start - 4);
            var reader = new BitReader(payload);
            switch (code)
            {
                case SequenceHeaderCode:
                    FinishPicture(output);
                    var sequence = Mpeg2Sequence.ParseHeader(reader);
                    if (_sequence is { } previous && (previous.Width != sequence.Width || previous.Height != sequence.Height))
                    {
                        OutputHeldReference(output);
                        _olderReference = null;
                        _newerReference = null;
                    }

                    _sequence = sequence;
                    break;
                case ExtensionStartCode:
                    HandleExtension(reader);
                    break;
                case GroupStartCode:
                    FinishPicture(output);
                    break;
                case SequenceEndCode:
                    FinishPicture(output);
                    OutputHeldReference(output);
                    break;
                case PictureStartCode:
                    FinishPicture(output);
                    StartPicture(reader, TimestampAt(_bufferStart + start));
                    break;
                case UserDataStartCode:
                    break;
                case >= 0x01 and <= 0xAF:
                    DecodeSlice(reader, code);
                    break;
            }
        }

        private TimeSpan TimestampAt(long offset)
        {
            var timestamp = TimeSpan.Zero;
            foreach (var (chunkOffset, chunkTime) in _chunkTimes)
            {
                if (chunkOffset <= offset)
                {
                    timestamp = chunkTime;
                }
            }

            return timestamp;
        }

        private void HandleExtension(BitReader reader)
        {
            var id = (int)reader.ReadBits(4);
            if (_pictureHeader is { } picture && _current is not null)
            {
                switch (id)
                {
                    case 8:
                        picture.ParseCodingExtension(reader);
                        return;
                    case 3:
                        _sequence!.ParseQuantMatrixExtension(reader);
                        return;
                    case 7 or 9 or 10:
                        throw new NotSupportedException("MPEG-2: scalable and picture display extensions with scalability are not supported");
                }

                return;
            }

            if (_sequence is null)
            {
                return;
            }

            switch (id)
            {
                case 1:
                    _sequence.ParseSequenceExtension(reader);
                    break;
                case 2:
                    _sequence.ParseDisplayExtension(reader);
                    break;
                case 3:
                    _sequence.ParseQuantMatrixExtension(reader);
                    break;
                case 5:
                    throw new NotSupportedException("MPEG-2: scalable sequences are not supported");
            }
        }

        private void StartPicture(BitReader reader, TimeSpan timestamp)
        {
            var sequence = _sequence ?? throw new InvalidDataException("MPEG-2: a picture before any sequence header");
            if (!sequence.HasExtension)
            {
                throw new NotSupportedException("MPEG-2: MPEG-1 video streams are not supported");
            }

            var header = Mpeg2PictureHeader.Parse(reader);
            if (header.Type == Mpeg2PictureType.Predicted && _newerReference is null)
            {
                throw new InvalidDataException("MPEG-2: a P picture without a reference picture");
            }

            if (header.Type == Mpeg2PictureType.Bidirectional && (_olderReference is null || _newerReference is null))
            {
                _pictureHeader = null;
                _current = null;
                return;
            }

            _pictureHeader = header;
            _current = new Mpeg2Picture(sequence.MacroblockWidth, sequence.MacroblockHeight)
            {
                Timestamp = timestamp,
                IsBidirectional = header.Type == Mpeg2PictureType.Bidirectional
            };
        }

        private void DecodeSlice(BitReader reader, int code)
        {
            if (_current is null || _pictureHeader is not { } header)
            {
                return;
            }

            if (!header.HasCodingExtension)
            {
                throw new InvalidDataException("MPEG-2: a picture has no picture coding extension");
            }

            if (header.PictureStructure != 3)
            {
                throw new NotSupportedException("MPEG-2: field pictures are not supported");
            }

            var forward = header.Type == Mpeg2PictureType.Bidirectional ? _olderReference : _newerReference;
            var backward = header.Type == Mpeg2PictureType.Bidirectional ? _newerReference : null;
            new Mpeg2SliceDecoder(_sequence!, header, _current, forward, backward).Decode(reader, code);
        }

        private void FinishPicture(Action<VideoFrame> output)
        {
            var picture = _current;
            if (picture is null)
            {
                return;
            }

            _current = null;
            _pictureHeader = null;
            if (picture.IsBidirectional)
            {
                output(ToFrame(picture));
                return;
            }

            OutputHeldReference(output);
            _olderReference = _newerReference;
            _newerReference = picture;
            _newerReferenceOutput = false;
        }

        private void OutputHeldReference(Action<VideoFrame> output)
        {
            if (_newerReference is { } reference && !_newerReferenceOutput)
            {
                _newerReferenceOutput = true;
                output(ToFrame(reference));
            }
        }

        private VideoFrame ToFrame(Mpeg2Picture picture)
        {
            var sequence = _sequence!;
            var frame = _pool.Rent(sequence.Width, sequence.Height, VideoColorRange.Limited, sequence.ColorMatrix);
            for (var y = 0; y < sequence.Height; y++)
            {
                picture.Planes[0].AsSpan(y * picture.LumaWidth, sequence.Width).CopyTo(frame.GetRow(VideoPlane.Y, y));
            }

            for (var y = 0; y < frame.ChromaHeight; y++)
            {
                picture.Planes[1].AsSpan(y * picture.ChromaWidth, frame.ChromaWidth).CopyTo(frame.GetRow(VideoPlane.U, y));
                picture.Planes[2].AsSpan(y * picture.ChromaWidth, frame.ChromaWidth).CopyTo(frame.GetRow(VideoPlane.V, y));
            }

            frame.Timestamp = picture.Timestamp;
            return frame;
        }
    }
}
