using System.Text;

namespace EggEncoder.Codecs.Wav
{
    public sealed class WavReader : IDisposable
    {
        private const int PcmFormatTag = 1;
        private const int MsAdpcmFormatTag = 2;
        private const int IeeeFloatFormatTag = 3;
        private const int ALawFormatTag = 6;
        private const int MuLawFormatTag = 7;
        private const int ImaAdpcmFormatTag = 17;
        private const int YamahaAdpcmFormatTag = 32;
        private const int WaveFormatExtensibleTag = 0xFFFE;

        private readonly FileStream _stream;
        private readonly long _dataChunkLength;
        private readonly bool _isFloatFormat;
        private readonly bool _isALaw;
        private readonly bool _isMuLaw;
        private readonly bool _isAdpcm;
        private readonly int _adpcmBlockAlign;
        private readonly int _adpcmSamplesPerBlock;
        private readonly ImaAdpcmDecoder.ChannelState[] _adpcmChannelStates = [];

        private readonly bool _isMsAdpcm;
        private readonly int _msAdpcmBlockAlign;
        private readonly int _msAdpcmSamplesPerBlock;
        private readonly short[] _msAdpcmCoeff1 = [];
        private readonly short[] _msAdpcmCoeff2 = [];
        private readonly MsAdpcmDecoder.ChannelState[] _msAdpcmChannelStates = [];

        // Yamaha ADPCM has no block structure at all (see YamahaAdpcmDecoder's own doc comment), so
        // there's no block-align/samples-per-block pair or pending-block buffer to track here, unlike
        // IMA/MS ADPCM above -- just per-channel state that carries continuously for the whole stream,
        // plus the one nibble left over whenever an odd number of nibbles has been consumed so far
        // (only possible for mono, since stereo always consumes nibbles in complete pairs per frame).
        private readonly bool _isYamahaAdpcm;
        private readonly YamahaAdpcmDecoder.ChannelState[] _yamahaChannelStates = [];
        private int _yamahaPendingByte;
        private bool _yamahaHasPendingHighNibble;
        private long _yamahaFramesProduced;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        // ADPCM decodes in whole-block units (the real block is almost never an exact multiple of the
        // caller's requested frame count), so decoded-but-not-yet-returned samples are buffered here;
        // PCM/float never populate this since they decode exactly what's requested, byte-for-byte.
        private int[] _adpcmPendingSamples = [];
        private int _adpcmPendingOffset;
        private int _adpcmPendingCount;
        private long _adpcmFramesProduced;

        // Same pending-buffer pattern as IMA ADPCM's own fields above, but kept fully separate rather
        // than shared -- the two formats' block layouts, channel-state shapes, and even coefficient
        // handling are different enough (MS ADPCM carries its own per-file coefficient table; IMA
        // ADPCM has none) that sharing fields would need a union-like abstraction for no real benefit.
        private int[] _msAdpcmPendingSamples = [];
        private int _msAdpcmPendingOffset;
        private int _msAdpcmPendingCount;
        private long _msAdpcmFramesProduced;

