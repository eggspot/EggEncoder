namespace EggEncoder.Media
{
    /// <summary>
    /// Every track of one packet stream, as a source reports it once its track headers are known
    /// and as an <see cref="IMediaPacketSink"/> receives it in <see cref="IMediaPacketSink.OpenAsync"/>.
    /// Track ids are unique within a set. See docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public sealed record MediaTrackSet
    {
        public MediaTrackSet(IReadOnlyList<MediaTrackInfo> tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);

            var seen = new HashSet<int>();
            foreach (var track in tracks)
            {
                if (track is null)
                {
                    throw new ArgumentException("Tracks must not contain null", nameof(tracks));
                }

                if (!seen.Add(track.TrackId))
                {
                    throw new ArgumentException($"Duplicate track id {track.TrackId}", nameof(tracks));
                }
            }

            Tracks = tracks.ToArray();
        }

        /// <summary>The tracks, in the order given. A defensive copy: later changes to the caller's list don't show here.</summary>
        public IReadOnlyList<MediaTrackInfo> Tracks { get; }

        /// <summary>The track with this id, or <c>null</c> if the set has none.</summary>
        public MediaTrackInfo? FindTrack(int trackId)
        {
            foreach (var track in Tracks)
            {
                if (track.TrackId == trackId)
                {
                    return track;
                }
            }

            return null;
        }
    }
}
