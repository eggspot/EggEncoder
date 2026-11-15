using EggEncoder.Codecs.H264;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class RowProgressTest
    {
        [Fact]
        public async Task WaitFor_Should_Return_Once_Another_Thread_Reports_Enough()
        {
            var progress = new RowProgress(2);
            var waiter = Task.Run(() => progress.WaitFor(1, 5));

            for (var done = 1; done <= 5; done++)
            {
                Thread.Sleep(2);
                progress.Report(1, done);
            }

            await waiter.WaitAsync(TimeSpan.FromSeconds(10));
            waiter.IsCompletedSuccessfully.Should().BeTrue();
        }

        [Fact]
        public void WaitFor_Already_Reached_Should_Return_At_Once()
        {
            var progress = new RowProgress(1);
            progress.Report(0, 3);

            var act = () => progress.WaitFor(0, 3);

            act.Should().NotThrow();
        }

        [Fact]
        public async Task Cancel_Should_Release_Waiters_With_OperationCanceledException()
        {
            var progress = new RowProgress(1);
            var waiter = Task.Run(() => progress.WaitFor(0, 1));
            Thread.Sleep(20);

            progress.Cancel();

            var act = () => waiter.WaitAsync(TimeSpan.FromSeconds(10));
            await act.Should().ThrowAsync<OperationCanceledException>();
            ((Action)(() => progress.WaitFor(0, 1))).Should().Throw<OperationCanceledException>();
        }
    }
}
