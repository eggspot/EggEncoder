using EggEncoder.Media;

namespace EggEncoder.Containers.Mp4
{
    /// <summary>
    /// Joins MP4 files end to end without decoding (stream copy), as ffmpeg's concat demuxer with
    /// <c>-c copy</c> does: every input must have the same tracks in the same order, with the same
    /// codec, timescale, rotation and codec configuration bytes.
    /// </summary>
    public static class Mp4Joiner
    {
        /// <summary>
        /// Writes <paramref name="inputs"/> one after another into <paramref name="destination"/>.
        /// Each input starts where the previous one's longest track ended. AAC priming packets
        /// (negative PTS) of every input after the first are dropped, so the joined audio has no
        /// overlap; a track is shifted later when needed to keep its decode times increasing.
        /// </summary>
        /// <param name="inputs">Readable, seekable MP4/MOV streams; left open.</param>
        /// <param name="destination">Where the joined MP4 goes; left open.</param>
        /// <param name="scratch">Passed to <see cref="Mp4Writer"/>; <c>null</c> uses a temporary file.</param>
        /// <exception cref="ArgumentException">No inputs, or inputs whose tracks differ.</exception>
        public static void Join(IReadOnlyList<Stream> inputs, Stream destination, Stream? scratch = null)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            ArgumentNullException.ThrowIfNull(destination);
            if (inputs.Count == 0)
            {
                throw new ArgumentException("At least one input is needed.", nameof(inputs));
            }

            var sources = new List<Mp4FileSource>();
            try
            {
                foreach (var input in inputs)
                {
                    sources.Add(new Mp4FileSource(input, leaveOpen: true));
                }

                var tracks = sources[0].Tracks;
                for (var k = 1; k < sources.Count; k++)
                {
                    RequireSameTracks(tracks, sources[k].Tracks, k);
                }

                using var writer = new Mp4Writer(destination, leaveOpen: true, scratch);
                writer.Open(tracks);
                var trackCount = tracks.Tracks.Count;
                var offsets = new long[trackCount];
                var lastDts = new long?[trackCount];
                for (var k = 0; k < sources.Count; k++)
                {
                    var source = sources[k];
                    var shifts = ShiftsFor(source, offsets, lastDts, dropPriming: k > 0);
                    foreach (var packet in source.ReadPackets())
                    {
                        var t = IndexOf(tracks, packet.TrackId);
                        if (k > 0 && IsPriming(tracks.Tracks[t], packet))
                        {
                            continue;
                        }

                        var shifted = packet with { Dts = packet.Dts + shifts[t], Pts = packet.Pts + shifts[t] };
                        writer.Write(shifted);
                        lastDts[t] = shifted.Dts;
                    }

                    AdvanceOffsets(source, tracks, offsets);
                }

                writer.Complete();
            }
            finally
            {
                foreach (var source in sources)
                {
                    source.Dispose();
                }
            }
        }

        /// <summary>Joins files on disk; see <see cref="Join(IReadOnlyList{Stream}, Stream, Stream?)"/>.</summary>
        public static void Join(IReadOnlyList<string> inputPaths, string destinationPath)
        {
            ArgumentNullException.ThrowIfNull(inputPaths);
            var streams = new List<Stream>();
            try
            {
                foreach (var path in inputPaths)
                {
                    streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }

                using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Join(streams, destination);
            }
            finally
            {
                foreach (var stream in streams)
                {
                    stream.Dispose();
                }
            }
        }

        private static long[] ShiftsFor(Mp4FileSource source, long[] offsets, long?[] lastDts, bool dropPriming)
        {
            var shifts = (long[])offsets.Clone();
            for (var t = 0; t < shifts.Length; t++)
            {
                var track = source.SourceTracks[t];
                var first = FirstKeptIndex(track, dropPriming);
                if (first < 0 || lastDts[t] is not { } previous)
                {
                    continue;
                }

                var firstDts = track.DecodeTimes[first] + track.EditShift;
                shifts[t] = Math.Max(shifts[t], previous + 1 - firstDts);
            }

            return shifts;
        }

        private static int FirstKeptIndex(Mp4SourceTrack track, bool dropPriming)
        {
            for (var i = 0; i < track.Offsets.Length; i++)
            {
                if (!dropPriming || track.Info.Kind != MediaKind.Audio || track.PresentationTime(i) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void AdvanceOffsets(Mp4FileSource source, MediaTrackSet tracks, long[] offsets)
        {
            var longest = -1;
            var longestEnd = 0L;
            for (var t = 0; t < tracks.Tracks.Count; t++)
            {
                var end = source.SourceTracks[t].PresentationEnd();
                if (longest < 0 || (Int128)end * tracks.Tracks[longest].Timescale > (Int128)longestEnd * tracks.Tracks[t].Timescale)
                {
                    longest = t;
                    longestEnd = end;
                }
            }

            for (var t = 0; t < offsets.Length; t++)
            {
                offsets[t] += MediaTimestamp.Rescale(longestEnd, tracks.Tracks[longest].Timescale, tracks.Tracks[t].Timescale);
            }
        }

        private static bool IsPriming(MediaTrackInfo track, MediaPacket packet) => track.Kind == MediaKind.Audio && packet.Pts < 0;

        private static int IndexOf(MediaTrackSet tracks, int trackId)
        {
            for (var t = 0; t < tracks.Tracks.Count; t++)
            {
                if (tracks.Tracks[t].TrackId == trackId)
                {
                    return t;
                }
            }

            throw new InvalidOperationException($"Track {trackId} is not in the joined track set.");
        }

        private static void RequireSameTracks(MediaTrackSet first, MediaTrackSet other, int inputIndex)
        {
            if (first.Tracks.Count != other.Tracks.Count)
            {
                throw new ArgumentException($"Input {inputIndex} has {other.Tracks.Count} tracks; input 0 has {first.Tracks.Count}.", "inputs");
            }

            for (var t = 0; t < first.Tracks.Count; t++)
            {
                var a = first.Tracks[t];
                var b = other.Tracks[t];
                if (a.TrackId != b.TrackId || a.Kind != b.Kind || a.Codec != b.Codec || a.Timescale != b.Timescale || a.Rotation != b.Rotation
                    || !a.CodecConfiguration.Span.SequenceEqual(b.CodecConfiguration.Span))
                {
                    throw new ArgumentException($"Track {t} of input {inputIndex} differs from input 0 (id, kind, codec, timescale, rotation or codec configuration); join needs identical tracks.", "inputs");
                }
            }
        }
    }
}
