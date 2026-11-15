using EggEncoder;
using Microsoft.Extensions.Logging.Abstractions;

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
    await RunCheck("Probe + decode produced MP3 (pure managed decode)", () => encoder.Probe(mp3Path));
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

    await RunCheck("MP4 file source: H.265 track and packet timestamps from a progressive MP4", () =>
    {
        using var mp4Source = EggEncoder.Containers.Mp4.Mp4FileSource.Open(Path.Combine(AppContext.BaseDirectory, "hevc.mp4"));
        var mp4Packets = mp4Source.ReadPackets().ToList();
        if (mp4Source.Tracks.Tracks.Count != 1 || mp4Packets.Count != 3 || mp4Packets[1].Pts != 512
            || mp4Source.Tracks.Tracks[0].Codec != EggEncoder.Media.MediaCodec.H265)
        {
            throw new InvalidOperationException("MP4 file source check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("MP4 writer: remux the H.265 fixture with Mp4Writer, split and join it back", () =>
    {
        using var input = EggEncoder.Containers.Mp4.Mp4FileSource.Open(Path.Combine(AppContext.BaseDirectory, "hevc.mp4"));
        var written = new MemoryStream();
        using (var mp4Writer = new EggEncoder.Containers.Mp4.Mp4Writer(written, leaveOpen: true, new MemoryStream()))
        {
            mp4Writer.Open(input.Tracks);
            foreach (var packet in input.ReadPackets())
            {
                mp4Writer.Write(packet);
            }

            mp4Writer.Complete();
        }

        var joined = new MemoryStream();
        EggEncoder.Containers.Mp4.Mp4Joiner.Join([new MemoryStream(written.ToArray()), new MemoryStream(written.ToArray())], joined, new MemoryStream());
        var parts = EggEncoder.Containers.Mp4.Mp4Splitter.Split(new MemoryStream(joined.ToArray()), [TimeSpan.FromSeconds(0.1)], _ => new MemoryStream());
        using var check = new EggEncoder.Containers.Mp4.Mp4FileSource(new MemoryStream(joined.ToArray()));
        if (check.ReadPackets().Count() != 6 || parts.Count != 2)
        {
            throw new InvalidOperationException("MP4 writer check produced unexpected results");
        }

        return Task.CompletedTask;
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
        var initHex = Convert.ToHexString(init);
        var tkhdWidthAndHeightOf64FromTheSps = "0040000000400000";
        if (!initHex.Contains(Convert.ToHexString("avcC"u8))
            || !initHex.Contains(tkhdWidthAndHeightOf64FromTheSps)
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

    await RunCheck("HLS: a mid-stream configuration change through HlsMediaPacketSink.UpdateTracks", async () =>
    {
        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var video = new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0);
        var sink = new EggEncoder.Streaming.InMemoryStreamingSink();
        await using var packetSink = new EggEncoder.Streaming.Hls.HlsMediaPacketSink(sink, 1, liveWindowSize: 1);
        await packetSink.OpenAsync(new EggEncoder.Media.MediaTrackSet([video]));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 0, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }));
        packetSink.UpdateTracks(new EggEncoder.Media.MediaTrackSet([video with { ConfigVersion = 1 }]));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 1000, 1000, true, 1, new byte[] { 0, 0, 0, 1, 0x65 }));
        await packetSink.WriteAsync(new EggEncoder.Media.MediaPacket(0, 2000, 2000, true, 1, new byte[] { 0, 0, 0, 1, 0x65 }));
        await packetSink.CompleteAsync();

        var playlist = System.Text.Encoding.UTF8.GetString(sink.Playlist!);
        if (!sink.InitSegments.ContainsKey("init_2.mp4") || sink.InitSegments.ContainsKey("init.mp4")
            || !playlist.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:1\n#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:1.000000,\nsegment_3.m4s\n#EXT-X-ENDLIST"))
        {
            throw new InvalidOperationException("An HLS configuration change produced unexpected results");
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

    await RunCheck("RTMP client: RtmpPublishClient publishes H.264 + AAC to the loopback RtmpServer", async () =>
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

        var avcC = Convert.FromHexString("014d400affe10017674d400aeca2136022000003000200000300641e244b2c01000468ce0fc8");
        var tracks = new EggEncoder.Media.MediaTrackSet(
        [
            new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, avcC, 0, width: 64, height: 64),
            new EggEncoder.Media.MediaTrackInfo(1, EggEncoder.Media.MediaKind.Audio, EggEncoder.Media.MediaCodec.Aac, 1000, new byte[] { 0x12, 0x08 }, 0, sampleRate: 44100, channels: 1)
        ]);
        await using var client = new EggEncoder.Live.Rtmp.RtmpPublishClient(new Uri($"rtmp://127.0.0.1:{server.LocalEndPoint!.Port}/live"), "key");
        await client.OpenAsync(tracks);
        await client.WriteAsync(new EggEncoder.Media.MediaPacket(0, 80, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 }));
        await client.WriteAsync(new EggEncoder.Media.MediaPacket(1, 23, 23, true, 0, new byte[] { 0x21 }));
        await client.CompleteAsync();

        var packets = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (packets.Count != 2 || packets[0].CompositionOffset != 80 || packets[1].Dts != 23 || client.TcUrl != $"rtmp://127.0.0.1:{server.LocalEndPoint.Port}/live")
        {
            throw new InvalidOperationException("RTMP client check produced unexpected results");
        }
    });

    await RunCheck("Mixing and loudness: PcmMixer, LoudnessMeter, LoudnessNormalizer and PcmAudio conversion", () =>
    {
        var tone = new float[48000 * 2];
        for (var n = 0; n < 48000; n++)
        {
            tone[2 * n] = tone[(2 * n) + 1] = (float)(0.9 * Math.Sin(2 * Math.PI * 1000 * n / 48000));
        }

        var mix = EggEncoder.Pcm.PcmMixer.Mix(
            [new EggEncoder.Pcm.PcmMixInput(new EggEncoder.Pcm.PcmAudio(tone, 2, 48000)), new EggEncoder.Pcm.PcmMixInput(EggEncoder.Pcm.PcmAudio.Silence(1, 44100, TimeSpan.FromSeconds(0.5)), 0.5, TimeSpan.FromSeconds(0.2))],
            EggEncoder.Pcm.PcmMixLength.Longest);
        var normalized = EggEncoder.Pcm.LoudnessNormalizer.Normalize(mix, -16);
        var loudness = EggEncoder.Pcm.LoudnessMeter.MeasureIntegratedLoudness(normalized);
        var speech = normalized.DownmixForSpeech();
        if (mix.FrameCount != 48000 || loudness is null || Math.Abs(loudness.Value + 16) > 0.5
            || EggEncoder.Pcm.LoudnessMeter.MeasureTruePeakDb(normalized) > EggEncoder.Pcm.LoudnessNormalizer.DefaultTruePeakCeilingDb
            || speech.SampleRate != 16000 || speech.ToInt16().Length != 16000)
        {
            throw new InvalidOperationException("Mixing and loudness check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Sidechain ducking: SidechainCompressor.Duck", () =>
    {
        var music = new EggEncoder.Pcm.PcmAudio(Enumerable.Repeat(0.5f, 9600).ToArray(), 2, 48000);
        var voice = new EggEncoder.Pcm.PcmAudio(Enumerable.Repeat(0.5f, 4800).ToArray(), 1, 48000);
        var ducked = EggEncoder.Pcm.SidechainCompressor.Duck(music, voice,
            new EggEncoder.Pcm.SidechainCompressorSettings { Threshold = 0.05, Ratio = 8, AttackMs = 20, ReleaseMs = 400 });
        if (ducked.FrameCount != 4800 || ducked.Samples[^1] >= 0.1f)
        {
            throw new InvalidOperationException("Sidechain ducking check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Video frames: VideoFramePool, VideoFrame, colours, frame rate, pipeline", () =>
    {
        var pool = new EggEncoder.Video.VideoFramePool();
        var frame = pool.Rent(1081, 1919);
        frame.Fill(new EggEncoder.Video.RgbaColor(255, 0, 0).ToYuv(EggEncoder.Video.VideoColorMatrix.Bt709, EggEncoder.Video.VideoColorRange.Limited));
        using var clone = frame.Clone();
        var size = new EggEncoder.Video.VideoFrameTransformPipeline().GetOutputSize(1081, 1919);
        EggEncoder.Video.VideoFrame? output = null;
        new EggEncoder.Video.VideoFrameTransformPipeline().Apply(frame, f => output = f);
        output!.Dispose();
        if (clone.GetRow(EggEncoder.Video.VideoPlane.V, 959)[540] != 240 || clone.ChromaWidth != 541 || size != (1081, 1919)
            || pool.RetainedBufferCount != 1 || new EggEncoder.Video.VideoFrameRate(30000, 1001).TimestampOf(30000) != TimeSpan.FromSeconds(1001))
        {
            throw new InvalidOperationException("Video frame check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Video filters: fit, overlay, blur, fade, frame rate", () =>
    {
        var logo = new EggEncoder.Video.RgbaImage(8, 8);
        logo.Pixels.Fill(255);
        var pipeline = new EggEncoder.Video.VideoFrameTransformPipeline(
            new EggEncoder.Video.Filters.FitTransform(36, 64, EggEncoder.Video.Filters.FitMode.Cover),
            new EggEncoder.Video.Filters.OverlayTransform(logo, 3, 5),
            new EggEncoder.Video.Filters.BoxBlurTransform(2, 1),
            new EggEncoder.Video.Filters.VideoFadeTransform(EggEncoder.Video.Filters.VideoFadeDirection.In, TimeSpan.Zero, TimeSpan.FromSeconds(1), new EggEncoder.Video.YuvColor(16, 128, 128)),
            new EggEncoder.Video.Filters.FrameRateTransform(new EggEncoder.Video.VideoFrameRate(50)));
        var outputs = new List<EggEncoder.Video.VideoFrame>();
        for (var n = 0; n < 3; n++)
        {
            var frame = EggEncoder.Video.VideoFrame.Create(64, 36);
            frame.Fill(new EggEncoder.Video.YuvColor(200, 100, 150));
            frame.Timestamp = TimeSpan.FromMilliseconds(40 * n);
            frame.Duration = TimeSpan.FromMilliseconds(40);
            pipeline.Apply(frame, outputs.Add);
        }

        pipeline.Flush(outputs.Add);
        var size = (outputs[0].Width, outputs[0].Height);
        outputs.ForEach(f => f.Dispose());
        if (outputs.Count != 6 || size != (36, 64) || pipeline.GetOutputSize(64, 36) != (36, 64))
        {
            throw new InvalidOperationException("Video filter check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Video text: TrueTypeFont, TextRenderer, TextOverlayTransform", () =>
    {
        var font = EggEncoder.Video.Text.TrueTypeFont.Load(Convert.FromHexString(
            "00010000000B0080000300304F532F3245D846570000013800000060636D6170001EEDFF000001AC00000064676C7966527075FA0000021C000000846865616430A38B32000000BC000000366868656107330290000000F400000024686D74780CB2003200000198000000146B65726E0039FFBC000002A00000001E6C6F6361004B0074000002100000000C6D617870000C001400000118000000206E616D65CEF41F53000002C000000087706F73746950293A000003480000004500010000000100000EA7219F5F0F3CF5000103E800000000E71EA3CB00000000E71EA3CBFFCE0000038402BC000000030002000000000000000100000320FF38005A03840000FFCE03B600010000000000000000000000000000000500010000000500040001000C00030002000000000000000000000000000200020003028A0190000500040000000000000000000000000000000000000000000000000000000000000000000000010200000000000000000000003F3F3F3F00000061FFFF02EEFF0600640384012C00000000000000000000000000000020000001F40032025800000226000002BC00000384000000000003000000030000001C000300010000001C0003000A0000003C00040020000000040004000100000064FFFF00000061FFFFFFA0000100000000000C00000000002800000000000000020000006100000064000000010001F6000001F600000000010000000D001900250032004200010032000001C2019000030000331121113201900190FE700000010000000001F401F4000300003111211101F401F4FE0C00010000000001F401F4000300003110201101F401F4FE0CFFFF00000000025802BC00270001006400C8000E000200002000FFFFFFCE0000038402BC00260003CE00008700010384000000004000C0000000000000010000001A00010002000C0001000000010002FF9C000200010028000000000004003600010000000000010014000000010000000000020007001400030001040900010028001B0003000104090002000E0043456767456E636F6465722053796E746865746963526567756C61720045006700670045006E0063006F006400650072002000530079006E0074006800650074006900630052006500670075006C006100720000020000000000000000000000000000000000000000000000000000000000000005000001020103010401050673717561726505637572766504636F6D70066E6573746564000000"));
        var renderer = new EggEncoder.Video.Text.TextRenderer(font);
        var text = renderer.Render("abc\nd", new EggEncoder.Video.Text.TextStyle { FontSize = 20, OutlineWidth = 1 });
        var overlay = new EggEncoder.Video.Text.TextOverlayTransform(renderer, "ab", new EggEncoder.Video.Text.TextStyle { FontSize = 20 }, EggEncoder.Video.Text.TextAnchor.TopLeft);
        var frame = EggEncoder.Video.VideoFrame.Create(40, 40);
        frame.Fill(new EggEncoder.Video.YuvColor(16, 128, 128));
        EggEncoder.Video.VideoFrame? output = null;
        overlay.Apply(frame, f => output = f);
        var drawn = output!.GetRow(EggEncoder.Video.VideoPlane.Y, 10)[5];
        output.Dispose();
        if (font.GetKerning(1, 2) != -100 || text.LineCount != 2 || text.Outline is null || drawn != 235)
        {
            throw new InvalidOperationException("Video text check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Subtitles: SubtitleBurnInTransform", () =>
    {
        var font = EggEncoder.Video.Text.TrueTypeFont.Load(Convert.FromHexString(
            "00010000000B0080000300304F532F3245D846570000013800000060636D6170001EEDFF000001AC00000064676C7966527075FA0000021C000000846865616430A38B32000000BC000000366868656107330290000000F400000024686D74780CB2003200000198000000146B65726E0039FFBC000002A00000001E6C6F6361004B0074000002100000000C6D617870000C001400000118000000206E616D65CEF41F53000002C000000087706F73746950293A000003480000004500010000000100000EA7219F5F0F3CF5000103E800000000E71EA3CB00000000E71EA3CBFFCE0000038402BC000000030002000000000000000100000320FF38005A03840000FFCE03B600010000000000000000000000000000000500010000000500040001000C00030002000000000000000000000000000200020003028A0190000500040000000000000000000000000000000000000000000000000000000000000000000000010200000000000000000000003F3F3F3F00000061FFFF02EEFF0600640384012C00000000000000000000000000000020000001F40032025800000226000002BC00000384000000000003000000030000001C000300010000001C0003000A0000003C00040020000000040004000100000064FFFF00000061FFFFFFA0000100000000000C00000000002800000000000000020000006100000064000000010001F6000001F600000000010000000D001900250032004200010032000001C2019000030000331121113201900190FE700000010000000001F401F4000300003111211101F401F4FE0C00010000000001F401F4000300003110201101F401F4FE0CFFFF00000000025802BC00270001006400C8000E000200002000FFFFFFCE0000038402BC00260003CE00008700010384000000004000C0000000000000010000001A00010002000C0001000000010002FF9C000200010028000000000004003600010000000000010014000000010000000000020007001400030001040900010028001B0003000104090002000E0043456767456E636F6465722053796E746865746963526567756C61720045006700670045006E0063006F006400650072002000530079006E0074006800650074006900630052006500670075006C006100720000020000000000000000000000000000000000000000000000000000000000000005000001020103010401050673717561726505637572766504636F6D70066E6573746564000000"));
        var subtitles = new EggEncoder.Video.Text.SubtitleBurnInTransform(
            new EggEncoder.Video.Text.TextRenderer(font),
            [new EggEncoder.Video.Text.SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "{\\b1}a\\Na")],
            new EggEncoder.Video.Text.SubtitleStyle { FontSize = 60 });
        var frame = EggEncoder.Video.VideoFrame.Create(384, 288);
        frame.Fill(new EggEncoder.Video.YuvColor(128, 128, 128));
        EggEncoder.Video.VideoFrame? output = null;
        subtitles.Apply(frame, f => output = f);
        var drawn = output!.GetRow(EggEncoder.Video.VideoPlane.Y, 250)[185];
        output.Dispose();
        if (drawn != 235)
        {
            throw new InvalidOperationException("Subtitle check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 decode: intra pictures from an Annex B stream", () =>
    {
        var stream = Convert.FromHexString("000000016742C00ADC47B011000003000100000300020F1227800000000168CE0164B20000010605FFFF2DDC45E9BDE6D948B7962CD820D923EEEF78323634202D20636F7265203136342072333037392064396131396630202D20482E3236342F4D5045472D342041564320636F646563202D20436F70796C65667420323030332D32303231202D20687474703A2F2F7777772E766964656F6C616E2E6F72672F783236342E68746D6C202D206F7074696F6E733A2063616261633D30207265663D31206465626C6F636B3D313A303A3020616E616C7973653D3078313A3078313131206D653D686578207375626D653D37207073793D31207073795F72643D312E30303A302E3030206D697865645F7265663D30206D655F72616E67653D3136206368726F6D615F6D653D31207472656C6C69733D31203878386463743D302063716D3D3020646561647A6F6E653D32312C313120666173745F70736B69703D31206368726F6D615F71705F6F66667365743D2D3220746872656164733D31206C6F6F6B61686561645F746872656164733D3120736C696365645F746872656164733D30206E723D3020646563696D6174653D3120696E7465726C616365643D3020626C757261795F636F6D7061743D3020636F6E73747261696E65645F696E7472613D3020626672616D65733D3020776569676874703D30206B6579696E743D31206B6579696E745F6D696E3D31207363656E656375743D343020696E7472615F726566726573683D302072633D637170206D62747265653D302071703D34382069705F726174696F3D312E34302061713D3000800000016588843FFC470B629C565C2F3F910BF8DCA5373CAA4CA69420889943140A0136E48701A0630D456490109B29203E572FC462231118631D3CBBFA9549BD2E4F8C607B40B3D3B2B97C1D1B5FDEFC0EF8BD76B1CDFE2387C507E238D6108C78E52F8426A9D281803430B807D0AC76034E0FD215941CB37CE1B096EA64A8ACBA388E23F4C47F9B099F994C912A9329A5081E3851A1E81B61E998ECE37CE2D115934DA973E21C72F0B98B2C5641871002C2EBF5AFC68624772E4E2C9CF5D71CF5D705E6E2A9D7F7DC5E2803068081135FFBDC62B7C1883FFAAB9EAAE7FE3F0E1AA756B685C0F8C2590A38B91266A86BD898A8F1C40263FFFF057E7D094418181EC4B965152DD0E8F5D99DCE1FFFD843C4F1575F0C06048C44ADCB1B751CC127FA8F1B1C0300FF60BF9B337C0E28410384B6E975D0CCDFB5ECC2A591192E70000000016742C00ADC47B011000003000100000300020F1227800000000168CE0164B20000016588820FFF11C2988F15A499B125E9B94A6C8954994D28408006172011E555186A2B2011E521F364D9491195CBF11888C4461B474F2EFEA5526EF8F27C6303DA059E9D95CBD04A8DAFEF7E06FA50AC737F88E1F141F88E3584231E394BE109AA74A0600D0C7107D0AC76034E0FD215941CB37CE1B096EA64A8ACBA388E23F4DBFCD84CFCCA6489549FBE3828614C81D036C593698ECE37CE1EAED36A5CF953D8E54F0B9D5587F7861C2800D175FAD7E05210311DC7F072E4E7D273D75C199F8AA1B79FEE2F14060D0103457FEF718ADF1083FF5573D55CFF8FE1C6D3DAE2EBC0F8C259051C5C89335435EC4C5478E20131FFF0F0D786F277094418181EC4B965152DD0E8F5D99DCC2CE009556DA87F6FFC3018120CA0ADCB1B751CC127FA8F1B1C7E387B1BEB9B337C0E28410384B6E975D0CCDFB5ECC2A591192E70000000016742C00ADC47B011000003000100000300020F1227800000000168CE0164B20000016588843FFC470A629C569266C497A6E529B225526534A10200185C8047955461A8AC80479487CD93652446572FC462231118631D3CBBFA9549BD2E4F8C607B40B3D3B2B97C1D1B5FDEFC0EF8BD76B1CDFE2387C507E238D6108C78E52F8426A9D281803431C41F42B1D80D383F48565072CDF386C25BA992A2B2E8E2388FD36FF36133F329922552F8E480F1C29903A06D87A663B38DF3835D43FAD69B52E7C438E5E17301B3FDAF2E861C4002C2EBF5AFC686211DCB938B273D75C73D75C16A6FC457977BEF887FFE0AE2BE300031A2BFF7B8B537050C420FFEAAE7AAB9FF8FC38DA7B59B15781F184B214717224CD50D7B13151E38804C7FFFE1AF0DE4BBF309211C0C0F625CB04E65E2743E8F5D99DCFFFFEC21E6C55D7C3018343B82B72C6DD473049FEA3C6C700C03FD8DF5DADBC0E30820709ADD2EBA199BF6BD9854B22325CE");
        using var decoder = new EggEncoder.Codecs.H264.H264Decoder();
        var frames = 0;
        var width = 0;
        var starts = new List<int>();
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 0 && stream[i + 3] == 1 && (stream[i + 4] & 0x1F) is 7)
            {
                starts.Add(i);
            }
        }

        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : stream.Length;
            decoder.Decode(stream.AsSpan(starts[k], end - starts[k]), TimeSpan.Zero, frame =>
            {
                frames++;
                width = frame.Width;
                frame.Dispose();
            });
        }

        if (frames != 3 || width != 64 || decoder.Height != 48)
        {
            throw new InvalidOperationException("H.264 decode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 decode: I and P pictures from an Annex B stream", () =>
    {
        var stream = Convert.FromHexString("000000016742C00ADA11EC0440000003004000000403C489A80000000168CE04CB200000010605FFFF69DC45E9BDE6D948B7962CD820D923EEEF78323634202D20636F7265203136342072333037392064396131396630202D20482E3236342F4D5045472D342041564320636F646563202D20436F70796C65667420323030332D32303231202D20687474703A2F2F7777772E766964656F6C616E2E6F72672F783236342E68746D6C202D206F7074696F6E733A2063616261633D30207265663D31206465626C6F636B3D313A303A3020616E616C7973653D3078313A3078313131206D653D686578207375626D653D37207073793D31207073795F72643D312E30303A302E3030206D697865645F7265663D30206D655F72616E67653D3136206368726F6D615F6D653D31207472656C6C69733D31203878386463743D302063716D3D3020646561647A6F6E653D32312C313120666173745F70736B69703D31206368726F6D615F71705F6F66667365743D2D3220746872656164733D31206C6F6F6B61686561645F746872656164733D3120736C696365645F746872656164733D30206E723D3020646563696D6174653D3120696E7465726C616365643D3020626C757261795F636F6D7061743D3020636F6E73747261696E65645F696E7472613D3020626672616D65733D3020776569676874703D30206B6579696E743D38206B6579696E745F6D696E3D31207363656E656375743D343020696E7472615F726566726573683D302072635F6C6F6F6B61686561643D382072633D637266206D62747265653D31206372663D32322E302071636F6D703D302E36302071706D696E3D302071706D61783D3639207170737465703D342069705F726174696F3D312E34302061713D313A312E303000800000016588845F1088A8030210FC0428C840002D6A051A50004A17A71F94EA5F1EB15FFE0B59071004011979A90646712D73BB48E227862E21949C2D277FF8EFE76123E460B9CB62BFFDD81B741513077888FF7D6A1EC70653D63F47ABD6E26830E8624564F57A4000798C66C70055BFBFF68078A42A7280C8FFEF81000080200030145424000400C30028003763712250A278530B763716C801F6306247FE307889CFEE7000BA08454C2348008D3685093338F1BE1ADDAFA198B9AB27AAD80082495FFFB58407D1B28B04DC33FBA22A023FBB53E008A641902955ABCD486467373DEB98307C001E2F0CA8C68540BE47FFAD46906190D48BD1EAF0356B172158AC7ABD403E4211314074CFF7C7086656294883A807EB3C59A3BE5D68B79A0E863D9BFD38219D18A4641D540D3E2A583591FDF6148214200500A1971A1D8AC0A4C5F4D803A5E0713207B04991944014957F7D8351B3D01EB5BF7DBD04273E7189F1397F4707885CBEBB01BA158B7B81E63BFF5826C48D946B884FEFB130115026CAC3C1A1000102165F0C5B0E4216ED7ECAE7FFFF085E8AFE5114DE13B6BFFD03821829979924198851B5B3BF8A0ED434C70231EB0991EAF73B090B9183E7358CFFF5003E2315D4D0FDF7FBE5003C2B17D6D0ED77FBE5A8698E0463D613A3D5E0531EE6730AA7A8F57AC06E8C222204D57AFF7CE04E37A96F4A0EA0802000202C9000100A080042A6988C191E8070DA9825C435D5DB14110330000505E3865A636A7A04CA629A21009D16B7FF99C6B9028DA980ECA0E2F240ECA2D800048A3977436A781AE745B5CEB67FFB11B08A9BC10BFC04210C83915AA02C2E73A104A4E416D2383F9AA1B339B96AFCDC44F0CE84362A05499FFE01F531D221214F9F6C923E4617392843FF9C04E3F296C420EAD61B74B4ED606B33FFDDA8D20C3216913A3D5EB713418742922327ABD2003C84226280A97F7FED01E290A9CA02213FDF025001001420003C002860EDAF04D33AB0114262609978106D4F01A214A55607A9B23F7CC000103B043B2C6D4F841E329A30761A239261848006E8500146D4F027A884F1422EB3FFC60000989E589AC86D4E001F087D82C2F9E001E2000150001006880002000003007902081EE32000535C0E2081EE1500029AE088140880186B83A0500F0030D73E1FFE1A3F3AB3F3F81C0009141022000201260A4595C0C3D95CC804426E061C1109B98BC780019020900010034C0827004000103C10000D8001579684C41D098F2C205620E102B10C3FFD02BF9106641C40020B1000040A8000410E492B9E512B990001028EB9E1C002051D73861807C361A158037B08A4256AFE07087086410206603420002E0287080032F5733143FD121D79C68ED11112E168130004268D0376EA4086FC9311C368984526E9100020E36E160006C00040518E01807FC6FC04C108A14AAD5EB9EBF08000B82040C8400040007340A028B82937E14CC080020E325904312E604368909DDC44348249DCB20000980C1F73F0C03FD86B8301665F038438532084000408C80002060100010501D01C004186CB01A803B863EBBCCB409828A9601598B9F09C95EED6580100158E018078F60C3E02608451C8AD4FC20402B00E0800040441000A2605171C92D1098084459641192E602E4433BA88CE49285B080020E325900006800E075CE000000001419A217F0CC4E0B1933E10000801805096C20002410A700052213205529E2FCD9322ED72D952F4B5EB7A26EF9EF1AA54AE000AED9B09DC837B8A29E3F274BCD8FCBA1BC3742D3CD000000001419A409FCF0D3D0C901381E9E23F898D7DF3E9F22FE3B2D860068DFBBE65FF8659273974F798C800000001419A609FCF03A6C1AA0029A3F452295096A7DF7D770FAA45ECE75EBD4F78081FEFB97BB7F45757399590C800000001419A80DFC39E197819DC42366DEC252788FA636A637DE02CEB3B983200000001419AA047F0E611F27E225E9AEC2960675804ACED15783C7F9B37BB1DB814D93613868B604D9709464864111A5FC0489C3916E97C4400000001419AC057F3FBC215C3671ACBC3722F0FBDDC6137F4D7E46F7EB7CE5F184EEF6FADFFF864A356FBF17B1A92A52418701834A91DBFE78000000001419AE0775CF5C10F8FF63E085F3F99DF0D160F79D01700349D4CD9FDBF01FA53EFF0C94148FBE16482A4B6FDBF796447FC11EFFF5920FF9E");
        using var decoder = new EggEncoder.Codecs.H264.H264Decoder();
        var frames = 0;
        var starts = new List<int>();
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 && (stream[i + 3] & 0x1F) is 1 or 5)
            {
                starts.Add(i);
            }
        }

        var first = 0;
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : stream.Length;
            decoder.Decode(stream.AsSpan(first, end - first), TimeSpan.Zero, frame =>
            {
                frames++;
                frame.Dispose();
            });
            first = end;
        }

        decoder.Flush(frame =>
        {
            frames++;
            frame.Dispose();
        });
        if (frames != 8)
        {
            throw new InvalidOperationException("H.264 P decode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 decode: CABAC I and P pictures from an Annex B stream", () =>
    {
        var stream = Convert.FromHexString("00000001674D400ADA11EC0440000003004000000C83C489A80000000168EF0F2C800000010605FFFF6DDC45E9BDE6D948B7962CD820D923EEEF78323634202D20636F7265203136342072333037392064396131396630202D20482E3236342F4D5045472D342041564320636F646563202D20436F70796C65667420323030332D32303231202D20687474703A2F2F7777772E766964656F6C616E2E6F72672F783236342E68746D6C202D206F7074696F6E733A2063616261633D31207265663D31206465626C6F636B3D313A303A3020616E616C7973653D3078313A3078313331206D653D756D68207375626D653D37207073793D31207073795F72643D312E30303A302E3030206D697865645F7265663D30206D655F72616E67653D3136206368726F6D615F6D653D31207472656C6C69733D31203878386463743D302063716D3D3020646561647A6F6E653D32312C313120666173745F70736B69703D31206368726F6D615F71705F6F66667365743D2D3220746872656164733D31206C6F6F6B61686561645F746872656164733D3120736C696365645F746872656164733D30206E723D3020646563696D6174653D3120696E7465726C616365643D3020626C757261795F636F6D7061743D3020636F6E73747261696E65645F696E7472613D3020626672616D65733D3020776569676874703D32206B6579696E743D323530206B6579696E745F6D696E3D3235207363656E656375743D343020696E7472615F726566726573683D302072635F6C6F6F6B61686561643D34302072633D637266206D62747265653D31206372663D32332E302071636F6D703D302E36302071706D696E3D302071706D61783D3639207170737465703D342069705F726174696F3D312E34302061713D313A312E3030008000000165888427D636CDCFDD2464371CB917A82BCD8E35C5317D10B1F6522E1F3EC9516CD569B3A09836EB52136F0C8E24B8B78315A10CA90910FD36E649997B73FF5F1CA6719702568D819F5D2BB50F442B0D4E7230AD636B397BFEC7E5B445853A7AAFEA2FE62ED65BFCFD63BB8EA53D458E718B08B6D0D6502E951474D519794F6BCFEDBE71F9EB78D934B63F4BE7598DF12A38AE836589EED22DF79D4C3D646FED8C5CD676AE9B5677EDF60B6F2413F083DD922A5A01B4916935FC61B76DCC778CA358D8B502A96479C3BB3EE67498BACD6D7875B8BD1586CA39B9F19730AA134C86919E95329CAC5AB00F4C0B633F066B233C0CDA1B5CF0C5A6F417D63DAE0DF8CF8CB694E9C397A93C670D086DC2C2F8E83DFFDE5BA78AE8C205DE4B6DFD8DE95372175E6B49012634F754FC6DE34CB7B79969C8857A9F402D8F5EDF09F8F88EF6621E7197046CE778575C06B12C90DC0A05E541BF26A3C9A747B37FD9176FC1E65A27FB3D0FEA7EA9B2502A5F55A8EA671940ABE31F442C2E69E9996C56A69F4CD58C105E8C78B5C3298F011592F0D00B366B73FE9FFB29265019F75008670E7628ED3032437781BEBB0139521FED1764BC001120663EF07A9E381DCE7CFFDE9BCB00AEDD24D5F74667DD60AAB96E12E78D83ADF33932D6B25763E0A3F137A6727AE6F580EE14B2A4AA95C2AE607682F6B5323E5ACAC58336C34302C44438C9713727754DEDF010B7D796206A9A4448533C0B7B9487306D47B14E9896A79AC058D98D812892E9AA2C8B35B8B28870193A9161F37686CEED75F25C8F110A06B514331AD27EF58F242DEE93FAEEA455738D01AF9250179089A2D3B0D541988F3CCC81D37F55A47A652145D11895AEC22262F4E5932361B85A6779DCF1D1AE4F17E6787EC97698FA35A9E9A71528011BDB41D9B928E5B6FC5B455CA17C415A14BAE61E1C568666C6A440393C0EBA79A0CFE35C082D37AC522A20B32610168C5CF49106A8659D11FFEF4E6DE94FA87491B5011D267185D713A3D1D5259C361D2F1821E5282A4FCF6B00E0621A34A1B2B0E42A7E44EFDD90E831B3744EA8997A82B43D69DD60905B0DCCDD5AD5A84D8484FD164593AD2F54C2753329A79A2420524D25057BCFF8EE4DDBB6D01100000001419A2624FF02F600000001419A4626FF043D00000001419A66223F05F400000001419A8626FFAECBA6EF1B68544DB3E73DCD3F7CC7439E2F00000001419AA6223F45A70AC6E66EFF631AD1C59BBE440710D44C4F241E25DDB6A1948EAB68B37B8A6187C0B26EB598F4F4D4A8DC6BA3B357A7185867A6869D936FA70AFF04AEC449069D1DE5EC680C7FF2D60E4DCE8ED2332FA9BDC6607A44C7B1CED9C100000001419AC6223FC18A539DD1119C1F037B906EFCBB85DB3C5315DD885B2FB7DB45930053E982867D571F5A364798CF0B1C9AD44DDF7660CD9E4AB0931E0E33B7E54E00F0414F698B8399B098F64DAF23DEA3B27292DE7FDFB62D00000001419AE6233F6189F09A930C982861BCFA10DFC5796D482F1D69A17AF3E7B1905FC16FA009B4784505F4EFFCD5DC8D6213DD294C43797E5675C6E804A46912D9F463B2D4B866C22DDF4C71");
        using var decoder = new EggEncoder.Codecs.H264.H264Decoder();
        var frames = 0;
        var starts = new List<int>();
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 && (stream[i + 3] & 0x1F) is 1 or 5)
            {
                starts.Add(i);
            }
        }

        var first = 0;
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : stream.Length;
            decoder.Decode(stream.AsSpan(first, end - first), TimeSpan.Zero, frame =>
            {
                frames++;
                frame.Dispose();
            });
            first = end;
        }

        decoder.Flush(frame =>
        {
            frames++;
            frame.Dispose();
        });
        if (frames != 8)
        {
            throw new InvalidOperationException("H.264 CABAC decode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 decode: High profile B pictures from an Annex B stream", () =>
    {
        var stream = Convert.FromHexString("000000016764000AACD9447B0110000003001000000300A0F12259600000000168EBE3CB22C00000010605FFFFA9DC45E9BDE6D948B7962CD820D923EEEF78323634202D20636F7265203136342072333037392064396131396630202D20482E3236342F4D5045472D342041564320636F646563202D20436F70796C65667420323030332D32303231202D20687474703A2F2F7777772E766964656F6C616E2E6F72672F783236342E68746D6C202D206F7074696F6E733A2063616261633D31207265663D33206465626C6F636B3D313A303A3020616E616C7973653D3078333A3078313333206D653D686578207375626D653D37207073793D31207073795F72643D312E30303A302E3030206D697865645F7265663D31206D655F72616E67653D3136206368726F6D615F6D653D31207472656C6C69733D31203878386463743D312063716D3D3020646561647A6F6E653D32312C313120666173745F70736B69703D31206368726F6D615F71705F6F66667365743D2D3220746872656164733D31206C6F6F6B61686561645F746872656164733D3120736C696365645F746872656164733D30206E723D3020646563696D6174653D3120696E7465726C616365643D3020626C757261795F636F6D7061743D3020636F6E73747261696E65645F696E7472613D3020626672616D65733D3320625F707972616D69643D3220625F61646170743D3120625F626961733D30206469726563743D3120776569676874623D31206F70656E5F676F703D3020776569676874703D32206B6579696E743D323530206B6579696E745F6D696E3D35207363656E656375743D343020696E7472615F726566726573683D302072635F6C6F6F6B61686561643D34302072633D637266206D62747265653D31206372663D32332E302071636F6D703D302E36302071706D696E3D302071706D61783D3639207170737465703D342069705F726174696F3D312E34302061713D313A312E3030008000000165888400BF635371E7171E50BA16F2EBE53335353ED3F1E752008238578EF3415E6F59BE64AA2D80067ED731838B999759B2A05E4A607FE949BF58F3DE1023730C16EFEA0D1477932A770DCEE4053A8BFB2ECE6F86F8DDA9F61A8EDB9285C0CF4626FD0982AC53D5B42120873D4B03E6AA034D2BB0809D23CD40EFD104013E0CEA57C6C92BBB2FCBB7E66CD8CD19339B9B00ABE522AA2485B48AF7968BA63820AD448DA0443F3C25CA2EB742AAF0C756344C351B6359738A347F9762E316B1D7AADB6F53FAB33B39FBE5360CFC38A62F25E4B35F21FCE6E6D1AB9340AA13CFEBA65F0954FAE1A34A681B2CB1E30EC60286A545792BE6061530D7CB32ABBE202ED29E478DCA23BB4E999FBB2AED8E07F0B252F9A384C79823CDD09AF72C53BB88F9CE3C0C0B870FCB47FC34EA14560F32DF32D82346616802D46D93A31F59592E52F0F444EB7B039F69F38C9D96435445551AE4E930A16E3E5F8EF90069A7D5E6D5F771DBA254179DD79682CBC7204874FFF774A89596C53C50348EC372C37690F28F6DC5CCC624C370DED60069964DAA516EF84C464DD577907E13A4604B0F757A43704677EC0DE7BB095A67E5CDCF6B2574FB6E03360A3E36D65EB93E479AF578611E4BCF54ED0C48300E0CFD3AC95F278AC6D8F3EDAC739CF5795ECCB63D4CD0124FF624916DF37413ED0DF0D0F12BE6D9AE663F464B15DC28A8A1221DCE98595DB170C272E395B01436D509DE3AB2E4D70EE7796055C861E893448B9E138342A5B17E84CAE00B40992BF250BEC280DCFCE0B0E5D0BC51712D1B3C3C07E82884A1A4690400382F74C47F7B51EE4DE4D32A73B36DE99234E018BB334AB9B8A1FF11527C36DDFA02CDCE99421376CB7E120E2EB7B8FDCE968BFE7C93AD0419C517875E5C09469DC8DE514DCDB66AC1DD749A6685F464103EE68E15C1C5289F3D17910866D5D2DD7FCDAEEFA8D931235DFD211FA9679E18D358CF6115202596838B5459212DE162669074C510A248EBD9FC69F55EC76304892F71779C3C9480AA5D58F83D20C13B339679414C04087435B34127535CDDE118BDC1DFA4FE4FCB7C77D003F924F9BE3BBAB35DFF107EE7F37A14F7F78C4592156CAB11AEA78E881362BA82D64FCFFE2C9343D081A93A99432D29C7509D0267A594A41EC4B1F59884A2EC6481C70689250AF5CD15B409BA9E0E6C31B9830F7B0BBD1B49333043567572F967D0A8AE0B87AC9542E96091FFCA9F070D930FB7F258CA56283A8BD7E3121E2F256395856290E838BDF7703326108D7EB996F7DA2C942DB8F8F7E8E92F899A966C397376657A9777A8145B5EF06CD33FD68D76F4D425B5C5D0536FDFE5B409D797E4A8A1FC730DC9C4582599BAB94F9FB270E5A26A6352BEDB996D4D20848C9B485E4E2BEC71C3DA2ACC5FBFA2F82BB32D70C770DD5120EF5ECD66967F9F1F4FE25EDF676940E1F83563A78C9216C72CD19B4E7F6FFEC6C5F63896F9954C99D6F014F811BCF2DE316269C74354DC417C054A8227EF1EC864DD6B39871C662C9C43506610D94089BD70212C812A10265B34989E74BFBDCEBEB20400D5D78426861150403206E100000001419A246C5FD607848A68AF160601E0B47DB34C377E067859F2358251544E68CEE82457FFD95B666FBCB1C1EE2D7BE92429CCDF1C2871A54994AB86FF415A447E6C3DC762C564A2A25E80C754833B8DBF193DE4672511F013D47A9538259CD66E51EF00B15C2539FFF2BCC4180F83F07BBAE1F3A9330C0091D24DF7BD33683AE07E7B9B1941FDBE489E740A837BF9D535B53000000001419E42789BFFF80127DCEC395786104CCF29A45BD2C79A7151D71100000001019E6174457FF9E5DD68358776A79AB6FEC879CD397189198BA3D2A5A43E786700000001019E636A457FEA19D184019326F7BE27D1945231CD29E8C100000001419A6549A84168994C0FFFE9F21835A73D0788D970291BA6F8680BE17F55AAC793BA0C0581033768AF326A73B941819B3E29E5EEE770798FFFA34302C509E42549FCD5E2EA91A6AE6A2ECBF2B0CC4D39C9897AB0A062BB49E0E64AE339582F8C068EAA90C78BFFA59BEE69CD45E6311A3930B5E5FA5CAB1F525F32BF3642EA7EDA04FA7B37BB3FE57F025E43ECB927486DB4D2C170253D574F8BC90DB866B02C64EE0C4859D4EE827D7EF139268E5B8D7A814100000001419A8649E10A5265303FF6F20DCA7C3154AD1182C552037CBD050ACFB565AA73DDE9B2A6F6A8B1B228255C2FBB29E00DF0C7AEC252B3FB39BFEBE0264B8900C86B62F0748C8FB3AC8B01BC04CB02965500000001419AA849E10E8994C14D135F16E543B1D231B6D900000001019EC76A457F9AC6859721F7F5AECAA4FC60C0BAB0CF85E0E77FB3CA3F882AB5A11D753469E2328CCA2B8ED0A6500CA1B5EE3A0008C000000001419AC949E10F2653024F1649A078E0EBA6699E7FFFCBAD3D08B70A2F8C93196A28F800000001419AEB49E10F265305113C57469208EE0FCD0FE9127EFCB8EBBC06B02AD982952F53CDC094C6CEA298FF69E7FF4B2AA0946C77F07232B45D663EEF023AC9451A6E93D1EA43599F9177ACB6E019751BF9A72C2715000667CF2D374E84917209E05490E6EF12A0A04BB96FE69BECB9E8EF64425CD157912B208E5EDF766D9D84FF4E6344330C9BBE24FFCB0664E34E5D9F63BF59931860648BF74578571B34D879B700000001019F0A6A457FACCFBF22E8EE594E1219C63BE2CC7CA1835E9D9315A75B9EFE3C53F8A02C");
        using var decoder = new EggEncoder.Codecs.H264.H264Decoder();
        var frames = 0;
        var starts = new List<int>();
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 && (stream[i + 3] & 0x1F) is 1 or 5)
            {
                starts.Add(i);
            }
        }

        var first = 0;
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : stream.Length;
            decoder.Decode(stream.AsSpan(first, end - first), TimeSpan.Zero, frame =>
            {
                frames++;
                frame.Dispose();
            });
            first = end;
        }

        decoder.Flush(frame =>
        {
            frames++;
            frame.Dispose();
        });
        if (frames != 12)
        {
            throw new InvalidOperationException("H.264 High profile decode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 encode: frames round trip through the decoder", () =>
    {
        var pool = new EggEncoder.Video.VideoFramePool();
        using var encoder = new EggEncoder.Codecs.H264.H264Encoder(new EggEncoder.Codecs.H264.H264EncoderSettings
        {
            Width = 48,
            Height = 32,
            FrameRate = new EggEncoder.Video.VideoFrameRate(25)
        });
        var configuration = encoder.DecoderConfiguration;
        var header = new List<byte>();
        foreach (var nal in configuration.SequenceParameterSets.Concat(configuration.PictureParameterSets))
        {
            header.AddRange(new byte[] { 0, 0, 0, 1 });
            header.AddRange(nal.ToArray());
        }

        using var decoder = new EggEncoder.Codecs.H264.H264Decoder();
        var decoded = 0;
        for (var index = 0; index < 4; index++)
        {
            using var frame = pool.Rent(48, 32);
            for (var y = 0; y < 32; y++)
            {
                var row = frame.GetRow(EggEncoder.Video.VideoPlane.Y, y);
                for (var x = 0; x < 48; x++)
                {
                    row[x] = (byte)((x * 4) + (y * 2) + (index * 3));
                }
            }

            frame.GetRow(EggEncoder.Video.VideoPlane.U, 0).Fill(128);
            encoder.Encode(frame, encoded =>
            {
                var annexB = EggEncoder.Media.AnnexBConverter.ToAnnexB(encoded.Payload.Span);
                decoder.Decode(index == 0 ? [.. header, .. annexB] : annexB, TimeSpan.Zero, output =>
                {
                    decoded++;
                    output.Dispose();
                });
            });
        }

        decoder.Flush(output =>
        {
            decoded++;
            output.Dispose();
        });
        if (decoded != 4)
        {
            throw new InvalidOperationException("H.264 encode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("H.264 encode: High profile CABAC with 8x8 transform on two wavefront threads", () =>
    {
        using var encoder = new EggEncoder.Codecs.H264.H264Encoder(new EggEncoder.Codecs.H264.H264EncoderSettings
        {
            Width = 64,
            Height = 64,
            FrameRate = new EggEncoder.Video.VideoFrameRate(25),
            Profile = EggEncoder.Codecs.H264.H264Profile.High,
            MaxDegreeOfParallelism = 2
        });
        var avcC = encoder.DecoderConfiguration.ToArray();
        using var decoder = new EggEncoder.Codecs.H264.H264Decoder(avcC);
        var decoded = 0;
        for (var index = 0; index < 3; index++)
        {
            using var frame = EggEncoder.Video.VideoFrame.Create(64, 64);
            for (var y = 0; y < 64; y++)
            {
                var row = frame.GetRow(EggEncoder.Video.VideoPlane.Y, y);
                for (var x = 0; x < 64; x++)
                {
                    row[x] = (byte)((x * 3) + (y * 2) + (index * 5));
                }
            }

            frame.Fill(new EggEncoder.Video.YuvColor(0, 120, 140));
            encoder.Encode(frame, encoded => decoder.Decode(encoded.Payload.Span, TimeSpan.Zero, output =>
            {
                decoded++;
                output.Dispose();
            }));
        }

        decoder.Flush(output =>
        {
            decoded++;
            output.Dispose();
        });
        if (decoded != 3 || avcC[1] != 100)
        {
            throw new InvalidOperationException("H.264 High profile encode check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("JPEG: encode a frame and an MP4 frame grab", () =>
    {
        using var frame = EggEncoder.Video.VideoFrame.Create(33, 17);
        frame.Fill(new EggEncoder.Video.RgbaColor(200, 40, 90).ToYuv(EggEncoder.Video.VideoColorMatrix.Bt709, EggEncoder.Video.VideoColorRange.Limited));
        var jpeg = EggEncoder.Codecs.Jpeg.JpegEncoder.Encode(frame);
        var mp4 = new MemoryStream();
        using (var h264 = new EggEncoder.Codecs.H264.H264Encoder(new EggEncoder.Codecs.H264.H264EncoderSettings { Width = 32, Height = 32, FrameRate = new EggEncoder.Video.VideoFrameRate(25) }))
        using (var mp4Writer = new EggEncoder.Containers.Mp4.Mp4Writer(mp4, leaveOpen: true, new MemoryStream()))
        {
            mp4Writer.Open(new EggEncoder.Media.MediaTrackSet([new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, h264.DecoderConfiguration.ToArray(), 0, width: 32, height: 32)]));
            using var picture = EggEncoder.Video.VideoFrame.Create(32, 32);
            for (var index = 0; index < 2; index++)
            {
                picture.Fill(new EggEncoder.Video.YuvColor((byte)(60 + (index * 40)), 128, 128));
                picture.Timestamp = TimeSpan.FromMilliseconds(40 * index);
                h264.Encode(picture, encoded => mp4Writer.Write(new EggEncoder.Media.MediaPacket(0, 40 * index, 40 * index, encoded.IsKeyframe, 0, encoded.Payload)));
            }

            mp4Writer.Complete();
        }

        mp4.Position = 0;
        using var reader = new EggEncoder.Containers.Mp4.Mp4VideoReader(mp4);
        var grabbed = reader.GrabJpeg(TimeSpan.FromMilliseconds(40));
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[^2] != 0xFF || jpeg[^1] != 0xD9 || grabbed.Length < 100 || reader.DisplaySize != (32, 32))
        {
            throw new InvalidOperationException("JPEG check produced unexpected results");
        }

        return Task.CompletedTask;
    });

    await RunCheck("Composition: a clip without an extension, an end card, music, rendered to MP4", () =>
    {
        var clipPath = Path.Combine(workingDirectory, "clip");
        using (var clipFile = File.Create(clipPath))
        using (var h264 = new EggEncoder.Codecs.H264.H264Encoder(new EggEncoder.Codecs.H264.H264EncoderSettings { Width = 32, Height = 32, FrameRate = new EggEncoder.Video.VideoFrameRate(25) }))
        using (var mp4Writer = new EggEncoder.Containers.Mp4.Mp4Writer(clipFile, leaveOpen: true, new MemoryStream()))
        {
            mp4Writer.Open(new EggEncoder.Media.MediaTrackSet([new EggEncoder.Media.MediaTrackInfo(0, EggEncoder.Media.MediaKind.Video, EggEncoder.Media.MediaCodec.H264, 1000, h264.DecoderConfiguration.ToArray(), 0, width: 32, height: 32)]));
            using var picture = EggEncoder.Video.VideoFrame.Create(32, 32);
            for (var index = 0; index < 5; index++)
            {
                picture.Fill(new EggEncoder.Video.YuvColor((byte)(60 + (index * 20)), 100, 160));
                h264.Encode(picture, encoded => mp4Writer.Write(new EggEncoder.Media.MediaPacket(0, 40 * index, 40 * index, encoded.IsKeyframe, 0, encoded.Payload)));
            }

            mp4Writer.Complete();
        }

        var musicPath = Path.Combine(workingDirectory, "music.wav");
        using (var wav = EggEncoder.Codecs.Wav.WavWriter.Create(musicPath, 2, 48000, 16, 4800))
        {
            var tone = new int[9600];
            for (var i = 0; i < 4800; i++)
            {
                tone[2 * i] = tone[(2 * i) + 1] = (int)(8000 * Math.Sin(i * 0.05));
            }

            wav.WriteInterleavedSamples(tone, 4800);
            wav.Finish();
        }

        var font = EggEncoder.Video.Text.TrueTypeFont.Load(Convert.FromHexString(
            "00010000000B0080000300304F532F3245D846570000013800000060636D6170001EEDFF000001AC00000064676C7966527075FA0000021C000000846865616430A38B32000000BC000000366868656107330290000000F400000024686D74780CB2003200000198000000146B65726E0039FFBC000002A00000001E6C6F6361004B0074000002100000000C6D617870000C001400000118000000206E616D65CEF41F53000002C000000087706F73746950293A000003480000004500010000000100000EA7219F5F0F3CF5000103E800000000E71EA3CB00000000E71EA3CBFFCE0000038402BC000000030002000000000000000100000320FF38005A03840000FFCE03B600010000000000000000000000000000000500010000000500040001000C00030002000000000000000000000000000200020003028A0190000500040000000000000000000000000000000000000000000000000000000000000000000000010200000000000000000000003F3F3F3F00000061FFFF02EEFF0600640384012C00000000000000000000000000000020000001F40032025800000226000002BC00000384000000000003000000030000001C000300010000001C0003000A0000003C00040020000000040004000100000064FFFF00000061FFFFFFA0000100000000000C00000000002800000000000000020000006100000064000000010001F6000001F600000000010000000D001900250032004200010032000001C2019000030000331121113201900190FE700000010000000001F401F4000300003111211101F401F4FE0C00010000000001F401F4000300003110201101F401F4FE0CFFFF00000000025802BC00270001006400C8000E000200002000FFFFFFCE0000038402BC00260003CE00008700010384000000004000C0000000000000010000001A00010002000C0001000000010002FF9C000200010028000000000004003600010000000000010014000000010000000000020007001400030001040900010028001B0003000104090002000E0043456767456E636F6465722053796E746865746963526567756C61720045006700670045006E0063006F006400650072002000530079006E0074006800650074006900630052006500670075006C006100720000020000000000000000000000000000000000000000000000000000000000000005000001020103010401050673717561726505637572766504636F6D70066E6573746564000000"));
        var composition = new EggEncoder.Video.Composition.VideoComposition
        {
            Width = 48,
            Height = 32,
            FrameRate = new EggEncoder.Video.VideoFrameRate(25),
            Scenes = [new EggEncoder.Video.Composition.CompositionScene { Source = clipPath }],
            Music = new EggEncoder.Video.Composition.CompositionMusic { Source = musicPath },
            EndCard = new EggEncoder.Video.Composition.CompositionEndCard { Text = "ab", Duration = TimeSpan.FromSeconds(0.2) },
            Font = font,
            MaxDegreeOfParallelism = 2
        };
        using var output = new MemoryStream();
        var result = composition.Render(output);
        if (result.FrameCount != 10 || !result.HasAudio || result.Thumbnail[0] != 0xFF || output.Length < 200)
        {
            throw new InvalidOperationException("Composition check produced unexpected results");
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
