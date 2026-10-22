using System.Diagnostics;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpServerFfmpegOracleTest
    {
        // Env-gated cross-check (docs/live-ingest-restream-plan.md item 4): set
        // EGGENCODER_FFMPEG_ORACLE=1 with ffmpeg on PATH to run it; passes trivially otherwise, so CI
        // needs no ffmpeg. A real `ffmpeg -re ... -c copy -f flv rtmp://...` publish must arrive intact.
        [Fact]
        public async Task FfmpegPublish_ToRtmpServer_Should_Deliver_Exactly_The_Fixture_Packets()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var startInfo = new ProcessStartInfo(
                "ffmpeg",
                $"-hide_banner -loglevel error -re -i \"{FlvDemuxerTest.H264AacFixture}\" -c copy -f flv rtmp://127.0.0.1:{server.LocalEndPoint!.Port}/live/streamkey")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo)!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, errors);
            var stream = await consumer.Started;
            stream.AppName.Should().Be("live");
            stream.StreamKey.Should().Be("streamkey");
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture)).Packets);
            stream.Metadata!.Encoder.Should().StartWith("Lavf");
        }
    }
}
