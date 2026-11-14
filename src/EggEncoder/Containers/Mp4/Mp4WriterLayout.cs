using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Containers.Mp4
{
    internal sealed class Mp4WriterTrack(MediaTrackInfo info, FmpInitSegmentWriter.InitTrack initTrack)
    {
        public MediaTrackInfo Info { get; } = info;

        public FmpInitSegmentWriter.InitTrack InitTrack { get; } = initTrack;

        public List<Mp4WriterSample> Samples { get; } = [];
    }

    internal readonly record struct Mp4WriterSample(long Dts, int CompositionOffset, int Size, bool IsKeyframe, long ScratchOffset)
    {
        public long Pts => Dts + CompositionOffset;
    }

    internal readonly record struct Mp4WriterChunk(int TrackIndex, int FirstSample, int SampleCount, long StartDts);

    internal sealed class Mp4WriterTrackLayout
    {
        public required uint[] Durations { get; init; }

        public required long MediaDuration { get; init; }

        public required long EmptyEditTicks { get; init; }

        public required long MediaTime { get; init; }

        public required long SegmentTicks { get; init; }

        public bool NeedsEditList => EmptyEditTicks > 0 || MediaTime != 0;

        public List<(int ChunkIndex, Mp4WriterChunk Chunk)> Chunks { get; } = [];
    }

    internal sealed class Mp4WriterLayout
    {
        private Mp4WriterLayout(Mp4WriterTrackLayout[] tracks, List<Mp4WriterChunk> chunks)
        {
            Tracks = tracks;
            Chunks = chunks;
        }

        public Mp4WriterTrackLayout[] Tracks { get; }

        public List<Mp4WriterChunk> Chunks { get; }

        public static Mp4WriterLayout Build(IReadOnlyList<Mp4WriterTrack> tracks, double maxChunkSeconds)
        {
            var trackLayouts = new Mp4WriterTrackLayout[tracks.Count];
            var chunks = new List<Mp4WriterChunk>();
            for (var t = 0; t < tracks.Count; t++)
            {
                trackLayouts[t] = BuildTrack(tracks[t]);
                var samples = tracks[t].Samples;
                var maxChunkTicks = Math.Max(1L, (long)(maxChunkSeconds * tracks[t].Info.Timescale));
                var first = 0;
                for (var i = 1; i <= samples.Count; i++)
                {
                    if (i == samples.Count || samples[i].Dts - samples[first].Dts >= maxChunkTicks)
                    {
                        chunks.Add(new Mp4WriterChunk(t, first, i - first, samples[first].Dts));
                        first = i;
                    }
                }
            }

            chunks.Sort((a, b) =>
            {
                var aTime = (Int128)a.StartDts * tracks[b.TrackIndex].Info.Timescale;
                var bTime = (Int128)b.StartDts * tracks[a.TrackIndex].Info.Timescale;
                var byTime = aTime.CompareTo(bTime);
                return byTime != 0 ? byTime : a.TrackIndex != b.TrackIndex ? a.TrackIndex.CompareTo(b.TrackIndex) : a.FirstSample.CompareTo(b.FirstSample);
            });

            for (var c = 0; c < chunks.Count; c++)
            {
                trackLayouts[chunks[c].TrackIndex].Chunks.Add((c, chunks[c]));
            }

            return new Mp4WriterLayout(trackLayouts, chunks);
        }

        private static Mp4WriterTrackLayout BuildTrack(Mp4WriterTrack track)
        {
            var samples = track.Samples;
            var durations = new uint[samples.Count];
            for (var i = 0; i < samples.Count; i++)
            {
                long delta = i + 1 < samples.Count
                    ? samples[i + 1].Dts - samples[i].Dts
                    : i > 0 ? durations[i - 1] : FmpMediaSegmentWriter.DefaultSampleDuration(track.InitTrack);
                durations[i] = delta <= uint.MaxValue
                    ? (uint)delta
                    : throw new ArgumentException($"Track {track.Info.TrackId} has a sample duration ({delta}) past 32 bits.");
            }

            if (samples.Count == 0)
            {
                return new Mp4WriterTrackLayout { Durations = durations, MediaDuration = 0, EmptyEditTicks = 0, MediaTime = 0, SegmentTicks = 0 };
            }

            var firstDts = samples[0].Dts;
            var minPts = long.MaxValue;
            var presentationEnd = long.MinValue;
            var mediaDuration = 0L;
            for (var i = 0; i < samples.Count; i++)
            {
                minPts = Math.Min(minPts, samples[i].Pts);
                presentationEnd = Math.Max(presentationEnd, samples[i].Pts + durations[i]);
                mediaDuration += durations[i];
            }

            var presentationStart = Math.Max(0, minPts);
            return new Mp4WriterTrackLayout
            {
                Durations = durations,
                MediaDuration = mediaDuration,
                EmptyEditTicks = presentationStart,
                MediaTime = Math.Max(0, presentationStart - firstDts),
                SegmentTicks = Math.Max(0, presentationEnd - presentationStart)
            };
        }
    }
}
