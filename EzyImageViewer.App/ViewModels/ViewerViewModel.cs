using System.ComponentModel;
using System.Runtime.CompilerServices;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Core.Navigation;
using EzyImageViewer.Imaging;

namespace EzyImageViewer.App.ViewModels;

/// <summary>
/// Per-window document state: session + edit history + folder navigation + status-bar text
/// (FR-VIEW-010). Session events fire on worker threads; the window marshals to the UI thread
/// before reading. The editor and the gate are UI-thread-affine.
///
/// Every document replacement goes through <see cref="RequestLoad"/> — the toolbar, drag-drop,
/// clipboard, navigation and the activation redirect alike — so the unsaved-edit guard has no
/// bypass (FR-HIST-005).
/// </summary>
public sealed class ViewerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DocumentLoader _documentLoader;
    private readonly FolderNavigator _navigator = new(ImageFormatCatalog.ViewableExtensions);
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

    /// <summary>
    /// Project metadata keyed by the exact document instance its loader produced — a shared slot
    /// would let a stale (superseded) project loader overwrite the winner's metadata, or bind an
    /// old state to a new document of the same path. Superseded documents are disposed without
    /// publishing and collected, taking their entry with them.
    /// </summary>
    private readonly ConditionalWeakTable<ImageDocument, ProjectOpenData> _pendingProjects = new();
    private readonly ConditionalWeakTable<ImageDocument, object> _pendingRecoveries = new();

    /// <summary>The project the current document came from (null otherwise); its embedded source
    /// backs quick re-save and full-resolution re-decode.</summary>
    public ProjectOpenData? OpenedProject { get; private set; }

    /// <summary>Active-layer hint from the opened project; the window consumes it once.</summary>
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
    public bool IsBusy => Session.State == SessionState.Loading;

    /// <summary>Blocks edits while either a source replacement or a page/editor transaction is
    /// pending; neither transition may accept mutations into the state it captured before await.</summary>
    public bool IsMutationBlocked => _gate.IsReplacementPending || _pageSwitchPending;

    /// <summary>Compatibility admission gate used by the current window mutation funnels.</summary>
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
            // Projects live outside folder navigation; the anchor stays where it was.
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

    /// <summary>Opens an .ezyimg project (FR-OUT-009): the embedded source decodes as the background
    /// and the saved state/active layer are seeded when the editor rebinds.</summary>
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

    public void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string sourceFormat)
    {
        _ = sourceFormat; // format is re-sniffed from the bytes
        RequestLoad(ct => _documentLoader.LoadMemoryAsync(bytes, DocumentSource.FromClipboard(), ct));
    }

    private void Load(string path)
    {
        if (ProjectStore.IsProjectPath(path))
        {
            OpenProject(path);
            return;
        }
        RequestLoad(ct => _documentLoader.LoadFileAsync(path, ct));
    }

    /// <summary>
    /// Fire-and-forget by contract: the activation router must never await a load, and the gate's
    /// prompt is asynchronous. Exceptions surface as session state, not as an unobserved task.
    /// </summary>
    private void RequestLoad(Func<CancellationToken, Task<ImageDocument>> loader)
    {
        LoadStarted?.Invoke();
        _ = _gate.RequestLoadAsync(loader);
    }

    public bool CanCloseWithoutPrompt() => _gate.CanCloseWithoutPrompt();

    /// <summary>
    /// Rebinds the editor when a *new source* document arrives. An edit never reaches here, so the
    /// history survives everything except an actual replacement. Call on the UI thread.
    /// </summary>
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
            // Project open: the saved state seeds the editor clean (no history, not modified).
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

            // Mutation is blocked across the await; recapture defensively before committing the
            // page/editor transaction so a future non-UI caller cannot persist a stale snapshot.
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
            // A latest-wins replacement owns and disposes the predecessor; its stale frame result
            // is expected cancellation, while an ODE from the live document remains visible.
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

    /// <summary>Recomputes status texts; call on the UI thread.</summary>
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
            // Non-destructive failure: the previous document stayed, but the error must be visible.
            SessionState.Ready when Session.LastError is { } error =>
                $"{AppStrings.StateReady} · {AppStrings.StateFailed}: {error.Message}",
            SessionState.Ready => AppStrings.StateReady,
            _ => "",
        };
        DiagnosticsText = document is { Diagnostics.Count: > 0 } ? string.Join(" · ", document.Diagnostics) : "";
        ModifiedText = Editor.IsModified ? AppStrings.StateModified : "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Window title with the FR-HIST-004 modified marker.</summary>
    public string BuildTitle()
    {
        const string product = "ezy Image Viewer";
        var name = Session.Current?.Source.Path is { } path ? Path.GetFileName(path) : null;
        var marker = Editor.IsModified ? "● " : "";
        return name is null ? $"{marker}{product}" : $"{marker}{name} - {product}";
    }

    /// <summary>
    /// Status bar shows the transform *output* dimensions — what the edited document is, not what
    /// the decoded frame happens to be. Reads the editor (not the session) so the size and the
    /// transform always belong to the same document.
    /// </summary>
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
