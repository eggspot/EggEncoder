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

        private static int Pack(ReadOnlySpan<int> samples, int bytesPerSample, byte[] chunk)
        {
            var i = 0;
            if (bytesPerSample == 2 && BitConverter.IsLittleEndian && System.Numerics.Vector.IsHardwareAccelerated)
            {
                var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(chunk.AsSpan(0, samples.Length * 2));
                var lanes = System.Numerics.Vector<int>.Count;
                for (; i + (2 * lanes) <= samples.Length; i += 2 * lanes)
                {
                    System.Numerics.Vector.Narrow(new System.Numerics.Vector<int>(samples[i..]), new System.Numerics.Vector<int>(samples[(i + lanes)..])).CopyTo(shorts[i..]);
                }
            }

            ref var output = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(chunk);
            for (; i < samples.Length; i++)
            {
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref System.Runtime.CompilerServices.Unsafe.Add(ref output, i * bytesPerSample), BitConverter.IsLittleEndian ? samples[i] : BinaryPrimitives.ReverseEndianness(samples[i]));
            }

            return samples.Length * bytesPerSample;
        }

        public static void Append(IncrementalHash md5, ReadOnlySpan<int> interleavedSamples, int bitsPerSample)
        {
            var bytesPerSample = (bitsPerSample + 7) / 8;
            var chunk = ArrayPool<byte>.Shared.Rent((ChunkSamples * bytesPerSample) + 3);
            try
            {
                while (!interleavedSamples.IsEmpty)
                {
                    var count = Math.Min(ChunkSamples, interleavedSamples.Length);
                    var length = Pack(interleavedSamples[..count], bytesPerSample, chunk);

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
