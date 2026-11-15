namespace EggEncoder.Codecs.H264;

internal sealed class RowProgress
{
    private const int DefaultSpinRounds = 64;
    private const int NobodyWaiting = int.MaxValue;

    private readonly int[] _done;
    private readonly int[] _lowestAwaited;
    private readonly object[] _gates;
    private readonly int _spinRounds;
    private volatile bool _cancelled;

    public RowProgress(int rows, int spinRounds = DefaultSpinRounds)
    {
        _done = new int[rows];
        _lowestAwaited = new int[rows];
        Array.Fill(_lowestAwaited, NobodyWaiting);
        _gates = new object[rows];
        for (var row = 0; row < rows; row++)
        {
            _gates[row] = new object();
        }

        _spinRounds = spinRounds;
    }

    public void Report(int row, int done)
    {
        Interlocked.Exchange(ref _done[row], done);
        if (done >= Volatile.Read(ref _lowestAwaited[row]))
        {
            var gate = _gates[row];
            lock (gate)
            {
                _lowestAwaited[row] = NobodyWaiting;
                Monitor.PulseAll(gate);
            }
        }
    }

    public bool HasReached(int row, int done) => Volatile.Read(ref _done[row]) >= done;

    public void Cancel()
    {
        _cancelled = true;
        foreach (var gate in _gates)
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
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

        var gate = _gates[row];
        lock (gate)
        {
            while (true)
            {
                ThrowIfCancelled();
                if (done < _lowestAwaited[row])
                {
                    Volatile.Write(ref _lowestAwaited[row], done);
                }

                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _done[row]) >= done)
                {
                    return;
                }

                Monitor.Wait(gate);
            }
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
