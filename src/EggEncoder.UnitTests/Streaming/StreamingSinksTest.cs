using EggEncoder.Streaming;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming
{
    public class StreamingSinksTest
    {
        [Fact]
        public void InMemoryStreamingSink_Should_RoundTrip_EverythingWritten()
        {
            var sink = new InMemoryStreamingSink();

            sink.WriteInitSegment([1, 2, 3]);
            sink.WriteMediaSegment(1, [4, 5]);
            sink.WriteMediaSegment(2, [6]);
            sink.WritePlaylist([7, 8, 9]);

            sink.InitSegment.Should().Equal(1, 2, 3);
            sink.MediaSegments[1].Should().Equal(4, 5);
            sink.MediaSegments[2].Should().Equal(6);
            sink.Playlist.Should().Equal(7, 8, 9);
        }

        [Fact]
        public void InMemoryStreamingSink_DeleteMediaSegment_Should_RemoveIt()
        {
            var sink = new InMemoryStreamingSink();
            sink.WriteMediaSegment(1, [4, 5]);

            sink.DeleteMediaSegment(1);

            sink.MediaSegments.Should().NotContainKey(1);
        }

        [Fact]
        public void InMemoryStreamingSink_DeleteMediaSegment_WithUnknownSequenceNumber_Should_BeSafe()
        {
            var sink = new InMemoryStreamingSink();

            var act = () => sink.DeleteMediaSegment(999);

            act.Should().NotThrow();
        }

        [Fact]
        public void FileSystemStreamingSink_Should_WriteFilesToDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hls_sink_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var sink = new FileSystemStreamingSink(directory);

                sink.WriteInitSegment([1, 2, 3]);
                sink.WriteMediaSegment(1, [4, 5]);
                sink.WritePlaylist("#EXTM3U\n"u8);

                File.ReadAllBytes(Path.Combine(directory, "init.mp4")).Should().Equal(1, 2, 3);
                File.ReadAllBytes(Path.Combine(directory, "segment_1.m4s")).Should().Equal(4, 5);
                File.Exists(Path.Combine(directory, "playlist.m3u8")).Should().BeTrue();

                sink.DeleteMediaSegment(1);
                File.Exists(Path.Combine(directory, "segment_1.m4s")).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void FileSystemStreamingSink_DeleteMediaSegment_WithMissingFile_Should_BeSafe()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hls_sink_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var sink = new FileSystemStreamingSink(directory);

                var act = () => sink.DeleteMediaSegment(42);

                act.Should().NotThrow();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void FileSystemStreamingSink_WithNonExistentDirectory_Should_Throw()
        {
            var act = () => new FileSystemStreamingSink(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}"));

            act.Should().Throw<DirectoryNotFoundException>();
        }

        [Fact]
        public void FileSystemStreamingSink_WithFilePrefix_Should_PrefixEveryFileName()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hls_sink_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var sink = new FileSystemStreamingSink(directory, "rendition_a_");

                sink.WriteInitSegment([1]);

                File.Exists(Path.Combine(directory, "rendition_a_init.mp4")).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void DelegateStreamingSink_Should_ForwardEachCall_ExactlyOnce()
        {
            byte[]? initSegment = null;
            (long Sequence, byte[] Bytes)? mediaSegment = null;
            byte[]? playlist = null;
            long? deletedSequence = null;

            var sink = new DelegateStreamingSink(
                onInitSegment: bytes => initSegment = bytes,
                onMediaSegment: (seq, bytes) => mediaSegment = (seq, bytes),
                onPlaylist: bytes => playlist = bytes,
                onDeleteMediaSegment: seq => deletedSequence = seq);

            sink.WriteInitSegment([1, 2]);
            sink.WriteMediaSegment(5, [3, 4]);
            sink.WritePlaylist([5, 6]);
            sink.DeleteMediaSegment(5);

            initSegment.Should().Equal(1, 2);
            mediaSegment!.Value.Sequence.Should().Be(5);
            mediaSegment!.Value.Bytes.Should().Equal(3, 4);
            playlist.Should().Equal(5, 6);
            deletedSequence.Should().Be(5);
        }

        [Fact]
        public void DelegateStreamingSink_WithAnyNullDelegate_Should_Throw()
        {
            Action<byte[]> noop1 = _ => { };
            Action<long, byte[]> noop2 = (_, _) => { };
            Action<byte[]> noop3 = _ => { };
            Action<long> noop4 = _ => { };

            ((Action)(() => new DelegateStreamingSink(null!, noop2, noop3, noop4))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(noop1, null!, noop3, noop4))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(noop1, noop2, null!, noop4))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(noop1, noop2, noop3, null!))).Should().ThrowExactly<ArgumentNullException>();
        }
    }
}
