namespace EggEncoder.Pcm;

/// <summary>One input of <see cref="PcmMixer.Mix"/>.</summary>
/// <param name="Audio">The audio; converted to the mix's format first.</param>
/// <param name="Gain">Linear gain applied before summing (ffmpeg <c>amix weights</c>); must be finite.</param>
/// <param name="StartOffset">Silence before the input starts (ffmpeg <c>adelay</c>), rounded to whole frames; must not be negative.</param>
/// <param name="Loop">Repeat the input from its first frame for as long as the mix runs (ffmpeg <c>aloop</c>).</param>
public readonly record struct PcmMixInput(PcmAudio Audio, double Gain = 1.0, TimeSpan StartOffset = default, bool Loop = false);
