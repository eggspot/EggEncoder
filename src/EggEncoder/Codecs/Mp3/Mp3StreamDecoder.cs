using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EggEncoder.Codecs.Mp3;

[SkipLocalsInit]
internal sealed class Mp3StreamDecoder
{
    internal const int SegmentFrames = 48;
    internal const int DefaultParallelism = 4;
    private const int FramesPerBlock = 4096;
    private const int MainBufferLength = 8192;
    private const int Padding = 16;
    private const int MaximumMainDataBegin = 511;

    private readonly Mp3LayerIIIDecoder _decoder = new();
    private readonly byte[] _main = new byte[MainBufferLength + Padding];
    private readonly float[] _left = new float[1152];
    private readonly float[] _right = new float[1152];
    private int _mainLength;
    private int[]? _warmUp;

    public static Mp3StreamInfo Decode(byte[] file, AudioBlockDecodedCallback onBlockDecoded, int maxDegreeOfParallelism)
    {
        var frames = FindAudioFrames(file);
        var channels = frames.Count == 0 ? 2 : frames[0].Header.Channels;
        var sampleRate = frames.Count == 0 ? 44100 : frames[0].Header.SampleRate;
        var samplesPerFrame = frames.Count == 0 ? 1152 : frames[0].Header.SamplesPerFrame;
        var totalSamples = (long)frames.Count * samplesPerFrame;
        var parallelism = Math.Clamp(maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : CodecParallelism.Current ?? Math.Min(Environment.ProcessorCount, DefaultParallelism), 1, 64);
        var output = new BlockWriter(channels, sampleRate, totalSamples, onBlockDecoded);
        if (parallelism == 1 || frames.Count <= SegmentFrames * 2)
        {
            var decoder = new Mp3StreamDecoder();
            var pcm = new int[samplesPerFrame * channels];
            for (var index = 0; index < frames.Count; index++)
            {
                decoder.DecodeToPcm(file, frames[index].Offset, frames[index].Header, channels, pcm);
                output.Write(pcm);
            }
        }
        else
        {
            DecodeSegments(file, frames, channels, samplesPerFrame, parallelism, output);
        }

        output.Flush();
        return new Mp3StreamInfo
        {
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = 16,
            TotalSamples = totalSamples
        };
    }

    internal static List<(int Offset, MpegAudioHeader Header)> FindAudioFrames(ReadOnlySpan<byte> file)
    {
        var frames = new List<(int, MpegAudioHeader)>();
        var position = SkipId3v2(file);
        MpegAudioHeader? first = null;
        var sawOtherLayer = false;
        while (position + 4 <= file.Length)
        {
            if (!MpegAudioHeader.TryParse(file[position..], out var header))
            {
                position++;
                continue;
            }

            if (header.Layer != 3)
            {
                sawOtherLayer = true;
                position++;
                continue;
            }

            var length = header.FrameLength;
            if (length < 4 + header.SideInfoLength + (header.HasCrc ? 2 : 0) || position + length > file.Length)
            {
                position++;
                continue;
            }

            if (first is { } reference && !header.IsCompatibleWith(reference))
            {
                position++;
                continue;
            }

            if (first is null && !NextHeaderConfirms(file, position + length, header))
            {
                position++;
                continue;
            }

            first ??= header;
            frames.Add((position, header));
            position += length;
        }

        if (frames.Count == 0)
        {
            throw sawOtherLayer
                ? new NotSupportedException("MP3: only MPEG Layer III audio is decoded, this file holds Layer I or II")
                : new InvalidDataException("MP3: no MPEG audio frames found");
        }

        if (IsInfoFrame(file, frames[0].Item1, frames[0].Item2))
        {
            frames.RemoveAt(0);
        }

        return frames;
    }

    internal static int WarmUpStart(IReadOnlyList<(int Offset, MpegAudioHeader Header)> frames, int start)
    {
        var first = Math.Max(0, start - 2);
        var bytes = 0;
        while (first > 0 && bytes < MaximumMainDataBegin)
        {
            first--;
            var header = frames[first].Header;
            bytes += header.FrameLength - 4 - header.SideInfoLength - (header.HasCrc ? 2 : 0);
        }

        return first;
    }

