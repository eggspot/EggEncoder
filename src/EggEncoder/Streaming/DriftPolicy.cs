namespace EggEncoder.Streaming
{
    /// <summary>How <see cref="LiveAudioPump"/> reacts when a live source's own wall-clock timing falls behind its declared sample rate.</summary>
    public enum DriftPolicy
    {
        /// <summary>Push every sample through unmodified, regardless of measured drift -- the right choice when the caller already has its own clock discipline.</summary>
        Ignore,

        /// <summary>Insert silence to catch up to wall-clock time once a real gap (not just scheduling jitter) is detected.</summary>
        SilenceFill
    }
}
