namespace EggEncoder.Containers.Matroska
{
    internal static class MatroskaIds
    {
        public const long Ebml = 0x1A45DFA3;
        public const long DocType = 0x4282;
        public const long Segment = 0x18538067;
        public const long SeekHead = 0x114D9B74;
        public const long Info = 0x1549A966;
        public const long TimestampScale = 0x2AD7B1;
        public const long Duration = 0x4489;
        public const long Tracks = 0x1654AE6B;
        public const long TrackEntry = 0xAE;
        public const long TrackNumber = 0xD7;
        public const long TrackType = 0x83;
        public const long CodecId = 0x86;
        public const long CodecPrivate = 0x63A2;
        public const long CodecDelay = 0x56AA;
        public const long DefaultDuration = 0x23E383;
        public const long ContentEncodings = 0x6D80;
        public const long Video = 0xE0;
        public const long PixelWidth = 0xB0;
        public const long PixelHeight = 0xBA;
        public const long Audio = 0xE1;
        public const long SamplingFrequency = 0xB5;
        public const long Channels = 0x9F;
        public const long Cluster = 0x1F43B675;
        public const long ClusterTimestamp = 0xE7;
        public const long SimpleBlock = 0xA3;
        public const long BlockGroup = 0xA0;
        public const long Block = 0xA1;
        public const long ReferenceBlock = 0xFB;
        public const long Cues = 0x1C53BB6B;
        public const long Chapters = 0x1043A770;
        public const long Tags = 0x1254C367;
        public const long Attachments = 0x1941A469;
        public const long Void = 0xEC;
        public const long Crc32 = 0xBF;

        public static bool IsTopLevel(long id) =>
            id is Cluster or Cues or Chapters or Tags or Attachments or SeekHead or Info or Tracks or Segment or Ebml;
    }
}
