using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Aiff
{
    // Plain AIFF (FORM/AIFF, COMM/SSND, integer PCM) and AIFC (FORM/AIFC) -- see AiffReader's doc
    // comment for the full compressionType coverage (and what's deliberately not covered: 'ima4').
    // Mirrors WavWriter structurally: same fixed-size-header-written-at-Create-time constraint (so,
    // like WavWriter, this needs an exact totalFrames up front -- AudioCutter.Pipeline.cs's
    // DeferredFixedHeaderSink pattern applies here too), same reused raw-byte scratch buffer, same
    // per-bit-depth write switch. The differences are AIFF's: big-endian throughout (except AIFC's own
    // 'sowt'), an 80-bit extended-float sample rate (see IeeeExtendedFloat), and signed (not WAV's
    // unsigned) 8-bit samples.
    //
    // AiffSampleFormat.Integer (the default) writes exactly what this type has always written -- a
    // plain FORM/AIFF container, no FVER chunk, an 18-byte COMM. Every other AiffSampleFormat value
    // writes a FORM/AIFC container instead (FVER + an extended COMM carrying the compressionType),
    // with the real-world standard empty compressionName (verified against real ffmpeg-produced AIFC
    // fixtures: an encoder is not expected to populate this field, only to leave room for it).
    public sealed class AiffWriter : IAudioSink
    {
        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _bytesPerDiskSample;
        private readonly AiffSampleFormat _sampleFormat;
        private readonly bool _needsPadByte;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private AiffWriter(FileStream stream, int channels, int bytesPerDiskSample, AiffSampleFormat sampleFormat, bool needsPadByte)
        {
            _stream = stream;
            _channels = channels;
            _bytesPerDiskSample = bytesPerDiskSample;
            _sampleFormat = sampleFormat;
            _needsPadByte = needsPadByte;
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32.</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the FORM/COMM/SSND chunk size fields are written at creation time.</param>
        public static AiffWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            return Create(filePath, channels, sampleRate, bitsPerSample, totalFrames, AiffSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Create(string, int, int, int, long)"/>, but additionally selects the
        /// on-disk sample representation (see <see cref="AiffSampleFormat"/>). <see cref="AiffSampleFormat.Integer"/>
        /// (the default) is byte-for-byte what the four-parameter overload always wrote; every other
        /// value writes a FORM/AIFC container instead, regardless of the destination file's own exact
        /// extension spelling among <c>.aiff</c>/<c>.aif</c>/<c>.aifc</c>.
        /// </summary>
        public static AiffWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames, AiffSampleFormat sampleFormat)
        {
            var (compressionType, commSampleSize, bytesPerDiskSample) = DetermineAifcEncoding(filePath, sampleFormat, bitsPerSample);
            var isAifc = sampleFormat != AiffSampleFormat.Integer;

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            try
            {
                var blockAlign = channels * bytesPerDiskSample;
                var dataSize = totalFrames * blockAlign;
                var needsPadByte = dataSize % 2 != 0;

                // Base COMM payload (channels(2) + numSampleFrames(4) + sampleSize(2) + sampleRate(10))
                // is 18 bytes for plain AIFF; AIFC adds compressionType(4) + an empty compressionName
                // pascal string (length byte(1) + 0 chars + 1 pad byte to keep that string's own total
                // even) = 6 more bytes, for 24 total -- verified against a real afconvert-produced AIFC
                // fixture's own COMM chunk bytes.
                var commChunkSize = isAifc ? 24 : 18;
                var ssndChunkSize = 8 + dataSize; // offset(4) + blockSize(4) + data (SSND's own declared size never includes its pad byte, matching WavWriter's "data" chunk convention)
                var fverChunkTotalSize = isAifc ? 8 + 4 : 0; // unlike commChunkSize/ssndChunkSize (payload only), this already includes FVER's own 8-byte chunk header since there's no other usage site that needs it separated out
                var formSize = 4 + fverChunkTotalSize + (8 + commChunkSize) + (8 + ssndChunkSize) + (needsPadByte ? 1 : 0); // 4 = "AIFF"/"AIFC" form type; FORM's size *does* include the pad byte, matching WavWriter's RIFF size

                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

                writer.Write("FORM"u8);
                WriteUInt32BigEndian(writer, (uint)formSize);
                writer.Write(isAifc ? "AIFC"u8 : "AIFF"u8);

                if (isAifc)
                {
                    writer.Write("FVER"u8);
                    WriteUInt32BigEndian(writer, 4);
                    WriteUInt32BigEndian(writer, 0xA2805140); // AIFF-C format version timestamp, the one real encoders always write
                }

                writer.Write("COMM"u8);
                WriteUInt32BigEndian(writer, (uint)commChunkSize);
                WriteInt16BigEndian(writer, (short)channels);
                WriteUInt32BigEndian(writer, (uint)totalFrames);
                WriteInt16BigEndian(writer, (short)commSampleSize);
                Span<byte> sampleRateBytes = stackalloc byte[10];
                IeeeExtendedFloat.FromDouble(sampleRate, sampleRateBytes);
                writer.Write(sampleRateBytes);

                if (isAifc)
                {
                    writer.Write(Encoding.ASCII.GetBytes(compressionType));
                    writer.Write((byte)0); // compressionName length (empty)
                    writer.Write((byte)0); // pad byte (1 + 0 is odd)
                }

                writer.Write("SSND"u8);
                WriteUInt32BigEndian(writer, (uint)ssndChunkSize);
                WriteUInt32BigEndian(writer, 0); // offset
                WriteUInt32BigEndian(writer, 0); // blockSize -- always 0 here; none of this writer's compressionTypes are block-structured

                return new AiffWriter(stream, channels, bytesPerDiskSample, sampleFormat, needsPadByte);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Maps (sampleFormat, bitsPerSample) to the compressionType FourCC to write, the COMM chunk's
        // own sampleSize field (the *coded* width on disk -- 8 for G.711, 64 for fl64 -- which can
        // differ from the logical bitsPerSample the int[] pipeline is handing in, the exact inverse of
        // AiffReader.DetermineCompression's "reported resolution vs coded width" split), and the real
        // on-disk byte width per sample. AiffSampleFormat.Integer never reaches the AIFC-specific cases
        // below (Create's isAifc check short-circuits it to the plain-AIFF path first), but still needs
        // its own bitsPerSample validated here rather than skipped.
        private static (string CompressionType, int CommSampleSize, int BytesPerDiskSample) DetermineAifcEncoding(string filePath, AiffSampleFormat sampleFormat, int bitsPerSample)
        {
            switch (sampleFormat)
            {
                case AiffSampleFormat.Integer:
                case AiffSampleFormat.LittleEndianInteger:
                    if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
                    {
                        throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit integer PCM are supported");
                    }

                    return (sampleFormat == AiffSampleFormat.Integer ? "NONE" : "sowt", bitsPerSample, bitsPerSample / 8);

                case AiffSampleFormat.Float32:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 32);
                    return ("fl32", 32, 4);

                case AiffSampleFormat.Float64:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 32);
                    return ("fl64", 64, 8);

                case AiffSampleFormat.ALaw:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 16);
                    return ("alaw", 8, 1);

                case AiffSampleFormat.MuLaw:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 16);
                    return ("ulaw", 8, 1);

                default:
                    throw new NotSupportedException($"Unsupported {nameof(AiffSampleFormat)}: {sampleFormat}");
            }
        }

        private static void RequireBitsPerSample(string filePath, AiffSampleFormat sampleFormat, int bitsPerSample, int required)
        {
            if (bitsPerSample != required)
            {
                throw new NotSupportedException($"'{filePath}' requests {nameof(AiffSampleFormat)}.{sampleFormat} but {bitsPerSample}-bit samples; only {required}-bit is valid");
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var bytesPerSample = _bytesPerDiskSample;
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

                switch (_sampleFormat)
                {
                    case AiffSampleFormat.Integer:
                        WriteBigEndianInteger(byteOffset, bytesPerSample, sample);
                        break;
                    case AiffSampleFormat.LittleEndianInteger:
                        WriteLittleEndianInteger(byteOffset, bytesPerSample, sample);
                        break;
                    case AiffSampleFormat.Float32:
                        BinaryPrimitives.WriteSingleBigEndian(new Span<byte>(_rawBytes, byteOffset, 4), Int32ToFloat32(sample));
                        break;
                    case AiffSampleFormat.Float64:
                        BinaryPrimitives.WriteDoubleBigEndian(new Span<byte>(_rawBytes, byteOffset, 8), Int32ToFloat64(sample));
                        break;
                    case AiffSampleFormat.ALaw:
                        _rawBytes[byteOffset] = G711Codec.EncodeALaw(sample);
                        break;
                    case AiffSampleFormat.MuLaw:
                        _rawBytes[byteOffset] = G711Codec.EncodeMuLaw(sample);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported {nameof(AiffSampleFormat)}: {_sampleFormat}");
                }
            }

            _stream.Write(_rawBytes, 0, byteCount);
        }

        // Big-endian, and signed even at 8-bit -- the exact mirror of AiffReader's DecodeBigEndianInteger.
        private void WriteBigEndianInteger(int byteOffset, int bytesPerSample, int sample)
        {
            switch (bytesPerSample)
            {
                case 1:
                    _rawBytes[byteOffset] = unchecked((byte)sample);
                    break;
                case 2:
                    _rawBytes[byteOffset] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 1] = (byte)sample;
                    break;
                case 3:
                    _rawBytes[byteOffset] = (byte)(sample >> 16);
                    _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 2] = (byte)sample;
                    break;
                case 4:
                    _rawBytes[byteOffset] = (byte)(sample >> 24);
                    _rawBytes[byteOffset + 1] = (byte)(sample >> 16);
                    _rawBytes[byteOffset + 2] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 3] = (byte)sample;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
            }
        }

        // AIFC 'sowt': the exact mirror of WriteBigEndianInteger with the byte order reversed.
        private void WriteLittleEndianInteger(int byteOffset, int bytesPerSample, int sample)
        {
            switch (bytesPerSample)
            {
                case 1:
                    _rawBytes[byteOffset] = unchecked((byte)sample);
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
                    _rawBytes[byteOffset] = (byte)sample;
                    _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 2] = (byte)(sample >> 16);
                    _rawBytes[byteOffset + 3] = (byte)(sample >> 24);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
            }
        }

        // Exact inverse of AiffReader.Float32ToInt32: a sample at this codebase's 32-bit int native
        // range maps back onto the same -1.0..1.0 normalized float AiffReader would have produced it from.
        private static float Int32ToFloat32(int sample) => (float)(sample / (double)int.MaxValue);

        // Exact inverse of AiffReader.Float64ToInt32 -- no intermediate float narrowing, so a Float64
        // destination carries the full double precision this codebase's scale conversion can produce,
        // not just whatever Int32ToFloat32 would have (even though the *source* int[] value itself is
        // never wider than this codebase's usual 32-bit native range either way).
        private static double Int32ToFloat64(int sample) => sample / (double)int.MaxValue;

        public void Finish()
        {
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

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteInt16BigEndian(BinaryWriter writer, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
