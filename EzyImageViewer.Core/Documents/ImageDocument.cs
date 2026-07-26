using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents;

public enum DocumentSourceKind
{
    File,
    Clipboard,
    Capture,
    /// <summary>.ezyimg 프로젝트. Path는 내장 배경이 아닌 프로젝트 파일.</summary>
    Project,
    /// <summary>화이트보드 같은 앱 생성 문서. 저장 때 항상 경로 질문.</summary>
    Generated,
}

public sealed record DocumentSource(DocumentSourceKind Kind, string? Path)
{
    public static DocumentSource FromFile(string path) => new(DocumentSourceKind.File, path);
    public static DocumentSource FromClipboard() => new(DocumentSourceKind.Clipboard, null);
    public static DocumentSource FromProject(string path) => new(DocumentSourceKind.Project, path);
    public static DocumentSource FromGenerated() => new(DocumentSourceKind.Generated, null);
}

/// <summary>DecodedFrame 소유. record 복사는 프레임을 공유해 교체 해제 때 픽셀이 함께 죽으므로 class.</summary>
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
    // 이름 변경으로 경로만 갈아탈 수 있어야 해서 set을 연다. 픽셀·프레임은 그대로다.
    public required DocumentSource Source { get; set; }

    /// <summary>
    /// 내용은 그대로고 파일 위치만 바뀐 경우(이름 변경) 원본 참조를 옮긴다.
    /// 길이·수정시각은 유지되므로 재읽기 검증은 계속 통한다.
    /// </summary>
    public void RebindSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Source.Kind is not (DocumentSourceKind.File or DocumentSourceKind.Project))
            return;
        Source = Source with { Path = Path.GetFullPath(path) };
        FrameSource?.RebindSourcePath(path);
    }

    /// <summary>EXIF 적용 뒤 원본 크기. 주석 좌표 기준이라 전체 해상도 재디코드에도 유지.</summary>
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
    /// <summary>로드 시 파일 식별값. 재읽기 전에 화면의 파일과 여전히 같은지 확인.</summary>
    public DateTime SourceLastWriteUtc { get; init; }
    /// <summary>픽셀 예산 때문에 축소 디코드했으면 true.</summary>
    public bool IsReducedPreview
    {
        get
        {
            lock (_frameSync)
                return _isReducedPreview;
        }
        init => _isReducedPreview = value;
    }
    /// <summary>시그니처 우선 분기 뒤 선택된 렌더러.</summary>
    public DocumentRendererInfo Renderer { get; init; } = DocumentRendererInfo.Unknown;
    /// <summary>확장자·시그니처 불일치 같은 비치명 진단.</summary>
    public IReadOnlyList<DocumentDiagnostic> DiagnosticEntries { get; init; } = [];
    /// <summary>현재 상태바·스모크 출력용 호환 투영값.</summary>
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

    /// <summary>현재 픽셀에서 애니메이션을 멈추고 인코딩 시퀀스 원본 해제.</summary>
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
