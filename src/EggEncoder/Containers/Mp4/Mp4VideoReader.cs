using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.Jpeg;
using EggEncoder.Media;
using EggEncoder.Video;
using EggEncoder.Video.Filters;

namespace EggEncoder.Containers.Mp4
{
    /// <summary>
    /// Decodes the first H.264 track of a progressive MP4/MOV into frames in display order, each
    /// stamped with its presentation time and turned upright by the track's rotation. Not thread-safe.
    /// </summary>
    public sealed class Mp4VideoReader : IDisposable
    {
        private const int TicksPerSecond = 10_000_000;

        private readonly Mp4FileSource _source;
        private readonly MediaTrackInfo _track;
        private readonly VideoFramePool? _pool;
        private bool _disposed;

        /// <param name="source">A readable, seekable MP4/MOV stream.</param>
        /// <param name="leaveOpen">Leave <paramref name="source"/> open when the reader is disposed.</param>
        /// <param name="pool">Where decoded frames are rented; a private pool when null.</param>
        /// <exception cref="NotSupportedException">The file has no H.264 track.</exception>
        /// <exception cref="InvalidDataException">The file is malformed.</exception>
        public Mp4VideoReader(Stream source, bool leaveOpen = false, VideoFramePool? pool = null)
        {
            _source = new Mp4FileSource(source, leaveOpen);
            _pool = pool;
            var track = _source.Tracks.Tracks.FirstOrDefault(t => t.Kind == MediaKind.Video && t.Codec == MediaCodec.H264);
            if (track is null)
            {
                _source.Dispose();
                throw new NotSupportedException("The file has no H.264 video track");
            }

            _track = track;
        }

        /// <summary>Opens the file at <paramref name="path"/>.</summary>
        /// <inheritdoc cref="Mp4VideoReader(Stream, bool, VideoFramePool?)"/>
        public static Mp4VideoReader Open(string path, VideoFramePool? pool = null)
        {
            var stream = File.OpenRead(path);
            try
            {
                return new Mp4VideoReader(stream, leaveOpen: false, pool);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>The decoded picture size before rotation.</summary>
        public (int Width, int Height) CodedSize => (_track.Width, _track.Height);

        /// <summary>The size of the frames this reader returns, after rotation.</summary>
        public (int Width, int Height) DisplaySize => _track.Rotation is 90 or 270 ? (_track.Height, _track.Width) : (_track.Width, _track.Height);

        /// <summary>Clockwise rotation applied to every frame: 0, 90, 180 or 270.</summary>
        public int Rotation => _track.Rotation;

        /// <summary>The file's duration.</summary>
        public TimeSpan Duration => _source.Duration;

        /// <summary>Whether the file also has an audio track.</summary>
        public bool HasAudio => _source.Tracks.Tracks.Any(t => t.Kind == MediaKind.Audio);

        /// <summary>
        /// Decodes frames from the last keyframe at or before <paramref name="startAt"/>, dropping
        /// those shown before it. The caller owns and disposes every frame it receives.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream is corrupt.</exception>
        public IEnumerable<VideoFrame> ReadFrames(TimeSpan startAt = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadFramesCore(startAt, startAt);
        }

        internal IEnumerable<VideoFrame> ReadFrames(TimeSpan startAt, TimeSpan dropBefore)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadFramesCore(startAt, dropBefore);
        }

        /// <summary>
        /// Returns the first frame shown at or after <paramref name="position"/>, or the last frame
        /// when the position is past the end.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
        /// <exception cref="InvalidDataException">The file has no decodable frame.</exception>
        public VideoFrame GrabFrame(TimeSpan position)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
            VideoFrame? last = null;
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var frame in ReadFramesCore(position, TimeSpan.MinValue))
            {
                last?.Dispose();
                last = frame;
                if (frame.Timestamp >= position)
                {
                    break;
                }
            }

            return last ?? throw new InvalidDataException("The video track has no decodable frame");
        }

        /// <summary>Returns the frame <see cref="GrabFrame"/> picks, encoded as a JPEG file.</summary>
        /// <param name="position">Where to grab.</param>
        /// <param name="quantizerScale">ffmpeg's <c>-q:v</c>, 1 to 31.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative or the scale is outside 1 to 31.</exception>
        public byte[] GrabJpeg(TimeSpan position, int quantizerScale = JpegEncoder.DefaultQuantizerScale)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(quantizerScale, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(quantizerScale, 31);
            using var frame = GrabFrame(position);
            return JpegEncoder.Encode(frame, quantizerScale);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source.Dispose();
        }

        private IEnumerable<VideoFrame> ReadFramesCore(TimeSpan startAt, TimeSpan dropBefore)
        {
            using var decoder = new H264Decoder(_track.CodecConfiguration.Span, _pool);
            var rotate = _track.Rotation == 0 ? null : new RotateTransform(_track.Rotation);
            var ready = new Queue<VideoFrame>();
            void Collect(VideoFrame frame)
            {
                if (frame.Timestamp < dropBefore)
                {
                    frame.Dispose();
                    return;
                }

                if (rotate is null)
                {
                    ready.Enqueue(frame);
                }
                else
                {
                    rotate.Apply(frame, ready.Enqueue);
                }
            }

            try
            {
                foreach (var packet in _source.ReadPackets(startAt))
                {
                    if (packet.TrackId != _track.TrackId)
                    {
                        continue;
                    }

                    var timestamp = TimeSpan.FromTicks(MediaTimestamp.Rescale(packet.Pts, _track.Timescale, TicksPerSecond));
                    decoder.Decode(packet.Payload.Span, timestamp, Collect);
                    while (ready.Count > 0)
                    {
                        yield return ready.Dequeue();
                    }
                }

                decoder.Flush(Collect);
                while (ready.Count > 0)
                {
                    yield return ready.Dequeue();
                }
            }
            finally
            {
                while (ready.Count > 0)
                {
                    ready.Dequeue().Dispose();
                }
            }
        }
    }
}
