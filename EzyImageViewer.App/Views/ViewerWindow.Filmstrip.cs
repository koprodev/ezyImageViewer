using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace EzyImageViewer.App.Views;

/// <summary>
/// 파일 위치 표시를 눌러 여는 가로 썸네일 스트립.
/// 창 내부 오버레이라 파일 선택과 방향키 탐색 중에도 유지되며 열 때 만들고 닫을 때 해제.
/// </summary>
public sealed partial class ViewerWindow
{
    private const int ThumbnailPixelSize = 96;
    private const double CardWidth = 108;
    private const double CardSpacing = 6;
    private const double CardHeight = 112;

    /// <summary>셸 썸네일 디스크 호출을 소수만 병렬 처리해 대형 폴더 요청 폭주 방지.</summary>
    private static readonly SemaphoreSlim ThumbnailRequests = new(4, 4);

    private ItemsRepeater? _filmstripRepeater;
    private ScrollViewer? _filmstripScroll;
    private IReadOnlyList<string> _filmstripFiles = [];
    private int _filmstripCurrentIndex = -1;
    private CancellationTokenSource? _filmstripLifetime;
    private readonly Dictionary<string, BitmapImage> _filmstripThumbnails = new(StringComparer.OrdinalIgnoreCase);

    private bool IsFilmstripOpen => FilmstripPanel.Visibility == Visibility.Visible;

    private void OnStatusPositionClicked(object sender, RoutedEventArgs e)
    {
        if (IsFilmstripOpen)
            CloseFilmstrip();
        else
            OpenFilmstrip();
    }

    private void OnFilmstripCloseClicked(object sender, RoutedEventArgs e) => CloseFilmstrip();

    private void OpenFilmstrip()
    {
        if (IsFilmstripOpen || !_viewModel.CanBrowseFiles)
            return;

        _filmstripFiles = _viewModel.NavigationFiles;
        _filmstripCurrentIndex = _viewModel.NavigationIndex;
        _filmstripLifetime = new CancellationTokenSource();

        var repeater = new ItemsRepeater
        {
            Layout = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = CardSpacing,
            },
            ItemTemplate = new FilmstripCardFactory(),
            ItemsSource = _filmstripFiles,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        repeater.ElementPrepared += OnFilmstripElementPrepared;
        repeater.ElementClearing += OnFilmstripElementClearing;

        // 스크롤 호스트 가상화로 보이는 카드만 생성. 2만 파일도 화면 몫만 일함.
        var scroll = new ScrollViewer
        {
            Content = repeater,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
        AutomationProperties.SetName(scroll, AppStrings.FilmstripLabel);

        _filmstripRepeater = repeater;
        _filmstripScroll = scroll;
        FilmstripHost.Children.Add(scroll);
        FilmstripPanel.Visibility = Visibility.Visible;
        scroll.Loaded += OnFilmstripScrollLoaded;
        UpdateFilmstripToggleState();
    }

    private void CloseFilmstrip()
    {
        if (!IsFilmstripOpen)
            return;

        FilmstripPanel.Visibility = Visibility.Collapsed;
        _filmstripLifetime?.Cancel();
        _filmstripLifetime?.Dispose();
        _filmstripLifetime = null;

        if (_filmstripRepeater is { } repeater)
        {
            repeater.ElementPrepared -= OnFilmstripElementPrepared;
            repeater.ElementClearing -= OnFilmstripElementClearing;
            repeater.ItemsSource = null;
        }
        if (_filmstripScroll is { } scroll)
        {
            scroll.Loaded -= OnFilmstripScrollLoaded;
            scroll.Content = null;
        }

        FilmstripHost.Children.Clear();
        _filmstripRepeater = null;
        _filmstripScroll = null;
        _filmstripFiles = [];
        _filmstripCurrentIndex = -1;
        // 디코드 썸네일은 스트립이 보이는 동안만 생존.
        _filmstripThumbnails.Clear();
        UpdateFilmstripToggleState();
    }

    private void OnFilmstripScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;
        scroll.Loaded -= OnFilmstripScrollLoaded;
        ScrollFilmstripTo(_filmstripCurrentIndex, disableAnimation: true);
    }

    /// <summary>카드·이전/다음·방향키 어디서 이동해도 영향받은 카드 둘만 갱신.</summary>
    private void SyncFilmstripSelection()
    {
        if (!IsFilmstripOpen || _filmstripRepeater is null)
            return;

        // 폴더가 바뀌면 목록을 다시 게시하므로 묵은 색인 대신 재생성.
        if (!ReferenceEquals(_viewModel.NavigationFiles, _filmstripFiles))
        {
            CloseFilmstrip();
            OpenFilmstrip();
            return;
        }

        var index = _viewModel.NavigationIndex;
        if (index == _filmstripCurrentIndex)
            return;

        if (TryGetFilmstripCard(_filmstripCurrentIndex) is { } previous)
            previous.SetCurrent(false);
        _filmstripCurrentIndex = index;
        if (TryGetFilmstripCard(index) is { } current)
            current.SetCurrent(true);
        ScrollFilmstripTo(index, disableAnimation: false);
    }

    private FilmstripCard? TryGetFilmstripCard(int index)
    {
        if (_filmstripRepeater is null || index < 0)
            return null;
        return _filmstripRepeater.TryGetElement(index) as FilmstripCard;
    }

    private void ScrollFilmstripTo(int index, bool disableAnimation)
    {
        if (_filmstripScroll is not { } scroll || index < 0)
            return;
        // 활성 파일을 왼쪽 고정 대신 중앙 배치. 다음 후보는 보통 이웃.
        var target = ((index * (CardWidth + CardSpacing)) + (CardWidth / 2))
            - (scroll.ViewportWidth / 2);
        scroll.ChangeView(Math.Max(0, target), null, null, disableAnimation);
    }

