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
