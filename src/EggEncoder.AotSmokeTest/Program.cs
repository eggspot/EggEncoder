using EggEncoder;
using Microsoft.Extensions.Logging.Abstractions;

// Exercises EggEncoder's riskiest-for-AOT paths — LibraryImport P/Invoke into libmp3lame/libFLAC,
// the UnmanagedCallersOnly + GCHandle FLAC decode callback, and the NLayer-backed MP3 decode —
// under a real `dotnet publish -p:PublishAot=true` binary, not just under the JIT.
var sourceWav = Path.Combine(AppContext.BaseDirectory, "sample.wav");
var workingDirectory = Path.Combine(Path.GetTempPath(), $"EggEncoderAotSmoke_{Guid.NewGuid():N}");
Directory.CreateDirectory(workingDirectory);

var encoder = new NativeEncoder(NullLogger<NativeEncoder>.Instance, enableLogging: false);

try
{
    var mp3Path = Path.Combine(workingDirectory, "out.mp3");
    var flacPath = Path.Combine(workingDirectory, "out.flac");
    var cutPath = Path.Combine(workingDirectory, "cut.wav");

    await RunCheck("Probe source WAV", () => encoder.Probe(sourceWav));
    await RunCheck("Convert WAV -> MP3 (libmp3lame P/Invoke encode)", () => encoder.ConvertFile(sourceWav, mp3Path));
    await RunCheck("Probe + decode produced MP3 (NLayer decode)", () => encoder.Probe(mp3Path));
    await RunCheck("Convert WAV -> FLAC (libFLAC P/Invoke encode)", () => encoder.ConvertFile(sourceWav, flacPath));
    await RunCheck("Probe + decode produced FLAC (UnmanagedCallersOnly callback)", () => encoder.Probe(flacPath));
    await RunCheck("Cut source WAV", () => encoder.CutFile(sourceWav, cutPath, 0, 1));

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
