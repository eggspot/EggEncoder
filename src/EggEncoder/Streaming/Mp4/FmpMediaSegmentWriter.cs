using EggEncoder.Codecs.Mov;
using EggEncoder.Media;

namespace EggEncoder.Streaming.Mp4
{
    /// <summary>
    /// Builds one fragmented MP4 (fMP4/CMAF) media segment (<c>moof</c>+<c>mdat</c>) at a time --
    /// the counterpart to <see cref="FmpInitSegmentWriter"/>'s own once-per-rendition init segment.
    /// Two modes, chosen by constructor: a single mono AAC-LC track fed
    /// <see cref="Mp4EncodedFrame"/>s from this project's own encoders (docs/streaming-plan.md item
    /// 2), and a <see cref="MediaTrackSet"/> of H.264, H.265 and AAC tracks fed
    /// <see cref="MediaPacket"/>s (docs/live-ingest-restream-plan.md item 5). Stateful across calls:
    /// <c>sequence_number</c> and each track's decode time carry forward from one call to the next,
    /// which is what makes segment N+1 play seamlessly after segment N with no gap or overlap --
    /// build a fresh instance per rendition, don't share one across renditions. Field values
    /// confirmed against a real ffmpeg-produced CMAF fMP4 fixture's own bytes.
    /// </summary>
    public sealed class FmpMediaSegmentWriter
    {
        private const int FrameModeTrackId = 1;
        private const uint DefaultBaseIsMoof = 0x020000;
        private const uint TfdtVersion1 = 0x01000000;
        private const uint TrunVersion1 = 0x01000000;
        private const uint TrunDataOffsetPresent = 0x000001;
        private const uint TrunSampleDurationPresent = 0x000100;
        private const uint TrunSampleSizePresent = 0x000200;
        private const uint TrunSampleFlagsPresent = 0x000400;
        private const uint TrunSampleCompositionTimeOffsetPresent = 0x000800;

        // ISO/IEC 14496-12 8.8.3.1 sample flags, as ffmpeg writes them: a sync sample depends on no
        // other (sample_depends_on = 2); any other video sample depends on others (1) and sets
        // sample_is_non_sync_sample.
        private const uint SyncSampleFlags = 0x02000000;
        private const uint NonSyncSampleFlags = 0x01010000;

        // Used for the last sample of a track only when nothing better is known (see
        // BuildMediaSegment): AAC-LC's frame length, and a 30 fps video frame.
        private const int DefaultAacFrameSamples = 1024;
        private const int DefaultVideoFrameRate = 30;

        private readonly PacketTrack[]? _packetTracks;
        private readonly Dictionary<int, PacketTrack>? _packetTracksById;

        private uint _nextSequenceNumber = 1;
        private long _cumulativeSampleCount;

        /// <summary>Frame mode: one mono AAC-LC track, fed by <see cref="BuildMediaSegment(IReadOnlyList{Mp4EncodedFrame})"/>.</summary>
        public FmpMediaSegmentWriter()
        {
        }

        /// <summary>
        /// Packet mode: the tracks of <paramref name="tracks"/>, fed by
        /// <see cref="BuildMediaSegment(IReadOnlyList{MediaPacket}, IReadOnlyDictionary{int, long})"/>.
        /// Pair it with <see cref="FmpInitSegmentWriter.BuildInitSegment(MediaTrackSet)"/> for the
        /// same set: both number the MP4 tracks by position in the set, plus one.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> is empty, or a track is rejected the way <see cref="FmpInitSegmentWriter.BuildInitSegment(MediaTrackSet)"/> rejects it.</exception>
        /// <exception cref="NotSupportedException">A track's codec is not H.264, H.265 or AAC.</exception>
        public FmpMediaSegmentWriter(MediaTrackSet tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("A packet-mode writer needs at least one track.", nameof(tracks));
            }

            _packetTracks = new PacketTrack[tracks.Tracks.Count];
            _packetTracksById = [];
            for (var i = 0; i < _packetTracks.Length; i++)
            {
                var info = tracks.Tracks[i];
                var initTrack = FmpInitSegmentWriter.ToInitTrack(info, i + 1);
                var track = new PacketTrack(info, initTrack.Mp4TrackId, initTrack.Kind == MediaKind.Video, DefaultSampleDuration(initTrack));
                _packetTracks[i] = track;
                _packetTracksById.Add(info.TrackId, track);
            }
        }

