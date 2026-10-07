using EggEncoder.Codecs;
using System.Text;

namespace EggEncoder.Codecs.Wav
{
    public sealed class WavWriter : IAudioSink
    {
        private const int PcmFormatTag = 1;
        private const int IeeeFloatFormatTag = 3;
        private const int ALawFormatTag = 6;
        private const int MuLawFormatTag = 7;
        private const int ImaAdpcmFormatTag = 17;
        private const int MsAdpcmFormatTag = 2;
        private const int YamahaAdpcmFormatTag = 32;

        // A fixed block-align byte count (the whole multi-channel block, matching WAVEFORMATEX's own
        // nBlockAlign convention) rather than a caller-configurable one -- this project's other
        // encoders don't expose a bitrate/block-size tuning knob either, and 1024 bytes matches
        // FFmpeg's own real ADPCM_IMA_WAV encoder's default block_size (confirmed directly from its
        // adpcmenc.c source), so files this writes land on a block shape real-world tools already
        // expect. Divides evenly into a whole number of samples for both mono and stereo (the only two
        // channel counts this format supports) -- (1024 - 4*channels) * 8 / (4*channels) + 1 is exact
        // for channels in {1, 2}, with no truncation to reason about.
        private const int ImaAdpcmBlockAlignBytes = 1024;

        // Same 1024-byte choice as ImaAdpcmBlockAlignBytes, for the same reason (FFmpeg's own real
        // ADPCM encoders share one block_size default/option across every ADPCM variant, confirmed
        // from its adpcmenc.c source) -- the resulting samples-per-block differs from IMA ADPCM's own
        // since MS ADPCM's block header (7 bytes/channel) and per-byte sample density (2 samples/byte
        // mono, 1 frame/byte stereo, vs IMA's 8-sample/4-byte groups) are both genuinely different.
        private const int MsAdpcmBlockAlignBytes = 1024;

        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _isFloatFormat;
        private readonly bool _isALaw;
        private readonly bool _isMuLaw;
        private readonly bool _needsPadByte;

        private readonly bool _isImaAdpcm;
        private readonly int _adpcmSamplesPerBlock;
        private readonly ImaAdpcmDecoder.ChannelState[] _adpcmChannelStates = [];
        private int[] _adpcmPendingSamples = [];
        private int _adpcmPendingCount;

        private readonly bool _isMsAdpcm;
        private readonly int _msAdpcmSamplesPerBlock;
        private readonly MsAdpcmDecoder.ChannelState[] _msAdpcmChannelStates = [];
        private int[] _msAdpcmPendingSamples = [];
        private int _msAdpcmPendingCount;

