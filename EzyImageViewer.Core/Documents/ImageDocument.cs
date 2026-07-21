using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents;

public enum DocumentSourceKind
{
    File,
    Clipboard,
    Capture,
    /// <summary>An .ezyimg project; Path is the project file, not the embedded background.</summary>
    Project,
}

public sealed record DocumentSource(DocumentSourceKind Kind, string? Path)
{
    public static DocumentSource FromFile(string path) => new(DocumentSourceKind.File, path);
    public static DocumentSource FromClipboard() => new(DocumentSourceKind.Clipboard, null);
    public static DocumentSource FromProject(string path) => new(DocumentSourceKind.Project, path);
}

/// <summary>
/// Owns its <see cref="DecodedFrame"/>; disposing the document disposes the pixels.
/// Deliberately a class, not a record: a <c>with</c> copy would share the frame reference, and the
/// session disposes the document it replaces — so the copy's pixels would die under it (ADR-0008).
/// </summary>
public sealed class ImageDocument : IDisposable
{
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _frameSwitch = new(1, 1);
    private DecodedFrame? _frame;
    private PixelSize _nativeSize;
    private bool _isReducedPreview;
    private bool _disposed;
    private bool _sequenceFlattened;

    public required DecodedFrame Frame
    {
        get
        {
            lock (_frameSync)
                return _frame ?? throw new InvalidOperationException("The document frame has not been initialized.");
        }
        init => _frame = value ?? throw new ArgumentNullException(nameof(value));
    }
    public required DocumentSource Source { get; init; }

    /// <summary>
    /// The source's own post-EXIF size. Equals the frame size unless <see cref="IsReducedPreview"/>;
    /// annotation geometry is expressed in this space so it survives a re-decode at full resolution.
    /// </summary>
    public required PixelSize NativeSize
    {
        get
        {
            lock (_frameSync)
                return _nativeSize;
        }
        init => _nativeSize = value;
    }

    public ImageFormat Format { get; init; }
    public long SourceFileBytes { get; init; }
    /// <summary>Load-time identity of a file source (with <see cref="SourceFileBytes"/>): a re-read
    /// for export or project embedding must verify the file is still the one on screen (§10).</summary>
    public DateTime SourceLastWriteUtc { get; init; }
    /// <summary>True when the frame was decoded at reduced size under the pixel budget (NFR-PERF-004/008).</summary>
    public bool IsReducedPreview
    {
        get
        {
            lock (_frameSync)
                return _isReducedPreview;
        }
        init => _isReducedPreview = value;
    }
    /// <summary>The renderer selected after signature-first dispatch (§8.5).</summary>
    public DocumentRendererInfo Renderer { get; init; } = DocumentRendererInfo.Unknown;
    /// <summary>Non-fatal findings, e.g. extension/signature mismatch (§8.5).</summary>
    public IReadOnlyList<DocumentDiagnostic> DiagnosticEntries { get; init; } = [];
    /// <summary>Compatibility projection used by the current status bar and smoke output.</summary>
    public IReadOnlyList<string> Diagnostics => DiagnosticEntries.Select(entry => entry.Message).ToArray();

    public IDocumentFrameSource? FrameSource { get; init; }
    public int FrameCount => _sequenceFlattened ? 1 : FrameSource?.FrameCount ?? 1;
    public DocumentSequenceKind SequenceKind => _sequenceFlattened
        ? DocumentSequenceKind.SingleFrame
        : FrameSource?.Kind ?? DocumentSequenceKind.SingleFrame;
    public IReadOnlyList<DocumentFrameInfo> Frames => _sequenceFlattened
        ? [DocumentFrameInfo.Still]
        : FrameSource?.Frames ?? [DocumentFrameInfo.Still];
    public bool SupportsScaleDependentRendering => !_sequenceFlattened
        && FrameSource?.IsScaleDependent == true;
    public int CurrentFrameIndex { get; private set; }
    public long SurfaceRevision { get; private set; }
    public bool WasAnimationFlattened => _sequenceFlattened;

    public Guid Id { get; } = Guid.NewGuid();

    public async Task<bool> LoadFrameAsync(
        int frameIndex,
        DecodeRequest request,
        bool forceRerender,
        CancellationToken cancellationToken)
    {
        if (_sequenceFlattened)
            throw new InvalidOperationException("The sequence was flattened to its current frame.");
        var source = FrameSource
            ?? throw new InvalidOperationException("This document does not have a lazy frame source.");
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, source.FrameCount);
        if (!forceRerender && frameIndex == CurrentFrameIndex)
            return false;

        await _frameSwitch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_frameSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!forceRerender && frameIndex == CurrentFrameIndex)
                    return false;
            }

            var result = await source.DecodeFrameAsync(frameIndex, request, cancellationToken).ConfigureAwait(false);
            DecodedFrame? previous = null;
            lock (_frameSync)
            {
                if (_disposed)
                {
                    result.Frame.Dispose();
                    throw new ObjectDisposedException(nameof(ImageDocument));
                }

                previous = _frame;
                _frame = result.Frame;
                _nativeSize = result.NativeSize;
                _isReducedPreview = result.IsReduced;
                CurrentFrameIndex = frameIndex;
                SurfaceRevision = checked(SurfaceRevision + 1);
            }
            previous?.Dispose();
            return true;
        }
        finally
        {
            _frameSwitch.Release();
        }
    }

    /// <summary>Stops an animation at its current pixels and releases the encoded sequence source.</summary>
    public async Task<bool> FlattenAnimationToCurrentFrameAsync(CancellationToken cancellationToken)
    {
        await _frameSwitch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_frameSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sequenceFlattened || FrameSource?.Kind != DocumentSequenceKind.Animation)
                    return false;
                _sequenceFlattened = true;
                CurrentFrameIndex = 0;
            }
            FrameSource.Dispose();
            return true;
        }
        finally
        {
            _frameSwitch.Release();
        }
    }

    public void Dispose()
    {
        DecodedFrame? frame;
        lock (_frameSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            frame = _frame;
        }
        frame?.Dispose();
        FrameSource?.Dispose();
    }
}
