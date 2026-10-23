using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EggEncoder.Live.Rtmp;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpPublishClientFfmpegOracleTest
    {
        [Fact]
        public async Task Publish_ToFfmpegListener_Should_Be_Recorded_With_Every_Packet()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var fixture = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));
            var port = FreePort();
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-rtmp-{Guid.NewGuid():N}.flv");
            using var ffmpeg = Process.Start(new ProcessStartInfo(
                "ffmpeg",
                $"-hide_banner -loglevel error -listen 1 -i rtmp://127.0.0.1:{port}/live/streamkey -c copy -f flv \"{path}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            try
            {
                await using (var client = await OpenWhenListeningAsync(port, fixture))
                {
                    foreach (var packet in fixture.Packets)
                    {
                        await client.WriteAsync(packet);
                    }

                    await client.CompleteAsync();
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var errors = await ffmpeg.StandardError.ReadToEndAsync(timeout.Token);
                await ffmpeg.WaitForExitAsync(timeout.Token);
                ffmpeg.ExitCode.Should().Be(0, errors);

                var probe = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -show_entries packet=stream_index -of compact=p=0 \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                })!;
                var output = await probe.StandardOutput.ReadToEndAsync(timeout.Token);
                await probe.WaitForExitAsync(timeout.Token);
                probe.ExitCode.Should().Be(0);
                output.Should().Contain("codec_name=h264|width=64|height=64");
                output.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");
                output.Split('\n').Count(l => l.StartsWith("stream_index=")).Should().Be(fixture.Packets.Count);

                var recorded = FlvDemuxerTest.Demux(await File.ReadAllBytesAsync(path, timeout.Token)).Packets;
                recorded.Select(p => p.Payload.ToArray()).Should().BeEquivalentTo(fixture.Packets.Select(p => p.Payload.ToArray()), o => o.WithStrictOrdering());
            }
            finally
            {
                if (!ffmpeg.HasExited)
                {
                    ffmpeg.Kill();
                }

                File.Delete(path);
            }
        }

        private static async Task<RtmpPublishClient> OpenWhenListeningAsync(int port, FlvDemuxerTest.Capture fixture)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var client = new RtmpPublishClient(new Uri($"rtmp://127.0.0.1:{port}/live"), "streamkey");
                try
                {
                    await client.OpenAsync(fixture.TrackSets[^1]);
                    return client;
                }
                catch (RtmpClientException e) when (e.Reason == RtmpClientFailureReason.ConnectFailed && DateTime.UtcNow < deadline)
                {
                    await client.DisposeAsync();
                    await Task.Delay(100);
                }
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            listener.Dispose();
            return port;
        }
    }
}
