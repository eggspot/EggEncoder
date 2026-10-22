using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// One parsed FLV audio or video tag body (FLV specification v10.1 Annex E.4.2/E.4.3, plus the
    /// Enhanced-FLV <c>ExVideoTagHeader</c>). An RTMP audio/video message body has exactly this layout,
    /// so the RTMP server shares the parser. <see cref="Payload"/> aliases the parsed span.
    /// </summary>
    public readonly ref struct FlvTagBody
    {
        internal FlvTagBody(
            FlvPacketType packetType,
            MediaKind kind,
            MediaCodec? codec,
            bool isKeyframe,
            int compositionTime,
            ReadOnlySpan<byte> payload,
            int soundRate = 0,
            int soundChannels = 0,
            string? unsupportedReason = null)
        {
            PacketType = packetType;
            Kind = kind;
            Codec = codec;
            IsKeyframe = isKeyframe;
            CompositionTime = compositionTime;
            Payload = payload;
            SoundRate = soundRate;
            SoundChannels = soundChannels;
            UnsupportedReason = unsupportedReason;
        }

        /// <summary>What this tag carries.</summary>
        public FlvPacketType PacketType { get; }

        /// <summary>Audio or video.</summary>
        public MediaKind Kind { get; }

        /// <summary>The codec, or <c>null</c> for an <see cref="FlvPacketType.Unsupported"/> codec.</summary>
        public MediaCodec? Codec { get; }

        /// <summary>True for a video keyframe (FrameType 1, or 4 for a server-generated one) and for every audio tag.</summary>
        public bool IsKeyframe { get; }

        /// <summary>The signed composition-time offset in milliseconds (PTS minus DTS); 0 when the layout carries none.</summary>
        public int CompositionTime { get; }

        /// <summary>The codec configuration (for a sequence header) or the coded frame, with every FLV header byte removed.</summary>
        public ReadOnlySpan<byte> Payload { get; }

        /// <summary>The legacy audio header's sample-rate hint in Hz (5512, 11025, 22050 or 44100; 8000 for MP3 8 kHz); 0 for video. AAC always writes 44100 here: its real rate is in the AudioSpecificConfig.</summary>
        public int SoundRate { get; }

        /// <summary>The legacy audio header's channel hint (1 or 2); 0 for video.</summary>
        public int SoundChannels { get; }

        /// <summary>Why the tag is <see cref="FlvPacketType.Unsupported"/>; <c>null</c> otherwise.</summary>
        public string? UnsupportedReason { get; }
    }
}
