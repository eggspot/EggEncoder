using System.Globalization;
using System.Runtime.ExceptionServices;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;
using EggEncoder.Streaming.MpegTs;

namespace EggEncoder.Streaming.Hls
{
    public sealed partial class HlsSegmenter
    {
        private const int DefaultVideoFrameRate = 30;
        private const int DefaultAacFrameSamples = 1024;

        private const string VersionedInitSegmentFileNameFormat = "init_{0}.mp4";

        private readonly long _targetDurationTicks;
        private MediaTrackSet? _tracks;
        private MediaTrackInfo? _cutTrack;
        private long _defaultCutTrackFrameDuration;
        private MediaTrackSet? _pendingTracks;
        private byte[]? _pendingTracksInitSegment;
        private byte[]? _initSegmentToWrite;
        private readonly List<MediaPacket> _pendingPackets = [];
        private readonly Dictionary<int, long> _lastDtsByTrack = [];

        private long? _segmentStartPts;
        private long _pendingMaxCutTrackPts;
        private long? _lastCutTrackFrameDuration;

        /// <summary>
        /// VOD mode for a packet stream: every segment stays in the playlist until
        /// <see cref="Complete"/> writes <c>EXT-X-ENDLIST</c>. See <see cref="AddPacket"/> for how
        /// segments are cut.
        /// </summary>
        /// <param name="tracks">H.264, H.265 and AAC tracks, as <see cref="FmpInitSegmentWriter.BuildInitSegment(MediaTrackSet)"/> (fMP4) or <see cref="MpegTsMuxer(MediaTrackSet)"/> (MPEG-TS) accept them.</param>
        /// <param name="targetDurationSeconds">The duration a segment reaches before the next cut point ends it.</param>
        /// <param name="sink">Receives the init segment (fMP4 only, written here), the media segments and the playlist.</param>
        /// <param name="segmentFormat">The container of every media segment.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive.</exception>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> is empty or has a track the chosen container rejects.</exception>
        /// <exception cref="NotSupportedException">A track's codec (or, for MPEG-TS, its AAC configuration) can't be carried in the chosen container.</exception>
        /// <exception cref="InvalidOperationException">The sink returned no URI for the fMP4 init segment.</exception>
        public HlsSegmenter(MediaTrackSet tracks, int targetDurationSeconds, IStreamingSink sink, SegmentFormat segmentFormat = SegmentFormat.Fmp4)
            : this(tracks, targetDurationSeconds, sink, liveWindowSize: null, segmentFormat)
        {
        }

        /// <summary>
        /// Live mode for a packet stream: the same sliding window as the live audio-frame
        /// constructor, with segments cut as <see cref="AddPacket"/> describes.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive, or <paramref name="liveWindowSize"/> is less than 1.</exception>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> is empty or has a track the chosen container rejects.</exception>
        /// <exception cref="NotSupportedException">A track's codec (or, for MPEG-TS, its AAC configuration) can't be carried in the chosen container.</exception>
        /// <exception cref="InvalidOperationException">The sink returned no URI for the fMP4 init segment.</exception>
        public HlsSegmenter(MediaTrackSet tracks, int targetDurationSeconds, IStreamingSink sink, int liveWindowSize, SegmentFormat segmentFormat = SegmentFormat.Fmp4)
            : this(tracks, targetDurationSeconds, sink, (int?)liveWindowSize, segmentFormat)
        {
        }

        private HlsSegmenter(MediaTrackSet tracks, int targetDurationSeconds, IStreamingSink sink, int? liveWindowSize, SegmentFormat segmentFormat)
            : this(targetDurationSeconds, sink, liveWindowSize, segmentFormat)
        {
            ArgumentNullException.ThrowIfNull(tracks);

            byte[]? initSegment = null;
            if (segmentFormat == SegmentFormat.Fmp4)
            {
                _fmp4Writer = new FmpMediaSegmentWriter(tracks);
                initSegment = FmpInitSegmentWriter.BuildInitSegment(tracks);
            }
            else
            {
                _mpegTsMuxer = new MpegTsMuxer(tracks);
            }

            _tracks = tracks;
            _cutTrack = tracks.Tracks.FirstOrDefault(IsVideo) ?? tracks.Tracks[0];
            _targetDurationTicks = (long)targetDurationSeconds * _cutTrack.Timescale;
            _defaultCutTrackFrameDuration = DefaultFrameDuration(_cutTrack);

            if (initSegment is not null)
            {
                WriteInitSegment(InitSegmentUri, initSegment);
            }
        }

