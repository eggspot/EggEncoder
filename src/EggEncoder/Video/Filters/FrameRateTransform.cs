namespace EggEncoder.Video.Filters;

/// <summary>
/// Changes the frame rate by dropping and repeating frames by their timestamps, as ffmpeg's <c>fps</c> filter does
/// with <c>round=near</c>. Output frame n is timed at the first input's timestamp plus n frame durations; each input
/// goes to the output slot nearest its timestamp, and slot n shows the latest input placed at or before it, so later
/// inputs in the same slot replace earlier ones and empty slots repeat the previous frame. <see cref="Flush"/> fills
/// the slots up to the end of the last input (its timestamp plus its <see cref="VideoFrame.Duration"/>, or one output
/// frame when that is zero) and always emits the last input at least once. Input timestamps should not decrease.
/// </summary>
public sealed class FrameRateTransform : IVideoFrameTransform
{
    private readonly VideoFrameRate _rate;
    private TimeSpan _start;
    private VideoFrame? _pending;
    private long _nextSlot;
    private TimeSpan _end;

    /// <param name="rate">The output frame rate.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is the default (0/0) value.</exception>
    public FrameRateTransform(VideoFrameRate rate)
    {
        if (rate.Numerator < 1 || rate.Denominator < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The frame rate must be positive");
        }

        _rate = rate;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => true;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(emit);
        if (_pending is null && _nextSlot == 0)
        {
            _start = frame.Timestamp;
        }

        var slot = _rate.NearestFrameIndex(frame.Timestamp - _start);
        if (_pending is not null && slot > _nextSlot)
        {
            EmitPendingUntil(slot, emit);
        }
        else
        {
            _pending?.Dispose();
        }

        _pending = frame;
        _end = frame.Timestamp + (frame.Duration > TimeSpan.Zero ? frame.Duration : _rate.FrameDuration);
    }

    /// <inheritdoc />
    public void Flush(Action<VideoFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (_pending is null)
        {
            return;
        }

        var endSlot = Math.Max(_rate.NearestFrameIndex(_end - _start), _nextSlot + 1);
        EmitPendingUntil(endSlot, emit);
    }

    private void EmitPendingUntil(long endSlot, Action<VideoFrame> emit)
    {
        var pending = _pending!;
        _pending = null;
        for (var slot = _nextSlot; slot < endSlot; slot++)
        {
            var output = slot == endSlot - 1 ? pending : pending.Clone();
            output.Timestamp = _start + _rate.TimestampOf(slot);
            output.Duration = _rate.FrameDuration;
            emit(output);
        }

        _nextSlot = endSlot;
    }
}
