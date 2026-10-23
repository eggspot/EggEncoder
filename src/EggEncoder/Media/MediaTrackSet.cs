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

        /// <summary>Two sets are equal when they hold equal tracks in the same order.</summary>
        public bool Equals(MediaTrackSet? other) =>
            other is not null && Tracks.SequenceEqual(other.Tracks);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var track in Tracks)
            {
                hash.Add(track);
            }

            return hash.ToHashCode();
        }

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

        internal void RequireSameTrackLayout(MediaTrackSet updated, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(updated, parameterName);
            if (updated.Tracks.Count != Tracks.Count)
            {
                throw new ArgumentException($"The updated set has {updated.Tracks.Count} tracks; the current one has {Tracks.Count}. Restart the rendition to add or remove a track.", parameterName);
            }

            for (var i = 0; i < Tracks.Count; i++)
            {
                var current = Tracks[i];
                var next = updated.Tracks[i];
                if (next.TrackId != current.TrackId)
                {
                    throw new ArgumentException($"Track {i} of the updated set has id {next.TrackId}; the current one has {current.TrackId}. Restart the rendition to reorder or renumber tracks.", parameterName);
                }

                if (next.Kind != current.Kind || next.Codec != current.Codec)
                {
                    throw new ArgumentException($"Track {current.TrackId} changed from {current.Kind} {current.Codec} to {next.Kind} {next.Codec}; restart the rendition to change a track's kind or codec.", parameterName);
                }

                if (next.Timescale != current.Timescale)
                {
                    throw new ArgumentException($"Track {current.TrackId} changed timescale from {current.Timescale} to {next.Timescale}; restart the rendition to change a timescale.", parameterName);
                }

                if (next.ConfigVersion == current.ConfigVersion && !next.CodecConfiguration.Span.SequenceEqual(current.CodecConfiguration.Span))
                {
                    throw new ArgumentException($"Track {current.TrackId} has new codec configuration bytes under the same config version {current.ConfigVersion}; a changed configuration needs a new ConfigVersion.", parameterName);
                }
            }
        }
    }
}