        /// <summary>
        /// Packets dropped because they arrived before the first cut point, of the stream or after
        /// a configuration change (see <see cref="UpdateTracks"/>): with a video track, everything
        /// before its first keyframe (audio included), so the segment starts decodable; without
        /// one, packets of other tracks that arrive before the first track's next packet.
        /// </summary>
        public long DroppedLeadingPacketCount { get; private set; }

        /// <summary>
        /// Adds one packet to the pending segment. Cut points are the keyframes of the first video
        /// track (every packet of the first audio track when there is no video): a cut point whose
        /// PTS is at least <c>targetDurationSeconds</c> past the pending segment's first cut point
        /// ends that segment, and starts the next one. Packets go into segments in the order they
        /// are added, so every segment starts at a cut point and its duration is the cut-point PTS
        /// difference, at least the target and at most one GOP (or audio frame) past it. Packets
        /// before the first cut point are dropped (see <see cref="DroppedLeadingPacketCount"/>).
        /// Only the first video track decides cut points, so a second video track's segments start
        /// wherever its packets fall. In live mode, keep the target at least as long as the
        /// stream's GOP: a longer segment raises <c>EXT-X-TARGETDURATION</c>, which RFC 8216
        /// §6.2.1 doesn't allow a live playlist to change. A packet must carry its track's
        /// <see cref="MediaTrackInfo.ConfigVersion"/>; after <see cref="UpdateTracks"/>, the new one.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The packet names a track not in the set, has a <see cref="MediaPacket.ConfigVersion"/>
        /// other than its track's, a negative DTS, a PTS before its DTS, or a DTS not greater than
        /// its track's previous one; nothing is added. Or the container rejects a pending packet
        /// when this packet's cut point ends the segment (a malformed payload, a timestamp it can't
        /// hold): that segment is discarded, no segment is written for it, and this packet still
        /// starts the next one. The timeline then has a gap the playlist doesn't mark.
        /// </exception>
        /// <exception cref="Exception">
        /// Whatever <see cref="IStreamingSink"/> throws while this packet's cut point writes the
        /// segment or the playlist (an <see cref="IOException"/>, say), after the same recovery:
        /// this packet starts the next segment, and a segment the sink failed to write is dropped
        /// without using up a sequence number.
        /// </exception>
        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called, or this segmenter was built for audio frames; or, at a cut, the sink returned no URI for the segment it wrote (the segment is left out of the playlist and its sequence number is reused).</exception>
        public void AddPacket(MediaPacket packet)
        {
            if (_tracks is null || _cutTrack is null)
            {
                throw new InvalidOperationException("This segmenter was built for encoded audio frames; construct it with a MediaTrackSet to add packets.");
            }

            if (_completed)
            {
                throw new InvalidOperationException("Cannot add packets after Complete() has been called.");
            }

            var track = (_pendingTracks ?? _tracks).FindTrack(packet.TrackId)
                ?? throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this segmenter's track set.", nameof(packet));
            var previousDts = ValidateTimestamps(track, packet);
            var cutFailure = _pendingTracks is not null ? ApplyPendingTrackUpdate() : null;
            _lastDtsByTrack[track.TrackId] = packet.Dts;

            var isCutTrack = track.TrackId == _cutTrack!.TrackId;
            var isCutPoint = isCutTrack && (packet.IsKeyframe || !IsVideo(track));
            if (_segmentStartPts is not { } segmentStartPts)
            {
                if (!isCutPoint)
                {
                    DroppedLeadingPacketCount++;
                    return;
                }

                StartPacketSegment(packet);
            }
            else if (isCutPoint && packet.Pts - segmentStartPts >= _targetDurationTicks)
            {
                cutFailure = CutPacketSegment(packet.Pts, packet.Dts, writePlaylist: true);
                StartPacketSegment(packet);
            }

            if (isCutTrack)
            {
                if (previousDts is { } previous)
                {
                    _lastCutTrackFrameDuration = packet.Dts - previous;
                }

                _pendingMaxCutTrackPts = Math.Max(_pendingMaxCutTrackPts, packet.Pts);
            }

            _pendingPackets.Add(packet);

            if (cutFailure is not null)
            {
                ExceptionDispatchInfo.Throw(cutFailure);
            }
        }

