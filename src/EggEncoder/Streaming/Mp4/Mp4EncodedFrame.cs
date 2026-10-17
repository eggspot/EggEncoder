namespace EggEncoder.Streaming.Mp4
{
    /// <summary>
    /// One encoded access unit, durable past a single callback invocation -- the shape
    /// <see cref="FmpMediaSegmentWriter"/> needs, since a caller must copy
    /// <see cref="EncodedAudioFrame.Payload"/> out of its own <c>ref struct</c> before it can be
    /// collected into a list spanning more than one <see cref="EncodedAudioFrameCallback"/> call.
    /// </summary>
    public readonly record struct Mp4EncodedFrame(byte[] Payload, int SampleCount);
}
