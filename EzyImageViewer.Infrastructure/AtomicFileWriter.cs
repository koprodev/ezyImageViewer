using System.Security.AccessControl;

namespace EzyImageViewer.Infrastructure;

/// <summary>Access control the finished file carries.</summary>
public enum AtomicFileProtection
{
    /// <summary>The file inherits its directory's ACL, as any ordinary Windows file does.</summary>
    Inherited,

    /// <summary>The file gets the explicit current-user + SYSTEM ACL that
    /// <see cref="AppDataSecurity"/> requires of everything inside the app-data tree.</summary>
    CurrentUserAndSystem,
}

/// <summary>
/// Writes a file without ever leaving a torn target (§10 저장 정책): the content lands in a sibling
/// temp file first (same directory → same volume, so the final move is a rename) and replace the
/// target only after a successful flush. On any failure the previous target content survives.
/// </summary>
public static class AtomicFileWriter
{
    private const string TempSuffix = ".tmp";
    private const int TempTokenLength = 32;

    /// <summary>Identifies a sibling temp produced by <see cref="Write(string, byte[])"/>. A write
    /// in flight holds its temp exclusively, so readers that walk a directory must be able to tell
    /// this transient entry apart from real content.</summary>
    public static bool IsTempFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.Length == 0
            || fileName[0] != '.'
            || !fileName.EndsWith(TempSuffix, StringComparison.Ordinal))
            return false;
        var body = fileName.AsSpan(0, fileName.Length - TempSuffix.Length);
        var separator = body.LastIndexOf('.');
        if (separator < 1)
            return false;
        var token = body[(separator + 1)..];
        return token.Length == TempTokenLength && Guid.TryParseExact(token, "N", out _);
    }

    public static void Write(
        string path,
        byte[] bytes,
        AtomicFileProtection protection = AtomicFileProtection.Inherited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        WriteCore(path, stream => stream.Write(bytes), protection);
    }

    public static void Write(
        string path,
        Action<Stream> writeContent,
        AtomicFileProtection protection = AtomicFileProtection.Inherited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        WriteCore(path, writeContent, protection);
    }

    private static void WriteCore(
        string path, Action<Stream> writeContent, AtomicFileProtection protection)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Cannot resolve the directory of '{path}'.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(
            directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }
            // The rename carries the temp's ACL to the target, so the target would otherwise keep
            // the ACEs the temp inherited from its directory rather than an explicit protected one.
            ApplyProtection(temp, protection);
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

    private static void ApplyProtection(string temp, AtomicFileProtection protection)
    {
        if (protection != AtomicFileProtection.CurrentUserAndSystem || !OperatingSystem.IsWindows())
            return;
        new FileInfo(temp).SetAccessControl(
            AppDataSecurity.CreateProtectedFileSecurityForCurrentUser());
    }
}
