using EggEncoder.Codecs.Mov;

namespace EggEncoder.Streaming.Mp4
{
    /// <summary>
    /// Builds a fragmented MP4 (fMP4/CMAF) init segment (<c>ftyp</c>+<c>moov</c>) for a single mono
    /// AAC-LC audio track -- written once per rendition, describing the track but holding no samples
    /// of its own (every sample lives in a media segment from <see cref="FmpMediaSegmentWriter"/>).
    /// Field values (<c>ftyp</c> brand set, box layout/ordering) are confirmed against a real
    /// ffmpeg-produced CMAF fMP4 fixture's own bytes, not assumed from the ISO/IEC 14496-12/23000-19
    /// spec text alone. See docs/streaming-plan.md item 2.
    /// </summary>
    public static class FmpInitSegmentWriter
    {
        private const int TrackId = 1;
        private const int SampleSizeBits = 16;

        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public static byte[] BuildInitSegment(int channels, int sampleRate)
        {
            if (channels != 1)
            {
                throw new NotSupportedException("Only mono fMP4 init segments are supported");
            }

            var audioSpecificConfig = Mp4EsdsParser.BuildAudioSpecificConfig(channels, sampleRate);

            var builder = new Mp4BoxBuilder();

            builder.WriteBox("ftyp", BuildFtyp());
            builder.WriteBox("moov", b => BuildMoov(b, channels, sampleRate, audioSpecificConfig));

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

        private static void BuildMoov(Mp4BoxBuilder moov, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            moov.WriteBox("mvhd", BuildMvhd(sampleRate));
            moov.WriteBox("trak", b => BuildTrak(b, channels, sampleRate, audioSpecificConfig));
            moov.WriteBox("mvex", b => b.WriteBox("trex", BuildTrex()));
        }

        private static byte[] BuildMvhd(int sampleRate)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32((uint)sampleRate) // timescale
                .WriteUInt32(0) // duration -- unknown up front for a fragmented file
                .WriteUInt32(0x00010000) // rate: 1.0
                .WriteUInt16(0x0100) // volume: 1.0
                .WriteUInt16(0) // reserved
                .WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteIdentityMatrix()
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // pre_defined
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // pre_defined
                .WriteUInt32(TrackId + 1) // next_track_ID
                .ToArray();
        }

        private static void BuildTrak(Mp4BoxBuilder trak, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            trak.WriteBox("tkhd", BuildTkhd());
            trak.WriteBox("mdia", b => BuildMdia(b, channels, sampleRate, audioSpecificConfig));
        }

        private static byte[] BuildTkhd()
        {
            const uint trackEnabledAndInMovie = 0x3;

            return new Mp4BoxBuilder()
                .WriteUInt32(trackEnabledAndInMovie) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32(TrackId)
                .WriteUInt32(0) // reserved
                .WriteUInt32(0) // duration
                .WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteUInt16(0) // layer
                .WriteUInt16(0) // alternate_group
                .WriteUInt16(0x0100) // volume: 1.0
                .WriteUInt16(0) // reserved
                .WriteIdentityMatrix()
                .WriteUInt32(0) // width -- audio track
                .WriteUInt32(0) // height -- audio track
                .ToArray();
        }

        private static void BuildMdia(Mp4BoxBuilder mdia, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            mdia.WriteBox("mdhd", BuildMdhd(sampleRate));
            mdia.WriteBox("hdlr", BuildHdlr());
            mdia.WriteBox("minf", b => BuildMinf(b, channels, sampleRate, audioSpecificConfig));
        }

        private static byte[] BuildMdhd(int sampleRate)
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // creation_time
                .WriteUInt32(0) // modification_time
                .WriteUInt32((uint)sampleRate) // timescale
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

        private static byte[] BuildHdlr()
        {
            var name = System.Text.Encoding.ASCII.GetBytes("SoundHandler\0");

            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(0) // pre_defined
                .WriteFourCc("soun") // handler_type
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // reserved
                .WriteBytes(name)
                .ToArray();
        }

        private static void BuildMinf(Mp4BoxBuilder minf, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            minf.WriteBox("smhd", BuildSmhd());
            minf.WriteBox("dinf", b => b.WriteBox("dref", BuildDref()));
            minf.WriteBox("stbl", b => BuildStbl(b, channels, sampleRate, audioSpecificConfig));
        }

        private static byte[] BuildSmhd()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt16(0) // balance
                .WriteUInt16(0) // reserved
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

        private static void BuildStbl(Mp4BoxBuilder stbl, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            stbl.WriteBox("stsd", b => BuildStsd(b, channels, sampleRate, audioSpecificConfig));
            stbl.WriteBox("stts", EmptyTableWithEntryCount());
            stbl.WriteBox("stsc", EmptyTableWithEntryCount());
            stbl.WriteBox("stsz", new Mp4BoxBuilder().WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray());
            stbl.WriteBox("stco", EmptyTableWithEntryCount());
        }

        private static byte[] EmptyTableWithEntryCount() =>
            new Mp4BoxBuilder().WriteUInt32(0).WriteUInt32(0).ToArray();

        private static void BuildStsd(Mp4BoxBuilder stsd, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            stsd.WriteUInt32(0); // version(0) + flags
            stsd.WriteUInt32(1); // entry_count
            stsd.WriteBox("mp4a", b => BuildMp4a(b, channels, sampleRate, audioSpecificConfig));
        }

        private static void BuildMp4a(Mp4BoxBuilder mp4a, int channels, int sampleRate, byte[] audioSpecificConfig)
        {
            mp4a.WriteUInt32(0).WriteUInt16(0); // reserved(6)
            mp4a.WriteUInt16(1); // data_reference_index
            mp4a.WriteUInt32(0).WriteUInt32(0); // reserved
            mp4a.WriteUInt16((ushort)channels);
            mp4a.WriteUInt16(SampleSizeBits);
            mp4a.WriteUInt16(0); // pre_defined
            mp4a.WriteUInt16(0); // reserved
            mp4a.WriteUInt32((uint)sampleRate << 16); // samplerate, 16.16 fixed
            mp4a.WriteBox("esds", Mp4EsdsParser.BuildEsdsBoxContent(audioSpecificConfig));
        }

        private static byte[] BuildTrex()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(TrackId)
                .WriteUInt32(1) // default_sample_description_index
                .WriteUInt32(0) // default_sample_duration -- always explicit per-sample in trun instead
                .WriteUInt32(0) // default_sample_size -- always explicit per-sample in trun instead
                .WriteUInt32(0) // default_sample_flags
                .ToArray();
        }
    }
}
