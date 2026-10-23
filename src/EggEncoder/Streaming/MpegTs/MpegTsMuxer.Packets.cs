using EggEncoder.Codecs.Aac;
using EggEncoder.Media;

namespace EggEncoder.Streaming.MpegTs
{
    // Packet mode: H.264, H.265 and AAC tracks of a MediaTrackSet, fed MediaPackets
    // (docs/live-ingest-restream-plan.md item 5b). Video PES carry Annex B access units with an
    // access unit delimiter first and the track's parameter sets before every keyframe; a PCR rides
    // in an adaptation field on the PCR PID; PES packets are stuffed with adaptation fields, as
    // ISO/IEC 13818-1 2.4.3.3 requires.
    public sealed partial class MpegTsMuxer
    {
        private const int TsPacketSize = 188;
        private const int TsHeaderSize = 4;
        private const int FirstElementaryPid = 0x0100;
        private const byte H264StreamType = 0x1B;
        private const byte H265StreamType = 0x24;
        private const byte FirstVideoStreamId = 0xE0;
        private const int MaxVideoTracks = 16; // stream_id 0xE0-0xEF
        private const int MaxAudioTracks = 32; // stream_id 0xC0-0xDF
        private const int PtsAndDtsPrefix = 0x3;
        private const int DtsPrefix = 0x1;

        // PCR runs this far behind the DTS it is computed from (clamped at 0), ffmpeg's default mux
        // delay: packets are written in DTS order, so every packet arrives no later than its DTS.
        private const long PcrDelayTicks = PtsClockHz * 7 / 10;

        // ISO/IEC 13818-1 2.7.2: at most 100 ms between PCRs. A segment's first packet always gets one.
        private const long MaxPcrIntervalTicks = PtsClockHz / 10;

        // A gap between PCRs is bridged with PCR-only packets only up to this many (5 s): a longer
        // one is a timestamp jump, not a sparse track, and bridging it would let one packet's DTS
        // decide how many bytes the segment takes.
        private const int MaxPcrBridgePackets = 50;

        private const byte RandomAccessIndicator = 0x40;
        private const byte PcrFlag = 0x10;
        private const int PcrFieldLength = 6;

        private const string TracksParameter = "tracks";

        private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

        // Access unit delimiters, primary_pic_type / pic_type "any slice type": H.264 NAL type 9 with
        // primary_pic_type 7; H.265 NAL type 35 (layer 0, temporal id 1) with pic_type 2. Each ends
        // in its RBSP stop bit.
        private static ReadOnlySpan<byte> H264AccessUnitDelimiter => [0x09, 0xF0];

        private static ReadOnlySpan<byte> H265AccessUnitDelimiter => [0x46, 0x01, 0x50];

        private static readonly byte[] PatPayload = BuildPatPayload();

        private readonly PacketTrack[]? _packetTracks;
        private readonly Dictionary<int, PacketTrack>? _packetTracksById;
        private readonly PacketTrack? _pcrTrack;
        private readonly byte[]? _packetModePmt;
        private int _patContinuityCounter;
        private int _pmtContinuityCounter;
        private long? _lastPcr;

        /// <summary>
        /// Packet mode: the tracks of <paramref name="tracks"/>, fed by
        /// <see cref="BuildSegment(IReadOnlyList{MediaPacket})"/>. Elementary PIDs are 0x100 plus
        /// the track's position in the set; the PCR rides on the first video track, or on the first
        /// track when there is no video. H.264 is written with <c>stream_type</c> 0x1B, H.265 with
        /// 0x24 and AAC as ADTS with 0x0F.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> is empty or has more than 16 video or 32 audio tracks, or a track's codec configuration is missing or invalid.</exception>
        /// <exception cref="NotSupportedException">
        /// A track's codec is not H.264, H.265 or AAC; or an AAC track can't be carried as ADTS: its
        /// object type isn't 1-4 (HE-AAC with explicit SBR/PS signalling is 5/29), its sample rate
        /// is explicit rather than indexed, or its channel configuration isn't 1-7.
        /// </exception>
        public MpegTsMuxer(MediaTrackSet tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("A packet-mode muxer needs at least one track.", nameof(tracks));
            }

