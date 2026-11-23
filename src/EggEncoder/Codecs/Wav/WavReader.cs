using System.Numerics;
using System.Runtime.InteropServices;
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

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
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

        private readonly bool _isYamahaAdpcm;
        private readonly YamahaAdpcmDecoder.ChannelState[] _yamahaChannelStates = [];
        private int _yamahaPendingByte;
        private bool _yamahaHasPendingHighNibble;
        private long _yamahaFramesProduced;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        private int[] _adpcmPendingSamples = [];
        private int _adpcmPendingOffset;
        private int _adpcmPendingCount;
        private long _adpcmFramesProduced;

        private int[] _msAdpcmPendingSamples = [];
        private int _msAdpcmPendingOffset;
        private int _msAdpcmPendingCount;
        private long _msAdpcmFramesProduced;

        private readonly IReadOnlyDictionary<string, string>? _tags;

        private WavReader(Stream stream, int channels, int sampleRate, int bitsPerSample, bool isFloatFormat, long dataChunkStart, long dataChunkLength, long totalSamples, bool isAdpcm, int adpcmBlockAlign, int adpcmSamplesPerBlock, bool isALaw, bool isMuLaw, bool isMsAdpcm, int msAdpcmBlockAlign, int msAdpcmSamplesPerBlock, short[] msAdpcmCoeff1, short[] msAdpcmCoeff2, bool isYamahaAdpcm, IReadOnlyDictionary<string, string>? tags, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
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
            _tags = tags;

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

        /// <summary>
        /// The file's 'LIST'/'INFO' metadata sub-chunks (e.g. "INAM" for title, "IART" for artist),
        /// keyed by their raw four-character chunk ID exactly as RIFF stores them -- not remapped to
        /// friendlier names, the same way <see cref="EggEncoder.Results.ProbeResult.CodecName"/>
        /// exposes "pcm_s16le" rather than a human label. Null if the file has no 'LIST'/'INFO'
        /// chunk at all (most WAV files don't carry one).
        /// </summary>
        public IReadOnlyDictionary<string, string>? Tags => _tags;

        public static WavReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return OpenCore(stream, filePath, leaveOpen: false);
        }

        /// <summary>
        /// Opens a WAV source from an already-open <see cref="Stream"/> instead of a file path --
        /// for a source that isn't a local file (an HTTP response body, blob storage, an in-memory
        /// buffer). Must be seekable: the header parse below relies on <see cref="Stream.Position"/>/
        /// <see cref="Stream.Seek"/> throughout, the same way the path-based overload's own
        /// <see cref="FileStream"/> always is. Unlike the path-based overload, this does NOT take
        /// ownership of <paramref name="stream"/> -- the caller opened it, so the caller is
        /// responsible for disposing it; this type's own <see cref="Dispose"/> leaves it open.
        /// </summary>
        public static WavReader Open(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek)
            {
                throw new ArgumentException("WavReader requires a seekable stream -- its header parse relies on Stream.Position/Seek throughout", nameof(stream));
            }

            return OpenCore(stream, "<stream>", leaveOpen: true);
        }

        private static WavReader OpenCore(Stream stream, string filePath, bool leaveOpen)
        {
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
                var dataRunsToEndOfStream = false;
                long? factChunkTotalSamples = null;
                Dictionary<string, string>? tags = null;

                while (stream.Length - stream.Position >= ChunkHeaderBytes)
                {
                    var chunkIdBytes = reader.ReadBytes(4);
                    var chunkSize = reader.ReadUInt32();
                    var chunkDataStart = stream.Position;
                    if (dataChunkFound && !IsPrintableChunkId(chunkIdBytes))
                    {
                        break;
                    }

                    var chunkId = Encoding.ASCII.GetString(chunkIdBytes);

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

                        if (formatTag == WaveFormatExtensibleTag && chunkSize >= 40)
                        {
                            reader.ReadBytes(8);
                            var subFormatTag = reader.ReadUInt16();
                            if (subFormatTag != PcmFormatTag && subFormatTag != IeeeFloatFormatTag)
                            {
                                throw new NotSupportedException($"'{filePath}' is WAVE_FORMAT_EXTENSIBLE with sub-format {subFormatTag}; only PCM and IEEE float are supported there");
                            }

                            isFloatFormat = subFormatTag == IeeeFloatFormatTag;
                        }

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
                        dataChunkFound = true;
                        if (IsUnsizedDataChunk(reader, chunkDataStart, chunkSize))
                        {
                            dataChunkLength = stream.Length - chunkDataStart;
                            dataRunsToEndOfStream = true;
                            break;
                        }

                        dataChunkLength = chunkSize;
                    }
                    else if (chunkId == "LIST")
                    {
                        var parsedTags = TryParseListInfoTags(reader, chunkDataStart, chunkSize);
                        if (parsedTags is not null)
                        {
                            tags = parsedTags;
                        }
                    }

                    var paddedChunkSize = chunkSize + (chunkSize % 2);
                    stream.Position = Math.Min(chunkDataStart + paddedChunkSize, stream.Length);
                }

                if (channels is null || sampleRate is null || bitsPerSample is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'fmt ' chunk");
                }

                if (!dataChunkFound)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'data' chunk");
                }

                if (dataRunsToEndOfStream || factChunkTotalSamples == 0)
                {
                    factChunkTotalSamples = null;
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

                    var maxSamplesPerBlock = ((adpcmBlockAlign - headerBytes) * 8 / headerBytes) + 1;
                    if (adpcmSamplesPerBlock > maxSamplesPerBlock)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={adpcmSamplesPerBlock}, but its block align of {adpcmBlockAlign} bytes can only hold {maxSamplesPerBlock}");
                    }

                    var totalSamples = factChunkTotalSamples ?? dataChunkLength / adpcmBlockAlign * adpcmSamplesPerBlock;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, totalSamples, isAdpcm: true, adpcmBlockAlign, adpcmSamplesPerBlock, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false, tags, leaveOpen);
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

                    var msAdpcmRemainingBytes = adpcmBlockAlign - msAdpcmHeaderBytes;
                    var msAdpcmMaxSamplesPerBlock = channels.Value == 1 ? 2 + (msAdpcmRemainingBytes * 2) : 2 + msAdpcmRemainingBytes;
                    if (msAdpcmSamplesPerBlock > msAdpcmMaxSamplesPerBlock)
                    {
                        throw new InvalidDataException($"'{filePath}' declares wSamplesPerBlock={msAdpcmSamplesPerBlock}, but its block align of {adpcmBlockAlign} bytes can only hold {msAdpcmMaxSamplesPerBlock}");
                    }

                    var msAdpcmTotalSamples = factChunkTotalSamples ?? dataChunkLength / adpcmBlockAlign * msAdpcmSamplesPerBlock;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, msAdpcmTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: true, msAdpcmBlockAlign: adpcmBlockAlign, msAdpcmSamplesPerBlock, msAdpcmCoeff1, msAdpcmCoeff2, isYamahaAdpcm: false, tags, leaveOpen);
                }

                if (isYamahaAdpcm)
                {
                    if (channels is not 1 and not 2)
                    {
                        throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo Yamaha ADPCM are supported");
                    }

                    var yamahaTotalSamples = factChunkTotalSamples ?? (dataChunkLength * 2) / channels.Value;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, yamahaTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: true, tags, leaveOpen);
                }

                if (isALaw || isMuLaw)
                {
                    var g711TotalSamples = factChunkTotalSamples ?? dataChunkLength / channels.Value;

                    return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample: 16, isFloatFormat: false, dataChunkStart, dataChunkLength, g711TotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw, isMuLaw, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false, tags, leaveOpen);
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

                var pcmFrameBytes = channels.Value * (bitsPerSample.Value / 8);
                dataChunkLength -= dataChunkLength % pcmFrameBytes;

                var pcmTotalSamples = dataChunkLength / pcmFrameBytes;

                return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample.Value, isFloatFormat, dataChunkStart, dataChunkLength, pcmTotalSamples, isAdpcm: false, adpcmBlockAlign: 0, adpcmSamplesPerBlock: 0, isALaw: false, isMuLaw: false, isMsAdpcm: false, msAdpcmBlockAlign: 0, msAdpcmSamplesPerBlock: 0, msAdpcmCoeff1: [], msAdpcmCoeff2: [], isYamahaAdpcm: false, tags, leaveOpen);
            }
            catch (EndOfStreamException e)
            {
                if (!leaveOpen)
                {
                    stream.Dispose();
                }

                throw new InvalidDataException($"'{filePath}' is truncated or corrupted: {e.Message}", e);
            }
            catch
            {
                if (!leaveOpen)
                {
                    stream.Dispose();
                }

                throw;
            }
        }

        private const uint UnknownDataChunkSize = 0xFFFFFFFF;
        private const int ChunkHeaderBytes = 8;

        private static bool IsPrintableChunkId(byte[] chunkId) =>
            Array.TrueForAll(chunkId, character => character is >= 0x20 and <= 0x7E);

        private static bool IsUnsizedDataChunk(BinaryReader reader, long chunkDataStart, uint chunkSize)
        {
            var available = reader.BaseStream.Length - chunkDataStart;
            if (chunkSize == UnknownDataChunkSize || chunkSize > available)
            {
                return true;
            }

            return chunkSize == 0 && available > 0 && !StartsWithWellFormedChunk(reader, chunkDataStart);
        }

        private static bool StartsWithWellFormedChunk(BinaryReader reader, long position)
        {
            var stream = reader.BaseStream;
            if (stream.Length - position < ChunkHeaderBytes)
            {
                return false;
            }

            stream.Position = position;
            var id = reader.ReadBytes(4);
            var size = reader.ReadUInt32();
            stream.Position = position;
            return IsPrintableChunkId(id) && position + ChunkHeaderBytes + size <= stream.Length;
        }

        private static Dictionary<string, string>? TryParseListInfoTags(BinaryReader reader, long chunkDataStart, uint chunkSize)
        {
            if (chunkSize < 4)
            {
                return null;
            }

            var listEnd = Math.Min(chunkDataStart + chunkSize, reader.BaseStream.Length);
            if (new string(reader.ReadChars(4)) != "INFO")
            {
                return null;
            }

            var tags = new Dictionary<string, string>();
            var stream = reader.BaseStream;

            while (stream.Position + 8 <= listEnd)
            {
                var subId = new string(reader.ReadChars(4));
                var subSize = reader.ReadUInt32();
                var subDataStart = stream.Position;

                if (subSize > int.MaxValue || subDataStart + subSize > listEnd)
                {
                    break;
                }

                var bytes = reader.ReadBytes((int)subSize);
                tags[subId] = Encoding.UTF8.GetString(bytes).TrimEnd('\0');

                var paddedSubSize = subSize + (subSize % 2);
                stream.Position = subDataStart + paddedSubSize;
            }

            return tags.Count > 0 ? tags : null;
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
            if (bytesPerSample == 2 && BitConverter.IsLittleEndian)
            {
                WidenPcm16(_rawBytes.AsSpan(0, sampleCount * 2), buffer);
                return sampleCount / Channels;
            }

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

        private static void WidenPcm16(ReadOnlySpan<byte> bytes, int[] buffer)
        {
            var samples = MemoryMarshal.Cast<byte, short>(bytes);
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var lanes = Vector<short>.Count;
                for (; i + lanes <= samples.Length; i += lanes)
                {
                    Vector.Widen(new Vector<short>(samples[i..]), out var low, out var high);
                    low.CopyTo(buffer, i);
                    high.CopyTo(buffer, i + Vector<int>.Count);
                }
            }

            for (; i < samples.Length; i++)
            {
                buffer[i] = samples[i];
            }
        }

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
                        var b = _stream.ReadByte();
                        if (b < 0)
                        {
                            _bytesRead += bytesConsumedThisCall;
                            _yamahaFramesProduced += frame;
                            return frame;
                        }

                        _yamahaPendingByte = b;
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
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
