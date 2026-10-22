namespace EggEncoder.Live.Rtmp.Amf0
{
    /// <summary>
    /// The AMF0 type markers (Adobe AMF0 specification, section 2.1) this codec reads and writes. The
    /// reserved and unsupported markers (Reference, Typed Object, MovieClip, RecordSet, Unsupported
    /// and the AMF3 switch) are never produced: <see cref="Amf0Reader"/> rejects them.
    /// </summary>
    public enum Amf0Type : byte
    {
        Number = 0x00,
        Boolean = 0x01,
        String = 0x02,
        Object = 0x03,
        Null = 0x05,
        Undefined = 0x06,
        EcmaArray = 0x08,
        StrictArray = 0x0A,
        Date = 0x0B,
        LongString = 0x0C,
        /// <summary>An XML document, carried as a long string.</summary>
        XmlDocument = 0x0F
    }
}