            _packetTracks = new PacketTrack[tracks.Tracks.Count];
            _packetTracksById = [];
            var videoCount = 0;
            var audioCount = 0;
            for (var i = 0; i < _packetTracks.Length; i++)
            {
                var info = tracks.Tracks[i];
                var pid = FirstElementaryPid + i;
                var track = info.Codec switch
                {
                    MediaCodec.H264 or MediaCodec.H265 => CreateVideoTrack(info, pid, (byte)(FirstVideoStreamId + videoCount++)),
                    MediaCodec.Aac => CreateAudioTrack(info, pid, (byte)(AudioStreamId + audioCount++)),
                    _ => throw new NotSupportedException($"{info.Codec} tracks are not supported in MPEG-TS; only H.264, H.265 and AAC are.")
                };

                if (videoCount > MaxVideoTracks || audioCount > MaxAudioTracks)
                {
                    throw new ArgumentException($"MPEG-TS stream ids allow at most {MaxVideoTracks} video and {MaxAudioTracks} audio tracks.", nameof(tracks));
                }

                _packetTracks[i] = track;
                _packetTracksById.Add(info.TrackId, track);
            }

            _pcrTrack = Array.Find(_packetTracks, t => t.IsVideo) ?? _packetTracks[0];
            _packetModePmt = BuildPmtPayload(_pcrTrack.Pid, _packetTracks.Select(t => (t.StreamType, t.Pid)).ToArray());
        }

        /// <summary>
        /// Builds one segment holding <paramref name="packets"/>: PAT, PMT, then one PES per packet,
        /// written in DTS order (a stable sort on the 90 kHz DTS, so packets with the same DTS keep
        /// the order given). Timestamps are rescaled to 90 kHz and written as given, never rebased,
        /// modulo 2^33. Video PES carry PTS and DTS (DTS only when it differs) and an Annex B access
        /// unit: an access unit delimiter first (the packet's own, when it starts with one), then,
        /// for a keyframe that carries no SPS of its own, the parameter sets from the track's
        /// configuration record (all of them, even if the packet carries a PPS), then the packet's NAL units. AAC PES carry one ADTS frame. The PES of
        /// every keyframe packet (every AAC packet is one) sets <c>random_access_indicator</c>. A PCR of
        /// max(0, DTS - 700 ms) goes on every PES of the PCR PID; a PCR-only packet on that PID goes
        /// before another PID's PES that opens the segment, and PCR-only packets 100 ms apart bridge
        /// any gap of more than 100 ms (up to 5 s; a longer one is left as a timestamp jump). PCR
        /// never passes a later DTS as long as no track's DTS falls more than 700 ms behind the
        /// latest DTS already written, in this segment or the previous one.
        /// Continuity counters continue from the previous segment. Validation and payload
        /// conversion happen before anything is written: a call that throws leaves the muxer
        /// unchanged.
        /// </summary>
        /// <param name="packets">At least one packet, of tracks in this muxer's set, in decode order within each track.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="packets"/> is empty; or a packet names a track not in the set, or has a
        /// <see cref="MediaPacket.ConfigVersion"/> other than its track's, a negative DTS, a PTS
        /// before its DTS, a DTS not strictly greater than its track's previous one (in this
        /// segment or an earlier one, compared both as given and in 90 kHz ticks), a timestamp past what a 90 kHz clock holds in 64 bits, a
        /// malformed length-prefixed video payload, or an AAC payload too long for one ADTS frame
        /// (8,184 bytes).
        /// </exception>
        /// <exception cref="InvalidOperationException">This muxer was built for frames (the <c>(channels, sampleRate)</c> constructor).</exception>
        public byte[] BuildSegment(IReadOnlyList<MediaPacket> packets)
        {
            ArgumentNullException.ThrowIfNull(packets);
            if (_packetTracks is null || _packetModePmt is null || _pcrTrack is null)
            {
                throw new InvalidOperationException("This muxer was built for Mp4EncodedFrames; construct it with a MediaTrackSet to pass MediaPackets.");
            }

            if (packets.Count == 0)
            {
                throw new ArgumentException("A segment must contain at least one packet.", nameof(packets));
            }

            var units = PreparePesUnits(packets);

            // Work out the PCR plan first, so the output can be sized exactly. A PES's PCR is
            // max(0, DTS - 700 ms), never below the last one. When that is more than 100 ms past
            // the last PCR, PCR-only packets 100 ms apart bridge the gap first; a PES on another
            // PID that opens the segment, or that follows a jump too long to bridge, also gets one
            // at its own PCR.
            var lastPcr = _lastPcr;
            var totalPacketCount = PsiPacketCount(PatPayload.Length) + PsiPacketCount(_packetModePmt.Length);
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                var pcr = Math.Max(lastPcr ?? 0, Math.Max(0, unit.Dts90k - PcrDelayTicks));
                var jumped = false;
                if (lastPcr is { } last && pcr - last > MaxPcrIntervalTicks)
                {
                    var bridgeCount = (pcr - last - 1) / MaxPcrIntervalTicks;
                    jumped = bridgeCount > MaxPcrBridgePackets;
                    for (var k = 1; !jumped && k <= bridgeCount; k++)
                    {
                        unit.PcrOnlyPackets.Add(last + (k * MaxPcrIntervalTicks));
                    }
                }

                if (unit.Track == _pcrTrack)
                {
                    unit.Pcr = pcr;
                }
                else if (i == 0 || lastPcr is null || jumped)
                {
                    // Also after a jump: the clock moves to the new timeline right away, rather
                    // than every later PES seeing the same unbridgeable gap until the PCR PID's next.
                    unit.PcrOnlyPackets.Add(pcr);
                }

                lastPcr = unit.Pcr ?? (unit.PcrOnlyPackets.Count > 0 ? unit.PcrOnlyPackets[^1] : lastPcr);

                totalPacketCount += unit.PcrOnlyPackets.Count + PesPacketCount(unit.Pes.Length, AdaptationFieldLength(unit));
            }

