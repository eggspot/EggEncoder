using System.Buffers.Binary;
using EggEncoder.Codecs.Mov;
using EggEncoder.Media;

namespace EggEncoder.Containers.Mp4
{
    internal sealed class Mp4SourceTrack
    {
        private const int Mpeg4AudioObjectType = 0x40;
        private const int Mpeg2AacMainObjectType = 0x66;
        private const int Mpeg2AacSsrObjectType = 0x68;
        private const int Mpeg1AudioObjectType = 0x6B;
        private const int Mpeg2AudioObjectType = 0x69;

        private Mp4SourceTrack(MediaTrackInfo info, List<(long Offset, int Size)> samples, Mp4SampleTiming timing, HashSet<int>? syncSamples, long editShift, int nalLengthSize)
        {
            Info = info;
            Offsets = samples.Select(s => s.Offset).ToArray();
            Sizes = samples.Select(s => s.Size).ToArray();
            DecodeTimes = timing.DecodeTimes;
            CompositionOffsets = timing.CompositionOffsets;
            TotalDuration = timing.TotalDuration;
            SyncSamples = syncSamples;
            EditShift = editShift;
            NalLengthSize = nalLengthSize;
        }

        public MediaTrackInfo Info { get; }

        public long[] Offsets { get; }

        public int[] Sizes { get; }

        public long[] DecodeTimes { get; }

        public int[]? CompositionOffsets { get; }

        public long TotalDuration { get; }

        public HashSet<int>? SyncSamples { get; }

        public long EditShift { get; }

        public int NalLengthSize { get; }

        public bool IsSync(int index) => SyncSamples is null || SyncSamples.Contains(index);

        public long PresentationTime(int index) => DecodeTimes[index] + EditShift + (CompositionOffsets?[index] ?? 0);

        public int FindStartIndex(TimeSpan startAt)
        {
            if (startAt <= TimeSpan.Zero || Offsets.Length == 0)
            {
                return 0;
            }

            var target = (long)Math.Round(startAt.TotalSeconds * Info.Timescale);
            var start = -1;
            for (var i = 0; i < Offsets.Length; i++)
            {
                if (IsSync(i) && PresentationTime(i) <= target)
                {
                    start = i;
                }
            }

            if (start < 0)
            {
                return 0;
            }

            return Info.Kind == MediaKind.Video ? start : Math.Max(0, start - 1);
        }

        public static Mp4SourceTrack? TryRead(Stream stream, Mp4TrackHeader header, uint movieTimescale)
        {
            var entry = Mp4TrackReader.ReadFirstSampleEntry(stream, header.Stbl);
            var description = header.HandlerType == "vide"
                ? ReadVideoDescription(stream, header, entry)
                : ReadAudioDescription(stream, header, entry);
            if (description is null)
            {
                return null;
            }

            var samples = Mp4SampleTable.ReadSamples(stream, header.Stbl);
            var timing = Mp4TrackReader.ReadTiming(stream, header.Stbl, samples.Count);
            var syncSamples = Mp4SampleTable.ReadSyncSamples(stream, header.Stbl);
            var editShift = Mp4TrackReader.ReadEditShift(stream, header.Trak, movieTimescale, header.Timescale);
            return new Mp4SourceTrack(description.Value.Info, samples, timing, syncSamples, editShift, description.Value.NalLengthSize);
        }

        private static (MediaTrackInfo Info, int NalLengthSize)? ReadVideoDescription(Stream stream, Mp4TrackHeader header, Mp4SampleEntry entry)
        {
            var childrenStart = entry.FieldsStart + Mp4TrackReader.VisualSampleEntryFieldBytes;
            if (childrenStart > entry.End)
            {
                throw new InvalidDataException($"Visual sample entry '{entry.FourCc}' is too short");
            }

            stream.Position = entry.FieldsStart + 24;
            Span<byte> size = stackalloc byte[4];
            stream.ReadExactly(size);
            int entryWidth = BinaryPrimitives.ReadUInt16BigEndian(size[..2]);
            int entryHeight = BinaryPrimitives.ReadUInt16BigEndian(size[2..]);

            switch (entry.FourCc)
            {
                case "avc1" or "avc3":
                {
                    var avcC = FindChild(stream, entry, childrenStart, "avcC");
                    if (avcC is null)
                    {
                        return null;
                    }

                    var record = AvcDecoderConfigurationRecord.Parse(avcC);
                    var (width, height) = TrySpsSize(() => H264SpsParser.Parse(record.SequenceParameterSets[0].Span)) ?? (entryWidth, entryHeight);
                    var configuration = record.NalLengthSize == 4 ? avcC : (record with { NalLengthSize = 4 }).ToArray();
                    return (VideoInfo(header, MediaCodec.H264, configuration, width, height), record.NalLengthSize);
                }

                case "hvc1" or "hev1":
                {
                    var hvcC = FindChild(stream, entry, childrenStart, "hvcC");
                    if (hvcC is null)
                    {
                        return null;
                    }

                    var record = HevcDecoderConfigurationRecord.Parse(hvcC);
                    var spsUnits = record.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType);
                    var (width, height) = (spsUnits.Count > 0 ? TrySpsSize(() => H265SpsParser.Parse(spsUnits[0].Span)) : null) ?? (entryWidth, entryHeight);
                    var configuration = record.NalLengthSize == 4 ? hvcC : (record with { NalLengthSize = 4 }).ToArray();
                    return (VideoInfo(header, MediaCodec.H265, configuration, width, height), record.NalLengthSize);
                }

                default:
                    return null;
            }
        }

