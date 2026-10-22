using EggEncoder.Codecs.Mov;
using EggEncoder.Media;

namespace EggEncoder.Streaming.Mp4
{
    /// <summary>
    /// Builds a fragmented MP4 (fMP4/CMAF) init segment (<c>ftyp</c>+<c>moov</c>) -- written once
    /// per rendition, describing the tracks but holding no samples of its own (every sample lives in
    /// a media segment from <see cref="FmpMediaSegmentWriter"/>). Two entry points: a single mono
    /// AAC-LC track from this project's own encoders (docs/streaming-plan.md item 2), and any
    /// <see cref="MediaTrackSet"/> of H.264, H.265 and AAC tracks from the packet model
    /// (docs/live-ingest-restream-plan.md item 5). Field values (<c>ftyp</c> brand set, box
    /// layout/ordering) are confirmed against a real ffmpeg-produced CMAF fMP4 fixture's own bytes,
    /// not assumed from the ISO/IEC 14496-12/23000-19 spec text alone.
    /// </summary>
    public static class FmpInitSegmentWriter
    {
        // The packet path's movie timescale; the movie duration it scales is always 0 here, so the
        // value only has to be valid (ffmpeg's own default).
        private const int PacketMovieTimescale = 1000;
        private const int SampleSizeBits = 16;
        private const string TracksParameter = "tracks";

        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public static byte[] BuildInitSegment(int channels, int sampleRate)
        {
            if (channels != 1)
            {
                throw new NotSupportedException("Only mono fMP4 init segments are supported");
            }

            var audioSpecificConfig = Mp4EsdsParser.BuildAudioSpecificConfig(channels, sampleRate);
            var track = new InitTrack(1, MediaKind.Audio, "mp4a", sampleRate, audioSpecificConfig, sampleRate, channels, 0, 0);

            return Build([track], sampleRate);
        }

        /// <summary>
        /// Builds an init segment with one <c>trak</c> per track of <paramref name="tracks"/>, in
        /// order: <c>avc1</c>+<c>avcC</c> for H.264, <c>hvc1</c>+<c>hvcC</c> for H.265 and
        /// <c>mp4a</c>+<c>esds</c> for AAC, each track's <c>mdhd</c> timescale being its own
        /// <see cref="MediaTrackInfo.Timescale"/>. The MP4 <c>track_ID</c> of a track is its
        /// position in the set plus one, the numbering <see cref="FmpMediaSegmentWriter"/>'s
        /// packet constructor uses for the same set.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="tracks"/> is empty; or a track's codec configuration is missing, isn't a
        /// valid avcC/hvcC record, or (AAC, when the track doesn't give them) its AudioSpecificConfig
        /// can't be read or gives no sample rate; or a video dimension or channel count doesn't fit
        /// the 16-bit sample entry field.
        /// </exception>
        /// <exception cref="NotSupportedException">A track's codec is not H.264, H.265 or AAC.</exception>
        public static byte[] BuildInitSegment(MediaTrackSet tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("An init segment needs at least one track.", nameof(tracks));
            }

            var initTracks = new InitTrack[tracks.Tracks.Count];
            for (var i = 0; i < initTracks.Length; i++)
            {
                initTracks[i] = ToInitTrack(tracks.Tracks[i], i + 1);
            }

            return Build(initTracks, PacketMovieTimescale);
        }

