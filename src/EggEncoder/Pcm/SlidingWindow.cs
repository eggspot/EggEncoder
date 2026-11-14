namespace EggEncoder.Pcm;

internal static class SlidingWindow
{
    public static void Min(float[] source, int length, int before, int after, float[] destination) =>
        Extreme(source, length, before, after, destination, keepsLargest: false);

    public static void Max(float[] source, int length, int before, int after, float[] destination) =>
        Extreme(source, length, before, after, destination, keepsLargest: true);

    private static void Extreme(float[] source, int length, int before, int after, float[] destination, bool keepsLargest)
    {
        var deque = new int[length];
        var head = 0;
        var tail = 0;
        var next = 0;

        for (var i = 0; i < length; i++)
        {
            var last = (int)Math.Min(length - 1L, (long)i + after);
            for (; next <= last; next++)
            {
                while (tail > head && (keepsLargest ? source[deque[tail - 1]] <= source[next] : source[deque[tail - 1]] >= source[next]))
                {
                    tail--;
                }

                deque[tail++] = next;
            }

            while (deque[head] < i - before)
            {
                head++;
            }

            destination[i] = source[deque[head]];
        }
    }
}
