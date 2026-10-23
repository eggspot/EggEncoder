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

    await RunCheck("FLV: write H.264 + AAC packets with onMetaData (AMF0), demux them back", async () =>
    {
        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var tracks = new EggEncoder.Media.MediaTrackSet(
        [
            new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0, width: 64, height: 64),
            new EggEncoder.Media.MediaTrackInfo(1, EggEncoder.Media.MediaKind.Audio, EggEncoder.Media.MediaCodec.Aac, 44100, new byte[] { 0x12, 0x08 }, 0, 44100, 1)
        ]);
        var stream = new MemoryStream();
        await using (var writer = new EggEncoder.Containers.Flv.FlvWriter(stream, leaveOpen: true))
        {
            await writer.OpenAsync(tracks);
            await writer.WriteAsync(new EggEncoder.Media.MediaPacket(0, 80, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }));
            await writer.WriteAsync(new EggEncoder.Media.MediaPacket(1, 1024, 1024, true, 0, new byte[] { 0x21 }));
            await writer.CompleteAsync();
        }

        stream.Position = 0;
        var packets = new List<EggEncoder.Media.MediaPacket>();
        await using var source = new EggEncoder.Containers.Flv.FlvFileSource(stream);
        await foreach (var packet in source.ReadPacketsAsync())
        {
            packets.Add(packet);
        }

        if (packets.Count != 2 || packets[0].CompositionOffset != 80 || packets[1].Dts != 23
            || source.Tracks!.Tracks.Count != 2 || source.Tracks.Tracks[0].Width != 64 || source.Metadata?.Width != 64)
        {
            throw new InvalidOperationException("FLV round trip produced unexpected results");
        }
    });

    await RunCheck("fMP4: multi-track H.264 + AAC init segment and a packet media segment", () =>
    {
        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var tracks = new EggEncoder.Media.MediaTrackSet(
        [
            new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0),
            new EggEncoder.Media.MediaTrackInfo(1, EggEncoder.Media.MediaKind.Audio, EggEncoder.Media.MediaCodec.Aac, 1000, new byte[] { 0x12, 0x08 }, 0)
        ]);
        var init = EggEncoder.Streaming.Mp4.FmpInitSegmentWriter.BuildInitSegment(tracks);
        var writer = new EggEncoder.Streaming.Mp4.FmpMediaSegmentWriter(tracks);
        var segment = writer.BuildMediaSegment(
        [
            new EggEncoder.Media.MediaPacket(0, 80, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }),
            new EggEncoder.Media.MediaPacket(1, 0, 0, true, 0, new byte[] { 0x21 }),
            new EggEncoder.Media.MediaPacket(0, 120, 40, false, 0, new byte[] { 0, 0, 0, 1, 0x41 })
        ]);
        // The 64x64 dimensions come from the SPS (the track says 0); the segment's mdat holds the
        // video payloads, then the audio one.
        var initHex = Convert.ToHexString(init);
        if (!initHex.Contains(Convert.ToHexString("avcC"u8))
            || !initHex.Contains("0040000000400000") // tkhd width/height: 64 << 16
            || !Convert.ToHexString(segment).EndsWith("0000000165" + "0000000141" + "21"))
        {
            throw new InvalidOperationException("fMP4 packet mode produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("MPEG-TS: H.264 + AAC packet segment with Annex B, ADTS and PCR", () =>
    {
        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var tracks = new EggEncoder.Media.MediaTrackSet(
        [
            new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0),
            new EggEncoder.Media.MediaTrackInfo(1, EggEncoder.Media.MediaKind.Audio, EggEncoder.Media.MediaCodec.Aac, 1000, new byte[] { 0x12, 0x08 }, 0)
        ]);
        var muxer = new EggEncoder.Streaming.MpegTs.MpegTsMuxer(tracks);
        var segment = muxer.BuildSegment(
        [
            new EggEncoder.Media.MediaPacket(1, 0, 0, true, 0, new byte[] { 0x21 }),
            new EggEncoder.Media.MediaPacket(0, 80, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 })
        ]);
        // PAT, PMT, a PCR-only packet on the video PID (the segment starts with audio: equal DTS
        // keeps the given order), the audio PES (ADTS), then the video PES: AUD, SPS, PPS, IDR.
        var hex = Convert.ToHexString(segment);
        if (segment.Length != 5 * 188 || segment[(2 * 188) + 3] != 0x2F || (segment[(4 * 188) + 1] & 0x40) == 0
            || !hex.Contains("0000000109F0" + "00000001674D400A")
            || !hex.Contains("00000001" + "68CE0FC8" + "0000000165")
            || !hex.Contains("FFF1504001"))
        {
            throw new InvalidOperationException("MPEG-TS packet mode produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("HLS: packet-driven segmenter through HlsMediaPacketSink, cut at video keyframes", async () =>
    {
        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var tracks = new EggEncoder.Media.MediaTrackSet(
        [
            new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0),
            new EggEncoder.Media.MediaTrackInfo(1, EggEncoder.Media.MediaKind.Audio, EggEncoder.Media.MediaCodec.Aac, 1000, new byte[] { 0x12, 0x08 }, 0)
        ]);
        var sink = new EggEncoder.Streaming.InMemoryStreamingSink();
        await using var packetSink = new EggEncoder.Streaming.Hls.HlsMediaPacketSink(sink, 1);
        await packetSink.OpenAsync(tracks);
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(1, 0, 0, true, 0, new byte[] { 0x21 }));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 0, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 500, 500, false, 0, new byte[] { 0, 0, 0, 1, 0x41 }));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 1000, 1000, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }));
        await packetSink.CompleteAsync();

        var playlist = System.Text.Encoding.UTF8.GetString(sink.Playlist!);
        if (sink.MediaSegments.Count != 2 || packetSink.Segmenter!.DroppedLeadingPacketCount != 1
            || !playlist.Contains("#EXTINF:1.000000,\nsegment_1.m4s\n#EXTINF:0.500000,\nsegment_2.m4s\n#EXT-X-ENDLIST")
            || EggEncoder.Streaming.Hls.M3u8MasterPlaylistWriter.BuildCodecsString(tracks) != "avc1.4D400A,mp4a.40.2")
        {
            throw new InvalidOperationException("Packet-driven HLS produced unexpected results");
        }
    });

    await RunCheck("RTMP core: handshake, then connect/createStream/publish and media through the chunk writer/reader", () =>
    {
        var client = EggEncoder.Live.Rtmp.RtmpHandshake.CreateClient();
        var server = EggEncoder.Live.Rtmp.RtmpHandshake.CreateServer();
        server.Start();
        server.Process(client.Process(server.Process(client.Start())));

        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new EggEncoder.Live.Rtmp.RtmpChunkWriter();
        writer.WriteSetChunkSize(output, 4096);
        writer.Write(output, EggEncoder.Live.Rtmp.RtmpCommand.ChunkStreamId, EggEncoder.Live.Rtmp.RtmpCommand.Connect(1, "live", "rtmp://127.0.0.1/live").ToMessage());
        writer.Write(output, EggEncoder.Live.Rtmp.RtmpCommand.ChunkStreamId, EggEncoder.Live.Rtmp.RtmpCommand.CreateStream(2).ToMessage());
        writer.Write(output, 8, EggEncoder.Live.Rtmp.RtmpCommand.Publish("key").ToMessage(streamId: 1));
        writer.Write(output, 6, new EggEncoder.Live.Rtmp.RtmpMessage(EggEncoder.Live.Rtmp.RtmpMessageType.Video, 1, 0x01000000, new byte[10_000]));

        var commands = new List<string>();
        var videoBytes = 0;
        var reader = new EggEncoder.Live.Rtmp.RtmpChunkReader(message =>
        {
            if (message.Type == EggEncoder.Live.Rtmp.RtmpMessageType.CommandAmf0)
            {
                commands.Add(EggEncoder.Live.Rtmp.RtmpCommand.Decode(message.Payload.Span).Name);
            }
            else if (message.Type == EggEncoder.Live.Rtmp.RtmpMessageType.Video && message.Timestamp == 0x01000000)
            {
                videoBytes = message.Payload.Length;
            }
        });
        reader.Feed(output.WrittenSpan);

        if (!client.IsComplete || !server.IsComplete || reader.ChunkSize != 4096 || videoBytes != 10_000
            || string.Join(",", commands) != "connect,createStream,publish")
        {
            throw new InvalidOperationException("RTMP core check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("RTMP server: loopback publish of H.264 + AAC, received as packets", async () =>
    {
        await using var server = new EggEncoder.Live.Rtmp.RtmpServer(new EggEncoder.Live.Rtmp.RtmpServerOptions
        {
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
            AuthorizePublishAsync = (request, _) => ValueTask.FromResult(request.StreamKey == "key")
        });
        var received = new TaskCompletionSource<List<EggEncoder.Media.MediaPacket>>();
        server.PublishStarted += (_, stream) => _ = Task.Run(async () =>
        {
            var packets = new List<EggEncoder.Media.MediaPacket>();
            await foreach (var packet in stream.ReadPacketsAsync())
            {
                packets.Add(packet);
            }

            received.SetResult(packets);
        });
        await server.StartAsync();

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(server.LocalEndPoint!);
        var network = client.GetStream();
        var handshake = EggEncoder.Live.Rtmp.RtmpHandshake.CreateClient();
        await network.WriteAsync(handshake.Start());
        var reply = new byte[handshake.BytesExpected];
        await network.ReadExactlyAsync(reply);
        await network.WriteAsync(handshake.Process(reply));

        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new EggEncoder.Live.Rtmp.RtmpChunkWriter();
        writer.WriteSetChunkSize(output, 4096);
        writer.Write(output, 3, EggEncoder.Live.Rtmp.RtmpCommand.Connect(1, "live", "rtmp://127.0.0.1/live").ToMessage());
        writer.Write(output, 3, EggEncoder.Live.Rtmp.RtmpCommand.CreateStream(2).ToMessage());
        writer.Write(output, 8, EggEncoder.Live.Rtmp.RtmpCommand.Publish("key").ToMessage(streamId: 1));
        writer.Write(output, 6, new EggEncoder.Live.Rtmp.RtmpMessage(EggEncoder.Live.Rtmp.RtmpMessageType.Video, 1, 0, (byte[])[0x17, 0, 0, 0, 0, .. avcC]));
        writer.Write(output, 4, new EggEncoder.Live.Rtmp.RtmpMessage(EggEncoder.Live.Rtmp.RtmpMessageType.Audio, 1, 0, new byte[] { 0xAF, 0, 0x12, 0x08 }));
        writer.Write(output, 6, new EggEncoder.Live.Rtmp.RtmpMessage(EggEncoder.Live.Rtmp.RtmpMessageType.Video, 1, 0, new byte[] { 0x17, 1, 0, 0, 0x50, 0, 0, 0, 1, 0x65 }));
        writer.Write(output, 4, new EggEncoder.Live.Rtmp.RtmpMessage(EggEncoder.Live.Rtmp.RtmpMessageType.Audio, 1, 23, new byte[] { 0xAF, 1, 0x21 }));
        writer.Write(output, 3, EggEncoder.Live.Rtmp.RtmpCommand.DeleteStream(3, 1).ToMessage());
        await network.WriteAsync(output.WrittenMemory);

        var packets = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (packets.Count != 2 || packets[0].CompositionOffset != 80 || packets[1].Dts != 23)
        {
            throw new InvalidOperationException("RTMP server check produced unexpected results");
        }
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