        /// <summary>
        /// Switches the stream to a new configuration of the same tracks: a new SPS (a resolution
        /// or level change), a new AudioSpecificConfig. The new set must hold the same track ids in
        /// the same order, each with its kind, codec and timescale unchanged (restart the rendition
        /// for anything else); a track whose <see cref="MediaTrackInfo.ConfigVersion"/> is unchanged
        /// must keep its codec configuration bytes. A set whose versions all match the current ones
        /// changes nothing. Nothing is written here: the next <see cref="AddPacket"/> ends the
        /// pending segment (built with the old configuration, its duration measured as
        /// <see cref="Complete"/> measures the final one), then waits for the next cut point as at
        /// the start of the stream, dropping packets before it (see
        /// <see cref="DroppedLeadingPacketCount"/>). The segment that starts there is preceded by
        /// <c>EXT-X-DISCONTINUITY</c> (unless it is the stream's first) and, for fMP4, by a new
        /// init segment, written just before it as <c>init_2.mp4</c>, <c>init_3.mp4</c> and so on
        /// and listed with <c>EXT-X-MAP</c>. Each track's DTS must keep increasing across the change.
        /// In live mode, an init segment no listed segment needs any more is deleted through
        /// <see cref="IStreamingSink.DeleteInitSegment"/>, and <c>EXT-X-DISCONTINUITY-SEQUENCE</c>
        /// counts the discontinuities that left the window. An update not yet applied when
        /// <see cref="Complete"/> is called is dropped. The master playlist's <c>CODECS</c> isn't
        /// rewritten here.
        /// </summary>
        /// <param name="tracks">The tracks' new configuration.</param>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> changes the track layout, or new codec configuration bytes come without a new version, or the chosen container rejects a track (as the constructors describe); the update is ignored.</exception>
        /// <exception cref="NotSupportedException">For MPEG-TS, an AAC track's new configuration can't be carried as ADTS; the update is ignored.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called, or this segmenter was built for audio frames.</exception>
        public void UpdateTracks(MediaTrackSet tracks)
        {
            var latest = _pendingTracks ?? _tracks
                ?? throw new InvalidOperationException("This segmenter was built for encoded audio frames; construct it with a MediaTrackSet to update tracks.");
            if (_completed)
            {
                throw new InvalidOperationException("Cannot update tracks after Complete() has been called.");
            }

            latest.RequireSameTrackLayout(tracks, nameof(tracks));
            if (!HasNewConfigVersion(latest, tracks))
            {
                return;
            }

            byte[]? initSegment = null;
            if (_segmentFormat == SegmentFormat.Fmp4)
            {
                initSegment = FmpInitSegmentWriter.BuildInitSegment(tracks);
            }
            else
            {
                _ = new MpegTsMuxer(tracks);
            }

            _pendingTracks = tracks;
            _pendingTracksInitSegment = initSegment;
        }

        private static bool HasNewConfigVersion(MediaTrackSet current, MediaTrackSet updated)
        {
            for (var i = 0; i < current.Tracks.Count; i++)
            {
                if (updated.Tracks[i].ConfigVersion != current.Tracks[i].ConfigVersion)
                {
                    return true;
                }
            }

            return false;
        }

        private Exception? ApplyPendingTrackUpdate()
        {
            var failure = _pendingPackets.Count > 0 ? CutPacketSegmentAfterLastFrame(writePlaylist: true) : null;
            _segmentStartPts = null;

            var tracks = _pendingTracks!;
            if (_segmentFormat == SegmentFormat.Fmp4)
            {
                _fmp4Writer!.UpdateTracks(tracks);
                _initSegmentToWrite = _pendingTracksInitSegment;
            }
            else
            {
                _mpegTsMuxer!.UpdateTracks(tracks);
            }

            _tracks = tracks;
            _cutTrack = tracks.FindTrack(_cutTrack!.TrackId)!;
            _defaultCutTrackFrameDuration = DefaultFrameDuration(_cutTrack);
            _nextSegmentIsDiscontinuity = _nextSequenceNumber > 1;
            _pendingTracks = null;
            _pendingTracksInitSegment = null;
            return failure;
        }