        /// <exception cref="ArgumentException"><paramref name="frames"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">This writer was built for packets (the <see cref="MediaTrackSet"/> constructor).</exception>
        public byte[] BuildMediaSegment(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            ArgumentNullException.ThrowIfNull(frames);
            if (_packetTracks is not null)
            {
                throw new InvalidOperationException("This writer was built for a MediaTrackSet; pass MediaPackets instead of frames.");
            }

            if (frames.Count == 0)
            {
                throw new ArgumentException("A media segment must contain at least one frame.", nameof(frames));
            }

            var samples = new SegmentSample[frames.Count];
            for (var i = 0; i < frames.Count; i++)
            {
                samples[i] = new SegmentSample(frames[i].Payload, (uint)frames[i].SampleCount, 0, 0);
            }

            var segment = BuildSegment([new TrackFragment(FrameModeTrackId, (ulong)_cumulativeSampleCount, IsVideo: false, samples)]);

            _cumulativeSampleCount += SumSampleCounts(frames);

            return segment;
        }

        /// <summary>
        /// Builds one media segment holding <paramref name="packets"/>: one <c>traf</c> per track
        /// that has packets in it (in the track set's order), each with its samples in the order
        /// given, and an <c>mdat</c> holding every payload, track by track. Each <c>tfdt</c> is
        /// that track's first DTS in the segment, so timestamps are written as given, never rebased.
        /// A sample's duration is the next DTS of its track minus its own. The last sample of a
        /// track in the segment takes, in order: <paramref name="nextDecodeTimes"/>'s entry for its
        /// track id; the previous sample's duration in this segment; that track's last duration
        /// from the previous segment; or a default (1024 samples for AAC, 1/30 s for video). The
        /// next segment's <c>tfdt</c> still comes from its real first DTS, so a guessed last
        /// duration never accumulates drift. Video samples carry sync/non-sync flags and, when any
        /// PTS differs from its DTS, signed composition offsets (version 1 <c>trun</c>); no edit
        /// list is written, so a stream with B-frames presents its first video frame at its PTS.
        /// Validation happens before anything is written: a call that throws leaves the writer
        /// unchanged.
        /// </summary>
        /// <param name="packets">At least one packet, of tracks in this writer's set, in decode order within each track.</param>
        /// <param name="nextDecodeTimes">Optional: per track id, the DTS of that track's first packet after this segment, when the caller already knows it (a segmenter cutting at the next keyframe knows the video one). Entries for tracks with no packet in this segment are ignored.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="packets"/> is empty; or a packet names a track not in the set, or has a
        /// <see cref="MediaPacket.ConfigVersion"/> other than its track's, a negative DTS, a DTS not
        /// strictly greater than its track's previous one (in this segment or an earlier one), a
        /// PTS - DTS outside the 32-bit range, or a duration past 32 bits; or a
        /// <paramref name="nextDecodeTimes"/> entry isn't greater than its track's last DTS.
        /// </exception>
        /// <exception cref="InvalidOperationException">This writer was built for frames (the parameterless constructor).</exception>
        public byte[] BuildMediaSegment(IReadOnlyList<MediaPacket> packets, IReadOnlyDictionary<int, long>? nextDecodeTimes = null)
        {
            ArgumentNullException.ThrowIfNull(packets);
            if (_packetTracks is null || _packetTracksById is null)
            {
                throw new InvalidOperationException("This writer was built for Mp4EncodedFrames; construct it with a MediaTrackSet to pass MediaPackets.");
            }

            if (packets.Count == 0)
            {
                throw new ArgumentException("A media segment must contain at least one packet.", nameof(packets));
            }

            var packetsByTrack = GroupByTrack(packets);

            var fragments = new List<TrackFragment>();
            var updates = new List<(PacketTrack Track, long LastDts, uint LastDuration)>();
            foreach (var track in _packetTracks)
            {
                if (!packetsByTrack.TryGetValue(track, out var trackPackets))
                {
                    continue;
                }

                long? nextDts = nextDecodeTimes is not null && nextDecodeTimes.TryGetValue(track.Info.TrackId, out var hint) ? hint : null;
                var samples = BuildSamples(track, trackPackets, nextDts);
                fragments.Add(new TrackFragment(track.Mp4TrackId, (ulong)trackPackets[0].Dts, track.IsVideo, samples));
                updates.Add((track, trackPackets[^1].Dts, samples[^1].Duration));
            }

            var segment = BuildSegment(fragments);

            foreach (var (track, lastDts, lastDuration) in updates)
            {
                track.LastDts = lastDts;
                track.LastDuration = lastDuration;
            }

            return segment;
        }

