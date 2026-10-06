using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Aiff
{
    // Plain AIFF (FORM/AIFF, COMM/SSND, integer PCM) and AIFC (FORM/AIFC, the compressed/float AIFF
    // variant -- a mandatory FVER chunk plus a COMM chunk extended with a compressionType FourCC and a
    // compressionName pascal string). AIFC's compressionName is purely a human-readable label with no
    // decoding relevance -- never parsed here, just skipped over by the chunk loop's own generic
    // skip-to-next-chunk logic, the same way an entirely unknown chunk (including AIFC's own FVER) is.
    //
    // Structurally this mirrors WavReader closely: same two-pass (scan all chunks, then seek back to the
    // sample data start) approach, same reused raw-byte scratch buffer. The differences are exactly
    // AIFF's: big-endian multi-byte fields throughout (IFF, like RIFF, but the opposite byte order), an
    // 80-bit extended-float sample rate instead of a plain integer (see IeeeExtendedFloat), and -- easy to
    // get backwards -- AIFF's 8-bit samples are signed (-128..127), unlike WAV's unsigned (0..255) 8-bit
    // convention.
    //
    // AIFC compressionType coverage: 'NONE'/'twos' (big-endian PCM -- 'twos' is what real-world tools
    // such as macOS's own afconvert write for plain PCM inside an AIFC container; both mean the exact
    // same thing as plain AIFF's own PCM, verified against a real afconvert-produced fixture), 'sowt'
    // (little-endian PCM -- the classic reason AIFC exists: cross-platform-friendly byte order), 'fl32'/
    // 'fl64' (big-endian IEEE float, decoded into this codebase's usual int32-native-range scale -- see
    // Float32ToInt32/Float64ToInt32), 'alaw'/'ulaw' (G.711 companded PCM, decoded via this project's own
    // Wav.G711Codec rather than a second, duplicate implementation -- the algorithm is byte-for-byte
    // identical to WAV's G.711, only the surrounding container differs), and 'ima4' (QuickTime IMA4
    // ADPCM, decode only -- see Ima4Decoder for the full writeup of why its bitstream framing is
    // genuinely different from WAV's own IMA ADPCM, even though the per-nibble math turns out to be
    // identical and is reused directly from Wav.ImaAdpcmDecoder). Every compressionType here was
    // verified against real ffmpeg- and afconvert-produced fixture files' actual bytes, not just a
    // read-through of Apple's AIFF-C spec -- ima4 specifically was cross-checked bit-exact against both
    // ffmpeg's own decode AND macOS's afconvert/CoreAudio decode of the same real files, for both mono
    // and stereo, before any of this file's ima4-specific code was written.
    //
    // ima4's own COMM chunk quirk: numSampleFrames reports the IMA4 BLOCK count, not the raw sample
    // count every other compressionType here uses it for (confirmed exactly: a real file's SSND data
    // length, divided by 34 bytes/block for mono, equals numSampleFrames precisely) -- Open() multiplies
    // by Ima4Decoder.SamplesPerChannelSubBlock to get the true TotalSamples this reader reports.
    public sealed class AiffReader : IDisposable
    {
        private enum Compression
        {
            BigEndianInteger,
            LittleEndianInteger,
            Float32,
            Float64,
            ALaw,
            MuLaw,
            Ima4
        }

        private readonly FileStream _stream;
        private readonly long _sampleDataLength;
        private readonly Compression _compression;
        private readonly int _bytesPerDiskSample;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        // ima4's own pending-buffer decode state -- kept fully separate from the simple per-sample
        // fields above (_bytesPerDiskSample is unused/0 for ima4) the same way WavReader keeps its own
        // MS ADPCM fields separate from its simple per-sample ones: the block framing is different
        // enough that sharing fields would need a union-like abstraction for no real benefit.
        private readonly ImaAdpcmDecoder.ChannelState[] _ima4ChannelStates = [];
        private readonly int _ima4BlockGroupBytes;
        private int[] _ima4PendingSamples = [];
        private int _ima4PendingOffset;
        private int _ima4PendingCount;
        private long _ima4FramesProduced;

        private AiffReader(FileStream stream, int channels, int sampleRate, int bitsPerSample, long totalSamples, long sampleDataStart, long sampleDataLength, Compression compression, int bytesPerDiskSample)
        {
            _stream = stream;
            _sampleDataLength = sampleDataLength;
            _compression = compression;
            _bytesPerDiskSample = bytesPerDiskSample;

            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            TotalSamples = totalSamples;

            if (compression == Compression.Ima4)
            {
                _ima4ChannelStates = new ImaAdpcmDecoder.ChannelState[channels];
                _ima4BlockGroupBytes = Ima4Decoder.BytesPerChannelSubBlock * channels;
                _ima4PendingSamples = new int[Ima4Decoder.SamplesPerChannelSubBlock * channels];
            }

            _stream.Seek(sampleDataStart, SeekOrigin.Begin);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int BitsPerSample { get; }

        public long TotalSamples { get; }

        public bool IsFloatFormat => IsFloat32 || IsFloat64;

        /// <summary>True for AIFC's 'fl32' compressionType; false for everything else, including 'fl64' -- see <see cref="IsFloat64"/> to tell them apart (both report <see cref="BitsPerSample"/> as 32).</summary>
        public bool IsFloat32 => _compression == Compression.Float32;

        /// <summary>True for AIFC's 'fl64' compressionType; false for everything else, including 'fl32' -- see <see cref="IsFloat32"/> to tell them apart (both report <see cref="BitsPerSample"/> as 32, since this codebase's int[] PCM model has no 64-bit representation; 'fl64' is strictly a wider/more precise on-disk encoding of that same -1.0..1.0 range).</summary>
        public bool IsFloat64 => _compression == Compression.Float64;

        public bool IsALaw => _compression == Compression.ALaw;

        public bool IsMuLaw => _compression == Compression.MuLaw;

        /// <summary>True for AIFC's 'sowt' compressionType (little-endian PCM); false for everything else, including plain AIFF.</summary>
        public bool IsLittleEndian => _compression == Compression.LittleEndianInteger;

        /// <summary>True for AIFC's 'ima4' compressionType (QuickTime IMA4 ADPCM, decode only); false for everything else.</summary>
        public bool IsIma4 => _compression == Compression.Ima4;

        public static AiffReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "FORM")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid AIFF file: missing FORM header");
                }

                reader.ReadUInt32(); // FORM size -- not needed, every chunk carries its own size
                var formType = new string(reader.ReadChars(4));
                if (formType is not "AIFF" and not "AIFC")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid AIFF file: missing AIFF/AIFC form type");
                }

                var isAifc = formType == "AIFC";

                int? channels = null;
                long? totalSampleFrames = null;
                int? bitsPerSample = null;
                int? sampleRate = null;
                long sampleDataStart = 0;
                long sampleDataLength = 0;
                var ssndFound = false;
                var compressionType = "NONE";

                while (stream.Position + 8 <= stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                    var chunkDataStart = stream.Position;

                    if (chunkId == "COMM")
                    {
                        channels = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());
                        totalSampleFrames = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                        bitsPerSample = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());
                        sampleRate = (int)Math.Round(IeeeExtendedFloat.ToDouble(reader.ReadBytes(10)));

                        if (isAifc)
                        {
                            if (chunkSize < 22)
                            {
                                throw new InvalidDataException($"'{filePath}' is AIFC but its 'COMM' chunk is too short to carry the required compressionType");
                            }

                            compressionType = new string(reader.ReadChars(4));

                            // compressionName (a pascal string: length byte + chars + pad-to-even) follows,
                            // but it's purely a human-readable label with no decoding relevance -- never
                            // read here. The chunk loop's own generic skip-to-next-chunk logic (below)
                            // advances past it correctly regardless of how many of its bytes were read.
                        }
                    }
                    else if (chunkId == "SSND")
                    {
                        var offset = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                        reader.ReadUInt32(); // blockSize -- always 0 for every compressionType this reader supports (none of them are block-structured)
                        sampleDataStart = chunkDataStart + 8 + offset;
                        sampleDataLength = chunkSize - 8 - offset;
                        ssndFound = true;
                    }

                    var paddedChunkSize = chunkSize + (chunkSize % 2);
                    stream.Position = chunkDataStart + paddedChunkSize;
                }

                if (channels is null || totalSampleFrames is null || bitsPerSample is null || sampleRate is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'COMM' chunk");
                }

                if (!ssndFound)
                {
                    throw new InvalidDataException($"'{filePath}' is missing an 'SSND' chunk");
                }

                if (channels.Value <= 0)
                {
                    throw new InvalidDataException($"'{filePath}' declares {channels.Value} channels in its COMM chunk");
                }

                var (compression, reportedBitsPerSample, bytesPerDiskSample) = DetermineCompression(filePath, compressionType, bitsPerSample.Value);

                if (compression == Compression.Ima4 && channels.Value is not 1 and not 2)
                {
                    throw new NotSupportedException($"'{filePath}' has {channels.Value} channels; only mono and stereo ima4 are supported");
                }

                // ima4's own quirk: numSampleFrames reports the block count, not the raw sample count
                // every other compressionType uses it for -- see this file's own top-of-file comment
                // for how that was confirmed, not assumed.
                var trueTotalSamples = compression == Compression.Ima4
                    ? totalSampleFrames.Value * Ima4Decoder.SamplesPerChannelSubBlock
                    : totalSampleFrames.Value;

                return new AiffReader(stream, channels.Value, sampleRate.Value, reportedBitsPerSample, trueTotalSamples, sampleDataStart, sampleDataLength, compression, bytesPerDiskSample);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Maps an AIFC compressionType (or "NONE" for a plain, non-AIFC AIFF) to this reader's internal
        // Compression enum, the bitsPerSample this reader reports publicly (the *decoded* resolution,
        // not necessarily the coded width on disk -- the same "decoded resolution, not coded width"
        // convention WavReader already uses for IMA/MS ADPCM and G.711), and the real on-disk byte
        // width per sample.
        private static (Compression Compression, int ReportedBitsPerSample, int BytesPerDiskSample) DetermineCompression(string filePath, string compressionType, int declaredBitsPerSample)
        {
            switch (compressionType)
            {
                case "NONE":
                case "twos":
                    RequireIntegerBitsPerSample(filePath, declaredBitsPerSample);
                    return (Compression.BigEndianInteger, declaredBitsPerSample, declaredBitsPerSample / 8);

                case "sowt":
                    RequireIntegerBitsPerSample(filePath, declaredBitsPerSample);
                    return (Compression.LittleEndianInteger, declaredBitsPerSample, declaredBitsPerSample / 8);

                case "fl32":
                    if (declaredBitsPerSample != 32)
                    {
                        throw new NotSupportedException($"'{filePath}' is AIFC 'fl32' but declares {declaredBitsPerSample}-bit samples; only 32-bit is valid");
                    }

                    return (Compression.Float32, 32, 4);

                case "fl64":
                    if (declaredBitsPerSample != 64)
                    {
                        throw new NotSupportedException($"'{filePath}' is AIFC 'fl64' but declares {declaredBitsPerSample}-bit samples; only 64-bit is valid");
                    }

                    // Reports 32, not 64: the decoded int[] values live at this codebase's usual
                    // 32-bit-native-range scale (see Float64ToInt32) -- the same scale fl32 decodes
                    // into -- not a 64-bit range this project's int[] PCM model has no representation
                    // for at all. 64 is the on-disk coded width; 32 is the decoded resolution.
                    return (Compression.Float64, 32, 8);

                case "alaw":
                    RequireG711BitsPerSample(filePath, "alaw", declaredBitsPerSample);
                    return (Compression.ALaw, 16, 1);

                case "ulaw":
                    RequireG711BitsPerSample(filePath, "ulaw", declaredBitsPerSample);
                    return (Compression.MuLaw, 16, 1);

                case "ima4":
                    if (declaredBitsPerSample != 4)
                    {
                        throw new NotSupportedException($"'{filePath}' is AIFC 'ima4' but declares {declaredBitsPerSample}-bit samples; only 4-bit is valid");
                    }

                    // Reports 16, the decoded resolution -- the same "decoded resolution, not coded
                    // width" convention WavReader uses for its own IMA/MS ADPCM. BytesPerDiskSample is
                    // unused for ima4 (0): it takes the separate block-group pending-buffer path in
                    // ReadInterleavedSamples, not the simple per-sample switch this tuple's third value
                    // drives for every other compressionType.
                    return (Compression.Ima4, 16, 0);

                default:
                    throw new NotSupportedException($"'{filePath}' uses unsupported AIFC compressionType '{compressionType}'");
            }
        }

        private static void RequireIntegerBitsPerSample(string filePath, int bitsPerSample)
        {
            if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
            {
                throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit integer PCM are supported");
            }
        }

        private static void RequireG711BitsPerSample(string filePath, string compressionType, int bitsPerSample)
        {
            if (bitsPerSample != 8)
            {
                throw new NotSupportedException($"'{filePath}' is AIFC '{compressionType}' but declares {bitsPerSample}-bit samples; only 8-bit is valid");
            }
        }

        public int ReadInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            if (_compression == Compression.Ima4)
            {
                return ReadIma4InterleavedSamples(buffer, maxSamplesPerChannel);
            }

            var bytesPerSample = _bytesPerDiskSample;
            var bytesPerFrame = bytesPerSample * Channels;
            var remainingBytes = _sampleDataLength - _bytesRead;
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

                buffer[i] = _compression switch
                {
                    Compression.BigEndianInteger => DecodeBigEndianInteger(byteOffset, bytesPerSample),
                    Compression.LittleEndianInteger => DecodeLittleEndianInteger(byteOffset, bytesPerSample),
                    Compression.Float32 => Float32ToInt32(BinaryPrimitives.ReadSingleBigEndian(new ReadOnlySpan<byte>(_rawBytes, byteOffset, 4))),
                    Compression.Float64 => Float64ToInt32(BinaryPrimitives.ReadDoubleBigEndian(new ReadOnlySpan<byte>(_rawBytes, byteOffset, 8))),
                    Compression.ALaw => G711Codec.DecodeALaw(_rawBytes[byteOffset]),
                    Compression.MuLaw => G711Codec.DecodeMuLaw(_rawBytes[byteOffset]),
                    _ => throw new NotSupportedException($"Unsupported AIFC compression: {_compression}")
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

        // Every AIFF/AIFC integer bit depth is signed -- including 8-bit, unlike WAV's unsigned 8-bit
        // convention. Each case sign-extends by composing big-endian bytes into the correct native-width
        // signed type before widening to int.
        private int DecodeBigEndianInteger(int byteOffset, int bytesPerSample) => bytesPerSample switch
        {
            1 => unchecked((sbyte)_rawBytes[byteOffset]),
            2 => (short)((_rawBytes[byteOffset] << 8) | _rawBytes[byteOffset + 1]),
            3 => ((_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8)) >> 8,
            4 => (_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8) | _rawBytes[byteOffset + 3],
            _ => throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}")
        };

        // AIFC 'sowt': the exact mirror of DecodeBigEndianInteger with the byte order reversed -- same
        // sign-extension requirement, just composing least-significant-byte-first.
        private int DecodeLittleEndianInteger(int byteOffset, int bytesPerSample) => bytesPerSample switch
        {
            1 => unchecked((sbyte)_rawBytes[byteOffset]),
            2 => (short)(_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8)),
            3 => (_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16)) << 8 >> 8,
            4 => _rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16) | (_rawBytes[byteOffset + 3] << 24),
            _ => throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}")
        };

        // Mirrors the simple per-sample ReadInterleavedSamples above structurally (same pending-buffer
        // copy-out loop WavReader's own ReadMsAdpcmInterleavedSamples uses), just driving ima4's own
        // block-group pending buffer instead.
        private int ReadIma4InterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var framesWritten = 0;

            while (framesWritten < maxSamplesPerChannel)
            {
                if (_ima4PendingOffset >= _ima4PendingCount && !DecodeNextIma4BlockGroup())
                {
                    break;
                }

                var framesAvailable = _ima4PendingCount - _ima4PendingOffset;
                var framesToCopy = Math.Min(framesAvailable, maxSamplesPerChannel - framesWritten);

                Array.Copy(_ima4PendingSamples, _ima4PendingOffset * Channels, buffer, framesWritten * Channels, framesToCopy * Channels);

                _ima4PendingOffset += framesToCopy;
                framesWritten += framesToCopy;
            }

            return framesWritten;
        }

        // Reads and decodes exactly one block group (one Ima4Decoder.BytesPerChannelSubBlock-byte
        // sub-block per channel) from the stream. Stops cleanly (no exception) if TotalSamples has
        // already been reached, or if a truncated final block group doesn't have enough bytes left --
        // the same two stop conditions WavReader's own DecodeNextMsAdpcmBlock has.
        private bool DecodeNextIma4BlockGroup()
        {
            if (_ima4FramesProduced >= TotalSamples)
            {
                return false;
            }

            var remainingBytes = _sampleDataLength - _bytesRead;
            if (remainingBytes < _ima4BlockGroupBytes)
            {
                return false;
            }

            if (_rawBytes.Length < _ima4BlockGroupBytes)
            {
                _rawBytes = new byte[_ima4BlockGroupBytes];
            }

            var bytesActuallyRead = ReadFullyIma4BlockGroup();
            _bytesRead += bytesActuallyRead;

            if (bytesActuallyRead < _ima4BlockGroupBytes)
            {
                return false;
            }

            Ima4Decoder.DecodeBlockGroup(new ReadOnlySpan<byte>(_rawBytes, 0, _ima4BlockGroupBytes), Channels, _ima4ChannelStates, _ima4PendingSamples);

            var samplesThisBlockGroup = (int)Math.Min(Ima4Decoder.SamplesPerChannelSubBlock, TotalSamples - _ima4FramesProduced);
            _ima4PendingOffset = 0;
            _ima4PendingCount = samplesThisBlockGroup;
            _ima4FramesProduced += samplesThisBlockGroup;

            return true;

            int ReadFullyIma4BlockGroup()
            {
                var totalBytesRead = 0;
                while (totalBytesRead < _ima4BlockGroupBytes)
                {
                    var bytesReadThisCall = _stream.Read(_rawBytes, totalBytesRead, _ima4BlockGroupBytes - totalBytesRead);
                    if (bytesReadThisCall == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesReadThisCall;
                }

                return totalBytesRead;
            }
        }

        private static int Float32ToInt32(float sample) => Pcm.FloatSampleConverter.ClampToNativeInt32(sample);

        // Same NaN/clamp/scale convention as FloatSampleConverter.ClampToNativeInt32(float), just taking
        // a double directly rather than narrowing to float first -- fl64's whole reason to exist on disk
        // is carrying more precision than fl32, so this preserves that precision right up to the final
        // int32-native-range scale, rather than throwing half of it away with an unnecessary intermediate
        // narrowing cast.
        private static int Float64ToInt32(double sample)
        {
            if (double.IsNaN(sample))
            {
                return 0;
            }

            var clamped = Math.Clamp(sample, -1.0, 1.0);
            return (int)(clamped * int.MaxValue);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
