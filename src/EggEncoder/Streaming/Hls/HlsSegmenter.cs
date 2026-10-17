using System.Text;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// Cuts an fMP4/CMAF HLS stream (VOD mode only -- see docs/streaming-plan.md item 3's own
    /// status note for the live-sliding-window follow-up) from a sequence of encoded audio frames:
    /// writes the init segment once at construction, accumulates frames and cuts a media segment
    /// once their cumulative duration reaches <c>targetDurationSeconds</c> (always on a frame
    /// boundary, so a real segment's duration is always &gt;= target by at most one frame), and
    /// rewrites the media playlist after every cut. <see cref="Complete"/> flushes the final,
    /// possibly-short segment and writes <c>EXT-X-ENDLIST</c>.
    /// </summary>
    public sealed class HlsSegmenter
    {
        private readonly int _sampleRate;
        private readonly int _targetDurationSeconds;
        private readonly IStreamingSink _sink;
        private readonly FmpMediaSegmentWriter _mediaSegmentWriter = new();
        private readonly List<Mp4EncodedFrame> _pendingFrames = [];
        private readonly List<HlsSegmentDescriptor> _segments = [];

        private long _accumulatedSampleCount;
        private long _nextSequenceNumber = 1;
        private double _maxObservedSegmentDurationSeconds;
        private bool _completed;

        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive.</exception>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public HlsSegmenter(int channels, int sampleRate, int targetDurationSeconds, IStreamingSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (targetDurationSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetDurationSeconds), "Target duration must be positive.");
            }

            _sampleRate = sampleRate;
            _targetDurationSeconds = targetDurationSeconds;
            _sink = sink;

            sink.WriteInitSegment(FmpInitSegmentWriter.BuildInitSegment(channels, sampleRate));
        }

        public const string InitSegmentUri = "init.mp4";

        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
        public void AddFrame(EncodedAudioFrame frame)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot add frames after Complete() has been called.");
            }

            _pendingFrames.Add(new Mp4EncodedFrame(frame.Payload.ToArray(), frame.SampleCount));
            _accumulatedSampleCount += frame.SampleCount;

            if (_accumulatedSampleCount / (double)_sampleRate >= _targetDurationSeconds)
            {
                CutSegment();
                WritePlaylist(endList: false);
            }
        }

        /// <summary>Flushes the final, possibly-short segment (if any frames are pending) and writes <c>EXT-X-ENDLIST</c>. Safe to call more than once -- every call after the first is a no-op.</summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            if (_pendingFrames.Count > 0)
            {
                CutSegment();
            }

            WritePlaylist(endList: true);
        }

        // Builds and writes one media segment from _pendingFrames; does NOT write the playlist --
        // each of this method's two callers writes it afterward with its own correct endList value
        // (AddFrame: false; Complete: true), rather than writing it here unconditionally and having
        // Complete immediately overwrite that same write again with the real, final value.
        private void CutSegment()
        {
            var sequenceNumber = _nextSequenceNumber++;
            var durationSeconds = _accumulatedSampleCount / (double)_sampleRate;
            _maxObservedSegmentDurationSeconds = Math.Max(_maxObservedSegmentDurationSeconds, durationSeconds);

            var segmentBytes = _mediaSegmentWriter.BuildMediaSegment(_pendingFrames);
            _sink.WriteMediaSegment(sequenceNumber, segmentBytes);

            _segments.Add(new HlsSegmentDescriptor(sequenceNumber, durationSeconds, SegmentUri(sequenceNumber)));

            _pendingFrames.Clear();
            _accumulatedSampleCount = 0;
        }

        private void WritePlaylist(bool endList)
        {
            var mediaSequence = _segments.Count > 0 ? _segments[0].SequenceNumber : 1;

            // The playlist's own TARGETDURATION must never be shorter than any segment it lists
            // (RFC 8216 §4.3.3.1) -- since cutting on frame boundaries only can overshoot the
            // configured target slightly, this tracks the real maximum observed so far rather than
            // just echoing the configured value back, which a long-enough-running stream could
            // eventually violate.
            var targetDurationTagValue = (int)Math.Ceiling(Math.Max(_targetDurationSeconds, _maxObservedSegmentDurationSeconds));

            var playlistText = M3u8PlaylistWriter.BuildMediaPlaylist(targetDurationTagValue, mediaSequence, _segments, InitSegmentUri, endList);
            _sink.WritePlaylist(Encoding.UTF8.GetBytes(playlistText));
        }

        private static string SegmentUri(long sequenceNumber) => $"segment_{sequenceNumber}.m4s";
    }
}
