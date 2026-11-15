using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.Mp3;

[SkipLocalsInit]
internal sealed class Mp3LayerIIIDecoder
{
    private const int Lines = 576;
    private const float InverseSquareRoot2 = 0.70710678118654752f;

    private readonly GranuleInfo[,] _granules = new GranuleInfo[2, 2];
    private readonly int[][] _scfsi = [new int[4], new int[4]];
    private readonly int[][] _longScalefactors = [new int[22], new int[22]];
    private readonly int[][] _shortScalefactors = [new int[39], new int[39]];
    private readonly int[] _lsfLongMax = new int[22];
    private readonly int[] _lsfShortMax = new int[39];
    private readonly int[][] _quantized = [new int[Lines + 4], new int[Lines + 4]];
    private readonly int[] _nonZero = new int[2];
    private readonly float[] _bandGains = new float[22];
    private readonly float[][] _spectrum = [new float[Lines], new float[Lines]];
    private readonly float[] _reorder = new float[Lines];
    private readonly float[][] _overlap = [new float[Lines], new float[Lines]];
    private readonly float[] _hybrid = new float[Lines];
    private readonly Mp3Synthesis[] _synthesis = [new Mp3Synthesis(), new Mp3Synthesis()];

    public Mp3LayerIIIDecoder()
    {
        for (var granule = 0; granule < 2; granule++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                _granules[granule, channel] = new GranuleInfo();
            }
        }
    }

    public static int ReadMainDataBegin(MpegAudioHeader header, ReadOnlySpan<byte> sideInfo) =>
        header.IsLsf ? sideInfo[0] : (sideInfo[0] << 1) | (sideInfo[1] >> 7);

    public void ReadSideInfo(MpegAudioHeader header, ReadOnlySpan<byte> paddedSideInfo)
    {
        var bits = new Mp3BitStream(paddedSideInfo, 0);
        var channels = header.Channels;
        if (header.IsLsf)
        {
            bits.Skip(8 + (channels == 1 ? 1 : 2));
        }
        else
        {
            bits.Skip(9 + (channels == 1 ? 5 : 3));
            for (var channel = 0; channel < channels; channel++)
            {
                for (var band = 0; band < 4; band++)
                {
                    _scfsi[channel][band] = bits.ReadBit();
                }
            }
        }

        for (var granule = 0; granule < header.Granules; granule++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var info = _granules[granule, channel];
                info.Part23Length = bits.Read(12);
                info.BigValues = bits.Read(9);
                if (info.BigValues > 288)
                {
                    throw new InvalidDataException("MP3: big_values is larger than 288");
                }

                info.GlobalGain = bits.Read(8);
                info.ScalefacCompress = bits.Read(header.IsLsf ? 9 : 4);
                info.WindowSwitching = bits.ReadBit() == 1;
                if (info.WindowSwitching)
                {
                    info.BlockType = bits.Read(2);
                    if (info.BlockType == 0)
                    {
                        throw new InvalidDataException("MP3: window switching with block type 0");
                    }

                    info.Mixed = bits.ReadBit() == 1;
                    info.TableSelect0 = bits.Read(5);
                    info.TableSelect1 = bits.Read(5);
                    info.TableSelect2 = 0;
                    info.SubblockGain0 = bits.Read(3);
                    info.SubblockGain1 = bits.Read(3);
                    info.SubblockGain2 = bits.Read(3);
                    info.Region0End = info.BlockType == 2 ? 3 * Mp3DecoderTables.ShortBandStarts[header.SampleRateIndex][3] : Mp3DecoderTables.LongBandStarts[header.SampleRateIndex][8];
                    info.Region1End = Lines;
                }
                else
                {
                    info.BlockType = 0;
                    info.Mixed = false;
                    info.TableSelect0 = bits.Read(5);
                    info.TableSelect1 = bits.Read(5);
                    info.TableSelect2 = bits.Read(5);
                    info.SubblockGain0 = info.SubblockGain1 = info.SubblockGain2 = 0;
                    var region0 = bits.Read(4);
                    var region1 = bits.Read(3);
                    var starts = Mp3DecoderTables.LongBandStarts[header.SampleRateIndex];
                    info.Region0End = starts[Math.Min(region0 + 1, 22)];
                    info.Region1End = starts[Math.Min(region0 + region1 + 2, 22)];
                }

                info.Preflag = !header.IsLsf && bits.ReadBit() == 1;
                info.ScalefacScale = bits.ReadBit() == 1;
                info.Count1Table = bits.ReadBit();
            }
        }
    }

    public void DecodeGranule(MpegAudioHeader header, int granule, ReadOnlySpan<byte> paddedMainData, ref int bitPosition, Span<float> left, Span<float> right)
    {
        var channels = header.Channels;
        for (var channel = 0; channel < channels; channel++)
        {
            var info = _granules[granule, channel];
            var end = bitPosition + info.Part23Length;
            var bits = new Mp3BitStream(paddedMainData, bitPosition);
            if (header.IsLsf)
            {
                ReadLsfScalefactors(header, info, channel, ref bits);
            }
            else
            {
                ReadMpeg1Scalefactors(info, channel, granule, ref bits);
            }

            if (!(info.WindowSwitching && info.BlockType == 2) && !header.Intensity)
            {
                _nonZero[channel] = DecodeLongBlock(header, info, channel, ref bits, end);
            }
            else
            {
                _nonZero[channel] = DecodeHuffman(info, _quantized[channel], ref bits, end);
                Requantize(header, info, channel);
            }

            bitPosition = end;
        }

        if (channels == 2 && (header.MidSide || header.Intensity))
        {
            ProcessStereo(header, granule);
        }

        for (var channel = 0; channel < channels; channel++)
        {
            var info = _granules[granule, channel];
            var spectrum = _spectrum[channel];
            if (info.WindowSwitching && info.BlockType == 2)
            {
                Reorder(header, info, spectrum);
            }

            var limit = SubbandLimit(header, _nonZero[channel], info);
            Antialias(info, spectrum, limit);
            Hybrid(info, spectrum, _overlap[channel], limit);
            _synthesis[channel].Synthesize(_hybrid, channel == 0 ? left : right);
        }
    }

    public void Reset()
    {
        foreach (var overlap in _overlap)
        {
            Array.Clear(overlap);
        }

        foreach (var synthesis in _synthesis)
        {
            synthesis.Reset();
        }
    }

    private void ReadMpeg1Scalefactors(GranuleInfo info, int channel, int granule, ref Mp3BitStream bits)
    {
        var slen1 = Mp3DecoderTables.Slen1[info.ScalefacCompress];
        var slen2 = Mp3DecoderTables.Slen2[info.ScalefacCompress];
        var longScalefactors = _longScalefactors[channel];
        var shortScalefactors = _shortScalefactors[channel];
        if (info.WindowSwitching && info.BlockType == 2)
        {
            var firstShort = 0;
            if (info.Mixed)
            {
                for (var band = 0; band < 8; band++)
                {
                    longScalefactors[band] = bits.Read(slen1);
                }

                firstShort = 3;
            }

            for (var band = firstShort; band < 12; band++)
            {
                var length = band < 6 ? slen1 : slen2;
                for (var window = 0; window < 3; window++)
                {
                    shortScalefactors[(band * 3) + window] = bits.Read(length);
                }
            }

            shortScalefactors[36] = shortScalefactors[37] = shortScalefactors[38] = 0;
            return;
        }

        ReadLongGroup(ref bits, longScalefactors, 0, 6, slen1, granule == 1 && _scfsi[channel][0] == 1);
        ReadLongGroup(ref bits, longScalefactors, 6, 11, slen1, granule == 1 && _scfsi[channel][1] == 1);
        ReadLongGroup(ref bits, longScalefactors, 11, 16, slen2, granule == 1 && _scfsi[channel][2] == 1);
        ReadLongGroup(ref bits, longScalefactors, 16, 21, slen2, granule == 1 && _scfsi[channel][3] == 1);
        longScalefactors[21] = 0;
    }

    private static void ReadLongGroup(ref Mp3BitStream bits, int[] scalefactors, int first, int end, int length, bool reuse)
    {
        if (reuse)
        {
            return;
        }

        for (var band = first; band < end; band++)
        {
            scalefactors[band] = bits.Read(length);
        }
    }

    private void ReadLsfScalefactors(MpegAudioHeader header, GranuleInfo info, int channel, ref Mp3BitStream bits)
    {
        Span<int> lengths = stackalloc int[4];
        var compress = info.ScalefacCompress;
        int table;
        info.Preflag = false;
        if (header.Intensity && channel == 1)
        {
            var half = compress >> 1;
            info.IntensityScale = compress & 1;
            if (half < 180)
            {
                lengths[0] = half / 36;
                lengths[1] = half % 36 / 6;
                lengths[2] = half % 36 % 6;
                lengths[3] = 0;
                table = 3;
            }
            else if (half < 244)
            {
                half -= 180;
                lengths[0] = (half % 64) >> 4;
                lengths[1] = (half % 16) >> 2;
                lengths[2] = half % 4;
                lengths[3] = 0;
                table = 4;
            }
            else
            {
                half -= 244;
                lengths[0] = half / 3;
                lengths[1] = half % 3;
                lengths[2] = 0;
                lengths[3] = 0;
                table = 5;
            }
        }
        else if (compress < 400)
        {
            lengths[0] = (compress >> 4) / 5;
            lengths[1] = (compress >> 4) % 5;
            lengths[2] = (compress & 15) >> 2;
            lengths[3] = compress & 3;
            table = 0;
        }
        else if (compress < 500)
        {
            compress -= 400;
            lengths[0] = (compress >> 2) / 5;
            lengths[1] = (compress >> 2) % 5;
            lengths[2] = compress & 3;
            lengths[3] = 0;
            table = 1;
        }
        else
        {
            compress -= 500;
            lengths[0] = compress / 3;
            lengths[1] = compress % 3;
            lengths[2] = 0;
            lengths[3] = 0;
            table = 2;
            info.Preflag = true;
        }

        var column = info.WindowSwitching && info.BlockType == 2 ? (info.Mixed ? 2 : 1) : 0;
        var longScalefactors = _longScalefactors[channel];
        var shortScalefactors = _shortScalefactors[channel];
        Array.Clear(longScalefactors);
        Array.Clear(shortScalefactors);
        var index = 0;
        var shortStart = info.Mixed ? 3 * 3 : 0;
        var longCount = info.Mixed ? 6 : 0;
        for (var part = 0; part < 4; part++)
        {
            var count = Mp3DecoderTables.LsfBandCounts[table, column, part];
            var length = lengths[part];
            var maximum = (1 << length) - 1;
            for (var i = 0; i < count; i++, index++)
            {
                var value = bits.Read(length);
                if (column == 0)
                {
                    longScalefactors[index] = value;
                    _lsfLongMax[index] = maximum;
                }
                else if (index < longCount)
                {
                    longScalefactors[index] = value;
                    _lsfLongMax[index] = maximum;
                }
                else
                {
                    var slot = shortStart + index - longCount;
                    if (slot < 39)
                    {
                        shortScalefactors[slot] = value;
                        _lsfShortMax[slot] = maximum;
                    }
                }
            }
        }

        if (column == 0)
        {
            for (; index < 22; index++)
            {
                _lsfLongMax[index] = 0;
            }
        }
    }

    private int DecodeLongBlock(MpegAudioHeader header, GranuleInfo info, int channel, ref Mp3BitStream bits, int end)
    {
        var gains = _bandGains;
        var quarter = Mp3DecoderTables.QuarterPowers;
        var scaleShift = info.ScalefacScale ? 2 : 1;
        var baseGain = info.GlobalGain - 210 + Mp3DecoderTables.GainOffset;
        var scalefactors = _longScalefactors[channel];
        for (var index = 0; index < 22; index++)
        {
            var pre = info.Preflag ? Mp3DecoderTables.Pretab[index] : 0;
            gains[index] = quarter[Math.Clamp(baseGain - ((scalefactors[index] + pre) << scaleShift), 0, quarter.Length - 1)];
        }

        var spectrum = _spectrum[channel];
        var starts = Mp3DecoderTables.LongBandStarts[header.SampleRateIndex];
        var bigEnd = Math.Min(info.BigValues * 2, Lines);
        var band = 0;
        var line = 0;
        line = DecodePairsScaled(spectrum, ref bits, line, Math.Min(info.Region0End, bigEnd), info.TableSelect0, gains, starts, ref band);
        line = DecodePairsScaled(spectrum, ref bits, line, Math.Min(info.Region1End, bigEnd), info.TableSelect1, gains, starts, ref band);
        line = DecodePairsScaled(spectrum, ref bits, line, bigEnd, info.TableSelect2, gains, starts, ref band);
        var lastNonZero = line;
        var quads = info.Count1Table == 0 ? Mp3HuffmanLookup.CountA : Mp3HuffmanLookup.CountB;
        while (line + 4 <= Lines && bits.Position < end)
        {
            var symbol = quads.Decode(ref bits);
            var first = line;
            for (var bit = 3; bit >= 0; bit--, line++)
            {
                while (starts[band + 1] <= line)
                {
                    band++;
                }

                if (((symbol >> bit) & 1) == 0)
                {
                    spectrum[line] = 0;
                    continue;
                }

                spectrum[line] = bits.ReadBit() == 1 ? -gains[band] : gains[band];
            }

            if (bits.Position > end)
            {
                spectrum[first] = spectrum[first + 1] = spectrum[first + 2] = spectrum[first + 3] = 0;
                line = first;
                break;
            }

            lastNonZero = line;
        }

        Array.Clear(spectrum, line, Lines - line);
        bits.Position = end;
        return lastNonZero;
    }

    private static int DecodePairsScaled(float[] spectrum, ref Mp3BitStream bits, int line, int end, int tableSelect, float[] gains, int[] starts, ref int band)
    {
        if (line >= end)
        {
            return line;
        }

        var lookup = Mp3HuffmanLookup.ForTable(tableSelect);
        if (lookup is null)
        {
            if (tableSelect is 4 or 14)
            {
                throw new InvalidDataException("MP3: reserved Huffman table");
            }

            Array.Clear(spectrum, line, end - line);
            return end;
        }

        var (_, _, linbits) = Mp3HuffmanTables.GetBigValueTable(tableSelect);
        var power = Mp3DecoderTables.Power43;
        while (starts[band + 1] <= line)
        {
            band++;
        }

        var bandEnd = starts[band + 1];
        var gain = gains[band];
        for (; line < end; line += 2)
        {
            if (line >= bandEnd)
            {
                while (starts[band + 1] <= line)
                {
                    band++;
                }

                bandEnd = starts[band + 1];
                gain = gains[band];
            }

            var window = bits.Peek(32);
            var entry = lookup.Lookup(window);
            var used = entry & 0xFF;
            var x = (entry >> 12) & 15;
            var y = (entry >> 8) & 15;
            if (linbits == 0 || (x < 15 && y < 15))
            {
                var hasX = (int)((uint)-x >> 31);
                var signX = (int)((window << used) >> 31) & hasX;
                used += hasX;
                var hasY = (int)((uint)-y >> 31);
                var signY = (int)((window << used) >> 31) & hasY;
                used += hasY;
                spectrum[line] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(power[x] * gain) ^ (signX << 31));
                spectrum[line + 1] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(power[y] * gain) ^ (signY << 31));
                bits.Skip(used);
                continue;
            }

            bits.Skip(used);
            if (x == 15)
            {
                x += bits.Read(linbits);
            }

            spectrum[line] = x == 0 ? 0 : bits.ReadBit() == 1 ? -power[x] * gain : power[x] * gain;
            if (y == 15)
            {
                y += bits.Read(linbits);
            }

            spectrum[line + 1] = y == 0 ? 0 : bits.ReadBit() == 1 ? -power[y] * gain : power[y] * gain;
        }

        return line;
    }

    private static int DecodeHuffman(GranuleInfo info, int[] output, ref Mp3BitStream bits, int end)
    {
        var bigEnd = Math.Min(info.BigValues * 2, Lines);
        var region0 = Math.Min(info.Region0End, bigEnd);
        var region1 = Math.Min(info.Region1End, bigEnd);
        var line = 0;
        line = DecodePairs(output, ref bits, line, region0, info.TableSelect0);
        line = DecodePairs(output, ref bits, line, region1, info.TableSelect1);
        line = DecodePairs(output, ref bits, line, bigEnd, info.TableSelect2);
        var lastNonZero = line;

        var quads = info.Count1Table == 0 ? Mp3HuffmanLookup.CountA : Mp3HuffmanLookup.CountB;
        while (line + 4 <= Lines && bits.Position < end)
        {
            var symbol = quads.Decode(ref bits);
            var v = (symbol >> 3) & 1;
            var w = (symbol >> 2) & 1;
            var x = (symbol >> 1) & 1;
            var y = symbol & 1;
            output[line] = v != 0 && bits.ReadBit() == 1 ? -1 : v;
            output[line + 1] = w != 0 && bits.ReadBit() == 1 ? -1 : w;
            output[line + 2] = x != 0 && bits.ReadBit() == 1 ? -1 : x;
            output[line + 3] = y != 0 && bits.ReadBit() == 1 ? -1 : y;
            if (bits.Position > end)
            {
                output[line] = output[line + 1] = output[line + 2] = output[line + 3] = 0;
                break;
            }

            line += 4;
            lastNonZero = line;
        }

        Array.Clear(output, line, Lines - line);
        bits.Position = end;
        return lastNonZero;
    }

    private static int DecodePairs(int[] output, ref Mp3BitStream bits, int line, int end, int tableSelect)
    {
        if (line >= end)
        {
            return line;
        }

        var lookup = Mp3HuffmanLookup.ForTable(tableSelect);
        if (lookup is null)
        {
            if (tableSelect is 4 or 14)
            {
                throw new InvalidDataException("MP3: reserved Huffman table");
            }

            Array.Clear(output, line, end - line);
            return end;
        }

        var (_, _, linbits) = Mp3HuffmanTables.GetBigValueTable(tableSelect);
        for (; line < end; line += 2)
        {
            var symbol = lookup.Decode(ref bits);
            var x = symbol >> 4;
            var y = symbol & 15;

            if (x == 15 && linbits > 0)
            {
                x += bits.Read(linbits);
            }

            if (x != 0 && bits.ReadBit() == 1)
            {
                x = -x;
            }

            if (y == 15 && linbits > 0)
            {
                y += bits.Read(linbits);
            }

            if (y != 0 && bits.ReadBit() == 1)
            {
                y = -y;
            }

            output[line] = x;
            output[line + 1] = y;
        }

        return line;
    }

    private void Requantize(MpegAudioHeader header, GranuleInfo info, int channel)
    {
        var quantized = _quantized[channel];
        var spectrum = _spectrum[channel];
        var limit = _nonZero[channel];
        var power = Mp3DecoderTables.Power43;
        var quarter = Mp3DecoderTables.QuarterPowers;
        var scaleShift = info.ScalefacScale ? 2 : 1;
        var baseGain = info.GlobalGain - 210 + Mp3DecoderTables.GainOffset;
        var longStarts = Mp3DecoderTables.LongBandStarts[header.SampleRateIndex];
        var shortStarts = Mp3DecoderTables.ShortBandStarts[header.SampleRateIndex];
        var longScalefactors = _longScalefactors[channel];
        var shortScalefactors = _shortScalefactors[channel];
        var line = 0;

        if (!(info.WindowSwitching && info.BlockType == 2) || info.Mixed)
        {
            var longEnd = info.WindowSwitching && info.BlockType == 2 ? 36 : Lines;
            for (var band = 0; band < 22 && line < Math.Min(longEnd, limit); band++)
            {
                var pre = info.Preflag ? Mp3DecoderTables.Pretab[band] : 0;
                var gain = quarter[Math.Clamp(baseGain - (((longScalefactors[band] + pre) << scaleShift) << 0), 0, quarter.Length - 1)];
                var bandEnd = Math.Min(longStarts[band + 1], longEnd);
                if (bandEnd > line)
                {
                    Dequantize(quantized.AsSpan(line, bandEnd - line), spectrum.AsSpan(line, bandEnd - line), power, gain);
                    line = bandEnd;
                }
            }

            if (longEnd == Lines)
            {
                Array.Clear(spectrum, line, Lines - line);
                return;
            }

            line = Math.Max(line, Math.Min(longEnd, limit));
            Array.Clear(spectrum, Math.Min(line, 36), 36 - Math.Min(line, 36));
            line = 36;
        }

        var firstShort = info.Mixed ? FirstMixedShortBand(shortStarts) : 0;
        for (var band = firstShort; band < 13; band++)
        {
            var width = shortStarts[band + 1] - shortStarts[band];
            for (var window = 0; window < 3; window++)
            {
                var start = (shortStarts[band] * 3) + (window * width);
                var subblock = window == 0 ? info.SubblockGain0 : window == 1 ? info.SubblockGain1 : info.SubblockGain2;
                var gain = quarter[Math.Clamp(baseGain - (8 * subblock) - (shortScalefactors[(band * 3) + window] << scaleShift), 0, quarter.Length - 1)];
                var used = Math.Clamp(limit - start, 0, width);
                Dequantize(quantized.AsSpan(start, used), spectrum.AsSpan(start, used), power, gain);
                spectrum.AsSpan(start + used, width - used).Clear();
            }
        }
    }

    private static void Dequantize(ReadOnlySpan<int> quantized, Span<float> spectrum, float[] power, float gain)
    {
        for (var i = 0; i < quantized.Length; i++)
        {
            var q = quantized[i];
            spectrum[i] = MathF.CopySign(power[Math.Abs(q)] * gain, q);
        }
    }

    private static int FirstMixedShortBand(int[] shortStarts)
    {
        var band = 0;
        while (band < 13 && shortStarts[band] * 3 < 36)
        {
            band++;
        }

        return band;
    }

    private void ProcessStereo(MpegAudioHeader header, int granule)
    {
        var left = _spectrum[0];
        var right = _spectrum[1];
        var info = _granules[granule, 1];
        var limit = Math.Max(_nonZero[0], _nonZero[1]);
        if (!header.Intensity)
        {
            MidSide(left, right, 0, limit);
            _nonZero[0] = _nonZero[1] = limit;
            return;
        }

        var shortBlocks = info.WindowSwitching && info.BlockType == 2;
        var sampleRateIndex = header.SampleRateIndex;
        var longStarts = Mp3DecoderTables.LongBandStarts[sampleRateIndex];
        var shortStarts = Mp3DecoderTables.ShortBandStarts[sampleRateIndex];
        var quantizedRight = _quantized[1];
        if (!shortBlocks)
        {
            var last = _nonZero[1] - 1;
            while (last >= 0 && quantizedRight[last] == 0)
            {
                last--;
            }

            var band = 0;
            while (band < 22 && longStarts[band] <= last)
            {
                band++;
            }

            var intensityStart = band < 22 ? longStarts[band] : Lines;
            if (header.MidSide)
            {
                MidSide(left, right, 0, intensityStart);
            }

            for (; band < 22; band++)
            {
                var position = band == 21 ? IntensityPosition(1, band - 1, false) : IntensityPosition(1, band, false);
                ApplyIntensity(header, info, left, right, longStarts[band], longStarts[band + 1], position, band == 21 ? band - 1 : band, false);
            }

            _nonZero[0] = _nonZero[1] = Lines;
            return;
        }

        var firstShort = info.Mixed ? FirstMixedShortBand(shortStarts) : 0;
        var mixedLongIntensity = info.Mixed && !HasNonZero(quantizedRight, 36, Lines);
        if (info.Mixed)
        {
            var longBound = mixedLongIntensity ? LastLongBandWithData(quantizedRight, longStarts) : 22;
            for (var band = 0; longStarts[band] < 36; band++)
            {
                var start = longStarts[band];
                var end = Math.Min(longStarts[band + 1], 36);
                if (band >= longBound)
                {
                    ApplyIntensity(header, info, left, right, start, end, IntensityPosition(1, band, false), band, false);
                }
                else if (header.MidSide)
                {
                    MidSide(left, right, start, end);
                }
            }
        }

        for (var window = 0; window < 3; window++)
        {
            var lastWithData = firstShort - 1;
            for (var band = 12; band >= firstShort; band--)
            {
                var width = shortStarts[band + 1] - shortStarts[band];
                var start = (shortStarts[band] * 3) + (window * width);
                if (HasNonZero(quantizedRight, start, start + width))
                {
                    lastWithData = band;
                    break;
                }
            }

            for (var band = firstShort; band < 13; band++)
            {
                var width = shortStarts[band + 1] - shortStarts[band];
                var start = (shortStarts[band] * 3) + (window * width);
                if (band <= lastWithData)
                {
                    if (header.MidSide)
                    {
                        MidSide(left, right, start, start + width);
                    }

                    continue;
                }

                var slot = band == 12 ? (11 * 3) + window : (band * 3) + window;
                ApplyIntensity(header, info, left, right, start, start + width, IntensityPosition(1, slot, true), slot, true);
            }
        }

        _nonZero[0] = _nonZero[1] = Lines;
    }

    private static int LastLongBandWithData(int[] quantized, int[] longStarts)
    {
        var band = 0;
        for (var b = 0; longStarts[b] < 36; b++)
        {
            if (HasNonZero(quantized, longStarts[b], Math.Min(longStarts[b + 1], 36)))
            {
                band = b + 1;
            }
        }

        return band;
    }

    private int IntensityPosition(int channel, int index, bool shortBand) =>
        shortBand ? _shortScalefactors[channel][index] : _longScalefactors[channel][index];

    private void ApplyIntensity(MpegAudioHeader header, GranuleInfo info, float[] left, float[] right, int start, int end, int position, int index, bool shortBand)
    {
        float kl;
        float kr;
        if (header.IsLsf)
        {
            var maximum = shortBand ? _lsfShortMax[index] : _lsfLongMax[index];
            if (position == maximum)
            {
                if (header.MidSide)
                {
                    MidSide(left, right, start, end);
                }

                return;
            }

            (kl, kr) = Mp3DecoderTables.LsfIntensity[info.IntensityScale][Math.Min(position, 63)];
        }
        else
        {
            if (position >= 7)
            {
                if (header.MidSide)
                {
                    MidSide(left, right, start, end);
                }

                return;
            }

            (kl, kr) = Mp3DecoderTables.Mpeg1Intensity[position];
        }

        for (var i = start; i < end; i++)
        {
            var value = left[i];
            left[i] = value * kl;
            right[i] = value * kr;
        }
    }

    private static bool HasNonZero(int[] values, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (values[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void MidSide(float[] left, float[] right, int start, int end)
    {
        var i = start;
        var scale = Vector128.Create(InverseSquareRoot2);
        for (; i + 4 <= end; i += 4)
        {
            var mid = Vector128.Create(left.AsSpan(i, 4));
            var side = Vector128.Create(right.AsSpan(i, 4));
            ((mid + side) * scale).CopyTo(left.AsSpan(i, 4));
            ((mid - side) * scale).CopyTo(right.AsSpan(i, 4));
        }

        for (; i < end; i++)
        {
            var mid = left[i];
            var side = right[i];
            left[i] = (mid + side) * InverseSquareRoot2;
            right[i] = (mid - side) * InverseSquareRoot2;
        }
    }

    private void Reorder(MpegAudioHeader header, GranuleInfo info, float[] spectrum)
    {
        var shortStarts = Mp3DecoderTables.ShortBandStarts[header.SampleRateIndex];
        var firstShort = info.Mixed ? FirstMixedShortBand(shortStarts) : 0;
        var start = shortStarts[firstShort] * 3;
        for (var band = firstShort; band < 13; band++)
        {
            var width = shortStarts[band + 1] - shortStarts[band];
            var bandStart = shortStarts[band] * 3;
            for (var window = 0; window < 3; window++)
            {
                for (var i = 0; i < width; i++)
                {
                    _reorder[bandStart + (i * 3) + window] = spectrum[bandStart + (window * width) + i];
                }
            }
        }

        Array.Copy(_reorder, start, spectrum, start, Lines - start);
    }

    private static int SubbandLimit(MpegAudioHeader header, int nonZero, GranuleInfo info)
    {
        if (info.WindowSwitching && info.BlockType == 2)
        {
            var shortStarts = Mp3DecoderTables.ShortBandStarts[header.SampleRateIndex];
            var band = 0;
            while (band < 12 && shortStarts[band + 1] * 3 < nonZero)
            {
                band++;
            }

            return Math.Min(32, ((shortStarts[band + 1] * 3) + 17) / 18 + (info.Mixed ? 1 : 0));
        }

        return Math.Min(32, ((nonZero + 17) / 18) + 1);
    }

    private static void Antialias(GranuleInfo info, float[] spectrum, int limit)
    {
        int boundaries;
        if (info.WindowSwitching && info.BlockType == 2)
        {
            if (!info.Mixed)
            {
                return;
            }

            boundaries = 1;
        }
        else
        {
            boundaries = Math.Min(31, limit);
        }

        var cs = Mp3DecoderTables.AliasCs;
        var ca = Mp3DecoderTables.AliasCa;
        if (Vector256.IsHardwareAccelerated)
        {
            var reverse = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);
            var csVector = Vector256.Create(cs.AsSpan(0, 8));
            var caVector = Vector256.Create(ca.AsSpan(0, 8));
            for (var subband = 1; subband <= boundaries; subband++)
            {
                var edge = subband * 18;
                var upper = Vector256.Create(spectrum.AsSpan(edge, 8));
                var lower = Vector256.Shuffle(Vector256.Create(spectrum.AsSpan(edge - 8, 8)), reverse);
                var newLower = (lower * csVector) - (upper * caVector);
                var newUpper = (upper * csVector) + (lower * caVector);
                newUpper.CopyTo(spectrum.AsSpan(edge, 8));
                Vector256.Shuffle(newLower, reverse).CopyTo(spectrum.AsSpan(edge - 8, 8));
            }

            return;
        }

        for (var subband = 1; subband <= boundaries; subband++)
        {
            var edge = subband * 18;
            for (var i = 0; i < 8; i++)
            {
                var lower = spectrum[edge - 1 - i];
                var upper = spectrum[edge + i];
                spectrum[edge - 1 - i] = (lower * cs[i]) - (upper * ca[i]);
                spectrum[edge + i] = (upper * cs[i]) + (lower * ca[i]);
            }
        }
    }

    private void Hybrid(GranuleInfo info, float[] spectrum, float[] overlap, int limit)
    {
        Span<float> buffer = stackalloc float[36];
        var shortBlocks = info.WindowSwitching && info.BlockType == 2;
        var longType = info.WindowSwitching ? info.BlockType : 0;
        var output = _hybrid;
        for (var subband = 0; subband < 32; subband++)
        {
            var offset = subband * 18;
            var invert = (subband & 1) == 1;
            if (subband < limit && !(shortBlocks && !(info.Mixed && subband < 2)))
            {
                Mp3Imdct.LongWithOverlap(spectrum, offset, output, subband, overlap, Mp3DecoderTables.LongWindows[shortBlocks ? 0 : longType], invert);
                continue;
            }

            var previous = overlap.AsSpan(offset, 18);
            if (subband >= limit)
            {
                for (var i = 0; i < 18; i++)
                {
                    output[(i * 32) + subband] = invert && (i & 1) == 1 ? -previous[i] : previous[i];
                }

                previous.Clear();
                continue;
            }

            Mp3Imdct.Short(spectrum.AsSpan(offset, 18), buffer);
            for (var i = 0; i < 18; i++)
            {
                var value = buffer[i] + previous[i];
                output[(i * 32) + subband] = invert && (i & 1) == 1 ? -value : value;
                previous[i] = buffer[18 + i];
            }
        }
    }

    private sealed class GranuleInfo
    {
        public int Part23Length { get; set; }

        public int BigValues { get; set; }

        public int GlobalGain { get; set; }

        public int ScalefacCompress { get; set; }

        public bool WindowSwitching { get; set; }

        public int BlockType { get; set; }

        public bool Mixed { get; set; }

        public int TableSelect0 { get; set; }

        public int TableSelect1 { get; set; }

        public int TableSelect2 { get; set; }

        public int SubblockGain0 { get; set; }

        public int SubblockGain1 { get; set; }

        public int SubblockGain2 { get; set; }

        public int Region0End { get; set; }

        public int Region1End { get; set; }

        public bool Preflag { get; set; }

        public bool ScalefacScale { get; set; }

        public int Count1Table { get; set; }

        public int IntensityScale { get; set; }
    }
}
