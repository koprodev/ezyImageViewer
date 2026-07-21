namespace EzyImageViewer.Imaging.Codecs.Isolation;

internal static class BoundedPipeReader
{
    private const int InitialCapacity = 64 * 1024;

    public static async Task<IsolatedCodecPipeCapture> ReadAsync(
        Stream source,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        var buffer = new byte[Math.Min(maximumBytes, InitialCapacity)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (length == maximumBytes)
                {
                    var overflowProbe = new byte[1];
                    if (await source.ReadAsync(overflowProbe, CancellationToken.None)
                            .ConfigureAwait(false) == 0)
                    {
                        return new IsolatedCodecPipeCapture(buffer, length);
                    }
                    throw CreateLimitException(maximumBytes);
                }

                var doubledCapacity = (long)buffer.Length * 2;
                var nextCapacity = (int)Math.Min(maximumBytes, doubledCapacity);
                Array.Resize(ref buffer, nextCapacity);
            }

            var read = await source.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return length == 0
                    ? IsolatedCodecPipeCapture.Empty
                    : new IsolatedCodecPipeCapture(buffer, length);
            }
            length += read;
        }
    }

    internal static long CalculateMaximumAllocationDuringGrowth(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        var capacity = Math.Min(maximumBytes, InitialCapacity);
        long peakBytes = capacity;
        while (capacity < maximumBytes)
        {
            var doubledCapacity = (long)capacity * 2;
            var nextCapacity = (int)Math.Min(maximumBytes, doubledCapacity);
            peakBytes = Math.Max(peakBytes, (long)capacity + nextCapacity);
            capacity = nextCapacity;
        }
        return Math.Max(peakBytes, (long)capacity + 1);
    }

    private static InvalidDataException CreateLimitException(int maximumBytes) =>
        new($"The isolated codec exceeded its {maximumBytes:N0}-byte pipe limit.");
}
