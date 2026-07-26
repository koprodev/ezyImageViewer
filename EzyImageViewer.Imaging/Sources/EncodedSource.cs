namespace EzyImageViewer.Imaging.Sources;

internal interface IEncodedSource : IDisposable
{
    Stream OpenRead();

    /// <summary>같은 내용이 다른 경로로 옮겨졌을 때 읽기 대상을 바꾼다(이름 변경).</summary>
    void RebindPath(string path)
    {
    }
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
    // 이름 변경으로 경로만 갈아탈 수 있어야 해서 필드로 둔다. 길이·시각은 그대로라 검증은 유지.
    private string _path = path;
    private bool _disposed;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length != expectedLength || info.LastWriteTimeUtc != expectedLastWriteUtc)
            throw new IOException("The source file changed after the document was opened.");
        return new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void RebindPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public void Dispose() => _disposed = true;
}
