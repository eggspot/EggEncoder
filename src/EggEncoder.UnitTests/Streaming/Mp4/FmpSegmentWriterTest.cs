using System.Buffers.Binary;
using EggEncoder.Codecs.Mov;
using EggEncoder.Streaming.Mp4;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Mp4
{
    public class FmpSegmentWriterTest
    {
        private const int Channels = 1;
        private const int SampleRate = 44100;

        [Fact]
        public void BuildInitSegment_Should_RoundTrip_Through_MovAtomReader()
        {
            var initSegment = FmpInitSegmentWriter.BuildInitSegment(Channels, SampleRate);

            using var stream = new MemoryStream(initSegment);

            var topLevel = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length);
            var ftyp = topLevel.Single(a => a.Type == "ftyp");
            stream.Position = ftyp.ContentStart;
            Span<byte> majorBrand = stackalloc byte[4];
            stream.ReadExactly(majorBrand);
            System.Text.Encoding.ASCII.GetString(majorBrand).Should().Be("iso5");

            var moov = topLevel.Single(a => a.Type == "moov");

            var mvhd = MovAtomReader.FindAtom(stream, "mvhd", moov.ContentStart, moov.ContentEnd)!.Value;
            ReadUInt32(stream, mvhd.ContentStart + 12).Should().Be((uint)SampleRate, "mvhd's own timescale");

            var trak = MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd).Single(a => a.Type == "trak");
            var mdia = MovAtomReader.FindAtom(stream, "mdia", trak.ContentStart, trak.ContentEnd)!.Value;

            var mdhd = MovAtomReader.FindAtom(stream, "mdhd", mdia.ContentStart, mdia.ContentEnd)!.Value;
            ReadUInt32(stream, mdhd.ContentStart + 12).Should().Be((uint)SampleRate, "mdhd's own timescale");

            var hdlr = MovAtomReader.FindAtom(stream, "hdlr", mdia.ContentStart, mdia.ContentEnd)!.Value;
            stream.Position = hdlr.ContentStart + 8;
            Span<byte> handlerType = stackalloc byte[4];
            stream.ReadExactly(handlerType);
            System.Text.Encoding.ASCII.GetString(handlerType).Should().Be("soun");

            var minf = MovAtomReader.FindAtom(stream, "minf", mdia.ContentStart, mdia.ContentEnd)!.Value;
            var stbl = MovAtomReader.FindAtom(stream, "stbl", minf.ContentStart, minf.ContentEnd)!.Value;
            var stsd = MovAtomReader.FindAtom(stream, "stsd", stbl.ContentStart, stbl.ContentEnd)!.Value;

            ReadUInt32(stream, stsd.ContentStart + 4).Should().Be(1, "stsd's own entry_count");

            var mp4a = MovAtomReader.FindAtom(stream, "mp4a", stsd.ContentStart + 8, stsd.ContentEnd)!.Value;
            // AudioSampleEntry fixed fields: reserved(6) + data_reference_index(2) + reserved(8) = 16
            // bytes before channelcount.
            ReadUInt16(stream, mp4a.ContentStart + 16).Should().Be(Channels, "mp4a's own channelcount");
            ReadUInt16(stream, mp4a.ContentStart + 18).Should().Be(16, "mp4a's own samplesize");
            ReadUInt32(stream, mp4a.ContentStart + 24).Should().Be((uint)SampleRate << 16, "mp4a's own samplerate, 16.16 fixed");

            var esds = MovAtomReader.FindAtom(stream, "esds", mp4a.ContentStart + 28, mp4a.ContentEnd)!.Value;
            var esdsBytes = new byte[esds.ContentEnd - esds.ContentStart];
            stream.Position = esds.ContentStart;
            stream.ReadExactly(esdsBytes);
            var audioConfig = Mp4EsdsParser.ParseAudioSpecificConfig(esdsBytes);
            audioConfig.SampleRate.Should().Be(SampleRate);
            audioConfig.Channels.Should().Be(Channels);

            foreach (var emptyTable in new[] { "stts", "stsc", "stco" })
            {
                var table = MovAtomReader.FindAtom(stream, emptyTable, stbl.ContentStart, stbl.ContentEnd)!.Value;
                ReadUInt32(stream, table.ContentStart + 4).Should().Be(0, $"{emptyTable}'s own entry_count -- a fragmented file's samples live in media segments, never the init segment");
            }

            var mvex = MovAtomReader.FindAtom(stream, "mvex", moov.ContentStart, moov.ContentEnd)!.Value;
            var trex = MovAtomReader.FindAtom(stream, "trex", mvex.ContentStart, mvex.ContentEnd)!.Value;
            ReadUInt32(stream, trex.ContentStart + 4).Should().Be(1, "trex's own track_ID");
        }

        [Fact]
        public void BuildInitSegment_WithStereoChannels_Should_Throw()
        {
            var act = () => FmpInitSegmentWriter.BuildInitSegment(2, SampleRate);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void BuildMediaSegment_WithNoFrames_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter();

            var act = () => writer.BuildMediaSegment([]);

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void BuildMediaSegment_Should_ProduceCorrectMoofFields_AndMdatBytes()
        {
            var writer = new FmpMediaSegmentWriter();
            var frames = BuildFrames([1024, 1024, 300]);

            var segment = writer.BuildMediaSegment(frames);

            var parsed = ParseMediaSegment(segment);

            parsed.SequenceNumber.Should().Be(1);
            parsed.BaseMediaDecodeTime.Should().Be(0);
            parsed.SampleDurations.Should().Equal(1024, 1024, 300);
            parsed.SampleSizes.Should().Equal(frames[0].Payload.Length, frames[1].Payload.Length, frames[2].Payload.Length);
            parsed.MdatContent.Should().Equal(frames[0].Payload.Concat(frames[1].Payload).Concat(frames[2].Payload));

            // default-base-is-moof + data-offset-present: the first sample must land exactly at
            // data_offset bytes from the start of moof.
            var expectedFirstSampleOffset = parsed.MoofTotalSize + 8;
            (parsed.DataOffset).Should().Be(expectedFirstSampleOffset);
        }

        [Fact]
        public void BuildMediaSegment_CalledTwice_Should_AdvanceSequenceNumber_AndBaseMediaDecodeTime()
        {
            var writer = new FmpMediaSegmentWriter();

            var first = writer.BuildMediaSegment(BuildFrames([1024, 1024]));
            var second = writer.BuildMediaSegment(BuildFrames([1024]));

            var parsedFirst = ParseMediaSegment(first);
            var parsedSecond = ParseMediaSegment(second);

            parsedFirst.SequenceNumber.Should().Be(1);
            parsedFirst.BaseMediaDecodeTime.Should().Be(0);

            parsedSecond.SequenceNumber.Should().Be(2);
            parsedSecond.BaseMediaDecodeTime.Should().Be(2048, "the first segment's two 1024-sample frames advance the running sample count by 2048");
        }

        private static List<Mp4EncodedFrame> BuildFrames(int[] sampleCounts)
        {
            var frames = new List<Mp4EncodedFrame>();
            var seed = 0;
            foreach (var sampleCount in sampleCounts)
            {
                var payload = new byte[16 + seed];
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)((seed + i) % 256);
                }

                frames.Add(new Mp4EncodedFrame(payload, sampleCount));
                seed += 7;
            }

            return frames;
        }

        private static ushort ReadUInt16(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[2];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }

        private static uint ReadUInt32(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }

        private static ulong ReadUInt64(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }

        // Mp4SampleTable/MovDecoder only understand the non-fragmented stsz/stco/stsc sample-table
        // layout -- a fragmented file's own moof/traf/tfhd/tfdt/trun sample table is a genuinely
        // different box set, parsed here directly (test-only; see docs/streaming-plan.md item 2's
        // own acceptance criteria for why this is deliberately not folded into Mp4SampleTable).
        private static ParsedMediaSegment ParseMediaSegment(byte[] segmentBytes)
        {
            using var stream = new MemoryStream(segmentBytes);

            var topLevel = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length);
            var moof = topLevel.Single(a => a.Type == "moof");
            var moofTotalSize = moof.ContentEnd - (moof.ContentStart - 8);

            var mfhd = MovAtomReader.FindAtom(stream, "mfhd", moof.ContentStart, moof.ContentEnd)!.Value;
            var sequenceNumber = (long)ReadUInt32(stream, mfhd.ContentStart + 4);

            var traf = MovAtomReader.FindAtom(stream, "traf", moof.ContentStart, moof.ContentEnd)!.Value;
            var tfdt = MovAtomReader.FindAtom(stream, "tfdt", traf.ContentStart, traf.ContentEnd)!.Value;
            var baseMediaDecodeTime = (long)ReadUInt64(stream, tfdt.ContentStart + 4);

            var trun = MovAtomReader.FindAtom(stream, "trun", traf.ContentStart, traf.ContentEnd)!.Value;
            var sampleCount = ReadUInt32(stream, trun.ContentStart + 4);
            var dataOffset = (long)ReadUInt32(stream, trun.ContentStart + 8);

            var durations = new List<int>();
            var sizes = new List<int>();
            var entryPosition = trun.ContentStart + 12;
            for (var i = 0; i < sampleCount; i++)
            {
                durations.Add((int)ReadUInt32(stream, entryPosition));
                sizes.Add((int)ReadUInt32(stream, entryPosition + 4));
                entryPosition += 8;
            }

            var mdat = topLevel.Single(a => a.Type == "mdat");
            var mdatContent = new byte[mdat.ContentEnd - mdat.ContentStart];
            stream.Position = mdat.ContentStart;
            stream.ReadExactly(mdatContent);

            return new ParsedMediaSegment(sequenceNumber, baseMediaDecodeTime, dataOffset, moofTotalSize, durations, sizes, mdatContent);
        }

        private sealed record ParsedMediaSegment(
            long SequenceNumber,
            long BaseMediaDecodeTime,
            long DataOffset,
            long MoofTotalSize,
            List<int> SampleDurations,
            List<int> SampleSizes,
            byte[] MdatContent);
    }
}
