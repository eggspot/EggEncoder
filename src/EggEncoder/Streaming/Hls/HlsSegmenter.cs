using System.Text;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;
using EggEncoder.Streaming.MpegTs;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// Cuts an HLS stream from a sequence of encoded audio frames, in either <see cref="SegmentFormat.Fmp4"/>
    /// (the default -- writes an init segment once at construction, referenced from the playlist
    /// via <c>EXT-X-MAP</c>) or <see cref="SegmentFormat.MpegTs"/> (no init segment at all; PAT/PMT
    /// travel inside every media segment instead). Accumulates frames and cuts a media segment once
    /// their cumulative duration reaches <c>targetDurationSeconds</c> (always on a frame boundary,
    /// so a real segment's duration is always &gt;= target by at most one frame), and rewrites the
    /// media playlist after every cut. <see cref="Complete"/> flushes the final, possibly-short
    /// segment and writes <c>EXT-X-ENDLIST</c>. Two further modes, chosen by which constructor is
    /// used: VOD (every segment ever produced stays in the playlist) or live (a bounded sliding
    /// window of the most recent segments -- see the live constructor's own doc comment). The
    /// <see cref="MediaTrackSet"/> constructors build a packet-driven segmenter instead (H.264,
    /// H.265 and AAC tracks fed through <see cref="AddPacket"/>, cut at video keyframes).
    /// </summary>
    public sealed partial class HlsSegmenter
    {
        private readonly int _sampleRate;
        private readonly int _targetDurationSeconds;
        private readonly IStreamingSink _sink;
        private readonly SegmentFormat _segmentFormat;
        private readonly FmpMediaSegmentWriter? _fmp4Writer;
        private readonly MpegTsMuxer? _mpegTsMuxer;
        private readonly List<Mp4EncodedFrame> _pendingFrames = [];
        private readonly List<HlsSegmentDescriptor> _segments = [];

        private readonly int? _liveWindowSize;
        private readonly string? _initSegmentUri;

        private long _accumulatedSampleCount;
        private long _nextSequenceNumber = 1;
        private double _maxObservedSegmentDurationSeconds;
        private bool _completed;

        /// <summary>VOD mode: every segment ever produced stays in the playlist until <see cref="Complete"/> writes <c>EXT-X-ENDLIST</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public HlsSegmenter(int channels, int sampleRate, int targetDurationSeconds, IStreamingSink sink, SegmentFormat segmentFormat = SegmentFormat.Fmp4)
            : this(channels, sampleRate, targetDurationSeconds, sink, liveWindowSize: null, segmentFormat)
        {
        }

        /// <summary>
        /// Live mode: once more than <paramref name="liveWindowSize"/> segments have been produced,
        /// the oldest is dropped from the playlist (<c>EXT-X-MEDIA-SEQUENCE</c> advances to match)
        /// and <see cref="IStreamingSink.DeleteMediaSegment(long, string)"/> is called for it. No <c>EXT-X-ENDLIST</c>
        /// is written until <see cref="Complete"/> signals the live stream itself has ended.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive, or <paramref name="liveWindowSize"/> is less than 1.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public HlsSegmenter(int channels, int sampleRate, int targetDurationSeconds, IStreamingSink sink, int liveWindowSize, SegmentFormat segmentFormat = SegmentFormat.Fmp4)
            : this(channels, sampleRate, targetDurationSeconds, sink, (int?)liveWindowSize, segmentFormat)
        {
        }

        private HlsSegmenter(int channels, int sampleRate, int targetDurationSeconds, IStreamingSink sink, int? liveWindowSize, SegmentFormat segmentFormat)
            : this(targetDurationSeconds, sink, liveWindowSize, segmentFormat)
        {
            _sampleRate = sampleRate;

            if (segmentFormat == SegmentFormat.Fmp4)
            {
                _fmp4Writer = new FmpMediaSegmentWriter();
                _initSegmentUri = RequireUri(sink.WriteInitSegment(InitSegmentUri, FmpInitSegmentWriter.BuildInitSegment(channels, sampleRate)));
            }
            else
            {
                _mpegTsMuxer = new MpegTsMuxer(channels, sampleRate);
            }
        }

        private HlsSegmenter(int targetDurationSeconds, IStreamingSink sink, int? liveWindowSize, SegmentFormat segmentFormat)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (targetDurationSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetDurationSeconds), "Target duration must be positive.");
            }

            if (liveWindowSize is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(liveWindowSize), "Live window size must be at least 1.");
            }

            _targetDurationSeconds = targetDurationSeconds;
            _sink = sink;
            _liveWindowSize = liveWindowSize;
            _segmentFormat = segmentFormat;
        }

        /// <summary>The file name the init segment is written under; the playlist lists whatever URI the <see cref="IStreamingSink"/> returns for it.</summary>
        public const string InitSegmentUri = "init.mp4";

        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called, or this segmenter was built for packets (a <see cref="MediaTrackSet"/> constructor).</exception>
        public void AddFrame(EncodedAudioFrame frame)
        {
            if (_tracks is not null)
            {
                throw new InvalidOperationException("This segmenter was built for a MediaTrackSet; add MediaPackets instead of frames.");
            }

            if (_completed)
            {
                throw new InvalidOperationException("Cannot add frames after Complete() has been called.");
            }

            _pendingFrames.Add(new Mp4EncodedFrame(frame.Payload.ToArray(), frame.SampleCount));
            _accumulatedSampleCount += frame.SampleCount;

            if (_accumulatedSampleCount / (double)_sampleRate >= _targetDurationSeconds)
            {
                CutFrameSegment();
                WritePlaylist(endList: false);
            }
        }

        /// <summary>
        /// Flushes the final, possibly-short segment (if any frames or packets are pending) and
        /// writes <c>EXT-X-ENDLIST</c>. Safe to call more than once -- every call after the first is
        /// a no-op. The playlist is ended even when the final segment can't be built (see
        /// <see cref="AddPacket"/>); that exception is rethrown after it.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            try
            {
                if (_tracks is not null)
                {
                    CutFinalPacketSegment();
                }
                else if (_pendingFrames.Count > 0)
                {
                    CutFrameSegment();
                }
            }
            finally
            {
                WritePlaylist(endList: true);
            }
        }

        private void CutFrameSegment()
        {
            var segmentBytes = _segmentFormat == SegmentFormat.Fmp4
                ? _fmp4Writer!.BuildMediaSegment(_pendingFrames)
                : _mpegTsMuxer!.BuildSegment(_pendingFrames);
            WriteSegment(segmentBytes, _accumulatedSampleCount / (double)_sampleRate);

            _pendingFrames.Clear();
            _accumulatedSampleCount = 0;
        }

        private void WriteSegment(byte[] segmentBytes, double durationSeconds)
        {
            var sequenceNumber = _nextSequenceNumber;
            var uri = RequireUri(_sink.WriteMediaSegment(sequenceNumber, SegmentFileName(sequenceNumber), segmentBytes));
            _nextSequenceNumber++;
            _maxObservedSegmentDurationSeconds = Math.Max(_maxObservedSegmentDurationSeconds, durationSeconds);

            _segments.Add(new HlsSegmentDescriptor(sequenceNumber, durationSeconds, uri));

            while (_liveWindowSize is { } windowSize && _segments.Count > windowSize)
            {
                var oldest = _segments[0];
                _segments.RemoveAt(0);
                _sink.DeleteMediaSegment(oldest.SequenceNumber, oldest.Uri);
            }
        }

        private void WritePlaylist(bool endList)
        {
            var mediaSequence = _segments.Count > 0 ? _segments[0].SequenceNumber : 1;
            var targetDurationTagValue = (int)Math.Ceiling(Math.Max(_targetDurationSeconds, _maxObservedSegmentDurationSeconds));
            var playlistText = M3u8PlaylistWriter.BuildMediaPlaylist(targetDurationTagValue, mediaSequence, _segments, _initSegmentUri, endList);
            _sink.WritePlaylist(Encoding.UTF8.GetBytes(playlistText));
        }

        private static string RequireUri(string? uri) =>
            string.IsNullOrEmpty(uri)
                ? throw new InvalidOperationException("The streaming sink returned no URI for a segment it wrote.")
                : uri;

        private string SegmentFileName(long sequenceNumber) => _segmentFormat == SegmentFormat.Fmp4 ? $"segment_{sequenceNumber}.m4s" : $"segment_{sequenceNumber}.ts";
    }
}