        // Yamaha ADPCM has no block structure (see YamahaAdpcmDecoder's own doc comment), so there's
        // no pending-block buffer here, unlike IMA/MS ADPCM above -- just per-channel state carried
        // continuously for the whole stream, plus the one nibble left over whenever an odd number of
        // nibbles has been written so far (only possible for mono; stereo always writes nibbles in
        // complete pairs per frame), flushed zero-padded by Finish().
        private readonly bool _isYamahaAdpcm;
        private readonly YamahaAdpcmDecoder.ChannelState[] _yamahaChannelStates = [];
        private int _yamahaPendingNibble;
        private bool _yamahaHasPendingNibble;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private WavWriter(FileStream stream, int channels, int bitsPerSample, bool isFloatFormat, bool isALaw, bool isMuLaw, bool needsPadByte, bool isImaAdpcm, int adpcmSamplesPerBlock, bool isMsAdpcm, int msAdpcmSamplesPerBlock, bool isYamahaAdpcm)
        {
            _stream = stream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _isFloatFormat = isFloatFormat;
            _isALaw = isALaw;
            _isMuLaw = isMuLaw;
            _needsPadByte = needsPadByte;

            _isImaAdpcm = isImaAdpcm;
            _adpcmSamplesPerBlock = adpcmSamplesPerBlock;

            if (isImaAdpcm)
            {
                _adpcmChannelStates = new ImaAdpcmDecoder.ChannelState[channels];
                _adpcmPendingSamples = new int[adpcmSamplesPerBlock * channels];
            }

            _isMsAdpcm = isMsAdpcm;
            _msAdpcmSamplesPerBlock = msAdpcmSamplesPerBlock;

            if (isMsAdpcm)
            {
                _msAdpcmChannelStates = new MsAdpcmDecoder.ChannelState[channels];
                _msAdpcmPendingSamples = new int[msAdpcmSamplesPerBlock * channels];
            }

            _isYamahaAdpcm = isYamahaAdpcm;

            if (isYamahaAdpcm)
            {
                _yamahaChannelStates = new YamahaAdpcmDecoder.ChannelState[channels];
            }
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32 for <see cref="WavSampleFormat.Integer"/>; must be 32 for <see cref="WavSampleFormat.Float32"/>; must be 16 for <see cref="WavSampleFormat.MuLaw"/>/<see cref="WavSampleFormat.ALaw"/>/<see cref="WavSampleFormat.ImaAdpcm"/>/<see cref="WavSampleFormat.MsAdpcm"/>/<see cref="WavSampleFormat.YamahaAdpcm"/> (the native range their companding formula/quantizer expects, even though G.711/the ADPCM variants are always 8/4 bits on disk).</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the RIFF header's size fields are written at creation time.</param>
        /// <param name="sampleFormat">
        /// Selects the on-disk encoding (see <see cref="WavSampleFormat"/>). For <see cref="WavSampleFormat.Float32"/>,
        /// each incoming sample is still an int at this codebase's 32-bit native range (the same scale
        /// <c>WavReader</c> produces when it decodes a float WAV, and the same scale
        /// <see cref="EggEncoder.Pcm.FloatSampleConverter"/> uses), converted to an actual IEEE 754 float at
        /// write time. For <see cref="WavSampleFormat.MuLaw"/>/<see cref="WavSampleFormat.ALaw"/>, each
        /// incoming sample is at the native 16-bit range, companded to one coded byte per sample via
        /// <see cref="G711Codec"/>. For <see cref="WavSampleFormat.ImaAdpcm"/>/<see cref="WavSampleFormat.MsAdpcm"/>,
        /// mono or stereo only, each incoming sample is at the native 16-bit range and is quantized into a
        /// 4-bit nibble via <c>ImaAdpcmEncoder</c>/<c>ImaAdpcmDecoder.QuantizeNibble</c> or
        /// <c>MsAdpcmEncoder</c>/<c>MsAdpcmDecoder.CompressSample</c> respectively, buffered internally into
        /// fixed-size blocks (see their own doc comments) rather than written one sample at a time. For
        /// <see cref="WavSampleFormat.YamahaAdpcm"/>, also mono or stereo only and quantized into a 4-bit
        /// nibble via <c>YamahaAdpcmEncoder.CompressSample</c>, but unlike
        /// <see cref="WavSampleFormat.ImaAdpcm"/>/<see cref="WavSampleFormat.MsAdpcm"/> there's no block
        /// structure at all -- two samples are written per byte continuously as they arrive, with at most
        /// one trailing nibble (mono, odd frame count) held pending until <see cref="Finish"/>.
        /// </param>
        public static WavWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames, WavSampleFormat sampleFormat = WavSampleFormat.Integer)
        {
            var isFloatFormat = sampleFormat == WavSampleFormat.Float32;
            var isALaw = sampleFormat == WavSampleFormat.ALaw;
            var isMuLaw = sampleFormat == WavSampleFormat.MuLaw;
            var isImaAdpcm = sampleFormat == WavSampleFormat.ImaAdpcm;
            var isMsAdpcm = sampleFormat == WavSampleFormat.MsAdpcm;
            var isYamahaAdpcm = sampleFormat == WavSampleFormat.YamahaAdpcm;

            if (isFloatFormat && bitsPerSample != 32)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit IEEE float samples; only 32-bit IEEE float is supported");
            }

            if ((isALaw || isMuLaw) && bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; G.711 encoding requires 16-bit input samples");
            }

            if (isImaAdpcm && bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; IMA ADPCM encoding requires 16-bit input samples");
            }

            if (isImaAdpcm && channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' requests {channels} channels; IMA ADPCM encoding supports only mono and stereo");
            }

