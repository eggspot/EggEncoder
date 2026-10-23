using System.Buffers.Binary;
using System.Security.Cryptography;
using EggEncoder.Codecs.Flac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacPcmMd5Test
    {
        [Theory]
        [InlineData(8, 0)]
        [InlineData(8, 1)]
        [InlineData(16, 4095)]
        [InlineData(16, 4096)]
        [InlineData(16, 4097)]
        [InlineData(20, 9000)]
        [InlineData(24, 12289)]
        [InlineData(32, 8193)]
        public void Append_AcrossChunkBoundaries_Should_MatchTheMd5OfTheRfcPacking(int bitsPerSample, int sampleCount)
        {
            var samples = RandomSamples(sampleCount, bitsPerSample);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            FlacPcmMd5.Append(md5, samples, bitsPerSample);

            md5.GetHashAndReset().Should().Equal(MD5.HashData(ReferencePacking(samples, bitsPerSample)));
        }

        [Fact]
        public void Append_CalledPerBlock_Should_MatchOneCallOverTheWholeStream()
        {
            var samples = RandomSamples(10_000, 16);
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var perBlock = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            FlacPcmMd5.Append(whole, samples, 16);
            for (var start = 0; start < samples.Length; start += 1152)
            {
                FlacPcmMd5.Append(perBlock, samples.AsSpan(start, Math.Min(1152, samples.Length - start)), 16);
            }

            perBlock.GetHashAndReset().Should().Equal(whole.GetHashAndReset());
        }

        private static int[] RandomSamples(int count, int bitsPerSample)
        {
            var random = new Random(bitsPerSample * 31 + count);
            var samples = new int[count];
            var min = bitsPerSample == 32 ? int.MinValue : -(1 << (bitsPerSample - 1));
            var max = bitsPerSample == 32 ? int.MaxValue : (1 << (bitsPerSample - 1)) - 1;
            for (var i = 0; i < count; i++)
            {
                samples[i] = (int)random.NextInt64(min, (long)max + 1);
            }

            return samples;
        }

        // RFC 9639 section 8.2: each sample little-endian at ceil(bitsPerSample / 8) bytes.
        private static byte[] ReferencePacking(int[] samples, int bitsPerSample)
        {
            var bytesPerSample = (bitsPerSample + 7) / 8;
            var packed = new byte[samples.Length * bytesPerSample];
            Span<byte> four = stackalloc byte[4];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(four, samples[i]);
                four[..bytesPerSample].CopyTo(packed.AsSpan(i * bytesPerSample));
            }

            return packed;
        }
    }
}
