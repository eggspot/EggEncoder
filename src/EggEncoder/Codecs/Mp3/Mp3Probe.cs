using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Probe
    {
        private const int MaxSyncSearchBytes = 65536;

        // internal (not private) so Mp3FrameEncoder can map its own sampleRate/bitRateKbps to the
        // header's bitrate-index/sample-rate-index fields via the exact same tables this probe
        // already uses to go the other direction, rather than duplicating them.
        internal static readonly int[] Mpeg1Layer3BitrateKbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1];
        internal static readonly int[] Mpeg1SampleRates = [44100, 48000, 32000, -1];

        private static readonly int[] _mpeg1Layer3BitrateKbps = Mpeg1Layer3BitrateKbps;
        private static readonly int[] _mpeg2Layer3BitrateKbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1];
        private static readonly int[] _mpeg1SampleRates = Mpeg1SampleRates;
        private static readonly int[] _mpeg2SampleRates = [22050, 24000, 16000, -1];
        private static readonly int[] _mpeg25SampleRates = [11025, 12000, 8000, -1];

        public static Mp3ProbeResult Probe(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            var tags = Id3v2Reader.ReadTags(stream);
            stream.Position = SkipId3V2Tag(stream);

            var frame = FindFirstFrame(stream)
                ?? throw new InvalidDataException($"'{filePath}' does not contain a valid MP3 frame");

            var vbrInfo = TryReadVbrHeader(stream, frame.Offset, frame.Header);
            var trailingTagSize = HasId3V1Tag(stream) ? 128 : 0;
            var audioDataLength = stream.Length - frame.Offset - trailingTagSize;

            double durationSeconds;
            int bitRateKbps;
            bool isVariableBitRate;

            if (vbrInfo is { FrameCount: > 0 } knownVbrInfo)
            {
                var samplesPerFrame = frame.Header.IsMpeg1 ? 1152 : 576;
                var totalSamples = (long)knownVbrInfo.FrameCount * samplesPerFrame;

                // The de-facto "LAME tag" encoder-delay/padding extension, when present, describes
                // exactly how much of that raw frame-count-derived sample total is lookahead/
                // flush silence rather than real audio -- see Mp3FrameEncoder.BuildInfoTagFrame's
                // own doc comment for the byte layout this was confirmed against. Subtracting it
                // here makes the reported duration match the real encoded source exactly, not just
                // approximately, for any file (ours or a real LAME/ffmpeg one) that carries it.
                if (knownVbrInfo.StartPadSamples is { } startPad && knownVbrInfo.EndPadSamples is { } endPad)
                {
                    totalSamples = Math.Max(0, totalSamples - startPad - endPad);
                }

                durationSeconds = (double)totalSamples / frame.Header.SampleRate;
                bitRateKbps = durationSeconds > 0 && knownVbrInfo.ByteCount > 0
                    ? (int)(knownVbrInfo.ByteCount * 8 / 1000 / durationSeconds)
                    : frame.Header.BitRateKbps;
                isVariableBitRate = knownVbrInfo.IsVbr;
            }
            else
            {
                durationSeconds = (double)audioDataLength * 8 / (frame.Header.BitRateKbps * 1000.0);
                bitRateKbps = frame.Header.BitRateKbps;
                isVariableBitRate = false;
            }

            return new Mp3ProbeResult
            {
                DurationInSeconds = (int)durationSeconds,
                DurationSeconds = durationSeconds,
                SampleRate = frame.Header.SampleRate,
                Channels = frame.Header.Channels,
                BitRate = bitRateKbps * 1000,
                IsVariableBitRate = isVariableBitRate,
                EncoderDelaySamples = vbrInfo?.StartPadSamples,
                EncoderPaddingSamples = vbrInfo?.EndPadSamples,
                Tags = tags
            };
        }

        private static long SkipId3V2Tag(Stream stream)
        {
            if (stream.Length < 10)
            {
                return 0;
            }

            stream.Position = 0;
            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);

            if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
            {
                return 0;
            }

            var size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            return 10 + size;
        }

        private static Mp3Frame? FindFirstFrame(Stream stream)
        {
            var searchStart = stream.Position;
            var bytesToSearch = (int)Math.Min(stream.Length - searchStart, MaxSyncSearchBytes);
            if (bytesToSearch < 4)
            {
                return null;
            }

            var buffer = new byte[bytesToSearch];
            stream.ReadExactly(buffer);

            for (var i = 0; i + 4 <= buffer.Length; i++)
            {
                var header = TryParseFrameHeader(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
                if (header is not null)
                {
                    return new Mp3Frame(header.Value, searchStart + i);
                }
            }

            return null;
        }

        private static Mp3FrameHeader? TryParseFrameHeader(byte b0, byte b1, byte b2, byte b3)
        {
            if (b0 != 0xFF || (b1 & 0xE0) != 0xE0)
            {
                return null;
            }

            var versionBits = (b1 >> 3) & 0x03;
            var layerBits = (b1 >> 1) & 0x03;

            if (versionBits == 1 || layerBits != 1)
            {
                return null;
            }

            var isMpeg1 = versionBits == 3;
            var isMpeg25 = versionBits == 0;
            var hasCrc = (b1 & 0x01) == 0;

            var bitrateIndex = (b2 >> 4) & 0x0F;
            var sampleRateIndex = (b2 >> 2) & 0x03;
            var padding = (b2 >> 1) & 0x01;

            var channelModeBits = (b3 >> 6) & 0x03;
            var channels = channelModeBits == 3 ? 1 : 2;

            var bitrateKbps = (isMpeg1 ? _mpeg1Layer3BitrateKbps : _mpeg2Layer3BitrateKbps)[bitrateIndex];
            var sampleRate = (isMpeg1 ? _mpeg1SampleRates : isMpeg25 ? _mpeg25SampleRates : _mpeg2SampleRates)[sampleRateIndex];

            if (bitrateKbps <= 0 || sampleRate <= 0)
            {
                return null;
            }

            var frameLengthBytes = ((isMpeg1 ? 144 : 72) * bitrateKbps * 1000 / sampleRate) + padding;
            var sideInfoSize = isMpeg1
                ? (channels == 1 ? 17 : 32)
                : (channels == 1 ? 9 : 17);

            return new Mp3FrameHeader(sampleRate, bitrateKbps, channels, frameLengthBytes, sideInfoSize, hasCrc, isMpeg1);
        }

        // Degrades gracefully to null -- the same "decorative/optional, stop at the first thing
        // that isn't there" convention TryReadLameTagDelayAndPadding below already established --
        // for a file whose Xing/Info flags claim a field (frame-count/byte-count/TOC/VBR-quality)
        // that the file doesn't actually have room for, not just for a header that's missing
        // entirely. Each of those four fields is individually length-guarded before being read for
        // exactly that reason: a real-world file can declare more fields present in its flags than
        // it actually contains (most commonly from truncation, but the flags and the bytes that
        // follow are both just as untrusted as any other header value here).
        private static VbrInfo? TryReadVbrHeader(Stream stream, long frameOffset, Mp3FrameHeader header)
        {
            var vbrHeaderOffset = frameOffset + 4 + (header.HasCrc ? 2 : 0) + header.SideInfoSize;
            if (vbrHeaderOffset + 8 > stream.Length)
            {
                return null;
            }

            stream.Position = vbrHeaderOffset;
            Span<byte> tagBuffer = stackalloc byte[4];
            stream.ReadExactly(tagBuffer);

            var tag = Encoding.ASCII.GetString(tagBuffer);
            if (tag != "Xing" && tag != "Info")
            {
                return null;
            }

            Span<byte> flagsBuffer = stackalloc byte[4];
            stream.ReadExactly(flagsBuffer);
            var flags = BinaryPrimitives.ReadUInt32BigEndian(flagsBuffer);

            uint frameCount = 0;
            uint byteCount = 0;

            if ((flags & 0x01) != 0)
            {
                if (stream.Position + 4 > stream.Length)
                {
                    return null;
                }

                Span<byte> frameCountBuffer = stackalloc byte[4];
                stream.ReadExactly(frameCountBuffer);
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(frameCountBuffer);
            }

            if ((flags & 0x02) != 0)
            {
                if (stream.Position + 4 > stream.Length)
                {
                    return null;
                }

                Span<byte> byteCountBuffer = stackalloc byte[4];
                stream.ReadExactly(byteCountBuffer);
                byteCount = BinaryPrimitives.ReadUInt32BigEndian(byteCountBuffer);
            }

            if ((flags & 0x04) != 0)
            {
                if (stream.Position + 100 > stream.Length)
                {
                    return null;
                }

                stream.Position += 100; // TOC (seek table) -- not surfaced by this probe
            }

            if ((flags & 0x08) != 0)
            {
                if (stream.Position + 4 > stream.Length)
                {
                    return null;
                }

                stream.Position += 4; // VBR quality -- not surfaced by this probe
            }

            var (startPad, endPad) = TryReadLameTagDelayAndPadding(stream);

            return new VbrInfo(frameCount, byteCount, tag == "Xing", startPad, endPad);
        }

        // The de-facto-standard "LAME tag" extension that immediately follows the base Xing/Info
        // fields above (an encoder short version string, several fields this probe doesn't surface,
        // and a 24-bit pair of 12-bit encoder-delay/padding sample counts) -- byte layout confirmed
        // field-for-field against FFmpeg's own real libavformat/mp3dec.c source
        // (mp3_parse_info_tag), not assumed, and cross-checked against a real ffmpeg+libmp3lame-
        // produced reference file's own raw bytes (see THIRD-PARTY-NOTICES.md). Read unconditionally
        // -- NOT gated on the version string being exactly "LAME"/"Lavf"/"Lavc" the way FFmpeg's own
        // reader gates its delay/padding parsing, since this probe has no reason to trust one
        // encoder's self-identification over another's for a field this project itself also writes
        // under its own, different version string (see Mp3FrameEncoder.BuildInfoTagFrame). Degrades
        // gracefully to (null, null) for a plain Xing/Info header with no LAME extension at all (a
        // real, valid, and fairly common case on its own) rather than throwing, the same
        // "decorative metadata, stop at the first thing that isn't there" convention this project's
        // other tag readers (WavReader.Tags, Id3v2Reader) already established.
        private const int LameExtensionBytesNeeded = 9 + 1 + 1 + 4 + 2 + 2 + 1 + 1 + 3;

        private static (int? StartPad, int? EndPad) TryReadLameTagDelayAndPadding(Stream stream)
        {
            if (stream.Position + LameExtensionBytesNeeded > stream.Length)
            {
                return (null, null);
            }

            stream.Position += 9 + 1 + 1 + 4 + 2 + 2 + 1 + 1; // version, revision/vbr, lowpass, peak, radio_rg, audiophile_rg, enc_flags, abr
            Span<byte> delaysBuffer = stackalloc byte[3];
            stream.ReadExactly(delaysBuffer);
            var delays = (delaysBuffer[0] << 16) | (delaysBuffer[1] << 8) | delaysBuffer[2];

            return (delays >> 12, delays & 0xFFF);
        }

        private static bool HasId3V1Tag(Stream stream)
        {
            if (stream.Length < 128)
            {
                return false;
            }

            stream.Position = stream.Length - 128;
            Span<byte> tag = stackalloc byte[3];
            stream.ReadExactly(tag);
            return tag[0] == (byte)'T' && tag[1] == (byte)'A' && tag[2] == (byte)'G';
        }
    }

    public class Mp3ProbeResult
    {
        public required int DurationInSeconds { get; init; }

        public required double DurationSeconds { get; init; }

        public required int SampleRate { get; init; }

        public required int Channels { get; init; }

        public required int BitRate { get; init; }

        public required bool IsVariableBitRate { get; init; }

        /// <summary>
        /// This file's own de-facto "LAME tag" encoder delay, in samples -- how much of the
        /// frame-count-derived total (every frame, including the Xing/Info header frame itself,
        /// contributing 1152 samples -- the standard LAME-tag convention this value, and
        /// <see cref="DurationSeconds"/>, both assume) is leading lookahead/warm-up silence rather
        /// than real audio. Null if the file carries no Xing/Info header, or one with no LAME-style
        /// extension (a real, valid case on its own). See
        /// <c>Mp3FrameEncoder.BuildInfoTagFrame</c>'s own doc comment for where this project's own
        /// encoder sets it.
        ///
        /// A real decoder's own raw decode doesn't always match that "every frame contributes 1152
        /// samples" assumption: some decoders (including this project's own <c>Mp3Decoder.Decode</c>,
        /// via NLayer) recognize and silently exclude the Xing/Info header frame from their own
        /// decoded output entirely, so skipping this many samples from the front of such a decoder's
        /// own raw output skips 1152 samples too many. This property reports the tag's own standard
        /// value, not an adjustment for any specific decoder's own behavior.
        /// </summary>
        public int? EncoderDelaySamples { get; init; }

        /// <summary>
        /// This file's own de-facto "LAME tag" encoder padding, in samples -- how much trailing
        /// flush silence should be discarded from the end of a raw decode, under the same "every
        /// frame contributes 1152 samples" convention <see cref="EncoderDelaySamples"/>'s own doc
        /// comment explains (including its caveat about decoders that exclude the header frame from
        /// their own raw output). Null under the same conditions <see cref="EncoderDelaySamples"/> is.
        /// </summary>
        public int? EncoderPaddingSamples { get; init; }

        public IReadOnlyDictionary<string, string>? Tags { get; init; }
    }

    internal readonly record struct Mp3FrameHeader(int SampleRate, int BitRateKbps, int Channels, int FrameLengthBytes, int SideInfoSize, bool HasCrc, bool IsMpeg1);

    internal readonly record struct Mp3Frame(Mp3FrameHeader Header, long Offset);

    internal readonly record struct VbrInfo(uint FrameCount, uint ByteCount, bool IsVbr, int? StartPadSamples, int? EndPadSamples);
}
