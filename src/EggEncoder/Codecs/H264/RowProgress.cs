namespace EggEncoder.Codecs.H264;

internal sealed class RowProgress
{
    private const int DefaultSpinRounds = 64;

    private readonly int[] _done;
    private readonly int _spinRounds;
    private readonly object _gate = new();
    private int _waiting;
    private volatile bool _cancelled;

    public RowProgress(int rows, int spinRounds = DefaultSpinRounds)
    {
        _done = new int[rows];
        _spinRounds = spinRounds;
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

    public bool HasReached(int row, int done) => Volatile.Read(ref _done[row]) >= done;

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
        if (Volatile.Read(ref _done[row]) >= done)
        {
            return;
        }

        for (var round = 0; round < _spinRounds; round++)
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
