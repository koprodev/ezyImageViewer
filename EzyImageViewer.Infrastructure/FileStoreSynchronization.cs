using System.Collections.Concurrent;

namespace EzyImageViewer.Infrastructure;

internal static class FileStoreSynchronization
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static object ForPath(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());
}
