using EggEncoder.Media;

namespace EggEncoder.Containers.Mp4
{
    /// <summary>
    /// Splits an MP4 into parts without decoding (stream copy), as ffmpeg's segment muxer with
    /// <c>-c copy</c> does: each cut moves to the first video keyframe at or after the requested
    /// time, so every part starts decodable, and each part's timestamps start again from 0.
    /// </summary>
    public static class Mp4Splitter
    {
        /// <summary>
        /// Writes one part per kept cut point plus one for the start. A video packet goes to the part
        /// of the keyframe that precedes it in decode order; any other packet goes to the part whose
        /// start time is the last one at or before its PTS. Cut points at or after the end, or that
        /// land on the same keyframe as an earlier one, are dropped. Without a video track, cuts land
        /// on the first audio packet at or after the requested time.
        /// </summary>
        /// <param name="input">A readable, seekable MP4/MOV stream; left open.</param>
        /// <param name="cutPoints">Requested part boundaries, in any order.</param>
        /// <param name="openPart">Called once per part (0, 1, ...) for its destination; the stream is disposed after the part is written.</param>
        /// <returns>Where each part starts in the input, in order; the first is always zero.</returns>
        public static IReadOnlyList<TimeSpan> Split(Stream input, IReadOnlyList<TimeSpan> cutPoints, Func<int, Stream> openPart)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(cutPoints);
            ArgumentNullException.ThrowIfNull(openPart);

            using var source = new Mp4FileSource(input, leaveOpen: true);
            var tracks = source.Tracks;
            var cutTrack = PickCutTrack(source);
            var cuts = cutTrack < 0 ? new List<(long Ticks, int Index)>() : ResolveCuts(source.SourceTracks[cutTrack], cutPoints);
            var cutTimescale = cutTrack < 0 ? 1 : tracks.Tracks[cutTrack].Timescale;

            var writers = new List<Mp4Writer>();
            try
            {
                for (var p = 0; p <= cuts.Count; p++)
                {
                    var writer = new Mp4Writer(openPart(p));
                    writers.Add(writer);
                    writer.Open(tracks);
                }

                var cutSampleIndex = 0;
                var cutTrackId = cutTrack < 0 ? -1 : tracks.Tracks[cutTrack].TrackId;
                foreach (var packet in source.ReadPackets())
                {
                    var timescale = tracks.FindTrack(packet.TrackId)!.Timescale;
                    int part;
                    if (packet.TrackId == cutTrackId)
                    {
                        part = cuts.Count(c => c.Index <= cutSampleIndex);
                        cutSampleIndex++;
                    }
                    else
                    {
                        part = cuts.Count(c => (Int128)c.Ticks * timescale <= (Int128)packet.Pts * cutTimescale);
                    }

                    var shift = part == 0 ? 0 : MediaTimestamp.Rescale(cuts[part - 1].Ticks, cutTimescale, timescale);
                    writers[part].Write(packet with { Pts = packet.Pts - shift, Dts = packet.Dts - shift });
                }

                foreach (var writer in writers)
                {
                    writer.Complete();
                }
            }
            finally
            {
                foreach (var writer in writers)
                {
                    writer.Dispose();
                }
            }

            return [TimeSpan.Zero, .. cuts.Select(c => TimeSpan.FromSeconds(c.Ticks / (double)cutTimescale))];
        }

        private static int PickCutTrack(Mp4FileSource source)
        {
            var tracks = source.Tracks.Tracks;
            for (var t = 0; t < tracks.Count; t++)
            {
                if (tracks[t].Kind == MediaKind.Video)
                {
                    return t;
                }
            }

            return tracks.Count > 0 ? 0 : -1;
        }

        private static List<(long Ticks, int Index)> ResolveCuts(Mp4SourceTrack track, IReadOnlyList<TimeSpan> cutPoints)
        {
            var cuts = new SortedDictionary<int, long>();
            foreach (var cutPoint in cutPoints)
            {
                var target = (long)Math.Round(cutPoint.TotalSeconds * track.Info.Timescale);
                for (var i = 1; i < track.Offsets.Length; i++)
                {
                    if (track.IsSync(i) && track.PresentationTime(i) >= target)
                    {
                        cuts.TryAdd(i, track.PresentationTime(i));
                        break;
                    }
                }
            }

            return cuts.Select(c => (c.Value, c.Key)).ToList();
        }
    }
}
