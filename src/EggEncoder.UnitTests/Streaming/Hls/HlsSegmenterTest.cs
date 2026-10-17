using System.Text;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Aac;
using EggEncoder.Streaming.Hls;
using EggEncoder.Streaming.Mp4;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class HlsSegmenterTest
    {
        private const int SampleRate = 44100;
        private const int SamplesPerFrame = 1024;

        [Fact]
        public void Constructor_Should_WriteInitSegment_Immediately()
        {
            var sink = new InMemoryStreamingSink();

            _ = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 6, sink);

            sink.InitSegment.Should().Equal(FmpInitSegmentWriter.BuildInitSegment(1, SampleRate));
        }

        [Fact]
        public void Constructor_WithNonPositiveTargetDuration_Should_Throw()
        {
            var act = () => new HlsSegmenter(1, SampleRate, targetDurationSeconds: 0, new InMemoryStreamingSink());

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_WithStereoChannels_Should_Throw()
        {
            var act = () => new HlsSegmenter(2, SampleRate, targetDurationSeconds: 6, new InMemoryStreamingSink());

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void AddFrame_BelowTargetDuration_Should_NotCutASegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 6, sink);

            segmenter.AddFrame(Frame(SamplesPerFrame, 0));

            sink.MediaSegments.Should().BeEmpty();
        }

        [Fact]
        public void AddFrame_ReachingTargetDuration_Should_CutExactlyOneSegment()
        {
            var sink = new InMemoryStreamingSink();
            // 1-second target; 44100/1024 ~= 43.07, so the 44th frame pushes cumulative duration past 1.0.
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink);

            for (var i = 0; i < 44; i++)
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            sink.MediaSegments.Should().ContainSingle();
            sink.MediaSegments.Should().ContainKey(1);
        }

        [Fact]
        public void Complete_WithPendingFrames_Should_FlushFinalShortSegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 6, sink);

            segmenter.AddFrame(Frame(SamplesPerFrame, 0));
            segmenter.AddFrame(Frame(500, 1));
            segmenter.Complete();

            sink.MediaSegments.Should().ContainSingle();

            var playlistText = Encoding.UTF8.GetString(sink.Playlist!);
            playlistText.Should().Contain("#EXT-X-ENDLIST");
        }

        [Fact]
        public void Complete_WithNoPendingFrames_Should_NotAddAnEmptyFinalSegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink);

            for (var i = 0; i < 44; i++)
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            var segmentCountBeforeComplete = sink.MediaSegments.Count;
            segmenter.Complete();

            sink.MediaSegments.Should().HaveCount(segmentCountBeforeComplete);
        }

        [Fact]
        public void Complete_CalledTwice_Should_BeIdempotent()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 6, sink);
            segmenter.AddFrame(Frame(SamplesPerFrame, 0));

            segmenter.Complete();
            var segmentCountAfterFirstComplete = sink.MediaSegments.Count;

            var act = () => segmenter.Complete();

            act.Should().NotThrow();
            sink.MediaSegments.Should().HaveCount(segmentCountAfterFirstComplete);
        }

        [Fact]
        public void AddFrame_AfterComplete_Should_Throw()
        {
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 6, new InMemoryStreamingSink());
            segmenter.Complete();

            var act = () => segmenter.AddFrame(Frame(SamplesPerFrame, 0));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Playlist_TargetDurationTag_Should_Never_Be_Shorter_Than_An_Overshot_Segment()
        {
            var sink = new InMemoryStreamingSink();
            // 44 frames of 1024 samples = 45056 samples = 1.0219s -- overshoots the 1-second target.
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink);
            for (var i = 0; i < 44; i++)
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            var playlistText = Encoding.UTF8.GetString(sink.Playlist!);

            playlistText.Should().Contain("#EXT-X-TARGETDURATION:2", "the real segment duration (~1.02s) must round up past the configured 1-second target, not silently violate RFC 8216 §4.3.3.1");
        }

        [Fact]
        public void Playlist_Should_ListOneExtinfPerSegment_AndReferenceTheInitSegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink);
            for (var i = 0; i < 88; i++) // ~2 seconds -> 2 segments at a 1-second target
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            segmenter.Complete();

            var playlistText = Encoding.UTF8.GetString(sink.Playlist!);
            var extinfCount = playlistText.Split('\n').Count(l => l.StartsWith("#EXTINF:"));

            extinfCount.Should().Be(sink.MediaSegments.Count);
            playlistText.Should().Contain($"#EXT-X-MAP:URI=\"{HlsSegmenter.InitSegmentUri}\"");
        }

        [Fact]
        public void EndToEnd_RealAacStreamingEncoder_Should_ProduceConsistentSegmentsAndPlaylist()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink);

            var totalSamplesWritten = 0;
            using (var encoder = AacStreamingEncoder.Create(1, SampleRate, frame =>
                   {
                       segmenter.AddFrame(frame);
                   }))
            {
                var samples = new int[SampleRate * 3]; // 3 seconds
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
                }

                encoder.Write(samples);
                totalSamplesWritten = samples.Length;
                encoder.Complete();
            }

            segmenter.Complete();

            sink.MediaSegments.Should().NotBeEmpty();

            var totalSampleCountAcrossSegments = 0L;
            foreach (var segmentBytes in sink.MediaSegments.Values)
            {
                totalSampleCountAcrossSegments += ReadTrunSampleCountSum(segmentBytes);
            }

            totalSampleCountAcrossSegments.Should().Be(totalSamplesWritten);
        }

        [Fact]
        public void LiveMode_Constructor_WithLiveWindowSizeLessThanOne_Should_Throw()
        {
            var act = () => new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, new InMemoryStreamingSink(), liveWindowSize: 0);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void LiveMode_AfterExceedingWindowSize_Should_RetainOnlyTheMostRecentSegments_AndDeleteTheRest()
        {
            var sink = new InMemoryStreamingSink();
            // 1-second target; one segment cut per 44 frames (see AddFrame_ReachingTargetDuration test).
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink, liveWindowSize: 2);

            for (var i = 0; i < 44 * 5; i++) // 5 segments' worth
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            sink.MediaSegments.Should().HaveCount(2, "only the live window's own most recent segments should still be retained");
            sink.MediaSegments.Keys.Should().BeEquivalentTo(new long[] { 4, 5 });
        }

        [Fact]
        public void LiveMode_MediaSequence_Should_MatchOldestRetainedSegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink, liveWindowSize: 2);

            for (var i = 0; i < 44 * 3; i++) // 3 segments -- exceeds the window by 1
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            var playlistText = Encoding.UTF8.GetString(sink.Playlist!);

            playlistText.Should().Contain("#EXT-X-MEDIA-SEQUENCE:2", "sequence 1 aged out, so 2 is now the oldest retained segment");
        }

        [Fact]
        public void LiveMode_Should_NotWriteEndList_UntilComplete()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink, liveWindowSize: 2);

            for (var i = 0; i < 44 * 3; i++)
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            Encoding.UTF8.GetString(sink.Playlist!).Should().NotContain("#EXT-X-ENDLIST");

            segmenter.Complete();

            Encoding.UTF8.GetString(sink.Playlist!).Should().Contain("#EXT-X-ENDLIST");
        }

        [Fact]
        public void LiveMode_DeleteMediaSegment_Should_BeCalledExactlyOncePerDroppedSegment_InAgingOutOrder()
        {
            var deletedInOrder = new List<long>();
            var sink = new DelegateStreamingSink(
                onInitSegment: _ => { },
                onMediaSegment: (_, _) => { },
                onPlaylist: _ => { },
                onDeleteMediaSegment: seq => deletedInOrder.Add(seq));
            var segmenter = new HlsSegmenter(1, SampleRate, targetDurationSeconds: 1, sink, liveWindowSize: 2);

            for (var i = 0; i < 44 * 4; i++) // 4 segments -- 2 must age out
            {
                segmenter.AddFrame(Frame(SamplesPerFrame, i));
            }

            deletedInOrder.Should().Equal(1L, 2L);
        }

        private static EncodedAudioFrame Frame(int sampleCount, long frameIndex) =>
            new(new byte[] { 0xAA, 0xBB, 0xCC }, sampleCount, frameIndex);

        // Sums every sample's own duration from a produced segment's trun box, reusing
        // FmpSegmentWriterTest's own "self-parse, don't trust Mp4SampleTable for fragmented files"
        // approach, but only for the one field this test actually needs.
        private static long ReadTrunSampleCountSum(byte[] segmentBytes)
        {
            using var stream = new MemoryStream(segmentBytes);
            var topLevel = EggEncoder.Codecs.Mov.MovAtomReader.EnumerateAtoms(stream, 0, stream.Length);
            var moof = topLevel.Single(a => a.Type == "moof");
            var traf = EggEncoder.Codecs.Mov.MovAtomReader.FindAtom(stream, "traf", moof.ContentStart, moof.ContentEnd)!.Value;
            var trun = EggEncoder.Codecs.Mov.MovAtomReader.FindAtom(stream, "trun", traf.ContentStart, traf.ContentEnd)!.Value;

            stream.Position = trun.ContentStart + 4;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            var sampleCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer);

            var total = 0L;
            var entryPosition = trun.ContentStart + 12;
            for (var i = 0; i < sampleCount; i++)
            {
                stream.Position = entryPosition;
                stream.ReadExactly(buffer);
                total += System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer);
                entryPosition += 8;
            }

            return total;
        }
    }
}
