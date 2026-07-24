using System.Diagnostics;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
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
        long Revision,
        bool SpeechBubble = false);

    // 고정 점선을 재사용. 매 프레임 만들면 네이티브 효과가 finalizer 줄에 차곡차곡 쌓임.
    private static readonly SKPathEffect RubberBandDash = SKPathEffect.CreateDash([6f, 4f], 0f);

    private readonly ViewerViewModel _viewModel;
    private readonly DocumentLoader _documentLoader;
    private readonly ViewTransform _transform = new();
    private readonly WinRtClipboardBackend _clipboard = new();
    private readonly RasterAssetImageCache _assetCache = new();

    /// <summary>빠른 저장 대상. 이미지 형식이 null이면 .ezyimg. 문서 교체 때 초기화.</summary>
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
    /// <summary>창 종료 시 작업자 쪽 저장·복사도 함께 취소.</summary>
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
    /// <summary>복원 상태 창은 첫 이미지에 한 번만 크기 맞춤.</summary>
    private bool _initialSizePending = true;
    /// <summary>늦게 끝난 비동기 작업이 닫힌 창 XAML을 건드리지 못하게 함.</summary>
    private bool _windowClosed;
    /// <summary>파일 크기 계산 전까지 화면 밖에서 기다리는 창.</summary>
    private bool _presentationDeferred;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presentationDeadline;
    // 모든 도구에서 Space+드래그와 오른쪽 드래그는 팬 동작.
    private bool _spaceHeld;
    private bool _rightPanActive;

    // ---- 편집 도구 --------------------------------------------------------------------------
    // 작성 초안은 문서 좌표에 둬 줌·팬에도 이미지와 붙어 있게 함.
    // 팬 델타를 직접 배율 보정하는 마지막 포인터만 DIP 사용.
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
    // 마스크 전용 색. 기본 검정이며 선 팔레트와 별개.
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
    private readonly ObservableCollection<string> _fontFamilies = [];
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

    // 자르기 초안은 출력 캔버스 픽셀에 둬 드래그 중에도 이미지에 붙어 있음.
    private static readonly float?[] CropRatios = [null, 1f, 4f / 3f, 16f / 9f];
    private int _cropRatioIndex;
    private readonly CropInteraction _cropInteraction = new();
    // 영역 선택은 자르기 드래그·검토 상태만 빌리고 자르기 확정에는 들어가지 않음.
    private readonly CropInteraction _regionInteraction = new();
    private bool _regionSelectMode;
    private bool _openGroupEnabled = true;
    private bool _selectGroupEnabled = true;
    private bool _transformGroupEnabled = true;
    private bool _cropGroupEnabled = true;
    private bool _zoomGroupEnabled = true;
    private bool _protectGroupEnabled = true;

    // 모든 제스처는 시작한 문서·편집기에 귀속. 문서 교체를 걸치면 후임 대신 제스처 종료.
    private long _gestureCounter;
    private long _gestureId;
    private Guid _gestureDocumentId;
    private long _gestureRevision;

    // 파생 변환 캐시. 상태 변환의 참조가 바뀌면 무효화.
    private TransformEvaluation? _evaluation;
    private BackgroundTransform? _evaluationTransform;
    private PixelSize _evaluationNativeSize;

    /// <summary>편집·배경 저장을 안전하게 끝낸 뒤 재닫기를 허용하는 표식.</summary>
    private bool _closeApproved;
    private bool _closePromptOpen;
    /// <summary>WinUI가 허용하는 단 하나의 대화상자.</summary>
    private ContentDialog? _activeDialog;
    private bool _activeDialogEditScoped;

    // 무인 실행 진입점(--smoke-open / --bench-open24mp).
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

    // 작성 대상. 레이어 선택은 실행 취소 대상이 아니라 창 상태.
    private Guid _activeLayerId = AnnotationLayer.InitialLayerId;
    private bool _layerPanelCollapsed;
    private Guid _renamingLayerId;

    internal Guid RecoveryWindowId { get; } = Guid.NewGuid();

    public ViewerWindow()
    {
        StartupTimeline.Mark("windowCtor");
        _documentLoader = AppServices.Loader;
        InitializeComponent();
        StartupTimeline.Mark("windowXaml");
        PreviousPageButton.Content = new IconSourceElement { IconSource = IconSourceFor("Icon.View.Previous") };
        NextPageButton.Content = new IconSourceElement { IconSource = IconSourceFor("Icon.View.Next") };
        AnimationPlaybackIcon.IconSource = IconSourceFor("Icon.View.Pause");
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        ConfigureToolRailOverflowHints();
        // ScrollViewer가 먼저 처리한 휠도 여기까지 받음.
        ContextBarScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnContextBarPointerWheel),
            handledEventsToo: true);
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
        // .ico 로드는 첫 프레임에 급하지 않음. 그동안 작업 표시줄은 EXE 내장 아이콘 사용.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_windowClosed)
                AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ezyImageViewer.ico"));
            StartupTimeline.Mark("windowIcon");
        });
        _viewModel = new ViewerViewModel(ConfirmDiscardAsync, _documentLoader);
        StartupTimeline.Mark("viewModel");
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LoadStarted += OnDocumentLoadStarted;
        ApplyToolDefaults(AppServices.RuntimeToolDefaults);
        ZoomSlider.ValueChanged += OnZoomSliderChanged;
        PopulateColorPalette();
        ColorFlyout.Opened += (_, _) => _colorFlyoutOpen = true;
        ColorFlyout.Closed += (_, _) => _colorFlyoutOpen = false;
        PopulateStyleOptions();
        StartupTimeline.Mark("palettes");
        _toolRailDock = AppServices.Settings.ToolRailDock;
        ApplyToolRailDock();
        ApplyTooltips();
        ApplySettings(AppServices.RuntimeSettings);
        StartupTimeline.Mark("applySettings");
        AppServices.RecoveryAvailabilityChanged += OnRecoveryAvailabilityChanged;
        ApplyRecoveryAvailability(AppServices.RecoveryAvailability);
        ApplyDataProtectionStatus();
        LayerPanelTitle.Text = AppStrings.LayerPanel;
        ApplyLayerPanelCollapse();
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        RegisterAccelerators();
        Root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Space) _spaceHeld = true; };
        Root.KeyUp += (_, e) => { if (e.Key == VirtualKey.Space) _spaceHeld = false; };
        // 다른 창에서 Space를 떼면 KeyUp이 안 오므로 포커스 이탈 때 팬 고착 해제.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                _spaceHeld = false;
        };

        // 다른 앱이 단축키를 선점했으면 조용히 실패하지 말고 알림.
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
            _windowClosed = true;
            var recoveryCompletion = _recoveryOpenCompletion;
            _recoveryOpenCompletion = null;
            _recoveryRestoreInProgress = false;
            recoveryCompletion?.TrySetResult(false);
            AppWindow.Changed -= OnAppWindowChanged;
            DetachToolRailOverflowHints();
            DetachXamlRoot();
            _canvasResizeGeneration++;
            _canvasResizeSettleTimer?.Stop();
            // 보이기도 전에 닫힌 창을 마감 타이머가 되살리면 공포물.
            StopPresentationDeadline();
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
        StartupTimeline.Mark("windowCtorDone");
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
        RecentMenuItem.IsEnabled = settings.RecentFilesEnabled;
        CaptureButton.IsEnabled = !AppServices.IsSafeMode;
        CaptureMenuItem.IsEnabled = !AppServices.IsSafeMode;
        _openGroupEnabled = settings.ToolbarOpenGroupEnabled;
        _selectGroupEnabled = settings.ToolbarSelectGroupEnabled;
        _transformGroupEnabled = settings.ToolbarTransformGroupEnabled;
        _cropGroupEnabled = settings.ToolbarCropGroupEnabled;
        _zoomGroupEnabled = settings.ToolbarZoomGroupEnabled;
        _protectGroupEnabled = settings.ToolbarProtectGroupEnabled;
        ApplyToolbarGrouping();
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
        SetTip(WhiteboardButton, AppStrings.ToolWhiteboard, AppStrings.TipWhiteboard);
        WhiteboardWhiteItem.Text = AppStrings.WhiteboardWhite;
        WhiteboardBlackItem.Text = AppStrings.WhiteboardBlack;
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
        SetTip(SettingsButton, AppStrings.ToolSettings, AppStrings.TipSettings);
        // 툴팁은 필름 스트립 상태에 맞추고 자동화 이름은 실시간 n/n을 가진 상태바가 담당.
        UpdateFilmstripToggleState();
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
        SetTip(SelectButton,
            _regionSelectMode ? AppStrings.SelectModeRegion : AppStrings.ToolSelect,
            _regionSelectMode ? AppStrings.TipRegionSelect : AppStrings.TipSelect);
        SetTip(SelectModeButton, AppStrings.ToolSelectMode, AppStrings.TipSelectMode);
        SetTip(RegionSelectButton, AppStrings.SelectModeRegion, AppStrings.TipRegionSelect);
        SelectModeObjectItem.Text = AppStrings.ToolSelect;
        SelectModeRegionItem.Text = AppStrings.SelectModeRegion;
        SetTip(OpenGroupButton, AppStrings.ToolOpenGroup, AppStrings.TipOpenGroup);
        SetTip(TransformGroupButton, AppStrings.ToolTransformGroup, AppStrings.TipTransformGroup);
        SetTip(CropGroupButton, AppStrings.ToolCropGroup, AppStrings.TipCropGroup);
        SetTip(ZoomGroupButton, AppStrings.ToolZoomGroup, AppStrings.TipZoomGroup);
        SetTip(ProtectGroupButton, AppStrings.ToolProtectGroup, AppStrings.TipProtectGroup);
        ConfigureGroupedMenus();
        SetTip(PenButton, AppStrings.ToolPen, AppStrings.TipPen);
        SetTip(HighlighterButton, AppStrings.ToolHighlighter, AppStrings.TipHighlighter);
        SetTip(LineButton, AppStrings.ToolLine, AppStrings.TipLine);
        SetTip(ArrowButton, AppStrings.ToolArrow, AppStrings.TipArrow);
        SetTip(RectangleButton, AppStrings.ToolRectangle, AppStrings.TipRectangle);
        SetTip(RoundedRectangleButton, AppStrings.ToolRoundedRectangle, AppStrings.TipRoundedRectangle);
        SetTip(EllipseButton, AppStrings.ToolEllipse, AppStrings.TipEllipse);
        SetTip(TextButton, AppStrings.ToolText, AppStrings.TipText);
        SetTip(NumberButton, AppStrings.ToolNumber, AppStrings.TipNumber);
        SetTip(SpeechBubbleButton, AppStrings.ToolSpeechBubble, AppStrings.TipSpeechBubble);
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
        StrokeWidthLabel.Text = AppStrings.StyleStrokeWidth;
        OpacityLabel.Text = AppStrings.StyleOpacity;
        FontSizeLabel.Text = AppStrings.StyleFontSize;
        CornerRadiusLabel.Text = AppStrings.StyleCornerRadius;
        ArrowheadLabel.Text = AppStrings.StyleArrowhead;
        FontFamilyLabel.Text = AppStrings.StyleFontFamily;
        AlignmentLabel.Text = AppStrings.StyleAlignment;
        RotationLabel.Text = AppStrings.StyleRotation;
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
        CropRatioMenuItem.Text = $"{AppStrings.MenuCropRatio}: {CropRatioText()}";
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
        // 설치 글꼴만 고르게 해 렌더 불가 이름 차단.
        // 열기 전엔 안 보이는 목록이라 작업자에서 늦게 불러오고 저장된 글꼴은 맨 위에 유지.
        FontFamilyBox.ItemsSource = _fontFamilies;
        LoadFontFamiliesAsync();

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

    private async void LoadFontFamiliesAsync()
    {
        string[] families;
        try
        {
            families = await Task.Run(static () => SKFontManager.Default.FontFamilies
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCulture)
                .ToArray());
        }
        catch (Exception)
        {
            // 열거 실패 시 저장된 글꼴 하나만 제공. 문서 이름 렌더는 그대로 가능.
            return;
        }
        if (_windowClosed)
            return;
        foreach (var family in families)
        {
            if (!_fontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase))
                _fontFamilies.Add(family);
        }
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

        // 마스크 문맥에서는 마스크 색만 변경. 선 색과 다른 선택은 서로 영역 침범 금지.
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
                    SpeechBubbleAnnotation bubble => bubble with { StrokeArgb = color.Argb },
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
        // 팔레트는 지금 바꿀 색을 표시. 마스크 문맥이면 마스크, 아니면 선.
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
        // 정적 아이콘 원본은 두 시각 소유자에 못 붙이므로 창별 원본으로 복제.
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
        // 텍스트 입력에 포커스가 있으면 양보. 이미지 복사가 사용자 글 복사를 덮으면 안 됨.
        AddConditional(VirtualKey.C, VirtualKeyModifiers.Control, () =>
        {
            if (IsTextInputFocused())
                return false;
            _ = CopyToClipboardAsync();
            return true;
        });
        // 잘라내기는 검토된 영역 선택에서만 사용. 그 외에는 키 양보.
        AddConditional(VirtualKey.X, VirtualKeyModifiers.Control, () =>
        {
            if (IsTextInputFocused()
                || _tool != CanvasTool.RegionSelect
                || _regionInteraction.Phase != CropInteractionPhase.Reviewing)
                return false;
            _ = CutRegionToClipboardAsync();
            return true;
        });
        Add(VirtualKey.F11, default, (_, _) => ToggleFullScreen());
        Add(VirtualKey.Escape, default, (_, _) => OnEscape());
        // Windows·타 플랫폼 관례를 모두 받아 Ctrl+Y와 Ctrl+Shift+Z는 다시 실행.
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

    // ---- 세션 → 렌더 스냅샷(UI 스레드 전용) -----------------------------------------------

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
        // 상태바와 제목이 수정 상태를 읽으니 편집기부터 재결합.
        _viewModel.SyncEditor();
        RecordSessionOutcome();
        // 교체 문서는 새 저장 대상, 프로젝트는 자기 저장 대상 사용.
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
                // 편집기 재결합이 기본 최상단 레이어를 그렸어도 복원된 작성 대상이 최종 승자.
                _activeLayerId = projectLayer;
                UpdateLayerPanel();
                UpdateToolUi();
            }
        }
        // 문서 교체 시작·완료는 걸쳐 있던 제스처와 대화상자를 종료. 후임 문서는 무죄.
        CancelActiveGesture();
        CancelEditDialog();
        RebuildSnapshot(_viewModel.Session.Current);
        PresentDeferredWindow();
        MaybeApplyInitialWindowSize();
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

    /// <summary>편집은 세션을 건드리지 않으므로 편집기 이벤트로 다시 그림.</summary>
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
        // 편집으로 출력 크기가 바뀌어도 보기 회전·모드는 유지.
        // 스냅샷이 늦으면 재생성 경로가 크기를 잡으니 여기서는 건너뜀.
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
        // 작성 레이어는 항상 존재해야 함. 숨김·잠금이면 캔버스 선택도 해제해 우회 편집 차단.
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
        SelectButton.IsChecked = _selectGroupEnabled
            ? _tool is CanvasTool.Select or CanvasTool.RegionSelect
            : _tool == CanvasTool.Select;
        RegionSelectButton.IsChecked = _tool == CanvasTool.RegionSelect;
        PenButton.IsChecked = _tool == CanvasTool.Pen;
        HighlighterButton.IsChecked = _tool == CanvasTool.Highlighter;
        LineButton.IsChecked = _tool == CanvasTool.Line;
        ArrowButton.IsChecked = _tool == CanvasTool.Arrow;
        RectangleButton.IsChecked = _tool == CanvasTool.Rectangle;
        RoundedRectangleButton.IsChecked = _tool == CanvasTool.RoundedRectangle;
        EllipseButton.IsChecked = _tool == CanvasTool.Ellipse;
        TextButton.IsChecked = _tool == CanvasTool.Text;
        NumberButton.IsChecked = _tool == CanvasTool.Number;
        SpeechBubbleButton.IsChecked = _tool == CanvasTool.SpeechBubble;
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

    /// <summary>변환·원본이 바뀔 때만 다시 계산하는 파이프라인 캐시.</summary>
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
    /// 배경 렌더 스냅샷의 단일 소유자. 픽셀 복사 후 UI 스레드에서 교체·해제.
    /// 늦은 프레임은 건너뛰고 주석 편집은 재업로드 없이 다시 그리기만 수행.
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
            // 콘텐츠 좌표는 변환 출력 캔버스. 맞춤·실제 크기와 편집 좌표는 축소 디코드와 무관.
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
            // 복사 중 교체됨. 다음 변경 이벤트가 현재 문서를 가져옴.
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
            : _tool == CanvasTool.RegionSelect
                && _regionInteraction.Phase == CropInteractionPhase.Reviewing
            ? AppStrings.RegionReviewHint
            : string.IsNullOrEmpty(_viewModel.DiagnosticsText)
                ? _viewModel.StateText
                : $"{_viewModel.StateText} · {_viewModel.DiagnosticsText}");
        StatusZoom.Text = $"{_transform.Scale * 100:0}%";
        PreviousButton.IsEnabled = _viewModel.CanOpenPrevious;
        NextButton.IsEnabled = _viewModel.CanOpenNext;
        StatusPageGroup.Visibility = _viewModel.HasMultipleFrames
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 폴더에 바꿔 볼 다른 파일이 있을 때만 필름 스트립 노출.
        StatusPositionButton.IsEnabled = _viewModel.CanBrowseFiles;
        if (_viewModel.CanBrowseFiles)
            SyncFilmstripSelection();
        else
            CloseFilmstrip();
        PreviousPageButton.IsEnabled = _viewModel.CanOpenPreviousPage;
        NextPageButton.IsEnabled = _viewModel.CanOpenNextPage;
        StatusProgress.IsActive = _viewModel.IsBusy;
        _updatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(_transform.Scale * 100f, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingZoomSlider = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StatusPositionButton,
            _viewModel.CanBrowseFiles
                ? $"{StatusPosition.Text} — {AppStrings.FilmstripLabel}"
                : StatusPosition.Text);
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

    // ---- 그리기 ------------------------------------------------------------------------------

    private void OnPaintSurface(object sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var viewport = canvas.DeviceClipBounds;
        DrawBackground(canvas, viewport.Width, viewport.Height);

        if (_snapshot is null)
            return;
        // 합성은 이 문서의 편집기 변환이 필요. 세션과 UI 콜백이 엇갈리면 다음 이벤트에 맡김.
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
        DrawRegionOverlay(canvas, viewMatrix);

        if (_firstPaintWatch is { IsRunning: true } && _viewModel.Session.State == SessionState.Ready)
        {
            _firstPaintWatch.Stop();
            DispatcherQueue.TryEnqueue(MaybeWriteUnattendedResult);
        }
    }

    /// <summary>기록에 넣지 않고 원본 좌표 작성 초안 그리기.</summary>
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
            CanvasTool.SpeechBubble when _drawAnchor is { } bubbleStart && _drawCurrent is { } bubbleEnd =>
                MakeBubbleDraft(RectF.FromCorners(bubbleStart.X, bubbleStart.Y, bubbleEnd.X, bubbleEnd.Y)),
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
            // 보호 초안도 실제 효과를 보여 주도록 스냅샷 동행.
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

    /// <summary>자르기 초안은 어둡게 가리지 않는 점선 상자. 검토 경계를 그대로 확정.</summary>
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

        CropOverlayRendering.Draw(canvas, viewMatrix, rect);
    }

    private void DrawRegionOverlay(SKCanvas canvas, SKMatrix viewMatrix)
    {
        if (_viewModel.Editor.Document is not { } document)
            return;
        var canvasSize = Evaluation(document).OutputSize;
        if (_regionInteraction.GetPreview(null, canvasSize.Width, canvasSize.Height) is not { } draft)
            return;
        var rect = SKRect.Create(draft.X, draft.Y, draft.Width, draft.Height);
        if (rect.Width < 1f || rect.Height < 1f)
            return;

        CropOverlayRendering.Draw(canvas, viewMatrix, rect);
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

    // ---- 입력 --------------------------------------------------------------------------------

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 캔버스 배치 크기보다 먼저 도착한 문서의 재시도 경로이기도 함.
        MaybeApplyInitialWindowSize();
        QueueCanvasResize();
    }

    private void OnCanvasLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_observedXamlRoot, Canvas.XamlRoot))
        {
            DetachXamlRoot();
            _observedXamlRoot = Canvas.XamlRoot;
            if (_observedXamlRoot is not null)
                _observedXamlRoot.Changed += OnXamlRootChanged;
        }
        MaybeApplyInitialWindowSize();
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

    // ---- 초기 창 크기 ------------------------------------------------------------------------

    /// <summary>이미지와 캔버스 가장자리 사이 한쪽 여백.</summary>
    private const double CanvasMarginDip = 24d;
    /// <summary>도구 막대 측정 전 사용하는 두께와 여백 기본값.</summary>
    private const double ToolRailReserveDip = 56d;
    private const double MinimumWindowWidthDip = 800d;
    private const double MinimumWindowHeightDip = 600d;
    /// <summary>XAML 상태바 행 높이. 클라이언트 영역에서 캔버스를 뺀 값.</summary>
    private const double StatusBarHeightDip = 44d;

    /// <summary>사용자가 보는 창은 정해진 위치·크기로 딱 한 번 표시.</summary>
    internal bool IsPresentationDeferred => _presentationDeferred;

    /// <summary>첫 이미지 크기 계산까지 화면 밖에서 대기. 마감까지 실패하면 현재 크기로 표시.</summary>
    internal void DeferFirstPresentation(TimeSpan deadline)
    {
        if (!_initialSizePending)
        {
            Activate();
            return;
        }
        _presentationDeferred = true;
        _presentationDeadline = DispatcherQueue.CreateTimer();
        _presentationDeadline.Interval = deadline;
        _presentationDeadline.IsRepeating = false;
        _presentationDeadline.Tick += (_, _) => PresentNow();
        _presentationDeadline.Start();
    }

    /// <summary>현재 위치·크기로 즉시 표시. 한 번 보인 뒤에는 자동 크기 변경 금지.</summary>
    internal void PresentNow()
    {
        if (_presentationDeferred)
        {
            _presentationDeferred = false;
            _initialSizePending = false;
        }
        StopPresentationDeadline();
        Activate();
    }

    private void StopPresentationDeadline()
    {
        _presentationDeadline?.Stop();
        _presentationDeadline = null;
    }

    /// <summary>아직 표시 대기 중인 창의 세션 결과 처리.</summary>
    private void PresentDeferredWindow()
    {
        if (!_presentationDeferred)
            return;
        switch (_viewModel.Session.State)
        {
            case SessionState.Ready when TryPresentSizedForFirstDocument():
                return;
            case SessionState.Ready:
            case SessionState.Failed:
                // 측정값도 이미지도 없으면 현재 모습으로 한 번 표시.
                PresentNow();
                return;
        }
    }

    /// <summary>
    /// 첫 프레임 전에 이미지에 맞춰 창 크기 결정.
    /// 배치 전 DPI는 창 핸들에서 읽고 실제 외곽 크기로 비클라이언트 영역 측정.
    /// </summary>
    private bool TryPresentSizedForFirstDocument()
    {
        if (_viewModel.Session.Current is not { NativeSize: { IsEmpty: false } native })
            return false;
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            return false;
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is not { } display)
            return false;
        // 잘못된 핸들이면 문서화된 실패값 반환. DPI 추측은 창 크기를 틀리게 함.
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (dpi == 0)
            return false;

        var rasterScale = dpi / 96d;
        var statusBar = (int)Math.Round(StatusBarHeightDip * rasterScale);
        // 배치 전 도구 막대 실제 너비가 없으므로 설계 너비 사용.
        var railReserve = (int)Math.Round(ToolRailReserveDip * rasterScale);
        var canvasMargin = (int)Math.Round(CanvasMarginDip * rasterScale);
        var margin = new PixelSize(
            canvasMargin + (_toolRailDock == ToolRailDock.Vertical ? railReserve : 0),
            canvasMargin + (_toolRailDock == ToolRailDock.Horizontal ? railReserve : 0));
        var workArea = new PixelSize(display.WorkArea.Width, display.WorkArea.Height);
        var minimumWindow = new PixelSize(
            (int)Math.Round(MinimumWindowWidthDip * rasterScale),
            (int)Math.Round(MinimumWindowHeightDip * rasterScale));

        var client = InitialWindowGeometry.Measure(
            native, new PixelSize(0, statusBar), margin, workArea, minimumWindow);
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            client.WindowSize.Width, client.WindowSize.Height));
        var frame = new PixelSize(
            Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width),
            Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height));

        // 외곽 프레임을 알았으니 창 좌표에서 다시 측정. 작업 영역 상한은 실제 창 기준.
        var layout = InitialWindowGeometry.Measure(
            native,
            new PixelSize(frame.Width, frame.Height + statusBar),
            margin,
            workArea,
            minimumWindow);
        var (x, y) = InitialWindowGeometry.Center(
            layout.WindowSize, workArea, display.WorkArea.X, display.WorkArea.Y);

        _transform.SetViewport(
            (float)Math.Max(1, layout.WindowSize.Width - frame.Width),
            (float)Math.Max(1, layout.WindowSize.Height - frame.Height - statusBar));
        _transform.OpenAtScale(layout.ContentScale);
        _fitPending = false;
        _initialSizePending = false;
        _presentationDeferred = false;
        StopPresentationDeadline();
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            x, y, layout.WindowSize.Width, layout.WindowSize.Height));
        Activate();
        UpdateStatusBar();
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    /// <summary>
    /// 첫 이미지를 최대 100%로 여백과 함께 담고 모니터 작업 영역 중앙에 배치.
    /// 창당 한 번만 실행하며 최대화·전체 화면과 사용자가 정한 후속 크기는 존중.
    /// </summary>
    private void MaybeApplyInitialWindowSize()
    {
        // 표시 대기 창의 크기는 지연 경로가 단독 소유. 여기서 재면 배치 전 캔버스와 경합.
        if (!_initialSizePending || _presentationDeferred)
            return;
        // 세션 Ready보다 UI 콜백이 늦으므로 스냅샷이 이 문서를 담을 때까지 기다려 순서 보장.
        if (_viewModel.Session.State != SessionState.Ready
            || _viewModel.Session.Current is not { NativeSize: { IsEmpty: false } native } document
            || document.Id != _snapshotDocumentId)
            return;
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            // 최대화·전체 화면은 사용자 선택. 손대지 않음.
            _initialSizePending = false;
            return;
        }
        var rasterScale = Canvas.XamlRoot?.RasterizationScale ?? 0d;
        if (rasterScale <= 0d || Canvas.ActualWidth <= 0d || Canvas.ActualHeight <= 0d)
            return; // 배치 전이면 캔버스 크기 변경·로드 경로가 재시도.
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is not { } display)
            return;
        _initialSizePending = false;

        var canvasWidth = Canvas.ActualWidth * rasterScale;
        var canvasHeight = Canvas.ActualHeight * rasterScale;
        // 제목 표시줄·테두리·상태바는 추측 말고 실측.
        var chrome = new PixelSize(
            Math.Max(0, AppWindow.Size.Width - (int)Math.Round(canvasWidth)),
            Math.Max(0, AppWindow.Size.Height - (int)Math.Round(canvasHeight)));
        var railReserve = ToolRailReserve();
        // 이미지가 중앙에 오므로 막대 여백은 양쪽 계산. 그래야 이미지가 막대 밑에 안 숨음.
        var margin = new PixelSize(
            (int)Math.Round((CanvasMarginDip
                + (_toolRailDock == ToolRailDock.Vertical ? railReserve : 0d)) * rasterScale),
            (int)Math.Round((CanvasMarginDip
                + (_toolRailDock == ToolRailDock.Horizontal ? railReserve : 0d)) * rasterScale));
        var workArea = new PixelSize(display.WorkArea.Width, display.WorkArea.Height);
        var layout = InitialWindowGeometry.Measure(
            native,
            chrome,
            margin,
            workArea,
            new PixelSize(
                (int)Math.Round(MinimumWindowWidthDip * rasterScale),
                (int)Math.Round(MinimumWindowHeightDip * rasterScale)));
        var (x, y) = InitialWindowGeometry.Center(
            layout.WindowSize, workArea, display.WorkArea.X, display.WorkArea.Y);

        // 크기 변경 전에 배율 적용. 이후 뷰포트 변경은 중앙 정렬만 해 여백을 덮지 않음.
        _transform.SetViewport(
            (float)Math.Max(1, layout.WindowSize.Width - chrome.Width),
            (float)Math.Max(1, layout.WindowSize.Height - chrome.Height));
        _transform.OpenAtScale(layout.ContentScale);
        _fitPending = false;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            x, y, layout.WindowSize.Width, layout.WindowSize.Height));
        UpdateStatusBar();
    }

    /// <summary>도킹 축의 도구 막대 두께와 바깥 여백 합계(DIP).</summary>
    private double ToolRailReserve()
    {
        var thickness = _toolRailDock == ToolRailDock.Horizontal
            ? ToolRail.ActualHeight
            : ToolRail.ActualWidth;
        var gap = _toolRailDock == ToolRailDock.Horizontal
            ? ToolRail.Margin.Top
            : ToolRail.Margin.Left;
        return thickness > 0d ? thickness + gap : ToolRailReserveDip;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(Canvas);
        // 오른쪽 버튼·펜 배럴 드래그는 Space+드래그처럼 팬. 캔버스 메뉴가 없어 키 충돌 없음.
        var rightPan = !currentPoint.Properties.IsLeftButtonPressed
            && currentPoint.Properties.IsRightButtonPressed;
        if ((!currentPoint.Properties.IsLeftButtonPressed && !rightPan) || _activePointerId is not null)
            return;
        if (!Canvas.CapturePointer(e.Pointer))
            return;

        _activePointerId = e.Pointer.PointerId;
        var point = currentPoint.Position;
        _lastPointer = new SKPoint((float)point.X, (float)point.Y);
        if (rightPan)
        {
            _rightPanActive = true;
            return;
        }

        // Space 팬은 도구 사용 중에도 최우선. 문서 교체 중 편집도 묻지 않고 사라지므로 차단.
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

        if (_tool == CanvasTool.RegionSelect)
        {
            var output = ToOutput(device);
            if (_regionInteraction.TryGetValidReview(
                    document.Id, _viewModel.Editor.Revision, out var regionReview)
                && regionReview.Contains(output.X, output.Y))
            {
                LiftRegionAndBeginDrag(document, regionReview, device);
                return;
            }
            var regionCanvas = Evaluation(document).OutputSize;
            if (output.X < 0f || output.Y < 0f
                || output.X > regionCanvas.Width || output.Y > regionCanvas.Height)
                return;
            BeginGesture(document);
            _regionInteraction.BeginDrag(output.X, output.Y, document.Id, _viewModel.Editor.Revision);
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }

        if (_tool != CanvasTool.Select)
        {
            // 막힌 레이어는 누를 때 알림. 확정 순간 사라지는 유령 초안 금지.
            if (!CanEditActiveLayer())
                return;
            // 작성 시작점은 보이는 내용 위만 허용. 시작 뒤 선이 밖으로 나가는 건 허용.
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
            // 보호 영역은 회전하지 않으므로 회전 손잡이 비활성.
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

    /// <summary>제스처가 시작한 정확한 문서·편집기만 수정하도록 확인.</summary>
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
        // 모든 경로에서 최신 위치 기록. 그리다 팬으로 바뀌어도 첫 프레임이 튀지 않게 함.
        _lastPointer = current;

        // Space·오른쪽 드래그는 초안 중에도 항상 팬. 초안은 문서 좌표라 팬 뒤 그대로 재개.
        if (!_spaceHeld && !_rightPanActive)
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

            if (_regionInteraction.Phase == CropInteractionPhase.Dragging)
            {
                var output = ToOutput(DevicePoint(e));
                _regionInteraction.UpdateDrag(output.X, output.Y);
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

    /// <summary>드래그 하나는 기록 하나. 첫 이동만 쌓고 나머지는 갱신.</summary>
    private void DragSelection(PointerRoutedEventArgs e)
    {
        // 누를 때뿐 아니라 실제 변경 때도 확인. 드래그 중 교체된 후임 문서는 건드리지 않음.
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
        var next = _activeSelectionHandle switch
        {
            SelectionHandle.Rotate => SelectionGeometry.Rotate(origin, point),
            SelectionHandle.Tail => SelectionGeometry.MoveTail(origin, point),
            _ => SelectionGeometry.Resize(origin, _activeSelectionHandle, point),
        };
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
        CompleteRegionDrag();
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
        _rightPanActive = false;
        _lastPointer = null;
        _activePointerId = null;
        Canvas.ReleasePointerCapture(e.Pointer);
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        if (pendingText is { } text)
            _ = ShowTextDialogAndCommitAsync(text);
    }

    /// <summary>포인터 캡처를 잃으면 제스처 폐기. 늦은 해제가 묵은 초안을 확정하지 못하게 함.</summary>
    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
            return;

        _drawAnchor = null;
        _drawCurrent = null;
        _inkPoints.Clear();
        _draftTool = CanvasTool.Select;
        _cropInteraction.CancelDrag();
        _regionInteraction.CancelDrag();
        _dragAnnotation = default;
        _dragMoved = false;
        _activeSelectionHandle = SelectionHandle.None;
        _selectionTransformOrigin = null;
        _selectionTransformMoved = false;
        _selectionBandAnchor = null;
        _selectionBandCurrent = null;
        _rightPanActive = false;
        _activePointerId = null;
        _lastPointer = null;
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    /// <summary>원본 좌표 초안 하나를 기록 항목 하나로 확정.</summary>
    /// <summary>드래그 중 초안 전용. 빈 ID·빈 텍스트라 문서에는 안 들어감.</summary>
    private SpeechBubbleAnnotation MakeBubbleDraft(RectF bounds) => new()
    {
        Id = Guid.Empty,
        Bounds = bounds,
        TailTip = SpeechBubbleGeometry.DefaultTailTip(bounds),
        Text = "",
        StrokeArgb = _drawStrokeColor,
        CornerRadius = _drawCornerRadius,
        Opacity = _drawOpacity,
    };

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
                    {
                        if (TrySelectTextTargetAt(a, bubble: false))
                            return null;
                        bounds = DefaultNativeBounds(a, 240f, 60f);
                    }
                    return new PendingText(
                        bounds, _drawStrokeColor, _drawOpacity, _drawFontSize,
                        _drawFontFamily, _drawFontBold, _drawFontItalic,
                        _drawTextAlignment,
                        _drawTextBackgroundEnabled ? 0xCCFF_FFFF : null,
                        _gestureDocumentId, _gestureRevision);
                case CanvasTool.SpeechBubble:
                    if (bounds.Width < 1f || bounds.Height < 1f)
                    {
                        if (TrySelectTextTargetAt(a, bubble: true))
                            return null;
                        bounds = DefaultNativeBounds(a, 240f, 120f);
                    }
                    return new PendingText(
                        bounds, _drawStrokeColor, _drawOpacity, _drawFontSize,
                        _drawFontFamily, _drawFontBold, _drawFontItalic,
                        _drawTextAlignment, null,
                        _gestureDocumentId, _gestureRevision, SpeechBubble: true);
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

    /// <summary>같은 텍스트 도구의 기존 주석을 짧게 누르면 새 초안 대신 선택.</summary>
    private bool TrySelectTextTargetAt(SKPoint nativePoint, bool bubble)
    {
        var hit = _viewModel.Editor.State.HitTest(nativePoint.X, nativePoint.Y);
        if (bubble ? hit is not SpeechBubbleAnnotation : hit is not TextAnnotation)
            return false;
        SetTool(CanvasTool.Select);
        _selectedAnnotation = hit!.Id;
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        Canvas.Invalidate();
        return true;
    }

    /// <summary>텍스트·말풍선 생성과 편집이 공유하는 단일 대화상자.</summary>
    private async Task<string?> PromptAnnotationTextAsync(string title, string? initial = null)
    {
        if (Content?.XamlRoot is null)
            return null;
        var textBox = new TextBox
        {
            Header = AppStrings.TextContentLabel,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            Height = 160,
            MaxLength = AnnotationValidator.MaxTextLength,
            Text = initial ?? "",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = textBox,
            PrimaryButtonText = AppStrings.DialogApply,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog, editScoped: true) != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(textBox.Text))
            return null;
        // WinUI TextBox 줄바꿈 '\r'을 문서 모델의 '\n'으로 통일.
        return textBox.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private async Task ShowTextDialogAndCommitAsync(PendingText pending)
    {
        var text = await PromptAnnotationTextAsync(
            pending.SpeechBubble ? AppStrings.SpeechBubbleTitle : AppStrings.TextTitle);
        if (text is null)
            return;
        if (_viewModel.IsReplacementPending
            || _viewModel.Editor.Document is not { } document
            || document.Id != pending.DocumentId
            || _viewModel.Editor.Revision != pending.Revision)
            return;

        Annotation annotation = pending.SpeechBubble
            ? new SpeechBubbleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = pending.Bounds,
                TailTip = SpeechBubbleGeometry.DefaultTailTip(pending.Bounds),
                Text = text,
                FontFamily = pending.FontFamily,
                FontSize = pending.FontSize,
                IsBold = pending.IsBold,
                IsItalic = pending.IsItalic,
                Alignment = pending.Alignment,
                StrokeArgb = pending.Color,
                CornerRadius = _cornerRadius,
                Opacity = pending.Opacity,
            }
            : new TextAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = pending.Bounds,
                Text = text,
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
        if (SelectedAnnotation() is { } selected)
            await EditAnnotationTextAsync(selected);
    }

    private async Task EditAnnotationTextAsync(Annotation before)
    {
        if (_viewModel.IsReplacementPending
            || before is not (TextAnnotation or SpeechBubbleAnnotation)
            || before.IsLocked
            || _viewModel.Editor.Document is not { } document
            || Content?.XamlRoot is null)
            return;
        var documentId = document.Id;
        var revision = _viewModel.Editor.Revision;
        var initial = before switch
        {
            TextAnnotation text => text.Text,
            SpeechBubbleAnnotation bubble => bubble.Text,
            _ => "",
        };
        try
        {
            var edited = await PromptAnnotationTextAsync(AppStrings.TextEditTitle, initial);
            if (edited is null
                || _viewModel.IsReplacementPending
                || _viewModel.Editor.Document is not { } target
                || target.Id != documentId
                || _viewModel.Editor.Revision != revision
                || !Equals(_viewModel.Editor.State.Find(before.Id), before))
                return;
            ApplySelectedEdit(AnnotationEditKind.Content, before switch
            {
                TextAnnotation text => text with { Text = edited },
                SpeechBubbleAnnotation bubble => bubble with { Text = edited },
                _ => before,
            });
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

    /// <summary>포인터 해제는 검토 초안만 생성. 명시적 확인 때 기록 변경.</summary>
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

    private void CompleteRegionDrag()
    {
        if (_regionInteraction.Phase != CropInteractionPhase.Dragging)
            return;
        if (!GestureStillValid())
        {
            _regionInteraction.CancelDrag();
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }

        var canvasSize = Evaluation(_viewModel.Editor.Document!).OutputSize;
        _regionInteraction.CompleteDrag(null, canvasSize.Width, canvasSize.Height);
        Canvas.Invalidate();
        UpdateStatusBar();
    }

    /// <summary>
    /// 검토 영역을 래스터 주석으로 띄우고 배경의 같은 영역은 지움.
    /// 한 기록으로 묶은 뒤 현재 누름을 선택 드래그로 이어 재클릭 없이 이동.
    /// </summary>
    private void LiftRegionAndBeginDrag(
        Core.Documents.ImageDocument document, CropReview review, SKPoint device)
    {
        if (!CanEditActiveLayer())
            return;
        if (document.IsReducedPreview || _snapshot is null)
        {
            SetStatusState(AppStrings.RegionNeedsFullRes);
            return;
        }

        var evaluation = Evaluation(document);
        if (!evaluation.TryGetOutputToNative(out var inverse))
            return;
        // 검토 영역의 원본 좌표 경계 상자. 자르기처럼 픽셀 격자 바깥쪽으로 맞춤.
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        foreach (var corner in (ReadOnlySpan<Vector2>)
        [
            new(review.Bounds.X, review.Bounds.Y),
            new(review.Bounds.Right, review.Bounds.Y),
            new(review.Bounds.Right, review.Bounds.Bottom),
            new(review.Bounds.X, review.Bounds.Bottom),
        ])
        {
            var mapped = Vector2.Transform(corner, inverse);
            min = Vector2.Min(min, mapped);
            max = Vector2.Max(max, mapped);
        }
        var x0 = Math.Clamp((int)MathF.Floor(min.X), 0, document.NativeSize.Width);
        var y0 = Math.Clamp((int)MathF.Floor(min.Y), 0, document.NativeSize.Height);
        var x1 = Math.Clamp((int)MathF.Ceiling(max.X), 0, document.NativeSize.Width);
        var y1 = Math.Clamp((int)MathF.Ceiling(max.Y), 0, document.NativeSize.Height);
        if (x1 - x0 < 1 || y1 - y0 < 1)
            return;

        try
        {
            using var lifted = _snapshot.Subset(new SKRectI(x0, y0, x1, y1))
                ?? throw new InvalidOperationException("Region extraction failed.");
            var png = ImageExporter.Encode(lifted, ExportFormat.Png);
            var asset = new RasterAsset
            {
                Id = Guid.NewGuid(),
                EncodedBytes = [.. png],
                PixelSize = new PixelSize(x1 - x0, y1 - y0),
                Format = "Png",
            };
            var retained = checked(
                _viewModel.Editor.State.Assets.Sum(item => item.EstimatedRetainedBytes)
                + asset.EstimatedRetainedBytes);
            if (retained > AnnotationValidator.MaxRasterAssetBytes)
                throw new InvalidDataException(
                    $"Raster assets exceed the {AnnotationValidator.MaxRasterAssetBytes:N0} byte document limit.");
            var annotation = new ImageAnnotation
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Bounds = new RectF(x0, y0, x1 - x0, y1 - y0),
            };

            // 지우기 경계는 원본 영역을 순방향 변환해 투명 구멍과 조각 픽셀을 일치시킴.
            var outMin = new Vector2(float.PositiveInfinity);
            var outMax = new Vector2(float.NegativeInfinity);
            foreach (var corner in (ReadOnlySpan<Vector2>)
                [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)])
            {
                var mapped = Vector2.Transform(corner, evaluation.NativeToOutput);
                outMin = Vector2.Min(outMin, mapped);
                outMax = Vector2.Max(outMax, mapped);
            }
            var erase = new EraseOp(new RectF(
                outMin.X, outMin.Y, outMax.X - outMin.X, outMax.Y - outMin.Y));
            var before = _viewModel.Editor.State.Transform;
            _ = TransformEvaluator.Evaluate(before.Append(erase), document.NativeSize);

            _assetCache.Warm(asset, SKImage.FromEncodedData(SKData.CreateCopy(png))
                ?? throw new InvalidDataException("Lifted region cannot be decoded."));
            _viewModel.Editor.Apply(new LiftRegionCommand(
                asset, annotation, _activeLayerId, before, erase));
            _regionInteraction.CancelAll();
            _selectedAnnotation = annotation.Id;
            _tool = CanvasTool.Select;

            // 현재 누름을 선택 주석 드래그로 이어감.
            BeginGesture(document);
            _dragAnnotation = annotation.Id;
            _dragOrigin = annotation.Bounds;
            _dragStartNative = ToNative(device) ?? new SKPoint(x0, y0);
            _dragMoved = false;
            UpdateLayerPanel();
            UpdateToolUi();
            UpdateEditCommands();
            Canvas.Invalidate();
            UpdateStatusBar();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
            or ArgumentException or IOException)
        {
            SetStatusState($"{AppStrings.EditFailed}: {ex.Message}");
        }
    }

    /// <summary>검토 영역을 PNG로 복사한 뒤 지움. 복사 실패면 픽셀도 보존.</summary>
    private async Task CutRegionToClipboardAsync()
    {
        if (_viewModel.Editor.Document is not { } document
            || _tool != CanvasTool.RegionSelect
            || !_regionInteraction.TryGetValidReview(
                document.Id, _viewModel.Editor.Revision, out var review))
            return;

        if (!await CopyToClipboardAsync())
            return;
        if (_viewModel.Editor.Document?.Id != document.Id)
            return;
        if (ApplyTransformOp(TransformEditKind.Erase, new EraseOp(review.Bounds)))
            SetStatusState(AppStrings.RegionCutDone);
    }

    private bool TryCommitCropReviewFromKeyboard() =>
        !_colorFlyoutOpen && _activeDialog is null && TryCommitCropReview();

    private async void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_colorFlyoutOpen || _activeDialog is not null)
            return;

        var point = e.GetPosition(Canvas);
        var scale = (float)Canvas.XamlRoot.RasterizationScale;
        var device = new SKPoint((float)point.X * scale, (float)point.Y * scale);

        if (_cropInteraction.Review is { } review)
        {
            var output = ToOutput(device);
            if (!review.Contains(output.X, output.Y))
                return;
            e.Handled = TryCommitCropReview();
            return;
        }

        // 빈 작업 공간 더블클릭은 파일 선택기 열기.
        if (_viewModel.Editor.Document is null || ToNativeVisible(device) is not { } native)
        {
            e.Handled = true;
            await OpenPickerAsync();
            return;
        }

        // 텍스트가 있는 주석을 더블클릭하면 내용 편집기 열기.
        if (_tool != CanvasTool.Select)
            return;
        var hit = _viewModel.Editor.State.HitTest(native.X, native.Y);
        if (hit is not (TextAnnotation or SpeechBubbleAnnotation) || hit.IsLocked)
            return;
        _selectedAnnotation = hit.Id;
        UpdateLayerPanel();
        UpdateToolUi();
        UpdateEditCommands();
        Canvas.Invalidate();
        e.Handled = true;
        await EditAnnotationTextAsync(hit);
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

    /// <summary>장치 픽셀 → 출력 캔버스 픽셀.</summary>
    private SKPoint ToOutput(SKPoint devicePoint) => _transform.ViewToContent(devicePoint);

    /// <summary>장치 픽셀 → 원본 픽셀. 캔버스 밖 드래그도 이어지도록 범위 제한 없음.</summary>
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
    /// 보이는 내용 위의 장치 픽셀만 원본 픽셀로 변환.
    /// 출력 캔버스와 원본 클립 밖은 역변환 전에 빠르게 거절.
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

    // ---- 명령 --------------------------------------------------------------------------------

    private async void OnOpenClicked(object sender, RoutedEventArgs e) => await OpenPickerAsync();
    private async void OnClipboardClicked(object sender, RoutedEventArgs e) => await OpenFromClipboardAsync();

    private async void OnCaptureClicked(object sender, RoutedEventArgs e) =>
        await (AppServices.Capture?.RequestCaptureAsync(this) ?? Task.CompletedTask);

    private async void OnWhiteboardWhiteClicked(object sender, RoutedEventArgs e) =>
        await OpenWhiteboardAsync(WhiteboardStyle.White);

    private async void OnWhiteboardBlackClicked(object sender, RoutedEventArgs e) =>
        await OpenWhiteboardAsync(WhiteboardStyle.Black);

    private async Task OpenWhiteboardAsync(WhiteboardStyle style)
    {
        try
        {
            // 8.3MP 채우기·PNG 인코딩은 UI 밖에서 수행. 교체 질문은 일반 메모리 원본과 동일.
            var bytes = await Task.Run(() => WhiteboardFactory.CreatePng(style));
            _viewModel.OpenGeneratedBytes(bytes);
        }
        catch (Exception ex)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
    }

    // ---- 캡처 알림(감지 시점 데이터 보존) --------------------------------------------------

    private Capture.Clipboard.ClipboardImagePayload? _pendingCaptureNotice;

    /// <summary>요청하지 않은 캡처를 UI 스레드에서 알림.</summary>
    public void ShowCaptureNotice(Capture.Clipboard.ClipboardImagePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _pendingCaptureNotice = payload;
        CaptureBar.Title = AppStrings.CaptureNoticeTitle;
        CaptureOpenButton.Content = AppStrings.CaptureNoticeOpen;
        CaptureBar.IsOpen = true;
    }

    /// <summary>캡처 실패 등 창 밖에서 온 일시 상태 문구.</summary>
    public void ShowTransientStatus(string text) => SetStatusState(text);

    /// <summary>캡처 오버레이 동안 창을 숨김. 완료 경로는 모두 Activate로 복원.</summary>
    public void PrepareForCapture()
    {
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Minimize();
    }

    /// <summary>
    /// 캡처로 최소화한 창을 복원하고 잠깐 최상단으로 올림.
    /// 오버레이 정리가 끝나 이전 앱이 다시 활성화된 뒤 최상단 상태 해제.
    /// </summary>
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
    // 오버레이 정리가 이전 앱을 다시 활성화하기에 충분한 시간.
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

    /// <summary>캡처 중 전경 권한을 잃은 프로세스가 창을 복원하도록 입력 큐를 잠깐 연결.</summary>
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
    private const uint SwpNoMoveNoSizeShow = 0x0001 | 0x0002 | 0x0040; // 크기·위치 유지, 창 표시.

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
            // 알림으로 열어도 저장 안 한 편집 보호 게이트는 꼬박 통과.
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

    private async void OnSettingsLinkRequested(object? sender, Uri target)
    {
        try
        {
            if (await Launcher.LaunchUriAsync(target))
                return;
            ReportExternalPageLaunchFailure();
        }
        catch (Exception ex)
        {
            ReportExternalPageLaunchFailure(ex);
        }
    }

    private void ReportExternalPageLaunchFailure(Exception? exception = null)
    {
        SetStatusState(AppStrings.LinkOpenFailed);
        _ = AppServices.Logs.TryEnqueue(
            LocalLogLevel.Warning,
            new StructuredLogEvent
            {
                Name = StructuredLogEventNames.ReleasePageLaunchFailed,
                ErrorCode = "shell_launch_failed",
            },
            exception);
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

    private void OnRotateCcwClicked(object sender, RoutedEventArgs e) =>
        ApplyTransformOp(TransformEditKind.Rotate, new RotateOp(270f));

    private void OnAnnotationToolClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string name } button
            || !Enum.TryParse<CanvasTool>(name, out var tool))
            return;
        // 분할 선택 버튼은 기억한 선택 모드 사용. 평면 막대에서는 영역 버튼이 따로 있어 객체 선택 유지.
        if (tool == CanvasTool.Select && _selectGroupEnabled && _regionSelectMode)
            tool = CanvasTool.RegionSelect;
        SetTool(button.IsChecked == true ? tool : CanvasTool.Select);
    }

    /// <summary>드롭다운 항목 앞에 도구 막대 아이콘 표시. 공유 글리프는 복제해 사용.</summary>
    private void ConfigureGroupedMenus()
    {
        SetMenuItem(OpenMenuItem, AppStrings.ToolOpen, "Icon.File.Open");
        SetMenuItem(RecentMenuItem, AppStrings.ToolRecent, "Icon.File.Recent");
        SetMenuItem(ClipboardMenuItem, AppStrings.ToolClipboard, "Icon.File.Clipboard");
        SetMenuItem(CaptureMenuItem, AppStrings.ToolCapture, "Icon.File.Capture");
        WhiteboardSubMenu.Text = AppStrings.ToolWhiteboard;
        WhiteboardWhiteMenuItem.Text = AppStrings.WhiteboardWhite;
        WhiteboardBlackMenuItem.Text = AppStrings.WhiteboardBlack;
        SetMenuItem(NewWindowMenuItem, AppStrings.ToolNewWindow, "Icon.File.NewWindow");
        SetMenuItem(CropMenuItem, AppStrings.MenuCrop, "Icon.Image.Crop");
        SetMenuItem(CropRatioMenuItem, AppStrings.MenuCropRatio, "Icon.Image.CropRatio");
        SetMenuItem(ResizeMenuItem, AppStrings.MenuResize, "Icon.Image.Resize");
        SetMenuItem(FitMenuItem, AppStrings.ToolFit, "Icon.View.Fit");
        // 1:1 아이콘은 화이트보드처럼 XAML 인라인 사용자 경로.
        ActualSizeMenuItem.Text = AppStrings.ToolActualSize;
        SetMenuItem(MosaicMenuItem, AppStrings.ToolMosaic, "Icon.Protect.Mosaic");
        SetMenuItem(BlurMenuItem, AppStrings.ToolBlur, "Icon.Protect.Blur");
        SetMenuItem(MaskMenuItem, AppStrings.ToolMask, "Icon.Protect.Mask");
        SetMenuItem(RotateCwMenuItem, AppStrings.ToolRotate, "Icon.Image.Rotate");
        SetMenuItem(RotateCcwMenuItem, AppStrings.ToolRotateCcw, "Icon.Image.Rotate", mirrored: true);
        SetMenuItem(FlipHorizontalMenuItem, AppStrings.ToolFlipHorizontal, "Icon.Image.FlipHorizontal");
        SetMenuItem(FlipVerticalMenuItem, AppStrings.ToolFlipVertical, "Icon.Image.FlipVertical", rotated: true);
        SetMenuItem(SelectModeObjectItem, AppStrings.ToolSelect, "Icon.Image.Select");
    }

    private void SetMenuItem(
        MenuFlyoutItem item, string text, string iconKey, bool mirrored = false, bool rotated = false)
    {
        item.Text = text;
        var icon = new FontIcon
        {
            FontFamily = (FontFamily)Root.Resources["Icon.FontFamily"],
            FontSize = 16,
            Glyph = ((FontIconSource)Root.Resources[iconKey]).Glyph,
        };
        // 대칭은 회전 글리프의 반시계 변형, 회전은 공유 뒤집기 글리프를 세로로 돌려 재사용.
        if (mirrored || rotated)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = mirrored
                ? new ScaleTransform { ScaleX = -1 }
                : new RotateTransform { Angle = 90 };
        }
        item.Icon = icon;
    }

    /// <summary>각 도구 그룹을 드롭다운·분할 버튼과 평면 버튼 사이에서 독립 전환.</summary>
    private void ApplyToolbarGrouping()
    {
        ApplyGroup(_openGroupEnabled, OpenGroupButton,
            OpenButton, RecentButton, ClipboardButton, CaptureButton,
            WhiteboardButton, NewWindowButton);
        ApplyGroup(_transformGroupEnabled, TransformGroupButton,
            RotateButton, FlipHorizontalButton, FlipVerticalButton);
        ApplyGroup(_cropGroupEnabled, CropGroupButton,
            CropButton, CropRatioButton, ResizeButton);
        ApplyGroup(_zoomGroupEnabled, ZoomGroupButton, FitButton, ActualSizeButton);
        ApplyGroup(_protectGroupEnabled, ProtectGroupButton, MosaicButton, BlurButton, MaskButton);
        SelectModeButton.Visibility = _selectGroupEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        RegionSelectButton.Visibility = _selectGroupEnabled
            ? Visibility.Collapsed : Visibility.Visible;
        // 다시 묶을 때 현재 선택 모드를 분할 버튼에 기억.
        if (_selectGroupEnabled)
            _regionSelectMode = _tool == CanvasTool.RegionSelect;
        UpdateSelectButtonIcon();
        UpdateToolUi();
        QueueToolRailOverflowUpdate(resetToStart: false);

        static void ApplyGroup(bool grouped, UIElement groupButton, params UIElement[] flatButtons)
        {
            groupButton.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in flatButtons)
                button.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>평면 토글 버튼의 드롭다운 짝. 같은 도구를 다시 누르면 객체 선택으로 복귀.</summary>
    private void OnToolMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string name }
            || !Enum.TryParse<CanvasTool>(name, out var tool))
            return;
        SetTool(_tool == tool ? CanvasTool.Select : tool);
    }

    private void OnSelectModeObjectClicked(object sender, RoutedEventArgs e) => ApplySelectMode(false);

    private void OnSelectModeRegionClicked(object sender, RoutedEventArgs e) => ApplySelectMode(true);

    private void ApplySelectMode(bool regionMode)
    {
        _regionSelectMode = regionMode;
        UpdateSelectButtonIcon();
        SetTip(SelectButton,
            regionMode ? AppStrings.SelectModeRegion : AppStrings.ToolSelect,
            regionMode ? AppStrings.TipRegionSelect : AppStrings.TipSelect);
        SetTool(regionMode ? CanvasTool.RegionSelect : CanvasTool.Select);
    }

    /// <summary>아이콘 원본 재할당 대신 표시만 교환. 원본 공유 재할당 시 글리프가 가끔 사라짐.</summary>
    private void UpdateSelectButtonIcon()
    {
        var showRegion = _selectGroupEnabled && _regionSelectMode;
        SelectObjectIcon.Visibility = showRegion ? Visibility.Collapsed : Visibility.Visible;
        SelectRegionIcon.Visibility = showRegion ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCropClicked(object sender, RoutedEventArgs e) =>
        SetTool(CropButton.IsChecked == true ? CanvasTool.Crop : CanvasTool.Select);

    /// <summary>편집 도구는 하나만 활성화. 바꾸면 진행 중 초안 폐기.</summary>
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

    private void OnStrokeWidthChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _strokeWidth = (float)newValue;
        if (SelectedAnnotation() is { IsLocked: false } selected)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, selected switch
            {
                InkAnnotation ink => ink with { StrokeWidth = _strokeWidth },
                LineAnnotation line => line with { StrokeWidth = _strokeWidth },
                RectangleAnnotation rectangle => rectangle with { StrokeWidth = _strokeWidth },
                SpeechBubbleAnnotation bubble => bubble with { StrokeWidth = _strokeWidth },
                _ => selected,
            });
        }
        SaveCurrentToolStyle();
    }

    private void OnOpacityChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _opacity = (float)(newValue / 100d);
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
                SpeechBubbleAnnotation bubble => bubble with { Opacity = _opacity },
                _ => selected,
            });
        }
        SaveCurrentToolStyle();
    }

    private void OnBlockSizeChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _mosaicBlockSize = (float)newValue;
        if (SelectedAnnotation() is ProtectionAnnotation { Kind: ProtectionKind.Mosaic, IsLocked: false } mosaic)
            ApplySelectedEdit(AnnotationEditKind.Style, mosaic with { BlockSize = _mosaicBlockSize });
        PublishCurrentToolDefaults();
    }

    private void OnBlurSigmaChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _blurSigma = (float)newValue;
        if (SelectedAnnotation() is ProtectionAnnotation { Kind: ProtectionKind.Blur, IsLocked: false } blur)
            ApplySelectedEdit(AnnotationEditKind.Style, blur with { BlurSigma = _blurSigma });
        PublishCurrentToolDefaults();
    }

    private void OnFontSizeChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _fontSize = (float)newValue;
        if (SelectedAnnotation() is { IsLocked: false } selected)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, selected switch
            {
                TextAnnotation text => text with { FontSize = _fontSize },
                NumberMarkerAnnotation marker => marker with { FontSize = _fontSize },
                SpeechBubbleAnnotation bubble => bubble with { FontSize = _fontSize },
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

    private void OnCornerRadiusChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue))
            return;
        _cornerRadius = (float)newValue;
        if (SelectedAnnotation() is { IsLocked: false } roundable)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, roundable switch
            {
                RectangleAnnotation rectangle => rectangle with { CornerRadius = _cornerRadius },
                SpeechBubbleAnnotation bubble => bubble with { CornerRadius = _cornerRadius },
                _ => roundable,
            });
        }
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

    /// <summary>설치 글꼴 선택. 사라진 저장 글꼴도 맨 앞에 붙여 현재 값 표시·재저장 허용.</summary>
    private void SelectFontFamily(string family)
    {
        var index = -1;
        for (var i = 0; i < _fontFamilies.Count; i++)
        {
            if (string.Equals(_fontFamilies[i], family, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0 && !string.IsNullOrWhiteSpace(family))
        {
            _fontFamilies.Insert(0, family);
            index = 0;
        }
        FontFamilyBox.SelectedIndex = index;
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolControls || FontFamilyBox.SelectedItem is not string family
            || string.IsNullOrWhiteSpace(family))
            return;
        _fontFamily = family;
        if (SelectedAnnotation() is { IsLocked: false } fontOwner)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, fontOwner switch
            {
                TextAnnotation text => text with { FontFamily = _fontFamily },
                SpeechBubbleAnnotation bubble => bubble with { FontFamily = _fontFamily },
                _ => fontOwner,
            });
        }
        PublishCurrentToolDefaults();
    }

    private void OnTextStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingToolControls)
            return;
        _fontBold = BoldButton.IsChecked == true;
        _fontItalic = ItalicButton.IsChecked == true;
        if (SelectedAnnotation() is { IsLocked: false } styled)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, styled switch
            {
                TextAnnotation text => text with { IsBold = _fontBold, IsItalic = _fontItalic },
                SpeechBubbleAnnotation bubble => bubble with
                {
                    IsBold = _fontBold,
                    IsItalic = _fontItalic,
                },
                _ => styled,
            });
        }
        PublishCurrentToolDefaults();
    }

    private void OnTextAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolControls
            || TextAlignmentBox.SelectedItem is not ComboBoxItem
            { Tag: AnnotationTextAlignment alignment })
            return;
        _textAlignment = alignment;
        if (SelectedAnnotation() is { IsLocked: false } aligned)
        {
            ApplySelectedEdit(AnnotationEditKind.Style, aligned switch
            {
                TextAnnotation text => text with { Alignment = alignment },
                SpeechBubbleAnnotation bubble => bubble with { Alignment = alignment },
                _ => aligned,
            });
        }
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
            or CanvasTool.RegionSelect
            || (_tool == CanvasTool.Select && !selectedMode)
            ? Visibility.Collapsed : Visibility.Visible;
        ToolContextLabel.Text = selectedMode ? AnnotationName(selected!) : ToolName(_tool);
        var hasStroke = selectedMode
            ? selected is InkAnnotation or LineAnnotation or RectangleAnnotation
                or SpeechBubbleAnnotation
            : _tool is not CanvasTool.Text and not CanvasTool.Number and not CanvasTool.Eyedropper
                and not CanvasTool.Mosaic and not CanvasTool.Blur and not CanvasTool.Mask;
        var hasFont = selectedMode
            ? selected is TextAnnotation or NumberMarkerAnnotation or SpeechBubbleAnnotation
            : _tool is CanvasTool.Text or CanvasTool.Number or CanvasTool.SpeechBubble;
        // 보호 효과는 항상 완전 불투명. 투명도 조절 없음.
        var hasOpacity = selectedMode
            ? selected is not ProtectionAnnotation
            : _tool is not CanvasTool.Mosaic and not CanvasTool.Blur and not CanvasTool.Mask;
        var hasBlockSize = selectedMode
            ? selected is ProtectionAnnotation { Kind: ProtectionKind.Mosaic }
            : _tool == CanvasTool.Mosaic;
        var hasBlurSigma = selectedMode
            ? selected is ProtectionAnnotation { Kind: ProtectionKind.Blur }
            : _tool == CanvasTool.Blur;
        StrokeWidthGroup.Visibility = hasStroke ? Visibility.Visible : Visibility.Collapsed;
        OpacityGroup.Visibility = hasOpacity ? Visibility.Visible : Visibility.Collapsed;
        BlockSizeGroup.Visibility = hasBlockSize ? Visibility.Visible : Visibility.Collapsed;
        BlurSigmaGroup.Visibility = hasBlurSigma ? Visibility.Visible : Visibility.Collapsed;
        FontSizeGroup.Visibility = hasFont ? Visibility.Visible : Visibility.Collapsed;
        var isShape = selectedMode
            ? selected is RectangleAnnotation
            : _tool is CanvasTool.Rectangle or CanvasTool.RoundedRectangle or CanvasTool.Ellipse;
        FillCheckBox.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
        CornerRadiusGroup.Visibility = selectedMode
            ? selected is RectangleAnnotation { Shape: ShapeKind.RoundedRectangle }
                or SpeechBubbleAnnotation
                ? Visibility.Visible : Visibility.Collapsed
            : _tool is CanvasTool.RoundedRectangle or CanvasTool.SpeechBubble
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArrowheadGroup.Visibility = selectedMode
            ? selected is LineAnnotation ? Visibility.Visible : Visibility.Collapsed
            : _tool == CanvasTool.Arrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isTextLike = selectedMode
            ? selected is TextAnnotation or SpeechBubbleAnnotation
            : _tool is CanvasTool.Text or CanvasTool.SpeechBubble;
        // 말풍선 배경은 필수 채우기 색 하나만 사용. 선택 텍스트 배경까지 열면 상태가 둘로 갈림.
        var isPlainText = selectedMode ? selected is TextAnnotation : _tool == CanvasTool.Text;
        FontFamilyGroup.Visibility = isTextLike ? Visibility.Visible : Visibility.Collapsed;
        BoldButton.Visibility = isTextLike ? Visibility.Visible : Visibility.Collapsed;
        ItalicButton.Visibility = isTextLike ? Visibility.Visible : Visibility.Collapsed;
        AlignmentGroup.Visibility = isTextLike ? Visibility.Visible : Visibility.Collapsed;
        TextBackgroundCheckBox.Visibility = isPlainText ? Visibility.Visible : Visibility.Collapsed;
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
        RotationGroup.Visibility = selected is ProtectionAnnotation
            ? Visibility.Collapsed : objectCommands;
        SendToBackButton.Visibility = objectCommands;
        SendBackwardButton.Visibility = objectCommands;
        BringForwardButton.Visibility = objectCommands;
        BringToFrontButton.Visibility = objectCommands;
        DuplicateButton.Visibility = objectCommands;
        EditTextButton.Visibility = selected is TextAnnotation or SpeechBubbleAnnotation
            ? Visibility.Visible : Visibility.Collapsed;
        _updatingToolControls = true;
        try
        {
            StrokeWidthBox.Value = selected switch
            {
                InkAnnotation ink => ink.StrokeWidth,
                LineAnnotation line => line.StrokeWidth,
                RectangleAnnotation rectangle => rectangle.StrokeWidth,
                SpeechBubbleAnnotation bubble => bubble.StrokeWidth,
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
                SpeechBubbleAnnotation bubble => bubble.FontSize,
                _ => _fontSize,
            };
            FillCheckBox.IsChecked = selected is RectangleAnnotation filledRectangle
                ? filledRectangle.FillArgb is not null : _fillEnabled;
            CornerRadiusBox.Value = selected switch
            {
                RectangleAnnotation rounded => rounded.CornerRadius,
                SpeechBubbleAnnotation bubble => bubble.CornerRadius,
                _ => _cornerRadius,
            };
            var selectedArrow = selected is LineAnnotation selectedLine
                ? selectedLine.EndArrowhead : _arrowhead;
            ArrowheadBox.SelectedIndex = selectedArrow == ArrowheadKind.Open ? 0 : 1;
            SelectFontFamily(selected switch
            {
                TextAnnotation textValue => textValue.FontFamily,
                SpeechBubbleAnnotation bubbleFont => bubbleFont.FontFamily,
                _ => _fontFamily,
            });
            BoldButton.IsChecked = selected switch
            {
                TextAnnotation boldText => boldText.IsBold,
                SpeechBubbleAnnotation boldBubble => boldBubble.IsBold,
                _ => _fontBold,
            };
            ItalicButton.IsChecked = selected switch
            {
                TextAnnotation italicText => italicText.IsItalic,
                SpeechBubbleAnnotation italicBubble => italicBubble.IsItalic,
                _ => _fontItalic,
            };
            TextAlignmentBox.SelectedIndex = (int)(selected switch
            {
                TextAnnotation aligned => aligned.Alignment,
                SpeechBubbleAnnotation alignedBubble => alignedBubble.Alignment,
                _ => _textAlignment,
            });
            TextBackgroundCheckBox.IsChecked = selected is TextAnnotation background
                ? background.BackgroundArgb is not null : _textBackgroundEnabled;
            ObjectRotationBox.Value = selected?.RotationDegrees ?? 0f;
        }
        finally
        {
            _updatingToolControls = false;
        }
        // 도구·선택 변경에 따라 팔레트의 마스크 색 문맥도 바뀔 수 있음.
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
        SpeechBubbleAnnotation bubble => bubble.Opacity,
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
        SpeechBubbleAnnotation => AppStrings.LayerTypeSpeechBubble,
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
            var visible = _viewModel.Editor.Document is not null && autoVisible;
            LayerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>행 하나가 레이어 전체. 표시·이름·잠금만 두며 객체는 행이 아님.</summary>
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

    /// <summary>이름 없는 레이어의 위치 기반 이름. 아래부터 번호 부여.</summary>
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

    private void OnLayerCollapseClicked(object sender, RoutedEventArgs e)
    {
        _layerPanelCollapsed = !_layerPanelCollapsed;
        ApplyLayerPanelCollapse();
    }

    /// <summary>헤더는 남기고 목록·동작만 접는 레이어 패널.</summary>
    private void ApplyLayerPanelCollapse()
    {
        var body = _layerPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LayerList.Visibility = body;
        LayerFooterButtons.Visibility = body;
        // 패널이 아래 고정이라 화살표도 본문이 펼쳐질 방향을 가리킴.
        LayerCollapseIcon.IconSource = IconSourceFor(
            _layerPanelCollapsed ? "Icon.Layer.Up" : "Icon.Layer.Down");
        SetTip(LayerCollapseButton,
            _layerPanelCollapsed ? AppStrings.LayerExpand : AppStrings.LayerCollapse,
            AppStrings.TipLayerCollapse);
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

    /// <summary>작성 명령 게이트. 활성 레이어가 보이고 잠금 해제돼야 함.</summary>
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

        // 휠 위는 막대 시작, 아래는 끝 방향.
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
            // 막대 여백까지 덮어 힌트를 가장자리에 밀착. 끝쪽 모서리는 막대와 같은 12px.
            hint.Margin = horizontal
                ? new Thickness(0, -4, start ? 0 : -8, -4)
                : new Thickness(-4, 0, -4, start ? 0 : -8);
            hint.CornerRadius = start
                ? new CornerRadius(0)
                : horizontal ? new CornerRadius(0, 12, 12, 0) : new CornerRadius(0, 0, 12, 12);
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
        // 어느 도킹이든 막대 두께 44px: 버튼 36px + 두께 축 여백 4px씩.
        ToolRail.Padding = horizontal ? new Thickness(8, 4, 8, 4) : new Thickness(4, 8, 4, 8);
        ToolRailItems.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        foreach (var group in new[]
        {
            FileToolGroup, ZoomToolGroup, HistoryToolGroup, ImageToolGroup, DrawingToolGroup,
            ShapeToolGroup, TextToolGroup, ProtectionToolGroup, ViewToolGroup,
        })
            group.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        foreach (var separator in new[]
        {
            DockMenuSeparator, FileZoomSeparator, ZoomHistorySeparator, HistoryImageSeparator,
            ImageDrawingSeparator, DrawingShapeSeparator, ShapeTextSeparator,
            TextProtectionSeparator, ProtectionViewSeparator,
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
        SelectModeButton.Width = horizontal ? 14 : 36;
        SelectModeButton.Height = horizontal ? 36 : 14;
        // 막대 플라이아웃은 막대 반대쪽으로 열기. 기본 위치는 창 가장자리에 잘림.
        foreach (var flyout in new[]
        {
            OpenGroupButton.Flyout, TransformGroupButton.Flyout,
            WhiteboardButton.Flyout, SelectModeButton.Flyout,
        })
        {
            if (flyout is not null)
                flyout.Placement = horizontal
                    ? FlyoutPlacementMode.BottomEdgeAlignedLeft
                    : FlyoutPlacementMode.RightEdgeAlignedTop;
        }
        Grid.SetRow(DockToggleButton, 0);
        Grid.SetColumn(DockToggleButton, 0);
        Grid.SetRow(DockMenuSeparator, horizontal ? 0 : 1);
        Grid.SetColumn(DockMenuSeparator, horizontal ? 1 : 0);
        Grid.SetRow(ToolRailScrollableViewport, horizontal ? 0 : 2);
        Grid.SetColumn(ToolRailScrollableViewport, horizontal ? 2 : 0);
        ToolRail.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ToolRail.VerticalAlignment = horizontal ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        ToolRail.MaxHeight = horizontal ? 44 : double.PositiveInfinity;
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
        AnnotationContextBar.Margin = horizontal
            ? new Thickness(12, 68, 12, 0)
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
        editor.LinkRequested += OnSettingsLinkRequested;
        AppSettings? candidate = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = AppStrings.SettingsTitle,
            Content = editor,
            PrimaryButtonText = AppStrings.SettingsSave,
            CloseButtonText = AppStrings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        // 페이지 허브가 기본 대화상자 너비 상한 548px보다 넓음.
        dialog.Resources["ContentDialogMaxWidth"] = 800d;
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
        editor.ApplyPendingAssociations();
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

    /// <summary>가로 전용 컨텍스트 막대에 세로 휠 입력을 연결.</summary>
    private void OnContextBarPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (ContextBarScroll.ScrollableWidth <= 0)
            return;
        var delta = e.GetCurrentPoint(ContextBarScroll).Properties.MouseWheelDelta;
        ContextBarScroll.ChangeView(
            ContextBarScroll.HorizontalOffset - delta, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private void OnObjectRotationChanged(CompactNumberBox sender, double newValue)
    {
        if (_updatingToolControls || !double.IsFinite(newValue)
            || SelectedAnnotation() is not { IsLocked: false } selected)
            return;
        if (selected is ProtectionAnnotation)
            return;
        ApplySelectedEdit(AnnotationEditKind.Geometry,
            selected with { RotationDegrees = (float)newValue });
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
            if (tool is CanvasTool.Select or CanvasTool.Crop or CanvasTool.RegionSelect)
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
        CanvasTool.SpeechBubble => AppStrings.ToolSpeechBubble,
        CanvasTool.Mosaic => AppStrings.ToolMosaic,
        CanvasTool.Blur => AppStrings.ToolBlur,
        CanvasTool.Mask => AppStrings.ToolMask,
        CanvasTool.Eyedropper => AppStrings.ToolEyedropper,
        CanvasTool.Crop => AppStrings.ToolCrop,
        CanvasTool.RegionSelect => AppStrings.SelectModeRegion,
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

    /// <summary>파이프라인 편집 단일 입구. 기록 전 평가해 잘못된 작업은 상태바에서 차단.</summary>
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

    /// <summary>명령 적용 전 초안·자르기 검토·포인터 캡처 정리.</summary>
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
        _regionInteraction.CancelAll();
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

    /// <summary>Esc는 전체 화면 해제보다 진행 중 작성 초안을 먼저 취소.</summary>
    private void OnEscape()
    {
        if (_drawAnchor is not null || _inkPoints.Count > 0
            || _cropInteraction.Phase != CropInteractionPhase.Idle
            || _regionInteraction.Phase != CropInteractionPhase.Idle)
        {
            CancelActiveGesture();
            Canvas.Invalidate();
            UpdateStatusBar();
            return;
        }
        ExitFullScreen();
    }

    // ---- 저장·내보내기·복사 ---------------------------------------------------------------

    /// <summary>내보내기 평면화 원본과 필요 시 전체 해상도 재디코드 수명 소유.</summary>
    private readonly record struct ExportFrame(SKImage Frame, bool OwnsFrame) : IDisposable
    {
        public void Dispose()
        {
            if (OwnsFrame)
                Frame.Dispose();
        }
    }

    /// <summary>빠른 저장은 추적 대상 사용. 대상이 없거나 다른 이름 저장이면 선택기 표시.</summary>
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
        // 선택기 기본값은 프로젝트면 프로젝트, 이미지면 PNG.
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

        // 원본 덮어쓰기는 명시적 확인 때만. 기본값으로 슬쩍 밀어 넣지 않음.
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
                // 파일·프로젝트 원본에만 보존할 메타데이터가 있음.
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
            // 실제 쓴 상태 토큰만 저장 완료 처리. 쓰는 중 편집이 끼면 수정 상태 유지.
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
                // 메타데이터 보존 요청을 조용히 무시하면 저장 결과를 거짓말하게 됨.
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
            // 선택기·쓰기가 문서 교체를 걸쳤어도 다른 문서를 대상으로 바꾸거나 저장 표시 금지.
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

    /// <summary>평면화한 편집 결과를 투명도 포함 복사. 검토 중인 자르기·영역 선택은 해당 부분만 복사.</summary>
    private async Task<bool> CopyToClipboardAsync()
    {
        if (_savingInProgress || _viewModel.Editor.Document is not { } document || _snapshot is null)
            return false;
        RectF? region = null;
        if (_cropInteraction.Phase == CropInteractionPhase.Reviewing)
        {
            if (!_cropInteraction.TryGetValidReview(
                document.Id, _viewModel.Editor.Revision, out var review))
            {
                _cropInteraction.CancelAll();
                Canvas.Invalidate();
                UpdateStatusBar();
                SetStatusState(AppStrings.CopyRegionStale);
                return false;
            }
            region = review.Bounds;
        }
        else if (_tool == CanvasTool.RegionSelect
            && _regionInteraction.Phase == CropInteractionPhase.Reviewing)
        {
            if (!_regionInteraction.TryGetValidReview(
                document.Id, _viewModel.Editor.Revision, out var review))
            {
                _regionInteraction.CancelAll();
                Canvas.Invalidate();
                UpdateStatusBar();
                SetStatusState(AppStrings.CopyRegionStale);
                return false;
            }
            region = review.Bounds;
        }
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
                using var flattened = region is { } bounds
                    ? DocumentFlattener.FlattenRegion(
                        frame, document.NativeSize, state, bounds, assets)
                    : DocumentFlattener.Flatten(
                        frame, document.NativeSize, state, assets);
                return ImageExporter.Encode(flattened, ExportFormat.Png);
            }, token);
            if (_viewModel.Editor.Document?.Id != document.Id)
                return false;
            await _clipboard.SetImagePngAsync(png, token);
            // 캡처 감시가 내부 복사를 새 캡처로 착각하지 않게 기록.
            AppServices.Capture?.NoteInternalCopy(png);
            SetStatusState(region is null ? AppStrings.CopyDone : AppStrings.CopyRegionDone);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or InvalidDataException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            SetStatusState($"{AppStrings.SaveFailed}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 내보내기용 소유 프레임. 축소 미리보기는 원본을 전체 해상도로 다시 디코드.
    /// 불가능하면 저해상도 확대 대신 명시적으로 실패.
    /// </summary>
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

    /// <summary>작업자 평면화에 넘겨도 안전한 UI 스냅샷 소유 복사본.</summary>
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

    /// <summary>로드 때 잡은 길이·수정 시각과 원본을 대조. 바뀌거나 사라졌으면 저장 거절.</summary>
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

    /// <summary>몇 KB EXIF 때문에 거대 원본을 읽지 않도록 둔 메타데이터 원본 상한.</summary>
    private const long MetadataReadBudget = 64L * 1024 * 1024;

    /// <summary>메타데이터 보존용 원본 바이트. 보조 정보라 실패하면 메타데이터 없이 저장하고 알림.</summary>
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

    /// <summary>내보내기별 새 캐시. UI 소유 캐시와 작업자 사이 공유 금지.</summary>
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

    /// <summary>프로젝트에 넣을 배경 원본. 파일·기존 내장 원본·렌더 배경 중 출처에 맞게 선택.</summary>
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
                // 원본이 바뀌거나 사라지면 저장 거절. 다른 픽셀로 몰래 갈아타지 않음.
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
                // 읽은 뒤 다시 확인해 외부 교체 파일이 복구 배경으로 끼어드는 경합 차단.
                if (SourceIsUnchanged(document, path))
                    return (Path.GetFileName(path), bytes);
            }
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 아래 제한된 렌더 스냅샷이 충실도를 지키는 복구 원본.
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

    /// <summary>갤러리용 작은 미리보기. 작업자에서 바로 목표 크기로 렌더.</summary>
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
        // 기본은 전체 제거. 보존을 골라도 GPS·MakerNote·일련번호·편집 전 썸네일은 제거.
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

    // ---- 저장하지 않은 변경 확인 ----------------------------------------------------------

    /// <summary>닫기를 먼저 취소하고 편집·저장을 비동기로 정리한 뒤 승인되면 다시 닫음.</summary>
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
            return DiscardDecision.Discard; // 물어볼 UI가 없는 무인 실행.

        // 저장 / 저장 안 함 / 취소. 저장 성공을 여기서 해석한 뒤 교체 허용.
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
        // 대화상자 사용 중 거절은 취소로 처리. 두 번째 창도, 데이터 손실도 없음.
        var result = await ShowDialogAsync(dialog, editScoped: false);
        if (result == ContentDialogResult.Primary)
            return await SaveAsync(quick: true) ? DiscardDecision.Discard : DiscardDecision.Cancel;
        return result == ContentDialogResult.Secondary ? DiscardDecision.Discard : DiscardDecision.Cancel;
    }

    // ---- 대화상자 조정 ---------------------------------------------------------------------

    /// <summary>
    /// 루트당 하나뿐인 대화상자 단일 입구. 이미 열렸으면 대기열 대신 None 반환.
    /// 문서 교체 시 편집 대화상자는 닫되 교체 흐름인 저장 확인은 유지.
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
            dialog.Hide(); // ShowAsync는 None으로 완료.
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

        // px와 %는 함께 갱신. 비율 잠금이 반대 축을 따라오게 함.
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
        foreach (var extension in AppServices.ViewableExtensions)
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

    // ---- 파일·폴더 끌어놓기 ---------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // async void 이벤트라 예외가 빠져나가면 프로세스 종료. 여기서 전부 회수.
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
                    case StorageFile file when AppServices.ViewableExtensions.Contains(
                        Path.GetExtension(file.Path)):
                        paths.Add(file.Path);
                        break;
                    case StorageFolder folder:
                        {
                            var first = Directory.EnumerateFiles(folder.Path)
                                .Where(path => AppServices.ViewableExtensions.Contains(
                                    Path.GetExtension(path)))
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
        // 저장 항목 조회의 COM 예외도 async void 밖으로 나가면 치명적이므로 함께 회수.
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            SetStatusState($"{AppStrings.StateFailed}: {ex.Message}");
        }
    }

    // ---- 무인 검증 진입점 -------------------------------------------------------------------

    /// <summary>--smoke-open: 파일 로드 후 세션 결과를 JSON으로 남기고 종료.</summary>
    public void ConfigureSmoke(
        string path,
        string? resultPath,
        string? projectPath = null,
        bool captureExercise = false)
    {
        _resultPath = resultPath ?? Path.Combine(Path.GetTempPath(), "ezy-smoke.json");
        _smokeProjectPath = projectPath;
        _smokeCaptureExercise = captureExercise;
        OpenFiles([path]);
    }

    private bool _smokeCaptureExercise;

    /// <summary>--smoke-hold: 열고 한 번 편집한 채 유지해 외부 UIA가 실제 저장 닫기 경로 검증.</summary>
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

    /// <summary>가짜 데이터로 중복 억제·알림·자동 열기를 검증. 실제 클립보드는 손대지 않음.</summary>
    private async Task<CaptureExerciseResult> ExerciseCaptureAsync()
    {
        using var coordinator = new Capture.Snipping.CaptureCoordinator(
            new Capture.Snipping.CaptureCoordinatorOptions { ResolveTarget = () => this },
            listen: false);
        // 교체 질문이 무인 실행을 막지 않도록 깨끗한 문서에 캡처 적용.
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

        // 공식 경로: 요청 → 상관관계 콜백 → 주입 토큰 교환 → 자동 열기. 실제 도구는 실행 안 함.
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

    /// <summary>무인 수정 저장이 실제 바이트를 쓰게 하는 결정적 주석 하나.</summary>
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

    /// <summary>--bench-open24mp: 로드 시작부터 첫 Ready 그리기까지 측정.</summary>
    public void ConfigureFirstPaintBench(string path, string? resultPath)
    {
        _resultPath = resultPath ?? Path.Combine(Path.GetTempPath(), "ezy-first-paint.json");
        _firstPaintWatch = Stopwatch.StartNew();
        OpenFiles([path]);
    }

    /// <summary>--bench-startup: 프로세스 진입부터 기본 창 첫 프레임까지 측정.</summary>
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
                            phases = StartupTimeline.Snapshot(processStartTimestamp),
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
                return; // 첫 프레임이 실제로 그려질 때까지 대기.
            _unattendedFlowStarted = true;

            // 스모크 모드에서 GL 표면 크기 변경과 전체 화면 왕복도 확인.
            if (_firstPaintWatch is null && state == SessionState.Ready)
            {
                await ExerciseWindowAsync();
                _windowExercised = true;
            }

            // --smoke-project: 선택기를 뺀 실제 저장 경로 종단 검증.
            var projectSaved = false;
            var quickResaved = false;
            if (_smokeProjectPath is { } smokeProject && state == SessionState.Ready
                && _viewModel.Editor.Document is { } smokeDocument)
            {
                projectSaved = await WriteTargetAsync(
                    smokeDocument, new SaveTarget(smokeProject, null));
                if (projectSaved)
                {
                    // 빠른 재저장은 깨끗한 지름길 말고 편집 후 Ctrl+S로 실제 새 바이트 작성.
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

            // --smoke-capture: 사용자 소유 클립보드·오버레이 없이 캡처 정책 종단 검증.
            var capture = new CaptureExerciseResult(false, false, false, false, false, false);
            bool? launcherSupported = null;
            if (_smokeCaptureExercise && state == SessionState.Ready)
            {
                capture = await ExerciseCaptureAsync();
                // 구형 실행 계약을 실제 OS에서 비침습 확인.
                launcherSupported = await Capture.Snipping.CaptureLauncher.IsSnippingAvailableAsync();
            }

            var document = _viewModel.Session.Current;
            // 원시 프레임 크기와 함께 문서 실제 크기인 변환 출력 크기 기록.
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
                // 보고할 대상이 없으면 누락·오류 JSON 자체가 실패 정보.
            }
        }
        _canvasResizeSettleTimer?.Stop();
        Canvas.EnableRenderLoop = false;
        DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
    }

    /// <summary>두 번 크기 변경, 전체 화면 왕복, 다시 그리기로 GL 표면 스모크.</summary>
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
        // 레이어 컨테이너 검증: 두 번째 레이어 추가 후 객체 이동.
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
