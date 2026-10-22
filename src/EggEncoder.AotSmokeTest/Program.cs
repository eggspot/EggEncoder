using EggEncoder;
using Microsoft.Extensions.Logging.Abstractions;

// Exercises EggEncoder's riskiest-for-AOT paths now that every codec is pure managed C# (no
// native binary, no P/Invoke, anywhere in this project) — the NLayer-backed MP3 decode
// dependency in particular ships no AOT/trim metadata of its own, and this project's own managed
// FLAC/MP3/WavPack encode+decode round trips need to keep working after trimming — under a real
// `dotnet publish -p:PublishAot=true` binary for each of this project's supported RIDs, not just
// under the JIT.
var sourceWav = Path.Combine(AppContext.BaseDirectory, "sample.wav");
var workingDirectory = Path.Combine(Path.GetTempPath(), $"EggEncoderAotSmoke_{Guid.NewGuid():N}");
Directory.CreateDirectory(workingDirectory);

var encoder = new NativeEncoder(NullLogger<NativeEncoder>.Instance, enableLogging: false);

try
{
    var mp3Path = Path.Combine(workingDirectory, "out.mp3");
    var flacPath = Path.Combine(workingDirectory, "out.flac");
    var wvPath = Path.Combine(workingDirectory, "out.wv");
    var cutPath = Path.Combine(workingDirectory, "cut.wav");

    await RunCheck("Probe source WAV", () => encoder.Probe(sourceWav));
    await RunCheck("Convert WAV -> MP3 (pure managed encode, no native call)", () => encoder.ConvertFile(sourceWav, mp3Path));
    await RunCheck("Probe + decode produced MP3 (NLayer decode)", () => encoder.Probe(mp3Path));
    await RunCheck("Convert WAV -> FLAC (pure managed encode, no native call)", () => encoder.ConvertFile(sourceWav, flacPath));
    await RunCheck("Probe + decode produced FLAC (pure managed decode, no native call)", () => encoder.Probe(flacPath));
    await RunCheck("Convert WAV -> WavPack (pure managed encode, no native call)", () => encoder.ConvertFile(sourceWav, wvPath));
    await RunCheck("Probe + decode produced WavPack (pure managed decode, no native call)", () => encoder.Probe(wvPath));
    await RunCheck("Cut source WAV", () => encoder.CutFile(sourceWav, cutPath, 0, 1));
    await RunCheck("Media packet model: avcC/SPS parse, Annex B round trip, timestamp rescale", () =>
    {
        var avcC = Convert.FromHexString("01640028ffe1001b67640028acd940780227e5c044000003000400000300c83c60c65801000668ebe3cb22c0fdf8f800");
        var record = EggEncoder.Media.AvcDecoderConfigurationRecord.Parse(avcC);
        var sps = EggEncoder.Media.H264SpsParser.Parse(record.SequenceParameterSets[0].Span);
        byte[] accessUnit = [0, 0, 0, 2, 0x65, 0x88];
        var annexB = EggEncoder.Media.AnnexBConverter.ToAnnexB(accessUnit);
        if (sps.Width != 1920 || sps.Height != 1080 || sps.CodecsString != "avc1.640028"
            || !record.ToArray().AsSpan().SequenceEqual(avcC)
            || !EggEncoder.Media.AnnexBConverter.ToLengthPrefixed(annexB).AsSpan().SequenceEqual(accessUnit)
            || EggEncoder.Media.MediaTimestamp.Rescale(1024, 44100, 90000) != 2090
            || !HevcCheckPasses())
        {
            throw new InvalidOperationException("Media packet model check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    Console.WriteLine("All AOT smoke checks passed.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"AOT smoke test FAILED: {e}");
    return 1;
}
finally
{
    Directory.Delete(workingDirectory, recursive: true);
}

static async Task RunCheck(string name, Func<Task> action)
{
    Console.WriteLine($"-> {name}");
    await action();
}

static bool HevcCheckPasses()
{
    var hvcC = Convert.FromHexString("01016000000090000000000078f000fcfdf8f800000f03a00001001840010c01ffff016000000300900000030000030078959809a10001002a420101016000000300900000030000030078a003c08010e596566924caf0168080000003008000000c84a2000100074401c172b46240");
    var record = EggEncoder.Media.HevcDecoderConfigurationRecord.Parse(hvcC);
    var sps = EggEncoder.Media.H265SpsParser.Parse(record.GetNalUnits(EggEncoder.Media.HevcDecoderConfigurationRecord.SpsNalUnitType)[0].Span);
    return sps.Width == 1920 && sps.Height == 1080 && sps.CodecsString == "hvc1.1.6.L120.90"
        && record.ToArray().AsSpan().SequenceEqual(hvcC);
}
