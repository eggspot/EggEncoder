using EggEncoder.Codecs.H264;
using EggEncoder.Video;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class DecodedPictureBufferTest
    {
        private static readonly VideoFramePool Pool = new();

        private static SequenceParameterSet Sps(int pocType = 2, int maxRefs = 2, int? reorder = 0, int? dpb = 4, int[]? cycle = null, int offsetNonRef = 0) => new()
        {
            ProfileIdc = 77,
            LevelIdc = 30,
            WidthInMacroblocks = 1,
            HeightInMacroblocks = 1,
            Log2MaxFrameNum = 4,
            PicOrderCountType = pocType,
            Log2MaxPicOrderCountLsb = 4,
            OffsetForReferenceFrame = cycle ?? [],
            OffsetForNonReferencePicture = offsetNonRef,
            MaxNumberOfReferenceFrames = maxRefs,
            MaxNumberOfReorderFrames = reorder,
            MaxDecodedFrameBuffering = dpb
        };

        private static SliceHeader Header(int frameNum, bool idr = false, int referenceIdc = 1, int pocLsb = 0, MemoryManagementOperation[]? operations = null, bool noOutput = false) => new()
        {
            FrameNum = frameNum,
            IsIdr = idr,
            NalReferenceIdc = referenceIdc,
            PicOrderCountLsb = pocLsb,
            Type = SliceType.P,
            ReferenceCountL0 = 4,
            MemoryManagement = operations,
            NoOutputOfPriorPictures = noOutput
        };

        private static (H264Picture Picture, List<H264Picture> Output) Decode(DecodedPictureBuffer buffer, SequenceParameterSet sps, SliceHeader header)
        {
            var picture = new H264Picture(Pool, sps)
            {
                PictureId = buffer.NewPictureId(),
                FrameNum = header.FrameNum,
                IsReference = header.NalReferenceIdc != 0,
                IsIdr = header.IsIdr
            };
            buffer.ComputePicOrderCount(picture, header, sps);
            buffer.Mark(picture, header, sps);
            var output = buffer.Store(picture, header, sps).ToList();
            return (picture, output);
        }

        [Fact]
        public void Poc_Type_0_Should_Carry_The_Msb_Across_Lsb_Wraps()
        {
            var sps = Sps(pocType: 0);
            var buffer = new DecodedPictureBuffer();
            var pocs = new List<int>();
            var lsbs = new[] { 0, 4, 8, 12, 0, 4 };
            for (var i = 0; i < lsbs.Length; i++)
            {
                pocs.Add(Decode(buffer, sps, Header(i, idr: i == 0, pocLsb: lsbs[i])).Picture.PicOrderCount);
            }

            pocs.Should().Equal(0, 4, 8, 12, 16, 20);
        }

        [Fact]
        public void Poc_Type_1_Should_Follow_The_Reference_Cycle()
        {
            var sps = Sps(pocType: 1, cycle: [4, 2], offsetNonRef: -1, maxRefs: 4);
            var buffer = new DecodedPictureBuffer();

            var pocs = new[]
            {
                Decode(buffer, sps, Header(0, idr: true)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(1)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(2, referenceIdc: 0)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(2)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(3)).Picture.PicOrderCount
            };

            pocs.Should().Equal(0, 4, 3, 6, 10);
        }

        [Fact]
        public void Poc_Type_2_Should_Double_The_Frame_Number()
        {
            var sps = Sps();
            var buffer = new DecodedPictureBuffer();

            var pocs = new[]
            {
                Decode(buffer, sps, Header(0, idr: true)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(1)).Picture.PicOrderCount,
                Decode(buffer, sps, Header(2, referenceIdc: 0)).Picture.PicOrderCount
            };

            pocs.Should().Equal(0, 2, 3);
        }

        [Fact]
        public void Sliding_Window_Should_Drop_The_Oldest_Short_Term_Reference()
        {
            var sps = Sps(maxRefs: 2);
            var buffer = new DecodedPictureBuffer();
            var idr = Decode(buffer, sps, Header(0, idr: true)).Picture;
            var first = Decode(buffer, sps, Header(1)).Picture;
            var second = Decode(buffer, sps, Header(2)).Picture;

            idr.IsReference.Should().BeFalse();
            first.IsReference.Should().BeTrue();
            second.IsReference.Should().BeTrue();
            buffer.ReferenceListP(second, Header(3), sps).Where(p => p is not null).Should().Equal(second, first);
        }

        [Fact]
        public void Sliding_Window_Should_Drop_The_Oldest_Reference_After_A_List_Was_Built_For_An_Earlier_Picture()
        {
            var sps = Sps(maxRefs: 2);
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));
            var one = Decode(buffer, sps, Header(1)).Picture;
            buffer.ReferenceListP(one, Header(2), sps);
            var two = Decode(buffer, sps, Header(2)).Picture;
            var three = Decode(buffer, sps, Header(3)).Picture;

            one.IsReference.Should().BeFalse();
            two.IsReference.Should().BeTrue();
            three.IsReference.Should().BeTrue();
        }

        [Fact]
        public void Mmco_1_Should_Unmark_A_Short_Term_Picture_By_PicNum()
        {
            var sps = Sps(maxRefs: 3);
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));
            var one = Decode(buffer, sps, Header(1)).Picture;
            Decode(buffer, sps, Header(2));

            Decode(buffer, sps, Header(3, operations: [new MemoryManagementOperation(1, 1, 0, 0, 0)]));

            one.IsReference.Should().BeFalse();
        }

        [Fact]
        public void Mmco_3_And_2_Should_Make_A_Picture_Long_Term_Then_Unmark_It()
        {
            var sps = Sps(maxRefs: 3);
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));
            var one = Decode(buffer, sps, Header(1)).Picture;
            var two = Decode(buffer, sps, Header(2, operations: [new MemoryManagementOperation(4, 0, 0, 0, 2), new MemoryManagementOperation(3, 0, 0, 1, 0)]));

            one.IsLongTerm.Should().BeTrue();
            one.LongTermFrameIndex.Should().Be(1);
            buffer.ReferenceListP(two.Picture, Header(3), sps).Where(p => p is not null).Last().Should().BeSameAs(one);

            Decode(buffer, sps, Header(3, operations: [new MemoryManagementOperation(2, 0, 1, 0, 0)]));

            one.IsReference.Should().BeFalse();
        }

        [Fact]
        public void Mmco_5_Should_Unmark_Everything_Reset_The_Poc_And_Output_Prior_Pictures()
        {
            var sps = Sps(maxRefs: 3, reorder: 4, dpb: 6);
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));
            var one = Decode(buffer, sps, Header(1)).Picture;

            var (current, output) = Decode(buffer, sps, Header(2, operations: [new MemoryManagementOperation(5, 0, 0, 0, 0)]));

            one.IsReference.Should().BeFalse();
            current.PicOrderCount.Should().Be(0);
            current.FrameNum.Should().Be(0);
            output.Should().HaveCount(2);
            Decode(buffer, sps, Header(1)).Picture.PicOrderCount.Should().Be(2);
        }

        [Fact]
        public void Bumping_Should_Output_In_Poc_Order_When_The_Buffer_Fills()
        {
            var sps = Sps(pocType: 0, maxRefs: 1, reorder: null, dpb: 2);
            var buffer = new DecodedPictureBuffer();
            var outputs = new List<int>();

            outputs.AddRange(Decode(buffer, sps, Header(0, idr: true, pocLsb: 0)).Output.Select(p => p.PicOrderCount));
            outputs.AddRange(Decode(buffer, sps, Header(1, pocLsb: 6)).Output.Select(p => p.PicOrderCount));
            outputs.AddRange(Decode(buffer, sps, Header(2, referenceIdc: 0, pocLsb: 2)).Output.Select(p => p.PicOrderCount));
            outputs.AddRange(Decode(buffer, sps, Header(2, referenceIdc: 0, pocLsb: 4)).Output.Select(p => p.PicOrderCount));
            outputs.AddRange(buffer.Flush().Select(p => p.PicOrderCount));

            outputs.Should().Equal(0, 2, 4, 6);
        }

        [Fact]
        public void No_Output_Of_Prior_Pictures_Should_Drop_Them()
        {
            var sps = Sps(reorder: 4, dpb: 6);
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));
            Decode(buffer, sps, Header(1));

            var (_, output) = Decode(buffer, sps, Header(0, idr: true, noOutput: true));

            output.Should().BeEmpty();
            buffer.TakeReleased().Should().HaveCount(2);
        }

        [Fact]
        public void A_Missing_Frame_Number_Should_Throw()
        {
            var sps = Sps();
            var buffer = new DecodedPictureBuffer();
            Decode(buffer, sps, Header(0, idr: true));

            FluentActions.Invoking(() => Decode(buffer, sps, Header(3))).Should().Throw<InvalidDataException>();
        }
    }
}
