using System.Diagnostics;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using EzyImageViewer.App.ViewModels;
using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Core.Input;
using EzyImageViewer.Imaging;
using EzyImageViewer.Infrastructure;
using EzyImageViewer.Rendering;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;

namespace EzyImageViewer.App.Views;

public sealed partial class ViewerWindow : Window, Capture.Snipping.ICaptureTarget
{
    private sealed record ToolColor(uint Argb, string Name);
    private sealed class SnapshotLease(ViewerWindow owner, SKImage image) : IDisposable
    {
        private ViewerWindow? _owner = owner;
        public SKImage Image { get; } = image;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSnapshotLease(Image);
        }
    }
    private readonly record struct ToolStyle(float StrokeWidth, float Opacity, float FontSize);
    private readonly record struct PendingText(
        RectF Bounds,
        uint Color,
        float Opacity,
        float FontSize,
        string FontFamily,
        bool IsBold,
        bool IsItalic,
        AnnotationTextAlignment Alignment,
        uint? BackgroundArgb,
        Guid DocumentId,
        long Revision);

    // Fixed dash, cached: SKPaint.Dispose does not dispose an assigned path effect, so building one
    // per repaint would strand a native effect on the finalizer queue every rubber-band frame.
    private static readonly SKPathEffect RubberBandDash = SKPathEffect.CreateDash([6f, 4f], 0f);

    private readonly ViewerViewModel _viewModel;
    private readonly DocumentLoader _documentLoader;
    private readonly ViewTransform _transform = new();
    private readonly WinRtClipboardBackend _clipboard = new();
    private readonly RasterAssetImageCache _assetCache = new();

    /// <summary>Where quick save writes (FR-OUT-002): a picked export file or the open project.
    /// Null image format means .ezyimg. Reset whenever the document is replaced. Options are
    /// asked once per target and ride along, so quick saves never re-prompt.</summary>
    private sealed record SaveTarget(string Path, ExportFormat? ImageFormat, ExportOptions? Options = null);
    private SaveTarget? _saveTarget;
    private Guid _saveTargetDocumentId;
    private Guid _recentDocumentId;
    private Exception? _loggedSessionError;
    private SessionState _trackedSessionState = SessionState.Idle;
    private long _documentOpenStartTimestamp;
    private bool _savingInProgress;
    private Task _recoveryClearTask = Task.CompletedTask;
    private long _recoveryGeneration;
    private DateTimeOffset? _recoveryCreatedAtUtc;
    private bool _recoveryRestoreInProgress;
    private TaskCompletionSource<bool>? _recoveryOpenCompletion;
    /// <summary>Cancels worker-side save/copy stages when the window goes down (§11.2).</summary>
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly HashSet<Guid> _assetWarmPending = [];
    private CancellationTokenSource _assetWarmCancellation = new();
    private long _assetWarmGeneration;
    private SKImage? _snapshot;
    private readonly object _snapshotLeaseSync = new();
    private readonly Dictionary<SKImage, int> _snapshotLeaseCounts =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<SKImage> _deferredSnapshotDisposals =
        new(ReferenceEqualityComparer.Instance);
    private Guid _snapshotDocumentId;
    private long _snapshotSurfaceRevision = -1;
    private SKShader? _checkerShader;
    private SKPoint? _lastPointer;
    private uint? _activePointerId;
    private bool _fitPending = true;
    // FR-VIEW-004: Space+drag is the pan gesture in every tool.
    private bool _spaceHeld;

    // ---- edit tools ----
    // Authoring drafts live in document space (annotations: native px, crop: output px) so they stay
    // glued to the image under zoom/pan and preview exactly what the commit creates; _lastPointer
    // stays in DIPs because the pan path scales its deltas itself.
    private CanvasTool _tool = CanvasTool.Select;
    private CanvasTool _draftTool = CanvasTool.Select;
    private Guid _selectedAnnotation;
    private SKPoint? _drawAnchor;
    private SKPoint? _drawCurrent;
    private readonly List<AnnotationPoint> _inkPoints = [];
    private float _inkSimplifyTolerance;
    private const uint DefaultStrokeColor = 0xFFE8_3B2E;
    private uint _strokeColor = DefaultStrokeColor;
    private uint _drawStrokeColor = DefaultStrokeColor;
    private float _strokeWidth = 3f;
    private float _opacity = 1f;
    private float _fontSize = 24f;
    private float _drawStrokeWidth = 3f;
    private float _drawOpacity = 1f;
    private float _drawFontSize = 24f;
    private bool _fillEnabled;
    private float _mosaicBlockSize = 12f;
    private float _blurSigma = 8f;
    // FR-ANNO-010: the mask's own color, black by default, independent of the stroke palette.
    private uint _maskColor = 0xFF00_0000;
    private float _drawBlockSize = 12f;
    private float _drawBlurSigma = 8f;
    private uint _drawMaskColor = 0xFF00_0000;
    private float _cornerRadius = 8f;
    private ArrowheadKind _arrowhead = ArrowheadKind.Triangle;
    private string _fontFamily = "Malgun Gothic";
    private bool _fontBold;
    private bool _fontItalic;
    private AnnotationTextAlignment _textAlignment;
    private bool _textBackgroundEnabled;
    private bool _drawFillEnabled;
    private float _drawCornerRadius = 8f;
    private ArrowheadKind _drawArrowhead = ArrowheadKind.Triangle;
    private string _drawFontFamily = "Malgun Gothic";
    private bool _drawFontBold;
    private bool _drawFontItalic;
    private AnnotationTextAlignment _drawTextAlignment;
    private bool _drawTextBackgroundEnabled;
    private readonly Dictionary<CanvasTool, ToolStyle> _toolStyles = [];
    private ToolDefaults _publishedToolDefaults = new();
    private bool _updatingToolControls;
    private bool _updatingZoomSlider;
    private readonly Dictionary<uint, Button> _colorButtons = [];
    private readonly Dictionary<uint, IconSourceElement> _colorIndicators = [];
    private readonly List<Button> _colorButtonOrder = [];
    private bool _colorFlyoutOpen;
    private Guid _dragAnnotation;
    private RectF _dragOrigin;
    private SKPoint _dragStartNative;
    private bool _dragMoved;
    private SelectionHandle _activeSelectionHandle;
    private Annotation? _selectionTransformOrigin;
    private bool _selectionTransformMoved;
    private SKPoint? _selectionBandAnchor;
    private SKPoint? _selectionBandCurrent;
    private bool _updatingLayerList;
    private ToolRailDock _toolRailDock;
    private readonly UISettings _uiSettings = new();
    private readonly Storyboard _toolRailOverflowPulse = new();
    private bool _animationsEnabled;
    private bool _toolRailOverflowPulseRunning;
    private bool _toolRailOverflowUpdateQueued;
    private bool _toolRailResetPending;
    private long _toolRailLayoutGeneration;
    private CancellationTokenSource? _pasteCancellation;
    private long _canvasResizeGeneration;
    private XamlRoot? _observedXamlRoot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasResizeSettleTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _animationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _scaleRenderTimer;
    private CancellationTokenSource? _scaleRenderCancellation;
    private int _scaleRenderTarget;
    private bool _animationTickInProgress;
    private bool _animationPausedByUser;
    private bool _animationEditAccepted;
    private bool _animationConfirmationPending;
    private long _animationFirstEditStateId;
    private readonly Dictionary<int, Guid> _pageActiveLayers = [];

    // Crop tool (FR-EDIT-001): the draft rectangle lives in output-canvas pixels, so it stays glued
    // to the image under pan/zoom while being dragged.
    private static readonly float?[] CropRatios = [null, 1f, 4f / 3f, 16f / 9f];
    private int _cropRatioIndex;
    private readonly CropInteraction _cropInteraction = new();

    // Every gesture is bound to the document and editor binding it started on; the mutation funnel
    // re-validates, so an interaction straddling a replacement dies instead of editing the successor.
    private long _gestureCounter;
    private long _gestureId;
    private Guid _gestureDocumentId;
    private long _gestureRevision;

    // Derived-transform cache; invalidated by reference identity of the state's transform.
    private TransformEvaluation? _evaluation;
    private BackgroundTransform? _evaluationTransform;
    private PixelSize _evaluationNativeSize;

    /// <summary>Set after edits and background stores are safely resolved so Closing can re-close.</summary>
    private bool _closeApproved;
    private bool _closePromptOpen;
    /// <summary>The single ContentDialog WinUI allows; see <see cref="ShowDialogAsync"/>.</summary>
    private ContentDialog? _activeDialog;
    private bool _activeDialogEditScoped;

    // Unattended run hooks (--smoke-open / --bench-open24mp).
    private string? _resultPath;
    private Stopwatch? _firstPaintWatch;
    private bool _unattendedFlowStarted;
    private bool _windowExercised;
    private bool _dockExercised;
    private bool _layerTransitionsExercised;
    private bool _startupHealthSessionPending;
    private bool _startupHealthFramePending;
    private string? _recoverySmokeResultPath;
    private bool _recoverySmokeSeedPending;
    private int _recoverySmokeResultWritten;

    // Authoring target (UR-007). Window state, not document state: selecting a layer is not undoable.
    private Guid _activeLayerId = AnnotationLayer.InitialLayerId;
    private bool? _layerPanelOverride;
    private Guid _renamingLayerId;

    internal Guid RecoveryWindowId { get; } = Guid.NewGuid();

    public ViewerWindow(DocumentLoader? documentLoader = null)
    {
        _documentLoader = documentLoader ?? AppServices.Loader;
        InitializeComponent();
        PreviousPageButton.Content = new IconSourceElement { IconSource = IconSourceFor("Icon.View.Previous") };
        NextPageButton.Content = new IconSourceElement { IconSource = IconSourceFor("Icon.View.Next") };
        AnimationPlaybackIcon.IconSource = IconSourceFor("Icon.View.Pause");
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        ConfigureToolRailOverflowHints();
        _canvasResizeSettleTimer = DispatcherQueue.CreateTimer();
        _canvasResizeSettleTimer.Interval = TimeSpan.FromMilliseconds(300);
        _canvasResizeSettleTimer.IsRepeating = false;
        _canvasResizeSettleTimer.Tick += OnCanvasResizeSettled;
        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.IsRepeating = false;
        _animationTimer.Tick += OnAnimationTimerTick;
        _scaleRenderTimer = DispatcherQueue.CreateTimer();
        _scaleRenderTimer.Interval = TimeSpan.FromMilliseconds(150);
        _scaleRenderTimer.IsRepeating = false;
        _scaleRenderTimer.Tick += OnScaleDependentRenderTimerTick;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ezyImageViewer.ico"));
        _viewModel = new ViewerViewModel(ConfirmDiscardAsync, _documentLoader);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LoadStarted += OnDocumentLoadStarted;
        ApplyToolDefaults(AppServices.RuntimeToolDefaults);
        ZoomSlider.ValueChanged += OnZoomSliderChanged;
        PopulateColorPalette();
        ColorFlyout.Opened += (_, _) => _colorFlyoutOpen = true;
        ColorFlyout.Closed += (_, _) => _colorFlyoutOpen = false;
        PopulateStyleOptions();
        _toolRailDock = AppServices.Settings.ToolRailDock;
        ApplyToolRailDock();
        ApplyTooltips();
        ApplySettings(AppServices.RuntimeSettings);
        AppServices.RecoveryAvailabilityChanged += OnRecoveryAvailabilityChanged;
        ApplyRecoveryAvailability(AppServices.RecoveryAvailability);
        ApplyDataProtectionStatus();
        LayerPanelTitle.Text = AppStrings.LayerPanel;
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        RegisterAccelerators();
        Root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Space) _spaceHeld = true; };
        Root.KeyUp += (_, e) => { if (e.Key == VirtualKey.Space) _spaceHeld = false; };
        // The KeyUp above never fires when Space is released while focus is elsewhere (Ctrl+O
        // picker, another window): a latched pan override would disable every edit press.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                _spaceHeld = false;
        };

        // FR-CAP-004: a lost hotkey (another app owns Ctrl+Shift+E) must not fail silently.
        if (AppServices.Capture is { HotkeyRegistered: false })
            SetStatusState(string.Format(
                AppStrings.CaptureHotkeyUnavailable,
                FormatCaptureHotkey(AppServices.Settings.CaptureHotkey)));
        _viewModel.Session.Changed += () => DispatcherQueue.TryEnqueue(OnSessionChanged);
        _viewModel.Editor.Changed += OnEditorChanged;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Canvas.Loaded += OnCanvasLoaded;
        Canvas.Unloaded += OnCanvasUnloaded;
        Closed += (_, _) =>
        {
            var recoveryCompletion = _recoveryOpenCompletion;
            _recoveryOpenCompletion = null;
            _recoveryRestoreInProgress = false;
            recoveryCompletion?.TrySetResult(false);
            AppWindow.Changed -= OnAppWindowChanged;
            DetachToolRailOverflowHints();
            DetachXamlRoot();
            _canvasResizeGeneration++;
            _canvasResizeSettleTimer?.Stop();
            _animationTimer.Stop();
            _scaleRenderTimer.Stop();
            _scaleRenderCancellation?.Cancel();
            _scaleRenderCancellation?.Dispose();
            _topmostReleaseTimer?.Stop();
            Canvas.EnableRenderLoop = false;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.LoadStarted -= OnDocumentLoadStarted;
            AppServices.RecoveryAvailabilityChanged -= OnRecoveryAvailabilityChanged;
            _viewModel.Dispose();
            SetSnapshot(null);
            _checkerShader?.Dispose();
            _assetWarmCancellation.Cancel();
            _assetWarmCancellation.Dispose();
            _assetCache.Dispose();
            _pasteCancellation?.Cancel();
            _pasteCancellation?.Dispose();
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
        };
    }

    private void OnRecoveryAvailabilityChanged(RecoveryAvailability availability)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyRecoveryAvailability(availability);
            return;
        }
        _ = DispatcherQueue.TryEnqueue(() => ApplyRecoveryAvailability(availability));
    }

    private void ApplyRecoveryAvailability(RecoveryAvailability availability)
    {
        if (AppServices.AppDataProtectionFailure is not null
            || availability is RecoveryAvailability.NotStarted
                or RecoveryAvailability.Available)
        {
            RecoveryAvailabilityBar.IsOpen = false;
            return;
        }

        RecoveryAvailabilityBar.Title = AppStrings.RecoveryAvailabilityTitle;
        RecoveryAvailabilityBar.Message = availability == RecoveryAvailability.Unavailable
            ? AppStrings.RecoveryUnavailablePersistent
            : AppStrings.RecoveryDegradedPersistent;
        RecoveryAvailabilityBar.Severity = availability == RecoveryAvailability.Unavailable
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;
        RecoveryAvailabilityBar.IsOpen = true;
    }

    private void ApplyDataProtectionStatus()
    {
        var failed = AppServices.AppDataProtectionFailure is not null;
        DataProtectionBar.Title = AppStrings.AppDataProtectionTitle;
        DataProtectionBar.Message = AppStrings.AppDataProtectionPersistent;
        DataProtectionBar.IsOpen = failed;
    }

    public void OpenFiles(IReadOnlyList<string> paths) => _viewModel.OpenFiles(paths);

    internal void TrackStartupHealthUntilSessionSettles()
    {
        _startupHealthSessionPending = true;
    }

    internal void MarkStartupHealthyAfterFirstFrame()
    {
        if (_startupHealthFramePending)
            return;
        _startupHealthFramePending = true;
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            CompositionTarget.Rendering -= rendering;
            _startupHealthFramePending = false;
            Program.MarkStartupHealthy();
        };
        CompositionTarget.Rendering += rendering;
    }

    internal void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Root.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _viewModel.SetIncludeSubfolders(settings.IncludeSubfoldersInNavigation);
        RecentButton.IsEnabled = settings.RecentFilesEnabled;
        CaptureButton.IsEnabled = !AppServices.IsSafeMode;
        SetTip(
            CaptureButton,
            $"{AppStrings.ToolCapture} ({FormatCaptureHotkey(settings.CaptureHotkey)})",
            AppStrings.TipCapture);
        UpdateStatusBar();
        if (AppServices.Capture is { HotkeyRegistered: false })
        {
            SetStatusState(string.Format(
                AppStrings.CaptureHotkeyUnavailable,
                FormatCaptureHotkey(settings.CaptureHotkey)));
        }
    }

    public void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string format) =>
        _viewModel.OpenClipboardBytes(bytes, format);

    private async void OnRecentClicked(object sender, RoutedEventArgs e)
    {
        if (!AppServices.RuntimeSettings.RecentFilesEnabled
            || Content?.XamlRoot is null)
            return;
        var entries = await AppServices.RecentFiles.SnapshotAsync();
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 420,
            MinWidth = 440,
        };
        foreach (var entry in entries)
        {
            var path = entry.Path;
            list.Items.Add(new ListViewItem
            {
                Tag = entry,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Path.GetFileName(path),
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = Path.GetDirectoryName(path) ?? "",
                            Opacity = 0.7,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            });
        }
        var content = entries.Count > 0
            ? (object)list
            : new TextBlock { Text = AppStrings.RecentEmpty, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.RecentTitle,
            Content = content,
            PrimaryButtonText = AppStrings.RecentOpen,
            SecondaryButtonText = AppStrings.RecentClear,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = true,
        };
        var doubleClicked = false;
        list.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is not null)
            {
                doubleClicked = true;
                dialog.Hide();
            }
        };

        var result = await ShowDialogAsync(dialog, editScoped: false);
        if ((result == ContentDialogResult.Primary || doubleClicked)
            && list.SelectedItem is ListViewItem
            {
                Tag: RecentFileEntry selected,
            })
        {
            OpenFiles([selected.Path]);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            try
            {
                await AppServices.RecentFiles.ClearAsync();
                SetStatusState(AppStrings.RecentCleared);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatusState(AppStrings.RecentClearFailed);
            }
        }
    }

    internal async Task ResolvePreviousRecoveriesAsync(
        RecoverySummaryEnumeration recoveryState)
    {
        ArgumentNullException.ThrowIfNull(recoveryState);
        var candidates = recoveryState.Summaries;
        if (candidates.Count == 0 || !await WaitForXamlRootAsync())
            return;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = recoveryState.IsComplete
                ? AppStrings.RecoveryBody
                : $"{AppStrings.RecoveryBody}\n\n{AppStrings.RecoveryIncompleteWarning}",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var candidate in candidates.OrderByDescending(value => value.UpdatedAtUtc))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{candidate.UpdatedAtUtc.ToLocalTime():g} · "
                    + $"{candidate.PayloadLength / (1024d * 1024d):0.0} MB",
            });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.RecoveryTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.RecoveryRestoreAll,
            SecondaryButtonText = recoveryState.IsComplete
                ? AppStrings.RecoveryDiscardAll
                : AppStrings.RecoveryDiscardVisible,
            CloseButtonText = AppStrings.RecoveryLater,
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = Program.IsRecoverySmokeVerify
            ? ContentDialogResult.Primary
            : await ShowDialogAsync(dialog, editScoped: false);
        if (result == ContentDialogResult.Secondary)
        {
            try
            {
                var remaining = await Task.Run(() =>
                    AppServices.RecoveryStore.DiscardCandidates(candidates));
                SetStatusState(remaining.IsComplete
                    ? AppStrings.RecoveryDiscarded
                    : AppStrings.RecoveryDiscardDeferred);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogRecoveryCleanupFailure(ex);
                SetStatusState(AppStrings.RecoveryFailed);
            }
            return;
        }
        if (result != ContentDialogResult.Primary)
            return;

        var failed = false;
        var firstWindowAvailable = true;
        var restoredCount = 0;
        foreach (var candidate in candidates.OrderBy(value => value.UpdatedAtUtc))
        {
            RecoveryRecord? record;
            try
            {
                record = await Task.Run(() => AppServices.RecoveryStore.TryLoad(
                    candidate.SessionId,
                    candidate.WindowId));
            }
            catch (Exception ex)
            {
                failed = true;
                LogRecoveryFailure(ex, "candidate_load_failed");
                continue;
            }
            if (record is null)
            {
                failed = true;
                continue;
            }
            var target = firstWindowAvailable
                ? this
                : AppServices.Windows!.OpenNewWindow();
            var additionalWindow = !firstWindowAvailable;
            var migrated = record with
            {
                SessionId = AppServices.RecoverySessionId,
                WindowId = target.RecoveryWindowId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            var migratedSaved = false;
            var restored = false;
            try
            {
                await Task.Run(() => AppServices.RecoveryStore.Save(migrated));
                migratedSaved = true;
                restored = await target.OpenRecoveryAsync(record.Payload, migrated.CreatedAtUtc);
                if (!restored)
                {
                    failed = true;
                    continue;
                }
                firstWindowAvailable = false;
                restoredCount++;
                await Task.Run(() => AppServices.RecoveryStore.ClearWindow(
                    record.SessionId,
                    record.WindowId));
                _ = AppServices.Logs.TryEnqueue(
                    LocalLogLevel.Information,
                    new StructuredLogEvent { Name = StructuredLogEventNames.RecoveryRestored });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                failed = true;
                LogRecoveryFailure(ex, "candidate_restore_failed");
            }
            finally
            {
                if (!restored && migratedSaved)
                {
                    try
                    {
                        await Task.Run(() => AppServices.RecoveryStore.ClearWindow(
                            migrated.SessionId,
                            migrated.WindowId));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed = true;
                        LogRecoveryFailure(ex, "migrated_cleanup_failed");
                    }
                }
                if (!restored && additionalWindow)
                {
                    try
                    {
                        await target.CloseFailedRecoveryWindowAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed = true;
                        LogRecoveryFailure(ex, "failed_window_close_failed");
                    }
                }
            }
        }

        var finalCleanupError = await Task.Run<Exception?>(() =>
        {
            try
            {
                Exception? firstError = null;
                var enumeration = AppServices.RecoveryStore.EnumerateSummaryState();
                if (!enumeration.IsComplete)
                    return new IOException(
                        "Recovery cleanup was deferred because enumeration was incomplete.");
                var remainingSessions = enumeration.Summaries
                    .Select(value => value.SessionId)
                    .ToHashSet();
                foreach (var sessionId in candidates.Select(value => value.SessionId).Distinct())
                {
                    if (!remainingSessions.Contains(sessionId))
                    {
                        try
                        {
                            AppServices.RecoveryStore.CompleteSession(sessionId);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            firstError ??= ex;
                        }
                    }
                }
                return firstError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex;
            }
        });
        if (finalCleanupError is not null)
        {
            failed = true;
            LogRecoveryFailure(finalCleanupError, "cleanup_failed");
        }
        SetStatusState(failed ? AppStrings.RecoveryFailed : AppStrings.RecoveryRestored);
        if (Program.IsRecoverySmokeVerify)
            await CompleteRecoverySmokeVerificationAsync(candidates, restoredCount, failed);
    }

    private async Task CompleteRecoverySmokeVerificationAsync(
        IReadOnlyList<RecoveryRecordSummary> candidates,
        int restoredCount,
        bool restoreFailed)
    {
        var sessionState = _viewModel.Session.State.ToString();
        var isModified = _viewModel.Editor.IsModified;
        var width = _viewModel.Session.Current?.Frame.Width ?? 0;
        var height = _viewModel.Session.Current?.Frame.Height ?? 0;
        var currentSessionId = AppServices.RecoverySessionId;
        try
        {
            if (AppServices.Windows is not { } windows)
                throw new InvalidOperationException("The recovery smoke window manager is unavailable.");
            await windows.PrepareCloseAsync(this);
            var remaining = await Task.Run(() =>
            {
                var enumeration = AppServices.RecoveryStore.EnumerateSummaryState();
                if (!enumeration.IsComplete)
                    return (Original: -1, Current: -1);
                var original = enumeration.Summaries.Count(summary => candidates.Any(candidate =>
                    candidate.SessionId == summary.SessionId
                    && candidate.WindowId == summary.WindowId));
                var current = enumeration.Summaries.Count(summary =>
                    summary.SessionId == currentSessionId)
                    + AppServices.RecoveryStore.EnumerateCrashMarkers().Count(marker =>
                        marker.SessionId == currentSessionId);
                return (Original: original, Current: current);
            });
            var verified = restoredCount > 0
                && !restoreFailed
                && remaining.Original == 0
                && remaining.Current == 0;
            WriteRecoverySmokeResult(new
            {
                state = verified ? "Verified" : "RestoreFailed",
                candidateCount = candidates.Count,
                restoredCount,
                originalCandidatesRemaining = remaining.Original,
                currentSessionArtifactsRemaining = remaining.Current,
                normalCleanupCompleted = remaining.Current == 0,
                failed = restoreFailed || !verified,
                sessionState,
                isModified,
                width,
                height,
            });
        }
        catch (Exception ex)
        {
            LogRecoveryFailure(ex, "diagnostic_cleanup_failed");
            WriteRecoverySmokeResult(new
            {
                state = "CleanupFailed",
                candidateCount = candidates.Count,
                restoredCount,
                failed = true,
                error = ex.GetType().Name,
                sessionState,
                isModified,
                width,
                height,
            });
        }
        _closeApproved = true;
        Close();
    }

    internal async Task<bool> OfferSafeModeAsync()
    {
        if (!await WaitForXamlRootAsync())
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.SafeModeTitle,
            Content = new TextBlock
            {
                Text = AppStrings.SafeModeBody,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = AppStrings.SafeModeStart,
            CloseButtonText = AppStrings.SafeModeContinue,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowDialogAsync(dialog, editScoped: false)
            == ContentDialogResult.Primary;
    }

    private async Task CloseFailedRecoveryWindowAsync()
    {
        if (AppServices.Windows is { } windows)
            await windows.PrepareCloseAsync(this);
        _closeApproved = true;
        Close();
    }

    private static void LogRecoveryCleanupFailure(Exception exception) =>
        LogRecoveryFailure(exception, "cleanup_failed");

    private static void LogRecoveryFailure(Exception exception, string errorCode)
    {
        _ = AppServices.Logs.TryEnqueue(
            LocalLogLevel.Error,
            new StructuredLogEvent
            {
                Name = StructuredLogEventNames.RecoveryCleanupFailed,
                ErrorCode = errorCode,
            },
            exception);
    }

    private async Task<bool> WaitForXamlRootAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Content?.XamlRoot is not null)
                return true;
            await Task.Delay(20);
        }
        return false;
    }

    private Task<bool> OpenRecoveryAsync(byte[] payload, DateTimeOffset createdAtUtc)
    {
        if (_recoveryOpenCompletion is not null)
            throw new InvalidOperationException("A recovery load is already active.");
        _recoveryCreatedAtUtc = createdAtUtc;
        _recoveryRestoreInProgress = true;
        _recoveryOpenCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _viewModel.OpenRecovery(payload);
        return _recoveryOpenCompletion.Task;
    }

    private void CompleteRecoveryOpenIfPending()
    {
        if (_recoveryOpenCompletion is not { } completion
            || _viewModel.Session.State is SessionState.Loading or SessionState.Idle)
            return;
        var restored = _viewModel.Session.State == SessionState.Ready
            && _viewModel.Session.LastError is null
            && _viewModel.Editor.IsModified
            && _viewModel.OpenedProject?.Path is null;
        _recoveryOpenCompletion = null;
        _recoveryRestoreInProgress = false;
        if (restored)
            UpdateRecoveryCheckpoint();
        completion.TrySetResult(restored);
    }

    private void ApplyTooltips()
    {
        SetTip(OpenButton, AppStrings.ToolOpen, AppStrings.TipOpen);
        SetTip(RecentButton, AppStrings.ToolRecent, AppStrings.TipRecent);
        SetTip(ClipboardButton, AppStrings.ToolClipboard, AppStrings.TipClipboard);
        SetTip(
            CaptureButton,
            $"{AppStrings.ToolCapture} ({FormatCaptureHotkey(AppServices.Settings.CaptureHotkey)})",
            AppStrings.TipCapture);
        SetTip(SaveButton, AppStrings.ToolSave, AppStrings.TipSave);
        SetTip(NewWindowButton, AppStrings.ToolNewWindow, AppStrings.TipNewWindow);
        SetTip(PreviousButton, AppStrings.ToolPrevious, AppStrings.TipPrevious);
        SetTip(NextButton, AppStrings.ToolNext, AppStrings.TipNext);
        SetTip(PreviousPageButton, AppStrings.ToolPreviousPage, AppStrings.ToolPreviousPage);
        SetTip(NextPageButton, AppStrings.ToolNextPage, AppStrings.ToolNextPage);
        SetTip(AnimationPlaybackButton, AppStrings.ToolPauseAnimation, AppStrings.ToolPauseAnimation);
        SetTip(FitButton, AppStrings.ToolFit, AppStrings.TipFit);
        SetTip(ActualSizeButton, AppStrings.ToolActualSize, AppStrings.TipActualSize);
        SetTip(RotateButton, AppStrings.ToolRotate, AppStrings.TipRotate);
        SetTip(EyedropperButton, AppStrings.ToolEyedropper, AppStrings.TipEyedropper);
        SetTip(ZoomOutButton, AppStrings.ToolZoomOut, AppStrings.TipZoomOut);
        SetTip(ZoomInButton, AppStrings.ToolZoomIn, AppStrings.TipZoomIn);
        SetTip(FullScreenButton, AppStrings.ToolFullScreen, AppStrings.TipFullScreen);
        SetTip(LayerPanelButton, AppStrings.ToolLayerPanel, AppStrings.TipLayerPanel);
        SetTip(SettingsButton, AppStrings.ToolSettings, AppStrings.TipSettings);
        SetTip(LayerAddButton, AppStrings.LayerAdd, AppStrings.TipLayerAdd);
        SetTip(LayerDeleteButton, AppStrings.LayerDelete, AppStrings.TipLayerDelete);
        SetTip(LayerUpButton, AppStrings.LayerMoveUp, AppStrings.TipLayerMoveUp);
        SetTip(LayerDownButton, AppStrings.LayerMoveDown, AppStrings.TipLayerMoveDown);
        SetTip(LayerRenameButton, AppStrings.LayerRename, AppStrings.TipLayerRename);
        SetTip(LayerMoveSelectionButton, AppStrings.LayerMoveSelection, AppStrings.TipLayerMoveSelection);
        SetTip(SendToBackButton, AppStrings.ToolSendToBack, AppStrings.TipSendToBack);
        SetTip(SendBackwardButton, AppStrings.ToolSendBackward, AppStrings.TipSendBackward);
        SetTip(BringForwardButton, AppStrings.ToolBringForward, AppStrings.TipBringForward);
        SetTip(BringToFrontButton, AppStrings.ToolBringToFront, AppStrings.TipBringToFront);
        SetTip(DuplicateButton, AppStrings.ToolDuplicate, AppStrings.TipDuplicate);
        SetTip(EditTextButton, AppStrings.ToolEditText, AppStrings.TipEditText);
        SetTip(SelectButton, AppStrings.ToolSelect, AppStrings.TipSelect);
        SetTip(PenButton, AppStrings.ToolPen, AppStrings.TipPen);
        SetTip(HighlighterButton, AppStrings.ToolHighlighter, AppStrings.TipHighlighter);
        SetTip(LineButton, AppStrings.ToolLine, AppStrings.TipLine);
        SetTip(ArrowButton, AppStrings.ToolArrow, AppStrings.TipArrow);
        SetTip(RectangleButton, AppStrings.ToolRectangle, AppStrings.TipRectangle);
        SetTip(RoundedRectangleButton, AppStrings.ToolRoundedRectangle, AppStrings.TipRoundedRectangle);
        SetTip(EllipseButton, AppStrings.ToolEllipse, AppStrings.TipEllipse);
        SetTip(TextButton, AppStrings.ToolText, AppStrings.TipText);
        SetTip(NumberButton, AppStrings.ToolNumber, AppStrings.TipNumber);
        SetTip(MosaicButton, AppStrings.ToolMosaic, AppStrings.TipMosaic);
        SetTip(BlurButton, AppStrings.ToolBlur, AppStrings.TipBlur);
        SetTip(MaskButton, AppStrings.ToolMask, AppStrings.TipMask);
        SetTip(UndoButton, AppStrings.ToolUndo, AppStrings.TipUndo);
        SetTip(RedoButton, AppStrings.ToolRedo, AppStrings.TipRedo);
        SetTip(CropButton, AppStrings.ToolCrop, AppStrings.TipCrop);
        SetTip(FlipHorizontalButton, AppStrings.ToolFlipHorizontal, AppStrings.TipFlipHorizontal);
        SetTip(FlipVerticalButton, AppStrings.ToolFlipVertical, AppStrings.TipFlipVertical);
        SetTip(ResizeButton, AppStrings.ToolResize, AppStrings.TipResize);
        FillCheckBox.Content = AppStrings.StyleFill;
        TextBackgroundCheckBox.Content = AppStrings.StyleBackground;
        BlockSizeLabel.Text = AppStrings.StyleBlockSize;
        BlurSigmaLabel.Text = AppStrings.StyleBlurSigma;
        SetNameAndTip(CornerRadiusBox, AppStrings.StyleCornerRadius);
        SetNameAndTip(ArrowheadBox, AppStrings.StyleArrowhead);
        SetNameAndTip(FontFamilyBox, AppStrings.StyleFontFamily);
        SetNameAndTip(BoldButton, AppStrings.StyleBold);
        SetNameAndTip(ItalicButton, AppStrings.StyleItalic);
        SetNameAndTip(TextAlignmentBox, AppStrings.StyleAlignment);
        SetNameAndTip(ObjectRotationBox, AppStrings.ToolRotate);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StatusProgress, AppStrings.StatusProgress);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            StatusState, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StatusDetailsScroll, AppStrings.StatusDetails);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ToolRailScroll, AppStrings.ToolRail);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Canvas, AppStrings.CanvasName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            AnnotationContextBar, AppStrings.ToolContext);
        SetGroupName(FileToolGroup, AppStrings.GroupFile);
        SetGroupName(HistoryToolGroup, AppStrings.GroupHistory);
        SetGroupName(ImageToolGroup, AppStrings.GroupImage);
        SetGroupName(DrawingToolGroup, AppStrings.GroupDrawing);
        SetGroupName(ShapeToolGroup, AppStrings.GroupShapes);
        SetGroupName(TextToolGroup, AppStrings.GroupText);
        SetGroupName(ProtectionToolGroup, AppStrings.GroupProtection);
        SetGroupName(ViewToolGroup, AppStrings.GroupView);
        UpdateDynamicTooltips();

        static void SetGroupName(UIElement element, string text) =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
    }

    private static void SetTip(
        UIElement element,
        string title,
        string description,
        string? automationName = null)
    {
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(element, $"{title}\n{description}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, automationName ?? title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(element, description);
    }

    private static void SetNameAndTip(UIElement element, string text)
    {
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
    }

    private static string FormatCaptureHotkey(CaptureHotkey hotkey)
    {
        var parts = new List<string>(5);
        if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0)
            parts.Add("Ctrl");
        if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0)
            parts.Add("Shift");
        if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0)
            parts.Add("Alt");
        if ((hotkey.Modifiers & HotkeyModifiers.Windows) != 0)
            parts.Add("Win");
        parts.Add(CaptureHotkeyPolicy.GetVirtualKeyDisplayName(hotkey.VirtualKey));
        return string.Join('+', parts);
    }

    private void UpdateDynamicTooltips()
    {
        SetTip(CropRatioButton,
            $"{AppStrings.ToolCropRatio}: {CropRatioText()}", AppStrings.TipCropRatio);
        SetTip(ColorButton,
            $"{AppStrings.ToolColor}: #{_strokeColor & 0x00FF_FFFF:X6}", AppStrings.TipColor);
        SetTip(DockToggleButton, AppStrings.ToolDockToggle,
            _toolRailDock == ToolRailDock.Horizontal
                ? AppStrings.TipDockVertical : AppStrings.TipDockHorizontal);
        SetTip(ZoomSlider,
            $"{AppStrings.StatusZoom}: {_transform.Scale * 100:0}%",
            AppStrings.TipZoomSlider,
            AppStrings.StatusZoom);
    }

    private void PopulateStyleOptions()
    {
        ArrowheadBox.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.ArrowheadOpen,
            Tag = ArrowheadKind.Open,
        });
        ArrowheadBox.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.ArrowheadTriangle,
            Tag = ArrowheadKind.Triangle,
        });
        ArrowheadBox.SelectedIndex = 1;

        TextAlignmentBox.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.AlignmentLeft,
            Tag = AnnotationTextAlignment.Left,
        });
        TextAlignmentBox.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.AlignmentCenter,
            Tag = AnnotationTextAlignment.Center,
        });
        TextAlignmentBox.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.AlignmentRight,
            Tag = AnnotationTextAlignment.Right,
        });
        TextAlignmentBox.SelectedIndex = 0;
    }

    private void PopulateColorPalette()
    {
        var colors = new ToolColor[]
        {
            new(0xFF00_0000, AppStrings.ColorBlack),
            new(0xFF55_5555, AppStrings.ColorGray),
            new(0xFFA6_A6A6, AppStrings.ColorSilver),
            new(0xFFFF_FFFF, AppStrings.ColorWhite),
            new(DefaultStrokeColor, AppStrings.ColorRed),
            new(0xFFF5_7C00, AppStrings.ColorOrange),
            new(0xFFFF_C107, AppStrings.ColorYellow),
            new(0xFF8B_C34A, AppStrings.ColorLime),
            new(0xFF2E_7D32, AppStrings.ColorGreen),
            new(0xFF00_9688, AppStrings.ColorTeal),
            new(0xFF03_A9F4, AppStrings.ColorSky),
            new(0xFF15_65C0, AppStrings.ColorBlue),
            new(0xFF1A_237E, AppStrings.ColorNavy),
            new(0xFF7B_1FA2, AppStrings.ColorPurple),
            new(0xFFD8_1B60, AppStrings.ColorMagenta),
            new(0xFF79_5548, AppStrings.ColorBrown),
        };

        for (var index = 0; index < colors.Length; index++)
        {
            var color = colors[index];
            var button = new Button
            {
                Width = 36,
                Height = 36,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Tag = color,
                Background = new SolidColorBrush(ToUiColor(0x0000_0000)),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var chip = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ToUiColor(color.Argb)),
                BorderThickness = new Thickness(1),
                BorderBrush = ResourceBrush("ControlStrongStrokeColorDefaultBrush",
                    new SolidColorBrush(ToUiColor(0xFF73_7373))),
            };
            var indicator = new IconSourceElement
            {
                Width = 14,
                Height = 14,
                IconSource = IconSourceFor(IsLightColor(color.Argb)
                    ? "Icon.Common.Check.Dark" : "Icon.Common.Check.Light", 14),
                Visibility = Visibility.Collapsed,
            };
            var content = new Grid { Width = 22, Height = 22 };
            content.Children.Add(chip);
            content.Children.Add(indicator);
            button.Content = content;
            button.Click += OnColorSwatchClicked;
            button.KeyDown += OnColorSwatchKeyDown;
            var colorName = $"{color.Name} #{color.Argb & 0x00FF_FFFF:X6}";
            SetTip(button, colorName, AppStrings.TipColorSwatch);
            Grid.SetRow(button, index / 4);
            Grid.SetColumn(button, index % 4);
            ColorGrid.Children.Add(button);
            _colorButtons.Add(color.Argb, button);
            _colorIndicators.Add(color.Argb, indicator);
            _colorButtonOrder.Add(button);
        }

        UpdateColorSelection();
    }

    private void OnColorSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolColor color })
            return;

        // In mask context the palette edits ONLY the mask color: the stroke color and any non-mask
        // selection stay untouched, and vice versa (FR-ANNO-010 independent mask color).
        if (IsMaskColorContext())
        {
            _maskColor = color.Argb;
            if (SelectedAnnotation() is ProtectionAnnotation
                { Kind: ProtectionKind.Mask, IsLocked: false } mask)
                ApplySelectedEdit(AnnotationEditKind.Style, mask with { MaskArgb = color.Argb });
        }
        else
        {
            _strokeColor = color.Argb;
            if (SelectedAnnotation() is { IsLocked: false } selected)
            {
                ApplySelectedEdit(AnnotationEditKind.Style, selected switch
                {
                    InkAnnotation ink => ink with { StrokeArgb = color.Argb },
                    LineAnnotation line => line with { StrokeArgb = color.Argb },
                    RectangleAnnotation rectangle => rectangle with { StrokeArgb = color.Argb },
                    TextAnnotation text => text with { ForegroundArgb = color.Argb },
                    NumberMarkerAnnotation marker => marker with { FillArgb = color.Argb },
                    _ => selected,
                });
            }
        }
        UpdateColorSelection();
        PublishCurrentToolDefaults();
        ColorFlyout.Hide();
    }

    private bool IsMaskColorContext() => _tool == CanvasTool.Mask
        || SelectedAnnotation() is ProtectionAnnotation { Kind: ProtectionKind.Mask };

    private void OnColorSwatchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        var index = _colorButtonOrder.IndexOf(button);
        if (index < 0)
            return;
        var target = e.Key switch
        {
            VirtualKey.Left when index % 4 > 0 => index - 1,
            VirtualKey.Right when index % 4 < 3 => index + 1,
            VirtualKey.Up when index >= 4 => index - 4,
            VirtualKey.Down when index + 4 < _colorButtonOrder.Count => index + 4,
            _ => index,
        };
        if (target == index)
            return;
        _colorButtonOrder[target].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void UpdateColorSelection()
    {
        // The palette reflects the color it would edit: mask color in mask context, stroke otherwise.
        var effective = IsMaskColorContext() ? _maskColor : _strokeColor;
        CurrentColorSwatch.Background = new SolidColorBrush(ToUiColor(effective | 0xFF00_0000));
        var selectedBrush = ResourceBrush(
            "SystemControlHighlightAccentBrush", new SolidColorBrush(ToUiColor(0xFF00_78D4)));
        var normalBrush = ResourceBrush(
            "ControlStrongStrokeColorDefaultBrush", new SolidColorBrush(ToUiColor(0xFF73_7373)));
        CurrentColorSwatch.BorderBrush = normalBrush;
        foreach (var (argb, button) in _colorButtons)
        {
            var selected = argb == effective;
            button.BorderThickness = new Thickness(selected ? 2 : 0);
            button.BorderBrush = selected ? selectedBrush : normalBrush;
            _colorIndicators[argb].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
                button, selected
                    ? $"{AppStrings.ColorSelected}. {AppStrings.TipColorSwatch}"
                    : AppStrings.TipColorSwatch);
        }
        UpdateDynamicTooltips();

    }

    private static Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush : fallback;

    private static bool IsLightColor(uint argb)
    {
        var red = (argb >> 16) & 0xFF;
        var green = (argb >> 8) & 0xFF;
        var blue = argb & 0xFF;
        return red * 299 + green * 587 + blue * 114 >= 150_000;
    }

    private IconSource IconSourceFor(string key, double size = 20)
    {
        // Clone into a per-window source: dynamic icons are glyph strings, while static resources
        // are FontIconSource instances that cannot be assigned to two visual owners directly.
        var resource = Root.Resources[key];
        var glyph = resource switch
        {
            string value => value,
            FontIconSource source => source.Glyph,
            _ => throw new InvalidOperationException($"Icon resource '{key}' is not a font glyph."),
        };
        return new FontIconSource
        {
            FontFamily = (FontFamily)Root.Resources["Icon.FontFamily"],
            FontSize = size,
            Glyph = glyph,
            Foreground = key.EndsWith(".Light", StringComparison.Ordinal)
                ? new SolidColorBrush(ToUiColor(0xFFFF_FFFF))
                : key.EndsWith(".Dark", StringComparison.Ordinal)
                    ? new SolidColorBrush(ToUiColor(0xFF00_0000))
                    : null,
        };
    }

    private static Windows.UI.Color ToUiColor(uint argb) =>
        Microsoft.UI.ColorHelper.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void RegisterAccelerators()
    {
        const VirtualKey openBracketKey = (VirtualKey)0xDB;
        const VirtualKey closeBracketKey = (VirtualKey)0xDD;
        Add(VirtualKey.Left, default, (_, _) => NavigateOrNudge(-1f, 0f));
        Add(VirtualKey.Right, default, (_, _) => NavigateOrNudge(1f, 0f));
        Add(VirtualKey.Up, default, (_, _) => NudgeSelection(0f, -1f));
        Add(VirtualKey.Down, default, (_, _) => NudgeSelection(0f, 1f));
        Add(VirtualKey.Left, VirtualKeyModifiers.Shift, (_, _) => NudgeSelection(-10f, 0f));
        Add(VirtualKey.Right, VirtualKeyModifiers.Shift, (_, _) => NudgeSelection(10f, 0f));
        Add(VirtualKey.Up, VirtualKeyModifiers.Shift, (_, _) => NudgeSelection(0f, -10f));
        Add(VirtualKey.Down, VirtualKeyModifiers.Shift, (_, _) => NudgeSelection(0f, 10f));
        Add(VirtualKey.PageUp, default, async (_, _) => await SwitchPageAsync(-1));
        Add(VirtualKey.PageDown, default, async (_, _) => await SwitchPageAsync(1));
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, (_, _) => FitToViewport());
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control, (_, _) => ActualSize());
        Add(VirtualKey.O, VirtualKeyModifiers.Control, async (_, _) => await OpenPickerAsync());
        Add(VirtualKey.V, VirtualKeyModifiers.Control, async (_, _) => await PasteFromClipboardAsync());
        Add(VirtualKey.S, VirtualKeyModifiers.Control, async (_, _) => await SaveAsync(quick: true));
        Add(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            async (_, _) => await SaveAsync(quick: false));
        // Yields while a text control has focus (layer rename, dialogs): standard text copy must
        // work there, and the image copy would silently replace the user's clipboard.
        AddConditional(VirtualKey.C, VirtualKeyModifiers.Control, () =>
        {
            if (IsTextInputFocused())
                return false;
            _ = CopyToClipboardAsync();
            return true;
        });
        Add(VirtualKey.F11, default, (_, _) => ToggleFullScreen());
        Add(VirtualKey.Escape, default, (_, _) => OnEscape());
        // FR-HIST-001: Ctrl+Y and Ctrl+Shift+Z both redo (Windows and cross-platform conventions).
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, (_, _) => Undo());
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, (_, _) => Redo());
        Add(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => Redo());
        Add(VirtualKey.Delete, default, (_, _) => DeleteSelection());
        Add(VirtualKey.D, VirtualKeyModifiers.Control, (_, _) => DuplicateSelection());
        Add(openBracketKey, VirtualKeyModifiers.Control, (_, _) => ReorderSelection(-1, false));
        Add(closeBracketKey, VirtualKeyModifiers.Control, (_, _) => ReorderSelection(1, false));
        Add(openBracketKey, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            (_, _) => ReorderSelection(-1, true));
        Add(closeBracketKey, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            (_, _) => ReorderSelection(1, true));
        AddConditional(VirtualKey.Enter, default, TryCommitCropReviewFromKeyboard);

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (s, e) =>
            {
                e.Handled = true;
                handler(s, e);
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        void AddConditional(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> handler)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) => e.Handled = handler();
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    private bool IsTextInputFocused() =>
        Content?.XamlRoot is { } xamlRoot
        && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot)
            is TextBox or RichEditBox or PasswordBox or AutoSuggestBox;

    // ---- session → render snapshot (UI thread only; swap and dispose are serialized here) ----

    private void OnSessionChanged()
    {
        _animationTimer.Stop();
        _scaleRenderTimer.Stop();
        _scaleRenderCancellation?.Cancel();
        _animationPausedByUser = false;
        _animationEditAccepted = false;
        _animationConfirmationPending = false;
        _pageActiveLayers.Clear();
        _pasteCancellation?.Cancel();
        // Editor rebind first: the status bar and title read IsModified from it.
        _viewModel.SyncEditor();
        RecordSessionOutcome();
        // A replaced document gets a fresh save target; a project brings its own (FR-OUT-009).
        var sessionDocumentId = _viewModel.Session.Current?.Id ?? Guid.Empty;
        if (sessionDocumentId != _saveTargetDocumentId)
        {
            _saveTargetDocumentId = sessionDocumentId;
            _saveTarget = _viewModel.OpenedProject is { Path: { } projectPath }
                ? new SaveTarget(projectPath, null)
                : null;
            if (_viewModel.TakePendingActiveLayerId() is { } projectLayer
                && _viewModel.Editor.State.FindLayer(projectLayer) is not null)
            {
                // The editor rebind already drew the panel with the default (top) layer; the
                // restored authoring target must win in the row highlight and footer too.
                _activeLayerId = projectLayer;
                UpdateLayerPanel();
                UpdateToolUi();
            }
        }
        // A replacement — its start AND its completion — kills any straddling interaction: a gesture
        // begun on the predecessor must never commit into the successor (the identity check in the
        // mutation funnel would refuse anyway; this also clears the visual draft), and an open edit
        // dialog now targets a canvas that no longer exists.
        CancelActiveGesture();
        CancelEditDialog();
        RebuildSnapshot(_viewModel.Session.Current);
        _viewModel.RefreshStatus();
        UpdateStatusBar();
        UpdateOverlay();
        UpdateEditCommands();
        Canvas.Invalidate();
        ConfigureAnimationPlayback();
        MaybeApplyHoldEdit();
        MaybeRunRecoverySmokeSeed();
        CompleteRecoveryOpenIfPending();
        MaybeWriteUnattendedResult();
    }

    private void RecordSessionOutcome()
    {
        long? elapsedMilliseconds = null;
        var sessionState = _viewModel.Session.State;
        if (sessionState != _trackedSessionState)
        {
            _trackedSessionState = sessionState;
            if (sessionState == SessionState.Loading
                && _documentOpenStartTimestamp == 0)
            {
                _documentOpenStartTimestamp = Stopwatch.GetTimestamp();
            }
            else if (_documentOpenStartTimestamp != 0
                && sessionState is SessionState.Ready or SessionState.Failed)
            {
                elapsedMilliseconds = Math.Max(
                    0,
                    (long)Math.Round(Stopwatch.GetElapsedTime(
                        _documentOpenStartTimestamp).TotalMilliseconds));
                _documentOpenStartTimestamp = 0;
            }
        }
        if (_startupHealthSessionPending
            && sessionState is SessionState.Ready or SessionState.Failed)
        {
            _startupHealthSessionPending = false;
            Program.MarkStartupHealthy();
        }
        if (_viewModel.Session.State == SessionState.Ready
            && _viewModel.Session.Current is { } document
            && document.Id != _recentDocumentId)
        {
            _recentDocumentId = document.Id;
            if (document.Source is
                { Kind: DocumentSourceKind.File or DocumentSourceKind.Project, Path: { } path })
            {
                if (AppServices.RuntimeSettings.RecentFilesEnabled)
                    _ = AppServices.RecentFiles.RecordOpened(path);
            }
            _ = AppServices.Logs.TryEnqueue(
                LocalLogLevel.Information,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.DocumentOpened,
                    Format = document.Format.ToString(),
                    Renderer = ToLogToken(document.Renderer.Name),
                    DocumentPath = document.Source.Path,
                    ElapsedMilliseconds = elapsedMilliseconds,
                });
        }

        if (_viewModel.Session.LastError is { } error
            && !ReferenceEquals(error, _loggedSessionError))
        {
            _loggedSessionError = error;
            _ = AppServices.Logs.TryEnqueue(
                LocalLogLevel.Warning,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.DocumentOpenFailed,
                    ErrorCode = "open_failed",
                    ElapsedMilliseconds = elapsedMilliseconds,
                },
                error);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateStatusBar();
    }

    private void OnDocumentLoadStarted()
    {
        _documentOpenStartTimestamp = Stopwatch.GetTimestamp();
    }

    private static string ToLogToken(string value)
    {
        var sanitized = new string(value.Take(64).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    /// <summary>Edits never touch the session, so they repaint through the editor's own event.</summary>
    private void OnEditorChanged()
    {
        if (_viewModel.Editor.Document is
            { SequenceKind: DocumentSequenceKind.Animation } animationDocument
            && _viewModel.Editor.IsModified
            && !_animationEditAccepted
            && !_animationConfirmationPending)
        {
            _animationConfirmationPending = true;
            _animationFirstEditStateId = _viewModel.Editor.CurrentStateId;
            _animationTimer.Stop();
            AnimationPlaybackButton.IsEnabled = false;
            CancelActiveGesture();
            _ = ConfirmAnimationFlattenAsync(animationDocument);
        }
        // An edit may have moved the transform output size; the view keeps its rotation and mode
        // (UpdateContentSize, not SetContent). Skipped while the snapshot lags the editor — the
        // rebuild path sizes the content itself.
        if (_viewModel.Editor.Document is { } document && document.Id == _snapshotDocumentId)
        {
            var output = Evaluation(document).OutputSize;
            _transform.UpdateContentSize(output.Width, output.Height);
        }
        _viewModel.RefreshStatus();
        _assetCache.Prune(_viewModel.Editor.State);
        QueueMissingAssetWarms();
        if (_selectedAnnotation != default
            && _viewModel.Editor.State.Find(_selectedAnnotation) is null)
            _selectedAnnotation = default;
        // The authoring layer must always exist; a hidden or locked layer also drops the canvas
        // selection so context-bar edits cannot bypass the layer state (UR-007).
        if (_viewModel.Editor.State.FindLayer(_activeLayerId) is null)
            _activeLayerId = _viewModel.Editor.State.Layers[^1].Id;
        if (_selectedAnnotation != default
            && _viewModel.Editor.State.FindLayerOf(_selectedAnnotation) is not { IsVisible: true, IsLocked: false })
            _selectedAnnotation = default;
        UpdateStatusBar();
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        Canvas.Invalidate();
        UpdateRecoveryCheckpoint();
    }

    private void UpdateRecoveryCheckpoint()
    {
        if (!AppServices.RecoveryEnabled || _recoveryRestoreInProgress)
            return;
        var generation = ++_recoveryGeneration;
        if (!_viewModel.Editor.IsModified || _viewModel.Editor.Document is null)
        {
            _recoveryCreatedAtUtc = null;
            _recoveryClearTask = ClearRecoveryCheckpointAsync();
            return;
        }
        _recoveryCreatedAtUtc ??= DateTimeOffset.UtcNow;
        _ = ScheduleRecoveryAfterClearAsync(generation, _recoveryClearTask);
    }

    private async Task ClearRecoveryCheckpointAsync()
    {
        try
        {
            await AppServices.Recovery.ClearWindowAsync(RecoveryWindowId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task ScheduleRecoveryAfterClearAsync(long generation, Task precedingClear)
    {
        try
        {
            await precedingClear;
            if (generation != _recoveryGeneration
                || !_viewModel.Editor.IsModified
                || _recoveryRestoreInProgress)
                return;
            await AppServices.Recovery.Schedule(
                RecoveryWindowId,
                BuildRecoveryRecordOnUiAsync);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or IOException or UnauthorizedAccessException)
        {
        }
    }

    private Task<RecoveryRecord> BuildRecoveryRecordOnUiAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<RecoveryRecord>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var document = _viewModel.Editor.Document
                    ?? throw new InvalidOperationException("No document is available for recovery.");
                if (!_viewModel.Editor.IsModified)
                    throw new InvalidOperationException("A clean document does not need recovery.");
                var documentId = document.Id;
                var stateId = _viewModel.Editor.CurrentStateId;
                var pages = _viewModel.CaptureProjectPages(_activeLayerId);
                var activePageIndex = document.SequenceKind == DocumentSequenceKind.Pages
                    ? document.CurrentFrameIndex
                    : 0;
                var createdAt = _recoveryCreatedAtUtc ?? DateTimeOffset.UtcNow;
                var (sourceName, sourceBytes) =
                    await GetRecoveryProjectSourceAsync(document, token);
                token.ThrowIfCancellationRequested();
                var payload = await Task.Run(() => ProjectStore.Build(
                    pages,
                    activePageIndex,
                    sourceName,
                    sourceBytes,
                    previewPng: null), token);
                token.ThrowIfCancellationRequested();
                if (_viewModel.Editor.Document?.Id != documentId
                    || _viewModel.Editor.CurrentStateId != stateId)
                    throw new OperationCanceledException(token);
                completion.TrySetResult(new RecoveryRecord
                {
                    SessionId = AppServices.RecoverySessionId,
                    WindowId = RecoveryWindowId,
                    CreatedAtUtc = createdAt,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = [],
                    Payload = payload,
                });
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(token.IsCancellationRequested
                    ? token
                    : new CancellationToken(canceled: true));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException(
                "The recovery snapshot could not reach the UI thread."));
        }
        return completion.Task;
    }

    private void UpdateEditCommands()
    {
        UndoButton.IsEnabled = _viewModel.Editor.CanUndo;
        RedoButton.IsEnabled = _viewModel.Editor.CanRedo;
        SelectButton.IsChecked = _tool == CanvasTool.Select;
        PenButton.IsChecked = _tool == CanvasTool.Pen;
        HighlighterButton.IsChecked = _tool == CanvasTool.Highlighter;
        LineButton.IsChecked = _tool == CanvasTool.Line;
        ArrowButton.IsChecked = _tool == CanvasTool.Arrow;
        RectangleButton.IsChecked = _tool == CanvasTool.Rectangle;
        RoundedRectangleButton.IsChecked = _tool == CanvasTool.RoundedRectangle;
        EllipseButton.IsChecked = _tool == CanvasTool.Ellipse;
        TextButton.IsChecked = _tool == CanvasTool.Text;
        NumberButton.IsChecked = _tool == CanvasTool.Number;
        MosaicButton.IsChecked = _tool == CanvasTool.Mosaic;
        BlurButton.IsChecked = _tool == CanvasTool.Blur;
        MaskButton.IsChecked = _tool == CanvasTool.Mask;
        EyedropperButton.IsChecked = _tool == CanvasTool.Eyedropper;
        CropButton.IsChecked = _tool == CanvasTool.Crop;
        var selected = SelectedAnnotation();
        var editable = selected is { IsLocked: false };
        DuplicateButton.IsEnabled = editable;
        SendToBackButton.IsEnabled = editable;
        SendBackwardButton.IsEnabled = editable;
        BringForwardButton.IsEnabled = editable;
        BringToFrontButton.IsEnabled = editable;
        EditTextButton.IsEnabled = editable && selected is TextAnnotation;
        Title = BuildWindowTitle();
    }

    /// <summary>Cached pipeline evaluation; recomputed only when the transform or source changes.
    /// Inputs were validated when the command was admitted, so this never throws at paint time.</summary>
    private TransformEvaluation Evaluation(Core.Documents.ImageDocument document)
    {
        var transform = _viewModel.Editor.State.Transform;
        if (_evaluation is null
            || !ReferenceEquals(_evaluationTransform, transform)
            || _evaluationNativeSize != document.NativeSize)
        {
            _evaluation = TransformEvaluator.Evaluate(transform, document.NativeSize);
            _evaluationTransform = transform;
            _evaluationNativeSize = document.NativeSize;
        }
        return _evaluation;
    }

    /// <summary>
    /// Single owner of the background render snapshot: pixel-copy + UI-thread-only swap/dispose is
    /// the safety contract (see ADR-0007). A frame superseded between the Changed event and this
    /// callback is skipped — the follow-up event rebuilds.
    /// Keyed on the document id alone: annotations composite at paint time and never invalidate
    /// the background, so an edit costs a redraw, not a re-upload (ADR-0008).
    /// </summary>
    private void RebuildSnapshot(Core.Documents.ImageDocument? document, bool preserveView = false)
    {
        if (document is null)
        {
            ResetAssetWarmQueue();
            SetSnapshot(null);
            _snapshotDocumentId = Guid.Empty;
            _snapshotSurfaceRevision = -1;
            _assetCache.Clear();
            return;
        }
        if (document.Id == _snapshotDocumentId
            && document.SurfaceRevision == _snapshotSurfaceRevision)
            return;

        try
        {
            if (document.Frame.IsDisposed)
                return;
            var image = document.Frame.ToSKImage();
            ResetAssetWarmQueue();
            _assetCache.Clear();
            SetSnapshot(image);
            _snapshotDocumentId = document.Id;
            _snapshotSurfaceRevision = document.SurfaceRevision;
            // Content space is the transform *output* canvas (identity on a fresh source = native
            // size, not the possibly-reduced frame): Fit/ActualSize work on the document's real
            // dimensions, and edit coordinates are decode-resolution-independent.
            if (preserveView)
            {
                _transform.UpdateContentSize(document.NativeSize.Width, document.NativeSize.Height);
            }
            else
            {
                _transform.SetContent(document.NativeSize.Width, document.NativeSize.Height);
                _fitPending = true;
                _selectedAnnotation = default;
            }
            if (_firstPaintWatch is null)
                Title = BuildWindowTitle();
        }
        catch (ObjectDisposedException)
        {
            // Superseded mid-copy; next Changed event carries the live document.
        }
    }

    private void UpdateStatusBar()
    {
        StatusPosition.Text = _viewModel.PositionText;
        StatusPage.Text = _viewModel.PageText;
        StatusDimensions.Text = _viewModel.DimensionsText;
        StatusFormat.Text = _viewModel.FormatText;
        StatusColorMode.Text = _viewModel.ColorModeText;
        StatusFileSize.Text = _viewModel.FileSizeText;
        StatusModified.Text = _viewModel.ModifiedText;
        SetStatusState(_cropInteraction.Phase == CropInteractionPhase.Reviewing
            ? AppStrings.CropReviewHint
            : string.IsNullOrEmpty(_viewModel.DiagnosticsText)
                ? _viewModel.StateText
                : $"{_viewModel.StateText} · {_viewModel.DiagnosticsText}");
        StatusZoom.Text = $"{_transform.Scale * 100:0}%";
        PreviousButton.IsEnabled = _viewModel.CanOpenPrevious;
        NextButton.IsEnabled = _viewModel.CanOpenNext;
        PreviousPageButton.IsEnabled = _viewModel.CanOpenPreviousPage;
        NextPageButton.IsEnabled = _viewModel.CanOpenNextPage;
        StatusProgress.IsActive = _viewModel.IsBusy;
        _updatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(_transform.Scale * 100f, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingZoomSlider = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusPosition, StatusPosition.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusPage, StatusPage.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StatusColorMode, $"{AppStrings.StatusColorMode}: {StatusColorMode.Text}");
        UpdateDynamicTooltips();
    }

    private string BuildWindowTitle() => AppServices.IsSafeMode
        ? $"{_viewModel.BuildTitle()} — {AppStrings.SafeModeLabel}"
        : _viewModel.BuildTitle();

    private void SetStatusState(string text)
    {
        var changed = !string.Equals(StatusState.Text, text, StringComparison.Ordinal);
        StatusState.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusState, text);
        if (!changed)
            return;

        var peer = FrameworkElementAutomationPeer.FromElement(StatusState)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusState);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.MostRecent,
            text,
            "StatusState");
    }

    private void UpdateOverlay()
    {
        var failed = _viewModel.Session.State == SessionState.Failed;
        OverlayMessage.Text = failed ? _viewModel.StateText : "";
        OverlayMessage.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- painting ----

    private void OnPaintSurface(object sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var viewport = canvas.DeviceClipBounds;
        DrawBackground(canvas, viewport.Width, viewport.Height);

        if (_snapshot is null)
            return;
        // The composite needs the editor's transform for this exact document; between a session
        // Changed event and the UI-thread callback they can lag one another — skip, the follow-up
        // event repaints.
        if (_viewModel.Editor.Document is not { } document || document.Id != _snapshotDocumentId)
            return;

        _transform.SetViewport(viewport.Width, viewport.Height);
        if (_fitPending)
        {
            _transform.FitToViewport();
            _fitPending = false;
            DispatcherQueue.TryEnqueue(UpdateStatusBar);
        }

        var viewMatrix = canvas.TotalMatrix.PreConcat(_transform.ToViewMatrix());
        DocumentComposite.Render(
            canvas, _snapshot, document.NativeSize, _viewModel.Editor.State,
            Evaluation(document), viewMatrix, _selectedAnnotation, _assetCache);
        DrawPendingAnnotation(canvas, viewMatrix);
        DrawSelectionBand(canvas, viewMatrix);
        DrawCropOverlay(canvas, viewMatrix);

        if (_firstPaintWatch is { IsRunning: true } && _viewModel.Session.State == SessionState.Ready)
        {
            _firstPaintWatch.Stop();
            DispatcherQueue.TryEnqueue(MaybeWriteUnattendedResult);
        }
    }

    /// <summary>Paints the native-space authoring draft without entering document history.</summary>
    private void DrawPendingAnnotation(SKCanvas canvas, SKMatrix viewMatrix)
    {
        if (_viewModel.Editor.Document is not { } document)
            return;
        var nativeToView = viewMatrix.PreConcat(
            DocumentComposite.ToSKMatrix(Evaluation(document).NativeToOutput));
        if (_draftTool is CanvasTool.Pen or CanvasTool.Highlighter && _inkPoints.Count > 0)
        {
            AnnotationRendering.DrawInkDraft(
                canvas, _inkPoints, nativeToView,
                _drawStrokeColor, _drawStrokeWidth, _drawOpacity);
            return;
        }
        Annotation? draft = _draftTool switch
        {
            CanvasTool.Line or CanvasTool.Arrow
                when _drawAnchor is { } lineStart && _drawCurrent is { } lineEnd =>
                new LineAnnotation
                {
                    Id = Guid.Empty,
                    Start = new AnnotationPoint(lineStart.X, lineStart.Y),
                    End = new AnnotationPoint(lineEnd.X, lineEnd.Y),
                    EndArrowhead = _draftTool == CanvasTool.Arrow
                        ? _drawArrowhead
                        : ArrowheadKind.None,
                    StrokeArgb = _drawStrokeColor,
                    StrokeWidth = _drawStrokeWidth,
                    Opacity = _drawOpacity,
                },
            CanvasTool.Rectangle or CanvasTool.RoundedRectangle or CanvasTool.Ellipse
                when _drawAnchor is { } shapeStart && _drawCurrent is { } shapeEnd =>
                new RectangleAnnotation
                {
                    Id = Guid.Empty,
                    Bounds = RectF.FromCorners(shapeStart.X, shapeStart.Y, shapeEnd.X, shapeEnd.Y),
                    Shape = ShapeFromTool(_draftTool),
                    StrokeArgb = _drawStrokeColor,
                    StrokeWidth = _drawStrokeWidth,
                    FillArgb = _drawFillEnabled ? _drawStrokeColor : null,
                    CornerRadius = _drawCornerRadius,
                    Opacity = _drawOpacity,
                },
            CanvasTool.Number when _drawAnchor is { } markerStart && _drawCurrent is { } markerEnd =>
                new NumberMarkerAnnotation
                {
                    Id = Guid.Empty,
                    Bounds = RectF.FromCorners(markerStart.X, markerStart.Y, markerEnd.X, markerEnd.Y),
                    Number = NextMarkerNumber(),
                    FillArgb = _drawStrokeColor,
                    FontSize = _drawFontSize,
                    Opacity = _drawOpacity,
                },
            CanvasTool.Mosaic or CanvasTool.Blur or CanvasTool.Mask
                when _drawAnchor is { } protectStart && _drawCurrent is { } protectEnd =>
                new ProtectionAnnotation
                {
                    Id = Guid.Empty,
                    Bounds = RectF.FromCorners(protectStart.X, protectStart.Y, protectEnd.X, protectEnd.Y),
                    Kind = ProtectionKindFromTool(_draftTool),
                    BlockSize = _drawBlockSize,
                    BlurSigma = _drawBlurSigma,
                    MaskArgb = _drawMaskColor,
                },
            _ => null,
        };
        if (draft is not null)
        {
            // The snapshot rides along so a protection draft previews its real effect.
            var frameToNative = _snapshot is null
                ? SKMatrix.Identity
                : SKMatrix.CreateScale(
                    document.NativeSize.Width / (float)_snapshot.Width,
                    document.NativeSize.Height / (float)_snapshot.Height);
            AnnotationRendering.DrawAnnotations(
                canvas,
                new DocumentState
                {
                    Layers = [new AnnotationLayer { Id = AnnotationLayer.InitialLayerId, Annotations = [draft] }],
                },
                nativeToView,
                backgroundFrame: _snapshot,
                frameToNative: frameToNative);
        }

        if (_draftTool != CanvasTool.Text
            || _drawAnchor is not { } anchor
            || _drawCurrent is not { } current)
            return;
        var bounds = RectF.FromCorners(anchor.X, anchor.Y, current.X, current.Y);
        using var builder = new SKPathBuilder();
        builder.MoveTo(nativeToView.MapPoint(bounds.X, bounds.Y));
        builder.LineTo(nativeToView.MapPoint(bounds.Right, bounds.Y));
        builder.LineTo(nativeToView.MapPoint(bounds.Right, bounds.Bottom));
        builder.LineTo(nativeToView.MapPoint(bounds.X, bounds.Bottom));
        builder.Close();
        using var quad = builder.Detach();
        using var pending = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(_drawStrokeColor).WithAlpha(0xB0),
            PathEffect = RubberBandDash,
        };
        canvas.DrawPath(quad, pending);
    }

    private void DrawSelectionBand(SKCanvas canvas, SKMatrix viewMatrix)
    {
        if (_selectionBandAnchor is not { } anchor || _selectionBandCurrent is not { } current
            || _viewModel.Editor.Document is not { } document)
            return;
        var nativeToView = viewMatrix.PreConcat(
            DocumentComposite.ToSKMatrix(Evaluation(document).NativeToOutput));
        var bounds = RectF.FromCorners(anchor.X, anchor.Y, current.X, current.Y);
        using var builder = new SKPathBuilder();
        builder.MoveTo(nativeToView.MapPoint(bounds.X, bounds.Y));
        builder.LineTo(nativeToView.MapPoint(bounds.Right, bounds.Y));
        builder.LineTo(nativeToView.MapPoint(bounds.Right, bounds.Bottom));
        builder.LineTo(nativeToView.MapPoint(bounds.X, bounds.Bottom));
        builder.Close();
        using var path = builder.Detach();
        using var paint = new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xC0),
            PathEffect = RubberBandDash,
        };
        canvas.DrawPath(path, paint);
    }

    /// <summary>Crop draft: dims the output canvas outside the candidate region. The constrained
    /// review bounds are stored unchanged for commit, so the overlay exactly matches the CropOp.</summary>
    private void DrawCropOverlay(SKCanvas canvas, SKMatrix viewMatrix)
    {
        if (_viewModel.Editor.Document is not { } document)
            return;
        var canvasSize = Evaluation(document).OutputSize;
        if (_cropInteraction.GetPreview(
            CropRatios[_cropRatioIndex], canvasSize.Width, canvasSize.Height) is not { } draft)
            return;
        var rect = SKRect.Create(draft.X, draft.Y, draft.Width, draft.Height);
        if (rect.Width < 1f || rect.Height < 1f)
            return;

        using var cropBuilder = new SKPathBuilder();
        cropBuilder.MoveTo(viewMatrix.MapPoint(rect.Left, rect.Top));
        cropBuilder.LineTo(viewMatrix.MapPoint(rect.Right, rect.Top));
        cropBuilder.LineTo(viewMatrix.MapPoint(rect.Right, rect.Bottom));
        cropBuilder.LineTo(viewMatrix.MapPoint(rect.Left, rect.Bottom));
        cropBuilder.Close();
        using var cropPath = cropBuilder.Detach();

        using var canvasBuilder = new SKPathBuilder();
        canvasBuilder.MoveTo(viewMatrix.MapPoint(0f, 0f));
        canvasBuilder.LineTo(viewMatrix.MapPoint(canvasSize.Width, 0f));
        canvasBuilder.LineTo(viewMatrix.MapPoint(canvasSize.Width, canvasSize.Height));
        canvasBuilder.LineTo(viewMatrix.MapPoint(0f, canvasSize.Height));
        canvasBuilder.Close();
        using var outputPath = canvasBuilder.Detach();

        canvas.Save();
        canvas.ClipPath(outputPath, SKClipOperation.Intersect, antialias: false);
        canvas.ClipPath(cropPath, SKClipOperation.Difference, antialias: false);
        canvas.DrawColor(new SKColor(0x00, 0x00, 0x00, 0x80));
        canvas.Restore();

        using var border = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0),
        };
        canvas.DrawPath(cropPath, border);
    }

    private void DrawBackground(SKCanvas canvas, int width, int height)
    {
        _checkerShader ??= ViewerBackgroundRendering.CreateCheckerShader();
        ViewerBackgroundRendering.Draw(canvas, width, height, _checkerShader);
    }

    private void QueueMissingAssetWarms()
    {
        if (_viewModel.Editor.Document is not { } document)
            return;
        foreach (var asset in _viewModel.Editor.State.Assets)
        {
            if (_assetCache.Find(asset.Id) is null && _assetWarmPending.Add(asset.Id))
                _ = WarmAssetFromStateAsync(
                    asset, document.Id, _viewModel.Editor.Revision,
                    _assetWarmGeneration, _assetWarmCancellation.Token);
        }
    }

    private async Task WarmAssetFromStateAsync(
        RasterAsset asset, Guid documentId, long revision, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _assetCache.WarmAsync(asset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_viewModel.Editor.Document?.Id != documentId
                || _viewModel.Editor.Revision != revision
                || _viewModel.Editor.State.FindAsset(asset.Id) is null)
                _assetCache.Prune(_viewModel.Editor.State);
            Canvas.Invalidate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
        finally
        {
            if (generation == _assetWarmGeneration)
                _assetWarmPending.Remove(asset.Id);
        }
    }

    private void ResetAssetWarmQueue()
    {
        _assetWarmCancellation.Cancel();
        _assetWarmCancellation.Dispose();
        _assetWarmCancellation = new CancellationTokenSource();
        _assetWarmGeneration = checked(_assetWarmGeneration + 1);
        _assetWarmPending.Clear();
    }

    // ---- input ----

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => QueueCanvasResize();

    private void OnCanvasLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_observedXamlRoot, Canvas.XamlRoot))
        {
            DetachXamlRoot();
            _observedXamlRoot = Canvas.XamlRoot;
            if (_observedXamlRoot is not null)
                _observedXamlRoot.Changed += OnXamlRootChanged;
        }
        QueueCanvasResize();
    }

    private void OnCanvasUnloaded(object sender, RoutedEventArgs e) => DetachXamlRoot();

    private void DetachXamlRoot()
    {
        if (_observedXamlRoot is not null)
            _observedXamlRoot.Changed -= OnXamlRootChanged;
        _observedXamlRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        QueueCanvasResize();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange)
            QueueCanvasResize();
    }

    private void QueueCanvasResize()
    {
        Canvas.Invalidate();
        if (_canvasResizeSettleTimer is { } timer)
        {
            Canvas.EnableRenderLoop = true;
            timer.Stop();
            timer.Start();
        }
        var generation = ++_canvasResizeGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _canvasResizeGeneration)
                return;
            Canvas.UpdateLayout();
            Canvas.Invalidate();
        });
    }

    private void OnCanvasResizeSettled(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        Canvas.EnableRenderLoop = false;
        Canvas.Invalidate();
        QueueScaleDependentRerender();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(Canvas);
        if (!currentPoint.Properties.IsLeftButtonPressed || _activePointerId is not null)
            return;
        if (!Canvas.CapturePointer(e.Pointer))
            return;

        _activePointerId = e.Pointer.PointerId;
        var point = currentPoint.Position;
        _lastPointer = new SKPoint((float)point.X, (float)point.Y);

        // Space is the pan override even mid-tool (FR-VIEW-004), so it wins over any edit gesture.
        // A replacement in flight also blocks editing: the pending swap would discard the edit unasked.
        if (_spaceHeld || _viewModel.Editor.Document is not { } document || _viewModel.IsReplacementPending)
            return;

        var device = DevicePoint(e);

        if (_tool == CanvasTool.Eyedropper)
        {
            var output = ToOutput(device);
            if (_snapshot is not null && DocumentPixelSampler.Sample(
                _snapshot, document.NativeSize, _viewModel.Editor.State,
                Evaluation(document), output.X, output.Y, _assetCache) is { } color)
            {
                _strokeColor = 0xFF00_0000u
                    | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
                UpdateColorSelection();
                SetStatusState($"{AppStrings.ColorSelected} #{_strokeColor & 0x00FF_FFFF:X6}");
            }
            return;
        }

        if (_tool == CanvasTool.Crop)
        {
            var output = ToOutput(device);
            var canvasSize = Evaluation(document).OutputSize;
            if (output.X < 0f || output.Y < 0f || output.X > canvasSize.Width || output.Y > canvasSize.Height)
                return;
            BeginGesture(document);
            _cropInteraction.BeginDrag(output.X, output.Y, document.Id, _viewModel.Editor.Revision);
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }

        if (_tool != CanvasTool.Select)
        {
            // Blocked-layer feedback happens at press: no phantom draft that vanishes on commit.
            if (!CanEditActiveLayer())
                return;
            // Authoring starts on visible content only; the stroke may extend past the edge later.
            if (ToNativeVisible(device) is not { } drawStart)
                return;
            BeginGesture(document);
            CaptureDraftStyle();
            _draftTool = _tool;
            if (_tool is CanvasTool.Pen or CanvasTool.Highlighter)
            {
                _inkPoints.Clear();
                _inkPoints.Add(new AnnotationPoint(drawStart.X, drawStart.Y));
                _inkSimplifyTolerance = NativeDistanceForDevicePixels(device, 0.75f);
            }
            else
            {
                _drawAnchor = drawStart;
                _drawCurrent = drawStart;
            }
            Canvas.Invalidate();
            return;
        }

        if (SelectedAnnotation() is { IsLocked: false } selected
            && ToNative(device) is { } handlePoint)
        {
            var radius = NativeDistanceForDevicePixels(device, 8f);
            var offset = NativeDistanceForDevicePixels(device, 24f);
            var handle = SelectionGeometry.HitTest(
                selected, new AnnotationPoint(handlePoint.X, handlePoint.Y), radius, offset);
            // Protection regions never rotate (ADR-0015); the rotate affordance is inert for them.
            if (handle == SelectionHandle.Rotate && selected is ProtectionAnnotation)
                handle = SelectionHandle.None;
            if (handle != SelectionHandle.None)
            {
                BeginGesture(document);
                _activeSelectionHandle = handle;
                _selectionTransformOrigin = selected;
                _selectionTransformMoved = false;
                return;
            }
        }

        var visibleNative = ToNativeVisible(device);
        var hit = visibleNative is { } native
            ? _viewModel.Editor.State.HitTest(native.X, native.Y)
            : null;
        _selectedAnnotation = hit?.Id ?? default;
        if (hit is not null && ToNative(device) is { } start)
        {
            BeginGesture(document);
            _dragAnnotation = hit.Id;
            _dragOrigin = hit.Bounds;
            _dragStartNative = start;
            _dragMoved = false;
        }
        else
        {
            _dragAnnotation = default;
            _dragMoved = false;
            if (visibleNative is { } bandStart)
            {
                BeginGesture(document);
                _selectionBandAnchor = bandStart;
                _selectionBandCurrent = bandStart;
            }
        }
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        Canvas.Invalidate();
    }

    private void BeginGesture(Core.Documents.ImageDocument document)
    {
        _gestureId = ++_gestureCounter;
        _gestureDocumentId = document.Id;
        _gestureRevision = _viewModel.Editor.Revision;
    }

    /// <summary>The mutation funnel's identity check: the gesture may only touch the exact document
    /// and editor binding it started on, and never while a replacement is pending.</summary>
    private bool GestureStillValid() =>
        !_viewModel.IsReplacementPending
        && _viewModel.Editor.Document is { } document
        && document.Id == _gestureDocumentId
        && _viewModel.Editor.Revision == _gestureRevision;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId || _lastPointer is not { } last)
            return;

        var point = e.GetCurrentPoint(Canvas).Position;
        var current = new SKPoint((float)point.X, (float)point.Y);
        // Track the latest position on every path: a pan that follows a draw/drag move must measure
        // its delta from the previous frame, not from the press point, or the first pan frame jumps.
        _lastPointer = current;

        // FR-VIEW-004: Space+drag ALWAYS pans, even over an in-progress draft — the draft lives in
        // document space, so it survives the pan untouched and resumes when Space is released.
        if (!_spaceHeld)
        {
            if (_draftTool is CanvasTool.Pen or CanvasTool.Highlighter
                && _inkPoints.Count > 0)
            {
                if (ToNative(DevicePoint(e)) is { } inkPoint)
                {
                    var next = new AnnotationPoint(inkPoint.X, inkPoint.Y);
                    var previous = _inkPoints[^1];
                    var dx = next.X - previous.X;
                    var dy = next.Y - previous.Y;
                    var minimum = MathF.Max(_inkSimplifyTolerance * 0.25f, 0.01f);
                    if ((dx * dx) + (dy * dy) >= minimum * minimum)
                    {
                        if (_inkPoints.Count < AnnotationValidator.MaxInkPoints)
                            _inkPoints.Add(next);
                        else
                            _inkPoints[^1] = next;
                    }
                }
                Canvas.Invalidate();
                return;
            }

            if (_drawAnchor is not null)
            {
                if (ToNative(DevicePoint(e)) is { } native)
                    _drawCurrent = native;
                Canvas.Invalidate();
                return;
            }

            if (_cropInteraction.Phase == CropInteractionPhase.Dragging)
            {
                var output = ToOutput(DevicePoint(e));
                _cropInteraction.UpdateDrag(output.X, output.Y);
                Canvas.Invalidate();
                return;
            }

            if (_activeSelectionHandle != SelectionHandle.None)
            {
                TransformSelection(e);
                return;
            }

            if (_selectionBandAnchor is not null)
            {
                if (ToNative(DevicePoint(e)) is { } bandCurrent)
                    _selectionBandCurrent = bandCurrent;
                Canvas.Invalidate();
                return;
            }

            if (_dragAnnotation != default)
            {
                DragSelection(e);
                return;
            }

            return;
        }
        var scale = (float)Canvas.XamlRoot.RasterizationScale;
        _transform.Pan((current.X - last.X) * scale, (current.Y - last.Y) * scale);
        Canvas.Invalidate();
    }

    /// <summary>
    /// One drag = one history entry: the first move stacks a command, the rest rewrite it, so undo
    /// jumps back to where the object started rather than replaying every intermediate pixel (§7.8).
    /// </summary>
    private void DragSelection(PointerRoutedEventArgs e)
    {
        // Re-checked at the mutation, not just at press: a replacement can start — or complete —
        // mid-drag (e.g. an activation redirect), and a gesture begun on the predecessor must not
        // edit the successor.
        if (!GestureStillValid())
        {
            CancelActiveGesture();
            return;
        }
        if (ToNative(DevicePoint(e)) is not { } native)
            return;
        var bounds = _dragOrigin.Translated(native.X - _dragStartNative.X, native.Y - _dragStartNative.Y);
        var command = new MoveAnnotationCommand(_dragAnnotation, _dragOrigin, bounds, _gestureId);
        if (command.IsNoOp)
            return;

        if (_dragMoved)
            _viewModel.Editor.ApplyCoalesced(command);
        else
            _viewModel.Editor.Apply(command);
        _dragMoved = true;
    }

    private void TransformSelection(PointerRoutedEventArgs e)
    {
        if (!GestureStillValid() || _selectionTransformOrigin is not { } origin)
        {
            CancelActiveGesture();
            return;
        }
        if (ToNative(DevicePoint(e)) is not { } native)
            return;
        var point = new AnnotationPoint(native.X, native.Y);
        var next = _activeSelectionHandle == SelectionHandle.Rotate
            ? SelectionGeometry.Rotate(origin, point)
            : SelectionGeometry.Resize(origin, _activeSelectionHandle, point);
        if (Equals(origin, next))
            return;
        var command = new ReplaceAnnotationCommand(
            AnnotationEditKind.Geometry, origin, next, _gestureId);
        if (_selectionTransformMoved)
            _viewModel.Editor.ApplyCoalesced(command);
        else
            _viewModel.Editor.Apply(command);
        _selectionTransformMoved = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
            return;

        var pendingText = CommitPendingAnnotation();
        CompleteCropDrag();
        if (_selectionBandAnchor is { } anchor && _selectionBandCurrent is { } current
            && GestureStillValid())
        {
            var bounds = RectF.FromCorners(anchor.X, anchor.Y, current.X, current.Y);
            _selectedAnnotation = bounds.Width >= 1f && bounds.Height >= 1f
                ? _viewModel.Editor.State.HitTest(bounds)?.Id ?? default
                : default;
        }
        _selectionBandAnchor = null;
        _selectionBandCurrent = null;
        _activeSelectionHandle = SelectionHandle.None;
        _selectionTransformOrigin = null;
        _selectionTransformMoved = false;
        _dragAnnotation = default;
        _dragMoved = false;
        _lastPointer = null;
        _activePointerId = null;
        Canvas.ReleasePointerCapture(e.Pointer);
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        if (pendingText is { } text)
            _ = ShowTextDialogAndCommitAsync(text);
    }

    /// <summary>
    /// Capture loss (touch cancel, window deactivation, a modal opened mid-gesture) means Released
    /// may never fire: abandon the gesture, or bare hover moves keep mutating and a stale draft
    /// would be committed by an unrelated later release.
    /// </summary>
    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
            return;

        _drawAnchor = null;
        _drawCurrent = null;
        _inkPoints.Clear();
        _draftTool = CanvasTool.Select;
        _cropInteraction.CancelDrag();
        _dragAnnotation = default;
        _dragMoved = false;
        _activeSelectionHandle = SelectionHandle.None;
        _selectionTransformOrigin = null;
        _selectionTransformMoved = false;
        _selectionBandAnchor = null;
        _selectionBandCurrent = null;
        _activePointerId = null;
        _lastPointer = null;
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    /// <summary>Turns one native-space draft into one history entry.</summary>
    private PendingText? CommitPendingAnnotation()
    {
        var anchor = _drawAnchor;
        var current = _drawCurrent;
        var points = _inkPoints.ToArray();
        var tool = _draftTool;
        _drawAnchor = null;
        _drawCurrent = null;
        _inkPoints.Clear();
        _draftTool = CanvasTool.Select;
        if (tool == CanvasTool.Select)
            return null;

        if (!GestureStillValid())
        {
            Canvas.Invalidate();
            return null;
        }

        Annotation? annotation = null;
        if (tool is CanvasTool.Pen or CanvasTool.Highlighter)
        {
            var simplified = InkSimplifier.Simplify(points, _inkSimplifyTolerance);
            if (!simplified.IsDefaultOrEmpty)
            {
                annotation = new InkAnnotation
                {
                    Id = Guid.NewGuid(),
                    Points = simplified,
                    Kind = tool == CanvasTool.Highlighter ? InkKind.Highlighter : InkKind.Pen,
                    StrokeArgb = _drawStrokeColor,
                    StrokeWidth = _drawStrokeWidth,
                    Opacity = _drawOpacity,
                };
            }
        }
        else if (anchor is { } a && current is { } b)
        {
            var bounds = RectF.FromCorners(a.X, a.Y, b.X, b.Y);
            switch (tool)
            {
                case CanvasTool.Line:
                case CanvasTool.Arrow:
                    if (DistanceSquared(a, b) >= 1f)
                    {
                        annotation = new LineAnnotation
                        {
                            Id = Guid.NewGuid(),
                            Start = new AnnotationPoint(a.X, a.Y),
                            End = new AnnotationPoint(b.X, b.Y),
                            EndArrowhead = tool == CanvasTool.Arrow
                                ? _drawArrowhead
                                : ArrowheadKind.None,
                            StrokeArgb = _drawStrokeColor,
                            StrokeWidth = _drawStrokeWidth,
                            Opacity = _drawOpacity,
                        };
                    }
                    break;
                case CanvasTool.Rectangle:
                case CanvasTool.RoundedRectangle:
                case CanvasTool.Ellipse:
                    if (bounds.Width >= 1f && bounds.Height >= 1f)
                    {
                        annotation = new RectangleAnnotation
                        {
                            Id = Guid.NewGuid(),
                            Bounds = bounds,
                            Shape = ShapeFromTool(tool),
                            StrokeArgb = _drawStrokeColor,
                            StrokeWidth = _drawStrokeWidth,
                            FillArgb = _drawFillEnabled ? _drawStrokeColor : null,
                            CornerRadius = _drawCornerRadius,
                            Opacity = _drawOpacity,
                        };
                    }
                    break;
                case CanvasTool.Text:
                    if (bounds.Width < 1f || bounds.Height < 1f)
                        bounds = DefaultNativeBounds(a, 240f, 60f);
                    return new PendingText(
                        bounds, _drawStrokeColor, _drawOpacity, _drawFontSize,
                        _drawFontFamily, _drawFontBold, _drawFontItalic,
                        _drawTextAlignment,
                        _drawTextBackgroundEnabled ? 0xCCFF_FFFF : null,
                        _gestureDocumentId, _gestureRevision);
                case CanvasTool.Mosaic:
                case CanvasTool.Blur:
                case CanvasTool.Mask:
                    if (bounds.Width >= 1f && bounds.Height >= 1f)
                    {
                        annotation = new ProtectionAnnotation
                        {
                            Id = Guid.NewGuid(),
                            Bounds = bounds,
                            Kind = ProtectionKindFromTool(tool),
                            BlockSize = _drawBlockSize,
                            BlurSigma = _drawBlurSigma,
                            MaskArgb = _drawMaskColor,
                        };
                    }
                    break;
                case CanvasTool.Number:
                    if (bounds.Width < 1f || bounds.Height < 1f)
                        bounds = DefaultNativeBounds(a, 36f, 36f);
                    if (AnnotationNumbering.TryGetNextMarkerNumber(
                        _viewModel.Editor.State.Annotations, out var number))
                    {
                        annotation = new NumberMarkerAnnotation
                        {
                            Id = Guid.NewGuid(),
                            Bounds = bounds,
                            Number = number,
                            FillArgb = _drawStrokeColor,
                            FontSize = _drawFontSize,
                            Opacity = _drawOpacity,
                        };
                    }
                    else
                    {
                        SetStatusState($"{AppStrings.EditFailed}: {AppStrings.MarkerLimitReached}");
                    }
                    break;
            }
        }

        if (annotation is not null && CanEditActiveLayer())
        {
            _viewModel.Editor.Apply(new AddAnnotationCommand(annotation, _activeLayerId));
            _selectedAnnotation = annotation.Id;
        }
        Canvas.Invalidate();
        return null;
    }

    private async Task ShowTextDialogAndCommitAsync(PendingText pending)
    {
        if (Content?.XamlRoot is null)
            return;
        var textBox = new TextBox
        {
            Header = AppStrings.TextContentLabel,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            Height = 160,
            MaxLength = AnnotationValidator.MaxTextLength,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.TextTitle,
            Content = textBox,
            PrimaryButtonText = AppStrings.DialogApply,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog, editScoped: true) != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(textBox.Text))
            return;
        if (_viewModel.IsReplacementPending
            || _viewModel.Editor.Document is not { } document
            || document.Id != pending.DocumentId
            || _viewModel.Editor.Revision != pending.Revision)
            return;

        var annotation = new TextAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = pending.Bounds,
            Text = textBox.Text,
            FontFamily = pending.FontFamily,
            ForegroundArgb = pending.Color,
            FontSize = pending.FontSize,
            IsBold = pending.IsBold,
            IsItalic = pending.IsItalic,
            Alignment = pending.Alignment,
            BackgroundArgb = pending.BackgroundArgb,
            Opacity = pending.Opacity,
        };
        if (!CanEditActiveLayer())
            return;
        _viewModel.Editor.Apply(new AddAnnotationCommand(annotation, _activeLayerId));
        _selectedAnnotation = annotation.Id;
    }

    private async void OnEditSelectedTextClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not TextAnnotation { IsLocked: false } before
            || _viewModel.Editor.Document is not { } document
            || Content?.XamlRoot is null)
            return;
        var documentId = document.Id;
        var revision = _viewModel.Editor.Revision;
        var textBox = new TextBox
        {
            Header = AppStrings.TextContentLabel,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            Height = 160,
            MaxLength = AnnotationValidator.MaxTextLength,
            Text = before.Text,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.TextEditTitle,
            Content = textBox,
            PrimaryButtonText = AppStrings.DialogApply,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            if (await ShowDialogAsync(dialog, editScoped: true) != ContentDialogResult.Primary
                || string.IsNullOrWhiteSpace(textBox.Text)
                || _viewModel.IsReplacementPending
                || _viewModel.Editor.Document is not { } target
                || target.Id != documentId
                || _viewModel.Editor.Revision != revision
                || !Equals(_viewModel.Editor.State.Find(before.Id), before))
                return;
            ApplySelectedEdit(AnnotationEditKind.Content, before with { Text = textBox.Text });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
    }

    private void CaptureDraftStyle()
    {
        _drawStrokeColor = _strokeColor;
        _drawStrokeWidth = _strokeWidth;
        _drawOpacity = _opacity;
        _drawFontSize = _fontSize;
        _drawFillEnabled = _fillEnabled;
        _drawCornerRadius = _cornerRadius;
        _drawArrowhead = _arrowhead;
        _drawFontFamily = _fontFamily;
        _drawFontBold = _fontBold;
        _drawFontItalic = _fontItalic;
        _drawTextAlignment = _textAlignment;
        _drawTextBackgroundEnabled = _textBackgroundEnabled;
        _drawBlockSize = _mosaicBlockSize;
        _drawBlurSigma = _blurSigma;
        _drawMaskColor = _maskColor;
    }

    private static ProtectionKind ProtectionKindFromTool(CanvasTool tool) => tool switch
    {
        CanvasTool.Mosaic => ProtectionKind.Mosaic,
        CanvasTool.Blur => ProtectionKind.Blur,
        _ => ProtectionKind.Mask,
    };

    private float NativeDistanceForDevicePixels(SKPoint device, float pixels)
    {
        if (ToNative(device) is not { } origin)
            return pixels;
        var x = ToNative(new SKPoint(device.X + pixels, device.Y));
        var y = ToNative(new SKPoint(device.X, device.Y + pixels));
        var xDistance = x is { } xp
            ? MathF.Sqrt(DistanceSquared(origin, xp))
            : 0f;
        var yDistance = y is { } yp
            ? MathF.Sqrt(DistanceSquared(origin, yp))
            : 0f;
        return MathF.Max(MathF.Max(xDistance, yDistance), 1e-4f);
    }

    private RectF DefaultNativeBounds(SKPoint anchor, float deviceWidth, float deviceHeight)
    {
        if (_viewModel.Editor.Document is not { } document)
            return new RectF(anchor.X, anchor.Y, deviceWidth, deviceHeight);
        var nativeToDevice = _transform.ToViewMatrix().PreConcat(
            DocumentComposite.ToSKMatrix(Evaluation(document).NativeToOutput));
        var device = nativeToDevice.MapPoint(anchor);
        var right = ToNative(new SKPoint(device.X + deviceWidth, device.Y));
        var bottom = ToNative(new SKPoint(device.X, device.Y + deviceHeight));
        return new RectF(
            anchor.X,
            anchor.Y,
            MathF.Max(1f, right is { } r
                ? MathF.Sqrt(DistanceSquared(anchor, r))
                : deviceWidth),
            MathF.Max(1f, bottom is { } b
                ? MathF.Sqrt(DistanceSquared(anchor, b))
                : deviceHeight));
    }

    private int NextMarkerNumber()
    {
        return AnnotationNumbering.TryGetNextMarkerNumber(
            _viewModel.Editor.State.Annotations, out var number)
            ? number
            : int.MaxValue;
    }

    private static ShapeKind ShapeFromTool(CanvasTool tool) => tool switch
    {
        CanvasTool.RoundedRectangle => ShapeKind.RoundedRectangle,
        CanvasTool.Ellipse => ShapeKind.Ellipse,
        _ => ShapeKind.Rectangle,
    };

    private static float DistanceSquared(SKPoint first, SKPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>A release produces a review draft; only an explicit confirmation mutates history.</summary>
    private void CompleteCropDrag()
    {
        if (_cropInteraction.Phase != CropInteractionPhase.Dragging)
            return;
        if (!GestureStillValid())
        {
            _cropInteraction.CancelDrag();
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }

        var canvasSize = Evaluation(_viewModel.Editor.Document!).OutputSize;
        _cropInteraction.CompleteDrag(
            CropRatios[_cropRatioIndex], canvasSize.Width, canvasSize.Height);
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    private bool TryCommitCropReviewFromKeyboard() =>
        !_colorFlyoutOpen && _activeDialog is null && TryCommitCropReview();

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_colorFlyoutOpen || _activeDialog is not null || _cropInteraction.Review is not { } review)
            return;

        var point = e.GetPosition(Canvas);
        var scale = (float)Canvas.XamlRoot.RasterizationScale;
        var output = ToOutput(new SKPoint((float)point.X * scale, (float)point.Y * scale));
        if (!review.Contains(output.X, output.Y))
            return;

        e.Handled = TryCommitCropReview();
    }

    private bool TryCommitCropReview()
    {
        if (_cropInteraction.Review is not { } review)
            return false;
        if (_viewModel.IsReplacementPending
            || _viewModel.Editor.Document is not { } document
            || document.Id != review.DocumentId
            || _viewModel.Editor.Revision != review.Revision)
        {
            _cropInteraction.CancelAll();
            Canvas.Invalidate();
            UpdateStatusBar();
            return false;
        }

        if (!ApplyTransformOp(TransformEditKind.Crop, new CropOp(review.Bounds)))
            return false;

        SetTool(CanvasTool.Select);
        return true;
    }

    private SKPoint DevicePoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas).Position;
        var scale = (float)Canvas.XamlRoot.RasterizationScale;
        return new SKPoint((float)point.X * scale, (float)point.Y * scale);
    }

    /// <summary>Device pixels → output-canvas pixels (the view's content space).</summary>
    private SKPoint ToOutput(SKPoint devicePoint) => _transform.ViewToContent(devicePoint);

    /// <summary>Device pixels → native source pixels, unbounded: drag deltas keep working when the
    /// pointer leaves the canvas, and an authored stroke may extend past the visible edge.</summary>
    private SKPoint? ToNative(SKPoint devicePoint)
    {
        if (_viewModel.Editor.Document is not { } document)
            return null;
        if (!Evaluation(document).TryGetOutputToNative(out var inverse))
            return null;
        var output = ToOutput(devicePoint);
        var native = Vector2.Transform(new Vector2(output.X, output.Y), inverse);
        return new SKPoint(native.X, native.Y);
    }

    /// <summary>
    /// Device pixels → native pixels only when the point lands on visible content: inside the
    /// output canvas AND inside the source clip. Cheap rejection happens before the inverse
    /// hit-test, so the transparent corners of a rotated canvas and cropped-away regions hit
    /// nothing (ADR-0009).
    /// </summary>
    private SKPoint? ToNativeVisible(SKPoint devicePoint)
    {
        if (_viewModel.Editor.Document is not { } document)
            return null;
        var evaluation = Evaluation(document);
        var output = ToOutput(devicePoint);
        if (output.X < 0f || output.Y < 0f
            || output.X > evaluation.OutputSize.Width || output.Y > evaluation.OutputSize.Height)
            return null;
        if (!evaluation.TryGetOutputToNative(out var inverse))
            return null;
        var native = Vector2.Transform(new Vector2(output.X, output.Y), inverse);
        return evaluation.ContainsNativePoint(native.X, native.Y) ? new SKPoint(native.X, native.Y) : null;
    }

    private bool ContentOverflowsViewport()
    {
        var rotated = _transform.RotatedContentSize;
        return rotated.Width * _transform.Scale > _transform.Viewport.Width + 0.5f
            || rotated.Height * _transform.Scale > _transform.Viewport.Height + 0.5f;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        var factor = point.Properties.MouseWheelDelta > 0 ? 1.1f : 1f / 1.1f;
        var scale = (float)Canvas.XamlRoot.RasterizationScale;
        _transform.ZoomAt(new SKPoint((float)point.Position.X * scale, (float)point.Position.Y * scale), factor);
        QueueScaleDependentRerender();
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    // ---- commands ----

    private async void OnOpenClicked(object sender, RoutedEventArgs e) => await OpenPickerAsync();
    private async void OnClipboardClicked(object sender, RoutedEventArgs e) => await OpenFromClipboardAsync();

    private async void OnCaptureClicked(object sender, RoutedEventArgs e) =>
        await (AppServices.Capture?.RequestCaptureAsync(this) ?? Task.CompletedTask);

    // ---- capture notification (FR-CAP-003; payload captured at detection time) ----

    private Capture.Clipboard.ClipboardImagePayload? _pendingCaptureNotice;

    /// <summary>Called by the coordinator on the UI thread for unsolicited captures.</summary>
    public void ShowCaptureNotice(Capture.Clipboard.ClipboardImagePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _pendingCaptureNotice = payload;
        CaptureBar.Title = AppStrings.CaptureNoticeTitle;
        CaptureOpenButton.Content = AppStrings.CaptureNoticeOpen;
        CaptureBar.IsOpen = true;
    }

    /// <summary>Transient status text from outside the window (capture failures).</summary>
    public void ShowTransientStatus(string text) => SetStatusState(text);

    /// <summary>Out of the shot while the snip overlay is up; every completion path restores
    /// through <see cref="Capture.Snipping.ICaptureTarget.Activate"/>.</summary>
    public void PrepareForCapture()
    {
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Minimize();
    }

    /// <summary>Coordinator activation must also restore a window it minimized for capture —
    /// Window.Activate alone does not un-minimize. After a capture the foreground belongs to
    /// the overlay/another app, so plain activation is denied. The window goes TOPMOST and
    /// stays there until the overlay teardown re-activates the previous app (which would
    /// otherwise jump back above a non-topmost window), then drops out of the topmost band.</summary>
    void Capture.Snipping.ICaptureTarget.Activate()
    {
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMoveNoSizeShow);
        StealForeground(hwnd);
        Activate();
        ScheduleTopmostRelease();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topmostReleaseTimer;
    // Long enough for the snip overlay teardown to finish re-activating the previous app.
    private static readonly TimeSpan TopmostHold = TimeSpan.FromMilliseconds(600);

    private void ScheduleTopmostRelease()
    {
        if (_topmostReleaseTimer is null)
        {
            _topmostReleaseTimer = DispatcherQueue.CreateTimer();
            _topmostReleaseTimer.IsRepeating = false;
            _topmostReleaseTimer.Interval = TopmostHold;
            _topmostReleaseTimer.Tick += (_, _) =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (GetForegroundWindow() != hwnd)
                    StealForeground(hwnd);
                SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMoveNoSizeShow);
            };
        }
        _topmostReleaseTimer.Stop();
        _topmostReleaseTimer.Start();
    }

    /// <summary>SetForegroundWindow is denied to a process that lost foreground during capture;
    /// briefly attaching to the current foreground thread's input queue lifts that lock.</summary>
    private static void StealForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
            return;
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, 0);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(foregroundThread, currentThread, true);
        SetForegroundWindow(hwnd);
        if (attached)
            AttachThreadInput(foregroundThread, currentThread, false);
    }

    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;
    private const uint SwpNoMoveNoSizeShow = 0x0001 | 0x0002 | 0x0040; // NOSIZE | NOMOVE | SHOWWINDOW

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, nint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private void OnCaptureOpenClicked(object sender, RoutedEventArgs e) => OpenPendingCaptureNotice();

    private void OnCaptureBarClosed(InfoBar sender, InfoBarClosedEventArgs args) =>
        _pendingCaptureNotice = null;

    private void OpenPendingCaptureNotice()
    {
        if (_pendingCaptureNotice is { } payload)
        {
            // The replacement still walks the unsaved-edit gate; the notice never bypasses it.
            _viewModel.OpenClipboardBytes(payload.Bytes, payload.Format);
        }
        _pendingCaptureNotice = null;
        CaptureBar.IsOpen = false;
    }

    private async void OnCheckForUpdatesRequested(object? sender, EventArgs e)
    {
        try
        {
            if (await Launcher.LaunchUriAsync(ReleaseDistributionPolicy.LatestReleasePage))
                return;
            ReportReleasePageLaunchFailure();
        }
        catch (Exception ex)
        {
            ReportReleasePageLaunchFailure(ex);
        }
    }

    private void ReportReleasePageLaunchFailure(Exception? exception = null)
    {
        SetStatusState(AppStrings.UpdateOpenFailed);
        _ = AppServices.Logs.TryEnqueue(
            LocalLogLevel.Warning,
            new StructuredLogEvent
            {
                Name = StructuredLogEventNames.ReleasePageLaunchFailed,
                ErrorCode = "shell_launch_failed",
            },
            exception);
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e) => await SaveAsync(quick: true);
    private void OnNewWindowClicked(object sender, RoutedEventArgs e) => AppServices.Windows?.OpenNewWindow();
    private void OnPreviousClicked(object sender, RoutedEventArgs e) => _viewModel.OpenPrevious();
    private void OnNextClicked(object sender, RoutedEventArgs e) => _viewModel.OpenNext();
    private async void OnPreviousPageClicked(object sender, RoutedEventArgs e) =>
        await SwitchPageAsync(-1);
    private async void OnNextPageClicked(object sender, RoutedEventArgs e) =>
        await SwitchPageAsync(1);
    private void OnAnimationPlaybackClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Session.Current?.SequenceKind != DocumentSequenceKind.Animation)
            return;
        _animationPausedByUser = !_animationPausedByUser;
        ConfigureAnimationPlayback();
    }
    private void OnFitClicked(object sender, RoutedEventArgs e) => FitToViewport();
    private void OnActualSizeClicked(object sender, RoutedEventArgs e) => ActualSize();
    private void OnFullScreenClicked(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => ZoomAtCenter(0.8f);
    private void OnZoomInClicked(object sender, RoutedEventArgs e) => ZoomAtCenter(1.25f);

    private async Task SwitchPageAsync(int direction)
    {
        if (_savingInProgress || _viewModel.IsMutationBlocked)
            return;
        if (_viewModel.Session.Current is not { SequenceKind: DocumentSequenceKind.Pages } document)
            return;
        var target = document.CurrentFrameIndex + direction;
        if (target < 0 || target >= document.FrameCount)
            return;

        _pageActiveLayers[document.CurrentFrameIndex] = _activeLayerId;
        _viewModel.SetPageActiveLayerId(document.CurrentFrameIndex, _activeLayerId);
        _scaleRenderTimer.Stop();
        _scaleRenderCancellation?.Cancel();
        CancelActiveGesture();
        CancelEditDialog();
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        try
        {
            if (!await _viewModel.OpenPageAsync(target, _shutdownCts.Token))
                return;
            var projectLayer = _viewModel.GetPageActiveLayerId(target);
            _activeLayerId = _pageActiveLayers.TryGetValue(target, out var restored)
                && _viewModel.Editor.State.FindLayer(restored) is not null
                    ? restored
                    : projectLayer is { } stored
                        && _viewModel.Editor.State.FindLayer(stored) is not null
                            ? stored
                    : _viewModel.Editor.State.Layers[^1].Id;
            RebuildSnapshot(document);
            QueueScaleDependentRerender();
            _viewModel.RefreshStatus();
            UpdateStatusBar();
            UpdateLayerPanel();
            UpdateToolUi();
            UpdateEditCommands();
            Canvas.Invalidate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ImageRejectedException or IOException)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
        finally
        {
            UpdateStatusBar();
        }
    }

    private void ConfigureAnimationPlayback()
    {
        _animationTimer.Stop();
        var document = _viewModel.Session.Current;
        if (document is not
            { SequenceKind: DocumentSequenceKind.Animation, FrameCount: > 1 } animation)
        {
            AnimationPlaybackButton.Visibility = Visibility.Collapsed;
            return;
        }
        AnimationPlaybackButton.Visibility = Visibility.Visible;
        var isPlaying = _animationsEnabled
            && !_animationPausedByUser
            && !_animationConfirmationPending;
        AnimationPlaybackIcon.IconSource = IconSourceFor(
            isPlaying ? "Icon.View.Pause" : "Icon.View.Play");
        var action = isPlaying ? AppStrings.ToolPauseAnimation : AppStrings.ToolPlayAnimation;
        SetTip(AnimationPlaybackButton, action, action);
        AnimationPlaybackButton.IsEnabled = _animationsEnabled && !_animationConfirmationPending;
        if (!isPlaying)
            return;

        var delay = animation.Frames[animation.CurrentFrameIndex].Duration;
        _animationTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(100);
        _animationTimer.Start();
    }

    private async void OnAnimationTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (_animationTickInProgress)
            return;
        _animationTickInProgress = true;
        var document = _viewModel.Session.Current;
        var restartPlayback = true;
        try
        {
            if (document is null
                || document.SequenceKind != DocumentSequenceKind.Animation
                || !await _viewModel.AdvanceAnimationAsync(_shutdownCts.Token)
                || !ReferenceEquals(document, _viewModel.Session.Current))
                return;
            RebuildSnapshot(document, preserveView: true);
            UpdateStatusBar();
            Canvas.Invalidate();
        }
        catch (OperationCanceledException)
        {
            restartPlayback = false;
        }
        catch (Exception ex) when (ex is ImageRejectedException or IOException)
        {
            restartPlayback = false;
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
        finally
        {
            _animationTickInProgress = false;
            if (restartPlayback
                && ReferenceEquals(document, _viewModel.Session.Current)
                && document?.SequenceKind == DocumentSequenceKind.Animation)
                ConfigureAnimationPlayback();
        }
    }

    private async Task ConfirmAnimationFlattenAsync(Core.Documents.ImageDocument document)
    {
        try
        {
            var result = ContentDialogResult.None;
            if (Content?.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = AppStrings.AnimationEditTitle,
                    Content = AppStrings.AnimationEditBody,
                    PrimaryButtonText = AppStrings.AnimationEditConfirm,
                    CloseButtonText = AppStrings.DialogCancel,
                    DefaultButton = ContentDialogButton.Primary,
                };
                result = await ShowDialogAsync(dialog, editScoped: true);
            }

            if (!ReferenceEquals(document, _viewModel.Session.Current))
                return;

            if (result == ContentDialogResult.Primary)
            {
                _animationEditAccepted = await document.FlattenAnimationToCurrentFrameAsync(
                    _shutdownCts.Token);
            }
            else if (_viewModel.Editor.CurrentStateId == _animationFirstEditStateId)
            {
                _viewModel.Editor.Undo();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(document, _viewModel.Session.Current)
                && _viewModel.Editor.CurrentStateId == _animationFirstEditStateId)
            {
                _viewModel.Editor.Undo();
            }
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
        finally
        {
            _animationConfirmationPending = false;
            if (ReferenceEquals(document, _viewModel.Session.Current))
            {
                _viewModel.RefreshStatus();
                UpdateStatusBar();
                UpdateEditCommands();
                if (!_animationEditAccepted)
                    ConfigureAnimationPlayback();
            }
        }
    }

    private void QueueScaleDependentRerender()
    {
        if (_viewModel.Session.Current is not { SupportsScaleDependentRendering: true } document)
            return;
        var rasterizationScale = Canvas.XamlRoot?.RasterizationScale ?? 1d;
        var desired = Math.Clamp(
            (long)Math.Ceiling(
                Math.Max(document.NativeSize.Width, document.NativeSize.Height)
                * _transform.Scale
                * rasterizationScale),
            1,
            AppServices.Limits.MaxDimension);
        var current = Math.Max(document.Frame.Width, document.Frame.Height);
        if (desired <= current * 1.25 && desired >= current * 0.5)
            return;

        _scaleRenderTarget = checked((int)desired);
        _scaleRenderTimer.Stop();
        _scaleRenderTimer.Start();
    }

    private async void OnScaleDependentRenderTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        var document = _viewModel.Session.Current;
        if (document is null || !document.SupportsScaleDependentRendering)
            return;

        _scaleRenderCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _scaleRenderCancellation = cancellation;
        try
        {
            if (!await _viewModel.RerenderScaleDependentAsync(_scaleRenderTarget, cancellation.Token)
                || !ReferenceEquals(document, _viewModel.Session.Current))
                return;
            RebuildSnapshot(document, preserveView: true);
            UpdateStatusBar();
            Canvas.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ImageRejectedException or IOException)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scaleRenderCancellation, cancellation))
                _scaleRenderCancellation = null;
            cancellation.Dispose();
        }
    }

    private void OnZoomSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingZoomSlider || !double.IsFinite(e.NewValue) || _transform.Scale <= 0f)
            return;
        ZoomAtCenter((float)(e.NewValue / 100d / _transform.Scale));
    }

    private void ZoomAtCenter(float factor)
    {
        if (_viewModel.Editor.Document is null || !float.IsFinite(factor) || factor <= 0f)
            return;
        _transform.ZoomAt(
            new SKPoint(_transform.Viewport.Width / 2f, _transform.Viewport.Height / 2f), factor);
        QueueScaleDependentRerender();
        UpdateStatusBar();
        Canvas.Invalidate();
    }

    private void OnRotateClicked(object sender, RoutedEventArgs e) =>
        ApplyTransformOp(TransformEditKind.Rotate, new RotateOp(90f));

    private void OnAnnotationToolClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string name } button
            || !Enum.TryParse<CanvasTool>(name, out var tool))
            return;
        SetTool(button.IsChecked == true ? tool : CanvasTool.Select);
    }

    private void OnCropClicked(object sender, RoutedEventArgs e) =>
        SetTool(CropButton.IsChecked == true ? CanvasTool.Crop : CanvasTool.Select);

    /// <summary>Edit tools are mutually exclusive; switching abandons any in-progress draft.</summary>
    private void SetTool(CanvasTool tool)
    {
        SaveCurrentToolStyle();
        _tool = tool;
        CancelActiveGesture();
        var style = _toolStyles.TryGetValue(tool, out var saved)
            ? saved
            : DefaultStyle(tool);
        _strokeWidth = style.StrokeWidth;
        _opacity = style.Opacity;
        _fontSize = style.FontSize;
        UpdateToolUi();
        UpdateEditCommands();
        Canvas.Invalidate();
    }

    private void OnStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _strokeWidth = (float)args.NewValue;
        if (SelectedAnnotation() is { IsLocked: false } selected)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, selected switch
            {
                InkAnnotation ink => ink with { StrokeWidth = _strokeWidth },
                LineAnnotation line => line with { StrokeWidth = _strokeWidth },
                RectangleAnnotation rectangle => rectangle with { StrokeWidth = _strokeWidth },
                _ => selected,
            });
        }
        SaveCurrentToolStyle();
    }

    private void OnOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _opacity = (float)(args.NewValue / 100d);
        if (SelectedAnnotation() is { IsLocked: false } selected)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, selected switch
            {
                InkAnnotation ink => ink with { Opacity = _opacity },
                LineAnnotation line => line with { Opacity = _opacity },
                RectangleAnnotation rectangle => rectangle with { Opacity = _opacity },
                TextAnnotation text => text with { Opacity = _opacity },
                NumberMarkerAnnotation marker => marker with { Opacity = _opacity },
                ImageAnnotation image => image with { Opacity = _opacity },
                _ => selected,
            });
        }
        SaveCurrentToolStyle();
    }

    private void OnBlockSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _mosaicBlockSize = (float)args.NewValue;
        if (SelectedAnnotation() is ProtectionAnnotation { Kind: ProtectionKind.Mosaic, IsLocked: false } mosaic)
            ApplySelectedEdit(AnnotationEditKind.Style, mosaic with { BlockSize = _mosaicBlockSize });
        PublishCurrentToolDefaults();
    }

    private void OnBlurSigmaChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _blurSigma = (float)args.NewValue;
        if (SelectedAnnotation() is ProtectionAnnotation { Kind: ProtectionKind.Blur, IsLocked: false } blur)
            ApplySelectedEdit(AnnotationEditKind.Style, blur with { BlurSigma = _blurSigma });
        PublishCurrentToolDefaults();
    }

    private void OnFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _fontSize = (float)args.NewValue;
        if (SelectedAnnotation() is { IsLocked: false } selected)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, selected switch
            {
                TextAnnotation text => text with { FontSize = _fontSize },
                NumberMarkerAnnotation marker => marker with { FontSize = _fontSize },
                _ => selected,
            });
        }
        SaveCurrentToolStyle();
    }

    private void OnFillChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingToolControls)
            return;
        _fillEnabled = FillCheckBox.IsChecked == true;
        if (SelectedAnnotation() is RectangleAnnotation { IsLocked: false } rectangle)
            ApplySelectedEdit(AnnotationEditKind.Style, rectangle with
            {
                FillArgb = _fillEnabled ? rectangle.StrokeArgb : null,
            });
        PublishCurrentToolDefaults();
    }

    private void OnCornerRadiusChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue))
            return;
        _cornerRadius = (float)args.NewValue;
        if (SelectedAnnotation() is RectangleAnnotation { IsLocked: false } rectangle)
            ApplySelectedEdit(AnnotationEditKind.Style, rectangle with { CornerRadius = _cornerRadius });
        PublishCurrentToolDefaults();
    }

    private void OnArrowheadChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolControls
            || ArrowheadBox.SelectedItem is not ComboBoxItem { Tag: ArrowheadKind arrowhead })
            return;
        _arrowhead = arrowhead;
        if (SelectedAnnotation() is LineAnnotation { IsLocked: false } line)
            ApplySelectedEdit(AnnotationEditKind.Style, line with { EndArrowhead = arrowhead });
        PublishCurrentToolDefaults();
    }

    private void OnFontFamilyChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingToolControls || string.IsNullOrWhiteSpace(FontFamilyBox.Text))
            return;
        _fontFamily = FontFamilyBox.Text.Trim();
        if (SelectedAnnotation() is TextAnnotation { IsLocked: false } text)
            ApplySelectedEdit(AnnotationEditKind.Style, text with { FontFamily = _fontFamily });
        PublishCurrentToolDefaults();
    }

    private void OnTextStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingToolControls)
            return;
        _fontBold = BoldButton.IsChecked == true;
        _fontItalic = ItalicButton.IsChecked == true;
        if (SelectedAnnotation() is TextAnnotation { IsLocked: false } text)
            ApplySelectedEdit(AnnotationEditKind.Style, text with
            {
                IsBold = _fontBold,
                IsItalic = _fontItalic,
            });
        PublishCurrentToolDefaults();
    }

    private void OnTextAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolControls
            || TextAlignmentBox.SelectedItem is not ComboBoxItem
            { Tag: AnnotationTextAlignment alignment })
            return;
        _textAlignment = alignment;
        if (SelectedAnnotation() is TextAnnotation { IsLocked: false } text)
            ApplySelectedEdit(AnnotationEditKind.Style, text with { Alignment = alignment });
        PublishCurrentToolDefaults();
    }

    private void OnTextBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingToolControls)
            return;
        _textBackgroundEnabled = TextBackgroundCheckBox.IsChecked == true;
        if (SelectedAnnotation() is TextAnnotation { IsLocked: false } text)
            ApplySelectedEdit(AnnotationEditKind.Style, text with
            {
                BackgroundArgb = _textBackgroundEnabled ? 0xCCFF_FFFF : null,
            });
        PublishCurrentToolDefaults();
    }

    private void UpdateToolUi()
    {
        var selected = _tool == CanvasTool.Select ? SelectedAnnotation() : null;
        var selectedMode = selected is not null;
        AnnotationContextBar.Visibility = _tool is CanvasTool.Crop or CanvasTool.Eyedropper
            || (_tool == CanvasTool.Select && !selectedMode)
            ? Visibility.Collapsed : Visibility.Visible;
        ToolContextLabel.Text = selectedMode ? AnnotationName(selected!) : ToolName(_tool);
        var hasStroke = selectedMode
            ? selected is InkAnnotation or LineAnnotation or RectangleAnnotation
            : _tool is not CanvasTool.Text and not CanvasTool.Number and not CanvasTool.Eyedropper
                and not CanvasTool.Mosaic and not CanvasTool.Blur and not CanvasTool.Mask;
        var hasFont = selectedMode
            ? selected is TextAnnotation or NumberMarkerAnnotation
            : _tool is CanvasTool.Text or CanvasTool.Number;
        // Protection is always fully opaque (FR-ANNO-008~010) — no opacity dial.
        var hasOpacity = selectedMode
            ? selected is not ProtectionAnnotation
            : _tool is not CanvasTool.Mosaic and not CanvasTool.Blur and not CanvasTool.Mask;
        var hasBlockSize = selectedMode
            ? selected is ProtectionAnnotation { Kind: ProtectionKind.Mosaic }
            : _tool == CanvasTool.Mosaic;
        var hasBlurSigma = selectedMode
            ? selected is ProtectionAnnotation { Kind: ProtectionKind.Blur }
            : _tool == CanvasTool.Blur;
        StrokeWidthLabel.Visibility = hasStroke ? Visibility.Visible : Visibility.Collapsed;
        StrokeWidthBox.Visibility = hasStroke ? Visibility.Visible : Visibility.Collapsed;
        OpacityLabel.Visibility = hasOpacity ? Visibility.Visible : Visibility.Collapsed;
        OpacityBox.Visibility = hasOpacity ? Visibility.Visible : Visibility.Collapsed;
        BlockSizeLabel.Visibility = hasBlockSize ? Visibility.Visible : Visibility.Collapsed;
        BlockSizeBox.Visibility = hasBlockSize ? Visibility.Visible : Visibility.Collapsed;
        BlurSigmaLabel.Visibility = hasBlurSigma ? Visibility.Visible : Visibility.Collapsed;
        BlurSigmaBox.Visibility = hasBlurSigma ? Visibility.Visible : Visibility.Collapsed;
        FontSizeLabel.Visibility = hasFont ? Visibility.Visible : Visibility.Collapsed;
        FontSizeBox.Visibility = hasFont ? Visibility.Visible : Visibility.Collapsed;
        var isShape = selectedMode
            ? selected is RectangleAnnotation
            : _tool is CanvasTool.Rectangle or CanvasTool.RoundedRectangle or CanvasTool.Ellipse;
        FillCheckBox.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
        CornerRadiusBox.Visibility = selectedMode
            ? selected is RectangleAnnotation { Shape: ShapeKind.RoundedRectangle }
                ? Visibility.Visible : Visibility.Collapsed
            : _tool == CanvasTool.RoundedRectangle
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArrowheadBox.Visibility = selectedMode
            ? selected is LineAnnotation ? Visibility.Visible : Visibility.Collapsed
            : _tool == CanvasTool.Arrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isText = selectedMode ? selected is TextAnnotation : _tool == CanvasTool.Text;
        FontFamilyBox.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        BoldButton.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        ItalicButton.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        TextAlignmentBox.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        TextBackgroundCheckBox.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        var objectCommands = selectedMode ? Visibility.Visible : Visibility.Collapsed;
        var objectEditable = selected is not { IsLocked: true };
        StrokeWidthBox.IsEnabled = objectEditable;
        OpacityBox.IsEnabled = objectEditable;
        BlockSizeBox.IsEnabled = objectEditable;
        BlurSigmaBox.IsEnabled = objectEditable;
        FontSizeBox.IsEnabled = objectEditable;
        FillCheckBox.IsEnabled = objectEditable;
        CornerRadiusBox.IsEnabled = objectEditable;
        ArrowheadBox.IsEnabled = objectEditable;
        FontFamilyBox.IsEnabled = objectEditable;
        BoldButton.IsEnabled = objectEditable;
        ItalicButton.IsEnabled = objectEditable;
        TextAlignmentBox.IsEnabled = objectEditable;
        TextBackgroundCheckBox.IsEnabled = objectEditable;
        ObjectRotationBox.IsEnabled = objectEditable && selected is not ProtectionAnnotation;
        ObjectRotationBox.Visibility = selected is ProtectionAnnotation
            ? Visibility.Collapsed : objectCommands;
        SendToBackButton.Visibility = objectCommands;
        SendBackwardButton.Visibility = objectCommands;
        BringForwardButton.Visibility = objectCommands;
        BringToFrontButton.Visibility = objectCommands;
        DuplicateButton.Visibility = objectCommands;
        EditTextButton.Visibility = selected is TextAnnotation ? Visibility.Visible : Visibility.Collapsed;
        _updatingToolControls = true;
        try
        {
            StrokeWidthBox.Value = selected switch
            {
                InkAnnotation ink => ink.StrokeWidth,
                LineAnnotation line => line.StrokeWidth,
                RectangleAnnotation rectangle => rectangle.StrokeWidth,
                _ => _strokeWidth,
            };
            OpacityBox.Value = (selectedMode ? AnnotationOpacity(selected) : _opacity) * 100f;
            BlockSizeBox.Value = selected is ProtectionAnnotation { Kind: ProtectionKind.Mosaic } mosaic
                ? mosaic.BlockSize : _mosaicBlockSize;
            BlurSigmaBox.Value = selected is ProtectionAnnotation { Kind: ProtectionKind.Blur } blur
                ? blur.BlurSigma : _blurSigma;
            FontSizeBox.Value = selected switch
            {
                TextAnnotation text => text.FontSize,
                NumberMarkerAnnotation marker => marker.FontSize,
                _ => _fontSize,
            };
            FillCheckBox.IsChecked = selected is RectangleAnnotation filledRectangle
                ? filledRectangle.FillArgb is not null : _fillEnabled;
            CornerRadiusBox.Value = selected is RectangleAnnotation rounded
                ? rounded.CornerRadius : _cornerRadius;
            var selectedArrow = selected is LineAnnotation selectedLine
                ? selectedLine.EndArrowhead : _arrowhead;
            ArrowheadBox.SelectedIndex = selectedArrow == ArrowheadKind.Open ? 0 : 1;
            FontFamilyBox.Text = selected is TextAnnotation textValue ? textValue.FontFamily : _fontFamily;
            BoldButton.IsChecked = selected is TextAnnotation boldText ? boldText.IsBold : _fontBold;
            ItalicButton.IsChecked = selected is TextAnnotation italicText ? italicText.IsItalic : _fontItalic;
            TextAlignmentBox.SelectedIndex = (int)(selected is TextAnnotation aligned
                ? aligned.Alignment : _textAlignment);
            TextBackgroundCheckBox.IsChecked = selected is TextAnnotation background
                ? background.BackgroundArgb is not null : _textBackgroundEnabled;
            ObjectRotationBox.Value = selected?.RotationDegrees ?? 0f;
        }
        finally
        {
            _updatingToolControls = false;
        }
        // Tool/selection changes can flip the mask-color context the palette reflects.
        UpdateColorSelection();
    }

    private static float AnnotationOpacity(Annotation? annotation) => annotation switch
    {
        InkAnnotation ink => ink.Opacity,
        LineAnnotation line => line.Opacity,
        RectangleAnnotation rectangle => rectangle.Opacity,
        TextAnnotation text => text.Opacity,
        NumberMarkerAnnotation marker => marker.Opacity,
        ImageAnnotation image => image.Opacity,
        _ => 1f,
    };

    private Annotation? SelectedAnnotation() => _selectedAnnotation == default
        ? null
        : _viewModel.Editor.State.Find(_selectedAnnotation);

    private bool ApplySelectedEdit(AnnotationEditKind kind, Annotation next)
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not { IsLocked: false } current
            || current.Id != next.Id
            || Equals(current, next))
            return false;
        try
        {
            _viewModel.Editor.Apply(new ReplaceAnnotationCommand(kind, current, next));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
            return false;
        }
    }

    private static string AnnotationName(Annotation annotation) => annotation.Name ?? annotation switch
    {
        InkAnnotation { Kind: InkKind.Highlighter } => AppStrings.LayerTypeHighlighter,
        InkAnnotation => AppStrings.LayerTypeInk,
        LineAnnotation { EndArrowhead: not ArrowheadKind.None } => AppStrings.LayerTypeArrow,
        LineAnnotation => AppStrings.LayerTypeLine,
        RectangleAnnotation { Shape: ShapeKind.Ellipse } => AppStrings.LayerTypeEllipse,
        RectangleAnnotation => AppStrings.LayerTypeRectangle,
        TextAnnotation => AppStrings.LayerTypeText,
        NumberMarkerAnnotation marker => $"{AppStrings.LayerTypeNumber} {marker.Number}",
        ImageAnnotation => AppStrings.LayerTypeImage,
        ProtectionAnnotation { Kind: ProtectionKind.Mosaic } => AppStrings.LayerTypeMosaic,
        ProtectionAnnotation { Kind: ProtectionKind.Blur } => AppStrings.LayerTypeBlur,
        ProtectionAnnotation => AppStrings.LayerTypeMask,
        _ => annotation.GetType().Name,
    };

    private void UpdateLayerPanel()
    {
        if (LayerList is null)
            return;
        _updatingLayerList = true;
        try
        {
            var state = _viewModel.Editor.State;
            var layers = state.Layers;
            LayerList.Items.Clear();
            for (var index = layers.Count - 1; index >= 0; index--)
            {
                var layer = layers[index];
                var item = new ListViewItem { Tag = layer.Id, Content = BuildLayerRow(layer, index) };
                var name = LayerDisplayName(layer, index);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    item, layer.Id == _activeLayerId ? $"{name} — {AppStrings.LayerActive}" : name);
                LayerList.Items.Add(item);
                if (layer.Id == _activeLayerId)
                    LayerList.SelectedItem = item;
            }
            var totalAnnotations = state.Annotations.Count;
            var autoVisible = totalAnnotations > 0 || layers.Count > 1;
            var visible = _viewModel.Editor.Document is not null && (_layerPanelOverride ?? autoVisible);
            LayerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            LayerPanelButton.IsChecked = visible;
            var activeIndex = state.LayerIndexOf(_activeLayerId);
            LayerAddButton.IsEnabled = _viewModel.Editor.Document is not null;
            LayerDeleteButton.IsEnabled = layers.Count > 1 && activeIndex >= 0;
            LayerUpButton.IsEnabled = activeIndex >= 0 && activeIndex < layers.Count - 1;
            LayerDownButton.IsEnabled = activeIndex > 0;
            LayerRenameButton.IsEnabled = activeIndex >= 0;
            LayerMoveSelectionButton.IsEnabled = activeIndex >= 0
                && layers[activeIndex] is { IsVisible: true, IsLocked: false }
                && SelectedAnnotation() is { IsLocked: false } selected
                && state.FindLayerOf(selected.Id)?.Id != _activeLayerId;
        }
        finally
        {
            _updatingLayerList = false;
        }
    }

    /// <summary>Row = whole layer (UR-007): eye, name (or rename box), lock. Objects are not rows.</summary>
    private Grid BuildLayerRow(AnnotationLayer layer, int index)
    {
        var name = LayerDisplayName(layer, index);
        var row = new Grid { ColumnSpacing = 6, Opacity = layer.IsVisible ? 1d : 0.6d };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var visibility = new Button
        {
            Tag = layer.Id,
            Content = new IconSourceElement
            {
                IconSource = IconSourceFor(layer.IsVisible
                    ? "Icon.Layer.Visible" : "Icon.Layer.Hidden"),
                Width = 20,
                Height = 20,
            },
            Width = 36,
            Height = 36,
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(0),
        };
        visibility.Click += OnLayerVisibilityClicked;
        SetTip(visibility,
            $"{(layer.IsVisible ? AppStrings.LayerHide : AppStrings.LayerShow)}: {name}",
            layer.IsVisible ? AppStrings.LayerVisible : AppStrings.LayerHidden);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            visibility,
            $"{(layer.IsVisible ? AppStrings.LayerHide : AppStrings.LayerShow)}: {name}");

        if (_renamingLayerId == layer.Id)
        {
            var editor = new TextBox
            {
                Tag = layer.Id,
                Text = layer.Name,
                VerticalAlignment = VerticalAlignment.Center,
                MaxLength = AnnotationValidator.MaxNameLength,
            };
            editor.KeyDown += OnLayerRenameKeyDown;
            editor.LostFocus += OnLayerRenameLostFocus;
            editor.Loaded += static (sender, _) => (sender as TextBox)?.Focus(FocusState.Programmatic);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, AppStrings.LayerRename);
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);
        }
        else
        {
            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = layer.Id == _activeLayerId
                    ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
        }

        var locked = new Button
        {
            Tag = layer.Id,
            Content = new IconSourceElement
            {
                IconSource = IconSourceFor(layer.IsLocked
                    ? "Icon.Layer.Locked" : "Icon.Layer.Unlocked"),
                Width = 20,
                Height = 20,
            },
            Width = 36,
            Height = 36,
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(0),
        };
        locked.Click += OnLayerLockClicked;
        SetTip(locked,
            $"{(layer.IsLocked ? AppStrings.LayerUnlock : AppStrings.LayerLock)}: {name}",
            layer.IsLocked ? AppStrings.LayerLocked : AppStrings.LayerUnlocked);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            locked,
            $"{(layer.IsLocked ? AppStrings.LayerUnlock : AppStrings.LayerLock)}: {name}");
        Grid.SetColumn(locked, 2);

        row.Children.Add(visibility);
        row.Children.Add(locked);
        return row;
    }

    /// <summary>Positional fallback for unnamed layers; index is bottom-based like Photoshop numbering.</summary>
    private static string LayerDisplayName(AnnotationLayer layer, int index) =>
        layer.Name.Length == 0 ? $"{AppStrings.LayerDefaultName} {index + 1}" : layer.Name;

    private void OnLayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLayerList)
            return;
        if (LayerList.SelectedItem is ListViewItem { Tag: Guid id }
            && _viewModel.Editor.State.FindLayer(id) is not null && id != _activeLayerId)
        {
            _activeLayerId = id;
            _renamingLayerId = default;
            UpdateLayerPanel();
        }
    }

    private void OnLayerVisibilityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }
            || _viewModel.Editor.State.FindLayer(id) is not { } layer)
            return;
        ApplyLayerEdit(LayerEditKind.Visibility, layer, layer with { IsVisible = !layer.IsVisible });
    }

    private void OnLayerLockClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }
            || _viewModel.Editor.State.FindLayer(id) is not { } layer)
            return;
        ApplyLayerEdit(LayerEditKind.Lock, layer, layer with { IsLocked = !layer.IsLocked });
    }

    private void ApplyLayerEdit(LayerEditKind kind, AnnotationLayer before, AnnotationLayer after)
    {
        if (_viewModel.IsReplacementPending || Equals(before, after))
            return;
        try
        {
            _viewModel.Editor.Apply(new ReplaceLayerCommand(kind, before, after));
        }
        catch (InvalidOperationException ex)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
    }

    private void OnLayerPanelToggleClicked(object sender, RoutedEventArgs e)
    {
        _layerPanelOverride = LayerPanelButton.IsChecked == true;
        UpdateLayerPanel();
    }

    private void OnLayerAddClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReplacementPending || _viewModel.Editor.Document is null)
            return;
        var state = _viewModel.Editor.State;
        var layer = new AnnotationLayer
        {
            Id = Guid.NewGuid(),
            Name = $"{AppStrings.LayerDefaultName} {state.Layers.Count + 1}",
        };
        var activeIndex = state.LayerIndexOf(_activeLayerId);
        _viewModel.Editor.Apply(new AddLayerCommand(layer, activeIndex < 0 ? null : activeIndex + 1));
        _activeLayerId = layer.Id;
        UpdateLayerPanel();
    }

    private void OnLayerDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReplacementPending)
            return;
        var state = _viewModel.Editor.State;
        if (state.Layers.Count <= 1 || state.FindLayer(_activeLayerId) is null)
            return;
        try
        {
            _viewModel.Editor.Apply(new DeleteLayerCommand(state, _activeLayerId));
        }
        catch (InvalidOperationException ex)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
    }

    private void OnLayerUpClicked(object sender, RoutedEventArgs e) => ReorderActiveLayer(+1);

    private void OnLayerDownClicked(object sender, RoutedEventArgs e) => ReorderActiveLayer(-1);

    private void ReorderActiveLayer(int direction)
    {
        if (_viewModel.IsReplacementPending)
            return;
        var state = _viewModel.Editor.State;
        var current = state.LayerIndexOf(_activeLayerId);
        if (current < 0)
            return;
        var target = Math.Clamp(current + direction, 0, state.Layers.Count - 1);
        if (current == target)
            return;
        _viewModel.Editor.Apply(new ReorderLayerCommand(state, _activeLayerId, target));
    }

    private void OnLayerRenameClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor.State.FindLayer(_activeLayerId) is null)
            return;
        _renamingLayerId = _renamingLayerId == _activeLayerId ? default : _activeLayerId;
        UpdateLayerPanel();
    }

    private void OnLayerRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: Guid id } box)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitLayerRename(id, box.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _renamingLayerId = default;
            UpdateLayerPanel();
        }
    }

    private void OnLayerRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: Guid id } box && _renamingLayerId == id)
            CommitLayerRename(id, box.Text);
    }

    private void CommitLayerRename(Guid layerId, string name)
    {
        _renamingLayerId = default;
        var state = _viewModel.Editor.State;
        if (state.FindLayer(layerId) is not { } layer)
        {
            UpdateLayerPanel();
            return;
        }
        var trimmed = name.Trim();
        if (trimmed.Length > AnnotationValidator.MaxNameLength)
            trimmed = trimmed[..AnnotationValidator.MaxNameLength];
        if (trimmed == layer.Name)
        {
            UpdateLayerPanel();
            return;
        }
        ApplyLayerEdit(LayerEditKind.Name, layer, layer with { Name = trimmed });
        UpdateLayerPanel();
    }

    private void OnLayerMoveSelectionClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        var state = _viewModel.Editor.State;
        if (state.FindLayer(_activeLayerId) is not { IsVisible: true, IsLocked: false }
            || state.FindLayerOf(selected.Id)?.Id == _activeLayerId)
            return;
        var command = new MoveAnnotationToLayerCommand(state, selected.Id, _activeLayerId);
        if (!command.IsNoOp)
            _viewModel.Editor.Apply(command);
    }

    /// <summary>Gate for authoring commands: the active layer must be visible and unlocked.</summary>
    private bool CanEditActiveLayer()
    {
        if (_viewModel.Editor.State.FindLayer(_activeLayerId) is not { } layer)
            return false;
        if (!layer.IsVisible)
        {
            SetStatusState(AppStrings.LayerBlockedHidden);
            return false;
        }
        if (layer.IsLocked)
        {
            SetStatusState(AppStrings.LayerBlockedLocked);
            return false;
        }
        return true;
    }

    private void ConfigureToolRailOverflowHints()
    {
        foreach (var hint in new UIElement[]
        {
            ToolRailStartOverflowHint,
            ToolRailEndOverflowHint,
        })
        {
            var animation = new DoubleAnimation
            {
                From = 0.35,
                To = 0.9,
                Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(animation, hint);
            Storyboard.SetTargetProperty(animation, "Opacity");
            _toolRailOverflowPulse.Children.Add(animation);
        }

        ToolRailScroll.Loaded += OnToolRailLoaded;
        ToolRailScroll.LayoutUpdated += OnToolRailLayoutUpdated;
        ToolRailScroll.ViewChanged += OnToolRailViewChanged;
        ToolRailScroll.SizeChanged += OnToolRailSizeChanged;
        ToolRailItems.SizeChanged += OnToolRailSizeChanged;
        ToolRailScroll.PointerWheelChanged += OnToolRailPointerWheelChanged;
    }

    private void DetachToolRailOverflowHints()
    {
        _toolRailLayoutGeneration++;
        _toolRailOverflowUpdateQueued = false;
        _toolRailResetPending = false;
        ToolRailScroll.Loaded -= OnToolRailLoaded;
        ToolRailScroll.LayoutUpdated -= OnToolRailLayoutUpdated;
        ToolRailScroll.ViewChanged -= OnToolRailViewChanged;
        ToolRailScroll.SizeChanged -= OnToolRailSizeChanged;
        ToolRailItems.SizeChanged -= OnToolRailSizeChanged;
        ToolRailScroll.PointerWheelChanged -= OnToolRailPointerWheelChanged;
        _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        StopToolRailOverflowPulse();
    }

    private void OnToolRailLoaded(object sender, RoutedEventArgs e) =>
        QueueToolRailOverflowUpdate(resetToStart: true);

    private void OnToolRailLayoutUpdated(object? sender, object e) =>
        UpdateToolRailOverflowHints();

    private void OnToolRailViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        UpdateToolRailOverflowHints();

    private void OnToolRailSizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueToolRailOverflowUpdate(resetToStart: false);

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        var animationsEnabled = sender.AnimationsEnabled;
        var generation = _toolRailLayoutGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _toolRailLayoutGeneration)
                return;
            _animationsEnabled = animationsEnabled;
            UpdateToolRailOverflowHints();
            ConfigureAnimationPlayback();
        });
    }

    private void OnToolRailPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_toolRailDock != ToolRailDock.Horizontal || ToolRailScroll.ScrollableWidth <= 0.5)
            return;
        var delta = e.GetCurrentPoint(ToolRailScroll).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        // Mouse wheel up moves toward the rail start; down moves toward the end.
        var targetOffset = Math.Clamp(
            ToolRailScroll.HorizontalOffset - (delta * 0.6),
            0d,
            ToolRailScroll.ScrollableWidth);
        ToolRailScroll.ChangeView(targetOffset, null, null, true);
        e.Handled = true;
    }

    private void QueueToolRailOverflowUpdate(bool resetToStart)
    {
        _toolRailResetPending |= resetToStart;
        if (_toolRailOverflowUpdateQueued)
            return;
        _toolRailOverflowUpdateQueued = true;
        var generation = _toolRailLayoutGeneration;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _toolRailLayoutGeneration)
                    return;
                _toolRailOverflowUpdateQueued = false;
                var reset = _toolRailResetPending;
                _toolRailResetPending = false;
                if (reset)
                    ToolRailScroll.ChangeView(0d, 0d, null, true);
                UpdateToolRailOverflowHints();
            }))
        {
            _toolRailOverflowUpdateQueued = false;
        }
    }

    private void UpdateToolRailOverflowHints()
    {
        const double epsilon = 0.5;
        var horizontal = _toolRailDock == ToolRailDock.Horizontal;
        var scrollable = horizontal
            ? ToolRailScroll.ScrollableWidth
            : ToolRailScroll.ScrollableHeight;
        var offset = horizontal
            ? ToolRailScroll.HorizontalOffset
            : ToolRailScroll.VerticalOffset;
        var hasOverflow = double.IsFinite(scrollable) && scrollable > epsilon;
        var showStart = hasOverflow && offset > epsilon;
        var showEnd = hasOverflow && offset < scrollable - epsilon;

        ToolRailStartOverflowHint.Visibility = showStart
            ? Visibility.Visible : Visibility.Collapsed;
        ToolRailEndOverflowHint.Visibility = showEnd
            ? Visibility.Visible : Visibility.Collapsed;

        if (!_animationsEnabled || (!showStart && !showEnd))
        {
            StopToolRailOverflowPulse();
            ToolRailStartOverflowHint.Opacity = 0.72;
            ToolRailEndOverflowHint.Opacity = 0.72;
            return;
        }
        if (_toolRailOverflowPulseRunning)
            return;
        _toolRailOverflowPulse.Begin();
        _toolRailOverflowPulseRunning = true;
    }

    private void StopToolRailOverflowPulse()
    {
        if (!_toolRailOverflowPulseRunning)
            return;
        _toolRailOverflowPulse.Stop();
        _toolRailOverflowPulseRunning = false;
    }

    private void ApplyToolRailOverflowHintLayout(bool horizontal)
    {
        foreach (var (hint, start) in new[]
        {
            (ToolRailStartOverflowHint, true),
            (ToolRailEndOverflowHint, false),
        })
        {
            hint.Width = horizontal ? 22 : double.NaN;
            hint.Height = horizontal ? double.NaN : 22;
            hint.HorizontalAlignment = horizontal
                ? (start ? HorizontalAlignment.Left : HorizontalAlignment.Right)
                : HorizontalAlignment.Stretch;
            hint.VerticalAlignment = horizontal
                ? VerticalAlignment.Stretch
                : (start ? VerticalAlignment.Top : VerticalAlignment.Bottom);
            hint.Background = CreateToolRailOverflowBrush(horizontal, start);
        }
    }

    private static LinearGradientBrush CreateToolRailOverflowBrush(bool horizontal, bool start)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
            EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1),
        };
        var colors = start
            ? new[] { 0xD03B_82F6u, 0x703B_82F6u, 0x003B_82F6u }
            : new[] { 0x003B_82F6u, 0x703B_82F6u, 0xD03B_82F6u };
        brush.GradientStops.Add(new GradientStop { Color = ToUiColor(colors[0]), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = ToUiColor(colors[1]), Offset = 0.45 });
        brush.GradientStops.Add(new GradientStop { Color = ToUiColor(colors[2]), Offset = 1 });
        return brush;
    }

    private void ApplyToolRailDock()
    {
        var horizontal = _toolRailDock == ToolRailDock.Horizontal;
        ToolRailItems.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        foreach (var group in new[]
        {
            FileToolGroup, HistoryToolGroup, ImageToolGroup, DrawingToolGroup,
            ShapeToolGroup, TextToolGroup, ProtectionToolGroup, ViewToolGroup,
        })
            group.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        foreach (var separator in new[]
        {
            DockMenuSeparator, FileHistorySeparator, HistoryImageSeparator, ImageDrawingSeparator,
            DrawingShapeSeparator, ShapeTextSeparator, TextProtectionSeparator, ProtectionViewSeparator,
        })
        {
            separator.Width = horizontal ? 1 : 28;
            separator.Height = horizontal ? 28 : 1;
            separator.Margin = horizontal
                ? new Thickness(6, 4, 6, 4)
                : new Thickness(4, 6, 4, 6);
            separator.HorizontalAlignment = HorizontalAlignment.Center;
            separator.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetRow(DockToggleButton, 0);
        Grid.SetColumn(DockToggleButton, 0);
        Grid.SetRow(DockMenuSeparator, horizontal ? 0 : 1);
        Grid.SetColumn(DockMenuSeparator, horizontal ? 1 : 0);
        Grid.SetRow(ToolRailScrollableViewport, horizontal ? 0 : 2);
        Grid.SetColumn(ToolRailScrollableViewport, horizontal ? 2 : 0);
        ToolRail.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ToolRail.VerticalAlignment = horizontal ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        ToolRail.MaxHeight = horizontal ? 64 : double.PositiveInfinity;
        ToolRailViewport.Width = horizontal ? double.NaN : 36;
        ToolRailViewport.Height = horizontal ? 36 : double.NaN;
        ToolRailScroll.HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled;
        ToolRailScroll.VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled;
        ToolRailScroll.HorizontalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        ToolRailScroll.VerticalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ToolRailScroll.HorizontalContentAlignment = horizontal
            ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ApplyToolRailOverflowHintLayout(horizontal);
        QueueToolRailOverflowUpdate(resetToStart: true);
        LayerPanel.Margin = horizontal ? new Thickness(12, 76, 0, 0) : new Thickness(68, 12, 0, 0);
        AnnotationContextBar.Margin = horizontal
            ? new Thickness(12, 76, 12, 0)
            : new Thickness(68, 12, 12, 0);
        UpdateDynamicTooltips();
    }

    private async void OnDockToggleClicked(object sender, RoutedEventArgs e)
    {
        _toolRailDock = _toolRailDock == ToolRailDock.Vertical
            ? ToolRailDock.Horizontal : ToolRailDock.Vertical;
        ApplyToolRailDock();
        try
        {
            await AppServices.UpdateSettingsAsync(current => current with
            {
                ToolRailDock = _toolRailDock,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null)
            return;
        var editor = new SettingsDialogContent(AppServices.Settings);
        editor.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        AppSettings? candidate = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.SettingsTitle,
            Content = new ScrollViewer
            {
                Content = editor,
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = AppStrings.SettingsSave,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (editor.TryCreateSettings(out var value))
                candidate = value;
            else
                args.Cancel = true;
        };
        if (await ShowDialogAsync(dialog, editScoped: false) != ContentDialogResult.Primary
            || candidate is null)
            return;
        try
        {
            await AppServices.UpdateSettingsAsync(current =>
                AppSettingsMerger.MergeSettingsDialogChanges(
                    editor.InitialSettings,
                    candidate,
                    current));
            SetStatusState(AppStrings.SettingsSaved);
        }
        catch (CaptureHotkeyUnavailableException ex)
        {
            SetStatusState(string.Format(
                AppStrings.CaptureHotkeyUnavailable,
                FormatCaptureHotkey(ex.RequestedHotkey)));
        }
        catch (RecentFileHistoryClearException)
        {
            SetStatusState(candidate.RecentFilesEnabled
                ? AppStrings.RecentEnableBlocked
                : AppStrings.RecentDisableIncomplete);
        }
        catch (AppDataProtectionException)
        {
            SetStatusState(AppStrings.AppDataProtectionPersistent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            SetStatusState($"{AppStrings.SettingsSaveFailed}: {ex.Message}");
        }
    }

    private void OnObjectRotationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingToolControls || !double.IsFinite(args.NewValue)
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        if (selected is ProtectionAnnotation)
            return;
        ApplySelectedEdit(AnnotationEditKind.Geometry,
            selected with { RotationDegrees = (float)args.NewValue });
    }

    private void OnSendToBackClicked(object sender, RoutedEventArgs e) => ReorderSelection(-1, true);
    private void OnSendBackwardClicked(object sender, RoutedEventArgs e) => ReorderSelection(-1, false);
    private void OnBringForwardClicked(object sender, RoutedEventArgs e) => ReorderSelection(1, false);
    private void OnBringToFrontClicked(object sender, RoutedEventArgs e) => ReorderSelection(1, true);
    private void OnDuplicateClicked(object sender, RoutedEventArgs e) => DuplicateSelection();

    private void NavigateOrNudge(float dx, float dy)
    {
        if (SelectedAnnotation() is { } selected)
        {
            if (!selected.IsLocked)
                NudgeSelection(dx, dy);
            return;
        }
        if (dx < 0f)
            _viewModel.OpenPrevious();
        else if (dx > 0f)
            _viewModel.OpenNext();
    }

    private void NudgeSelection(float dx, float dy)
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        var next = selected.Bounds.Translated(dx, dy);
        _viewModel.Editor.Apply(new MoveAnnotationCommand(selected.Id, selected.Bounds, next));
    }

    private void DuplicateSelection()
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        var command = new DuplicateAnnotationCommand(_viewModel.Editor.State, selected.Id);
        _viewModel.Editor.Apply(command);
        _selectedAnnotation = command.DuplicateId;
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
    }

    private void ReorderSelection(int direction, bool absolute)
    {
        if (_viewModel.IsReplacementPending
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        var state = _viewModel.Editor.State;
        if (state.FindLayerOf(selected.Id) is not { } layer)
            return;
        var current = layer.IndexOf(selected.Id);
        var target = absolute
            ? direction < 0 ? 0 : layer.Annotations.Count - 1
            : Math.Clamp(current + direction, 0, layer.Annotations.Count - 1);
        if (current == target)
            return;
        _viewModel.Editor.Apply(new ReorderAnnotationCommand(state, selected.Id, target));
    }

    private void SaveCurrentToolStyle()
    {
        if (_tool is not CanvasTool.Select and not CanvasTool.Crop)
        {
            _toolStyles[_tool] = new ToolStyle(_strokeWidth, _opacity, _fontSize);
            PublishCurrentToolDefaults();
        }
    }

    private void ApplyToolDefaults(ToolDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _toolStyles.Clear();
        foreach (var pair in defaults.Styles)
        {
            if (Enum.TryParse<CanvasTool>(pair.Key, ignoreCase: false, out var tool))
            {
                _toolStyles[tool] = new ToolStyle(
                    pair.Value.StrokeWidth,
                    pair.Value.Opacity,
                    pair.Value.FontSize);
            }
        }
        _strokeColor = defaults.StrokeArgb;
        _maskColor = defaults.MaskArgb;
        _fillEnabled = defaults.FillEnabled;
        _mosaicBlockSize = defaults.MosaicBlockSize;
        _blurSigma = defaults.BlurSigma;
        _cornerRadius = defaults.CornerRadius;
        _arrowhead = defaults.Arrowhead;
        _fontFamily = defaults.FontFamily;
        _fontBold = defaults.FontBold;
        _fontItalic = defaults.FontItalic;
        _textAlignment = defaults.TextAlignment;
        _textBackgroundEnabled = defaults.TextBackgroundEnabled;
        _publishedToolDefaults = CreateToolDefaultsSnapshot();
    }

    private ToolDefaults CreateToolDefaultsSnapshot()
    {
        var styles = new Dictionary<string, ToolStylePreference>(StringComparer.Ordinal);
        foreach (var tool in Enum.GetValues<CanvasTool>())
        {
            if (tool is CanvasTool.Select or CanvasTool.Crop)
                continue;
            var style = _toolStyles.TryGetValue(tool, out var saved)
                ? saved
                : DefaultStyle(tool);
            styles.Add(tool.ToString(), new ToolStylePreference
            {
                StrokeWidth = style.StrokeWidth,
                Opacity = style.Opacity,
                FontSize = style.FontSize,
            });
        }
        return new ToolDefaults
        {
            Styles = styles,
            StrokeArgb = _strokeColor,
            MaskArgb = _maskColor,
            FillEnabled = _fillEnabled,
            MosaicBlockSize = _mosaicBlockSize,
            BlurSigma = _blurSigma,
            CornerRadius = _cornerRadius,
            Arrowhead = _arrowhead,
            FontFamily = _fontFamily,
            FontBold = _fontBold,
            FontItalic = _fontItalic,
            TextAlignment = _textAlignment,
            TextBackgroundEnabled = _textBackgroundEnabled,
        };
    }

    private void PublishCurrentToolDefaults()
    {
        var edited = CreateToolDefaultsSnapshot();
        AppServices.PublishToolDefaults(_publishedToolDefaults, edited);
        _publishedToolDefaults = edited;
    }

    private static ToolStyle DefaultStyle(CanvasTool tool) => tool switch
    {
        CanvasTool.Highlighter => new ToolStyle(16f, 0.35f, 24f),
        CanvasTool.Number => new ToolStyle(3f, 1f, 18f),
        _ => new ToolStyle(3f, 1f, 24f),
    };

    private static string ToolName(CanvasTool tool) => tool switch
    {
        CanvasTool.Pen => AppStrings.ToolPen,
        CanvasTool.Highlighter => AppStrings.ToolHighlighter,
        CanvasTool.Line => AppStrings.ToolLine,
        CanvasTool.Arrow => AppStrings.ToolArrow,
        CanvasTool.Rectangle => AppStrings.ToolRectangle,
        CanvasTool.RoundedRectangle => AppStrings.ToolRoundedRectangle,
        CanvasTool.Ellipse => AppStrings.ToolEllipse,
        CanvasTool.Text => AppStrings.ToolText,
        CanvasTool.Number => AppStrings.ToolNumber,
        CanvasTool.Mosaic => AppStrings.ToolMosaic,
        CanvasTool.Blur => AppStrings.ToolBlur,
        CanvasTool.Mask => AppStrings.ToolMask,
        CanvasTool.Eyedropper => AppStrings.ToolEyedropper,
        CanvasTool.Crop => AppStrings.ToolCrop,
        _ => AppStrings.ToolSelect,
    };

    private void OnCropRatioClicked(object sender, RoutedEventArgs e)
    {
        CancelActiveGesture();
        _cropRatioIndex = (_cropRatioIndex + 1) % CropRatios.Length;
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    private string CropRatioText() => _cropRatioIndex switch
    {
        1 => "1:1",
        2 => "4:3",
        3 => "16:9",
        _ => AppStrings.CropRatioFree,
    };

    private void OnFlipHorizontalClicked(object sender, RoutedEventArgs e) =>
        ApplyTransformOp(TransformEditKind.Flip, new FlipOp(Horizontal: true));

    private void OnFlipVerticalClicked(object sender, RoutedEventArgs e) =>
        ApplyTransformOp(TransformEditKind.Flip, new FlipOp(Horizontal: false));

    private async void OnResizeClicked(object sender, RoutedEventArgs e) =>
        await ShowResizeDialogAsync();

    /// <summary>
    /// The one admission point for pipeline edits: the candidate is evaluated against this source
    /// before it enters the history, so an op the evaluator rejects (crop misses the canvas, output
    /// over the caps) reports on the status bar instead of poisoning paint.
    /// </summary>
    private bool ApplyTransformOp(TransformEditKind kind, TransformOp op)
    {
        if (_viewModel.IsReplacementPending || _viewModel.Editor.Document is not { } document)
            return false;
        CancelActiveGesture();

        var before = _viewModel.Editor.State.Transform;
        var after = before.Append(op);
        try
        {
            _ = TransformEvaluator.Evaluate(after, document.NativeSize);
        }
        catch (InvalidOperationException ex)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
            return false;
        }

        _viewModel.Editor.Apply(new TransformCommand(kind, before, after));
        _selectedAnnotation = default;
        return true;
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_viewModel.IsReplacementPending)
            return;
        CancelActiveGesture();
        if (_viewModel.Editor.Undo())
            _selectedAnnotation = default;
    }

    private void Redo()
    {
        if (_viewModel.IsReplacementPending)
            return;
        CancelActiveGesture();
        if (_viewModel.Editor.Redo())
            _selectedAnnotation = default;
    }

    private void DeleteSelection()
    {
        if (_viewModel.IsReplacementPending)
            return;
        CancelActiveGesture();
        var editor = _viewModel.Editor;
        if (_selectedAnnotation == default
            || editor.State.Find(_selectedAnnotation) is not { IsLocked: false })
            return;
        editor.Apply(new DeleteAnnotationCommand(editor.State, _selectedAnnotation));
        _selectedAnnotation = default;
    }

    /// <summary>
    /// Abandons drafts and crop review before a command mutates history. It also releases an active
    /// pointer before clearing visual state, so a later capture-lost event is harmless.
    /// </summary>
    private void CancelActiveGesture()
    {
        var releasePointers = _activePointerId is not null;
        _activePointerId = null;
        _lastPointer = null;
        _drawAnchor = null;
        _drawCurrent = null;
        _inkPoints.Clear();
        _draftTool = CanvasTool.Select;
        _cropInteraction.CancelAll();
        _dragAnnotation = default;
        _dragMoved = false;
        _activeSelectionHandle = SelectionHandle.None;
        _selectionTransformOrigin = null;
        _selectionTransformMoved = false;
        _selectionBandAnchor = null;
        _selectionBandCurrent = null;
        if (releasePointers)
            Canvas.ReleasePointerCaptures();
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    /// <summary>Esc abandons the in-progress authoring draft (SSOT §16.3) before it means
    /// "leave full screen". A selection drag is finalized by release, not canceled here.</summary>
    private void OnEscape()
    {
        if (_drawAnchor is not null || _inkPoints.Count > 0
            || _cropInteraction.Phase != CropInteractionPhase.Idle)
        {
            CancelActiveGesture();
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }
        ExitFullScreen();
    }

    // ---- M6 save / export / copy (FR-OUT-001~005, 008 strip default, 009; §10 원본 보호) ----

    /// <summary>The frame an export flattens from, owning the full re-decode when one was needed.</summary>
    private readonly record struct ExportFrame(SKImage Frame, bool OwnsFrame) : IDisposable
    {
        public void Dispose()
        {
            if (OwnsFrame)
                Frame.Dispose();
        }
    }

    /// <summary>Quick save writes to the tracked target; without one (or on Ctrl+Shift+S) the
    /// picker chooses. Returns true when bytes actually landed on disk.</summary>
    private async Task<bool> SaveAsync(bool quick)
    {
        if (_savingInProgress || _viewModel.IsReplacementPending
            || _viewModel.Editor.Document is not { } document || _snapshot is null)
            return false;
        _savingInProgress = true;
        try
        {
            if (quick && _saveTarget is { } target)
            {
                if (!_viewModel.Editor.IsModified && File.Exists(target.Path))
                {
                    SetStatusState(AppStrings.SaveNoChanges);
                    return true;
                }
                return await WriteTargetAsync(document, target);
            }
            try
            {
                return await SaveAsPickerAsync(document);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetStatusState($"{AppStrings.SaveFailed}: {ex.Message}");
                return false;
            }
        }
        finally
        {
            _savingInProgress = false;
        }
    }

    private async Task<bool> SaveAsPickerAsync(Core.Documents.ImageDocument document)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            SuggestedFileName = SuggestedSaveName(document),
        };
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        // First entry is the picker default: projects re-save as projects, images as PNG.
        if (document.Source.Kind == DocumentSourceKind.Project)
            picker.FileTypeChoices.Add(AppStrings.ProjectTypeName, [ProjectStore.Extension]);
        picker.FileTypeChoices.Add("PNG", [".png"]);
        picker.FileTypeChoices.Add("JPEG", [".jpg", ".jpeg"]);
        picker.FileTypeChoices.Add("WebP", [".webp"]);
        if (document.Source.Kind != DocumentSourceKind.Project)
            picker.FileTypeChoices.Add(AppStrings.ProjectTypeName, [ProjectStore.Extension]);

        var file = await picker.PickSaveFileAsync();
        if (file is null || _viewModel.Editor.Document?.Id != document.Id)
            return false;
        var path = file.Path;

        // §10: the original is overwritten only on an explicit confirmation, never as a default.
        if (document.Source is { Kind: DocumentSourceKind.File, Path: { } original }
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase)
            && !await ConfirmOverwriteOriginalAsync())
            return false;

        SaveTarget target = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ProjectStore.Extension => new SaveTarget(path, null),
            ".jpg" or ".jpeg" => new SaveTarget(path, ExportFormat.Jpeg),
            ".webp" => new SaveTarget(path, ExportFormat.WebP),
            _ => new SaveTarget(path, ExportFormat.Png),
        };
        return await WriteTargetAsync(document, target);
    }

    private async Task<bool> WriteTargetAsync(Core.Documents.ImageDocument document, SaveTarget target)
    {
        try
        {
            var options = target.Options ?? ExportOptions.Default;
            if (target.Options is null && target.ImageFormat is { } imageFormat)
            {
                // FR-OUT-008: only File/Project sources have metadata to offer keeping.
                var offerMetadata = document.Source.Kind
                    is DocumentSourceKind.File or DocumentSourceKind.Project;
                if (imageFormat is ExportFormat.Jpeg or ExportFormat.WebP || offerMetadata)
                {
                    if (await AskExportOptionsAsync(imageFormat, offerMetadata) is not { } picked)
                        return false;
                    options = picked;
                }
                target = target with { Options = options };
            }
            var token = _shutdownCts.Token;
            // Transaction token: the write persists exactly this state. MarkSaved applies only if
            // the editor still sits at it afterwards — an edit landing mid-write stays modified.
            var state = _viewModel.Editor.State;
            var stateId = _viewModel.Editor.CurrentStateId;
            SetStatusState(AppStrings.SaveInProgress);
            byte[] bytes;
            var metadataSkipped = false;
            if (target.ImageFormat is { } format)
            {
                var metadataSource = options.KeepMetadata
                    ? await TryGetMetadataSourceAsync(document) : null;
                using var export = await GetExportFrameAsync(document);
                var frame = export.Frame;
                (bytes, var metadataApplied) = await Task.Run(async () =>
                {
                    using var assets = await WarmExportAssetsAsync(state, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    using var flattened = DocumentFlattener.Flatten(
                        frame, document.NativeSize, state, assets);
                    var encoded = ImageExporter.Encode(flattened, format, options);
                    var applied = false;
                    if (metadataSource is not null
                        && ExportMetadata.TryExtractExif(metadataSource) is { } rawExif
                        && ExportMetadata.ScrubSensitive(rawExif, flattened.Width, flattened.Height)
                            is { } safeExif)
                    {
                        var embedded = ExportMetadata.Embed(encoded, format, safeExif);
                        applied = !ReferenceEquals(embedded, encoded);
                        encoded = embedded;
                    }
                    return (encoded, applied);
                }, token);
                // The user asked to keep metadata; a silent no-op would misreport what was saved.
                metadataSkipped = options.KeepMetadata && !metadataApplied;
            }
            else
            {
                var (sourceName, sourceBytes) = await GetProjectSourceAsync(document, token);
                var pages = _viewModel.CaptureProjectPages(_activeLayerId);
                var activePageIndex = document.SequenceKind == DocumentSequenceKind.Pages
                    ? document.CurrentFrameIndex
                    : 0;
                using var export = new ExportFrame(CopySnapshot(), OwnsFrame: true);
                var frame = export.Frame;
                bytes = await Task.Run(async () =>
                {
                    using var assets = await WarmExportAssetsAsync(state, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var preview = BuildPreviewPng(frame, document.NativeSize, state, assets);
                    return ProjectStore.Build(pages, activePageIndex, sourceName, sourceBytes, preview);
                }, token);
            }
            // The picker or the write may have straddled a replacement; never retarget or mark a
            // different document (checked again after the write for the same reason).
            if (_viewModel.Editor.Document?.Id != document.Id)
                return false;
            await Task.Run(() => AtomicFileWriter.Write(target.Path, bytes), token);
            if (_viewModel.Editor.Document?.Id != document.Id)
                return false;
            _saveTarget = target;
            if (target.ImageFormat is null)
                _viewModel.MarkAllPagesSaved(stateId);
            else
                _viewModel.Editor.MarkSaved(stateId);
            _viewModel.RefreshStatus();
            UpdateStatusBar();
            SetStatusState(metadataSkipped ? AppStrings.SaveDoneNoMetadata : AppStrings.SaveDone);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            SetStatusState($"{AppStrings.SaveFailed}: {ex.Message}");
            return false;
        }
    }

    /// <summary>FR-OUT-001: the flattened edit result goes to the clipboard with transparency.</summary>
    private async Task CopyToClipboardAsync()
    {
        if (_savingInProgress || _viewModel.Editor.Document is not { } document || _snapshot is null)
            return;
        try
        {
            var token = _shutdownCts.Token;
            var state = _viewModel.Editor.State;
            using var export = await GetExportFrameAsync(document);
            var frame = export.Frame;
            var png = await Task.Run(async () =>
            {
                using var assets = await WarmExportAssetsAsync(state, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var flattened = DocumentFlattener.Flatten(
                    frame, document.NativeSize, state, assets);
                return ImageExporter.Encode(flattened, ExportFormat.Png);
            }, token);
            if (_viewModel.Editor.Document?.Id != document.Id)
                return;
            await _clipboard.SetImagePngAsync(png, token);
            // FR-CAP-005: the capture watcher must not mistake this copy for a new capture.
            AppServices.Capture?.NoteInternalCopy(png);
            SetStatusState(AppStrings.CopyDone);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or InvalidDataException or System.Runtime.InteropServices.COMException)
        {
            SetStatusState($"{AppStrings.SaveFailed}: {ex.Message}");
        }
    }

    /// <summary>The frame an export flattens from — always an owned copy, since a replacement
    /// disposes the UI snapshot mid-flight. A reduced-preview document re-decodes its source at
    /// full resolution so the export carries real pixels (§10); when that is impossible the export
    /// fails explicitly rather than silently upscaling preview pixels to the native output size.</summary>
    private async Task<ExportFrame> GetExportFrameAsync(Core.Documents.ImageDocument document)
    {
        if (document.WasAnimationFlattened && document.IsReducedPreview)
            throw new InvalidOperationException(AppStrings.SaveFullResUnavailable);
        if (!document.IsReducedPreview)
            return new ExportFrame(CopySnapshot(), OwnsFrame: true);

        var sourceFrameIndex = document.SequenceKind == DocumentSequenceKind.Pages
            ? document.CurrentFrameIndex
            : 0;
        var pixels = (long)document.NativeSize.Width * document.NativeSize.Height;
        var limits = Core.Imaging.InputLimits.Default with
        {
            DisplayByteBudget = Math.Min(2L * 1024 * 1024 * 1024, Math.Max(
                Core.Imaging.InputLimits.Default.DisplayByteBudget,
                pixels * Core.Imaging.InputLimits.DisplayBytesPerPixel)),
        };
        var loader = AppServices.CreateDocumentLoader(limits);
        Core.Documents.ImageDocument? full = null;
        if (document.Source is { Kind: DocumentSourceKind.File, Path: { } path })
        {
            EnsureSourceUnchanged(document, path);
            full = await loader.LoadFileAsync(path, _shutdownCts.Token);
        }
        else if (document.Source.Kind == DocumentSourceKind.Project
            && _viewModel.OpenedProject is { } project)
        {
            full = await loader.LoadMemoryAsync(
                project.SourceBytes, document.Source, _shutdownCts.Token);
        }
        if (full is null || full.IsReducedPreview)
        {
            full?.Dispose();
            throw new InvalidOperationException(AppStrings.SaveFullResUnavailable);
        }
        using (full)
        {
            if (sourceFrameIndex > 0)
            {
                await full.LoadFrameAsync(
                    sourceFrameIndex,
                    new DecodeRequest(limits),
                    forceRerender: false,
                    _shutdownCts.Token);
                if (full.IsReducedPreview)
                    throw new InvalidOperationException(AppStrings.SaveFullResUnavailable);
            }
            return new ExportFrame(full.Frame.ToSKImage(), OwnsFrame: true);
        }
    }

    /// <summary>Owned raster copy of the UI snapshot, safe to hand to a worker flatten.</summary>
    private SKImage CopySnapshot()
    {
        var frame = _snapshot ?? throw new InvalidOperationException("No frame to export.");
        using var pixmap = frame.PeekPixels();
        if (pixmap is not null)
            return SKImage.FromPixelCopy(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes);
        using var bitmap = SKBitmap.FromImage(frame)
            ?? throw new InvalidOperationException("Could not read the frame pixels.");
        return SKImage.FromBitmap(bitmap);
    }

    private SnapshotLease AcquireSnapshotLease()
    {
        lock (_snapshotLeaseSync)
        {
            var image = _snapshot
                ?? throw new InvalidOperationException("No background to embed.");
            _snapshotLeaseCounts.TryGetValue(image, out var count);
            _snapshotLeaseCounts[image] = checked(count + 1);
            return new SnapshotLease(this, image);
        }
    }

    private void SetSnapshot(SKImage? image)
    {
        SKImage? dispose = null;
        lock (_snapshotLeaseSync)
        {
            var previous = _snapshot;
            if (ReferenceEquals(previous, image))
                return;
            _snapshot = image;
            if (previous is not null)
            {
                if (_snapshotLeaseCounts.ContainsKey(previous))
                    _deferredSnapshotDisposals.Add(previous);
                else
                    dispose = previous;
            }
        }
        DisposeSnapshotOnUi(dispose);
    }

    private void ReleaseSnapshotLease(SKImage image)
    {
        SKImage? dispose = null;
        lock (_snapshotLeaseSync)
        {
            if (!_snapshotLeaseCounts.TryGetValue(image, out var count))
                return;
            if (count > 1)
            {
                _snapshotLeaseCounts[image] = count - 1;
            }
            else
            {
                _snapshotLeaseCounts.Remove(image);
                if (_deferredSnapshotDisposals.Remove(image))
                    dispose = image;
            }
        }
        DisposeSnapshotOnUi(dispose);
    }

    private void DisposeSnapshotOnUi(SKImage? image)
    {
        if (image is null)
            return;
        if (DispatcherQueue.HasThreadAccess)
            image.Dispose();
        else
            _ = DispatcherQueue.TryEnqueue(image.Dispose);
    }

    private async Task<byte[]> EncodeSnapshotPngAsync(CancellationToken cancellationToken)
    {
        using var lease = AcquireSnapshotLease();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var data = lease.Image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Background PNG encoding failed.");
            return data.ToArray();
        }, cancellationToken);
    }

    /// <summary>§10: a save must re-read the same file the user is looking at. Length + last-write
    /// identity is captured at load time; an externally changed or missing original refuses the
    /// save instead of silently combining on-screen edits with different pixels.</summary>
    private static void EnsureSourceUnchanged(Core.Documents.ImageDocument document, string path)
    {
        if (!SourceIsUnchanged(document, path))
            throw new InvalidOperationException(AppStrings.SaveSourceChanged);
    }

    private static bool SourceIsUnchanged(
        Core.Documents.ImageDocument document,
        string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            && info.Length == document.SourceFileBytes
            && info.LastWriteTimeUtc == document.SourceLastWriteUtc;
    }

    /// <summary>Reading a 512MiB original for a few KB of EXIF is a memory hazard, not a
    /// throughput concern; files above this contribute no metadata (status says so).</summary>
    private const long MetadataReadBudget = 64L * 1024 * 1024;

    /// <summary>Source bytes for the FR-OUT-008 keep option. Best-effort by contract: metadata is
    /// auxiliary, so a changed/missing/oversized original contributes nothing rather than failing
    /// the save (unlike the pixel path, which refuses — <see cref="EnsureSourceUnchanged"/>);
    /// the saved-without-metadata status keeps the user informed.</summary>
    private async Task<byte[]?> TryGetMetadataSourceAsync(Core.Documents.ImageDocument document)
    {
        switch (document.Source)
        {
            case { Kind: DocumentSourceKind.File, Path: { } path }:
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != document.SourceFileBytes
                    || info.LastWriteTimeUtc != document.SourceLastWriteUtc
                    || info.Length > MetadataReadBudget)
                    return null;
                return await File.ReadAllBytesAsync(path, _shutdownCts.Token);
            }
            case { Kind: DocumentSourceKind.Project } when _viewModel.OpenedProject is { } project:
                return project.SourceBytes;
            default:
                return null;
        }
    }

    /// <summary>Fresh per-export cache: the UI-owned cache is pruned and cleared on the UI thread
    /// and must never be shared with a worker flatten.</summary>
    private static async Task<RasterAssetImageCache> WarmExportAssetsAsync(
        DocumentState state, CancellationToken token)
    {
        var cache = new RasterAssetImageCache();
        try
        {
            foreach (var asset in state.Assets)
                await cache.WarmAsync(asset, token).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    /// <summary>What a project embeds as its background (§7.10 embedded-source): the original file
    /// bytes when they are still there, the project's own embedded source on re-save, and the
    /// rendered background for clipboard/capture documents.</summary>
    private async Task<(string Name, byte[] Bytes)> GetProjectSourceAsync(
        Core.Documents.ImageDocument document,
        CancellationToken cancellationToken)
    {
        if (document.WasAnimationFlattened && document.IsReducedPreview)
            throw new InvalidOperationException(AppStrings.SaveFullResUnavailable);
        if (document.WasAnimationFlattened)
        {
            var flattened = await EncodeSnapshotPngAsync(cancellationToken);
            return ("flattened-animation-frame.png", flattened);
        }

        switch (document.Source)
        {
            case { Kind: DocumentSourceKind.File, Path: { } path }:
                // Identity-checked: a changed/missing original refuses the save — never a silent
                // switch to different pixels or a rendered stand-in (§10).
                EnsureSourceUnchanged(document, path);
                return (Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken));
            case { Kind: DocumentSourceKind.Project } when _viewModel.OpenedProject is { } project:
                return (project.SourceName, project.SourceBytes);
            default:
            {
                var bytes = await EncodeSnapshotPngAsync(cancellationToken);
                return ("background.png", bytes);
            }
        }
    }

    private async Task<(string Name, byte[] Bytes)> GetRecoveryProjectSourceAsync(
        Core.Documents.ImageDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Source is not { Kind: DocumentSourceKind.File, Path: { } path })
            return await GetProjectSourceAsync(document, cancellationToken);

        if (!CanUseRenderedRecoveryFallback(document))
            return await GetProjectSourceAsync(document, cancellationToken);

        try
        {
            if (SourceIsUnchanged(document, path))
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                // Recheck after the read so an external replacement cannot race the first identity
                // check and become the embedded recovery background.
                if (SourceIsUnchanged(document, path))
                    return (Path.GetFileName(path), bytes);
            }
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The bounded rendered snapshot below is the fidelity-safe recovery source.
        }

        return ("recovered-background.png",
            await EncodeSnapshotPngAsync(cancellationToken));
    }

    private static bool CanUseRenderedRecoveryFallback(
        Core.Documents.ImageDocument document) =>
        RecoverySourceFallbackPolicy.CanEmbedRenderedBackground(
            document.SequenceKind,
            document.IsReducedPreview,
            document.NativeSize,
            new Core.Imaging.PixelSize(document.Frame.Width, document.Frame.Height));

    /// <summary>Small preview for the container gallery view, rendered directly at the preview
    /// scale on the caller's (worker) thread — no native-size intermediate, and never the UI
    /// thread (NFR-PERF-006). View-quality is fine here.</summary>
    private static byte[]? BuildPreviewPng(
        SKImage frame, Core.Imaging.PixelSize nativeSize, DocumentState state,
        RasterAssetImageCache assets)
    {
        var evaluation = Core.Documents.Layers.TransformEvaluator.Evaluate(state.Transform, nativeSize);
        var output = evaluation.OutputSize;
        var scale = MathF.Min(1f, 512f / Math.Max(output.Width, output.Height));
        var width = Math.Max(1, (int)(output.Width * scale));
        var height = Math.Max(1, (int)(output.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(
            surface.Canvas, frame, nativeSize, state, evaluation,
            SKMatrix.CreateScale(scale, scale), assetCache: assets);
        using var preview = surface.Snapshot();
        using var encoded = preview.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    private string SuggestedSaveName(Core.Documents.ImageDocument document) =>
        document.Source.Path is { } sourcePath
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : AppStrings.SaveDefaultName;

    private async Task<ExportOptions?> AskExportOptionsAsync(ExportFormat format, bool offerMetadata)
    {
        if (Content?.XamlRoot is null)
            return ExportOptions.Default;
        var quality = new NumberBox
        {
            Header = AppStrings.ExportQualityLabel,
            Value = 90,
            Minimum = 1,
            Maximum = 100,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Visibility = format == ExportFormat.Png ? Visibility.Collapsed : Visibility.Visible,
        };
        var lossless = new CheckBox
        {
            Content = AppStrings.ExportLosslessLabel,
            Visibility = format == ExportFormat.WebP ? Visibility.Visible : Visibility.Collapsed,
        };
        // FR-OUT-008 (Q6 = b): unchecked default = full strip; keeping still scrubs GPS,
        // MakerNote, serials and the pre-edit thumbnail (ExportMetadata).
        var keepMetadata = new CheckBox
        {
            Content = AppStrings.ExportKeepMetadataLabel,
            Visibility = offerMetadata ? Visibility.Visible : Visibility.Collapsed,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.ExportOptionsTitle,
            Content = new StackPanel { Spacing = 8, Children = { quality, lossless, keepMetadata } },
            PrimaryButtonText = AppStrings.DialogApply,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog, editScoped: true) != ContentDialogResult.Primary)
            return null;
        return new ExportOptions
        {
            Quality = double.IsFinite(quality.Value) ? Math.Clamp((int)quality.Value, 1, 100) : 90,
            WebPLossless = lossless.IsChecked == true,
            KeepMetadata = keepMetadata.IsChecked == true,
        };
    }

    private async Task<bool> ConfirmOverwriteOriginalAsync()
    {
        if (Content?.XamlRoot is null)
            return false;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.OverwriteTitle,
            Content = AppStrings.OverwriteBody,
            PrimaryButtonText = AppStrings.OverwriteConfirm,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog, editScoped: false) == ContentDialogResult.Primary;
    }

    // ---- unsaved-change confirmation (FR-HIST-005) ----

    /// <summary>
    /// The close path cannot await, so it cancels the close, resolves edits and background stores,
    /// then re-closes on approval.
    /// The prompt is three-way (저장/저장 안 함/취소, FR-HIST-005); Save is resolved inside
    /// <see cref="ConfirmDiscardAsync"/> before the decision reaches the gate or this path.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
            return;

        args.Cancel = true;
        if (_closePromptOpen)
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            _closePromptOpen = true;
            try
            {
                if (!_viewModel.CanCloseWithoutPrompt()
                    && await ConfirmDiscardAsync() != DiscardDecision.Discard)
                    return;
                if (AppServices.Windows is { } windows)
                    await windows.PrepareCloseAsync(this);
                _closeApproved = true;
                Close();
            }
            catch (Exception ex)
            {
                SetStatusState($"{AppStrings.RecoveryFailed}: {ex.Message}");
            }
            finally
            {
                _closePromptOpen = false;
            }
        });
    }

    private async Task<DiscardDecision> ConfirmDiscardAsync()
    {
        if (Content?.XamlRoot is null)
            return DiscardDecision.Discard; // No UI to ask through (unattended runs).

        // Three-way since M6 (FR-HIST-005): 저장 / 저장 안 함 / 취소. Save resolves here — a
        // successful save means the edits are persisted and the replacement may proceed.
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.DiscardTitle,
            Content = AppStrings.DiscardBody,
            PrimaryButtonText = AppStrings.DialogSaveButton,
            SecondaryButtonText = AppStrings.DialogDontSaveButton,
            CloseButtonText = AppStrings.DiscardCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        // Refused-while-busy comes back as None → Cancel: never a second dialog, never data loss.
        var result = await ShowDialogAsync(dialog, editScoped: false);
        if (result == ContentDialogResult.Primary)
            return await SaveAsync(quick: true) ? DiscardDecision.Discard : DiscardDecision.Cancel;
        return result == ContentDialogResult.Secondary ? DiscardDecision.Discard : DiscardDecision.Cancel;
    }

    // ---- dialog coordination ----

    /// <summary>
    /// WinUI allows one ContentDialog per root (a second ShowAsync throws), and the discard guard,
    /// the close path and the M3 edit dialogs can all ask — every dialog goes through here. A
    /// request while one is open is refused with None rather than queued: for a discard prompt
    /// None means Cancel (fail-closed), for an edit dialog it means nothing happens.
    /// Edit-scoped dialogs are torn down on document replacement — their canvas is gone; the
    /// discard prompt must survive it, being the replacement flow itself.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, bool editScoped)
    {
        if (_activeDialog is not null)
            return ContentDialogResult.None;
        _activeDialog = dialog;
        _activeDialogEditScoped = editScoped;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _activeDialog = null;
        }
    }

    private void CancelEditDialog()
    {
        if (_activeDialog is { } dialog && _activeDialogEditScoped)
            dialog.Hide(); // its ShowAsync completes with None
    }

    private async Task ShowResizeDialogAsync()
    {
        if (_viewModel.IsReplacementPending || _viewModel.Editor.Document is not { } document)
            return;
        if (Content?.XamlRoot is null)
            return;
        var documentId = document.Id;
        var revision = _viewModel.Editor.Revision;
        var current = Evaluation(document).OutputSize;
        var aspect = current.Width / (double)current.Height;

        var widthBox = new NumberBox
        {
            Header = AppStrings.ResizeWidthLabel,
            Value = current.Width,
            Minimum = 1,
            Maximum = TransformEvaluator.MaxOutputDimension,
        };
        var heightBox = new NumberBox
        {
            Header = AppStrings.ResizeHeightLabel,
            Value = current.Height,
            Minimum = 1,
            Maximum = TransformEvaluator.MaxOutputDimension,
        };
        var percentBox = new NumberBox
        {
            Header = AppStrings.ResizePercentLabel,
            Value = 100,
            Minimum = 1,
            Maximum = 6400,
        };
        var keepAspect = new CheckBox { Content = AppStrings.ResizeKeepAspect, IsChecked = true };

        // FR-EDIT-002: px and % stay in sync; the aspect lock drives the passive axis.
        var syncing = false;
        widthBox.ValueChanged += (_, _) => Sync(() =>
        {
            if (!double.IsFinite(widthBox.Value))
                return;
            if (keepAspect.IsChecked == true)
                heightBox.Value = Math.Max(1, Math.Round(widthBox.Value / aspect));
            percentBox.Value = Math.Round(widthBox.Value / current.Width * 100, 1);
        });
        heightBox.ValueChanged += (_, _) => Sync(() =>
        {
            if (!double.IsFinite(heightBox.Value))
                return;
            if (keepAspect.IsChecked == true)
            {
                widthBox.Value = Math.Max(1, Math.Round(heightBox.Value * aspect));
                percentBox.Value = Math.Round(widthBox.Value / current.Width * 100, 1);
            }
        });
        percentBox.ValueChanged += (_, _) => Sync(() =>
        {
            if (!double.IsFinite(percentBox.Value))
                return;
            widthBox.Value = Math.Max(1, Math.Round(current.Width * percentBox.Value / 100));
            heightBox.Value = Math.Max(1, Math.Round(current.Height * percentBox.Value / 100));
        });

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(widthBox);
        panel.Children.Add(heightBox);
        panel.Children.Add(percentBox);
        panel.Children.Add(keepAspect);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.ResizeTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.DialogApply,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog, editScoped: true) != ContentDialogResult.Primary)
            return;
        if (_viewModel.IsReplacementPending || _viewModel.Editor.Document is not { } target
            || target.Id != documentId || _viewModel.Editor.Revision != revision)
            return;
        if (!double.IsFinite(widthBox.Value) || !double.IsFinite(heightBox.Value))
            return;
        var size = new PixelSize((int)Math.Round(widthBox.Value), (int)Math.Round(heightBox.Value));
        if (size.Width < 1 || size.Height < 1 || size == current)
            return;
        ApplyTransformOp(TransformEditKind.Resize, new ResizeOp(size));
        return;

        void Sync(Action update)
        {
            if (syncing)
                return;
            syncing = true;
            try
            {
                update();
            }
            finally
            {
                syncing = false;
            }
        }
    }

    private void FitToViewport()
    {
        _transform.FitToViewport();
        QueueScaleDependentRerender();
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    private void ActualSize()
    {
        _transform.ActualSize();
        QueueScaleDependentRerender();
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    private async Task OpenPickerAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var extension in Core.Imaging.ImageFormatCatalog.ViewableExtensions)
            picker.FileTypeFilter.Add(extension);
        picker.FileTypeFilter.Add(ProjectStore.Extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            _viewModel.OpenFiles(files.Select(f => f.Path).ToArray());
    }

    private async Task OpenFromClipboardAsync()
    {
        try
        {
            var payload = await _clipboard.TryGetImageAsync(AppServices.Limits.MaxFileBytes, CancellationToken.None);
            if (payload is not null)
                _viewModel.OpenClipboardBytes(payload.Bytes, payload.Format);
        }
        catch (Exception ex)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_viewModel.Editor.Document is null)
        {
            await OpenFromClipboardAsync();
            return;
        }
        if (_viewModel.IsReplacementPending)
            return;

        _pasteCancellation?.Cancel();
        _pasteCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _pasteCancellation = cancellation;
        var targetId = _viewModel.Editor.Document.Id;
        var revision = _viewModel.Editor.Revision;
        try
        {
            var payload = await _clipboard.TryGetImageAsync(
                AnnotationValidator.MaxRasterAssetBytes, cancellation.Token);
            if (payload is null)
                return;
            var asset = new RasterAsset
            {
                Id = Guid.NewGuid(),
                EncodedBytes = payload.Bytes.ToImmutableArray(),
                PixelSize = new PixelSize(1, 1),
                Format = payload.Format,
            };
            var retained = checked(
                _viewModel.Editor.State.Assets.Sum(item => item.EstimatedRetainedBytes)
                + asset.EstimatedRetainedBytes);
            if (retained > AnnotationValidator.MaxRasterAssetBytes)
                throw new InvalidDataException(
                    $"Raster assets exceed the {AnnotationValidator.MaxRasterAssetBytes:N0} byte document limit.");

            using var decoded = await _documentLoader.LoadMemoryAsync(
                payload.Bytes, DocumentSource.FromClipboard(), cancellation.Token);
            asset = asset with { PixelSize = decoded.NativeSize, Format = decoded.Format.ToString() };
            AnnotationValidator.Validate(asset);
            await _assetCache.WarmAsync(asset, decoded.Frame, cancellation.Token);

            if (_viewModel.IsReplacementPending
                || _viewModel.Editor.Document is not { } target
                || target.Id != targetId
                || _viewModel.Editor.Revision != revision)
                return;
            var size = decoded.NativeSize;
            var scale = MathF.Min(1f, MathF.Min(
                target.NativeSize.Width * 0.5f / size.Width,
                target.NativeSize.Height * 0.5f / size.Height));
            var width = MathF.Max(1f, size.Width * scale);
            var height = MathF.Max(1f, size.Height * scale);
            var annotation = new ImageAnnotation
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Bounds = new RectF(
                    (target.NativeSize.Width - width) / 2f,
                    (target.NativeSize.Height - height) / 2f,
                    width,
                    height),
            };
            if (!CanEditActiveLayer())
                return;
            _viewModel.Editor.Apply(new AddImageAnnotationCommand(asset, annotation, _activeLayerId));
            _selectedAnnotation = annotation.Id;
            _tool = CanvasTool.Select;
            UpdateLayerPanel();
            UpdateToolUi();
            UpdateEditCommands();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_pasteCancellation, cancellation))
                _pasteCancellation = null;
            cancellation.Dispose();
            _assetCache.Prune(_viewModel.Editor.State);
        }
    }

    private void ToggleFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        else
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void ExitFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
    }

    // ---- drag & drop (FR-APP-003: files and folders) ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // async void event handler: any escaping exception kills the process — contain everything.
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case StorageFile file when Core.Imaging.ImageFormatCatalog.IsViewable(file.Path):
                        paths.Add(file.Path);
                        break;
                    case StorageFolder folder:
                        {
                            var first = Directory.EnumerateFiles(folder.Path)
                                .Where(Core.Imaging.ImageFormatCatalog.IsViewable)
                                .OrderBy(Path.GetFileName, Core.Navigation.NaturalStringComparer.Instance)
                                .FirstOrDefault();
                            if (first is not null)
                                paths.Add(first);
                            break;
                        }
                }
            }
            if (paths.Count > 0)
                _viewModel.OpenFiles(paths);
        }
        // COMException included: GetStorageItemsAsync is a WinRT call, and an escape here kills the
        // process (async void) — exactly what the containment above promises against.
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
    }

    // ---- unattended verification hooks ----

    /// <summary>--smoke-open: load a file, report the session outcome as JSON, exit.</summary>
    public void ConfigureSmoke(
        string path,
        string? resultPath,
        string? projectPath = null,
        bool captureExercise = false,
        bool isolatedCodecExercise = false)
    {
        _resultPath = resultPath ?? Path.Combine(Path.GetTempPath(), "ezy-smoke.json");
        _smokeProjectPath = projectPath;
        _smokeCaptureExercise = captureExercise;
        _isolatedCodecExercise = isolatedCodecExercise;
        OpenFiles([path]);
    }

    private bool _smokeCaptureExercise;
    private bool _isolatedCodecExercise;

    /// <summary>--smoke-hold: open, apply one edit, stay open — an external UIA gate then drives
    /// the real close prompt's 저장 branch against the document's own save target.</summary>
    public void ConfigureEditHold(string path)
    {
        _holdEditPending = true;
        OpenFiles([path]);
    }

    internal void ConfigureRecoverySmokeSeed(string path, string resultPath)
    {
        _recoverySmokeResultPath = resultPath;
        _recoverySmokeSeedPending = true;
        _holdEditPending = true;
        OpenFiles([path]);
    }

    internal void ConfigureRecoverySmokeVerification(string resultPath)
    {
        _recoverySmokeResultPath = resultPath;
        if (AppServices.PendingRecoveries.Count == 0)
        {
            WriteRecoverySmokeResult(new
            {
                state = "NoCandidates",
                candidateCount = 0,
                restoredCount = 0,
            });
        }
    }

    private string? _smokeProjectPath;
    private bool _holdEditPending;

    private void MaybeApplyHoldEdit()
    {
        if (!_holdEditPending || _viewModel.Session.State != SessionState.Ready
            || _viewModel.Editor.Document is null)
            return;
        _holdEditPending = false;
        ApplySmokeQuickEdit();
    }

    private void MaybeRunRecoverySmokeSeed()
    {
        if (!_recoverySmokeSeedPending)
            return;
        if (_viewModel.Session.State == SessionState.Failed)
        {
            _recoverySmokeSeedPending = false;
            WriteRecoverySmokeResult(new
            {
                state = "OpenFailed",
                error = _viewModel.Session.LastError?.GetType().Name,
            });
            return;
        }
        if (_viewModel.Session.State != SessionState.Ready
            || !_viewModel.Editor.IsModified)
        {
            return;
        }
        _recoverySmokeSeedPending = false;
        _ = WaitForRecoverySmokeCheckpointAsync();
    }

    private async Task WaitForRecoverySmokeCheckpointAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var enumeration = await Task.Run(
                    AppServices.RecoveryStore.EnumerateSummaryState);
                var summary = enumeration.Summaries.FirstOrDefault(value =>
                    value.SessionId == AppServices.RecoverySessionId
                    && value.WindowId == RecoveryWindowId);
                if (summary is not null)
                {
                    WriteRecoverySmokeResult(new
                    {
                        state = "SeedReady",
                        checkpointPayloadBytes = summary.PayloadLength,
                        markerCount = AppServices.RecoveryStore.EnumerateCrashMarkers().Count,
                        isModified = _viewModel.Editor.IsModified,
                    });
                    return;
                }
                await Task.Delay(50);
            }
            WriteRecoverySmokeResult(new { state = "CheckpointTimeout" });
        }
        catch (Exception ex)
        {
            WriteRecoverySmokeResult(new
            {
                state = "CheckpointError",
                error = ex.GetType().Name,
            });
        }
    }

    private void WriteRecoverySmokeResult(object result)
    {
        if (_recoverySmokeResultPath is not { } resultPath
            || Interlocked.Exchange(ref _recoverySmokeResultWritten, 1) != 0)
        {
            return;
        }
        try
        {
            AtomicFileWriter.Write(
                resultPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            SetStatusState($"Recovery smoke output failed: {ex.GetType().Name}");
        }
    }

    private sealed record CaptureExerciseResult(
        bool DupIgnored, bool HashDupIgnored, bool NoticeShown, bool NoticeOpened, bool AutoOpened,
        bool OfficialOpened);

    /// <summary>Drives the capture policy through a listener-less coordinator: marker/hash dedup,
    /// the passive notice (shown + opened), and the armed auto-open — with synthetic payloads,
    /// never the user's clipboard (FR-CAP-001~005 minus the real overlay, which is manual).</summary>
    private async Task<CaptureExerciseResult> ExerciseCaptureAsync()
    {
        using var coordinator = new Capture.Snipping.CaptureCoordinator(
            new Capture.Snipping.CaptureCoordinatorOptions { ResolveTarget = () => this },
            listen: false);
        // Replacement prompts would block an unattended run; captures land on clean documents.
        _viewModel.Editor.MarkSaved();

        static Capture.Clipboard.ClipboardImagePayload Png(int width, int height)
        {
            using var surface = SKSurface.Create(new SKImageInfo(
                width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(new SKColor(0x2E, 0x7D, 0x32));
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new Capture.Clipboard.ClipboardImagePayload(
                data.ToArray(), Capture.Clipboard.ClipboardImagePayload.Png);
        }

        var before = _viewModel.Editor.Document?.Id;
        coordinator.HandlePayload(Png(5, 5), hasMarker: true);
        var dupIgnored = _viewModel.Editor.Document?.Id == before && !CaptureBar.IsOpen;

        var internalCopy = Png(6, 6);
        coordinator.NoteInternalCopy(internalCopy.Bytes);
        coordinator.HandlePayload(internalCopy, hasMarker: false);
        var hashDupIgnored = _viewModel.Editor.Document?.Id == before && !CaptureBar.IsOpen;

        coordinator.HandlePayload(Png(7, 3), hasMarker: false);
        var noticeShown = CaptureBar.IsOpen && _viewModel.Editor.Document?.Id == before;
        OpenPendingCaptureNotice();
        var noticeOpened = await WaitForDocumentSizeAsync(7, 3);

        _viewModel.Editor.MarkSaved();
        coordinator.ArmWithoutLaunch();
        coordinator.HandlePayload(Png(9, 4), hasMarker: false);
        var autoOpened = await WaitForDocumentSizeAsync(9, 4);

        // Official redirect path (Q7=b): request → callback with the captured correlation id →
        // injected token redeem → auto-open. The real Snipping Tool is never launched.
        _viewModel.Editor.MarkSaved();
        var officialPayload = Png(11, 6);
        string? correlationId = null;
        using var official = new Capture.Snipping.CaptureCoordinator(
            new Capture.Snipping.CaptureCoordinatorOptions
            {
                ResolveTarget = () => this,
                LaunchOfficialCaptureAsync = id =>
                {
                    correlationId = id;
                    return Task.FromResult(true);
                },
                RedeemTokenAsync = (_, _) =>
                    Task.FromResult<Capture.Clipboard.ClipboardImagePayload?>(officialPayload),
            }, listen: false);
        await official.RequestCaptureAsync(this);
        await official.HandleProtocolResponseAsync(new Uri(
            $"{Capture.Snipping.SnipProtocol.RedirectUri}?code=200&reason=Success"
            + $"&x-request-correlation-id={correlationId}&file-access-token=smoke-token"));
        var officialOpened = await WaitForDocumentSizeAsync(11, 6);

        return new CaptureExerciseResult(
            dupIgnored, hashDupIgnored, noticeShown, noticeOpened, autoOpened, officialOpened);
    }

    private async Task<bool> WaitForDocumentSizeAsync(int width, int height)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (_viewModel.Editor.Document is { } document
                && document.NativeSize.Width == width && document.NativeSize.Height == height)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>One deterministic annotation, so an unattended dirty save writes real bytes.</summary>
    private void ApplySmokeQuickEdit()
    {
        if (_viewModel.Editor.Document is not { NativeSize: { IsEmpty: false } native })
            return;
        var unit = MathF.Max(2f, MathF.Min(native.Width, native.Height) / 100f);
        _viewModel.Editor.Apply(new AddAnnotationCommand(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(42 * unit, 30 * unit, 8 * unit, 6 * unit),
            StrokeArgb = 0xFF7B_1FA2,
            StrokeWidth = unit / 3f,
        }, _activeLayerId));
    }

    /// <summary>--bench-open24mp: measure load-start → first Ready paint (NFR-PERF-002).</summary>
    public void ConfigureFirstPaintBench(string path, string? resultPath)
    {
        _resultPath = resultPath ?? Path.Combine(Path.GetTempPath(), "ezy-first-paint.json");
        _firstPaintWatch = Stopwatch.StartNew();
        OpenFiles([path]);
    }

    /// <summary>--bench-startup: measure process entry to the first default-window frame.</summary>
    public void ConfigureStartupBench(
        string resultPath,
        long processStartTimestamp,
        Func<Task> completeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        ArgumentNullException.ThrowIfNull(completeAsync);
        if (Content is not FrameworkElement root)
            throw new InvalidOperationException("The startup benchmark requires a framework root.");
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            root.Loaded -= loaded;
            EventHandler<object>? rendering = null;
            rendering = (_, _) =>
            {
                CompositionTarget.Rendering -= rendering;
                var elapsed = Stopwatch.GetElapsedTime(processStartTimestamp).TotalMilliseconds;
                _ = DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var assembly = typeof(ViewerWindow).Assembly;
                        string? packageVersion = null;
                        if (Capture.Snipping.PackageIdentity.HasIdentity)
                        {
                            var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
                            packageVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                        }
                        var result = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            state = "Ready",
                            startupMs = elapsed,
                            targetMs = 1500,
                            passed = elapsed <= 1500,
                            measurement = "main-entry-to-first-composition-frame",
                            normalStartupPipeline = true,
                            packaged = Capture.Snipping.PackageIdentity.HasIdentity,
                            packageVersion,
                            assemblyVersion = assembly.GetName().Version?.ToString(),
                            buildConfiguration = assembly
                                .GetCustomAttribute<AssemblyConfigurationAttribute>()?
                                .Configuration,
                            framework = RuntimeInformation.FrameworkDescription,
                            os = RuntimeInformation.OSDescription,
                            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                            processorCount = Environment.ProcessorCount,
                            measuredAtUtc = DateTimeOffset.UtcNow,
                        });
                        AtomicFileWriter.Write(resultPath, result);
                    }
                    finally
                    {
                        try
                        {
                            await completeAsync();
                        }
                        finally
                        {
                            Application.Current.Exit();
                        }
                    }
                });
            };
            CompositionTarget.Rendering += rendering;
        };
        root.Loaded += loaded;
    }

    private async void MaybeWriteUnattendedResult()
    {
        try
        {
            if (_resultPath is null || _unattendedFlowStarted)
                return;
            var state = _viewModel.Session.State;
            if (state is not (SessionState.Ready or SessionState.Failed))
                return;
            if (_firstPaintWatch is { IsRunning: true })
                return; // wait for the first painted frame
            _unattendedFlowStarted = true;

            // Smoke mode also exercises resize + fullscreen round-trip on the GL surface.
            if (_firstPaintWatch is null && state == SessionState.Ready)
            {
                await ExerciseWindowAsync();
                _windowExercised = true;
            }

            // --smoke-project: drive the real save path (picker excluded) end to end (FR-OUT-002/009).
            var projectSaved = false;
            var quickResaved = false;
            if (_smokeProjectPath is { } smokeProject && state == SessionState.Ready
                && _viewModel.Editor.Document is { } smokeDocument)
            {
                projectSaved = await WriteTargetAsync(
                    smokeDocument, new SaveTarget(smokeProject, null));
                if (projectSaved)
                {
                    // Quick re-save must be a real dirty write, not the clean short-circuit:
                    // one deterministic edit, then the product Ctrl+S path, proven by new bytes.
                    var before = System.Security.Cryptography.SHA256.HashData(
                        await File.ReadAllBytesAsync(smokeProject));
                    ApplySmokeQuickEdit();
                    var wrote = _viewModel.Editor.IsModified && await SaveAsync(quick: true);
                    var after = System.Security.Cryptography.SHA256.HashData(
                        await File.ReadAllBytesAsync(smokeProject));
                    quickResaved = wrote && !before.AsSpan().SequenceEqual(after)
                        && !_viewModel.Editor.IsModified;
                }
            }

            // --smoke-capture: exercise the capture policy end to end without touching the real
            // clipboard or the real snipping overlay (both are user-owned surfaces).
            var capture = new CaptureExerciseResult(false, false, false, false, false, false);
            bool? launcherSupported = null;
            if (_smokeCaptureExercise && state == SessionState.Ready)
            {
                capture = await ExerciseCaptureAsync();
                // Non-intrusive real-OS probe of the legacy launch contract ([21차] 필수 1).
                launcherSupported = await Capture.Snipping.CaptureLauncher.IsSnippingAvailableAsync();
            }

            var document = _viewModel.Session.Current;
            // Transform output dimensions (the document's real size), beside the raw frame size.
            var output = _viewModel.Editor.Document is { } edited
                ? Evaluation(edited).OutputSize
                : default;
            var result = new
            {
                state = _viewModel.Session.State.ToString(),
                width = document?.Frame.Width ?? 0,
                height = document?.Frame.Height ?? 0,
                outputWidth = output.Width,
                outputHeight = output.Height,
                format = document?.Format.ToString(),
                renderer = document?.Renderer,
                sequenceKind = document?.SequenceKind.ToString(),
                frameCount = document?.FrameCount ?? 0,
                currentFrameIndex = document?.CurrentFrameIndex ?? 0,
                wasAnimationFlattened = document?.WasAnimationFlattened ?? false,
                isReducedPreview = document?.IsReducedPreview ?? false,
                diagnostics = document?.Diagnostics,
                error = _viewModel.Session.LastError?.Message,
                firstPaintMs = _firstPaintWatch?.Elapsed.TotalMilliseconds,
                windowExercised = _windowExercised,
                dockExercised = _dockExercised,
                layerTransitionsExercised = _layerTransitionsExercised,
                annotationCount = _viewModel.Editor.State.Annotations.Count,
                layerItemCount = LayerList.Items.Count,
                projectSaved,
                quickResaved,
                captureDupIgnored = capture.DupIgnored,
                captureHashDupIgnored = capture.HashDupIgnored,
                captureNoticeShown = capture.NoticeShown,
                captureNoticeOpened = capture.NoticeOpened,
                captureAutoOpened = capture.AutoOpened,
                captureOfficialOpened = capture.OfficialOpened,
                captureLauncherSupported = launcherSupported,
                captureHotkeyRegistered = AppServices.Capture?.HotkeyRegistered,
                packageIdentity = Capture.Snipping.PackageIdentity.HasIdentity,
                isolatedCodecExercise = _isolatedCodecExercise,
                isModified = _viewModel.Editor.IsModified,
                timestampUtc = DateTimeOffset.UtcNow,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_resultPath)!);
            File.WriteAllText(_resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(_resultPath!, JsonSerializer.Serialize(new { state = "SmokeError", error = ex.ToString() }));
            }
            catch
            {
                // Nothing left to report to; the non-zero information is the missing/error JSON.
            }
        }
        _canvasResizeSettleTimer?.Stop();
        Canvas.EnableRenderLoop = false;
        DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
    }

    /// <summary>Resize twice, fullscreen round-trip, then repaint — GL surface smoke (ADR-0007 follow-up).</summary>
    private async Task ExerciseWindowAsync()
    {
        ExerciseAnnotations();
        Canvas.Invalidate();
        await Task.Delay(150);
        var originalDock = _toolRailDock;
        _toolRailDock = originalDock == ToolRailDock.Vertical
            ? ToolRailDock.Horizontal : ToolRailDock.Vertical;
        ApplyToolRailDock();
        _dockExercised = ToolRailItems.Orientation
            == (_toolRailDock == ToolRailDock.Horizontal ? Orientation.Horizontal : Orientation.Vertical);
        await Task.Delay(150);
        _toolRailDock = originalDock;
        ApplyToolRailDock();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
        await Task.Delay(200);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 900));
        await Task.Delay(200);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        await Task.Delay(300);
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        await Task.Delay(200);
        Canvas.Invalidate();
        await Task.Delay(350);
    }

    private void ExerciseAnnotations()
    {
        if (_viewModel.Editor.Document is not { NativeSize: { IsEmpty: false } native })
            return;
        var emptyCollapsed = LayerPanel.Visibility == Visibility.Collapsed
            && LayerList.Items.Count == 1;
        var unit = MathF.Max(2f, MathF.Min(native.Width, native.Height) / 100f);
        Annotation[] annotations =
        [
            new InkAnnotation
            {
                Id = Guid.NewGuid(),
                Points = [new(2 * unit, 2 * unit), new(8 * unit, 5 * unit), new(14 * unit, 2 * unit)],
                StrokeArgb = 0xFFE8_3B2E,
                StrokeWidth = unit / 2f,
            },
            new LineAnnotation
            {
                Id = Guid.NewGuid(),
                Start = new(2 * unit, 10 * unit),
                End = new(18 * unit, 10 * unit),
                EndArrowhead = ArrowheadKind.Triangle,
                StrokeArgb = 0xFF15_65C0,
                StrokeWidth = unit / 3f,
            },
            new RectangleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(22 * unit, 2 * unit, 14 * unit, 10 * unit),
                Shape = ShapeKind.RoundedRectangle,
                StrokeArgb = 0xFF2E_7D32,
                FillArgb = 0x402E_7D32,
                StrokeWidth = unit / 3f,
            },
            new TextAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(2 * unit, 15 * unit, 28 * unit, 8 * unit),
                Text = "ezy 한글 العربية",
                FontSize = MathF.Max(8f, 3 * unit),
                ForegroundArgb = 0xFFFF_FFFF,
            },
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(34 * unit, 15 * unit, 6 * unit, 6 * unit),
                Number = 1,
                FontSize = MathF.Max(8f, 2.5f * unit),
            },
        ];
        foreach (var annotation in annotations)
            _viewModel.Editor.Apply(new AddAnnotationCommand(annotation, _activeLayerId));
        _selectedAnnotation = annotations[0].Id;
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        var populatedVisible = LayerPanel.Visibility == Visibility.Visible
            && LayerList.Items.Count == 1
            && LayerList.SelectedItem is ListViewItem { Tag: Guid selectedId }
            && selectedId == _activeLayerId;
        // Layer container exercise (UR-007): add a second layer, move an object into it.
        var addedLayer = new AnnotationLayer
        {
            Id = Guid.NewGuid(),
            Name = $"{AppStrings.LayerDefaultName} 2",
        };
        _viewModel.Editor.Apply(new AddLayerCommand(addedLayer));
        _activeLayerId = addedLayer.Id;
        _viewModel.Editor.Apply(new MoveAnnotationToLayerCommand(
            _viewModel.Editor.State, annotations[0].Id, addedLayer.Id));
        UpdateLayerPanel();
        var layered = LayerList.Items.Count == 2
            && _viewModel.Editor.State.FindLayerOf(annotations[0].Id)?.Id == addedLayer.Id;
        for (var index = 0; index < annotations.Length + 2; index++)
            _viewModel.Editor.Undo();
        var collapsedAfterUndo = LayerPanel.Visibility == Visibility.Collapsed
            && LayerList.Items.Count == 1;
        for (var index = 0; index < annotations.Length + 2; index++)
            _viewModel.Editor.Redo();
        _selectedAnnotation = annotations[0].Id;
        UpdateLayerPanel();
        var restoredSelection = LayerPanel.Visibility == Visibility.Visible
            && LayerList.Items.Count == 2
            && _viewModel.Editor.State.FindLayerOf(annotations[0].Id)?.Id == addedLayer.Id;
        _layerTransitionsExercised = emptyCollapsed && populatedVisible && layered
            && collapsedAfterUndo && restoredSelection;
    }
}
