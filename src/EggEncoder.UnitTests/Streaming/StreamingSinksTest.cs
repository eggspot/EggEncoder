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

        [Fact]
        public void InMemoryStreamingSink_NamedInitSegments_Should_BeKeptByName_And_Deletable()
        {
            var sink = new InMemoryStreamingSink();

            sink.WriteInitSegment("init.mp4", [1]).Should().Be("init.mp4");
            sink.WriteInitSegment("init_2.mp4", [2]).Should().Be("init_2.mp4");
            sink.DeleteInitSegment("init.mp4");
            sink.DeleteInitSegment("never_written.mp4");

            sink.InitSegment.Should().Equal(2);
            sink.InitSegments.Keys.Should().Equal("init_2.mp4");
            sink.InitSegments["init_2.mp4"].Should().Equal(2);
        }

        [Fact]
        public void InMemoryStreamingSink_InvalidInitSegmentNames_Should_Throw()
        {
            var sink = new InMemoryStreamingSink();

            sink.Invoking(s => s.WriteInitSegment("", [1])).Should().ThrowExactly<ArgumentException>();
            sink.Invoking(s => s.WriteInitSegment(null!, [1])).Should().ThrowExactly<ArgumentNullException>();
            sink.Invoking(s => s.DeleteInitSegment(null!)).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void FileSystemStreamingSink_DeleteInitSegment_Should_DeleteTheFileItReturned()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hls_sink_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var sink = new FileSystemStreamingSink(directory, "a_");
                var uri = sink.WriteInitSegment("init_2.mp4", [1]);

                sink.DeleteInitSegment(uri);

                uri.Should().Be("a_init_2.mp4");
                File.Exists(Path.Combine(directory, uri)).Should().BeFalse();
                sink.Invoking(s => s.DeleteInitSegment(uri)).Should().NotThrow("the file is already gone");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Theory]
        [InlineData("init_2.mp4", "no prefix")]
        [InlineData("a_../init_2.mp4", "a directory part")]
        [InlineData("a_", "no file name")]
        public void FileSystemStreamingSink_DeleteInitSegment_UriItDidNotReturn_Should_Throw(string uri, string reason)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hls_sink_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var sink = new FileSystemStreamingSink(directory, "a_");

                sink.Invoking(s => s.DeleteInitSegment(uri)).Should().Throw<ArgumentException>(reason);
                sink.Invoking(s => s.DeleteInitSegment(null!)).Should().ThrowExactly<ArgumentNullException>();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void DelegateStreamingSink_WithNamedInitSegments_Should_ForwardNames_And_Deletions()
        {
            var initSegments = new List<(string Name, byte[] Bytes)>();
            var deletedInits = new List<string>();
            var sink = new DelegateStreamingSink(
                onInitSegment: (name, bytes) => initSegments.Add((name, bytes)),
                onMediaSegment: (_, _) => { },
                onPlaylist: _ => { },
                onDeleteMediaSegment: _ => { },
                onDeleteInitSegment: deletedInits.Add);

            sink.WriteInitSegment([1]);
            sink.WriteInitSegment("init_2.mp4", [2]).Should().Be("init_2.mp4");
            sink.DeleteInitSegment("init.mp4");

            initSegments.Select(s => s.Name).Should().Equal("init.mp4", "init_2.mp4");
            initSegments[1].Bytes.Should().Equal(2);
            deletedInits.Should().Equal("init.mp4");
            sink.Invoking(s => s.WriteInitSegment("", [1])).Should().ThrowExactly<ArgumentException>();
            sink.Invoking(s => s.DeleteInitSegment(null!)).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void DelegateStreamingSink_WithUnnamedInitSegments_Should_ForwardTheBytes_And_IgnoreDeletions()
        {
            var initSegments = new List<byte[]>();
            var sink = new DelegateStreamingSink(initSegments.Add, (_, _) => { }, _ => { }, _ => { });

            sink.WriteInitSegment("init_2.mp4", [2]).Should().Be("init_2.mp4");
            sink.DeleteInitSegment("init_2.mp4");

            initSegments.Should().ContainSingle().Which.Should().Equal(2);
        }

        [Fact]
        public void DelegateStreamingSink_WithNamedInitSegments_AndAnyNullDelegate_Should_Throw()
        {
            Action<string, byte[]> init = (_, _) => { };
            Action<long, byte[]> media = (_, _) => { };
            Action<byte[]> playlist = _ => { };
            Action<long> deleteMedia = _ => { };
            Action<string> deleteInit = _ => { };

            ((Action)(() => new DelegateStreamingSink(null!, media, playlist, deleteMedia, deleteInit))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(init, null!, playlist, deleteMedia, deleteInit))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(init, media, null!, deleteMedia, deleteInit))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(init, media, playlist, null!, deleteInit))).Should().ThrowExactly<ArgumentNullException>();
            ((Action)(() => new DelegateStreamingSink(init, media, playlist, deleteMedia, null!))).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void IStreamingSink_DefaultDeleteInitSegment_Should_DoNothing()
        {
            IStreamingSink sink = new MinimalSink();

            sink.Invoking(s => s.DeleteInitSegment("init.mp4")).Should().NotThrow();
        }

        private sealed class MinimalSink : IStreamingSink
        {
            public void WriteInitSegment(ReadOnlySpan<byte> bytes) => throw new InvalidOperationException();

            public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException();

            public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => throw new InvalidOperationException();

            public void DeleteMediaSegment(long sequenceNumber) => throw new InvalidOperationException();
        }
    }
}
