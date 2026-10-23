using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EggEncoder.Codecs.Flac
{
    // The STREAMINFO MD5 is defined over the decoded PCM packed little-endian at
    // ceil(bitsPerSample / 8) bytes per sample (RFC 9639 section 8.2), the same packing WavWriter
    // already uses for 24-bit -- shared between FlacDecoder (verifying it) and FlacEncoderSession
    // (computing it) so both sides agree on the exact same byte packing.
    internal static class FlacPcmMd5
    {
        // Samples are packed into a pooled chunk and hashed one chunk at a time: one AppendData call
        // per sample used to cost more than the whole rest of FLAC decode.
        private const int ChunkSamples = 4096;

        public static void Append(IncrementalHash md5, ReadOnlySpan<int> interleavedSamples, int bitsPerSample)
        {
            var bytesPerSample = (bitsPerSample + 7) / 8;
            var chunk = ArrayPool<byte>.Shared.Rent((ChunkSamples * bytesPerSample) + 3);
            try
            {
                while (!interleavedSamples.IsEmpty)
                {
                    var count = Math.Min(ChunkSamples, interleavedSamples.Length);
                    var length = 0;
                    foreach (var sample in interleavedSamples[..count])
                    {
                        // A full 4-byte write, then advance by bytesPerSample: the next write
                        // overwrites the unused high bytes. The +3 above leaves room for the last one.
                        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(length), sample);
                        length += bytesPerSample;
                    }

                    md5.AppendData(chunk, 0, length);
                    interleavedSamples = interleavedSamples[count..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
    }
}
