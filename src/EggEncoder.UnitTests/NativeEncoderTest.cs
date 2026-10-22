using System.Text;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Codecs.Wma;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EggEncoder.UnitTests
{
    public class NativeEncoderTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _imaAdpcmWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono.wav");
        private static readonly string _msAdpcmWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav");
        private static readonly string _yamahaAdpcmWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono.wav");
        private static readonly string _muLawWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono.wav");
        private static readonly string _aLawWavFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_alaw_mono.wav");
        private static readonly string _movFixturePath = Path.GetFullPath("Codecs/Mov/test.mov");
        private static readonly string _mp4FixturePath = Path.GetFullPath("Codecs/Mov/test.mp4");
        private static readonly string _aacFixturePath = Path.GetFullPath("Codecs/Aac/tone_mono.aac");
        private static readonly string _wmaFixturePath = Path.GetFullPath("Codecs/Wma/tone_mono.wma");

        private readonly Mock<ILogger<NativeEncoder>> _logger = new();

        private readonly NativeEncoder _nativeEncoder;

        public NativeEncoderTest()
        {
            _nativeEncoder = new NativeEncoder(_logger.Object);
        }

        [Fact]
        public async Task Probe_WavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_wavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_wavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_s16le");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(2);
            probeResult.ChannelLayout.Should().Be("stereo");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.TimeBase.Should().Be("1/44100");
            // This fixture is itself ffmpeg-produced and carries a real 'LIST'/'INFO' chunk -- a
            // software tag ffmpeg writes into every WAV it encodes.
            probeResult.Tags.Should().NotBeNull();
            probeResult.Tags!.Should().ContainKey("ISFT");
            probeResult.IntegratedLoudness.Should().NotBeNull().And.BeGreaterThan(double.NegativeInfinity);
        }

        [Fact]
        public async Task Probe_WavFile_IntegratedLoudness_Should_Match_The_ReferenceConformanceSignal()
        {
            // The official ITU-R BS.1770-4 conformance test signal, round-tripped through the full
            // Probe pipeline (not just WaveformCalculator directly, which WaveformCalculatorTest
            // already covers) -- confirms NativeEncoder.ProbeWav actually wires sampleRate through
            // and surfaces the result, not just that the underlying calculator works in isolation.
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                const int sampleRate = 48000;
                const double peakDbfs = -23.0;
                var amplitude = Math.Pow(10, peakDbfs / 20.0) * short.MaxValue;
                var frameCount = sampleRate * 2;
                var interleaved = new int[frameCount * 2];
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var sample = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * 997 * frame / sampleRate));
                    interleaved[frame * 2] = sample;
                    interleaved[(frame * 2) + 1] = sample;
                }

                var wavPath = Path.Combine(tempDirectory, "reference.wav");
                WavFileBuilder.Create(wavPath, channels: 2, sampleRate, bitsPerSample: 16, interleaved);

                var probeResult = await _nativeEncoder.Probe(wavPath);

                probeResult.IntegratedLoudness.Should().NotBeNull();
                probeResult.IntegratedLoudness!.Value.Should().BeApproximately(peakDbfs, 0.1);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_WavFormat_Should_Return_Same_Metadata_As_Path_Based_Probe()
        {
            using var stream = File.OpenRead(_wavFixturePath);
            var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Wav);

            AssertNonEmptyWaveform(probeResult.Waveform);
            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_wavFixturePath).Length);
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(2);
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.Tags.Should().NotBeNull();
            probeResult.Tags!.Should().ContainKey("ISFT");
        }

        [Fact]
        public async Task Probe_Stream_FlacFormat_Should_Return_Correct_Metadata()
        {
            var bytes = FlacFileBuilder.BuildConstantFrame(channels: 1, bitsPerSample: 8, sampleRate: 8000, blockSize: 16, constantValuePerChannel: [42]);
            using var stream = new MemoryStream(bytes);

            var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Flac);

            probeResult.FormatName.Should().Be("flac");
            probeResult.CodecName.Should().Be("flac");
            probeResult.SampleRate.Should().Be(8000);
            probeResult.Channels.Should().Be(1);
            probeResult.BitsPerSample.Should().Be(8);
            probeResult.DurationInSamples.Should().Be(16);
        }

        [Fact]
        public async Task Probe_Stream_WithNullSource_Should_Throw()
        {
            var act = async () => await _nativeEncoder.Probe((Stream)null!, AudioFormat.Wav);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task Probe_Stream_WithUnsupportedFormat_Should_ThrowNotSupportedException()
        {
            // Every defined AudioFormat is wired up for the Stream-based overload now -- an
            // undefined value should still fail clearly rather than silently misbehaving.
            using var stream = new MemoryStream();
            var act = async () => await _nativeEncoder.Probe(stream, (AudioFormat)999);
            await act.Should().ThrowAsync<NotSupportedException>();
        }

        [Theory]
        [InlineData("cbr128.mp3")]
        [InlineData("vbr_q4.mp3")]
        [InlineData("no_xing.mp3")]
        public async Task Probe_Stream_Mp3Format_Should_Return_Same_Result_As_Path_Based_Probe(string fixtureName)
        {
            var fixturePath = Path.GetFullPath($"Codecs/Mp3/{fixtureName}");
            var pathResult = await _nativeEncoder.Probe(fixturePath);

            using var stream = File.OpenRead(fixturePath);
            var streamResult = await _nativeEncoder.Probe(stream, AudioFormat.Mp3);

            AssertNonEmptyWaveform(streamResult.Waveform);
            streamResult.Should().BeEquivalentTo(pathResult);
            stream.CanRead.Should().BeTrue("Probe(Stream) must not dispose the caller's stream");
        }

        [Fact]
        public async Task Probe_Stream_Mp3Format_NonSeekable_Should_Return_Same_Result_As_Path_Based_Probe()
        {
            // Unlike every other format's own non-seekable case (SizeBytes = 0, "unknown"), MP3
            // reads the whole source into memory up front, so its byte count is genuinely known.
            var fixturePath = Path.GetFullPath("Codecs/Mp3/cbr128.mp3");
            var pathResult = await _nativeEncoder.Probe(fixturePath);

            using var stream = new NonSeekableStream(File.ReadAllBytes(fixturePath));
            var streamResult = await _nativeEncoder.Probe(stream, AudioFormat.Mp3);

            streamResult.Should().BeEquivalentTo(pathResult);
        }

        [Fact]
        public async Task Probe_Stream_Mp3Format_SeekableNotAtStart_Should_Decode_From_Current_Position_And_Report_Full_Stream_Length()
        {
            // Matches every other format's own SizeBytes convention (GetStreamLength: the seekable
            // stream's full Length), while still probing/decoding from the current position.
            var fixturePath = Path.GetFullPath("Codecs/Mp3/cbr128.mp3");
            var pathResult = await _nativeEncoder.Probe(fixturePath);

            var prefix = new byte[100];
            using var stream = new MemoryStream([.. prefix, .. File.ReadAllBytes(fixturePath)]);
            stream.Position = prefix.Length;

            var streamResult = await _nativeEncoder.Probe(stream, AudioFormat.Mp3);

            streamResult.Should().BeEquivalentTo(pathResult, options => options.Excluding(result => result.SizeBytes));
            streamResult.SizeBytes.Should().Be(stream.Length);
        }

        [Fact]
        public async Task Probe_Stream_Mp3Format_WithNoMp3Frame_Should_Throw_InvalidDataException()
        {
            using var stream = new MemoryStream(new byte[64]);

            var act = async () => await _nativeEncoder.Probe(stream, AudioFormat.Mp3);

            await act.Should().ThrowAsync<InvalidDataException>();
        }

        [Fact]
        public async Task Probe_Stream_AiffFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var aiffPath = Path.Combine(tempDirectory, "source.aiff");
                AiffFileBuilder.Create(aiffPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1000, -1000, 500]);

                using var stream = new MemoryStream(File.ReadAllBytes(aiffPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Aiff);

                probeResult.FormatName.Should().Be("aiff");
                probeResult.SampleRate.Should().Be(8000);
                probeResult.Channels.Should().Be(1);
                probeResult.BitsPerSample.Should().Be(16);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_AuFormat_WithCorruptedChannelCount_Should_ThrowInvalidDataException()
        {
            // Bytes a fuzzing pass produced from a real A-law fixture: one corrupted channels byte
            // (0x27000001 channels). Before AuReader capped the channel count, Probe(Stream) tried to
            // allocate one loudness filter per channel and exhausted memory (crashing the test host).
            var bytes = new byte[24 + 64];
            ".snd"u8.CopyTo(bytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 24);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 64);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 27);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 44100);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 0x27000001);
            using var stream = new MemoryStream(bytes);

            var act = () => _nativeEncoder.Probe(stream, AudioFormat.Au);

            await act.Should().ThrowExactlyAsync<InvalidDataException>().WithMessage("*channels*");
        }

        [Fact]
        public async Task Probe_Stream_AuFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var auPath = Path.Combine(tempDirectory, "source.au");
                AuFileBuilder.Create(auPath, channels: 1, sampleRate: 8000, encoding: 3, [0, 0, 3, 232]);

                using var stream = new MemoryStream(File.ReadAllBytes(auPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Au);

                probeResult.FormatName.Should().Be("au");
                probeResult.SampleRate.Should().Be(8000);
                probeResult.Channels.Should().Be(1);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_AacFormat_Should_Return_Correct_Metadata()
        {
            using var stream = new MemoryStream(File.ReadAllBytes(_aacFixturePath));
            var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Aac);

            probeResult.FormatName.Should().Be("aac");
            probeResult.CodecName.Should().Be("aac");
            probeResult.SampleRate.Should().BePositive();
            probeResult.ChannelLayout.Should().Be("mono");
        }

        [Fact]
        public async Task Probe_Stream_WavPackFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var wvPath = Path.Combine(tempDirectory, "source.wv");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1000, -1000, 500]);
                WavPackEncoder.Encode(wavPath, wvPath);

                using var stream = new MemoryStream(File.ReadAllBytes(wvPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.WavPack);

                probeResult.FormatName.Should().Be("wv");
                probeResult.CodecName.Should().Be("wavpack");
                probeResult.SampleRate.Should().Be(8000);
                probeResult.Channels.Should().Be(1);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_WavFile_WithListInfoChunk_Should_Surface_Tags()
        {
            // End-to-end: confirms NativeEncoder.Probe actually wires WavReader.Tags through to
            // ProbeResult.Tags, not just that WavReader itself parses them correctly (covered
            // directly in WavReaderTest).
            var filePath = Path.GetTempFileName() + ".wav"; // NativeEncoder.Probe dispatches by extension
            try
            {
                File.WriteAllBytes(filePath, BuildMinimalWavWithListInfoTags("My Title", "My Artist"));

                var probeResult = await _nativeEncoder.Probe(filePath);

                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!["INAM"].Should().Be("My Title");
                probeResult.Tags!["IART"].Should().Be("My Artist");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static byte[] BuildMinimalWavWithListInfoTags(string title, string artist)
        {
            byte[] InfoSubChunk(string id, string value)
            {
                var valueBytes = Encoding.UTF8.GetBytes(value + "\0");
                var padding = valueBytes.Length % 2 == 0 ? [] : new byte[] { 0 };
                return [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes((uint)valueBytes.Length), .. valueBytes, .. padding];
            }

            byte[] listPayload = [.. Encoding.ASCII.GetBytes("INFO"), .. InfoSubChunk("INAM", title), .. InfoSubChunk("IART", artist)];
            var listPadding = listPayload.Length % 2 == 0 ? Array.Empty<byte>() : new byte[] { 0 };
            const int dataSize = 4;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write("RIFF"u8);
            writer.Write((uint)(4 + (8 + 16) + (8 + listPayload.Length + listPadding.Length) + (8 + dataSize)));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((uint)8000);
            writer.Write((uint)16000);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write("LIST"u8);
            writer.Write((uint)listPayload.Length);
            writer.Write(listPayload);
            writer.Write(listPadding);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);

            return ms.ToArray();
        }

        [Fact]
        public async Task Probe_WavFile_Should_Return_PeakAmplitude_And_RmsLevel_Matching_An_Independent_Decode()
        {
            // Cross-checks Probe's new PeakAmplitude/RmsLevel fields against values computed
            // independently here from the exact same fixture's own decoded samples (via WavReader
            // directly, not WaveformCalculator), rather than trusting a hand-picked expected
            // constant -- if NativeEncoder ever stopped actually feeding every real sample into the
            // same WaveformCalculator instance it reads these back from, this would catch it.
            var probeResult = await _nativeEncoder.Probe(_wavFixturePath);

            using var reader = WavReader.Open(_wavFixturePath);
            var buffer = new int[reader.TotalSamples * reader.Channels];
            reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

            var maxAmplitude = 1L << (reader.BitsPerSample - 1);
            var expectedPeak = (double)buffer.Max(sample => Math.Abs((long)sample)) / maxAmplitude;
            var expectedRms = Math.Sqrt(buffer.Average(sample => (double)sample * sample)) / maxAmplitude;

            probeResult.PeakAmplitude.Should().BeApproximately(expectedPeak, 1e-9);
            probeResult.RmsLevel.Should().BeApproximately(expectedRms, 1e-9);
        }

        [Fact]
        public async Task Probe_ImaAdpcmWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_imaAdpcmWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_imaAdpcmWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_ima_wav");
            probeResult.CodecLongName.Should().Be("ADPCM IMA WAV");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(88200);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_MsAdpcmWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_msAdpcmWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_msAdpcmWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_ms");
            probeResult.CodecLongName.Should().Be("ADPCM Microsoft");
            probeResult.SampleRate.Should().Be(22050);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(44100);
            probeResult.TimeBase.Should().Be("1/22050");
        }

        [Fact]
        public async Task Probe_YamahaAdpcmWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_yamahaAdpcmWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_yamahaAdpcmWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_yamaha");
            probeResult.CodecLongName.Should().Be("ADPCM Yamaha");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(88200);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_MuLawWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_muLawWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_muLawWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_mulaw");
            probeResult.CodecLongName.Should().Be("PCM mu-law / G.711 mu-law");
            probeResult.SampleRate.Should().Be(8000);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(16000);
            probeResult.TimeBase.Should().Be("1/8000");
        }

        [Fact]
        public async Task Probe_ALawWavFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var probeResult = await _nativeEncoder.Probe(_aLawWavFixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("wav");
            probeResult.SizeBytes.Should().Be(new FileInfo(_aLawWavFixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_alaw");
            probeResult.CodecLongName.Should().Be("PCM A-law / G.711 A-law");
            probeResult.SampleRate.Should().Be(8000);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(16000);
            probeResult.TimeBase.Should().Be("1/8000");
        }

        [Fact]
        public async Task Probe_AiffFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var aiffPath = Path.Combine(tempDirectory, "source.aiff");
                var samples = Enumerable.Range(0, 44100 * 2).SelectMany(frame => new[] { frame % 1000, -(frame % 1000) }).ToArray();
                AiffFileBuilder.Create(aiffPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, samples);

                var probeResult = await _nativeEncoder.Probe(aiffPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("aiff");
                probeResult.SizeBytes.Should().Be(new FileInfo(aiffPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("pcm_s16be");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.ChannelLayout.Should().Be("stereo");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
                probeResult.Tags.Should().BeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_AiffFile_WithTags_Should_Surface_Tags()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var aiffPath = Path.Combine(tempDirectory, "tagged.aiff");
                var tags = new Dictionary<string, string> { ["NAME"] = "Title", ["AUTH"] = "Artist" };

                using (var writer = AiffWriter.Create(aiffPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: 2, tags))
                {
                    writer.WriteInterleavedSamples([0, 1000], 2);
                }

                var probeResult = await _nativeEncoder.Probe(aiffPath);

                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!.Should().ContainKey("NAME").WhoseValue.Should().Be("Title");
                probeResult.Tags.Should().ContainKey("AUTH").WhoseValue.Should().Be("Artist");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_AifcFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var fixturePath = Path.GetFullPath("Codecs/Aiff/fixture_fl32_mono.aifc");

            var probeResult = await _nativeEncoder.Probe(fixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("aiff", "ffprobe itself reports 'aiff' as the format_name for a real AIFC file too -- it's the same container family, not a distinct format");
            probeResult.SizeBytes.Should().Be(new FileInfo(fixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(0.1, 0.01);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_f32be");
            probeResult.CodecLongName.Should().Be("PCM 32-bit floating point big-endian");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(32);
            probeResult.DurationInSamples.Should().Be(4410);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_Ima4AifcFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var fixturePath = Path.GetFullPath("Codecs/Aiff/fixture_ima4_mono.aifc");

            var probeResult = await _nativeEncoder.Probe(fixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("aiff");
            probeResult.SizeBytes.Should().Be(new FileInfo(fixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(0.2, 0.01);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("adpcm_ima_qt");
            probeResult.CodecLongName.Should().Be("ADPCM IMA QuickTime");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16, "ima4 decodes to 16-bit PCM resolution regardless of its own 4-bit coded storage width");
            probeResult.DurationInSamples.Should().Be(8832);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_AuFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var fixturePath = Path.GetFullPath("Codecs/Au/fixture_mulaw_mono.au");

            var probeResult = await _nativeEncoder.Probe(fixturePath);

            AssertNonEmptyWaveform(probeResult.Waveform);

            probeResult.FormatName.Should().Be("au");
            probeResult.FormatLongName.Should().Be("Sun AU");
            probeResult.SizeBytes.Should().Be(new FileInfo(fixturePath).Length);
            probeResult.DurationSeconds.Should().BeApproximately(0.1, 0.01);

            probeResult.CodecType.Should().Be("audio");
            probeResult.CodecName.Should().Be("pcm_mulaw");
            probeResult.CodecLongName.Should().Be("PCM mu-law / G.711 mu-law");
            probeResult.SampleRate.Should().Be(44100);
            probeResult.Channels.Should().Be(1);
            probeResult.ChannelLayout.Should().Be("mono");
            probeResult.BitsPerSample.Should().Be(16);
            probeResult.DurationInSamples.Should().Be(4410);
            probeResult.TimeBase.Should().Be("1/44100");
        }

        [Fact]
        public async Task Probe_CafFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var cafPath = Path.Combine(tempDirectory, "source.caf");
                var samples = Enumerable.Range(0, 44100 * 2).Select(frame => frame % 1000).ToArray();

                using (var session = AlacEncoderSession.OpenSession(cafPath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(cafPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("caf");
                probeResult.SizeBytes.Should().Be(new FileInfo(cafPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("alac");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_TtaFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var ttaPath = Path.Combine(tempDirectory, "source.tta");
                var samples = Enumerable.Range(0, 44100 * 2).Select(frame => frame % 1000).ToArray();

                using (var session = TtaEncoderSession.OpenSession(ttaPath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(ttaPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("tta");
                probeResult.SizeBytes.Should().Be(new FileInfo(ttaPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("tta");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be("1/44100");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_TtaFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var ttaPath = Path.Combine(tempDirectory, "source.tta");
                using (var session = TtaEncoderSession.OpenSession(ttaPath, channels: 1, sampleRate: 8000, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples([0, 1000, -1000, 500], frameCount: 4);
                    session.Finish();
                }

                using var stream = new MemoryStream(File.ReadAllBytes(ttaPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Tta);

                probeResult.FormatName.Should().Be("tta");
                probeResult.CodecName.Should().Be("tta");
                probeResult.SampleRate.Should().Be(8000);
                probeResult.Channels.Should().Be(1);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_CafFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var cafPath = Path.Combine(tempDirectory, "source.caf");
                using (var session = AlacEncoderSession.OpenSession(cafPath, channels: 1, sampleRate: 8000, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples([0, 1000, -1000, 500], frameCount: 4);
                    session.Finish();
                }

                using var stream = new MemoryStream(File.ReadAllBytes(cafPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Caf);

                probeResult.FormatName.Should().Be("caf");
                probeResult.CodecName.Should().Be("alac");
                probeResult.SampleRate.Should().Be(8000);
                probeResult.Channels.Should().Be(1);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_OpusFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                // Opus encoding here is fixed at 48kHz -- the shared _wavFixturePath is 44100Hz, so
                // this needs its own source, the same way OpusEncoderTest's own fixtures do.
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 2, sampleRate: 48000, bitsPerSample: 16, [0, 0, 1000, -1000, -1000, 1000, 500, -500]);

                var opusPath = Path.Combine(tempDirectory, "source.opus");
                OpusEncoder.Encode(sourceWavPath, opusPath);

                using var stream = new MemoryStream(File.ReadAllBytes(opusPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Opus);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.CodecName.Should().Be("opus");
                probeResult.SampleRate.Should().Be(48000);
                probeResult.Channels.Should().Be(2);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_OpusFormat_WithNonSeekableStream_Should_Still_Succeed()
        {
            // OpusDecoder's own Stream overload reads the whole source into memory up front, so
            // (unlike Vorbis below) it genuinely needs no seek support at all -- confirmed directly
            // rather than just documented, by actually probing a non-seekable wrapper stream.
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 2, sampleRate: 48000, bitsPerSample: 16, [0, 0, 1000, -1000, -1000, 1000, 500, -500]);

                var opusPath = Path.Combine(tempDirectory, "source.opus");
                OpusEncoder.Encode(sourceWavPath, opusPath);

                using var nonSeekableStream = new NonSeekableStream(File.ReadAllBytes(opusPath));
                var probeResult = await _nativeEncoder.Probe(nonSeekableStream, AudioFormat.Opus);

                probeResult.CodecName.Should().Be("opus");
                probeResult.Channels.Should().Be(2);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_VorbisFormat_Should_Return_Correct_Metadata()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var oggPath = Path.Combine(tempDirectory, "source.ogg");
                VorbisEncoder.Encode(_wavFixturePath, oggPath);

                using var stream = new MemoryStream(File.ReadAllBytes(oggPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Vorbis);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.CodecName.Should().Be("vorbis");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_VorbisFormat_WithNonSeekableStream_Should_Throw()
        {
            // Unlike every other Stream-based codec here, VorbisDecoder.Decode(Stream, ...) requires
            // CanSeek -- NVorbis's own VorbisReader needs to seek internally (confirmed directly in
            // VorbisDecoderTest, not just asserted here) -- so this is a real, documented constraint,
            // not a gap: probing from a non-seekable source must fail clearly, not corrupt silently.
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var oggPath = Path.Combine(tempDirectory, "source.ogg");
                VorbisEncoder.Encode(_wavFixturePath, oggPath);

                using var nonSeekableStream = new NonSeekableStream(File.ReadAllBytes(oggPath));
                var act = async () => await _nativeEncoder.Probe(nonSeekableStream, AudioFormat.Vorbis);

                await act.Should().ThrowAsync<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_WmaFormat_Should_Return_Correct_Metadata()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var samples = new short[sampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var wmaPath = Path.Combine(tempDirectory, "source.wma");
                WmaEncoder.Encode(wmaPath, samples, channels, sampleRate);

                using var stream = new MemoryStream(File.ReadAllBytes(wmaPath));
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Wma);

                probeResult.FormatName.Should().Be("asf");
                probeResult.CodecName.Should().Be("wmav2");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(channels);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_OpusFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var opusPath = Path.Combine(tempDirectory, "source.opus");
                const int sampleRate = 48000;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                using (var session = OpusEncoderSession.OpenSession(opusPath, channels: 1, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(opusPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.SizeBytes.Should().Be(new FileInfo(opusPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("opus");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
                probeResult.Tags.Should().BeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_OpusFile_WithTags_Should_Surface_Tags()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var opusPath = Path.Combine(tempDirectory, "tagged.opus");
                var tags = new Dictionary<string, string> { ["TITLE"] = "Title", ["ARTIST"] = "Artist" };

                using (var session = OpusEncoderSession.OpenSession(opusPath, channels: 1, sampleRate: 48000, bitsPerSample: 16, tags))
                {
                    session.WriteInterleavedSamples([0, 1000, -1000, 500], frameCount: 4);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(opusPath);

                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
                probeResult.Tags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Artist");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_VorbisFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var oggPath = Path.Combine(tempDirectory, "source.ogg");
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                using (var session = VorbisEncoderSession.OpenSession(oggPath, channels: 1, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(oggPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("ogg");
                probeResult.SizeBytes.Should().Be(new FileInfo(oggPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("vorbis");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
                probeResult.Tags.Should().BeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_VorbisFile_WithTags_Should_Surface_Tags()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var oggPath = Path.Combine(tempDirectory, "tagged.ogg");
                var tags = new Dictionary<string, string> { ["TITLE"] = "Title", ["ARTIST"] = "Artist" };

                using (var session = VorbisEncoderSession.OpenSession(oggPath, channels: 1, sampleRate: 44100, bitsPerSample: 16, tags: tags))
                {
                    session.WriteInterleavedSamples([0, 1000, -1000, 500], frameCount: 4);
                    session.Finish();
                }

                var probeResult = await _nativeEncoder.Probe(oggPath);

                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
                probeResult.Tags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Artist");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_WavPackFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var wvPath = Path.Combine(tempDirectory, "source.wv");
                WavPackEncoder.Encode(sourceWavPath, wvPath);

                var probeResult = await _nativeEncoder.Probe(wvPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("wv");
                probeResult.SizeBytes.Should().Be(new FileInfo(wvPath).Length);
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);

                probeResult.CodecType.Should().Be("audio");
                probeResult.CodecName.Should().Be("wavpack");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.TimeBase.Should().Be($"1/{sampleRate}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_FlacFile_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var flacPath = Path.Combine(tempDirectory, "source.flac");
                FlacEncoder.Encode(_wavFixturePath, flacPath);

                var probeResult = await _nativeEncoder.Probe(flacPath);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("flac");
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.CodecName.Should().Be("flac");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.ChannelLayout.Should().Be("stereo");
                probeResult.Tags.Should().BeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_FlacFile_WithVorbisCommentTag_Should_Surface_Tags()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var flacPath = Path.Combine(tempDirectory, "tagged.flac");
                var tags = new Dictionary<string, string> { ["TITLE"] = "Title", ["ARTIST"] = "Artist" };
                FlacEncoder.Encode(_wavFixturePath, flacPath, tags: tags);

                var probeResult = await _nativeEncoder.Probe(flacPath);

                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
                probeResult.Tags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Artist");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Mp3File_Should_Return_Correct_Metadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "source.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = await _nativeEncoder.Probe(mp3Path);

                AssertNonEmptyWaveform(probeResult.Waveform);

                probeResult.FormatName.Should().Be("mp3");
                probeResult.DurationSeconds.Should().BeApproximately(2, 0.1);
                probeResult.CodecName.Should().Be("mp3");
                probeResult.SampleRate.Should().Be(44100);
                probeResult.BitsPerSample.Should().Be(16);
                probeResult.ChannelLayout.Should().Be("stereo");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_AacFile_Should_Return_Correct_Metadata()
        {
            var probeResult = await _nativeEncoder.Probe(_aacFixturePath);

            probeResult.FormatName.Should().Be("aac");
            probeResult.SampleRate.Should().BePositive();
            probeResult.CodecName.Should().Be("aac");
            probeResult.CodecType.Should().Be("audio");
            probeResult.ChannelLayout.Should().Be("mono");
        }

        [Fact]
        public async Task Probe_WmaFile_Should_Return_Correct_Metadata()
        {
            var probeResult = await _nativeEncoder.Probe(_wmaFixturePath);

            probeResult.FormatName.Should().Be("asf");
            probeResult.SampleRate.Should().BePositive();
            probeResult.CodecName.Should().Be("wmav2");
            probeResult.CodecType.Should().Be("audio");
        }

        [Fact]
        public async Task Probe_MovFile_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_movFixturePath);

            probeResult.Waveform.Should().BeNull();
            probeResult.PeakAmplitude.Should().BeNull();
            probeResult.RmsLevel.Should().BeNull();

            probeResult.FormatName.Should().Be("mov");
            probeResult.DurationSeconds.Should().BeApproximately(5, 0.1);
            probeResult.CodecType.Should().Be("video");
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);
        }

        [Fact]
        public async Task Probe_Mp4File_Should_Return_Correct_VideoMetadata()
        {
            var probeResult = await _nativeEncoder.Probe(_mp4FixturePath);

            probeResult.Waveform.Should().BeNull();
            probeResult.PeakAmplitude.Should().BeNull();
            probeResult.RmsLevel.Should().BeNull();
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);

            probeResult.FormatName.Should().Be("mp4");
            probeResult.DurationSeconds.Should().BeApproximately(5, 0.1);
        }

        [Fact]
        public async Task Probe_Mp4FileWithAacAudio_Should_Return_AudioMetadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                const int sampleCount = sampleRate;

                var samples = new short[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var aacPath = Path.Combine(tempDirectory, "source.aac");
                AacEncoder.Encode(aacPath, samples, channels: 1, sampleRate);

                var rawFrames = Mp4FileBuilder.ExtractRawAacFrames(aacPath);
                var mp4Path = Path.Combine(tempDirectory, "source.mp4");
                Mp4FileBuilder.Create(mp4Path, sampleRate, rawFrames);

                var probeResult = await _nativeEncoder.Probe(mp4Path);

                probeResult.FormatName.Should().Be("mp4");
                probeResult.CodecType.Should().Be("video");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                AssertNonEmptyWaveform(probeResult.Waveform);

                // Proves the DecodedAudioTrack -> ProbeResult forwarding path (ProbeVideo's own,
                // separate from every other codec's direct WaveformCalculator -> ProbeResult path)
                // actually carries PeakAmplitude/RmsLevel through too, not just Waveform. Structural
                // sanity rather than a predicted value: AAC's own lossy quantization/windowing
                // didn't preserve this signal's pre-encode peak closely enough for a tight tolerance
                // to be meaningful (confirmed by actually measuring it, not assumed -- the real
                // round-tripped peak landed well under half of the original 10000/32768).
                // BeGreaterThan(0.0), not just BeInRange: a range check alone would still pass even
                // if the forwarding path silently regressed to always returning 0.0 (e.g. a future
                // change that zeroed DecodedAudioTrack's own fields) -- confirmed this gap was real
                // by temporarily weakening the assertions to range-only and seeing they'd still pass
                // against a hard-coded 0.0, not assumed.
                probeResult.PeakAmplitude.Should().NotBeNull();
                probeResult.PeakAmplitude!.Value.Should().BeInRange(0.0, 1.0).And.BeGreaterThan(0.0);
                probeResult.RmsLevel.Should().NotBeNull();
                probeResult.RmsLevel!.Value.Should().BeInRange(0.0, 1.0).And.BeGreaterThan(0.0);
                probeResult.RmsLevel!.Value.Should().BeLessThanOrEqualTo(probeResult.PeakAmplitude!.Value, "RMS can never exceed the peak for any real signal");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_WithMovFormat_Should_Return_Correct_VideoMetadata()
        {
            using var stream = File.OpenRead(_movFixturePath);

            var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Mov);

            probeResult.FormatName.Should().Be("mov");
            probeResult.CodecType.Should().Be("video");
            probeResult.DurationSeconds.Should().BeApproximately(5, 0.1);
            probeResult.Width.Should().Be(640);
            probeResult.Height.Should().Be(360);
            probeResult.Waveform.Should().BeNull();
        }

        [Fact]
        public async Task Probe_Stream_WithMovFormat_WithAacAudio_Should_Return_AudioMetadata_And_Waveform()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                const int sampleCount = sampleRate;

                var samples = new short[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var aacPath = Path.Combine(tempDirectory, "source.aac");
                AacEncoder.Encode(aacPath, samples, channels: 1, sampleRate);

                var rawFrames = Mp4FileBuilder.ExtractRawAacFrames(aacPath);
                var mp4Path = Path.Combine(tempDirectory, "source.mp4");
                Mp4FileBuilder.Create(mp4Path, sampleRate, rawFrames);

                using var stream = File.OpenRead(mp4Path);
                var probeResult = await _nativeEncoder.Probe(stream, AudioFormat.Mov);

                probeResult.CodecType.Should().Be("video");
                probeResult.SampleRate.Should().Be(sampleRate);
                probeResult.Channels.Should().Be(1);
                probeResult.ChannelLayout.Should().Be("mono");
                probeResult.BitsPerSample.Should().Be(16);
                AssertNonEmptyWaveform(probeResult.Waveform);
                probeResult.PeakAmplitude.Should().NotBeNull();
                probeResult.PeakAmplitude!.Value.Should().BeInRange(0.0, 1.0).And.BeGreaterThan(0.0);
                stream.CanRead.Should().BeTrue("Probe(Stream, AudioFormat) must not dispose the caller's stream");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Stream_WithMovFormat_WithNonSeekableStream_Should_Throw()
        {
            var bytes = File.ReadAllBytes(_movFixturePath);
            using var nonSeekableStream = new NonSeekableStream(bytes);

            var act = () => _nativeEncoder.Probe(nonSeekableStream, AudioFormat.Mov);

            await act.Should().ThrowExactlyAsync<ArgumentException>().WithMessage("*seekable*");
        }

        [Fact]
        public async Task Probe_UnsupportedExtension_Should_Throw()
        {
            var act = () => _nativeEncoder.Probe("file.xyz");
            await act.Should().ThrowExactlyAsync<NotSupportedException>();
        }

        [Fact]
        public async Task ConvertFile_Stream_WavToWav_Should_Succeed()
        {
            using var sourceStream = File.OpenRead(_wavFixturePath);
            using var destStream = new MemoryStream();

            await _nativeEncoder.ConvertFile(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav);

            destStream.Length.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task ConvertFile_Stream_WithUnsupportedDestFormat_Should_Throw()
        {
            using var sourceStream = File.OpenRead(_wavFixturePath);
            using var destStream = new MemoryStream();

            var act = () => _nativeEncoder.ConvertFile(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Opus);

            await act.Should().ThrowExactlyAsync<NotSupportedException>();
        }

        [Fact]
        public async Task ConvertFile_Stream_WithAlreadyCancelledToken_Should_Throw()
        {
            using var sourceStream = File.OpenRead(_wavFixturePath);
            using var destStream = new MemoryStream();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = () => _nativeEncoder.ConvertFile(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav, cts.Token);

            await act.Should().ThrowExactlyAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task CutFile_Stream_WavToWav_Should_Succeed()
        {
            using var sourceStream = File.OpenRead(_wavFixturePath);
            using var destStream = new MemoryStream();

            var produced = await _nativeEncoder.CutFile(sourceStream, AudioFormat.Wav, 0, 1, destStream, AudioFormat.Wav);

            produced.Should().BeTrue();
            destStream.Length.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task CutFile_Stream_WithRangeOutsideDuration_Should_ReturnFalse()
        {
            using var sourceStream = File.OpenRead(_wavFixturePath);
            using var destStream = new MemoryStream();

            var produced = await _nativeEncoder.CutFile(sourceStream, AudioFormat.Wav, 1000, 2000, destStream, AudioFormat.Wav);

            produced.Should().BeFalse();
            destStream.Length.Should().Be(0);
        }

        [Fact]
        public async Task ConvertFile_ToNestedMissingDirectory_Should_CreateDirectory_And_Convert()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "nested", "dest.flac");

                await _nativeEncoder.ConvertFile(_wavFixturePath, destPath);

                File.Exists(destPath).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task CutFile_ToNestedMissingDirectory_Should_CreateDirectory_And_Cut()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "nested", "cut.wav");

                await _nativeEncoder.CutFile(_wavFixturePath, destPath, 0, 1);

                File.Exists(destPath).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_Should_Log_Start_And_Completion()
        {
            await _nativeEncoder.Probe(_wavFixturePath);

            _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
        }

        [Fact]
        public async Task ConvertFile_Should_Log_Start_And_Completion()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                await _nativeEncoder.ConvertFile(_wavFixturePath, destPath);

                _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task CutFile_Should_Log_Start_And_Completion()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destPath = Path.Combine(tempDirectory, "cut.wav");

                await _nativeEncoder.CutFile(_wavFixturePath, destPath, 0, 1);

                _logger.Invocations.Should().HaveCountGreaterThanOrEqualTo(2, "both a start and a completion message should be logged");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task Probe_WithLoggingDisabled_Should_Not_Log()
        {
            var logger = new Mock<ILogger<NativeEncoder>>();
            var encoder = new NativeEncoder(logger.Object, enableLogging: false);

            await encoder.Probe(_wavFixturePath);

            logger.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task Probe_WithLoggingDisabled_OnFailure_Should_Not_Log()
        {
            var logger = new Mock<ILogger<NativeEncoder>>();
            var encoder = new NativeEncoder(logger.Object, enableLogging: false);

            var act = () => encoder.Probe("file.xyz");

            await act.Should().ThrowExactlyAsync<NotSupportedException>();
            logger.Invocations.Should().BeEmpty();
        }

        private static void AssertNonEmptyWaveform(IReadOnlyList<double>? waveform)
        {
            waveform.Should().NotBeNull();
            waveform.Should().NotBeEmpty();
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }
    }
}
