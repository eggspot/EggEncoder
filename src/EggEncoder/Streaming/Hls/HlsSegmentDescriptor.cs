namespace EggEncoder.Streaming.Hls
{
    /// <summary>One media segment's own entry in a media playlist.</summary>
    public readonly record struct HlsSegmentDescriptor(long SequenceNumber, double DurationSeconds, string Uri);
}
