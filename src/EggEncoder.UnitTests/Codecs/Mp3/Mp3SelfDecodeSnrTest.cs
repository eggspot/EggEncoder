using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    // Scratch: a from-scratch, independent IMDCT + overlap-add + synthesis-filter decoder (NOT
    // calling any of this project's own decode-side code at all) used to measure SNR against the
    // SAME methodology Mp3EncoderSnrTest uses against NLayer/ffmpeg. If this independent decode
    // gets good SNR but NLayer/ffmpeg don't, the bug is in bitstream framing/interpretation, not
    // DSP content. Deleted once the encoder is fixed.
    public class Mp3SelfDecodeSnrTest
    {
        [Fact]
        public void SelfDecode_Tone_Should_MeetSnrFloor()
        {
            const int sampleRate = 44100;
            const int channels = 1;
            const int seconds = 2;
            var frameCount = sampleRate * seconds;
            var original = new int[frameCount];
            for (var i = 0; i < frameCount; i++)
            {
                original[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var path = Path.Combine(Path.GetTempPath(), $"selfdecode_{Guid.NewGuid():N}.mp3");
            try
            {
                using (var session = Mp3Encoder.OpenSession(path, channels, sampleRate, bitsPerSample: 16, bitRateKbps: 128))
                {
                    session.WriteInterleavedSamples(original, original.Length / channels);
                    session.Finish();
                }

                var decoded = SelfDecode(path, channels);

                var offset = FindBestAlignmentOffset(original, decoded, sampleRate / 4);
                var compareLength = Math.Min(decoded.Count - offset, original.Length);
                Assert.True(compareLength > 1000);

                double dot = 0, origEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dot += decoded[offset + i] * (double)original[i];
                    origEnergy += (double)original[i] * original[i];
                }

                var scale = dot / origEnergy;
                double errorEnergy = 0, signalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    var expected = original[i] * scale;
                    var error = decoded[offset + i] - expected;
                    errorEnergy += error * error;
                    signalEnergy += expected * expected;
                }

                var snrDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));
                Console.WriteLine($"Self-decode SNR: {snrDb:F1} dB, offset={offset}, scale={scale:F4}");
                Assert.True(snrDb > 10, $"expected SNR > 10dB, got {snrDb:F1}dB (offset={offset}, scale={scale})");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static List<double> SelfDecode(string path, int channels)
        {
            var data = File.ReadAllBytes(path);
            var c = new[] { -0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037 };
            var cs = new double[8];
            var ca = new double[8];
            for (var i = 0; i < 8; i++)
            {
                cs[i] = 1.0 / Math.Sqrt(1 + (c[i] * c[i]));
                ca[i] = c[i] / Math.Sqrt(1 + (c[i] * c[i]));
            }

            var sineWindow = new double[36];
            for (var n = 0; n < 36; n++)
            {
                sineWindow[n] = Math.Sin((Math.PI / 36) * (n + 0.5));
            }

            var overlapHistory = new double[32][];
            for (var sb = 0; sb < 32; sb++)
            {
                overlapHistory[sb] = new double[18];
            }

            var synthesisV = new double[1024];
            var output = new List<double>();

            var pos = 0;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF || (data[pos + 1] & 0xE0) != 0xE0)
                {
                    break;
                }

                var bitrateIdx = (data[pos + 2] >> 4) & 0xF;
                var srIdx = (data[pos + 2] >> 2) & 0x3;
                var padding = (data[pos + 2] >> 1) & 0x1;
                var mode = (data[pos + 3] >> 6) & 0x3;
                var frameChannels = mode == 3 ? 1 : 2;
                if (frameChannels != channels)
                {
                    break;
                }

                var bitrates = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
                var srates = new[] { 44100, 48000, 32000 };
                if (bitrateIdx is 0 or > 14 || srIdx > 2)
                {
                    break;
                }

                var frameLen = (144 * bitrates[bitrateIdx] * 1000 / srates[srIdx]) + padding;
                var reader = new BitReader(data, pos);
                reader.SkipBits(32);
                reader.SkipBits(9); // main_data_begin
                reader.SkipBits(channels == 1 ? 5 : 3); // private_bits
                reader.SkipBits(channels * 4); // scfsi

                var granules = new (int BigValues, int GlobalGain, int TableSelect)[2, 2];
                for (var gr = 0; gr < 2; gr++)
                {
                    for (var ch = 0; ch < channels; ch++)
                    {
                        reader.SkipBits(12);
                        var bv = (int)reader.ReadBits(9);
                        var gg = (int)reader.ReadBits(8);
                        reader.SkipBits(4);
                        reader.SkipBits(1);
                        var ts = (int)reader.ReadBits(5);
                        reader.SkipBits(10);
                        reader.SkipBits(7);
                        reader.SkipBits(3);
                        granules[gr, ch] = (bv, gg, ts);
                    }
                }

                for (var gr = 0; gr < 2; gr++)
                {
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var (bigValues, globalGain, tableSelect) = granules[gr, ch];
                        var xr = new double[576];
                        if (bigValues > 0)
                        {
                            var (table, width, linbits) = Mp3HuffmanTables.GetBigValueTable(tableSelect);
                            var maxDirect = width - 1;
                            var scale = Math.Pow(2.0, (globalGain - 210) / 4.0);
                            for (var pair = 0; pair < bigValues; pair++)
                            {
                                var index = table.Decode(reader);
                                var codeX = index / width;
                                var codeY = index % width;

                                var x = codeX;
                                if (codeX == maxDirect && linbits > 0)
                                {
                                    x += (int)reader.ReadBits(linbits);
                                }

                                if (x != 0 && reader.ReadBits(1) != 0)
                                {
                                    x = -x;
                                }

                                var y = codeY;
                                if (codeY == maxDirect && linbits > 0)
                                {
                                    y += (int)reader.ReadBits(linbits);
                                }

                                if (y != 0 && reader.ReadBits(1) != 0)
                                {
                                    y = -y;
                                }

                                var magX = Math.Pow(Math.Abs(x), 4.0 / 3.0) * scale;
                                var magY = Math.Pow(Math.Abs(y), 4.0 / 3.0) * scale;
                                xr[pair * 2] = x < 0 ? -magX : magX;
                                xr[(pair * 2) + 1] = y < 0 ? -magY : magY;
                            }
                        }

                        for (var sb = 1; sb < 32; sb++)
                        {
                            for (var i = 0; i < 8; i++)
                            {
                                var lowerIndex = (18 * sb) - 1 - i;
                                var upperIndex = (18 * sb) + i;
                                var lower = xr[lowerIndex];
                                var upper = xr[upperIndex];
                                xr[lowerIndex] = (lower * cs[i]) - (upper * ca[i]);
                                xr[upperIndex] = (upper * cs[i]) + (lower * ca[i]);
                            }
                        }

                        for (var t = 0; t < 18; t++)
                        {
                            var subbandTick = new double[32];
                            for (var sb = 0; sb < 32; sb++)
                            {
                                var coeffs = new double[18];
                                Array.Copy(xr, sb * 18, coeffs, 0, 18);
                                var timeSamples = Mdct.Inverse(coeffs);
                                for (var n = 0; n < 36; n++)
                                {
                                    timeSamples[n] *= sineWindow[n];
                                }

                                var hist = overlapHistory[sb];
                                var value = timeSamples[t] + hist[t];
                                if (t == 17)
                                {
                                    Array.Copy(timeSamples, 18, hist, 0, 18);
                                }

                                if (sb % 2 == 1 && t % 2 == 1)
                                {
                                    value = -value;
                                }

                                subbandTick[sb] = value;
                            }

                            var pcm = SynthesisFilter(subbandTick, synthesisV);
                            output.AddRange(pcm.Select(v => (double)v));
                        }
                    }
                }

                pos += frameLen;
            }

            return output;
        }

        private static int[] SynthesisFilter(double[] subbandSamples, double[] synthesisV)
        {
            var newV = new double[64];
            for (var i = 0; i < 64; i++)
            {
                double sum = 0;
                for (var k = 0; k < 32; k++)
                {
                    sum += Math.Cos((16 + i) * ((2 * k) + 1) * Math.PI / 64.0) * subbandSamples[k];
                }

                newV[i] = sum;
            }

            Array.Copy(synthesisV, 0, synthesisV, 64, 1024 - 64);
            Array.Copy(newV, 0, synthesisV, 0, 64);

            var u = new double[512];
            for (var i = 0; i < 8; i++)
            {
                for (var j = 0; j < 32; j++)
                {
                    u[(64 * i) + j] = synthesisV[(128 * i) + j];
                    u[(64 * i) + 32 + j] = synthesisV[(128 * i) + 96 + j];
                }
            }

            var w = new double[512];
            for (var i = 0; i < 512; i++)
            {
                w[i] = u[i] * Mp3Tables.WindowCoefficients[i];
            }

            var samples = new int[32];
            for (var j = 0; j < 32; j++)
            {
                double sum = 0;
                for (var i = 0; i < 16; i++)
                {
                    sum += w[j + (32 * i)];
                }

                samples[j] = (int)Math.Round(sum);
            }

            return samples;
        }

        private static int FindBestAlignmentOffset(int[] original, List<double> decoded, int maxOffset)
        {
            var compareLength = Math.Min(2000, original.Length);
            var bestOffset = 0;
            var bestCorrelation = double.MinValue;

            for (var offset = 0; offset <= maxOffset && offset + compareLength <= decoded.Count; offset++)
            {
                double dot = 0, origEnergy = 0, decEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dot += decoded[offset + i] * original[i];
                    origEnergy += (double)original[i] * original[i];
                    decEnergy += decoded[offset + i] * decoded[offset + i];
                }

                var normalized = origEnergy > 0 && decEnergy > 0 ? dot / Math.Sqrt(origEnergy * decEnergy) : 0;
                if (normalized > bestCorrelation)
                {
                    bestCorrelation = normalized;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }
    }
}