        // Validates one packet-model track and resolves what its sample entry needs. Shared with
        // FmpMediaSegmentWriter's packet constructor, so both reject the same track sets the same
        // way. Unknown dimensions or audio parameters (0) are read from the codec configuration;
        // an SPS this project's parser can't read leaves the dimensions 0, which decoders don't
        // need (they read the SPS themselves).
        internal static InitTrack ToInitTrack(MediaTrackInfo track, int mp4TrackId)
        {
            var configuration = track.CodecConfiguration.ToArray();
            switch (track.Codec)
            {
                case MediaCodec.H264:
                {
                    var record = Parse(track, () => AvcDecoderConfigurationRecord.Parse(configuration));
                    var (width, height) = ResolveDimensions(track, () =>
                    {
                        var sps = H264SpsParser.Parse(record.SequenceParameterSets[0].Span);
                        return (sps.Width, sps.Height);
                    });
                    return new InitTrack(mp4TrackId, MediaKind.Video, "avc1", track.Timescale, configuration, 0, 0, RequireUInt16(track, width), RequireUInt16(track, height));
                }

                case MediaCodec.H265:
                {
                    var record = Parse(track, () => HevcDecoderConfigurationRecord.Parse(configuration));
                    var (width, height) = ResolveDimensions(track, () =>
                    {
                        // Parse rejects a record with no SPS, so there is always a first one.
                        var sps = H265SpsParser.Parse(record.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType)[0].Span);
                        return (sps.Width, sps.Height);
                    });
                    return new InitTrack(mp4TrackId, MediaKind.Video, "hvc1", track.Timescale, configuration, 0, 0, RequireUInt16(track, width), RequireUInt16(track, height));
                }

                case MediaCodec.Aac:
                {
                    if (configuration.Length == 0)
                    {
                        throw new ArgumentException($"AAC track {track.TrackId} has no AudioSpecificConfig.", TracksParameter);
                    }

                    var (sampleRate, channels) = (track.SampleRate, track.Channels);
                    if (sampleRate == 0 || channels == 0)
                    {
                        var fromConfig = Parse(track, () => AudioSpecificConfigReader.Read(configuration));
                        sampleRate = sampleRate == 0 ? fromConfig.SampleRate : sampleRate;
                        channels = channels == 0 ? fromConfig.Channels : channels;
                    }

                    if (sampleRate <= 0)
                    {
                        throw new ArgumentException($"AAC track {track.TrackId} has no usable sample rate.", TracksParameter);
                    }

                    // channelConfiguration 0 means the layout lives in a program config element;
                    // mp4a's channelcount then says 2, as ffmpeg writes it.
                    channels = channels == 0 ? 2 : channels;

                    return new InitTrack(mp4TrackId, MediaKind.Audio, "mp4a", track.Timescale, configuration, sampleRate, RequireUInt16(track, channels), 0, 0);
                }

                default:
                    throw new NotSupportedException($"{track.Codec} tracks are not supported in fMP4; only H.264, H.265 and AAC are.");
            }
        }

        // The sample entry's width, height and channelcount fields are 16 bits wide.
        private static int RequireUInt16(MediaTrackInfo track, int value) =>
            value <= ushort.MaxValue
                ? value
                : throw new ArgumentException($"{track.Codec} track {track.TrackId}: {value} doesn't fit a 16-bit MP4 sample entry field.", TracksParameter);

        private static T Parse<T>(MediaTrackInfo track, Func<T> parse)
        {
            try
            {
                return parse();
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException($"{track.Codec} track {track.TrackId} has an invalid codec configuration: {e.Message}", TracksParameter, e);
            }
        }

        private static (int Width, int Height) ResolveDimensions(MediaTrackInfo track, Func<(int, int)> readSps)
        {
            if (track.Width != 0 && track.Height != 0)
            {
                return (track.Width, track.Height);
            }

            try
            {
                return readSps();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                return (track.Width, track.Height);
            }
        }

        private static byte[] Build(IReadOnlyList<InitTrack> tracks, int movieTimescale)
        {
            var builder = new Mp4BoxBuilder();

            builder.WriteBox("ftyp", BuildFtyp());
            builder.WriteBox("moov", b => BuildMoov(b, tracks, movieTimescale));

            return builder.ToArray();
        }

        private static byte[] BuildFtyp()
        {
            return new Mp4BoxBuilder()
                .WriteFourCc("iso5") // major_brand
                .WriteUInt32(512) // minor_version
                .WriteFourCc("iso5") // compatible_brands[]
                .WriteFourCc("iso6")
                .WriteFourCc("mp41")
                .ToArray();
        }

        private static void BuildMoov(Mp4BoxBuilder moov, IReadOnlyList<InitTrack> tracks, int movieTimescale)
        {
            moov.WriteBox("mvhd", BuildMvhd(movieTimescale, tracks.Count + 1));
            foreach (var track in tracks)
            {
                moov.WriteBox("trak", b => BuildTrak(b, track));
            }

            moov.WriteBox("mvex", b =>
            {
                foreach (var track in tracks)
                {
                    b.WriteBox("trex", BuildTrex(track.Mp4TrackId));
                }
            });
        }

        private static byte[] BuildMvhd(int timescale, int nextTrackId)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32((uint)timescale)
                .WriteUInt32(0) // duration -- unknown up front for a fragmented file
                .WriteUInt32(0x00010000) // rate: 1.0
                .WriteUInt16(0x0100) // volume: 1.0
                .WriteUInt16(0) // reserved
                .WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteIdentityMatrix()
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // pre_defined
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // pre_defined
                .WriteUInt32((uint)nextTrackId)
                .ToArray();
        }

        private static void BuildTrak(Mp4BoxBuilder trak, InitTrack track)
        {
            trak.WriteBox("tkhd", BuildTkhd(track));
            trak.WriteBox("mdia", b => BuildMdia(b, track));
        }

        private static byte[] BuildTkhd(InitTrack track)
        {
            const uint trackEnabledAndInMovie = 0x3;

            var isAudio = track.Kind == MediaKind.Audio;
            return new Mp4BoxBuilder()
                .WriteUInt32(trackEnabledAndInMovie) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32((uint)track.Mp4TrackId)
                .WriteUInt32(0) // reserved
                .WriteUInt32(0) // duration
                .WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteUInt16(0) // layer
                .WriteUInt16(0) // alternate_group
                .WriteUInt16(isAudio ? (ushort)0x0100 : (ushort)0) // volume: 1.0 for audio, 0 otherwise
                .WriteUInt16(0) // reserved
                .WriteIdentityMatrix()
                .WriteUInt32((uint)track.Width << 16) // width, 16.16 fixed -- 0 for audio
                .WriteUInt32((uint)track.Height << 16) // height, 16.16 fixed -- 0 for audio
                .ToArray();
        }

        private static void BuildMdia(Mp4BoxBuilder mdia, InitTrack track)
        {
            mdia.WriteBox("mdhd", BuildMdhd(track.Timescale));
            mdia.WriteBox("hdlr", BuildHdlr(track.Kind));
            mdia.WriteBox("minf", b => BuildMinf(b, track));
        }

        private static byte[] BuildMdhd(int timescale)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32((uint)timescale)
                .WriteUInt32(0) // duration
                .WriteUInt16(PackIso639Language("und"))
                .WriteUInt16(0) // pre_defined
                .ToArray();
        }

        // ISO 639-2/T packs 3 lowercase letters into 15 bits (5 bits each, letter - 0x60), with a
        // zero pad bit on top -- confirmed against the real fixture's own "und" (undefined) value,
        // 0x55C4.
        private static ushort PackIso639Language(string threeLetterCode)
        {
            var packed = 0;
            foreach (var c in threeLetterCode)
            {
                packed = (packed << 5) | (c - 0x60);
            }

            return (ushort)packed;
        }

        private static byte[] BuildHdlr(MediaKind kind)
        {
            var isAudio = kind == MediaKind.Audio;
            var name = System.Text.Encoding.ASCII.GetBytes(isAudio ? "SoundHandler\0" : "VideoHandler\0");

            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // pre_defined
                .WriteFourCc(isAudio ? "soun" : "vide") // handler_type
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteBytes(name)
                .ToArray();
        }

        private static void BuildMinf(Mp4BoxBuilder minf, InitTrack track)
        {
            if (track.Kind == MediaKind.Audio)
            {
                minf.WriteBox("smhd", BuildSmhd());
            }
            else
            {
                minf.WriteBox("vmhd", BuildVmhd());
            }

            minf.WriteBox("dinf", b => b.WriteBox("dref", BuildDref()));
            minf.WriteBox("stbl", b => BuildStbl(b, track));
        }

        private static byte[] BuildSmhd()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt16(0) // balance
                .WriteUInt16(0) // reserved
                .ToArray();
        }

        private static byte[] BuildVmhd()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(1) // version(0) + flags(1), the value ISO/IEC 14496-12 requires
                .WriteUInt16(0) // graphicsmode: copy
                .WriteUInt16(0).WriteUInt16(0).WriteUInt16(0) // opcolor
                .ToArray();
        }

        private static byte[] BuildDref()
        {
            const uint selfContained = 0x000001;

            var builder = new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(1); // entry_count

            builder.WriteBox("url ", new Mp4BoxBuilder().WriteUInt32(selfContained).ToArray());

            return builder.ToArray();
        }

        private static void BuildStbl(Mp4BoxBuilder stbl, InitTrack track)
        {
            stbl.WriteBox("stsd", b => BuildStsd(b, track));
            stbl.WriteBox("stts", EmptyTableWithEntryCount());
            stbl.WriteBox("stsc", EmptyTableWithEntryCount());
            stbl.WriteBox("stsz", new Mp4BoxBuilder().WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray());
            stbl.WriteBox("stco", EmptyTableWithEntryCount());
        }

        private static byte[] EmptyTableWithEntryCount() =>
            new Mp4BoxBuilder().WriteUInt32(0).WriteUInt32(0).ToArray();

        private static void BuildStsd(Mp4BoxBuilder stsd, InitTrack track)
        {
            stsd.WriteUInt32(0); // version(0) + flags
            stsd.WriteUInt32(1); // entry_count
            if (track.Kind == MediaKind.Audio)
            {
                stsd.WriteBox("mp4a", b => BuildMp4a(b, track));
            }
            else
            {
                stsd.WriteBox(track.SampleEntryType, b => BuildVisualSampleEntry(b, track));
            }
        }

        private static void BuildMp4a(Mp4BoxBuilder mp4a, InitTrack track)
        {
            mp4a.WriteUInt32(0).WriteUInt16(0); // reserved(6)
            mp4a.WriteUInt16(1); // data_reference_index
            mp4a.WriteUInt32(0).WriteUInt32(0); // reserved
            mp4a.WriteUInt16((ushort)track.Channels);
            mp4a.WriteUInt16(SampleSizeBits);
            mp4a.WriteUInt16(0); // pre_defined
            mp4a.WriteUInt16(0); // reserved
            // samplerate, 16.16 fixed: a rate past 16 bits doesn't fit and is written as 0, as
            // ffmpeg does -- decoders take the real rate from the AudioSpecificConfig.
            var sampleRate = track.SampleRate <= ushort.MaxValue ? (uint)track.SampleRate : 0;
            mp4a.WriteUInt32(sampleRate << 16);
            mp4a.WriteBox("esds", Mp4EsdsParser.BuildEsdsBoxContent(track.Configuration));
        }

        // VisualSampleEntry (ISO/IEC 14496-12 12.1.3) followed by the avcC/hvcC configuration box
        // (ISO/IEC 14496-15), whose bytes are the track's codec configuration record as-is.
        private static void BuildVisualSampleEntry(Mp4BoxBuilder entry, InitTrack track)
        {
            const uint resolution72Dpi = 0x00480000;
            const ushort depth24BitColour = 0x0018;

            entry.WriteUInt32(0).WriteUInt16(0); // reserved(6)
            entry.WriteUInt16(1); // data_reference_index
            entry.WriteUInt16(0); // pre_defined
            entry.WriteUInt16(0); // reserved
            entry.WriteUInt32(0).WriteUInt32(0).WriteUInt32(0); // pre_defined(12)
            entry.WriteUInt16((ushort)track.Width);
            entry.WriteUInt16((ushort)track.Height);
            entry.WriteUInt32(resolution72Dpi); // horizresolution
            entry.WriteUInt32(resolution72Dpi); // vertresolution
            entry.WriteUInt32(0); // reserved
            entry.WriteUInt16(1); // frame_count
            entry.WriteBytes(new byte[32]); // compressorname: an empty Pascal string, zero-padded
            entry.WriteUInt16(depth24BitColour);
            entry.WriteInt16(-1); // pre_defined
            entry.WriteBox(track.SampleEntryType == "avc1" ? "avcC" : "hvcC", track.Configuration);
        }

        private static byte[] BuildTrex(int mp4TrackId)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32((uint)mp4TrackId)
                .WriteUInt32(1) // default_sample_description_index
                .WriteUInt32(0) // default_sample_duration -- always explicit per-sample in trun instead
                .WriteUInt32(0) // default_sample_size -- always explicit per-sample in trun instead
                .WriteUInt32(0) // default_sample_flags
                .ToArray();
        }

        internal sealed record InitTrack(
            int Mp4TrackId,
            MediaKind Kind,
            string SampleEntryType,
            int Timescale,
            byte[] Configuration,
            int SampleRate,
            int Channels,
            int Width,
            int Height);
    }
}
