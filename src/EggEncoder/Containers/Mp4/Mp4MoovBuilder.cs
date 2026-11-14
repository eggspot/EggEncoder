using EggEncoder.Codecs.Mov;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Containers.Mp4
{
    internal static class Mp4MoovBuilder
    {
        private const uint FixedOne = 0x00010000;
        private const uint FixedOneW = 0x40000000;

        public static byte[] Build(IReadOnlyList<Mp4WriterTrack> tracks, Mp4WriterLayout layout, int movieTimescale, long mdatPayloadStart)
        {
            var chunkOffsets = new long[layout.Chunks.Count];
            var offset = mdatPayloadStart;
            for (var c = 0; c < layout.Chunks.Count; c++)
            {
                chunkOffsets[c] = offset;
                var chunk = layout.Chunks[c];
                var samples = tracks[chunk.TrackIndex].Samples;
                for (var i = chunk.FirstSample; i < chunk.FirstSample + chunk.SampleCount; i++)
                {
                    offset += samples[i].Size;
                }
            }

            var trackDurations = new long[tracks.Count];
            for (var t = 0; t < tracks.Count; t++)
            {
                var trackLayout = layout.Tracks[t];
                trackDurations[t] = MediaTimestamp.Rescale(trackLayout.EmptyEditTicks + trackLayout.SegmentTicks, tracks[t].Info.Timescale, movieTimescale);
            }

            var moov = new Mp4BoxBuilder();
            moov.WriteBox("mvhd", BuildMvhd(movieTimescale, trackDurations.DefaultIfEmpty(0).Max(), tracks.Count + 1));
            for (var t = 0; t < tracks.Count; t++)
            {
                var track = tracks[t];
                var trackLayout = layout.Tracks[t];
                var duration = trackDurations[t];
                moov.WriteBox("trak", trak =>
                {
                    trak.WriteBox("tkhd", BuildTkhd(track, duration));
                    if (trackLayout.NeedsEditList)
                    {
                        trak.WriteBox("edts", edts => edts.WriteBox("elst", BuildElst(track, trackLayout, movieTimescale)));
                    }

                    trak.WriteBox("mdia", mdia =>
                    {
                        mdia.WriteBox("mdhd", BuildMdhd(track.Info.Timescale, trackLayout.MediaDuration));
                        mdia.WriteBox("hdlr", FmpInitSegmentWriter.BuildHdlr(track.Info.Kind));
                        mdia.WriteBox("minf", minf =>
                        {
                            minf.WriteBox(track.Info.Kind == MediaKind.Audio ? "smhd" : "vmhd", track.Info.Kind == MediaKind.Audio ? FmpInitSegmentWriter.BuildSmhd() : FmpInitSegmentWriter.BuildVmhd());
                            minf.WriteBox("dinf", dinf => dinf.WriteBox("dref", FmpInitSegmentWriter.BuildDref()));
                            minf.WriteBox("stbl", stbl => BuildStbl(stbl, track, trackLayout, chunkOffsets));
                        });
                    });
                });
            }

            return new Mp4BoxBuilder().WriteBox("moov", moov.ToArray()).ToArray();
        }

        private static void BuildStbl(Mp4BoxBuilder stbl, Mp4WriterTrack track, Mp4WriterTrackLayout layout, long[] chunkOffsets)
        {
            var samples = track.Samples;
            stbl.WriteBox("stsd", stsd => FmpInitSegmentWriter.BuildStsd(stsd, track.InitTrack));

            var stts = RunLengths(layout.Durations.Length, i => (long)layout.Durations[i]);
            stbl.WriteBox("stts", b =>
            {
                b.WriteUInt32(0).WriteUInt32((uint)stts.Count);
                foreach (var (count, value) in stts)
                {
                    b.WriteUInt32((uint)count).WriteUInt32((uint)value);
                }
            });

            if (track.Info.Kind == MediaKind.Video && samples.Any(s => !s.IsKeyframe))
            {
                var syncIndices = Enumerable.Range(0, samples.Count).Where(i => samples[i].IsKeyframe).ToList();
                stbl.WriteBox("stss", b =>
                {
                    b.WriteUInt32(0).WriteUInt32((uint)syncIndices.Count);
                    foreach (var index in syncIndices)
                    {
                        b.WriteUInt32((uint)(index + 1));
                    }
                });
            }

            if (samples.Any(s => s.CompositionOffset != 0))
            {
                var ctts = RunLengths(samples.Count, i => samples[i].CompositionOffset);
                var version = samples.Any(s => s.CompositionOffset < 0) ? 1u : 0u;
                stbl.WriteBox("ctts", b =>
                {
                    b.WriteUInt32(version << 24).WriteUInt32((uint)ctts.Count);
                    foreach (var (count, value) in ctts)
                    {
                        b.WriteUInt32((uint)count).WriteInt32((int)value);
                    }
                });
            }

            var chunkSizes = layout.Chunks.Select(c => (long)c.Chunk.SampleCount).ToList();
            var stsc = RunLengths(chunkSizes.Count, i => chunkSizes[i]);
            stbl.WriteBox("stsc", b =>
            {
                b.WriteUInt32(0).WriteUInt32((uint)stsc.Count);
                var firstChunk = 1;
                foreach (var (count, value) in stsc)
                {
                    b.WriteUInt32((uint)firstChunk).WriteUInt32((uint)value).WriteUInt32(1);
                    firstChunk += count;
                }
            });

            stbl.WriteBox("stsz", b =>
            {
                b.WriteUInt32(0).WriteUInt32(0).WriteUInt32((uint)samples.Count);
                foreach (var sample in samples)
                {
                    b.WriteUInt32((uint)sample.Size);
                }
            });

            stbl.WriteBox("stco", b =>
            {
                b.WriteUInt32(0).WriteUInt32((uint)layout.Chunks.Count);
                foreach (var (chunkIndex, _) in layout.Chunks)
                {
                    b.WriteUInt32((uint)chunkOffsets[chunkIndex]);
                }
            });
        }

        private static List<(int Count, long Value)> RunLengths(int length, Func<int, long> valueAt)
        {
            var runs = new List<(int Count, long Value)>();
            for (var i = 0; i < length; i++)
            {
                var value = valueAt(i);
                if (runs.Count > 0 && runs[^1].Value == value)
                {
                    runs[^1] = (runs[^1].Count + 1, value);
                }
                else
                {
                    runs.Add((1, value));
                }
            }

            return runs;
        }

        private static byte[] BuildMvhd(int timescale, long duration, int nextTrackId)
        {
            var version1 = duration > uint.MaxValue;
            var builder = new Mp4BoxBuilder().WriteUInt32(version1 ? 1u << 24 : 0);
            WriteTimesAndDuration(builder, version1, (uint)timescale, duration);
            return builder
                .WriteUInt32(FixedOne)
                .WriteUInt16(0x0100)
                .WriteUInt16(0)
                .WriteUInt32(0).WriteUInt32(0)
                .WriteIdentityMatrix()
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0)
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0)
                .WriteUInt32((uint)nextTrackId)
                .ToArray();
        }

        private static byte[] BuildTkhd(Mp4WriterTrack track, long duration)
        {
            const uint trackEnabledAndInMovie = 0x3;
            var version1 = duration > uint.MaxValue;
            var builder = new Mp4BoxBuilder().WriteUInt32((version1 ? 1u << 24 : 0) | trackEnabledAndInMovie);
            if (version1)
            {
                builder.WriteUInt64(0).WriteUInt64(0).WriteUInt32((uint)track.InitTrack.Mp4TrackId).WriteUInt32(0).WriteUInt64((ulong)duration);
            }
            else
            {
                builder.WriteUInt32(0).WriteUInt32(0).WriteUInt32((uint)track.InitTrack.Mp4TrackId).WriteUInt32(0).WriteUInt32((uint)duration);
            }

            var isAudio = track.Info.Kind == MediaKind.Audio;
            builder.WriteUInt32(0).WriteUInt32(0)
                .WriteUInt16(0)
                .WriteUInt16(isAudio ? (ushort)1 : (ushort)0)
                .WriteUInt16(isAudio ? (ushort)0x0100 : (ushort)0)
                .WriteUInt16(0);
            WriteRotationMatrix(builder, isAudio ? 0 : track.Info.Rotation, track.InitTrack.Width, track.InitTrack.Height);
            return builder
                .WriteUInt32((uint)track.InitTrack.Width << 16)
                .WriteUInt32((uint)track.InitTrack.Height << 16)
                .ToArray();
        }

        internal static void WriteRotationMatrix(Mp4BoxBuilder builder, int rotation, int width, int height)
        {
            var (a, b, c, d, x, y) = rotation switch
            {
                90 => (0, 1, -1, 0, height, 0),
                180 => (-1, 0, 0, -1, width, height),
                270 => (0, -1, 1, 0, 0, width),
                _ => (1, 0, 0, 1, 0, 0)
            };

            builder.WriteInt32(a << 16).WriteInt32(b << 16).WriteUInt32(0)
                .WriteInt32(c << 16).WriteInt32(d << 16).WriteUInt32(0)
                .WriteInt32(x << 16).WriteInt32(y << 16).WriteUInt32(FixedOneW);
        }

        private static byte[] BuildMdhd(int timescale, long duration)
        {
            var version1 = duration > uint.MaxValue;
            var builder = new Mp4BoxBuilder().WriteUInt32(version1 ? 1u << 24 : 0);
            WriteTimesAndDuration(builder, version1, (uint)timescale, duration);
            return builder.WriteUInt16(FmpInitSegmentWriter.PackIso639Language("und")).WriteUInt16(0).ToArray();
        }

        private static void WriteTimesAndDuration(Mp4BoxBuilder builder, bool version1, uint timescale, long duration)
        {
            if (version1)
            {
                builder.WriteUInt64(0).WriteUInt64(0).WriteUInt32(timescale).WriteUInt64((ulong)duration);
            }
            else
            {
                builder.WriteUInt32(0).WriteUInt32(0).WriteUInt32(timescale).WriteUInt32((uint)duration);
            }
        }

        private static byte[] BuildElst(Mp4WriterTrack track, Mp4WriterTrackLayout layout, int movieTimescale)
        {
            var emptyDuration = MediaTimestamp.Rescale(layout.EmptyEditTicks, track.Info.Timescale, movieTimescale);
            var segmentDuration = MediaTimestamp.Rescale(layout.SegmentTicks, track.Info.Timescale, movieTimescale);
            var entries = new List<(long SegmentDuration, long MediaTime)>();
            if (emptyDuration > 0)
            {
                entries.Add((emptyDuration, -1));
            }

            entries.Add((segmentDuration, layout.MediaTime));
            var version1 = entries.Any(e => e.SegmentDuration > uint.MaxValue || e.MediaTime > int.MaxValue);
            var builder = new Mp4BoxBuilder().WriteUInt32(version1 ? 1u << 24 : 0).WriteUInt32((uint)entries.Count);
            foreach (var (duration, mediaTime) in entries)
            {
                if (version1)
                {
                    builder.WriteUInt64((ulong)duration).WriteUInt64(unchecked((ulong)mediaTime));
                }
                else
                {
                    builder.WriteUInt32((uint)duration).WriteInt32((int)mediaTime);
                }

                builder.WriteUInt32(FixedOne);
            }

            return builder.ToArray();
        }
    }
}
