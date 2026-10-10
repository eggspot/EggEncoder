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
        public static void Append(IncrementalHash md5, ReadOnlySpan<int> interleavedSamples, int bitsPerSample)
        {
            var bytesPerSample = (bitsPerSample + 7) / 8;
            Span<byte> packed = stackalloc byte[4];
            foreach (var sample in interleavedSamples)
            {
                BinaryPrimitives.WriteInt32LittleEndian(packed, sample);
                md5.AppendData(packed[..bytesPerSample]);
            }
        }
    }
}