        private WavReader(FileStream stream, int channels, int sampleRate, int bitsPerSample, bool isFloatFormat, long dataChunkStart, long dataChunkLength, long totalSamples, bool isAdpcm, int adpcmBlockAlign, int adpcmSamplesPerBlock, bool isALaw, bool isMuLaw, bool isMsAdpcm, int msAdpcmBlockAlign, int msAdpcmSamplesPerBlock, short[] msAdpcmCoeff1, short[] msAdpcmCoeff2, bool isYamahaAdpcm)
        {
            _stream = stream;
            _dataChunkLength = dataChunkLength;
            _isFloatFormat = isFloatFormat;
            _isALaw = isALaw;
            _isMuLaw = isMuLaw;
            _isAdpcm = isAdpcm;
            _adpcmBlockAlign = adpcmBlockAlign;
            _adpcmSamplesPerBlock = adpcmSamplesPerBlock;
            _isMsAdpcm = isMsAdpcm;
            _msAdpcmBlockAlign = msAdpcmBlockAlign;
            _msAdpcmSamplesPerBlock = msAdpcmSamplesPerBlock;
            _msAdpcmCoeff1 = msAdpcmCoeff1;
            _msAdpcmCoeff2 = msAdpcmCoeff2;
            _isYamahaAdpcm = isYamahaAdpcm;

            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            TotalSamples = totalSamples;

            if (isAdpcm)
            {
                _adpcmChannelStates = new ImaAdpcmDecoder.ChannelState[channels];
                _adpcmPendingSamples = new int[adpcmSamplesPerBlock * channels];
            }

            if (isMsAdpcm)
            {
                _msAdpcmChannelStates = new MsAdpcmDecoder.ChannelState[channels];
                _msAdpcmPendingSamples = new int[msAdpcmSamplesPerBlock * channels];
            }

            if (isYamahaAdpcm)
            {
                _yamahaChannelStates = new YamahaAdpcmDecoder.ChannelState[channels];
            }

            _stream.Seek(dataChunkStart, SeekOrigin.Begin);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int BitsPerSample { get; }

        public long TotalSamples { get; }

        public bool IsFloatFormat => _isFloatFormat;

        public bool IsALaw => _isALaw;

        public bool IsMuLaw => _isMuLaw;

        public bool IsImaAdpcm => _isAdpcm;

        public bool IsMsAdpcm => _isMsAdpcm;

        public bool IsYamahaAdpcm => _isYamahaAdpcm;

        public static WavReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "RIFF")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid WAV file: missing RIFF header");
                }

