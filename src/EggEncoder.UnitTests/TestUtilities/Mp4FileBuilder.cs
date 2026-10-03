using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Aac;
using EggEncoder.Transform;

namespace EggEncoder.UnitTests.TestUtilities
{
    // Builds a minimal but valid MP4 file containing either a single mono AAC-LC audio track
    // (Create) or a single video track of arbitrary (not necessarily decodable -- this is for
    // exercising MovVideoDemuxer's container-level demuxing, not any codec) sample bytes
    // (CreateVideoOnly). Not a general-purpose muxer: one sample per chunk, one sample description
    // entry, no edit lists/fragmentation -- just enough structure to round-trip through
    // MovDecoder's/MovVideoDemuxer's demuxing.
    public static class Mp4FileBuilder
    {
        public static void Create(string filePath, int sampleRate, IReadOnlyList<byte[]> rawAacFrames)
        {
            var mdatContent = Concat(rawAacFrames);
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), UInt32Bytes(0x200), Encoding.ASCII.GetBytes("isom"), Encoding.ASCII.GetBytes("iso2"), Encoding.ASCII.GetBytes("mp41"));
            var mdat = Box("mdat", mdatContent);

            var mdatContentStart = ftyp.Length + 8; // + mdat's own 8-byte header
            var chunkOffsets = new List<uint>(rawAacFrames.Count);
            var runningOffset = (uint)mdatContentStart;
            foreach (var frame in rawAacFrames)
            {
                chunkOffsets.Add(runningOffset);
                runningOffset += (uint)frame.Length;
            }

