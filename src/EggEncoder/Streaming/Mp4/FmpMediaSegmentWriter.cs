using EggEncoder.Codecs.Mov;

namespace EggEncoder.Streaming.Mp4
{
    /// <summary>
    /// Builds one fragmented MP4 (fMP4/CMAF) media segment (<c>moof</c>+<c>mdat</c>) at a time for a
    /// single mono AAC-LC audio track -- the counterpart to <see cref="FmpInitSegmentWriter"/>'s own
    /// once-per-rendition init segment. Stateful across calls: <c>sequence_number</c> and
    /// <c>baseMediaDecodeTime</c> both carry forward from one call to the next, which is what makes
    /// segment N+1 play seamlessly after segment N with no gap or overlap -- build a fresh instance
    /// per rendition, don't share one across renditions. Field values confirmed against a real
    /// ffmpeg-produced CMAF fMP4 fixture's own bytes. See docs/streaming-plan.md item 2.
    /// </summary>
    public sealed class FmpMediaSegmentWriter
    {
        private const int TrackId = 1;
        private const uint DefaultBaseIsMoof = 0x020000;
        private const uint TfdtVersion1 = 0x01000000;
        private const uint TrunDataOffsetDurationSizePresent = 0x000301;

        private uint _nextSequenceNumber = 1;
        private long _cumulativeSampleCount;

        /// <exception cref="ArgumentException"><paramref name="frames"/> is empty.</exception>
        public byte[] BuildMediaSegment(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            ArgumentNullException.ThrowIfNull(frames);
            if (frames.Count == 0)
            {
                throw new ArgumentException("A media segment must contain at least one frame.", nameof(frames));
            }

            var moof = new Mp4BoxBuilder();

            var moofSizeFieldOffset = moof.Length;
            moof.WriteUInt32(0); // moof size -- patched once the real total is known
            moof.WriteFourCc("moof");

            moof.WriteBox("mfhd", BuildMfhdContent());

            var trafSizeFieldOffset = moof.Length;
            moof.WriteUInt32(0); // traf size -- patched below
            moof.WriteFourCc("traf");
            moof.WriteBox("tfhd", BuildTfhdContent());
            moof.WriteBox("tfdt", BuildTfdtContent());

            var trunSizeFieldOffset = moof.Length;
            moof.WriteUInt32(0); // trun size -- patched below
            moof.WriteFourCc("trun");
            moof.WriteUInt32(TrunDataOffsetDurationSizePresent);
            moof.WriteUInt32((uint)frames.Count);
            var dataOffsetFieldOffset = moof.Length;
            moof.WriteUInt32(0); // data_offset -- patched once moof's own total size is known
            foreach (var frame in frames)
            {
                moof.WriteUInt32((uint)frame.SampleCount);
                moof.WriteUInt32((uint)frame.Payload.Length);
            }

            moof.PatchUInt32(trunSizeFieldOffset, (uint)(moof.Length - trunSizeFieldOffset));
            moof.PatchUInt32(trafSizeFieldOffset, (uint)(moof.Length - trafSizeFieldOffset));
            moof.PatchUInt32(moofSizeFieldOffset, (uint)(moof.Length - moofSizeFieldOffset));

            // default-base-is-moof (tfhd) + data-offset-present (trun): the first sample's file
            // position is moof's own total size (the base) plus this offset, and the first sample
            // immediately follows mdat's own 8-byte box header.
            moof.PatchUInt32(dataOffsetFieldOffset, (uint)(moof.Length - moofSizeFieldOffset + 8));

            var segment = new Mp4BoxBuilder();
            segment.WriteBytes(moof.ToArray());
            segment.WriteBox("mdat", BuildMdatContent(frames));

            _cumulativeSampleCount += SumSampleCounts(frames);
            _nextSequenceNumber++;

            return segment.ToArray();
        }

        private byte[] BuildMfhdContent()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(0) + flags
                .WriteUInt32(_nextSequenceNumber)
                .ToArray();
        }

        private static byte[] BuildTfhdContent()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(DefaultBaseIsMoof) // version(0) + flags
                .WriteUInt32(TrackId)
                .ToArray();
        }

        private byte[] BuildTfdtContent()
        {
            return new Mp4BoxBuilder()
                .WriteUInt32(TfdtVersion1) // version(1) + flags(0)
                .WriteUInt64((ulong)_cumulativeSampleCount)
                .ToArray();
        }

        private static byte[] BuildMdatContent(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            var totalLength = 0;
            foreach (var frame in frames)
            {
                totalLength += frame.Payload.Length;
            }

            var content = new byte[totalLength];
            var offset = 0;
            foreach (var frame in frames)
            {
                frame.Payload.CopyTo(content, offset);
                offset += frame.Payload.Length;
            }

            return content;
        }

        private static long SumSampleCounts(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            var total = 0L;
            foreach (var frame in frames)
            {
                total += frame.SampleCount;
            }

            return total;
        }
    }
}
