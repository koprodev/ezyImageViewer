namespace EzyImageViewer.Imaging.Sources;

internal interface IEncodedSource : IDisposable
{
    Stream OpenRead();
}

internal sealed class MemoryEncodedSource(byte[] bytes) : IEncodedSource
{
    private byte[]? _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public Stream OpenRead() => new MemoryStream(
        _bytes ?? throw new ObjectDisposedException(nameof(MemoryEncodedSource)),
        writable: false);

    public void Dispose() => _bytes = null;
}

internal sealed class FileEncodedSource(
    string path,
    long expectedLength,
    DateTime expectedLastWriteUtc) : IEncodedSource
{
    private bool _disposed;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedLength || info.LastWriteTimeUtc != expectedLastWriteUtc)
            throw new IOException("The source file changed after the document was opened.");
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Dispose() => _disposed = true;
}
