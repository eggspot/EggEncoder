using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// Parses FLV audio and video tag bodies: legacy <c>AUDIODATA</c> (AAC, MP3) and <c>VIDEODATA</c>
    /// (AVC, plus the widely used non-standard HEVC codec id 12 on read), and the Enhanced-FLV
    /// <c>ExVideoTagHeader</c> with FourCC codec ids (<c>avc1</c>, <c>hvc1</c>, <c>av01</c>,
    /// <c>vp09</c>). A codec or packet layout it doesn't handle yields an
    /// <see cref="FlvPacketType.Unsupported"/> body rather than an exception, so one odd tag never
    /// fails a whole stream; a body too short for its own header throws
    /// <see cref="InvalidDataException"/>. See docs/live-ingest-restream-plan.md item 2.
    /// </summary>
    public static class FlvTagBodyParser
    {
        internal const int SoundFormatMp3 = 2;
        internal const int SoundFormatAac = 10;
        internal const int SoundFormatMp3_8Khz = 14;
        internal const int VideoCodecAvc = 7;
        internal const int VideoCodecHevcLegacy = 12;

        internal const int FrameTypeKeyframe = 1;
        internal const int FrameTypeInterframe = 2;
        internal const int FrameTypeGeneratedKeyframe = 4;
        internal const int FrameTypeCommand = 5;

        internal const int ExPacketSequenceStart = 0;
        internal const int ExPacketCodedFrames = 1;
        internal const int ExPacketSequenceEnd = 2;
        internal const int ExPacketCodedFramesX = 3;
        internal const int ExPacketMetadata = 4;

        internal const uint FourCcAvc1 = 0x61766331; // "avc1"
        internal const uint FourCcHvc1 = 0x68766331; // "hvc1"
        internal const uint FourCcAv01 = 0x61763031; // "av01"
        internal const uint FourCcVp09 = 0x76703039; // "vp09"

        private static readonly int[] SoundRates = [5512, 11025, 22050, 44100];

        /// <summary>Parses one <c>VIDEODATA</c> tag body (or an RTMP video message body).</summary>
        /// <exception cref="InvalidDataException">The body is shorter than its own header.</exception>
        public static FlvTagBody ParseVideo(ReadOnlySpan<byte> body)
        {
            Require(body, 1, "video tag header");
            var first = body[0];
            return (first & 0x80) != 0 ? ParseExVideo(body) : ParseLegacyVideo(body);
        }

        /// <summary>Parses one <c>AUDIODATA</c> tag body (or an RTMP audio message body).</summary>
        /// <exception cref="InvalidDataException">The body is shorter than its own header.</exception>
        public static FlvTagBody ParseAudio(ReadOnlySpan<byte> body)
        {
            Require(body, 1, "audio tag header");
            var soundFormat = body[0] >> 4;
            var soundRate = SoundRates[(body[0] >> 2) & 0x03];
            var channels = (body[0] & 0x01) + 1;
            switch (soundFormat)
            {
                case SoundFormatAac:
                    Require(body, 2, "AAC packet type");
                    return body[1] switch
                    {
                        0 => new FlvTagBody(FlvPacketType.SequenceHeader, MediaKind.Audio, MediaCodec.Aac, true, 0, body[2..], soundRate, channels),
                        1 => new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Audio, MediaCodec.Aac, true, 0, body[2..], soundRate, channels),
                        var other => Unsupported(MediaKind.Audio, $"AAC packet type {other}")
                    };
                case SoundFormatMp3:
                    return new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Audio, MediaCodec.Mp3, true, 0, body[1..], soundRate, channels);
                case SoundFormatMp3_8Khz:
                    return new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Audio, MediaCodec.Mp3, true, 0, body[1..], 8000, channels);
                default:
                    return Unsupported(MediaKind.Audio, $"audio SoundFormat {soundFormat}");
            }
        }

        private static FlvTagBody ParseLegacyVideo(ReadOnlySpan<byte> body)
        {
            var frameType = body[0] >> 4;
            var codecId = body[0] & 0x0F;
            if (frameType == FrameTypeCommand)
            {
                return new FlvTagBody(FlvPacketType.Ignored, MediaKind.Video, null, false, 0, ReadOnlySpan<byte>.Empty);
            }

            MediaCodec codec;
            switch (codecId)
            {
                case VideoCodecAvc:
                    codec = MediaCodec.H264;
                    break;
                case VideoCodecHevcLegacy:
                    codec = MediaCodec.H265;
                    break;
                default:
                    return Unsupported(MediaKind.Video, $"video CodecID {codecId}");
            }

            Require(body, 5, "AVC video packet header");
            var compositionTime = ReadSignedInt24(body[2..]);
            var isKeyframe = IsKeyframe(frameType);
            return body[1] switch
            {
                0 => new FlvTagBody(FlvPacketType.SequenceHeader, MediaKind.Video, codec, isKeyframe, 0, body[5..]),
                1 => new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Video, codec, isKeyframe, compositionTime, body[5..]),
                2 => new FlvTagBody(FlvPacketType.SequenceEnd, MediaKind.Video, codec, isKeyframe, 0, ReadOnlySpan<byte>.Empty),
                var other => Unsupported(MediaKind.Video, $"AVC packet type {other}")
            };
        }

        private static FlvTagBody ParseExVideo(ReadOnlySpan<byte> body)
        {
            var frameType = (body[0] >> 4) & 0x07;
            var packetType = body[0] & 0x0F;
            if (packetType > ExPacketMetadata)
            {
                // MPEG2TSSequenceStart, Multitrack and ModEx (Enhanced RTMP v2) change the layout
                // that follows; none is produced by the encoders this item targets.
                return Unsupported(MediaKind.Video, $"Enhanced-FLV video packet type {packetType}");
            }

            Require(body, 5, "Enhanced-FLV video FourCC");
            var fourCc = (uint)(body[1] << 24 | body[2] << 16 | body[3] << 8 | body[4]);
            if (frameType == FrameTypeCommand || packetType == ExPacketMetadata)
            {
                return new FlvTagBody(FlvPacketType.Ignored, MediaKind.Video, null, false, 0, ReadOnlySpan<byte>.Empty);
            }

            MediaCodec codec;
            switch (fourCc)
            {
                case FourCcAvc1:
                    codec = MediaCodec.H264;
                    break;
                case FourCcHvc1:
                    codec = MediaCodec.H265;
                    break;
                case FourCcAv01:
                    codec = MediaCodec.Av1;
                    break;
                case FourCcVp09:
                    codec = MediaCodec.Vp9;
                    break;
                default:
                    return Unsupported(MediaKind.Video, $"Enhanced-FLV video FourCC 0x{fourCc:X8}");
            }

            var isKeyframe = IsKeyframe(frameType);
            switch (packetType)
            {
                case ExPacketSequenceStart:
                    return new FlvTagBody(FlvPacketType.SequenceHeader, MediaKind.Video, codec, isKeyframe, 0, body[5..]);
                case ExPacketSequenceEnd:
                    return new FlvTagBody(FlvPacketType.SequenceEnd, MediaKind.Video, codec, isKeyframe, 0, ReadOnlySpan<byte>.Empty);
                case ExPacketCodedFrames when codec is MediaCodec.H264 or MediaCodec.H265:
                    // Only AVC and HEVC carry a composition time in CodedFrames; AV1 and VP9 never reorder.
                    Require(body, 8, "Enhanced-FLV composition time");
                    return new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Video, codec, isKeyframe, ReadSignedInt24(body[5..]), body[8..]);
                default:
                    // CodedFrames for AV1/VP9, or CodedFramesX: no composition time, it is zero.
                    return new FlvTagBody(FlvPacketType.CodedFrame, MediaKind.Video, codec, isKeyframe, 0, body[5..]);
            }
        }

        private static bool IsKeyframe(int frameType) => frameType is FrameTypeKeyframe or FrameTypeGeneratedKeyframe;

        private static int ReadSignedInt24(ReadOnlySpan<byte> data)
        {
            var value = data[0] << 16 | data[1] << 8 | data[2];
            return (value << 8) >> 8;
        }

        private static FlvTagBody Unsupported(MediaKind kind, string reason) =>
            new(FlvPacketType.Unsupported, kind, null, false, 0, ReadOnlySpan<byte>.Empty, unsupportedReason: reason);

        private static void Require(ReadOnlySpan<byte> body, int length, string what)
        {
            if (body.Length < length)
            {
                throw new InvalidDataException($"FLV: tag body of {body.Length} bytes is too short for its {what}");
            }
        }
    }
}
