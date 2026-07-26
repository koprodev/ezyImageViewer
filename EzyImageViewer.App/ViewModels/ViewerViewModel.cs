using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Core.Navigation;
using EzyImageViewer.Imaging;

namespace EzyImageViewer.App.ViewModels;

/// <summary>
/// 창별 문서 세션·편집 기록·폴더 탐색·상태 문구.
/// 세션 이벤트는 작업자에서 오며 편집기·로드 게이트는 UI 스레드 전용.
/// 모든 문서 교체는 <see cref="RequestLoad"/>를 지나 저장 안 한 편집 보호를 우회하지 못함.
/// </summary>
public sealed class ViewerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DocumentLoader _documentLoader;
    private readonly FolderNavigator _navigator = new(AppServices.ViewableExtensions);
    private readonly DocumentLoadGate _gate;
    private readonly SemaphoreSlim _navigatorGate = new(1, 1);
    private readonly CancellationTokenSource _navigationLifetime = new();
    private long _navigatorGeneration;
    private bool _navigatorScanning;
    private bool _includeSubfoldersRequested;
    private bool _disposed;
    private Guid _editorDocumentId;
    private readonly Dictionary<int, DocumentEditor.Snapshot> _pageSnapshots = [];
    private readonly LinkedList<int> _pageHistoryLru = [];
    private readonly Dictionary<int, Guid?> _pageActiveLayerIds = [];
    private bool _pageSwitchPending;

    /// <summary>로더가 만든 정확한 문서 인스턴스별 프로젝트 메타데이터. 늦은 로더의 덮어쓰기 방지.</summary>
    private readonly ConditionalWeakTable<ImageDocument, ProjectOpenData> _pendingProjects = new();
    private readonly ConditionalWeakTable<ImageDocument, object> _pendingRecoveries = new();

    /// <summary>현재 문서의 프로젝트 원본. 빠른 재저장·전체 해상도 재디코드에 사용.</summary>
    public ProjectOpenData? OpenedProject { get; private set; }

    /// <summary>열린 프로젝트의 활성 레이어 힌트. 창이 한 번 소비.</summary>
    public Guid? PendingActiveLayerId { get; private set; }

    public Guid? TakePendingActiveLayerId()
    {
        var value = PendingActiveLayerId;
        PendingActiveLayerId = null;
        return value;
    }

    public ViewerViewModel(
        Func<Task<DiscardDecision>> confirmDiscardAsync,
        DocumentLoader? documentLoader = null)
    {
        ArgumentNullException.ThrowIfNull(confirmDiscardAsync);
        _documentLoader = documentLoader ?? AppServices.Loader;
        _gate = new DocumentLoadGate(Session, Editor, confirmDiscardAsync);
        Editor.Changed += EnforcePageHistoryBudget;
    }

    public DocumentSession Session { get; } = new();

    public DocumentEditor Editor { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LoadStarted;

    public string PositionText { get; private set; } = "";
    public string PageText { get; private set; } = "";
    public string DimensionsText { get; private set; } = "";
    public string FormatText { get; private set; } = "";
    public string ColorModeText { get; private set; } = "";
    public string FileSizeText { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public string DiagnosticsText { get; private set; } = "";
    public string ModifiedText { get; private set; } = "";

    public bool IsModified => Editor.IsModified;
    public bool CanOpenPrevious => !_navigatorScanning && _navigator.CanMovePrevious;
    public bool CanOpenNext => !_navigatorScanning && _navigator.CanMoveNext;
    public bool CanOpenPreviousPage => Session.Current is
        { SequenceKind: DocumentSequenceKind.Pages, CurrentFrameIndex: > 0 };
    public bool CanOpenNextPage => Session.Current is { SequenceKind: DocumentSequenceKind.Pages } document
        && document.CurrentFrameIndex < document.FrameCount - 1;
    /// <summary>다중 페이지·애니메이션 위치 표시 여부. 단일 프레임이면 숨김.</summary>
    public bool HasMultipleFrames => Session.Current is { FrameCount: > 1 };
    /// <summary>이동할 다른 파일이 있을 때만 썸네일 스트립 표시.</summary>
    public bool CanBrowseFiles => !_navigatorScanning && _navigator.Count > 1;
    /// <summary>썸네일 스트립 탐색 순서. 스캔 중에는 비어 있음.</summary>
    public IReadOnlyList<string> NavigationFiles => _navigatorScanning ? [] : _navigator.Files;
    public int NavigationIndex => _navigatorScanning ? -1 : _navigator.CurrentIndex;
    public bool IsBusy => Session.State == SessionState.Loading;

    /// <summary>원본 교체·페이지 편집 트랜잭션 중 편집 차단.</summary>
    public bool IsMutationBlocked => _gate.IsReplacementPending || _pageSwitchPending;

    /// <summary>현재 창 변경 경로가 공유하는 호환성 입장 게이트.</summary>
    public bool IsReplacementPending => IsMutationBlocked;

    public void SetIncludeSubfolders(bool includeSubfolders)
    {
        if (_includeSubfoldersRequested == includeSubfolders)
            return;
        _includeSubfoldersRequested = includeSubfolders;
        QueueNavigatorUpdate(() => _navigator.SetIncludeSubfolders(includeSubfolders));
    }

    public void OpenFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        if (ProjectStore.IsProjectPath(paths[0]))
        {
            // 프로젝트는 폴더 탐색 밖이므로 기존 기준점 유지.
            OpenProject(paths[0]);
            return;
        }
        var path = paths[0];
        var includeSubfolders = _includeSubfoldersRequested;
        QueueNavigatorUpdate(() =>
        {
            _navigator.SetIncludeSubfolders(includeSubfolders);
            _navigator.AnchorTo(path);
        });
        Load(paths[0]);
    }

    /// <summary>.ezyimg의 내장 원본을 배경으로 열고 저장 상태·활성 레이어 복원.</summary>
    public void OpenProject(string path) => RequestLoad(async ct =>
    {
        var data = await Task.Run(() => ProjectStore.Read(path), ct).ConfigureAwait(true);
        var document = await _documentLoader.LoadMemoryAsync(
            data.SourceBytes, DocumentSource.FromProject(path), ct).ConfigureAwait(true);
        try
        {
            if (data.Document.Pages.Count > 1 && data.Document.Pages.Count != document.FrameCount)
            {
                throw new InvalidDataException(
                    $"Project has {data.Document.Pages.Count} page states but its source has {document.FrameCount} frames.");
            }
            if (data.Document.ActivePageIndex >= document.FrameCount)
                throw new InvalidDataException("Project active page is outside the embedded source.");
            if (data.Document.ActivePageIndex > 0)
            {
                await document.LoadFrameAsync(
                    data.Document.ActivePageIndex,
                    new DecodeRequest(AppServices.Limits),
                    forceRerender: false,
                    ct).ConfigureAwait(true);
            }
            _pendingProjects.AddOrUpdate(document, data);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    });

    public void OpenRecovery(byte[] projectBytes) => RequestLoad(async ct =>
    {
        ArgumentNullException.ThrowIfNull(projectBytes);
        var data = await Task.Run(() => ProjectStore.Read(projectBytes), ct).ConfigureAwait(true);
        var document = await _documentLoader.LoadMemoryAsync(
            data.SourceBytes,
            new DocumentSource(DocumentSourceKind.Project, null),
            ct).ConfigureAwait(true);
        try
        {
            if (data.Document.Pages.Count > 1 && data.Document.Pages.Count != document.FrameCount)
                throw new InvalidDataException("Recovered page state count does not match its source.");
            if (data.Document.ActivePageIndex >= document.FrameCount)
                throw new InvalidDataException("Recovered active page is outside its source.");
            if (data.Document.ActivePageIndex > 0)
            {
                await document.LoadFrameAsync(
                    data.Document.ActivePageIndex,
                    new DecodeRequest(AppServices.Limits),
                    forceRerender: false,
                    ct).ConfigureAwait(true);
            }
            _pendingProjects.AddOrUpdate(document, data);
            _pendingRecoveries.Add(document, new object());
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    });

    /// <summary>디스크에 실재하는 현재 파일 경로. 클립보드·생성 문서는 null.</summary>
    public string? CurrentFilePath =>
        Session.Current?.Source is { Kind: DocumentSourceKind.File, Path: { } path } ? path : null;

    /// <summary>
    /// 이름이 바뀐 파일로 현재 문서를 다시 묶는다. 재로드가 아니라 제자리 갱신이라
    /// 저장하지 않은 편집이 그대로 남는다. 탐색 목록 기준점도 새 이름으로 옮긴다.
    /// </summary>
    public void RebindRenamedFile(string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        if (!Session.RebindCurrentSourcePath(newPath))
            return;
        var includeSubfolders = _includeSubfoldersRequested;
        QueueNavigatorUpdate(() =>
        {
            _navigator.SetIncludeSubfolders(includeSubfolders);
            _navigator.AnchorTo(newPath);
        });
    }

    /// <summary>원본이 사라져 보여 줄 게 없을 때 빈 화면으로 돌아간다.</summary>
    public void CloseDocument(string? rescanAnchor = null)
    {
        if (!Session.CloseCurrent())
            return;
        Editor.Reset(null);
        if (rescanAnchor is null)
            return;
        var includeSubfolders = _includeSubfoldersRequested;
        QueueNavigatorUpdate(() =>
        {
            _navigator.SetIncludeSubfolders(includeSubfolders);
            _navigator.AnchorTo(rescanAnchor);
        });
    }

    public void OpenNext()
    {
        if (_navigatorScanning)
            return;
        if (_navigator.MoveNext() is { } path)
            Load(path);
    }

    public void OpenPrevious()
    {
        if (_navigatorScanning)
            return;
        if (_navigator.MovePrevious() is { } path)
            Load(path);
    }

    /// <summary>썸네일 색인으로 파일 열기. 탐색기 변경이 끝난 뒤 UI 스레드에서만 읽음.</summary>
    public void OpenAt(int index)
    {
        if (_navigatorScanning)
            return;
        if (_navigator.MoveTo(index) is { } path)
            Load(path);
    }

    public void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string sourceFormat)
    {
        _ = sourceFormat; // 실제 형식은 바이트에서 다시 판별.
        RequestLoad(ct => _documentLoader.LoadMemoryAsync(bytes, DocumentSource.FromClipboard(), ct));
    }

    /// <summary>앱 생성 픽셀도 같은 메모리 로드 경계로 전달.</summary>
    public void OpenGeneratedBytes(ReadOnlyMemory<byte> bytes) =>
        RequestLoad(ct => _documentLoader.LoadMemoryAsync(bytes, DocumentSource.FromGenerated(), ct));

    private void Load(string path)
    {
        if (ProjectStore.IsProjectPath(path))
        {
            OpenProject(path);
            return;
        }
        RequestLoad(ct => _documentLoader.LoadFileAsync(path, ct));
    }

    /// <summary>활성화 라우터를 막지 않는 비대기 로드. 예외는 세션 상태로 노출.</summary>
    private void RequestLoad(Func<CancellationToken, Task<ImageDocument>> loader)
    {
        LoadStarted?.Invoke();
        _ = _gate.RequestLoadAsync(loader);
    }

    public bool CanCloseWithoutPrompt() => _gate.CanCloseWithoutPrompt();

    /// <summary>새 원본 문서가 왔을 때만 편집기 재결합. UI 스레드 전용.</summary>
    public void SyncEditor()
    {
        var document = Session.Current;
        var id = document?.Id ?? Guid.Empty;
        if (id == _editorDocumentId)
            return;
        _editorDocumentId = id;
        _pageSnapshots.Clear();
        _pageHistoryLru.Clear();
        _pageActiveLayerIds.Clear();
        OpenedProject = null;
        PendingActiveLayerId = null;
        if (document is not null && _pendingProjects.TryGetValue(document, out var pending))
        {
            var recovered = _pendingRecoveries.Remove(document);
            _pendingProjects.Remove(document);
            // 프로젝트 열기는 저장 상태로 깨끗하게 시작. 기록·수정 표식 없음.
            Editor.Reset(document, pending.State);
            for (var pageIndex = 0; pageIndex < pending.Document.Pages.Count; pageIndex++)
            {
                var page = pending.Document.Pages[pageIndex];
                _pageActiveLayerIds[pageIndex] = page.ActiveLayerId;
                if (pageIndex != pending.Document.ActivePageIndex)
                    _pageSnapshots[pageIndex] = Editor.CreateCleanSnapshot(page.State);
            }
            OpenedProject = pending;
            PendingActiveLayerId = pending.ActiveLayerId;
            if (recovered)
                Editor.MarkRecoveryPendingSave();
            return;
        }
        Editor.Reset(document);
    }

    public async Task<bool> OpenPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        var document = Session.Current;
        if (document is null || document.SequenceKind != DocumentSequenceKind.Pages || _pageSwitchPending)
            return false;
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.FrameCount);
        if (pageIndex == document.CurrentFrameIndex)
            return false;

        _pageSwitchPending = true;
        try
        {
            var previousIndex = document.CurrentFrameIndex;
            var previousSnapshot = Editor.CaptureSnapshot();
            var changed = await LoadFrameForCurrentDocumentAsync(
                document,
                pageIndex,
                new DecodeRequest(AppServices.Limits),
                forceRerender: false,
                cancellationToken);
            if (!changed || !ReferenceEquals(Editor.Document, document))
                return false;

            // 대기 뒤 상태를 다시 잡아 미래의 비 UI 호출자도 묵은 스냅샷을 확정하지 못하게 함.
            previousSnapshot = Editor.CaptureSnapshot();
            StoreInactivePage(previousIndex, previousSnapshot);
            if (TakeInactivePage(pageIndex) is { } target)
                Editor.RestoreSnapshot(document, target);
            else
                Editor.Reset(document);
            EnforcePageHistoryBudget();
            UpdateInactivePageDirtyState(pageIndex);
            RefreshStatus();
            return true;
        }
        finally
        {
            _pageSwitchPending = false;
        }
    }

    public async Task<bool> AdvanceAnimationAsync(CancellationToken cancellationToken)
    {
        var document = Session.Current;
        if (document is null || document.SequenceKind != DocumentSequenceKind.Animation)
            return false;
        var next = (document.CurrentFrameIndex + 1) % document.FrameCount;
        var changed = await LoadFrameForCurrentDocumentAsync(
            document,
            next,
            new DecodeRequest(AppServices.Limits),
            forceRerender: false,
            cancellationToken);
        if (changed)
            RefreshStatus();
        return changed;
    }

    public async Task<bool> RerenderScaleDependentAsync(
        int preferredMaxDimension,
        CancellationToken cancellationToken)
    {
        var document = Session.Current;
        if (document is null || !document.SupportsScaleDependentRendering || _pageSwitchPending)
            return false;
        var changed = await LoadFrameForCurrentDocumentAsync(
            document,
            document.CurrentFrameIndex,
            new DecodeRequest(AppServices.Limits, preferredMaxDimension),
            forceRerender: true,
            cancellationToken);
        if (changed)
            RefreshStatus();
        return changed;
    }

    private async Task<bool> LoadFrameForCurrentDocumentAsync(
        ImageDocument document,
        int frameIndex,
        DecodeRequest request,
        bool forceRerender,
        CancellationToken cancellationToken)
    {
        try
        {
            var changed = await document.LoadFrameAsync(
                frameIndex, request, forceRerender, cancellationToken);
            return changed && ReferenceEquals(document, Session.Current);
        }
        catch (ObjectDisposedException) when (!ReferenceEquals(document, Session.Current))
        {
            // 최신 교체가 이전 문서를 해제하므로 묵은 프레임의 해제 예외는 예상 취소.
            return false;
        }
    }

    public IReadOnlyList<DocumentState> CapturePageStates()
    {
        if (Session.Current is not { } document)
            return [];
        if (document.SequenceKind != DocumentSequenceKind.Pages)
            return [Editor.State];

        var activeIndex = document.CurrentFrameIndex;
        var activeState = Editor.State;
        var states = new DocumentState[document.FrameCount];
        for (var index = 0; index < states.Length; index++)
            states[index] = index == activeIndex
                ? activeState
                : _pageSnapshots.TryGetValue(index, out var snapshot)
                ? snapshot.State
                : DocumentState.Empty;
        return states;
    }

    public Guid? GetPageActiveLayerId(int pageIndex) =>
        _pageActiveLayerIds.GetValueOrDefault(pageIndex);

    public void SetPageActiveLayerId(int pageIndex, Guid? activeLayerId) =>
        _pageActiveLayerIds[pageIndex] = activeLayerId;

    public IReadOnlyList<ProjectPageState> CaptureProjectPages(Guid? currentActiveLayerId)
    {
        var document = Session.Current
            ?? throw new InvalidOperationException("No document is loaded.");
        var statePageIndex = document.SequenceKind == DocumentSequenceKind.Pages
            ? document.CurrentFrameIndex
            : 0;
        _pageActiveLayerIds[statePageIndex] = currentActiveLayerId;
        var states = CapturePageStates();
        return states.Select((state, pageIndex) =>
            new ProjectPageState(state, _pageActiveLayerIds.GetValueOrDefault(pageIndex))).ToArray();
    }

    public bool MarkAllPagesSaved(long expectedCurrentStateId)
    {
        if (Session.Current is null)
            return false;
        if (Editor.CurrentStateId != expectedCurrentStateId)
            return false;
        foreach (var index in _pageSnapshots.Keys.ToArray())
            _pageSnapshots[index] = _pageSnapshots[index].AsSaved();
        Editor.MarkSaved();
        Editor.SetInactiveScopesModified(false);
        return true;
    }

    private void UpdateInactivePageDirtyState(int activeIndex)
    {
        Editor.SetInactiveScopesModified(_pageSnapshots.Any(pair =>
            pair.Key != activeIndex && pair.Value.IsModified));
    }

    private void StoreInactivePage(int pageIndex, DocumentEditor.Snapshot snapshot)
    {
        _pageSnapshots[pageIndex] = snapshot;
        _pageHistoryLru.Remove(pageIndex);
        if (snapshot.RetainedBytes > 0)
            _pageHistoryLru.AddLast(pageIndex);
    }

    private DocumentEditor.Snapshot? TakeInactivePage(int pageIndex)
    {
        if (!_pageSnapshots.Remove(pageIndex, out var snapshot))
            return null;
        _pageHistoryLru.Remove(pageIndex);
        return snapshot;
    }

    private void EnforcePageHistoryBudget()
    {
        var retained = Editor.RetainedBytes + _pageSnapshots.Values.Sum(snapshot => snapshot.RetainedBytes);
        while (retained > Editor.MaxRetainedBytes && _pageHistoryLru.First is { } oldest)
        {
            _pageHistoryLru.RemoveFirst();
            var pageIndex = oldest.Value;
            if (!_pageSnapshots.TryGetValue(pageIndex, out var snapshot)
                || snapshot.RetainedBytes == 0)
            {
                continue;
            }

            retained -= snapshot.RetainedBytes;
            _pageSnapshots[pageIndex] = snapshot.WithoutHistory();
        }
    }

    /// <summary>상태 문구 재계산. UI 스레드 전용.</summary>
    public void RefreshStatus()
    {
        var document = Session.Current;
        PositionText = !_navigatorScanning && _navigator.Count > 0
            ? $"{AppStrings.StatusFile} {_navigator.CurrentIndex + 1} / {_navigator.Count}" : "";
        PageText = document is null ? "" :
            $"{AppStrings.StatusPage} {document.CurrentFrameIndex + 1} / {document.FrameCount}";
        DimensionsText = FormatDimensions();
        FormatText = document?.Format.ToString().ToUpperInvariant() ?? "";
        ColorModeText = document is null ? "" : document.Frame.HasAlpha
            ? AppStrings.ColorModeRgba8 : AppStrings.ColorModeRgb8;
        FileSizeText = document is { SourceFileBytes: > 0 } ? FormatBytes(document.SourceFileBytes) : "";
        StateText = Session.State switch
        {
            SessionState.Loading => AppStrings.StateLoading,
            SessionState.Failed => $"{AppStrings.StateFailed}: {Session.LastError?.Message}",
            // 이전 문서는 살았어도 오류는 사용자에게 보여 줌.
            SessionState.Ready when Session.LastError is { } error =>
                $"{AppStrings.StateReady} · {AppStrings.StateFailed}: {error.Message}",
            SessionState.Ready => AppStrings.StateReady,
            _ => "",
        };
        DiagnosticsText = document is { Diagnostics.Count: > 0 } ? string.Join(" · ", document.Diagnostics) : "";
        ModifiedText = Editor.IsModified ? AppStrings.StateModified : "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>설치 빌드를 구분하는 제목 버전. 파일 버전과 짧은 commit 조합.</summary>
    private static readonly string ProductTitle = BuildProductTitle();

    private static string BuildProductTitle()
    {
        const string product = "ezy Image Viewer";
        var assembly = typeof(ViewerViewModel).Assembly;
        var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = informational?.IndexOf('+') ?? -1;
        var commit = plus >= 0 && informational!.Length > plus + 1
            ? informational.Substring(plus + 1, Math.Min(7, informational.Length - plus - 1))
            : null;
        if (string.IsNullOrWhiteSpace(file))
            return product;
        return commit is null ? $"{product} {file}" : $"{product} {file} ({commit})";
    }

    /// <summary>수정 표식을 포함한 창 제목.</summary>
    public string BuildTitle()
    {
        var name = Session.Current?.Source.Path is { } path ? Path.GetFileName(path) : null;
        var marker = Editor.IsModified ? "● " : "";
        return name is null ? $"{marker}{ProductTitle}" : $"{marker}{name} - {ProductTitle}";
    }

    /// <summary>상태바에 디코드 프레임이 아닌 편집 문서의 변환 출력 크기 표시.</summary>
    private string FormatDimensions()
    {
        if (Editor.Document is not { } document)
            return "";
        var output = TransformEvaluator.Evaluate(Editor.State.Transform, document.NativeSize).OutputSize;
        return $"{output.Width} × {output.Height}"
            + (document.IsReducedPreview ? $" ({AppStrings.StatusPreview})" : "");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#}MB",
        >= 1024 => $"{bytes / 1024.0:0.#}KB",
        _ => $"{bytes}B",
    };

    private void QueueNavigatorUpdate(Action update)
    {
        var generation = ++_navigatorGeneration;
        _navigatorScanning = true;
        RefreshStatus();
        _ = RunNavigatorUpdateAsync(generation, update);
    }

    private async Task RunNavigatorUpdateAsync(long generation, Action update)
    {
        try
        {
            await _navigatorGate.WaitAsync(_navigationLifetime.Token);
            try
            {
                if (generation != _navigatorGeneration || _disposed)
                    return;
                await Task.Run(update, _navigationLifetime.Token);
            }
            finally
            {
                _navigatorGate.Release();
            }
        }
        catch (OperationCanceledException) when (_navigationLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
        }
        finally
        {
            if (!_disposed && generation == _navigatorGeneration)
            {
                _navigatorScanning = false;
                RefreshStatus();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _navigationLifetime.Cancel();
        Session.Dispose();
    }
}
