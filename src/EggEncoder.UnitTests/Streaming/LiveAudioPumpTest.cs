using EggEncoder.Streaming;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming
{
    public class LiveAudioPumpTest
    {
        private const int SampleRate = 1000; // round numbers make gap-size arithmetic easy to verify by hand
        private const int Channels = 1;

        [Fact]
        public void Constructor_WithNullEncoder_Should_Throw()
        {
            var act = () => new LiveAudioPump(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_WithNonPositiveThreshold_Should_Throw()
        {
            var act = () => new LiveAudioPump(new FakeStreamingEncoder(Channels, SampleRate), silenceFillThresholdSeconds: 0);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void PushSamples_WithSampleCountNotAMultipleOfChannels_Should_Throw()
        {
            var pump = new LiveAudioPump(new FakeStreamingEncoder(channels: 2, SampleRate));

            var act = () => pump.PushSamples(new[] { 1, 2, 3 });

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void Ignore_WithIrregularTimestamps_Should_PushEverySampleThrough_WithNoInsertions()
        {
            var encoder = new FakeStreamingEncoder(Channels, SampleRate);
            var pump = new LiveAudioPump(encoder, DriftPolicy.Ignore);
            var baseTime = DateTimeOffset.UtcNow;

            pump.PushSamples(Repeat(1, 100), baseTime);
            pump.PushSamples(Repeat(2, 100), baseTime + TimeSpan.FromSeconds(50)); // a huge "gap" by the clock, irrelevant under Ignore
            pump.PushSamples(Repeat(3, 100), baseTime + TimeSpan.FromMilliseconds(1)); // even time running backwards changes nothing

            encoder.WrittenSamples.Should().HaveCount(300);
            encoder.WrittenSamples.Take(100).Should().AllBeEquivalentTo(1);
            encoder.WrittenSamples.Skip(100).Take(100).Should().AllBeEquivalentTo(2);
            encoder.WrittenSamples.Skip(200).Take(100).Should().AllBeEquivalentTo(3);
        }

        [Fact]
        public void SilenceFill_FirstCall_Should_NeverInsertSilence()
        {
            var encoder = new FakeStreamingEncoder(Channels, SampleRate);
            var pump = new LiveAudioPump(encoder, DriftPolicy.SilenceFill);

            pump.PushSamples(Repeat(7, 100), DateTimeOffset.UtcNow);

            encoder.WrittenSamples.Should().Equal(Repeat(7, 100).ToArray());
        }

        [Fact]
        public void SilenceFill_WithARealGap_Should_InsertExactlyEnoughSilence_ToCloseIt()
        {
            var encoder = new FakeStreamingEncoder(Channels, SampleRate);
            var pump = new LiveAudioPump(encoder, DriftPolicy.SilenceFill, silenceFillThresholdSeconds: 0.1);
            var t0 = DateTimeOffset.UtcNow;

            pump.PushSamples(Repeat(1, 100), t0); // establishes the reference point; 100 frames "real" so far
            pump.PushSamples(Repeat(2, 100), t0 + TimeSpan.FromSeconds(2)); // 2000 frames expected by now, only 100 actual -> 1900-frame gap

            encoder.WrittenSamples.Should().HaveCount(100 + 1900 + 100);
            encoder.WrittenSamples.Take(100).Should().AllBeEquivalentTo(1);
            encoder.WrittenSamples.Skip(100).Take(1900).Should().AllBeEquivalentTo(0, "the inserted silence must be exactly zero-valued samples");
            encoder.WrittenSamples.Skip(2000).Take(100).Should().AllBeEquivalentTo(2);
        }

        [Fact]
        public void SilenceFill_WithOnlySchedulingJitter_Should_NotInsertSilence()
        {
            var encoder = new FakeStreamingEncoder(Channels, SampleRate);
            var pump = new LiveAudioPump(encoder, DriftPolicy.SilenceFill, silenceFillThresholdSeconds: 0.1);
            var t0 = DateTimeOffset.UtcNow;

            pump.PushSamples(Repeat(1, 100), t0);
            // 100 frames (0.1s) elapse in real audio; wall clock says 0.12s passed -- a 20ms gap, under the 100ms threshold.
            pump.PushSamples(Repeat(2, 100), t0 + TimeSpan.FromMilliseconds(120));

            encoder.WrittenSamples.Should().HaveCount(200, "a gap smaller than the configured threshold must not trigger silence-fill");
        }

        [Fact]
        public void SilenceFill_OmittingCaptureTimestamp_Should_NeverTriggerSilenceFill()
        {
            var encoder = new FakeStreamingEncoder(Channels, SampleRate);
            var pump = new LiveAudioPump(encoder, DriftPolicy.SilenceFill, silenceFillThresholdSeconds: 0.1);
            var t0 = DateTimeOffset.UtcNow;

            pump.PushSamples(Repeat(1, 100), t0);
            pump.PushSamples(Repeat(2, 100)); // no timestamp at all -- must be pure pass-through regardless of the policy
            pump.PushSamples(Repeat(3, 100), t0 + TimeSpan.FromSeconds(10)); // a real gap, but measured only against the last call that DID carry a timestamp

            // No silence from the untimed call; the final call's own gap is measured from t0 against the
            // cumulative 200 real frames pushed so far (100 + 100), not reset by the untimed call.
            var expectedGapFrames = (10 * SampleRate) - 200;
            encoder.WrittenSamples.Should().HaveCount(100 + 100 + (int)expectedGapFrames + 100);
        }

        private static int[] Repeat(int value, int count) => Enumerable.Repeat(value, count).ToArray();

        private sealed class FakeStreamingEncoder : IStreamingEncoder
        {
            public FakeStreamingEncoder(int channels, int sampleRate)
            {
                Channels = channels;
                SampleRate = sampleRate;
            }

            public int Channels { get; }

            public int SampleRate { get; }

            public int SamplesPerFrame => 1;

            public List<int> WrittenSamples { get; } = [];

            public void Write(ReadOnlySpan<int> interleavedSamples) => WrittenSamples.AddRange(interleavedSamples.ToArray());

            public void Complete()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
