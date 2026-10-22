namespace EggEncoder.UnitTests.Media
{
    // Codec-configuration records captured from real encoder output, as data: the avcC/hvcC box
    // payloads of MP4s produced by `ffmpeg -f lavfi -i testsrc=... -pix_fmt yuv420p -c:v libx264`
    // / `-c:v libx265 -x265-params info=0 -tag:v hvc1`, with the stream properties `ffprobe`
    // reported for each baked in as the expected values. The 640x360 H.264 record is not here:
    // tests read it straight out of the checked-in Codecs/Mov/test.mp4 fixture.
    internal static class MediaFixtures
    {
        // 1920x1080 (coded 1088 rows), High, level 4.0.
        public static readonly byte[] Avc1080pRecord = Convert.FromHexString(
            "01640028ffe1001b67640028acd940780227e5c044000003000400000300c83c60c65801000668ebe3cb22c0fdf8f800");

        // 176x144, Constrained Baseline, level 1.1: no high-profile extension, no chroma fields in the SPS.
        public static readonly byte[] AvcBaselineRecord = Convert.FromHexString(
            "0142c00bffe100166742c00bd902c4ec0440000003004000000c83c50a9201000568cb83cb20");

        // 320x240 interlaced (field-coded, frame_mbs_only_flag = 0), High, level 2.1.
        public static readonly byte[] AvcInterlacedRecord = Convert.FromHexString(
            "01640015ffe1001a67640015acd941410fcb808800000300080000030190f8a14cb001000768fba3cb3002c0fdf8f800");

        // 1920x1080, Main, level 4 (general_level_idc 120), no conformance window.
        public static readonly byte[] Hevc1080pRecord = Convert.FromHexString(
            "01016000000090000000000078f000fcfdf8f800000f03a00001001840010c01ffff016000000300900000030000030078959809a10001002a420101016000000300900000030000030078a003c08010e596566924caf0168080000003008000000c84a2000100074401c172b46240");

        // 100x60 (coded 64 rows, conformance window crops 4), Main, level 1 (general_level_idc 30).
        public static readonly byte[] Hevc100x60Record = Convert.FromHexString(
            "0101600000009000000000001ef000fcfdf8f800000f03a00001001840010c01ffff01600000030090000003000003001e959809a10001002a42010101600000030090000003000003001ea03481077796566b932bc05a020000030002000003003210a2000100064401c173d189");

        public static string Mp4FixturePath => Path.GetFullPath("Codecs/Mov/test.mp4");

        // The avcC payload of Codecs/Mov/test.mp4 (640x360, High, level 3.0).
        public static byte[] ReadMp4FixtureAvcC()
        {
            var bytes = File.ReadAllBytes(Mp4FixturePath);
            var typeIndex = bytes.AsSpan().IndexOf("avcC"u8);
            var boxSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(typeIndex - 4));
            return bytes.AsSpan(typeIndex + 4, boxSize - 8).ToArray();
        }
    }
}