            var totalSamples = (long)rawAacFrames.Count * 1024;
            var moov = BuildMoov(sampleRate, rawAacFrames, chunkOffsets, totalSamples);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            stream.Write(ftyp);
            stream.Write(mdat);
            stream.Write(moov);
        }

        // Reads a whole-file ADTS AAC stream (as produced by AacEncoder.Encode) and strips each
        // frame's 7-byte ADTS header, returning the raw_data_block bytes MP4 stores per sample --
        // the exact same field sequence AacDecoder.ParseAdtsHeader reads, so frame boundaries here
        // are guaranteed consistent with what the decoder itself expects.
        public static List<byte[]> ExtractRawAacFrames(string adtsFilePath)
        {
            var fileBytes = File.ReadAllBytes(adtsFilePath);
            var frames = new List<byte[]>();
            var reader = new BitReader(fileBytes);

            while (reader.RemainingBits >= 56)
            {
                var frameStartBit = reader.BitPosition;

                reader.SkipBits(12); // syncword
                reader.SkipBits(1); // ID
                reader.SkipBits(2); // layer
                reader.SkipBits(1); // protection_absent
                reader.SkipBits(2); // profile
                reader.SkipBits(4); // sampling_frequency_index
                reader.SkipBits(1); // private_bit
                reader.SkipBits(3); // channel_configuration
                reader.SkipBits(1); // original/copy
                reader.SkipBits(1); // home
                reader.SkipBits(1); // copyright_id_bit
                reader.SkipBits(1); // copyright_id_start
                var frameLength = (int)reader.ReadBits(13);
                reader.SkipBits(11); // adts_buffer_fullness
                reader.SkipBits(2); // number_of_raw_data_blocks_in_frame - 1

                const int headerByteLength = 7;
                var payloadStartByte = (frameStartBit / 8) + headerByteLength;
                var payloadLength = frameLength - headerByteLength;

                frames.Add(fileBytes[payloadStartByte..(payloadStartByte + payloadLength)]);

                reader.SkipToBitPosition(frameStartBit + (frameLength * 8));
            }

            return frames;
        }

        // sampleEntryCount lets a test force 'stsd' to declare zero sample description entries
        // (the real demuxer must reject that, even though real-world files always have at least
        // one) without having to hand-build the rest of the box tree just for that one case.
        public static void CreateVideoOnly(string filePath, string codecFourCc, IReadOnlyList<byte[]> samples, IReadOnlyList<int>? keyframeSampleIndices, int sampleEntryCount = 1)
        {
            var mdatContent = Concat(samples);
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), UInt32Bytes(0x200), Encoding.ASCII.GetBytes("isom"), Encoding.ASCII.GetBytes("iso2"), Encoding.ASCII.GetBytes("mp41"));
            var mdat = Box("mdat", mdatContent);

            var mdatContentStart = ftyp.Length + 8;
            var chunkOffsets = new List<uint>(samples.Count);
            var runningOffset = (uint)mdatContentStart;
            foreach (var sample in samples)
            {
                chunkOffsets.Add(runningOffset);
                runningOffset += (uint)sample.Length;
            }

            var moov = BuildVideoMoov(codecFourCc, samples, chunkOffsets, keyframeSampleIndices, sampleEntryCount);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            stream.Write(ftyp);
            stream.Write(mdat);
            stream.Write(moov);
        }

        private static byte[] BuildVideoMoov(string codecFourCc, IReadOnlyList<byte[]> samples, List<uint> chunkOffsets, IReadOnlyList<int>? keyframeSampleIndices, int sampleEntryCount)
        {
            var sampleCount = samples.Count;

            var mvhd = Box("mvhd",
                new byte[4],
                UInt32Bytes(0), UInt32Bytes(0),
                UInt32Bytes(600), UInt32Bytes((uint)sampleCount),
                UInt32Bytes(0x00010000),
                UInt16Bytes(0x0100), UInt16Bytes(0),
                new byte[8],
                IdentityMatrix(),
                new byte[24],
                UInt32Bytes(2));

            var tkhd = Box("tkhd",
                new byte[] { 0, 0, 0, 7 },
                UInt32Bytes(0), UInt32Bytes(0),
                UInt32Bytes(1),
                new byte[4],
                UInt32Bytes((uint)sampleCount),
                new byte[8],
                UInt16Bytes(0), UInt16Bytes(0),
                UInt16Bytes(0), UInt16Bytes(0),
                IdentityMatrix(),
                UInt32Bytes(640 << 16), UInt32Bytes(360 << 16));

            var mdhd = Box("mdhd",
                new byte[4],
                UInt32Bytes(0), UInt32Bytes(0),
                UInt32Bytes(600), UInt32Bytes((uint)sampleCount),
                UInt16Bytes(0x55C4), UInt16Bytes(0));

            var hdlr = Box("hdlr",
                new byte[4],
                UInt32Bytes(0),
                Encoding.ASCII.GetBytes("vide"),
                new byte[12],
                [(byte)0]);

            var vmhd = Box("vmhd", new byte[] { 0, 0, 0, 1 }, new byte[8]);
            var url = Box("url ", new byte[] { 0, 0, 0, 1 });
            var dref = Box("dref", new byte[4], UInt32Bytes(1), url);
            var dinf = Box("dinf", dref);

            var sampleEntries = new List<byte[]>(sampleEntryCount);
            for (var i = 0; i < sampleEntryCount; i++)
            {
                var sampleEntryContent = Concat(
                [
                    new byte[6], UInt16Bytes(1), // reserved, data_reference_index
                    new byte[16], // pre_defined/reserved (QuickTime-era fields)
                    UInt16Bytes(640), UInt16Bytes(360), // width, height
                    UInt32Bytes(0x00480000), UInt32Bytes(0x00480000), // horiz/vert resolution, 72dpi
                    UInt32Bytes(0), // reserved
                    UInt16Bytes(1), // frame_count
                    new byte[32], // compressorname (Pascal string)
                    UInt16Bytes(0x0018), // depth
                    UInt16Bytes(0xFFFF) // pre_defined = -1
                ]);
                sampleEntries.Add(Box(codecFourCc, sampleEntryContent));
            }

            var stsd = Box("stsd", Concat([new byte[4], UInt32Bytes((uint)sampleEntryCount), .. sampleEntries]));
            var stts = Box("stts", new byte[4], UInt32Bytes(1), UInt32Bytes((uint)sampleCount), UInt32Bytes(1));
            var stsc = Box("stsc", new byte[4], UInt32Bytes(1), UInt32Bytes(1), UInt32Bytes(1), UInt32Bytes(1));

            var stszEntries = new List<byte[]> { new byte[4], UInt32Bytes(0), UInt32Bytes((uint)sampleCount) };
            foreach (var sample in samples)
            {
                stszEntries.Add(UInt32Bytes((uint)sample.Length));
            }

            var stsz = Box("stsz", stszEntries.ToArray());

            var stcoEntries = new List<byte[]> { new byte[4], UInt32Bytes((uint)chunkOffsets.Count) };
            stcoEntries.AddRange(chunkOffsets.Select(UInt32Bytes));
            var stco = Box("stco", stcoEntries.ToArray());

            var stblParts = new List<byte[]> { stsd, stts, stsc, stsz, stco };
            if (keyframeSampleIndices is not null)
            {
                var stssEntries = new List<byte[]> { new byte[4], UInt32Bytes((uint)keyframeSampleIndices.Count) };
                stssEntries.AddRange(keyframeSampleIndices.Select(index => UInt32Bytes((uint)(index + 1))));
                stblParts.Add(Box("stss", stssEntries.ToArray()));
            }

            var stbl = Box("stbl", stblParts.ToArray());
            var minf = Box("minf", vmhd, dinf, stbl);
            var mdia = Box("mdia", mdhd, hdlr, minf);
            var trak = Box("trak", tkhd, mdia);

            return Box("moov", mvhd, trak);
        }

        private static byte[] BuildMoov(int sampleRate, IReadOnlyList<byte[]> rawAacFrames, List<uint> chunkOffsets, long totalSamples)
        {
            var mvhd = Box("mvhd",
                new byte[4], // version+flags
                UInt32Bytes(0), UInt32Bytes(0), // creation/modification time
                UInt32Bytes((uint)sampleRate), UInt32Bytes((uint)totalSamples), // timescale, duration
                UInt32Bytes(0x00010000), // rate = 1.0
                UInt16Bytes(0x0100), UInt16Bytes(0), // volume = 1.0, reserved
                new byte[8], // reserved
                IdentityMatrix(),
                new byte[24], // pre_defined
                UInt32Bytes(2)); // next_track_ID

            var tkhd = Box("tkhd",
                new byte[] { 0, 0, 0, 7 }, // version+flags (track enabled/in movie/in preview)
                UInt32Bytes(0), UInt32Bytes(0), // creation/modification time
                UInt32Bytes(1), // track_ID
                new byte[4], // reserved
                UInt32Bytes((uint)totalSamples),
                new byte[8], // reserved
                UInt16Bytes(0), UInt16Bytes(0), // layer, alternate_group
                UInt16Bytes(0x0100), UInt16Bytes(0), // volume, reserved
                IdentityMatrix(),
                UInt32Bytes(0), UInt32Bytes(0)); // width, height (audio-only track)

            var mdhd = Box("mdhd",
                new byte[4], // version+flags
                UInt32Bytes(0), UInt32Bytes(0), // creation/modification time
                UInt32Bytes((uint)sampleRate), UInt32Bytes((uint)totalSamples),
                UInt16Bytes(0x55C4), UInt16Bytes(0)); // language "und", pre_defined

            var hdlr = Box("hdlr",
                new byte[4], // version+flags
                UInt32Bytes(0), // pre_defined
                Encoding.ASCII.GetBytes("soun"),
                new byte[12], // reserved
                [(byte)0]); // empty name string

            var smhd = Box("smhd", new byte[4], UInt16Bytes(0), UInt16Bytes(0));
            var url = Box("url ", new byte[] { 0, 0, 0, 1 });
            var dref = Box("dref", new byte[4], UInt32Bytes(1), url);
            var dinf = Box("dinf", dref);

            var audioSpecificConfig = BuildAudioSpecificConfig(sampleRate);
            var esds = Box("esds",
                new byte[4], // version+flags
                Descriptor(0x03, Concat(
                [
                    UInt16Bytes(1), // ES_ID
                    [(byte)0], // flags
                    Descriptor(0x04, Concat(
                    [
                        [(byte)0x40], // objectTypeIndication: MPEG-4 Audio
                        [(byte)0x15], // streamType=5 (audio) << 2 | upStream=0 << 1 | reserved=1
                        new byte[3], // bufferSizeDB
                        UInt32Bytes(0), // maxBitrate
                        UInt32Bytes(0), // avgBitrate
                        Descriptor(0x05, audioSpecificConfig)
                    ])),
                    Descriptor(0x06, [(byte)0x02]) // SLConfigDescriptor, predefined=2 (reserved for MP4)
                ])));

            var mp4a = Box("mp4a",
                new byte[6], // reserved
                UInt16Bytes(1), // data_reference_index
                new byte[8], // version/revision/vendor
                UInt16Bytes(1), // channelcount (mono)
                UInt16Bytes(16), // samplesize
                UInt16Bytes(0), UInt16Bytes(0), // pre_defined, reserved
                UInt32Bytes((uint)sampleRate << 16), // samplerate, 16.16 fixed-point
                esds);

            var stsd = Box("stsd", new byte[4], UInt32Bytes(1), mp4a);
            var stts = Box("stts", new byte[4], UInt32Bytes(1), UInt32Bytes((uint)rawAacFrames.Count), UInt32Bytes(1024));
            var stsc = Box("stsc", new byte[4], UInt32Bytes(1), UInt32Bytes(1), UInt32Bytes(1), UInt32Bytes(1));

            var stszEntries = new List<byte[]> { new byte[4], UInt32Bytes(0), UInt32Bytes((uint)rawAacFrames.Count) };
            foreach (var frame in rawAacFrames)
            {
                stszEntries.Add(UInt32Bytes((uint)frame.Length));
            }

            var stsz = Box("stsz", stszEntries.ToArray());

            var stcoEntries = new List<byte[]> { new byte[4], UInt32Bytes((uint)chunkOffsets.Count) };
            stcoEntries.AddRange(chunkOffsets.Select(UInt32Bytes));
            var stco = Box("stco", stcoEntries.ToArray());

            var stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
            var minf = Box("minf", smhd, dinf, stbl);
            var mdia = Box("mdia", mdhd, hdlr, minf);
            var trak = Box("trak", tkhd, mdia);

            return Box("moov", mvhd, trak);
        }

        private static byte[] BuildAudioSpecificConfig(int sampleRate)
        {
            var sampleRateIndex = Array.IndexOf(AacTables.SampleRates, sampleRate);
            if (sampleRateIndex < 0)
            {
                throw new ArgumentException($"Sample rate {sampleRate} is not a valid MPEG-4 AAC sample rate", nameof(sampleRate));
            }

            const int aacLowComplexityObjectType = 2;
            const int monoChannelConfiguration = 1;

            var byte0 = (byte)((aacLowComplexityObjectType << 3) | (sampleRateIndex >> 1));
            var byte1 = (byte)(((sampleRateIndex & 1) << 7) | (monoChannelConfiguration << 3));

            return [byte0, byte1];
        }

        private static byte[] Descriptor(byte tag, byte[] content)
        {
            if (content.Length >= 128)
            {
                throw new NotSupportedException("Descriptor content too large for this minimal test builder's single-byte size encoding");
            }

            return Concat([[tag], [(byte)content.Length], content]);
        }

        private static byte[] Box(string fourCc, params byte[][] parts)
        {
            var content = Concat(parts);
            var box = new byte[8 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(box, 4);
            content.CopyTo(box, 8);
            return box;
        }

        private static byte[] Concat(IReadOnlyList<byte[]> parts)
        {
            var totalLength = 0;
            foreach (var part in parts)
            {
                totalLength += part.Length;
            }

            var result = new byte[totalLength];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(result, offset);
                offset += part.Length;
            }

            return result;
        }

        private static byte[] IdentityMatrix()
        {
            // Unity transformation matrix per ISO/IEC 14496-12: [0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000]
            return Concat(
            [
                UInt32Bytes(0x00010000), UInt32Bytes(0), UInt32Bytes(0),
                UInt32Bytes(0), UInt32Bytes(0x00010000), UInt32Bytes(0),
                UInt32Bytes(0), UInt32Bytes(0), UInt32Bytes(0x40000000)
            ]);
        }

        private static byte[] UInt32Bytes(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] UInt16Bytes(ushort value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            return bytes;
        }
    }
}