        private static MediaTrackInfo VideoInfo(Mp4TrackHeader header, MediaCodec codec, byte[] configuration, int width, int height) =>
            new(header.TrackId, MediaKind.Video, codec, header.Timescale, configuration, 0, width: width, height: height)
            {
                Rotation = header.Rotation
            };

        private static (int Width, int Height)? TrySpsSize(Func<H264SpsInfo> parse)
        {
            try
            {
                var sps = parse();
                return (sps.Width, sps.Height);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static (int Width, int Height)? TrySpsSize(Func<H265SpsInfo> parse)
        {
            try
            {
                var sps = parse();
                return (sps.Width, sps.Height);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static (MediaTrackInfo Info, int NalLengthSize)? ReadAudioDescription(Stream stream, Mp4TrackHeader header, Mp4SampleEntry entry)
        {
            if (entry.FieldsStart + Mp4TrackReader.AudioSampleEntryFieldBytes > entry.End)
            {
                throw new InvalidDataException($"Audio sample entry '{entry.FourCc}' is too short");
            }

            stream.Position = entry.FieldsStart;
            var fields = new byte[Mp4TrackReader.AudioSampleEntryFieldBytes];
            stream.ReadExactly(fields);
            var soundVersion = BinaryPrimitives.ReadUInt16BigEndian(fields.AsSpan(8, 2));
            int entryChannels = BinaryPrimitives.ReadUInt16BigEndian(fields.AsSpan(16, 2));
            var entrySampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(fields.AsSpan(24, 4)) >> 16);
            var childrenStart = entry.FieldsStart + Mp4TrackReader.AudioSampleEntryFieldBytes + soundVersion switch
            {
                1 => 16,
                2 => 36,
                _ => 0
            };

            switch (entry.FourCc)
            {
                case ".mp3":
                    return (AudioInfo(header, MediaCodec.Mp3, [], entrySampleRate, entryChannels), 0);
                case "mp4a":
                {
                    var esds = FindChild(stream, entry, childrenStart, "esds")
                        ?? FindChild(stream, entry, entry.FieldsStart + Mp4TrackReader.AudioSampleEntryFieldBytes, "esds");
                    if (esds is null)
                    {
                        return null;
                    }

                    var decoderConfig = Mp4EsdsParser.ReadDecoderConfig(esds);
                    if (decoderConfig.ObjectTypeIndication is Mpeg1AudioObjectType or Mpeg2AudioObjectType)
                    {
                        return (AudioInfo(header, MediaCodec.Mp3, [], entrySampleRate, entryChannels), 0);
                    }

                    if (decoderConfig.ObjectTypeIndication is not (Mpeg4AudioObjectType or (>= Mpeg2AacMainObjectType and <= Mpeg2AacSsrObjectType))
                        || decoderConfig.DecoderSpecificInfo.Length == 0)
                    {
                        return null;
                    }

                    var (sampleRate, channels) = AudioSpecificConfigReader.Read(decoderConfig.DecoderSpecificInfo);
                    return (AudioInfo(header, MediaCodec.Aac, decoderConfig.DecoderSpecificInfo, sampleRate, channels == 0 ? entryChannels : channels), 0);
                }

                default:
                    return null;
            }
        }

        private static MediaTrackInfo AudioInfo(Mp4TrackHeader header, MediaCodec codec, byte[] configuration, int sampleRate, int channels) =>
            new(header.TrackId, MediaKind.Audio, codec, header.Timescale, configuration, 0, sampleRate, channels);

        private static byte[]? FindChild(Stream stream, Mp4SampleEntry entry, long childrenStart, string type)
        {
            var atom = MovAtomReader.FindAtom(stream, type, childrenStart, entry.End);
            return atom is null ? null : Mp4TrackReader.ReadWholeContent(stream, atom.Value);
        }
    }
}
