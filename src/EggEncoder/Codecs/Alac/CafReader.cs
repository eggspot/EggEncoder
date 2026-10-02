using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Alac
{
    // Reads a CAF (Core Audio Format) file holding ALAC-compressed audio: the 8-byte "caff" file
    // header, then a sequence of chunks (12-byte header -- 4-byte ASCII type + 8-byte big-endian
    // signed size -- followed by that many bytes of payload). Only the chunks ALAC playback actually
    // needs are interpreted ('desc', 'kuki', 'pakt', 'data'); anything else (e.g. 'free') is skipped.
    //
    // This codebase only ever produces/expects CAF files holding ALAC -- a 'desc' chunk naming any
    // other format (e.g. raw LPCM, which CAF also commonly carries) is rejected rather than decoded.
    internal sealed class CafReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int[] _packetByteSizes;

        private int _nextPacketIndex;

        private CafReader(FileStream stream, AlacSpecificConfig config, int[] packetByteSizes, long totalValidFrames, long dataChunkStart)
        {
            _stream = stream;
            _packetByteSizes = packetByteSizes;

            Config = config;
            TotalValidFrames = totalValidFrames;

            _stream.Seek(dataChunkStart, SeekOrigin.Begin);
        }

        public AlacSpecificConfig Config { get; }

        public long TotalValidFrames { get; }

        public bool HasMorePackets => _nextPacketIndex < _packetByteSizes.Length;

        public static CafReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "caff")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid CAF file: missing 'caff' header");
                }

                reader.ReadBytes(4); // mFileVersion + mFileFlags -- not needed, skipped raw

                AlacSpecificConfig? config = null;
                int[]? packetByteSizes = null;
                var totalValidFrames = 0L;
                var dataChunkStart = -1L;
                var sawDescChunk = false;

                while (stream.Position + 12 <= stream.Length)
                {
                    var chunkType = new string(reader.ReadChars(4));
                    var chunkSize = BinaryPrimitives.ReverseEndianness(reader.ReadInt64());
                    var chunkDataStart = stream.Position;

                    switch (chunkType)
                    {
                        case "desc":
                            var formatId = ParseDescChunk(reader, filePath);
                            if (formatId != "alac")
                            {
                                throw new NotSupportedException($"'{filePath}' is a CAF file holding '{formatId}' audio, not 'alac'; only ALAC-in-CAF is supported");
                            }

                            sawDescChunk = true;
                            break;
                        case "kuki":
                            var cookie = reader.ReadBytes(AlacSpecificConfig.EncodedSize);
                            config = AlacSpecificConfig.Parse(cookie);
                            break;
                        case "pakt":
                            (packetByteSizes, totalValidFrames) = ParsePaktChunk(reader);
                            break;
                        case "data":
                            reader.ReadBytes(4); // mEditCount -- not needed, skipped raw (avoids an endianness question for an unused field)
                            dataChunkStart = stream.Position;
                            break;
                    }

                    if (chunkType == "data" && chunkSize < 0)
                    {
                        // A size of -1 means "the rest of the file" -- always true for the last chunk
                        // written by this codebase's own CafWriter, so there's nothing further to skip.
                        break;
                    }

                    stream.Position = chunkDataStart + chunkSize;
                }

                if (!sawDescChunk)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'desc' chunk");
                }

                if (config is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'kuki' chunk");
                }

                if (packetByteSizes is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'pakt' chunk");
                }

                if (dataChunkStart < 0)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'data' chunk");
                }

                return new CafReader(stream, config, packetByteSizes, totalValidFrames, dataChunkStart);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public byte[] ReadNextPacket()
        {
            if (!HasMorePackets)
            {
                throw new InvalidOperationException("No more ALAC packets remain in this CAF file");
            }

            var packetBytes = new byte[_packetByteSizes[_nextPacketIndex]];
            var bytesRead = _stream.Read(packetBytes, 0, packetBytes.Length);

            if (bytesRead != packetBytes.Length)
            {
                throw new InvalidDataException("CAF 'data' chunk ended before all packets listed in 'pakt' were read");
            }

            _nextPacketIndex++;
            return packetBytes;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private static string ParseDescChunk(BinaryReader reader, string filePath)
        {
            reader.ReadBytes(8); // mSampleRate -- skipped raw; used via the cookie's own sampleRate field instead
            var formatId = new string(reader.ReadChars(4));
            reader.ReadBytes(20); // mFormatFlags + mBytesPerPacket + mFramesPerPacket + mChannelsPerFrame + mBitsPerChannel -- not needed, skipped raw

            if (formatId.Length != 4)
            {
                throw new InvalidDataException($"'{filePath}' has a malformed CAF 'desc' chunk");
            }

            return formatId;
        }

        private static (int[] PacketByteSizes, long TotalValidFrames) ParsePaktChunk(BinaryReader reader)
        {
            var numberPackets = BinaryPrimitives.ReverseEndianness(reader.ReadInt64());
            var numberValidFrames = BinaryPrimitives.ReverseEndianness(reader.ReadInt64());
            reader.ReadBytes(8); // mPrimingFrames + mRemainderFrames -- not needed, skipped raw

            var packetByteSizes = new int[numberPackets];
            for (var i = 0; i < numberPackets; i++)
            {
                packetByteSizes[i] = ReadVariableLengthQuantity(reader);
            }

            return (packetByteSizes, numberValidFrames);
        }

        private static int ReadVariableLengthQuantity(BinaryReader reader)
        {
            var value = 0;
            while (true)
            {
                var b = reader.ReadByte();
                value = (value << 7) | (b & 0x7F);

                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }
        }
    }
}