    private void UpdateFilmstripToggleState()
    {
        var open = IsFilmstripOpen;
        var action = open ? AppStrings.FilmstripHide : AppStrings.FilmstripShow;
        SetTip(FilmstripCloseButton, AppStrings.FilmstripHide, AppStrings.FilmstripHide);
        ToolTipService.SetToolTip(StatusPositionButton, $"{action}\n{AppStrings.TipFilmstrip}");
    }

    private void OnFilmstripElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FilmstripCard card)
            return;

        var index = args.Index;
        if (index < 0 || index >= _filmstripFiles.Count)
            return;

        var path = _filmstripFiles[index];
        card.Bind(index, Path.GetFileName(path), index == _filmstripCurrentIndex);
        card.Click -= OnFilmstripCardClicked;
        card.Click += OnFilmstripCardClicked;
        card.KeyDown -= OnFilmstripCardKeyDown;
        card.KeyDown += OnFilmstripCardKeyDown;

        if (_filmstripThumbnails.TryGetValue(path, out var cached))
        {
            card.SetThumbnail(cached);
            return;
        }
        if (_filmstripLifetime is { } lifetime)
            _ = LoadFilmstripThumbnailAsync(card, path, lifetime.Token);
    }

    private void OnFilmstripElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FilmstripCard card)
            return;
        card.Click -= OnFilmstripCardClicked;
        card.KeyDown -= OnFilmstripCardKeyDown;
        card.Release();
    }

    private void OnFilmstripCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FilmstripCard { Index: >= 0 } card)
            return;
        _viewModel.OpenAt(card.Index);
    }

    private void OnFilmstripCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FilmstripCard { Index: >= 0 } card)
            return;

        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
            return;

        e.Handled = true;
        var targetIndex = e.Key == VirtualKey.Left
            ? card.Index - 1
            : card.Index + 1;
        if (targetIndex < 0 || targetIndex >= _filmstripFiles.Count)
            return;

        // 포커스와 본 이미지를 한 칸씩 동행. 썸네일만 산책 보내지 않음.
        var targetCard = _filmstripRepeater?.GetOrCreateElement(targetIndex) as FilmstripCard;
        _viewModel.OpenAt(targetIndex);
        targetCard?.Focus(FocusState.Keyboard);
    }

    /// <summary>직접 디코드 대신 Windows 셸 썸네일 캐시 사용.</summary>
    private async Task LoadFilmstripThumbnailAsync(
        FilmstripCard card,
        string path,
        CancellationToken cancellationToken)
    {
        var token = card.Token;
        try
        {
            await ThumbnailRequests.WaitAsync(cancellationToken);
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
                using var thumbnail = await file
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbnailPixelSize)
                    .AsTask(cancellationToken);
                if (thumbnail is null || thumbnail.Size == 0)
                    return;

                var bitmap = new BitmapImage { DecodePixelWidth = ThumbnailPixelSize };
                await bitmap.SetSourceAsync(thumbnail).AsTask(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;

                _filmstripThumbnails[path] = bitmap;
                // 불러오는 동안 카드가 다른 파일로 재사용됐을 수 있음.
                if (card.Token == token)
                    card.SetThumbnail(bitmap);
            }
            finally
            {
                ThumbnailRequests.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or FileNotFoundException
            or ArgumentException
            or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            // 없거나 못 읽는 파일은 자리표시 유지. 스트립은 로드 화면이 아님.
        }
    }

    private sealed class FilmstripCardFactory : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => new FilmstripCard();

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
        }
    }

    /// <summary>스트립 항목. 재결합마다 토큰을 올려 늦은 썸네일이 엉뚱한 파일을 칠하지 못하게 함.</summary>
    private sealed partial class FilmstripCard : Button
    {
        private static readonly SolidColorBrush NoHighlight =
            new(Microsoft.UI.Colors.Transparent);

        private readonly Image _image;
        private readonly TextBlock _caption;
        private readonly Border _frame;
        private string _fileName = "";
        private bool _isCurrent;

        public FilmstripCard()
        {
            Width = CardWidth;
            Height = CardHeight;
            Padding = new Thickness(2);
            BorderThickness = new Thickness(0);
            Background = NoHighlight;

            _image = new Image
            {
                Width = ThumbnailPixelSize,
                Height = 72,
                Stretch = Stretch.Uniform,
            };
            _frame = new Border
            {
                Width = ThumbnailPixelSize + 4,
                Height = 76,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(2),
                BorderBrush = NoHighlight,
                Child = _image,
            };
            _caption = new TextBlock
            {
                FontSize = 11,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = ThumbnailPixelSize,
            };
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { _frame, _caption },
            };
        }

        public int Index { get; private set; } = -1;
        public int Token { get; private set; }

        public void Bind(int index, string fileName, bool isCurrent)
        {
            Index = index;
            Token++;
            _fileName = fileName;
            _image.Source = null;
            _caption.Text = fileName;
            ToolTipService.SetToolTip(this, fileName);
            SetCurrent(isCurrent);
        }

        public void SetCurrent(bool isCurrent)
        {
            _isCurrent = isCurrent;
            _frame.BorderBrush = isCurrent
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : NoHighlight;
            _caption.FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            var name = $"{Index + 1}. {_fileName}";
            AutomationProperties.SetName(
                this, _isCurrent ? $"{name} ({AppStrings.FilmstripCurrent})" : name);
        }

        public void SetThumbnail(BitmapImage bitmap) => _image.Source = bitmap;

        public void Release()
        {
            Token++;
            Index = -1;
            _image.Source = null;
        }
    }
}