        private Dictionary<PacketTrack, List<MediaPacket>> GroupByTrack(IReadOnlyList<MediaPacket> packets)
        {
            var byTrack = new Dictionary<PacketTrack, List<MediaPacket>>();
            foreach (var packet in packets)
            {
                if (!_packetTracksById!.TryGetValue(packet.TrackId, out var track))
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this writer's track set.", nameof(packets));
                }

                if (packet.ConfigVersion != track.Info.ConfigVersion)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has config version {packet.ConfigVersion}; the track's is {track.Info.ConfigVersion}.", nameof(packets));
                }

                if (packet.Dts < 0)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a negative DTS ({packet.Dts}); fMP4 decode times are unsigned.", nameof(packets));
                }

                if (packet.CompositionOffset is < int.MinValue or > int.MaxValue)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a PTS - DTS ({packet.CompositionOffset}) outside the 32-bit range.", nameof(packets));
                }

                if (!byTrack.TryGetValue(track, out var list))
                {
                    list = [];
                    byTrack.Add(track, list);
                }

                var previousDts = list.Count > 0 ? list[^1].Dts : track.LastDts;
                if (previousDts is not null && packet.Dts <= previousDts)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, not greater than the track's previous {previousDts}.", nameof(packets));
                }

                list.Add(packet);
            }

            return byTrack;
        }

        private static SegmentSample[] BuildSamples(PacketTrack track, List<MediaPacket> packets, long? nextDts)
        {
            var samples = new SegmentSample[packets.Count];
            for (var i = 0; i < packets.Count; i++)
            {
                var packet = packets[i];
                uint duration;
                if (i + 1 < packets.Count)
                {
                    duration = ToDuration(track, packets[i + 1].Dts - packet.Dts, "packets");
                }
                else if (nextDts is not null)
                {
                    if (nextDts <= packet.Dts)
                    {
                        throw new ArgumentException($"Next decode time {nextDts} for track {track.Info.TrackId} isn't greater than its last DTS {packet.Dts}.", "nextDecodeTimes");
                    }

                    duration = ToDuration(track, nextDts.Value - packet.Dts, "nextDecodeTimes");
                }
                else if (i > 0)
                {
                    duration = samples[i - 1].Duration;
                }
                else
                {
                    duration = track.LastDuration ?? track.DefaultDuration;
                }

                var flags = packet.IsKeyframe ? SyncSampleFlags : NonSyncSampleFlags;
                samples[i] = new SegmentSample(packet.Payload, duration, flags, (int)packet.CompositionOffset);
            }

            return samples;
        }

        private static uint ToDuration(PacketTrack track, long delta, string parameterName) =>
            delta <= uint.MaxValue
                ? (uint)delta
                : throw new ArgumentException($"Track {track.Info.TrackId} has a sample duration ({delta}) past 32 bits.", parameterName);

        private static uint DefaultSampleDuration(FmpInitSegmentWriter.InitTrack track)
        {
            var duration = track.Kind == MediaKind.Audio
                ? MediaTimestamp.Rescale(DefaultAacFrameSamples, track.SampleRate, track.Timescale)
                : MediaTimestamp.Rescale(1, DefaultVideoFrameRate, track.Timescale);

            // A coarse timescale (say 1 tick per second) would round a frame down to 0.
            return (uint)Math.Clamp(duration, 1, uint.MaxValue);
        }

        private byte[] BuildSegment(IReadOnlyList<TrackFragment> fragments)
        {
            var moof = new Mp4BoxBuilder();

            var moofSizeFieldOffset = moof.Length;
            moof.WriteUInt32(0); // moof size -- patched once the real total is known
            moof.WriteFourCc("moof");

            moof.WriteBox("mfhd", BuildMfhdContent());

            var dataOffsetFieldOffsets = new int[fragments.Count];
            for (var f = 0; f < fragments.Count; f++)
            {
                var fragment = fragments[f];

                var trafSizeFieldOffset = moof.Length;
                moof.WriteUInt32(0); // traf size -- patched below
                moof.WriteFourCc("traf");
                moof.WriteBox("tfhd", BuildTfhdContent(fragment.Mp4TrackId));
                moof.WriteBox("tfdt", BuildTfdtContent(fragment.BaseMediaDecodeTime));

                var hasCompositionOffsets = fragment.IsVideo && fragment.Samples.Any(s => s.CompositionOffset != 0);
                var trunFlags = TrunDataOffsetPresent | TrunSampleDurationPresent | TrunSampleSizePresent;
                if (fragment.IsVideo)
                {
                    trunFlags |= TrunSampleFlagsPresent;
                }

                if (hasCompositionOffsets)
                {
                    // Version 1: the offsets are signed.
                    trunFlags |= TrunVersion1 | TrunSampleCompositionTimeOffsetPresent;
                }

                var trunSizeFieldOffset = moof.Length;
                moof.WriteUInt32(0); // trun size -- patched below
                moof.WriteFourCc("trun");
                moof.WriteUInt32(trunFlags);
                moof.WriteUInt32((uint)fragment.Samples.Count);
                dataOffsetFieldOffsets[f] = moof.Length;
                moof.WriteUInt32(0); // data_offset -- patched once moof's own total size is known
                foreach (var sample in fragment.Samples)
                {
                    moof.WriteUInt32(sample.Duration);
                    moof.WriteUInt32((uint)sample.Payload.Length);
                    if (fragment.IsVideo)
                    {
                        moof.WriteUInt32(sample.Flags);
                    }

                    if (hasCompositionOffsets)
                    {
                        moof.WriteInt32(sample.CompositionOffset);
                    }
                }

                moof.PatchUInt32(trunSizeFieldOffset, (uint)(moof.Length - trunSizeFieldOffset));
                moof.PatchUInt32(trafSizeFieldOffset, (uint)(moof.Length - trafSizeFieldOffset));
            }

            moof.PatchUInt32(moofSizeFieldOffset, (uint)(moof.Length - moofSizeFieldOffset));

            // default-base-is-moof (tfhd) + data-offset-present (trun): each track's first sample
            // sits at moof's own total size (the base) plus its offset; the first track's first
            // sample immediately follows mdat's own 8-byte box header, and each later track's
            // samples follow the earlier tracks' in mdat.
            var dataOffset = (long)(moof.Length - moofSizeFieldOffset) + 8;
            for (var f = 0; f < fragments.Count; f++)
            {
                moof.PatchUInt32(dataOffsetFieldOffsets[f], checked((uint)dataOffset));
                foreach (var sample in fragments[f].Samples)
                {
                    dataOffset += sample.Payload.Length;
                }
            }

            var segment = new Mp4BoxBuilder();
            segment.WriteBytes(moof.ToArray());
            segment.WriteBox("mdat", BuildMdatContent(fragments));

            _nextSequenceNumber++;

            return segment.ToArray();
        }

        private byte[] BuildMfhdContent()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(_nextSequenceNumber)
                .ToArray();
        }

        private static byte[] BuildTfhdContent(int mp4TrackId)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(DefaultBaseIsMoof) // version(0) + flags
                .WriteUInt32((uint)mp4TrackId)
                .ToArray();
        }

        private static byte[] BuildTfdtContent(ulong baseMediaDecodeTime)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(TfdtVersion1) // version(1) + flags(0)
                .WriteUInt64(baseMediaDecodeTime)
                .ToArray();
        }

        private static byte[] BuildMdatContent(IReadOnlyList<TrackFragment> fragments)
        {
            var totalLength = 0L;
            foreach (var fragment in fragments)
            {
                foreach (var sample in fragment.Samples)
                {
                    totalLength += sample.Payload.Length;
                }
            }

            var content = new byte[checked((int)totalLength)];
            var offset = 0;
            foreach (var fragment in fragments)
            {
                foreach (var sample in fragment.Samples)
                {
                    sample.Payload.Span.CopyTo(content.AsSpan(offset));
                    offset += sample.Payload.Length;
                }
            }

            return content;
        }

        private static long SumSampleCounts(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            var total = 0L;
            foreach (var frame in frames)
            {
                total += frame.SampleCount;
            }

            return total;
        }

        private readonly record struct SegmentSample(ReadOnlyMemory<byte> Payload, uint Duration, uint Flags, int CompositionOffset);

        private sealed record TrackFragment(int Mp4TrackId, ulong BaseMediaDecodeTime, bool IsVideo, IReadOnlyList<SegmentSample> Samples);

        // IsVideo follows the codec, as the init segment's sample entry does, not MediaTrackInfo.Kind.
        private sealed class PacketTrack(MediaTrackInfo info, int mp4TrackId, bool isVideo, uint defaultDuration)
        {
            public MediaTrackInfo Info { get; } = info;

            public int Mp4TrackId { get; } = mp4TrackId;

            public bool IsVideo { get; } = isVideo;

            public uint DefaultDuration { get; } = defaultDuration;

            public long? LastDts { get; set; }

            public uint? LastDuration { get; set; }
        }
    }
}
