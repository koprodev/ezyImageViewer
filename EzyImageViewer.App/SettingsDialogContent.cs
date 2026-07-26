using System.Globalization;
using EzyImageViewer.Core.Input;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EzyImageViewer.App;

/// <summary>
/// 페이지형 환경설정 허브. 일반·도구 모음·파일 연결·정보·업데이트·개발 지원 제공.
/// 동작 설정은 저장·취소 병합 계약, 파일 연결은 Store 패키지 등록 현황을 안내.
/// </summary>
internal sealed class SettingsDialogContent : Grid
{
    private readonly AppSettings _initial;
    private readonly ComboBox _language = new();
    private readonly TextBlock _languageRestartNote = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        Visibility = Visibility.Collapsed,
    };
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

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    public SettingsDialogContent(AppSettings initial)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        // 크기는 가장 긴 언어(독일어·러시아어)와 가장 높은 글자(데바나가리)를 기준으로 잡았다.
        Width = 720;
        Height = 500;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _pages.Add(BuildGeneralPage());
        _pages.Add(BuildToolbarGroupPage());
        _pages.Add(BuildFileAssociationPage());
        _pages.Add(BuildAboutPage());
        _pages.Add(BuildUpdatePage());
        _pages.Add(BuildSupportPage());

        // 문자열을 그대로 넣으면 독일어·러시아어 항목이 열 너비에서 말없이 잘린다.
        // 줄바꿈되는 TextBlock으로 감싸 어떤 언어가 와도 글자가 사라지지 않게 한다.
        _navigation.ItemsSource = new[]
        {
            AppStrings.SettingsNavGeneral,
            AppStrings.SettingsNavToolbarGroups,
            AppStrings.SettingsNavFileAssoc,
            AppStrings.SettingsNavAbout,
            AppStrings.SettingsNavUpdate,
            AppStrings.SettingsNavSupport,
        }.Select(NavigationLabel).ToArray();
        _navigation.SelectionMode = ListViewSelectionMode.Single;
        _navigation.SelectionChanged += (_, _) =>
        {
            if (_navigation.SelectedIndex is >= 0 and var index && index < _pages.Count)
                _pageHost.Content = _pages[index];
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
    public event EventHandler<Uri>? LinkRequested;

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
            Language = SelectedValue<string>(_language),
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
        // 표시명은 각 언어 원어민이 읽을 이름이라 번역하지 않는다. "시스템 기본"만 현재 UI 언어를 따른다.
        var languageChoices = new List<Choice<string>>
        {
            new(AppStrings.SettingsLanguageSystem, LanguagePolicy.SystemDefault),
        };
        languageChoices.AddRange(LanguagePolicy.Supported.Select(
            supported => new Choice<string>(supported.NativeName, supported.Tag)));
        ConfigureCombo(_language, AppStrings.SettingsLanguage, languageChoices, _initial.Language);
        _language.SelectionChanged += (_, _) => _languageRestartNote.Visibility =
            string.Equals(SelectedValue<string>(_language), _initial.Language, StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;
        _languageRestartNote.Text = AppStrings.SettingsLanguageRestartNote;

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
        panel.Children.Add(_language);
        panel.Children.Add(_languageRestartNote);
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
        return BuildPackagedAssociationInfo();
    }

    /// <summary>
    /// Store 패키지용 읽기 전용 안내. 연결의 주인은 매니페스트이고 앱이 낄 자리는 없다.
    /// 체크박스를 주면 매니페스트에 없는 형식까지 켤 수 있어 거짓 성공이 되므로 현황만 보여 준다.
    /// </summary>
    private UIElement BuildPackagedAssociationInfo()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(MutedText(AppStrings.FileAssocPackagedNote, 0.75));
        panel.Children.Add(LinkButton(
            AppStrings.FileAssocWindowsSettings,
            FileAssociationPolicy.GetDefaultAppsSettingsUri()));
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.FileAssocPackagedRegistered,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // 매니페스트 SupportedFileTypes와 같은 목록. 어긋나면 계약 테스트가 잡는다.
        var extensions = new TextBlock
        {
            Text = string.Join(
                "   ",
                FileAssociationPolicy.EssentialExtensions.Select(
                    extension => extension.TrimStart('.').ToUpperInvariant())),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            extensions, string.Join(", ", FileAssociationPolicy.EssentialExtensions));
        panel.Children.Add(extensions);
        return WrapPage(panel);
    }

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
        panel.Children.Add(MutedText(AppStrings.UpdateStoreManagedNote, 0.75));
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
            this, ExternalLinkPolicy.SupportPage);
        panel.Children.Add(support);
        panel.Children.Add(MutedText(AppStrings.AboutLicense, 0.6, 12));
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

    /// <summary>나비게이션 항목. 접근성 이름은 TextBlock의 Text가 그대로 노출한다.</summary>
    private static TextBlock NavigationLabel(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    };

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
        // WinUI 기본 켬/끔 문자열은 MRT 재정의를 따르지 않고 OS 표시 언어로 굳는다.
        // 러시아어 화면에 한국어 "켬"이 남는 걸 실제로 봤다. 우리 리소스로 못 박는다.
        toggle.OnContent = AppStrings.ToggleOn;
        toggle.OffContent = AppStrings.ToggleOff;
    }

    private static void ConfigureCombo<T>(
        ComboBox combo,
        string header,
        IReadOnlyList<Choice<T>> choices,
        T selected)
    {
        combo.Header = header;
        combo.ItemsSource = choices;
        // 저장된 값이 목록에 없으면 첫 항목으로 떨어뜨린다. 대화상자가 통째로 죽는 것보단 낫다.
        combo.SelectedItem = choices.FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(
            choice.Value, selected)) ?? choices[0];
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(combo, header);
    }

    private static T SelectedValue<T>(ComboBox combo) =>
        ((Choice<T>)combo.SelectedItem).Value;

    private void ShowValidation(string text)
    {
        _validation.Text = text;
        _validation.Visibility = Visibility.Visible;
        // 단축키 편집기는 첫 페이지에 있으니 검증 실패 때 그쪽을 보여 줌.
        _navigation.SelectedIndex = 0;
    }
}
