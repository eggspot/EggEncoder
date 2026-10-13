namespace EggEncoder.Codecs.Wma
{
    internal static class AsfContainerReader
    {
        private const byte ErrorCorrectionPresentFlag = 0x80;
        private const byte ErrorCorrectionLengthTypeMask = 0x60;
        private const byte ErrorCorrectionDataSizeMask = 0x02;
        private const byte MultiplePayloadsPresentFlag = 0x01;
        private const byte NumberOfPayloadsMask = 0x3F;

        private const byte PacketLengthFieldSizeMask = 0x60;
        private const byte SequenceFieldSizeMask = 0x06;
        private const byte PaddingLengthFieldSizeMask = 0x18;

        private const byte MediaObjectNumberFieldSizeMask = 0x30;
        private const byte OffsetIntoMediaObjectFieldSizeMask = 0x0C;
        private const byte ReplicatedDataLengthFieldSizeMask = 0x03;

        public static WmaStreamProperties Read(string wmaFilePath, out List<byte[]> framePayloads)
        {
            var fileBytes = File.ReadAllBytes(wmaFilePath);
            var position = 0;

            var headerGuid = ReadGuid(fileBytes, ref position);
            if (headerGuid != AsfGuids.HeaderObject)
            {
                throw new InvalidDataException("Missing ASF Header Object");
            }

            var headerSize = (long)ReadUInt64(fileBytes, ref position);
            var headerObjectCount = ReadUInt32(fileBytes, ref position);
            position += 2;

            WmaStreamProperties? streamProperties = null;
            var packetSize = 0;

            for (var i = 0; i < headerObjectCount; i++)
            {
                var subObjectStart = position;
                var subObjectGuid = ReadGuid(fileBytes, ref position);
                var subObjectSize = (long)ReadUInt64(fileBytes, ref position);

                if (subObjectGuid == AsfGuids.FilePropertiesObject)
                {
                    // Preroll is nominally 64-bit; only the low 32 bits (preroll) matter, the high 32 (ignore) are skipped.
                    position += 16 + 8 + 8 + 8 + 8 + 8 + 4 + 4 + 4;
                    packetSize = (int)ReadUInt32(fileBytes, ref position);
                }
                else if (subObjectGuid == AsfGuids.StreamPropertiesObject)
                {
                    var streamTypeGuid = ReadGuid(fileBytes, ref position);
                    if (streamTypeGuid == AsfGuids.AudioStreamType)
                    {
                        streamProperties = ReadStreamProperties(fileBytes, ref position);
                    }
                }

                position = (int)(subObjectStart + subObjectSize);
            }

            position = (int)headerSize;

            var dataGuid = ReadGuid(fileBytes, ref position);
            if (dataGuid != AsfGuids.DataObject)
            {
                throw new InvalidDataException("Missing ASF Data Object");
            }

            position += 8 + 16;
            var totalDataPackets = ReadUInt64(fileBytes, ref position);
            position += 2;

            if (streamProperties is null)
            {
                throw new NotSupportedException("ASF file does not contain a WMA audio stream");
            }

            if (packetSize <= 0)
            {
                throw new InvalidDataException("Invalid ASF packet size");
            }

            framePayloads = [];
            for (var packetIndex = 0UL; packetIndex < totalDataPackets; packetIndex++)
            {
                var packetStart = position;
                ReadPacketPayloads(fileBytes, ref position, packetSize, framePayloads);
                position = packetStart + packetSize;
            }

            return streamProperties.Value;

            static WmaStreamProperties ReadStreamProperties(byte[] fileBytes, ref int position)
            {
                position += 16 + 8;
                var typeSpecificDataLength = ReadUInt32(fileBytes, ref position);
                position += 4 + 2 + 4;

                var typeSpecificDataStart = position;
                var formatTag = ReadUInt16(fileBytes, ref position);
                var channels = ReadUInt16(fileBytes, ref position);
                var samplesPerSecond = ReadUInt32(fileBytes, ref position);
                position += 4;
                var blockAlign = ReadUInt16(fileBytes, ref position);
                var bitsPerSample = ReadUInt16(fileBytes, ref position);
                var extraDataSize = ReadUInt16(fileBytes, ref position);

                if (formatTag != 0x0161)
                {
                    throw new NotSupportedException($"WMA format tag 0x{formatTag:X4} is not supported; only WMAv2 (0x0161) is supported");
                }

                EnsureBytesAvailable(fileBytes, position, extraDataSize);
                var extraData = new byte[extraDataSize];
                Array.Copy(fileBytes, position, extraData, 0, extraDataSize);

                position = (int)(typeSpecificDataStart + typeSpecificDataLength);

                return new WmaStreamProperties
                {
                    Channels = channels,
                    SampleRate = (int)samplesPerSecond,
                    BlockAlign = blockAlign,
                    BitsPerSample = bitsPerSample,
                    ExtraData = extraData
                };
            }

            static void ReadPacketPayloads(byte[] fileBytes, ref int position, int packetSize, List<byte[]> framePayloads)
            {
                var errorCorrectionFlags = ReadByte(fileBytes, ref position);

                byte lengthFlags;
                if ((errorCorrectionFlags & ErrorCorrectionPresentFlag) != 0)
                {
                    if ((errorCorrectionFlags & ErrorCorrectionLengthTypeMask) == 0)
                    {
                        position += errorCorrectionFlags & ErrorCorrectionDataSizeMask;
                    }

                    lengthFlags = ReadByte(fileBytes, ref position);
                }
                else
                {
                    lengthFlags = errorCorrectionFlags;
                }

                var propertyFlags = ReadByte(fileBytes, ref position);

                ReadVariableLengthField(fileBytes, ref position, lengthFlags, PacketLengthFieldSizeMask, 0x20, 0x40, 0x60);
                ReadVariableLengthField(fileBytes, ref position, lengthFlags, SequenceFieldSizeMask, 0x02, 0x04, 0x06);
                ReadVariableLengthField(fileBytes, ref position, lengthFlags, PaddingLengthFieldSizeMask, 0x08, 0x10, 0x18);

                position += 4 + 2;

                if ((lengthFlags & MultiplePayloadsPresentFlag) == 0)
                {
                    throw new NotSupportedException("ASF packets without the multiple-payloads flag are not supported");
                }

                var payloadFlags = ReadByte(fileBytes, ref position);
                var payloadCount = payloadFlags & NumberOfPayloadsMask;

                for (var i = 0; i < payloadCount; i++)
                {
                    position += 1;
                    ReadVariableLengthField(fileBytes, ref position, propertyFlags, MediaObjectNumberFieldSizeMask, 0x10, 0x20, 0x30);
                    ReadVariableLengthField(fileBytes, ref position, propertyFlags, OffsetIntoMediaObjectFieldSizeMask, 0x04, 0x08, 0x0C);
                    var replicatedDataLength = ReadVariableLengthField(fileBytes, ref position, propertyFlags, ReplicatedDataLengthFieldSizeMask, 0x01, 0x02, 0x03);

                    if (replicatedDataLength == 1)
                    {
                        throw new NotSupportedException("ASF compressed sub-payloads are not supported");
                    }

                    if (replicatedDataLength >= 8)
                    {
                        position += (int)replicatedDataLength;
                    }

                    var payloadLength = ReadUInt16(fileBytes, ref position);
                    EnsureBytesAvailable(fileBytes, position, payloadLength);
                    var payload = new byte[payloadLength];
                    Array.Copy(fileBytes, position, payload, 0, payloadLength);
                    position += payloadLength;

                    framePayloads.Add(payload);
                }
            }

            static uint ReadVariableLengthField(byte[] fileBytes, ref int position, byte flags, byte mask, byte byteFlag, byte wordFlag, byte dwordFlag)
            {
                var maskedValue = flags & mask;
                if (maskedValue == byteFlag)
                {
                    return ReadByte(fileBytes, ref position);
                }

                if (maskedValue == wordFlag)
                {
                    return ReadUInt16(fileBytes, ref position);
                }

                if (maskedValue == dwordFlag)
                {
                    return ReadUInt32(fileBytes, ref position);
                }

                return 0;
            }
        }

        private static byte ReadByte(byte[] fileBytes, ref int position)
        {
            EnsureBytesAvailable(fileBytes, position, 1);
            var value = fileBytes[position];
            position += 1;

            return value;
        }

        private static Guid ReadGuid(byte[] fileBytes, ref int position)
        {
            EnsureBytesAvailable(fileBytes, position, 16);
            var guid = new Guid(fileBytes.AsSpan(position, 16));
            position += 16;

            return guid;
        }

        private static ushort ReadUInt16(byte[] fileBytes, ref int position)
        {
            EnsureBytesAvailable(fileBytes, position, 2);
            var value = BitConverter.ToUInt16(fileBytes, position);
            position += 2;

            return value;
        }

        private static uint ReadUInt32(byte[] fileBytes, ref int position)
        {
            EnsureBytesAvailable(fileBytes, position, 4);
            var value = BitConverter.ToUInt32(fileBytes, position);
            position += 4;

            return value;
        }

        private static ulong ReadUInt64(byte[] fileBytes, ref int position)
        {
            EnsureBytesAvailable(fileBytes, position, 8);
            var value = BitConverter.ToUInt64(fileBytes, position);
            position += 8;

            return value;
        }

        // Every read in this file funnels through here (or through Array.Copy call sites that call
        // this directly themselves) -- confirmed by a truncated/corrupted real file previously
        // throwing ArgumentOutOfRangeException (BitConverter.ToUInt16/32/64, Guid's own span
        // constructor) or even an un-typed IndexOutOfRangeException (the raw fileBytes[position]
        // indexing ReadByte now replaces), neither of which is the clear, typed
        // InvalidDataException every other reader in this codebase (WavReader/AiffReader/AuReader's
        // own ReadFully loops, CafReader/TtaReader's own explicit short-read checks) already gives
        // for this exact class of malformed input. (long)position avoids the position+count
        // addition itself overflowing int range first, for a position already corrupted by a
        // bogus file-declared size field upstream.
        private static void EnsureBytesAvailable(byte[] fileBytes, int position, int count)
        {
            if (position < 0 || (long)position + count > fileBytes.Length)
            {
                throw new InvalidDataException($"ASF file is truncated or corrupted: expected {count} more byte(s) at position {position}, but the file is only {fileBytes.Length} byte(s) long");
            }
        }
    }

    internal readonly struct WmaStreamProperties
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BlockAlign { get; init; }

        public required int BitsPerSample { get; init; }

        public required byte[] ExtraData { get; init; }
    }
}