                reader.ReadUInt32();
                if (new string(reader.ReadChars(4)) != "WAVE")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid WAV file: missing WAVE header");
                }

                int? channels = null;
                int? sampleRate = null;
                int? bitsPerSample = null;
                var isFloatFormat = false;
                var isALaw = false;
                var isMuLaw = false;
                var isAdpcm = false;
                var adpcmBlockAlign = 0;
                var adpcmSamplesPerBlock = 0;
                var isMsAdpcm = false;
                var msAdpcmSamplesPerBlock = 0;
                var msAdpcmCoeff1 = Array.Empty<short>();
                var msAdpcmCoeff2 = Array.Empty<short>();
                var isYamahaAdpcm = false;
                long dataChunkStart = 0;
                long dataChunkLength = 0;
                var dataChunkFound = false;
                long? factChunkTotalSamples = null;

                while (stream.Position < stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = reader.ReadUInt32();
                    var chunkDataStart = stream.Position;

                    if (chunkId == "fmt ")
                    {
                        var formatTag = reader.ReadUInt16();
                        if (formatTag != PcmFormatTag && formatTag != MsAdpcmFormatTag && formatTag != IeeeFloatFormatTag && formatTag != ALawFormatTag && formatTag != MuLawFormatTag && formatTag != ImaAdpcmFormatTag && formatTag != YamahaAdpcmFormatTag && formatTag != WaveFormatExtensibleTag)
                        {
                            throw new NotSupportedException($"'{filePath}' uses unsupported WAV format tag {formatTag}; only PCM, IEEE float, G.711 A-law/mu-law, IMA ADPCM, MS ADPCM, and Yamaha ADPCM are supported");
                        }

                        isFloatFormat = formatTag == IeeeFloatFormatTag;
                        isALaw = formatTag == ALawFormatTag;
                        isMuLaw = formatTag == MuLawFormatTag;
                        isAdpcm = formatTag == ImaAdpcmFormatTag;
                        isMsAdpcm = formatTag == MsAdpcmFormatTag;
                        isYamahaAdpcm = formatTag == YamahaAdpcmFormatTag;

                        channels = reader.ReadUInt16();
                        sampleRate = (int)reader.ReadUInt32();
                        reader.ReadUInt32();
                        adpcmBlockAlign = reader.ReadUInt16();
                        bitsPerSample = reader.ReadUInt16();

                        if (isAdpcm)
                        {
                            if (chunkSize < 20)
                            {
                                throw new InvalidDataException($"'{filePath}' is IMA ADPCM but its 'fmt ' chunk is too short to carry the required wSamplesPerBlock extension");
                            }

                            var cbSize = reader.ReadUInt16();
                            if (cbSize < 2)
                            {
                                throw new InvalidDataException($"'{filePath}' is IMA ADPCM but its 'fmt ' chunk extension is too short to carry wSamplesPerBlock");
                            }

                            adpcmSamplesPerBlock = reader.ReadUInt16();
                        }
                        else if (isMsAdpcm)
                        {
                            if (chunkSize < 22)
                            {
                                throw new InvalidDataException($"'{filePath}' is MS ADPCM but its 'fmt ' chunk is too short to carry the required coefficient table");
                            }

                            var cbSize = reader.ReadUInt16();
                            if (cbSize < 4)
                            {
                                throw new InvalidDataException($"'{filePath}' is MS ADPCM but its 'fmt ' chunk extension is too short to carry wSamplesPerBlock/wNumCoef");
                            }

                            msAdpcmSamplesPerBlock = reader.ReadUInt16();
                            var numCoef = reader.ReadUInt16();
                            if (numCoef == 0)
                            {
                                throw new InvalidDataException($"'{filePath}' is MS ADPCM but declares zero coefficient pairs");
                            }

                            if (chunkSize < 22 + (numCoef * 4))
                            {
                                throw new InvalidDataException($"'{filePath}' is MS ADPCM but its 'fmt ' chunk is too short to carry all {numCoef} declared coefficient pairs");
                            }

                            msAdpcmCoeff1 = new short[numCoef];
                            msAdpcmCoeff2 = new short[numCoef];
                            for (var i = 0; i < numCoef; i++)
                            {
                                msAdpcmCoeff1[i] = reader.ReadInt16();
                                msAdpcmCoeff2[i] = reader.ReadInt16();
                            }
                        }
                    }
                    else if (chunkId == "fact")
                    {
                        factChunkTotalSamples = reader.ReadUInt32();
                    }
                    else if (chunkId == "data")
                    {
                        dataChunkStart = chunkDataStart;
                        dataChunkLength = chunkSize;
                        dataChunkFound = true;
                    }

                    var paddedChunkSize = chunkSize + (chunkSize % 2);
                    stream.Position = chunkDataStart + paddedChunkSize;
                }

                if (channels is null || sampleRate is null || bitsPerSample is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'fmt ' chunk");
                }

                if (!dataChunkFound)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'data' chunk");
                }

                if (isAdpcm)
                {
                    if (channels is not 1 and not 2)
                    {
                        throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo IMA ADPCM are supported");
                    }

                    if (adpcmSamplesPerBlock <= 1)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={adpcmSamplesPerBlock}, which is too small to carry any real IMA ADPCM data");
                    }

                    var headerBytes = 4 * channels.Value;
                    if (adpcmBlockAlign < headerBytes)
                    {
                        throw new InvalidDataException($"'{filePath}' declares a block align of {adpcmBlockAlign} bytes, too small to hold the {headerBytes}-byte per-channel ADPCM block header");
                    }

                    // The standard IMA ADPCM relationship between block align and samples per block --
                    // a wSamplesPerBlock claiming more samples than the block's own data bytes can
                    // actually hold would run DecodeBlock's data loop past the end of its own block
                    // buffer (IndexOutOfRangeException) rather than failing cleanly here.
                    var maxSamplesPerBlock = ((adpcmBlockAlign - headerBytes) * 8 / headerBytes) + 1;
                    if (adpcmSamplesPerBlock > maxSamplesPerBlock)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={adpcmSamplesPerBlock}, but its block align of {adpcmBlockAlign} bytes can only hold {maxSamplesPerBlock}");
                    }

                    // The 'fact' chunk's own total is the authoritative sample count (the standard,
                    // recommended WAV convention for any non-PCM format) -- it's what lets a decoder
                    // trim trailing padding from the last block without guessing. Fall back to deriving
                    // it from the data chunk's own block count only if 'fact' is missing entirely.
                    var totalSamples = factChunkTotalSamples ?? dataChunkLength / adpcmBlockAlign * adpcmSamplesPerBlock;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, totalSamples, isAdpcm: true, adpcmBlockAlign, adpcmSamplesPerBlock, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false);
                }

                if (isMsAdpcm)
                {
                    if (channels is not 1 and not 2)
                    {
                        throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo MS ADPCM are supported");
                    }

                    if (msAdpcmSamplesPerBlock <= 2)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={msAdpcmSamplesPerBlock}, which is too small to carry any real MS ADPCM data beyond its own 2-sample block header");
                    }

                    var msAdpcmHeaderBytes = 7 * channels.Value;
                    if (adpcmBlockAlign < msAdpcmHeaderBytes)
                    {
                        throw new InvalidDataException($"'{filePath}' declares a block align of {adpcmBlockAlign} bytes, too small to hold the {msAdpcmHeaderBytes}-byte MS ADPCM block header");
                    }

                    // Mono wastes the last nibble of its final data byte when the remaining sample
                    // count is odd (the same "wasted trailing nibble" pattern IMA ADPCM has), so the
                    // safe upper bound doubles the remaining bytes; stereo always uses both nibbles of
                    // every byte (one per channel), so the bound is exact with no slack.
                    var msAdpcmRemainingBytes = adpcmBlockAlign - msAdpcmHeaderBytes;
                    var msAdpcmMaxSamplesPerBlock = channels.Value == 1 ? 2 + (msAdpcmRemainingBytes * 2) : 2 + msAdpcmRemainingBytes;
                    if (msAdpcmSamplesPerBlock > msAdpcmMaxSamplesPerBlock)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={msAdpcmSamplesPerBlock}, but its block align of {adpcmBlockAlign} bytes can only hold {msAdpcmMaxSamplesPerBlock}");
                    }

                    // Same 'fact' chunk convention as IMA ADPCM -- authoritative when present, falling
                    // back to the data chunk's own block count only if it's missing entirely.
                    var msAdpcmTotalSamples = factChunkTotalSamples ?? dataChunkLength / adpcmBlockAlign * msAdpcmSamplesPerBlock;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, msAdpcmTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: true, msAdpcmBlockAlign: adpcmBlockAlign, msAdpcmSamplesPerBlock, msAdpcmCoeff1, msAdpcmCoeff2, isYamahaAdpcm: false);
                }

                if (isYamahaAdpcm)
                {
                    if (channels is not 1 and not 2)
                    {
                        throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo Yamaha ADPCM are supported");
                    }

                    // No block structure, so no block-align-derived bound to check here (unlike IMA/MS
                    // ADPCM above) -- every nibble in the data chunk is real audio data (or, for mono
                    // with an odd total frame count, one trailing zero-pad nibble YamahaAdpcmEncoder's
                    // own Finish() path writes) with no larger block padding to reason about.
                    //
                    // Same 'fact' chunk convention as every other non-PCM format here -- authoritative
                    // when present; the fallback derives frames from the raw nibble count (two nibbles
                    // per byte) divided across channels, which undercounts by at most the one trailing
                    // pad nibble mono can have, matching the real on-disk data exactly whenever 'fact'
                    // is absent.
                    var yamahaTotalSamples = factChunkTotalSamples ?? (dataChunkLength * 2) / channels.Value;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, yamahaTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: true);
                }

                if (isALaw || isMuLaw)
                {
                    // Unlike every other codec here, G.711 has no structural reason to limit channel
                    // count -- it's a per-sample, per-channel companding formula with no block/frame
                    // structure at all, so any channel count decodes/encodes correctly; the mono/
                    // stereo-only limits elsewhere in this project come from the underlying native
                    // library or bitstream structure each of those codecs actually has, not from a
                    // project-wide rule.
                    //
                    // The 'fact' chunk's total is preferred when present (the standard WAV convention
                    // for a non-PCM format), but unlike IMA ADPCM there's no block padding to trim --
                    // each byte is exactly one sample, so the fallback is just the data chunk's own
                    // byte count divided evenly across channels.
                    var g711TotalSamples = factChunkTotalSamples ?? dataChunkLength / channels.Value;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, g711TotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw, isMuLaw, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false);
                }

                if (isFloatFormat)
                {
                    if (bitsPerSample != 32)
                    {
                        throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit IEEE float samples; only 32-bit IEEE float is supported");
                    }
                }
                else if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
                {
                    throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit PCM are supported");
                }

                var pcmTotalSamples = dataChunkLength / (channels.Value * (bitsPerSample.Value / 8));

                return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample.Value, isFloatFormat, dataChunkStart, dataChunkLength, pcmTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public int ReadInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            if (_isAdpcm)
            {
                return ReadAdpcmInterleavedSamples(buffer, maxSamplesPerChannel);
            }

            if (_isMsAdpcm)
            {
                return ReadMsAdpcmInterleavedSamples(buffer, maxSamplesPerChannel);
            }

            if (_isYamahaAdpcm)
            {
                return ReadYamahaAdpcmInterleavedSamples(buffer, maxSamplesPerChannel);
            }

            // G.711 always decodes to 16-bit resolution (BitsPerSample) but is only ever 1 coded byte
            // per sample on disk, regardless of that reported resolution -- the same "reported
            // decoded width != actual coded width" gap IMA ADPCM has, just without IMA ADPCM's block
            // structure requiring a whole separate buffered-decode path to bridge it.
            var bytesPerSample = _isALaw || _isMuLaw ? 1 : BitsPerSample / 8;
            var bytesPerFrame = bytesPerSample * Channels;
            var remainingBytes = _dataChunkLength - _bytesRead;
            var framesToRead = (int)Math.Min(maxSamplesPerChannel, remainingBytes / bytesPerFrame);

            if (framesToRead <= 0)
            {
                return 0;
            }

            var byteCount = framesToRead * bytesPerFrame;
            if (_rawBytes.Length < byteCount)
            {
                _rawBytes = new byte[byteCount];
            }

            var bytesActuallyRead = ReadFully();
            _bytesRead += bytesActuallyRead;

            var sampleCount = bytesActuallyRead / bytesPerSample;
            for (var i = 0; i < sampleCount; i++)
            {
                var byteOffset = i * bytesPerSample;
                buffer[i] = bytesPerSample switch
                {
                    1 => _isALaw ? G711Codec.DecodeALaw(_rawBytes[byteOffset])
                        : _isMuLaw ? G711Codec.DecodeMuLaw(_rawBytes[byteOffset])
                        : _rawBytes[byteOffset] - 128,
                    2 => (short)(_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8)),
                    3 => (_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16)) << 8 >> 8,
                    4 => _isFloatFormat
                        ? Float32ToInt32(BitConverter.Int32BitsToSingle(_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16) | (_rawBytes[byteOffset + 3] << 24)))
                        : _rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16) | (_rawBytes[byteOffset + 3] << 24),
                    _ => throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}")
                };
            }

            return sampleCount / Channels;

            int ReadFully()
            {
                var totalBytesRead = 0;
                while (totalBytesRead < byteCount)
                {
                    var bytesReadThisCall = _stream.Read(_rawBytes, totalBytesRead, byteCount - totalBytesRead);
                    if (bytesReadThisCall == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesReadThisCall;
                }

                return totalBytesRead;
            }
        }

        // Delegates to FloatSampleConverter's shared conversion so a float WAV's NaN/Infinity/
        // out-of-range samples are handled identically regardless of entry point -- see its doc comment.
        private static int Float32ToInt32(float sample) => Pcm.FloatSampleConverter.ClampToNativeInt32(sample);

        private int ReadAdpcmInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var framesWritten = 0;

            while (framesWritten < maxSamplesPerChannel)
            {
                if (_adpcmPendingOffset >= _adpcmPendingCount && !DecodeNextAdpcmBlock())
                {
                    break;
                }

                var framesAvailable = _adpcmPendingCount - _adpcmPendingOffset;
                var framesToCopy = Math.Min(framesAvailable, maxSamplesPerChannel - framesWritten);

                Array.Copy(_adpcmPendingSamples, _adpcmPendingOffset * Channels, buffer, framesWritten * Channels, framesToCopy * Channels);

                _adpcmPendingOffset += framesToCopy;
                framesWritten += framesToCopy;
            }

            return framesWritten;
        }

        // Decodes exactly one more whole block from the stream into _adpcmPendingSamples, returning
        // false once there's nothing left to decode (end of data chunk, the authoritative TotalSamples
        // has already been reached, or the remaining bytes don't even add up to one complete block --
        // the last of which only protects against a malformed/truncated file: every real encoder pads
        // its own final block to the full block size and relies on TotalSamples, not a short read, to
        // signal where the real audio ends -- confirmed against a real ffmpeg-produced file, whose
        // final block is fully present but mostly trailing padding beyond its own 'fact' chunk total).
        private bool DecodeNextAdpcmBlock()
        {
            if (_adpcmFramesProduced >= TotalSamples)
            {
                return false;
            }

            var remainingBytes = _dataChunkLength - _bytesRead;
            if (remainingBytes < _adpcmBlockAlign)
            {
                return false;
            }

            if (_rawBytes.Length < _adpcmBlockAlign)
            {
                _rawBytes = new byte[_adpcmBlockAlign];
            }

            var bytesActuallyRead = ReadFullyAdpcmBlock();
            _bytesRead += bytesActuallyRead;

            if (bytesActuallyRead < _adpcmBlockAlign)
            {
                return false;
            }

            var samplesThisBlock = (int)Math.Min(_adpcmSamplesPerBlock, TotalSamples - _adpcmFramesProduced);

            ImaAdpcmDecoder.DecodeBlock(new ReadOnlySpan<byte>(_rawBytes, 0, _adpcmBlockAlign), Channels, _adpcmSamplesPerBlock, _adpcmChannelStates, _adpcmPendingSamples);

            _adpcmPendingOffset = 0;
            _adpcmPendingCount = samplesThisBlock;
            _adpcmFramesProduced += samplesThisBlock;

            return true;

            int ReadFullyAdpcmBlock()
            {
                var totalBytesRead = 0;
                while (totalBytesRead < _adpcmBlockAlign)
                {
                    var bytesReadThisCall = _stream.Read(_rawBytes, totalBytesRead, _adpcmBlockAlign - totalBytesRead);
                    if (bytesReadThisCall == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesReadThisCall;
                }

                return totalBytesRead;
            }
        }

        // Mirrors ReadAdpcmInterleavedSamples exactly, just driving the MS ADPCM pending buffer instead.
        private int ReadMsAdpcmInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var framesWritten = 0;

            while (framesWritten < maxSamplesPerChannel)
            {
                if (_msAdpcmPendingOffset >= _msAdpcmPendingCount && !DecodeNextMsAdpcmBlock())
                {
                    break;
                }

                var framesAvailable = _msAdpcmPendingCount - _msAdpcmPendingOffset;
                var framesToCopy = Math.Min(framesAvailable, maxSamplesPerChannel - framesWritten);

                Array.Copy(_msAdpcmPendingSamples, _msAdpcmPendingOffset * Channels, buffer, framesWritten * Channels, framesToCopy * Channels);

                _msAdpcmPendingOffset += framesToCopy;
                framesWritten += framesToCopy;
            }

            return framesWritten;
        }

        // Mirrors DecodeNextAdpcmBlock exactly (same stop conditions: TotalSamples already reached,
        // or not enough bytes left for one whole block), just calling MsAdpcmDecoder.DecodeBlock with
        // its own coefficient table instead.
        private bool DecodeNextMsAdpcmBlock()
        {
            if (_msAdpcmFramesProduced >= TotalSamples)
            {
                return false;
            }

            var remainingBytes = _dataChunkLength - _bytesRead;
            if (remainingBytes < _msAdpcmBlockAlign)
            {
                return false;
            }

            if (_rawBytes.Length < _msAdpcmBlockAlign)
            {
                _rawBytes = new byte[_msAdpcmBlockAlign];
            }

            var bytesActuallyRead = ReadFullyMsAdpcmBlock();
            _bytesRead += bytesActuallyRead;

            if (bytesActuallyRead < _msAdpcmBlockAlign)
            {
                return false;
            }

            var samplesThisBlock = (int)Math.Min(_msAdpcmSamplesPerBlock, TotalSamples - _msAdpcmFramesProduced);

            MsAdpcmDecoder.DecodeBlock(new ReadOnlySpan<byte>(_rawBytes, 0, _msAdpcmBlockAlign), Channels, _msAdpcmSamplesPerBlock, _msAdpcmChannelStates, _msAdpcmCoeff1, _msAdpcmCoeff2, _msAdpcmPendingSamples);

            _msAdpcmPendingOffset = 0;
            _msAdpcmPendingCount = samplesThisBlock;
            _msAdpcmFramesProduced += samplesThisBlock;

            return true;

            int ReadFullyMsAdpcmBlock()
            {
                var totalBytesRead = 0;
                while (totalBytesRead < _msAdpcmBlockAlign)
                {
                    var bytesReadThisCall = _stream.Read(_rawBytes, totalBytesRead, _msAdpcmBlockAlign - totalBytesRead);
                    if (bytesReadThisCall == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesReadThisCall;
                }

                return totalBytesRead;
            }
        }

        // Yamaha ADPCM has no block structure (see YamahaAdpcmDecoder's own doc comment), so unlike
        // ReadAdpcmInterleavedSamples/ReadMsAdpcmInterleavedSamples there's no pending-block buffer
        // here -- just the one leftover high nibble carried across calls whenever an odd number of
        // nibbles has been consumed so far (only possible for mono; stereo always consumes nibbles in
        // complete pairs per frame). Bounds the frame count to decode by both the 'fact'-chunk-
        // authoritative remaining total AND the bytes actually left in the data chunk (plus any
        // already-buffered pending nibble), so a truncated/malformed file degrades to returning fewer
        // frames rather than reading past the data chunk.
        private int ReadYamahaAdpcmInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var remainingBytes = _dataChunkLength - _bytesRead;
            var availableNibbles = (remainingBytes * 2) + (_yamahaHasPendingHighNibble ? 1 : 0);
            var framesAvailable = availableNibbles / Channels;
            var framesToRead = (int)Math.Min(Math.Min(maxSamplesPerChannel, TotalSamples - _yamahaFramesProduced), framesAvailable);

            if (framesToRead <= 0)
            {
                return 0;
            }

            var bytesConsumedThisCall = 0;

            for (var frame = 0; frame < framesToRead; frame++)
            {
                for (var channel = 0; channel < Channels; channel++)
                {
                    int nibble;
                    if (_yamahaHasPendingHighNibble)
                    {
                        nibble = _yamahaPendingByte >> 4;
                        _yamahaHasPendingHighNibble = false;
                    }
                    else
                    {
                        _yamahaPendingByte = _stream.ReadByte();
                        bytesConsumedThisCall++;
                        nibble = _yamahaPendingByte & 0x0F;
                        _yamahaHasPendingHighNibble = true;
                    }

                    buffer[(frame * Channels) + channel] = YamahaAdpcmDecoder.ExpandNibble(ref _yamahaChannelStates[channel], nibble);
                }
            }

            _bytesRead += bytesConsumedThisCall;
            _yamahaFramesProduced += framesToRead;

            return framesToRead;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
