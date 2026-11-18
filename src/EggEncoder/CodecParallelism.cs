namespace EggEncoder
{
    internal static class CodecParallelism
    {
        private static readonly AsyncLocal<int?> _limit = new();

        internal static int? Current => _limit.Value;

        internal static Scope Limit(int? maxDegreeOfParallelism)
        {
            var previous = _limit.Value;
            if (maxDegreeOfParallelism is not null)
            {
                _limit.Value = maxDegreeOfParallelism;
            }

            return new Scope(previous);
        }

        internal readonly struct Scope(int? previous) : IDisposable
        {
            public void Dispose() => _limit.Value = previous;
        }
    }
}
