namespace EzyImageViewer.Infrastructure;

/// <summary>
/// Writes a file without ever leaving a torn target (§10 저장 정책): the content lands in a sibling
/// temp file first (same directory → same volume, so the final move is a rename) and replace the
/// target only after a successful flush. On any failure the previous target content survives.
/// </summary>
public static class AtomicFileWriter
{
    public static void Write(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        WriteCore(path, stream => stream.Write(bytes));
    }

    public static void Write(string path, Action<Stream> writeContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        WriteCore(path, writeContent);
    }

    private static void WriteCore(string path, Action<Stream> writeContent)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Cannot resolve the directory of '{path}'.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The stray temp file is cosmetic; the original error is what matters.
            }
            throw;
        }
    }
}
