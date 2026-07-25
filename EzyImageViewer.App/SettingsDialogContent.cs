using System.Globalization;
using EzyImageViewer.Core.Input;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EzyImageViewer.App;

/// <summary>
/// 페이지형 환경설정 허브. 일반·도구 모음·파일 연결·정보·업데이트·개발 지원 제공.
/// 동작 설정은 저장·취소 병합 계약, 파일 연결은 전용 버튼으로 즉시 적용.
/// </summary>
internal sealed class SettingsDialogContent : Grid
{
    private readonly AppSettings _initial;
    private readonly ComboBox _theme = new();
    private readonly ComboBox _singleInstance = new();
    private readonly ToggleSwitch _clipboardWatch = new();
    private readonly ToggleSwitch _recentFiles = new();
    private readonly ToggleSwitch _includeSubfolders = new();
    private readonly ToggleSwitch _toolbarOpenGroup = new();
    private readonly ToggleSwitch _toolbarSelectGroup = new();
    private readonly ToggleSwitch _toolbarTransformGroup = new();
    private readonly ToggleSwitch _toolbarCropGroup = new();
    private readonly ToggleSwitch _toolbarZoomGroup = new();
    private readonly ToggleSwitch _toolbarProtectGroup = new();
    private readonly Button _checkForUpdates = new();
    private readonly TextBlock _updateStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly Button _openUpdateRelease = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed,
    };
    private Uri? _updateReleasePage;
    private readonly CheckBox _control = new();
    private readonly CheckBox _alt = new();
    private readonly CheckBox _shift = new();
    private readonly CheckBox _windows = new();
    private readonly ComboBox _hotkey = new();
    private readonly TextBlock _validation = new()
    {
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.Colors.OrangeRed),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private readonly ListView _navigation = new();
    private readonly ContentPresenter _pageHost = new();
    private readonly List<UIElement> _pages = [];
    private readonly Dictionary<string, CheckBox> _extensionBoxes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _applyAssociations = new();
    private readonly TextBlock _associationStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.8,
    };
    private const int FileAssociationPageIndex = 2;
    private IReadOnlySet<string> _appliedExtensions = new HashSet<string>();
    private bool _associationPageVisited;
    private bool _associationsAvailable = true;

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    public SettingsDialogContent(AppSettings initial)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        Width = 680;
        Height = 460;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _pages.Add(BuildGeneralPage());
        _pages.Add(BuildToolbarGroupPage());
        _pages.Add(BuildFileAssociationPage());
        System.Diagnostics.Debug.Assert(
            _pages.Count - 1 == FileAssociationPageIndex,
            "FileAssociationPageIndex must track the navigation order.");
        _pages.Add(BuildAboutPage());
        _pages.Add(BuildUpdatePage());
        _pages.Add(BuildSupportPage());

        _navigation.ItemsSource = new[]
        {
            AppStrings.SettingsNavGeneral,
            AppStrings.SettingsNavToolbarGroups,
            AppStrings.SettingsNavFileAssoc,
            AppStrings.SettingsNavAbout,
            AppStrings.SettingsNavUpdate,
            AppStrings.SettingsNavSupport,
        };
        _navigation.SelectionMode = ListViewSelectionMode.Single;
        _navigation.SelectionChanged += (_, _) =>
        {
            if (_navigation.SelectedIndex is >= 0 and var index && index < _pages.Count)
            {
                _pageHost.Content = _pages[index];
                _associationPageVisited |= index == FileAssociationPageIndex;
            }
        };
        AutomationProperties.SetName(_navigation, AppStrings.SettingsTitle);
        SetColumn(_navigation, 0);
        Children.Add(_navigation);

        _pageHost.Margin = new Thickness(20, 0, 0, 0);
        SetColumn(_pageHost, 1);
        Children.Add(_pageHost);
        _navigation.SelectedIndex = 0;
    }

    public AppSettings InitialSettings => _initial;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler<Uri>? LinkRequested;

    public void SetUpdateCheckPending()
    {
        _checkForUpdates.IsEnabled = false;
        _updateStatus.Text = AppStrings.UpdateChecking;
        _updateStatus.Visibility = Visibility.Visible;
        _openUpdateRelease.Visibility = Visibility.Collapsed;
        _updateReleasePage = null;
    }

    public void SetUpdateCheckResult(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _checkForUpdates.IsEnabled = true;
        _updateStatus.Text = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => string.Format(
                CultureInfo.CurrentCulture,
                AppStrings.UpdateAvailableBody,
                result.CurrentVersion,
                result.LatestVersion),
            UpdateCheckStatus.Current => string.Format(
                CultureInfo.CurrentCulture,
                AppStrings.UpdateCurrent,
                result.CurrentVersion),
            _ => AppStrings.UpdateUnavailable,
        };
        _updateStatus.Visibility = Visibility.Visible;
        _updateReleasePage = result.Status == UpdateCheckStatus.UpdateAvailable
            ? result.ReleasePage
            : null;
        _openUpdateRelease.Visibility = _updateReleasePage is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public bool TryCreateSettings(out AppSettings settings)
    {
        var modifiers = HotkeyModifiers.None;
        if (_alt.IsChecked == true) modifiers |= HotkeyModifiers.Alt;
        if (_control.IsChecked == true) modifiers |= HotkeyModifiers.Control;
        if (_shift.IsChecked == true) modifiers |= HotkeyModifiers.Shift;
        if (_windows.IsChecked == true) modifiers |= HotkeyModifiers.Windows;
        if (modifiers == HotkeyModifiers.None
            || _hotkey.SelectedItem is not Choice<int> hotkey)
        {
            settings = _initial;
            ShowValidation(AppStrings.SettingsHotkeyInvalid);
            return false;
        }

        settings = _initial with
        {
            Theme = SelectedValue<AppTheme>(_theme),
            SingleInstanceBehavior = SelectedValue<SingleInstanceBehavior>(_singleInstance),
            ClipboardWatchEnabled = _clipboardWatch.IsOn,
            RecentFilesEnabled = _recentFiles.IsOn,
            IncludeSubfoldersInNavigation = _includeSubfolders.IsOn,
            ToolbarOpenGroupEnabled = _toolbarOpenGroup.IsOn,
            ToolbarSelectGroupEnabled = _toolbarSelectGroup.IsOn,
            ToolbarTransformGroupEnabled = _toolbarTransformGroup.IsOn,
            ToolbarCropGroupEnabled = _toolbarCropGroup.IsOn,
            ToolbarZoomGroupEnabled = _toolbarZoomGroup.IsOn,
            ToolbarProtectGroupEnabled = _toolbarProtectGroup.IsOn,
            CaptureHotkey = new CaptureHotkey
            {
                Modifiers = modifiers,
                VirtualKey = hotkey.Value,
            },
        };
        try
        {
            AppSettingsStore.Validate(settings);
            _validation.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (ArgumentException)
        {
            settings = _initial;
            ShowValidation(AppStrings.SettingsHotkeyInvalid);
            return false;
        }
    }

    private ScrollViewer BuildGeneralPage()
    {
        var language = new ComboBox
        {
            Header = AppStrings.SettingsLanguage,
            ItemsSource = new[] { AppStrings.SettingsLanguageKorean },
            SelectedIndex = 0,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(language, AppStrings.SettingsLanguage);

        ConfigureCombo(_theme, AppStrings.SettingsTheme, new[]
        {
            new Choice<AppTheme>(AppStrings.SettingsThemeSystem, AppTheme.System),
            new Choice<AppTheme>(AppStrings.SettingsThemeLight, AppTheme.Light),
            new Choice<AppTheme>(AppStrings.SettingsThemeDark, AppTheme.Dark),
        }, _initial.Theme);
        ConfigureCombo(_singleInstance, AppStrings.SettingsFileActivation, new[]
        {
            new Choice<SingleInstanceBehavior>(
                AppStrings.SettingsReuseWindow, SingleInstanceBehavior.ReuseExistingWindow),
            new Choice<SingleInstanceBehavior>(
                AppStrings.SettingsOpenNewWindow, SingleInstanceBehavior.OpenNewWindow),
        }, _initial.SingleInstanceBehavior);

        ConfigureToggle(_clipboardWatch, AppStrings.SettingsClipboardWatch,
            _initial.ClipboardWatchEnabled);
        ConfigureToggle(_recentFiles, AppStrings.SettingsRecentFiles,
            _initial.RecentFilesEnabled);
        ConfigureToggle(_includeSubfolders, AppStrings.SettingsIncludeSubfolders,
            _initial.IncludeSubfoldersInNavigation);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(language);
        panel.Children.Add(MutedText(AppStrings.SettingsLanguageNote, 0.6, 12));
        panel.Children.Add(MutedText(AppStrings.SettingsPrivacySummary, 0.75));
        panel.Children.Add(_theme);
        panel.Children.Add(_singleInstance);
        panel.Children.Add(_clipboardWatch);
        panel.Children.Add(_recentFiles);
        panel.Children.Add(_includeSubfolders);
        panel.Children.Add(BuildHotkeyEditor(_initial.CaptureHotkey));
        panel.Children.Add(_validation);
        return WrapPage(panel);
    }

    private ScrollViewer BuildToolbarGroupPage()
    {
        ConfigureToggle(_toolbarOpenGroup, AppStrings.SettingsToolbarGroupOpen,
            _initial.ToolbarOpenGroupEnabled);
        ConfigureToggle(_toolbarSelectGroup, AppStrings.SettingsToolbarGroupSelect,
            _initial.ToolbarSelectGroupEnabled);
        ConfigureToggle(_toolbarTransformGroup, AppStrings.SettingsToolbarGroupTransform,
            _initial.ToolbarTransformGroupEnabled);
        ConfigureToggle(_toolbarCropGroup, AppStrings.SettingsToolbarGroupCrop,
            _initial.ToolbarCropGroupEnabled);
        ConfigureToggle(_toolbarZoomGroup, AppStrings.SettingsToolbarGroupZoom,
            _initial.ToolbarZoomGroupEnabled);
        ConfigureToggle(_toolbarProtectGroup, AppStrings.SettingsToolbarGroupProtect,
            _initial.ToolbarProtectGroupEnabled);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(MutedText(AppStrings.SettingsToolbarGroups, 0.75));
        panel.Children.Add(_toolbarOpenGroup);
        panel.Children.Add(_toolbarSelectGroup);
        panel.Children.Add(_toolbarTransformGroup);
        panel.Children.Add(_toolbarCropGroup);
        panel.Children.Add(_toolbarZoomGroup);
        panel.Children.Add(_toolbarProtectGroup);
        return WrapPage(panel);
    }

    private UIElement BuildFileAssociationPage()
    {
        try
        {
            _appliedExtensions = FileAssociationRegistrar.ReadRegisteredExtensions();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            _associationsAvailable = false;
        }

        var page = new Grid { RowSpacing = 10 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var description = MutedText(AppStrings.FileAssocDescription, 0.75);
        SetRow(description, 0);
        page.Children.Add(description);

        var windowsSettings = new HyperlinkButton
        {
            Content = AppStrings.FileAssocWindowsSettings,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(windowsSettings, AppStrings.FileAssocWindowsSettings);
        windowsSettings.Click += (_, _) => LinkRequested?.Invoke(
            this, FileAssociationPolicy.GetDefaultAppsSettingsUri());
        SetRow(windowsSettings, 1);
        page.Children.Add(windowsSettings);

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var groups = new StackPanel { Spacing = 10, Padding = new Thickness(10) };
        foreach (var group in FileAssociationPolicy.Groups)
        {
            groups.Children.Add(new TextBlock
            {
                Text = GroupTitle(group.Key),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            groups.Children.Add(BuildExtensionGrid(group.Extensions));
        }
        var list = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Gray),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer
            {
                Content = groups,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
        SetColumn(list, 0);
        body.Children.Add(list);

        var actions = new StackPanel { Spacing = 8, MinWidth = 116 };
        actions.Children.Add(SelectionButton(
            AppStrings.FileAssocSelectEssential,
            extension => FileAssociationPolicy.EssentialExtensions.Contains(
                extension, StringComparer.OrdinalIgnoreCase)));
        actions.Children.Add(SelectionButton(AppStrings.FileAssocSelectAll, _ => true));
        actions.Children.Add(SelectionButton(AppStrings.FileAssocSelectNone, _ => false));
        SetColumn(actions, 1);
        body.Children.Add(actions);
        SetRow(body, 2);
        page.Children.Add(body);

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_associationStatus, 0);
        footer.Children.Add(_associationStatus);
        _applyAssociations.Content = AppStrings.FileAssocApply;
        AutomationProperties.SetName(_applyAssociations, AppStrings.FileAssocApply);
        _applyAssociations.Click += (_, _) => ApplyAssociations();
        SetColumn(_applyAssociations, 1);
        footer.Children.Add(_applyAssociations);
        SetRow(footer, 3);
        page.Children.Add(footer);

        if (!_associationsAvailable)
        {
            _associationStatus.Text = AppStrings.FileAssocUnavailable;
            foreach (var box in _extensionBoxes.Values)
                box.IsEnabled = false;
        }
        UpdateAssociationApplyState();
        return page;
    }

    private Grid BuildExtensionGrid(IReadOnlyList<string> extensions)
    {
        const int columns = 3;
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        for (var row = 0; row <= (extensions.Count - 1) / columns; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < extensions.Count; index++)
        {
            var extension = extensions[index];
            var box = new CheckBox
            {
                Content = extension.TrimStart('.').ToUpperInvariant(),
                IsChecked = _appliedExtensions.Contains(extension),
                MinWidth = 0,
            };
            AutomationProperties.SetName(box, extension);
            _extensionBoxes[extension] = box;
            SetColumn(box, index % columns);
            SetRow(box, index / columns);
            grid.Children.Add(box);
        }
        return grid;
    }

    private Button SelectionButton(string label, Func<string, bool> shouldCheck)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = _associationsAvailable,
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            foreach (var (extension, box) in _extensionBoxes)
                box.IsChecked = shouldCheck(extension);
        };
        return button;
    }

    /// <summary>
    /// 파일 연결 페이지를 연 경우에만 대화상자 저장 때 적용.
    /// 테마만 바꾼 사용자의 기본 앱을 슬쩍 가져오지 않게 함.
    /// </summary>
    public void ApplyPendingAssociations()
    {
        if (!_associationsAvailable || !_associationPageVisited)
            return;
        ApplyAssociations();
    }

    /// <summary>
    /// 선택 확장자를 연결 프로그램 후보로 등록하고 비패키지 빌드에서는 기본 앱 전환 시도.
    /// 확장자별 결과를 내며 OS가 완전히 막으면 Windows 기본 앱 페이지 안내.
    /// </summary>
    private void ApplyAssociations()
    {
        var desired = _extensionBoxes
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            FileAssociationRegistrar.Apply(desired);
            _appliedExtensions = desired;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _associationStatus.Text = $"{AppStrings.FileAssocApplyFailed}: {ex.Message}";
            UpdateAssociationApplyState();
            return;
        }

        if (desired.Count == 0)
        {
            _associationStatus.Text = AppStrings.FileAssocCleared;
            UpdateAssociationApplyState();
            return;
        }

#if EZY_UNPACKAGED
        var outcome = UserChoiceDefaultWriter.SetDefaults(desired);
        if (outcome.Blocked)
        {
            _associationStatus.Text = AppStrings.FileAssocSetDefaultUnsupported;
            LinkRequested?.Invoke(this, FileAssociationPolicy.GetDefaultAppsSettingsUri());
        }
        else if (outcome.AllSet)
        {
            _associationStatus.Text = AppStrings.FileAssocSetDefaultAll;
        }
        else
        {
            // 일부 실패는 페이지에 안내만 표시. 하나 놓칠 때마다 설정 창이 튀어나오지 않게 함.
            var message = string.Format(
                CultureInfo.CurrentCulture,
                AppStrings.FileAssocSetDefaultPartial,
                outcome.SetCount,
                outcome.Total);
            if (outcome.AnyRestoreFailed)
                message += " " + AppStrings.FileAssocSetDefaultRestoreFailed;
            _associationStatus.Text = message;
        }
#else
        _associationStatus.Text = AppStrings.FileAssocApplied;
#endif
        UpdateAssociationApplyState();
    }

    private void UpdateAssociationApplyState()
    {
        // 선택이 같아도 다시 적용 가능. 다른 앱이 가져간 기본값을 되찾는 길.
        _applyAssociations.IsEnabled = _associationsAvailable;
    }

    private static string GroupTitle(string key) => key switch
    {
        "raster" => AppStrings.FileAssocGroupRaster,
        "codec" => AppStrings.FileAssocGroupCodec,
        "vector" => AppStrings.FileAssocGroupVector,
        _ => key,
    };

    private ScrollViewer BuildAboutPage()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = FileAssociationPolicy.RegisteredApplicationName,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock { Text = FormattedVersion() });
        panel.Children.Add(MutedText(AppStrings.AboutDescription, 0.8));
        panel.Children.Add(MutedText(AppStrings.AboutLicense, 0.6, 12));
        panel.Children.Add(LinkButton(
            AppStrings.AboutProjectPage, ReleaseDistributionPolicy.ProjectPage));
        return WrapPage(panel);
    }

    private ScrollViewer BuildUpdatePage()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.SettingsApplicationInformation,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock { Text = FormattedVersion() });
        panel.Children.Add(MutedText(AppStrings.UpdatePolicyNote, 0.75));
        _checkForUpdates.Content = AppStrings.SettingsCheckForUpdates;
        _checkForUpdates.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_checkForUpdates, AppStrings.SettingsCheckForUpdates);
        _checkForUpdates.Click += (_, _) =>
            CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_checkForUpdates);
        panel.Children.Add(_updateStatus);
        _openUpdateRelease.Content = AppStrings.UpdateOpenRelease;
        AutomationProperties.SetName(_openUpdateRelease, AppStrings.UpdateOpenRelease);
        _openUpdateRelease.Click += (_, _) =>
        {
            if (_updateReleasePage is { } page)
                LinkRequested?.Invoke(this, page);
        };
        panel.Children.Add(_openUpdateRelease);
        return WrapPage(panel);
    }

    private ScrollViewer BuildSupportPage()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(MutedText(AppStrings.SupportNote, 0.9));
        var support = new Button
        {
            Content = AppStrings.SupportAction,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)
            && style is Style accent)
        {
            support.Style = accent;
        }
        AutomationProperties.SetName(support, AppStrings.SupportAction);
        support.Click += (_, _) => LinkRequested?.Invoke(
            this, ReleaseDistributionPolicy.SupportPage);
        panel.Children.Add(support);
        panel.Children.Add(MutedText(AppStrings.AboutLicense, 0.6, 12));
        panel.Children.Add(LinkButton(
            AppStrings.AboutProjectPage, ReleaseDistributionPolicy.ProjectPage));
        return WrapPage(panel);
    }

    private HyperlinkButton LinkButton(string label, Uri target)
    {
        var link = new HyperlinkButton { Content = label, Padding = new Thickness(0) };
        AutomationProperties.SetName(link, label);
        link.Click += (_, _) => LinkRequested?.Invoke(this, target);
        return link;
    }

    private static string FormattedVersion() => string.Format(
        CultureInfo.CurrentCulture,
        AppStrings.SettingsCurrentVersion,
        AppServices.ApplicationVersion);

    private static TextBlock MutedText(string text, double opacity, double? fontSize = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = opacity,
        };
        if (fontSize is double size)
            block.FontSize = size;
        return block;
    }

    private static ScrollViewer WrapPage(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(0, 0, 12, 0),
    };

    private StackPanel BuildHotkeyEditor(CaptureHotkey hotkey)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = AppStrings.SettingsCaptureHotkey });
        var modifiers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        ConfigureModifier(_control, "Ctrl", hotkey.Modifiers.HasFlag(HotkeyModifiers.Control));
        ConfigureModifier(_alt, "Alt", hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt));
        ConfigureModifier(_shift, "Shift", hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift));
        ConfigureModifier(_windows, "Win", hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows));
        modifiers.Children.Add(_control);
        modifiers.Children.Add(_alt);
        modifiers.Children.Add(_shift);
        modifiers.Children.Add(_windows);
        panel.Children.Add(modifiers);

        var keys = CaptureHotkeyPolicy.SupportedVirtualKeys
            .Select(key => new Choice<int>(
                CaptureHotkeyPolicy.GetVirtualKeyDisplayName(key),
                key))
            .ToList();
        _hotkey.ItemsSource = keys;
        _hotkey.SelectedItem = keys.FirstOrDefault(value => value.Value == hotkey.VirtualKey)
            ?? keys.First(value => value.Value == 0x45);
        _hotkey.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(_hotkey, AppStrings.SettingsCaptureHotkey);
        panel.Children.Add(_hotkey);
        return panel;
    }

    private static void ConfigureModifier(CheckBox box, string text, bool value)
    {
        box.Content = text;
        box.IsChecked = value;
    }

    private static void ConfigureToggle(ToggleSwitch toggle, string header, bool value)
    {
        toggle.Header = header;
        toggle.IsOn = value;
    }

    private static void ConfigureCombo<T>(
        ComboBox combo,
        string header,
        IReadOnlyList<Choice<T>> choices,
        T selected) where T : struct, Enum
    {
        combo.Header = header;
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.First(choice => EqualityComparer<T>.Default.Equals(
            choice.Value, selected));
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private static T SelectedValue<T>(ComboBox combo) where T : struct, Enum =>
        ((Choice<T>)combo.SelectedItem).Value;

    private void ShowValidation(string text)
    {
        _validation.Text = text;
        _validation.Visibility = Visibility.Visible;
        // 단축키 편집기는 첫 페이지에 있으니 검증 실패 때 그쪽을 보여 줌.
        _navigation.SelectedIndex = 0;
    }
}