            if (isMsAdpcm && bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; MS ADPCM encoding requires 16-bit input samples");
            }

            if (isMsAdpcm && channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' requests {channels} channels; MS ADPCM encoding supports only mono and stereo");
            }

            if (isYamahaAdpcm && bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; Yamaha ADPCM encoding requires 16-bit input samples");
            }

            if (isYamahaAdpcm && channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' requests {channels} channels; Yamaha ADPCM encoding supports only mono and stereo");
            }

            if (!isFloatFormat && !isALaw && !isMuLaw && !isImaAdpcm && !isMsAdpcm && !isYamahaAdpcm && bitsPerSample is not 8 and not 16 and not 24 and not 32)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit PCM are supported");
            }

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            try
            {
                if (isImaAdpcm)
                {
                    return CreateImaAdpcm(stream, filePath, channels, sampleRate, totalFrames);
                }

                if (isMsAdpcm)
                {
                    return CreateMsAdpcm(stream, filePath, channels, sampleRate, totalFrames);
                }

                if (isYamahaAdpcm)
                {
                    return CreateYamahaAdpcm(stream, channels, sampleRate, totalFrames);
                }

                // G.711 is always 1 byte/sample on disk regardless of the 16-bit input scale its
                // companding formula expects -- the same "reported/input resolution != on-disk width"
                // gap WavReader's own IsALaw/IsMuLaw decode path bridges, just in the other direction.
                var onDiskBitsPerSample = isALaw || isMuLaw ? 8 : bitsPerSample;
                var bytesPerSample = onDiskBitsPerSample / 8;
                var blockAlign = channels * bytesPerSample;
                var dataSize = totalFrames * blockAlign;
                var needsPadByte = dataSize % 2 != 0;

                var formatTag = isALaw ? ALawFormatTag : isMuLaw ? MuLawFormatTag : isFloatFormat ? IeeeFloatFormatTag : PcmFormatTag;

                // G.711 is a non-PCM format tag, so -- matching real encoders (confirmed against real
                // ffmpeg-produced G.711 WAV files) -- its 'fmt ' chunk carries a 2-byte cbSize
                // extension (always 0 here; G.711 needs no further extension data) and is followed by
                // a 'fact' chunk giving the authoritative per-channel sample count.
                var fmtChunkPayloadSize = isALaw || isMuLaw ? 18 : 16;
                var factChunkSize = isALaw || isMuLaw ? 8 + 4 : 0;
                var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize) + (needsPadByte ? 1 : 0);

                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