            var output = new byte[totalPacketCount * TsPacketSize];
            var position = 0;
            var patContinuityCounter = _patContinuityCounter;
            var pmtContinuityCounter = _pmtContinuityCounter;
            var continuityCounters = new Dictionary<PacketTrack, int>();
            foreach (var track in _packetTracks)
            {
                continuityCounters[track] = track.ContinuityCounter;
            }

            position = WritePsi(output, position, PatPid, PatPayload, ref patContinuityCounter);
            position = WritePsi(output, position, PmtPid, _packetModePmt, ref pmtContinuityCounter);
            foreach (var unit in units)
            {
                foreach (var pcr in unit.PcrOnlyPackets)
                {
                    // A PCR-only packet carries no payload, so it doesn't advance the PCR PID's
                    // continuity counter (2.4.3.3): it repeats the counter of the PID's last packet.
                    position = WritePcrOnlyPacket(output, position, _pcrTrack.Pid, pcr, (continuityCounters[_pcrTrack] + 15) & 0x0F);
                }

                var continuityCounter = continuityCounters[unit.Track];
                position = WritePes(output, position, unit, ref continuityCounter);
                continuityCounters[unit.Track] = continuityCounter;
            }

            // Commit: nothing above can throw.
            _patContinuityCounter = patContinuityCounter;
            _pmtContinuityCounter = pmtContinuityCounter;
            _lastPcr = lastPcr;
            foreach (var track in _packetTracks)
            {
                track.ContinuityCounter = continuityCounters[track];
            }

            foreach (var unit in units)
            {
                unit.Track.LastDts = unit.SourceDts;
                unit.Track.LastDts90k = unit.Dts90k;
            }