        private long? ValidateTimestamps(MediaTrackInfo track, MediaPacket packet)
        {
            if (packet.ConfigVersion != track.ConfigVersion)
            {
                throw new ArgumentException($"Packet for track {track.TrackId} has config version {packet.ConfigVersion}; the track's is {track.ConfigVersion}. Call UpdateTracks before adding packets of a new configuration.", nameof(packet));
            }

            if (packet.Dts < 0)
            {
                throw new ArgumentException($"Packet for track {track.TrackId} has a negative DTS ({packet.Dts}).", nameof(packet));
            }

            if (packet.Pts < packet.Dts)
            {
                throw new ArgumentException($"Packet for track {track.TrackId} has a PTS ({packet.Pts}) before its DTS ({packet.Dts}).", nameof(packet));
            }

            if (!_lastDtsByTrack.TryGetValue(track.TrackId, out var lastDts))
            {
                return null;
            }

            return packet.Dts > lastDts
                ? lastDts
                : throw new ArgumentException($"Packet for track {track.TrackId} has DTS {packet.Dts}, not greater than the track's previous {lastDts}.", nameof(packet));
        }

        private void StartPacketSegment(MediaPacket cutPoint)
        {
            _segmentStartPts = cutPoint.Pts;
            _pendingMaxCutTrackPts = cutPoint.Pts;
        }

        private void CutFinalPacketSegment()
        {
            if (_pendingPackets.Count == 0)
            {
                return;
            }

            var failure = CutPacketSegmentAfterLastFrame(writePlaylist: false);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        }

        private Exception? CutPacketSegmentAfterLastFrame(bool writePlaylist)
        {
            var lastFrameDuration = _lastCutTrackFrameDuration ?? _defaultCutTrackFrameDuration;
            var lastCutTrackDts = _lastDtsByTrack[_cutTrack!.TrackId];
            return CutPacketSegment(_pendingMaxCutTrackPts + lastFrameDuration, lastCutTrackDts + lastFrameDuration, writePlaylist);
        }

        private Exception? CutPacketSegment(long endPts, long nextCutTrackDts, bool writePlaylist)
        {
            var durationSeconds = (endPts - _segmentStartPts!.Value) / (double)_cutTrack!.Timescale;
            try
            {
                if (_initSegmentToWrite is { } initSegment)
                {
                    WriteInitSegment(string.Format(CultureInfo.InvariantCulture, VersionedInitSegmentFileNameFormat, _initSegmentCount + 1), initSegment);
                    _initSegmentToWrite = null;
                }

                var segmentBytes = _segmentFormat == SegmentFormat.Fmp4
                    ? _fmp4Writer!.BuildMediaSegment(_pendingPackets, new Dictionary<int, long> { [_cutTrack.TrackId] = nextCutTrackDts })
                    : _mpegTsMuxer!.BuildSegment(_pendingPackets);
                WriteSegment(segmentBytes, durationSeconds);
                if (writePlaylist)
                {
                    WritePlaylist(endList: false);
                }

                return null;
            }
            catch (Exception e)
            {
                return e;
            }
            finally
            {
                _pendingPackets.Clear();
            }
        }

        private static long DefaultFrameDuration(MediaTrackInfo track)
        {
            var duration = IsVideo(track)
                ? MediaTimestamp.Rescale(1, DefaultVideoFrameRate, track.Timescale)
                : MediaTimestamp.Rescale(DefaultAacFrameSamples, AacSampleRate(track), track.Timescale);
            return Math.Max(1, duration);
        }

        private static bool IsVideo(MediaTrackInfo track) => track.Codec is MediaCodec.H264 or MediaCodec.H265;

        private static int AacSampleRate(MediaTrackInfo track) =>
            track.SampleRate > 0 ? track.SampleRate : AudioSpecificConfigReader.Read(track.CodecConfiguration.Span).SampleRate;
    }
}
