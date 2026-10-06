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

        // A fixed block-align byte count (the whole multi-channel block, matching WAVEFORMATEX's own
        // nBlockAlign convention) rather than a caller-configurable one -- this project's other
        // encoders don't expose a bitrate/block-size tuning knob either, and 1024 bytes matches
        // FFmpeg's own real ADPCM_IMA_WAV encoder's default block_size (confirmed directly from its
        // adpcmenc.c source), so files this writes land on a block shape real-world tools already
        // expect. Divides evenly into a whole number of samples for both mono and stereo (the only two
        // channel counts this format supports) -- (1024 - 4*channels) * 8 / (4*channels) + 1 is exact
        // for channels in {1, 2}, with no truncation to reason about.
        private const int ImaAdpcmBlockAlignBytes = 1024;

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

        private byte[] _rawBytes = [];
        private bool _disposed;

        private WavWriter(FileStream stream, int channels, int bitsPerSample, bool isFloatFormat, bool isALaw, bool isMuLaw, bool needsPadByte, bool isImaAdpcm, int adpcmSamplesPerBlock)
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
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32 for <see cref="WavSampleFormat.Integer"/>; must be 32 for <see cref="WavSampleFormat.Float32"/>; must be 16 for <see cref="WavSampleFormat.MuLaw"/>/<see cref="WavSampleFormat.ALaw"/>/<see cref="WavSampleFormat.ImaAdpcm"/> (the native range their companding formula/quantizer expects, even though G.711/IMA ADPCM are always 8/4 bits on disk).</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the RIFF header's size fields are written at creation time.</param>
        /// <param name="sampleFormat">
        /// Selects the on-disk encoding (see <see cref="WavSampleFormat"/>). For <see cref="WavSampleFormat.Float32"/>,
        /// each incoming sample is still an int at this codebase's 32-bit native range (the same scale
        /// <c>WavReader</c> produces when it decodes a float WAV, and the same scale
        /// <see cref="EggEncoder.Pcm.FloatSampleConverter"/> uses), converted to an actual IEEE 754 float at
        /// write time. For <see cref="WavSampleFormat.MuLaw"/>/<see cref="WavSampleFormat.ALaw"/>, each
        /// incoming sample is at the native 16-bit range, companded to one coded byte per sample via
        /// <see cref="G711Codec"/>. For <see cref="WavSampleFormat.ImaAdpcm"/>, mono or stereo only, each
        /// incoming sample is at the native 16-bit range and is quantized into a 4-bit nibble via
        /// <c>ImaAdpcmEncoder</c>/<c>ImaAdpcmDecoder.QuantizeNibble</c>, buffered internally into fixed-size
        /// blocks (see <see cref="WavSampleFormat.ImaAdpcm"/>'s own doc comment) rather than written one
        /// sample at a time.
        /// </param>
        public static WavWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames, WavSampleFormat sampleFormat = WavSampleFormat.Integer)
        {
            var isFloatFormat = sampleFormat == WavSampleFormat.Float32;
            var isALaw = sampleFormat == WavSampleFormat.ALaw;
            var isMuLaw = sampleFormat == WavSampleFormat.MuLaw;
            var isImaAdpcm = sampleFormat == WavSampleFormat.ImaAdpcm;

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

            if (!isFloatFormat && !isALaw && !isMuLaw && !isImaAdpcm && bitsPerSample is not 8 and not 16 and not 24 and not 32)
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

                return new WavWriter(stream, channels, bitsPerSample, isFloatFormat, isALaw, isMuLaw, needsPadByte, isImaAdpcm: false, adpcmSamplesPerBlock: 0);
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

            return new WavWriter(stream, channels, bitsPerSample: 16, isFloatFormat: false, isALaw: false, isMuLaw: false, needsPadByte, isImaAdpcm: true, samplesPerBlock);
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

        public void Finish()
        {
            if (_isImaAdpcm && _adpcmPendingCount > 0)
            {
                EncodeAndWriteImaAdpcmBlock();
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