            return output;
        }

        private List<PesUnit> PreparePesUnits(IReadOnlyList<MediaPacket> packets)
        {
            var lastDts = new Dictionary<PacketTrack, (long Source, long Dts90k)>();
            var units = new List<PesUnit>(packets.Count);
            foreach (var packet in packets)
            {
                if (!_packetTracksById!.TryGetValue(packet.TrackId, out var track))
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this muxer's track set.", nameof(packets));
                }

                if (packet.ConfigVersion != track.Info.ConfigVersion)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has config version {packet.ConfigVersion}; the track's is {track.Info.ConfigVersion}.", nameof(packets));
                }

                if (packet.Dts < 0)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a negative DTS ({packet.Dts}).", nameof(packets));
                }

                if (packet.Pts < packet.Dts)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a PTS ({packet.Pts}) before its DTS ({packet.Dts}).", nameof(packets));
                }

                var (previousDts, previousDts90k) = lastDts.TryGetValue(track, out var previous) ? previous : (track.LastDts, track.LastDts90k);
                if (previousDts is not null && packet.Dts <= previousDts)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, not greater than the track's previous {previousDts}.", nameof(packets));
                }

                long dts90k, pts90k;
                try
                {
                    dts90k = MediaTimestamp.Rescale(packet.Dts, track.Info.Timescale, (int)PtsClockHz);
                    pts90k = MediaTimestamp.Rescale(packet.Pts, track.Info.Timescale, (int)PtsClockHz);
                }
                catch (OverflowException e)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has a timestamp too large for a 90 kHz clock.", nameof(packets), e);
                }

                // A timescale finer than 90 kHz can round two increasing DTS to the same tick.
                if (previousDts90k is not null && dts90k <= previousDts90k)
                {
                    throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, which rounds to the same 90 kHz DTS as the track's previous packet.", nameof(packets));
                }

                lastDts[track] = (packet.Dts, dts90k);

                var pes = track.IsVideo
                    ? BuildVideoPes(track, packet, pts90k, dts90k)
                    : BuildAudioPes(track, packet, pts90k);
                units.Add(new PesUnit(track, packet.Dts, dts90k, packet.IsKeyframe, pes));
            }

            // List<T>.Sort isn't stable; OrderBy is.
            return units.OrderBy(u => u.Dts90k).ToList();
        }

        private static byte[] BuildVideoPes(PacketTrack track, MediaPacket packet, long pts90k, long dts90k)
        {
            var payload = packet.Payload.Span;
            List<(int Offset, int Length)> nalUnits;
            try
            {
                nalUnits = AnnexBConverter.SplitLengthPrefixed(payload, track.NalLengthSize);
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has a malformed length-prefixed payload: {e.Message}", "packets", e);
            }

            var startsWithDelimiter = NalUnitType(track, payload[nalUnits[0].Offset]) == track.AccessUnitDelimiterType;
            var insertParameterSets = packet.IsKeyframe;
            foreach (var (offset, _) in nalUnits)
            {
                if (NalUnitType(track, payload[offset]) == track.SpsType)
                {
                    insertParameterSets = false;
                }
            }

            var delimiter = track.Info.Codec == MediaCodec.H264 ? H264AccessUnitDelimiter : H265AccessUnitDelimiter;

            var hasDts = pts90k != dts90k;
            var headerDataLength = hasDts ? 10 : 5;
            var esLength = 0L;
            if (!startsWithDelimiter)
            {
                esLength += StartCode.Length + delimiter.Length;
            }

            if (insertParameterSets)
            {
                esLength += track.ParameterSets.Length;
            }

            foreach (var (_, length) in nalUnits)
            {
                esLength += StartCode.Length + length;
            }

            var pes = new byte[9 + headerDataLength + esLength];
            WritePesHeader(pes, track.StreamId, pesPacketLength: 0, dataAlignment: true, pts90k, hasDts ? dts90k : null);

            var position = 9 + headerDataLength;
            var nextNal = 0;
            if (startsWithDelimiter)
            {
                position = WriteNal(pes, position, payload.Slice(nalUnits[0].Offset, nalUnits[0].Length));
                nextNal = 1;
            }
            else
            {
                position = WriteNal(pes, position, delimiter);
            }

            if (insertParameterSets)
            {
                track.ParameterSets.CopyTo(pes, position);
                position += track.ParameterSets.Length;
            }

            for (var i = nextNal; i < nalUnits.Count; i++)
            {
                position = WriteNal(pes, position, payload.Slice(nalUnits[i].Offset, nalUnits[i].Length));
            }

            return pes;
        }

        private static byte[] BuildAudioPes(PacketTrack track, MediaPacket packet, long pts90k)
        {
            var payload = packet.Payload.Span;
            if (payload.Length > AdtsFrameWriter.MaxPayloadLength)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has {payload.Length} bytes; one ADTS frame holds at most {AdtsFrameWriter.MaxPayloadLength}.", "packets");
            }

            const int headerDataLength = 5;
            var esLength = AdtsFrameWriter.HeaderLength + payload.Length;
            var pes = new byte[9 + headerDataLength + esLength];
            WritePesHeader(pes, track.StreamId, pesPacketLength: 3 + headerDataLength + esLength, dataAlignment: false, pts90k, null);
            AdtsFrameWriter.WriteHeader(pes.AsSpan(9 + headerDataLength), track.AdtsProfile, track.AdtsSampleRateIndex, track.AdtsChannelConfiguration, payload.Length);
            payload.CopyTo(pes.AsSpan(9 + headerDataLength + AdtsFrameWriter.HeaderLength));

            return pes;
        }

        // PES header (2.4.3.6) with the '10' marker, data_alignment_indicator as given, and PTS or
        // PTS + DTS. A video PES_packet_length of 0 means "unbounded", which 2.4.3.7 allows only
        // for video elementary streams in a transport stream.
        private static void WritePesHeader(byte[] pes, byte streamId, int pesPacketLength, bool dataAlignment, long pts90k, long? dts90k)
        {
            pes[0] = 0x00;
            pes[1] = 0x00;
            pes[2] = 0x01;
            pes[3] = streamId;
            pes[4] = (byte)(pesPacketLength >> 8);
            pes[5] = (byte)pesPacketLength;
            pes[6] = (byte)(dataAlignment ? 0x84 : 0x80);
            if (dts90k is { } dts)
            {
                pes[7] = 0xC0; // PTS_DTS_flags='11'
                pes[8] = 10;
                EncodeTimestamp(pts90k & PtsWrapMask, PtsAndDtsPrefix, pes.AsSpan(9, 5));
                EncodeTimestamp(dts & PtsWrapMask, DtsPrefix, pes.AsSpan(14, 5));
            }
            else
            {
                pes[7] = 0x80; // PTS_DTS_flags='10'
                pes[8] = 5;
                EncodeTimestamp(pts90k & PtsWrapMask, PtsOnlyPrefix, pes.AsSpan(9, 5));
            }
        }

        private static int WriteNal(byte[] destination, int position, ReadOnlySpan<byte> nal)
        {
            StartCode.CopyTo(destination.AsSpan(position));
            nal.CopyTo(destination.AsSpan(position + StartCode.Length));
            return position + StartCode.Length + nal.Length;
        }

        private static int NalUnitType(PacketTrack track, byte header) =>
            track.Info.Codec == MediaCodec.H264 ? header & 0x1F : (header >> 1) & 0x3F;

        // The adaptation field (length byte included) the first TS packet of a PES needs before any
        // stuffing: a flags byte for random_access_indicator and/or a PCR.
        private static int AdaptationFieldLength(PesUnit unit) =>
            unit.Pcr is not null ? 2 + PcrFieldLength
            : unit.IsKeyframe ? 2
            : 0;

        private static int PesPacketCount(int pesLength, int firstAdaptationFieldLength)
        {
            var firstCapacity = TsPacketSize - TsHeaderSize - firstAdaptationFieldLength;
            const int capacity = TsPacketSize - TsHeaderSize;
            return pesLength <= firstCapacity ? 1 : 1 + ((pesLength - firstCapacity + capacity - 1) / capacity);
        }

        private static int PsiPacketCount(int payloadLength) =>
            (payloadLength + TsPacketSize - TsHeaderSize - 1) / (TsPacketSize - TsHeaderSize);

        // A PSI section split across TS packets, the rest of the last one filled with 0xFF, which
        // 2.4.4 allows after a section (unlike after a PES packet).
        private static int WritePsi(byte[] output, int position, int pid, byte[] payload, ref int continuityCounter)
        {
            const int capacity = TsPacketSize - TsHeaderSize;
            for (var offset = 0; offset < payload.Length; offset += capacity)
            {
                var chunk = Math.Min(capacity, payload.Length - offset);
                WriteTsHeader(output, position, pid, payloadUnitStart: offset == 0, hasAdaptationField: false, hasPayload: true, continuityCounter);
                payload.AsSpan(offset, chunk).CopyTo(output.AsSpan(position + TsHeaderSize));
                output.AsSpan(position + TsHeaderSize + chunk, capacity - chunk).Fill(0xFF);
                continuityCounter = (continuityCounter + 1) & 0x0F;
                position += TsPacketSize;
            }

            return position;
        }

        // One PES across as many TS packets as it takes. The first carries the PCR and
        // random_access_indicator, if any; the last is padded with adaptation-field stuffing bytes
        // (2.4.3.3), never with bytes after the PES.
        private static int WritePes(byte[] output, int position, PesUnit unit, ref int continuityCounter)
        {
            var pes = unit.Pes;
            for (var offset = 0; offset < pes.Length;)
            {
                var first = offset == 0;
                var flagsLength = first ? AdaptationFieldLength(unit) : 0;
                var chunk = Math.Min(TsPacketSize - TsHeaderSize - flagsLength, pes.Length - offset);
                var adaptationFieldLength = TsPacketSize - TsHeaderSize - chunk;

                WriteTsHeader(output, position, unit.Track.Pid, payloadUnitStart: first, hasAdaptationField: adaptationFieldLength > 0, hasPayload: true, continuityCounter);
                if (adaptationFieldLength > 0)
                {
                    byte flags = 0;
                    if (first && unit.IsKeyframe)
                    {
                        flags |= RandomAccessIndicator;
                    }

                    WriteAdaptationField(output.AsSpan(position + TsHeaderSize, adaptationFieldLength), flags, first ? unit.Pcr : null);
                }

                pes.AsSpan(offset, chunk).CopyTo(output.AsSpan(position + TsHeaderSize + adaptationFieldLength));
                offset += chunk;
                continuityCounter = (continuityCounter + 1) & 0x0F;
                position += TsPacketSize;
            }

            return position;
        }

        private static int WritePcrOnlyPacket(byte[] output, int position, int pid, long pcr, int continuityCounter)
        {
            WriteTsHeader(output, position, pid, payloadUnitStart: false, hasAdaptationField: true, hasPayload: false, continuityCounter);
            WriteAdaptationField(output.AsSpan(position + TsHeaderSize, TsPacketSize - TsHeaderSize), 0, pcr);
            return position + TsPacketSize;
        }

        private static void WriteTsHeader(byte[] output, int position, int pid, bool payloadUnitStart, bool hasAdaptationField, bool hasPayload, int continuityCounter)
        {
            var adaptationFieldControl = (hasAdaptationField ? 0x2 : 0) | (hasPayload ? 0x1 : 0);
            output[position] = 0x47;
            output[position + 1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
            output[position + 2] = (byte)pid;
            output[position + 3] = (byte)((adaptationFieldControl << 4) | (continuityCounter & 0x0F));
        }

        // The whole adaptation field, its length byte first, filling `field` exactly: a field of
        // one byte is just adaptation_field_length = 0, which is how a single stuffing byte is
        // written. PCR (2.4.3.5): a 33-bit base in 90 kHz units, 6 reserved bits, a 9-bit
        // extension (always 0 here, since every PCR is computed from a 90 kHz timestamp).
        private static void WriteAdaptationField(Span<byte> field, byte flags, long? pcr)
        {
            field[0] = (byte)(field.Length - 1);
            if (field.Length == 1)
            {
                return;
            }

            var position = 2;
            if (pcr is { } value)
            {
                flags |= PcrFlag;
                var pcrBase = value & PtsWrapMask;
                field[2] = (byte)(pcrBase >> 25);
                field[3] = (byte)(pcrBase >> 17);
                field[4] = (byte)(pcrBase >> 9);
                field[5] = (byte)(pcrBase >> 1);
                field[6] = (byte)(((pcrBase & 1) << 7) | 0x7E);
                field[7] = 0x00;
                position += PcrFieldLength;
            }

            field[1] = flags;
            field[position..].Fill(0xFF);
        }

        private static PacketTrack CreateVideoTrack(MediaTrackInfo info, int pid, byte streamId)
        {
            var configuration = info.CodecConfiguration.ToArray();
            int nalLengthSize;
            IEnumerable<ReadOnlyMemory<byte>> parameterSets;
            try
            {
                if (info.Codec == MediaCodec.H264)
                {
                    var record = AvcDecoderConfigurationRecord.Parse(configuration);
                    nalLengthSize = record.NalLengthSize;
                    parameterSets = record.SequenceParameterSets.Concat(record.PictureParameterSets);
                }
                else
                {
                    var record = HevcDecoderConfigurationRecord.Parse(configuration);
                    nalLengthSize = record.NalLengthSize;
                    parameterSets = record.GetNalUnits(HevcDecoderConfigurationRecord.VpsNalUnitType)
                        .Concat(record.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType))
                        .Concat(record.GetNalUnits(HevcDecoderConfigurationRecord.PpsNalUnitType));
                }
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException($"{info.Codec} track {info.TrackId} has an invalid codec configuration: {e.Message}", TracksParameter, e);
            }

            var annexB = new List<byte>();
            foreach (var nal in parameterSets)
            {
                annexB.AddRange(StartCode);
                annexB.AddRange(nal.Span);
            }

            var isH264 = info.Codec == MediaCodec.H264;
            return new PacketTrack(info, pid, isH264 ? H264StreamType : H265StreamType, streamId, isVideo: true)
            {
                NalLengthSize = nalLengthSize,
                ParameterSets = annexB.ToArray(),
                AccessUnitDelimiterType = isH264 ? 9 : 35,
                SpsType = isH264 ? 7 : HevcDecoderConfigurationRecord.SpsNalUnitType
            };
        }

        private static PacketTrack CreateAudioTrack(MediaTrackInfo info, int pid, byte streamId)
        {
            if (info.CodecConfiguration.IsEmpty)
            {
                throw new ArgumentException($"AAC track {info.TrackId} has no AudioSpecificConfig.", TracksParameter);
            }

            AudioSpecificConfigFields fields;
            try
            {
                fields = AudioSpecificConfigReader.ReadFields(info.CodecConfiguration.Span);
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException($"AAC track {info.TrackId} has an invalid codec configuration: {e.Message}", TracksParameter, e);
            }

            // ADTS's 2-bit profile is audioObjectType - 1, so only Main/LC/SSR/LTP fit.
            if (fields.ObjectType is < 1 or > 4)
            {
                throw new NotSupportedException($"AAC track {info.TrackId} has audio object type {fields.ObjectType}; ADTS carries only types 1-4.");
            }

            if (fields.FrequencyIndex == 15)
            {
                throw new NotSupportedException($"AAC track {info.TrackId} has an explicit sample rate ({fields.SampleRate} Hz); ADTS carries only indexed rates.");
            }

            // channelConfiguration 0 puts the layout in a program config element in the
            // AudioSpecificConfig, which ADTS would have to carry in-band instead.
            if (fields.Channels is < 1 or > 7)
            {
                throw new NotSupportedException($"AAC track {info.TrackId} has channel configuration {fields.Channels}; ADTS carries only 1-7.");
            }

            return new PacketTrack(info, pid, AdtsAacStreamType, streamId, isVideo: false)
            {
                AdtsProfile = fields.ObjectType - 1,
                AdtsSampleRateIndex = fields.FrequencyIndex,
                AdtsChannelConfiguration = fields.Channels
            };
        }

        private sealed class PacketTrack(MediaTrackInfo info, int pid, byte streamType, byte streamId, bool isVideo)
        {
            public MediaTrackInfo Info { get; } = info;

            public int Pid { get; } = pid;

            public byte StreamType { get; } = streamType;

            public byte StreamId { get; } = streamId;

            public bool IsVideo { get; } = isVideo;

            public int NalLengthSize { get; init; }

            public byte[] ParameterSets { get; init; } = [];

            public int AccessUnitDelimiterType { get; init; }

            public int SpsType { get; init; }

            public int AdtsProfile { get; init; }

            public int AdtsSampleRateIndex { get; init; }

            public int AdtsChannelConfiguration { get; init; }

            public long? LastDts { get; set; }

            public long? LastDts90k { get; set; }

            public int ContinuityCounter { get; set; }
        }

        private sealed class PesUnit(PacketTrack track, long sourceDts, long dts90k, bool isKeyframe, byte[] pes)
        {
            public PacketTrack Track { get; } = track;

            public long SourceDts { get; } = sourceDts;

            public long Dts90k { get; } = dts90k;

            public bool IsKeyframe { get; } = isKeyframe;

            public byte[] Pes { get; } = pes;

            public long? Pcr { get; set; }

            // PCRs of the PCR-only packets written just before this PES, in order.
            public List<long> PcrOnlyPackets { get; } = [];
        }
    }
}
