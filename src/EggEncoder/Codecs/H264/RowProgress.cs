namespace EggEncoder.Codecs.H264;

internal sealed class RowProgress
{
    private const int SpinRounds = 64;

    private readonly int[] _done;
    private readonly object _gate = new();
    private int _waiting;
    private volatile bool _cancelled;

    public RowProgress(int rows)
    {
        _done = new int[rows];
    }

    public void Report(int row, int done)
    {
        Interlocked.Exchange(ref _done[row], done);
        if (Volatile.Read(ref _waiting) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    public void WaitFor(int row, int done)
    {
        for (var round = 0; round < SpinRounds; round++)
        {
            if (Volatile.Read(ref _done[row]) >= done)
            {
                return;
            }

            ThrowIfCancelled();
            Thread.SpinWait(32);
        }

        Interlocked.Increment(ref _waiting);
        try
        {
            lock (_gate)
            {
                while (Volatile.Read(ref _done[row]) < done)
                {
                    ThrowIfCancelled();
                    Monitor.Wait(_gate);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private void ThrowIfCancelled()
    {
        if (_cancelled)
        {
            throw new OperationCanceledException();
        }
    }
}