    private static void DecodeSegments(byte[] file, List<(int Offset, MpegAudioHeader Header)> frames, int channels, int samplesPerFrame, int parallelism, BlockWriter output)
    {
        var segmentCount = (frames.Count + SegmentFrames - 1) / SegmentFrames;
        var ahead = Math.Min(segmentCount, parallelism * 2);
        var buffers = new int[ahead][];
        for (var i = 0; i < ahead; i++)
        {
            buffers[i] = new int[SegmentFrames * samplesPerFrame * channels];
        }

        var decoders = new System.Collections.Concurrent.ConcurrentBag<Mp3StreamDecoder>();
        var pending = new Task[ahead];
        using var stop = new CancellationTokenSource();

        Task Start(int segment) => Task.Run(
            () =>
            {
                var decoder = decoders.TryTake(out var reused) ? reused : new Mp3StreamDecoder();
                decoder.DecodeSegment(file, frames, segment, channels, samplesPerFrame, buffers[segment % ahead], stop.Token);
                decoders.Add(decoder);
            },
            stop.Token);

        for (var segment = 0; segment < ahead; segment++)
        {
            pending[segment] = Start(segment);
        }

        try
        {
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var slot = segment % ahead;
                pending[slot].GetAwaiter().GetResult();
                var start = segment * SegmentFrames;
                var length = Math.Min(frames.Count, start + SegmentFrames) - start;
                output.Write(buffers[slot].AsSpan(0, length * samplesPerFrame * channels));
                if (segment + ahead < segmentCount)
                {
                    pending[slot] = Start(segment + ahead);
                }
            }
        }
        catch
        {
            stop.Cancel();
            foreach (var task in pending)
            {
                try
                {
                    task?.Wait();
                }
                catch (AggregateException)
                {
                }
            }

            throw;
        }
    }

    private void DecodeSegment(byte[] file, List<(int Offset, MpegAudioHeader Header)> frames, int segment, int channels, int samplesPerFrame, int[] buffer, CancellationToken cancellationToken)
    {
        var start = segment * SegmentFrames;
        var end = Math.Min(frames.Count, start + SegmentFrames);
        var frameSamples = samplesPerFrame * channels;
        Reset();
        for (var index = WarmUpStart(frames, start); index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = index < start ? WarmUpBuffer(frameSamples) : buffer.AsSpan((index - start) * frameSamples, frameSamples);
            DecodeToPcm(file, frames[index].Offset, frames[index].Header, channels, target);
        }
    }

    private Span<int> WarmUpBuffer(int length)
    {
        if (_warmUp is null || _warmUp.Length < length)
        {
            _warmUp = new int[length];
        }

        return _warmUp.AsSpan(0, length);
    }

    private void Reset()
    {
        _decoder.Reset();
        _mainLength = 0;
    }

    private void DecodeToPcm(byte[] file, int offset, MpegAudioHeader header, int channels, Span<int> pcm)
    {
        var granules = header.Granules;
        var produced = granules * 576;
        if (!DecodeFrame(file, offset, header))
        {
            Array.Clear(_left, 0, produced);
            Array.Clear(_right, 0, produced);
        }
        else if (header.Channels == 1)
        {
            Array.Copy(_left, _right, produced);
        }

        if (channels == 1)
        {
            ToInt16(_left.AsSpan(0, produced), pcm[..produced]);
            return;
        }

        ToInterleavedInt16(_left.AsSpan(0, produced), _right.AsSpan(0, produced), pcm);
    }

    private bool DecodeFrame(byte[] file, int offset, MpegAudioHeader header)
    {
        Span<byte> side = stackalloc byte[32 + Padding];
        var sideStart = offset + 4 + (header.HasCrc ? 2 : 0);
        var sideLength = header.SideInfoLength;
        side.Clear();
        file.AsSpan(sideStart, sideLength).CopyTo(side);
        var mainDataBegin = Mp3LayerIIIDecoder.ReadMainDataBegin(header, side);
        var frameData = file.AsSpan(sideStart + sideLength, offset + header.FrameLength - sideStart - sideLength);
        var available = mainDataBegin <= _mainLength;
        var keep = available ? mainDataBegin : Math.Min(_mainLength, MaximumMainDataBegin);
        Array.Copy(_main, _mainLength - keep, _main, 0, keep);
        _mainLength = keep;

        frameData.CopyTo(_main.AsSpan(_mainLength));
        _mainLength += frameData.Length;
        if (!available)
        {
            return false;
        }

        try
        {
            _decoder.ReadSideInfo(header, side);
            var bitPosition = 0;
            for (var granule = 0; granule < header.Granules; granule++)
            {
                _decoder.DecodeGranule(header, granule, _main, ref bitPosition, _left.AsSpan(granule * 576, 576), _right.AsSpan(granule * 576, 576));
            }

            return true;
        }
        catch (InvalidDataException)
        {
            _decoder.Reset();
            return false;
        }
    }

    private static int SkipId3v2(ReadOnlySpan<byte> file)
    {
        var position = 0;
        while (position + 10 <= file.Length && file[position] == (byte)'I' && file[position + 1] == (byte)'D' && file[position + 2] == (byte)'3')
        {
            var size = ((file[position + 6] & 0x7F) << 21) | ((file[position + 7] & 0x7F) << 14) | ((file[position + 8] & 0x7F) << 7) | (file[position + 9] & 0x7F);
            position += 10 + size + ((file[position + 5] & 0x10) != 0 ? 10 : 0);
        }

        return Math.Min(position, file.Length);
    }

    private static bool NextHeaderConfirms(ReadOnlySpan<byte> file, int next, MpegAudioHeader header)
    {
        if (next + 4 > file.Length)
        {
            return true;
        }

        return MpegAudioHeader.TryParse(file[next..], out var following) && following.Layer == 3 && following.IsCompatibleWith(header);
    }

    private static bool IsInfoFrame(ReadOnlySpan<byte> file, int offset, MpegAudioHeader header)
    {
        var tag = offset + 4 + (header.HasCrc ? 2 : 0) + header.SideInfoLength;
        if (tag + 4 <= file.Length)
        {
            var magic = file.Slice(tag, 4);
            if (magic.SequenceEqual("Xing"u8) || magic.SequenceEqual("Info"u8))
            {
                return true;
            }
        }

        var vbri = offset + 36;
        return vbri + 4 <= file.Length && file.Slice(vbri, 4).SequenceEqual("VBRI"u8);
    }

    private static void ToInterleavedInt16(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<int> output)
    {
        var i = 0;
        if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            var scale = Vector128.Create(32768f);
            var lowest = Vector128.Create(-32768f);
            var highest = Vector128.Create(32767f);
            var magic = Vector128.Create(12582912f);
            for (; i + 4 <= left.Length; i += 4)
            {
                var l = Vector128.ConvertToInt32((Vector128.Min(Vector128.Max(Vector128.Create(left.Slice(i, 4)) * scale, lowest), highest) + magic) - magic);
                var r = Vector128.ConvertToInt32((Vector128.Min(Vector128.Max(Vector128.Create(right.Slice(i, 4)) * scale, lowest), highest) + magic) - magic);
                if (Sse2.IsSupported)
                {
                    Sse2.UnpackLow(l, r).CopyTo(output.Slice(2 * i, 4));
                    Sse2.UnpackHigh(l, r).CopyTo(output.Slice((2 * i) + 4, 4));
                }
                else
                {
                    AdvSimd.Arm64.ZipLow(l, r).CopyTo(output.Slice(2 * i, 4));
                    AdvSimd.Arm64.ZipHigh(l, r).CopyTo(output.Slice((2 * i) + 4, 4));
                }
            }
        }

        for (; i < left.Length; i++)
        {
            output[2 * i] = ToInt16(left[i]);
            output[(2 * i) + 1] = ToInt16(right[i]);
        }
    }

    private static int ToInt16(float sample)
    {
        var scaled = MathF.Round(sample * 32768f);
        return scaled >= short.MaxValue ? short.MaxValue : scaled <= short.MinValue ? short.MinValue : (int)scaled;
    }

    private static void ToInt16(ReadOnlySpan<float> samples, Span<int> output)
    {
        var i = 0;
        var scale = Vector128.Create(32768f);
        var lowest = Vector128.Create(-32768f);
        var highest = Vector128.Create(32767f);
        var magic = Vector128.Create(12582912f);
        for (; i + 4 <= samples.Length; i += 4)
        {
            var scaled = Vector128.Min(Vector128.Max(Vector128.Create(samples.Slice(i, 4)) * scale, lowest), highest);
            var rounded = (scaled + magic) - magic;
            Vector128.ConvertToInt32(rounded).CopyTo(output.Slice(i, 4));
        }

        for (; i < samples.Length; i++)
        {
            var scaled = MathF.Round(samples[i] * 32768f);
            output[i] = scaled >= short.MaxValue ? short.MaxValue : scaled <= short.MinValue ? short.MinValue : (int)scaled;
        }
    }

    private sealed class BlockWriter
    {
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly long _totalSamples;
        private readonly AudioBlockDecodedCallback _callback;
        private readonly int[] _block;
        private int _filled;

        public BlockWriter(int channels, int sampleRate, long totalSamples, AudioBlockDecodedCallback callback)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _totalSamples = totalSamples;
            _callback = callback;
            _block = new int[FramesPerBlock * channels];
        }

        public void Write(ReadOnlySpan<int> interleaved)
        {
            while (interleaved.Length > 0)
            {
                var count = Math.Min(interleaved.Length, _block.Length - _filled);
                interleaved[..count].CopyTo(_block.AsSpan(_filled));
                _filled += count;
                interleaved = interleaved[count..];
                if (_filled == _block.Length)
                {
                    _callback(_block, _channels, _sampleRate, 16, _totalSamples);
                    _filled = 0;
                }
            }
        }

        public void Flush()
        {
            if (_filled > 0)
            {
                _callback(_block.AsSpan(0, _filled), _channels, _sampleRate, 16, _totalSamples);
                _filled = 0;
            }
        }
    }
}
