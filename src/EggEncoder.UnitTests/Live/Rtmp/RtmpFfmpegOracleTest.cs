using System.Diagnostics;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpFfmpegOracleTest
    {
        // Env-gated cross-check: set EGGENCODER_FFMPEG_ORACLE=1 with ffmpeg on PATH to run it; passes
        // trivially otherwise, so CI needs no ffmpeg. Setting EGGENCODER_RTMP_CAPTURE_PATH as well
        // writes every byte ffmpeg sent, which is how ffmpeg_publish.bin was captured.
        [Fact]
        public async Task FfmpegPublish_ToLoopbackReceiver_Should_Deliver_Commands_Metadata_And_Every_Tag()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            using var receiver = new RtmpTestReceiver();
            var receive = receiver.RunAsync(TimeSpan.FromSeconds(20));
            var startInfo = new ProcessStartInfo(
                "ffmpeg",
                $"-hide_banner -loglevel error -i \"{FlvDemuxerTest.H264AacFixture}\" -c copy -f flv rtmp://127.0.0.1:{receiver.Port}/live/streamkey")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo)!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await receive;

            var capturePath = Environment.GetEnvironmentVariable("EGGENCODER_RTMP_CAPTURE_PATH");
            if (!string.IsNullOrEmpty(capturePath))
            {
                File.WriteAllBytes(capturePath, receiver.ClientBytes.ToArray());
            }

            process.ExitCode.Should().Be(0, errors);
            RtmpFfmpegCaptureTest.AssertPublishSession(receiver.Messages);
        }
    }
}