                writer.Write("RIFF"u8);
                writer.Write((uint)riffSize);
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write((uint)fmtChunkPayloadSize);
                writer.Write((ushort)formatTag);
                writer.Write((ushort)channels);
                writer.Write((uint)sampleRate);
                writer.Write((uint)(sampleRate * blockAlign));
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)onDiskBitsPerSample);

                if (isALaw || isMuLaw)
                {
                    writer.Write((ushort)0); // cbSize: no further 'fmt ' extension data for G.711
                    writer.Write("fact"u8);
                    writer.Write((uint)4);
                    writer.Write((uint)totalFrames);
                }

                writer.Write("data"u8);
                writer.Write((uint)dataSize);

                return new WavWriter(stream, channels, bitsPerSample, isFloatFormat, isALaw, isMuLaw, needsPadByte, isImaAdpcm: false, adpcmSamplesPerBlock: 0, isMsAdpcm: false, msAdpcmSamplesPerBlock: 0, isYamahaAdpcm: false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // IMA ADPCM's block structure (a fixed-size header per channel, a block-align derived sample
        // count, a 'fact' chunk carrying the true per-channel frame total since the data chunk's own
        // size is block-quantized rather than exactly totalFrames * bytesPerFrame) is different enough
        // from every other WavSampleFormat's simple per-sample-width math above that it gets its own
        // method rather than threading more conditionals through the shared one.
        private static WavWriter CreateImaAdpcm(FileStream stream, string filePath, int channels, int sampleRate, long totalFrames)
        {
            const int headerBytesPerChannel = 4;
            var samplesPerBlock = ((ImaAdpcmBlockAlignBytes - (headerBytesPerChannel * channels)) * 8 / (headerBytesPerChannel * channels)) + 1;
            var blockCount = (totalFrames + samplesPerBlock - 1) / samplesPerBlock; // ceiling division -- 0 blocks for 0 frames
            var dataSize = blockCount * ImaAdpcmBlockAlignBytes;
            var needsPadByte = dataSize % 2 != 0;

            const int fmtChunkPayloadSize = 20; // base(16) + cbSize(2) + wSamplesPerBlock(2)
            const int factChunkSize = 8 + 4;
            var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize) + (needsPadByte ? 1 : 0);

            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)fmtChunkPayloadSize);
            writer.Write((ushort)ImaAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)((long)sampleRate * ImaAdpcmBlockAlignBytes / samplesPerBlock)); // nAvgBytesPerSec
            writer.Write((ushort)ImaAdpcmBlockAlignBytes);
            writer.Write((ushort)4); // wBitsPerSample -- the coded width; WavReader reports the decoded 16-bit resolution instead, the same convention it already uses for MS ADPCM/G.711
            writer.Write((ushort)2); // cbSize
            writer.Write((ushort)samplesPerBlock);
            writer.Write("fact"u8);
            writer.Write((uint)4);
            writer.Write((uint)totalFrames);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);

            return new WavWriter(stream, channels, bitsPerSample: 16, isFloatFormat: false, isALaw: false, isMuLaw: false, needsPadByte, isImaAdpcm: true, samplesPerBlock, isMsAdpcm: false, msAdpcmSamplesPerBlock: 0, isYamahaAdpcm: false);
        }

        // MS ADPCM's block structure mirrors IMA ADPCM's own CreateImaAdpcm above in shape (a
        // fixed-size header, a block-align derived sample count, a 'fact' chunk carrying the true
        // frame total), but the header itself is genuinely different: a 1-byte predictor index per
        // channel (always 0 here, see MsAdpcmEncoder's own doc comment), plus the file-level 'fmt '
        // chunk extension additionally carries the full standard 7-pair coefficient table every real
        // MS ADPCM file includes, which IMA ADPCM's own extension has no equivalent of at all.
        private static WavWriter CreateMsAdpcm(FileStream stream, string filePath, int channels, int sampleRate, long totalFrames)
        {
            const int headerBytesPerChannel = 7;
            var coeffCount = MsAdpcmEncoder.Coeff1Table.Length;

            // Mono wastes the last nibble of its final data byte when the remaining sample count is
            // odd (the same "wasted trailing nibble" pattern IMA ADPCM has) -- filling to capacity
            // here always yields an even remaining count regardless (see the class-level reasoning
            // this mirrors from ImaAdpcmBlockAlignBytes), so this encoder never actually needs to
            // reason about that odd case itself, only WavReader's own decode side does.
            var remainingBytes = MsAdpcmBlockAlignBytes - (headerBytesPerChannel * channels);
            var samplesPerBlock = channels == 1 ? 2 + (remainingBytes * 2) : 2 + remainingBytes;

            var blockCount = (totalFrames + samplesPerBlock - 1) / samplesPerBlock; // ceiling division -- 0 blocks for 0 frames
            var dataSize = blockCount * MsAdpcmBlockAlignBytes;
            var needsPadByte = dataSize % 2 != 0;

            // base(16) + cbSize(2) + wSamplesPerBlock(2) + wNumCoef(2) + coeffCount * (Coeff1(2) + Coeff2(2))
            var fmtChunkPayloadSize = 22 + (coeffCount * 4);
            const int factChunkSize = 8 + 4;
            var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize) + (needsPadByte ? 1 : 0);

            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)fmtChunkPayloadSize);
            writer.Write((ushort)MsAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)((long)sampleRate * MsAdpcmBlockAlignBytes / samplesPerBlock)); // nAvgBytesPerSec
            writer.Write((ushort)MsAdpcmBlockAlignBytes);
            writer.Write((ushort)4); // wBitsPerSample -- the coded width; WavReader reports the decoded 16-bit resolution instead
            writer.Write((ushort)(4 + (coeffCount * 4))); // cbSize: wSamplesPerBlock(2) + wNumCoef(2) + the coefficient pairs themselves
            writer.Write((ushort)samplesPerBlock);
            writer.Write((ushort)coeffCount);
            for (var i = 0; i < coeffCount; i++)
            {
                writer.Write(MsAdpcmEncoder.Coeff1Table[i]);
                writer.Write(MsAdpcmEncoder.Coeff2Table[i]);
            }

            writer.Write("fact"u8);
            writer.Write((uint)4);
            writer.Write((uint)totalFrames);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);

            return new WavWriter(stream, channels, bitsPerSample: 16, isFloatFormat: false, isALaw: false, isMuLaw: false, needsPadByte, isImaAdpcm: false, adpcmSamplesPerBlock: 0, isMsAdpcm: true, samplesPerBlock, isYamahaAdpcm: false);
        }

        // Yamaha ADPCM has no block structure at all (see YamahaAdpcmDecoder's own doc comment), so --
        // unlike CreateImaAdpcm/CreateMsAdpcm above -- the data chunk's exact size is known directly
        // from totalFrames with no block-quantization padding to reason about: two nibbles per byte,
        // rounded up by at most the one trailing zero-pad nibble mono needs for an odd total frame
        // count (stereo always has an even total nibble count, one frame per byte, so it never pads).
        // No 'fmt' chunk extension is needed either -- confirmed directly from a real ffmpeg-produced
        // fixture's own bytes, whose 'fmt' chunk carries cbSize=0 with no further extension data.
        private static WavWriter CreateYamahaAdpcm(FileStream stream, int channels, int sampleRate, long totalFrames)
        {
            var totalNibbles = totalFrames * channels;
            var dataSize = (totalNibbles + 1) / 2; // ceiling division
            var needsPadByte = dataSize % 2 != 0;

            const int fmtChunkPayloadSize = 18; // base(16) + cbSize(2)
            const int factChunkSize = 8 + 4;
            var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize) + (needsPadByte ? 1 : 0);

            // Purely informational -- this format has no real block structure to align to, so no
            // decode logic depends on this value at all (unlike IMA/MS ADPCM's own nBlockAlign, which
            // WavReader needs to size its block buffer). 4 bytes/sample-pair-of-channels at 4 bits/
            // sample is a reasonable, self-consistent choice; real-world tools only ever treat it as
            // advisory metadata for this format.
            const int blockAlign = 4;
            var byteRate = (long)sampleRate * channels / 2; // 4 bits/sample on disk

            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)fmtChunkPayloadSize);
            writer.Write((ushort)YamahaAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)byteRate);
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)4); // wBitsPerSample -- the coded width; WavReader reports the decoded 16-bit resolution instead
            writer.Write((ushort)0); // cbSize: no further 'fmt ' extension data for Yamaha ADPCM
            writer.Write("fact"u8);
            writer.Write((uint)4);
            writer.Write((uint)totalFrames);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);

            return new WavWriter(stream, channels, bitsPerSample: 16, isFloatFormat: false, isALaw: false, isMuLaw: false, needsPadByte, isImaAdpcm: false, adpcmSamplesPerBlock: 0, isMsAdpcm: false, msAdpcmSamplesPerBlock: 0, isYamahaAdpcm: true);
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            if (_isImaAdpcm)
            {
                WriteImaAdpcmInterleavedSamples(buffer, frameCount);
                return;
            }

            if (_isMsAdpcm)
            {
                WriteMsAdpcmInterleavedSamples(buffer, frameCount);
                return;
            }

            if (_isYamahaAdpcm)
            {
                WriteYamahaAdpcmInterleavedSamples(buffer, frameCount);
                return;
            }

            var bytesPerSample = _isALaw || _isMuLaw ? 1 : _bitsPerSample / 8;
            var sampleCount = frameCount * _channels;
            var byteCount = sampleCount * bytesPerSample;

            if (_rawBytes.Length < byteCount)
            {
                _rawBytes = new byte[byteCount];
            }

            for (var i = 0; i < sampleCount; i++)
            {
                var byteOffset = i * bytesPerSample;
                var sample = buffer[i];

                switch (bytesPerSample)
                {
                    case 1:
                        _rawBytes[byteOffset] = _isALaw ? G711Codec.EncodeALaw(sample)
                            : _isMuLaw ? G711Codec.EncodeMuLaw(sample)
                            : (byte)(sample + 128);
                        break;
                    case 2:
                        _rawBytes[byteOffset] = (byte)sample;
                        _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        break;
                    case 3:
                        _rawBytes[byteOffset] = (byte)sample;
                        _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        _rawBytes[byteOffset + 2] = (byte)(sample >> 16);
                        break;
                    case 4:
                        var bits = _isFloatFormat ? BitConverter.SingleToInt32Bits(Int32ToFloat32(sample)) : sample;
                        _rawBytes[byteOffset] = (byte)bits;
                        _rawBytes[byteOffset + 1] = (byte)(bits >> 8);
                        _rawBytes[byteOffset + 2] = (byte)(bits >> 16);
                        _rawBytes[byteOffset + 3] = (byte)(bits >> 24);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                }
            }

            _stream.Write(_rawBytes, 0, byteCount);
        }

        // Exact inverse of WavReader.Float32ToInt32: a sample at this codebase's 32-bit int native
        // range maps back onto the same -1.0..1.0 normalized float WavReader would have produced it from.
        private static float Int32ToFloat32(int sample) => (float)(sample / (double)int.MaxValue);

        // Buffers incoming frames into a per-block pending array (sized to _adpcmSamplesPerBlock) and
        // encodes+writes exactly one real on-disk block each time it fills, mirroring WavReader's own
        // DecodeNextMsAdpcmBlock-style pending-buffer pattern in reverse. A final, shorter-than-a-full-
        // block remainder is left pending here and only flushed (padded) by Finish().
        private void WriteImaAdpcmInterleavedSamples(int[] buffer, int frameCount)
        {
            var framesConsumed = 0;
            while (framesConsumed < frameCount)
            {
                var framesToBuffer = Math.Min(frameCount - framesConsumed, _adpcmSamplesPerBlock - _adpcmPendingCount);

                Array.Copy(buffer, framesConsumed * _channels, _adpcmPendingSamples, _adpcmPendingCount * _channels, framesToBuffer * _channels);

                _adpcmPendingCount += framesToBuffer;
                framesConsumed += framesToBuffer;

                if (_adpcmPendingCount == _adpcmSamplesPerBlock)
                {
                    EncodeAndWriteImaAdpcmBlock();
                }
            }
        }

        // Pads a short final block by repeating its own last real frame into the remaining slots --
        // never decoded back (WavReader stops reporting samples once the 'fact' chunk's true total is
        // reached, the same way it already does for ima4/MS ADPCM), so the padding value itself has no
        // effect on round-trip correctness; repeating the last frame just avoids an arbitrary jump in
        // the encoder's own adaptive state for no reason.
        private void EncodeAndWriteImaAdpcmBlock()
        {
            for (var i = _adpcmPendingCount; i < _adpcmSamplesPerBlock; i++)
            {
                Array.Copy(_adpcmPendingSamples, (_adpcmPendingCount - 1) * _channels, _adpcmPendingSamples, i * _channels, _channels);
            }

            if (_rawBytes.Length < ImaAdpcmBlockAlignBytes)
            {
                _rawBytes = new byte[ImaAdpcmBlockAlignBytes];
            }

            ImaAdpcmEncoder.EncodeBlock(_adpcmPendingSamples, _channels, _adpcmSamplesPerBlock, _adpcmChannelStates, _rawBytes.AsSpan(0, ImaAdpcmBlockAlignBytes));
            _stream.Write(_rawBytes, 0, ImaAdpcmBlockAlignBytes);

            _adpcmPendingCount = 0;
        }

        // Mirrors WriteImaAdpcmInterleavedSamples exactly, just driving MS ADPCM's own pending buffer
        // and block-align byte count instead.
        private void WriteMsAdpcmInterleavedSamples(int[] buffer, int frameCount)
        {
            var framesConsumed = 0;
            while (framesConsumed < frameCount)
            {
                var framesToBuffer = Math.Min(frameCount - framesConsumed, _msAdpcmSamplesPerBlock - _msAdpcmPendingCount);

                Array.Copy(buffer, framesConsumed * _channels, _msAdpcmPendingSamples, _msAdpcmPendingCount * _channels, framesToBuffer * _channels);

                _msAdpcmPendingCount += framesToBuffer;
                framesConsumed += framesToBuffer;

                if (_msAdpcmPendingCount == _msAdpcmSamplesPerBlock)
                {
                    EncodeAndWriteMsAdpcmBlock();
                }
            }
        }

        // Mirrors EncodeAndWriteImaAdpcmBlock exactly -- same "pad a short final block by repeating
        // its own last real frame" reasoning applies unchanged, since WavReader stops reporting
        // samples at the 'fact' chunk's true count for MS ADPCM too.
        private void EncodeAndWriteMsAdpcmBlock()
        {
            for (var i = _msAdpcmPendingCount; i < _msAdpcmSamplesPerBlock; i++)
            {
                Array.Copy(_msAdpcmPendingSamples, (_msAdpcmPendingCount - 1) * _channels, _msAdpcmPendingSamples, i * _channels, _channels);
            }

            if (_rawBytes.Length < MsAdpcmBlockAlignBytes)
            {
                _rawBytes = new byte[MsAdpcmBlockAlignBytes];
            }

            MsAdpcmEncoder.EncodeBlock(_msAdpcmPendingSamples, _channels, _msAdpcmSamplesPerBlock, _msAdpcmChannelStates, _rawBytes.AsSpan(0, MsAdpcmBlockAlignBytes));
            _stream.Write(_rawBytes, 0, MsAdpcmBlockAlignBytes);

            _msAdpcmPendingCount = 0;
        }

        // Yamaha ADPCM has no block structure, so there's no WriteImaAdpcmInterleavedSamples-style
        // pending-block buffer to drive -- just the nibble stream itself. Each sample's nibble is
        // computed directly via YamahaAdpcmEncoder.CompressSample (a closed-form formula, not a
        // search -- see its own doc comment) and packed two-per-byte in the exact order
        // YamahaAdpcmDecoder consumes them (channel = sample index % channels, matching the flat
        // interleaved buffer's own frame-major/channel-minor layout): low nibble first, high nibble
        // second. A nibble left pending across calls (only possible for mono with an odd total frame
        // count so far) is carried in _yamahaPendingNibble/_yamahaHasPendingNibble and flushed,
        // zero-padded, by Finish() below.
        private void WriteYamahaAdpcmInterleavedSamples(int[] buffer, int frameCount)
        {
            var sampleCount = frameCount * _channels;

            // +1 slack byte to always have room for a nibble carried in from a previous call plus
            // this call's own first nibble landing in the same byte.
            var maxByteCount = (sampleCount / 2) + 1;
            if (_rawBytes.Length < maxByteCount)
            {
                _rawBytes = new byte[maxByteCount];
            }

            var byteCount = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                var channel = i % _channels;
                var nibble = YamahaAdpcmEncoder.CompressSample(ref _yamahaChannelStates[channel], buffer[i]);

                if (_yamahaHasPendingNibble)
                {
                    _rawBytes[byteCount++] = (byte)(_yamahaPendingNibble | (nibble << 4));
                    _yamahaHasPendingNibble = false;
                }
                else
                {
                    _yamahaPendingNibble = nibble;
                    _yamahaHasPendingNibble = true;
                }
            }

            _stream.Write(_rawBytes, 0, byteCount);
        }

        public void Finish()
        {
            if (_isImaAdpcm && _adpcmPendingCount > 0)
            {
                EncodeAndWriteImaAdpcmBlock();
            }

            if (_isMsAdpcm && _msAdpcmPendingCount > 0)
            {
                EncodeAndWriteMsAdpcmBlock();
            }

            if (_isYamahaAdpcm && _yamahaHasPendingNibble)
            {
                _stream.WriteByte((byte)_yamahaPendingNibble);
                _yamahaHasPendingNibble = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_needsPadByte)
            {
                _stream.WriteByte(0);
            }

            _stream.Dispose();
        }
    }
}
