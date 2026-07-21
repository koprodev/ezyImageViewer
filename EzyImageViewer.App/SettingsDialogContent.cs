using System.Globalization;
using EzyImageViewer.Core.Input;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EzyImageViewer.App;

/// <summary>Typed editor for the M9-A settings that have live product behavior.</summary>
internal sealed class SettingsDialogContent : StackPanel
{
    private readonly AppSettings _initial;
    private readonly ComboBox _theme = new();
    private readonly ComboBox _singleInstance = new();
    private readonly ToggleSwitch _clipboardWatch = new();
    private readonly ToggleSwitch _recentFiles = new();
    private readonly ToggleSwitch _includeSubfolders = new();
    private readonly Button _checkForUpdates = new();
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

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    public SettingsDialogContent(AppSettings initial)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        Spacing = 12;
        MaxWidth = 520;
        Children.Add(new TextBlock
        {
            Text = AppStrings.SettingsPrivacySummary,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        ConfigureCombo(_theme, AppStrings.SettingsTheme, new[]
        {
            new Choice<AppTheme>(AppStrings.SettingsThemeSystem, AppTheme.System),
            new Choice<AppTheme>(AppStrings.SettingsThemeLight, AppTheme.Light),
            new Choice<AppTheme>(AppStrings.SettingsThemeDark, AppTheme.Dark),
        }, initial.Theme);
        ConfigureCombo(_singleInstance, AppStrings.SettingsFileActivation, new[]
        {
            new Choice<SingleInstanceBehavior>(
                AppStrings.SettingsReuseWindow, SingleInstanceBehavior.ReuseExistingWindow),
            new Choice<SingleInstanceBehavior>(
                AppStrings.SettingsOpenNewWindow, SingleInstanceBehavior.OpenNewWindow),
        }, initial.SingleInstanceBehavior);

        ConfigureToggle(_clipboardWatch, AppStrings.SettingsClipboardWatch,
            initial.ClipboardWatchEnabled);
        ConfigureToggle(_recentFiles, AppStrings.SettingsRecentFiles,
            initial.RecentFilesEnabled);
        ConfigureToggle(_includeSubfolders, AppStrings.SettingsIncludeSubfolders,
            initial.IncludeSubfoldersInNavigation);

        Children.Add(_theme);
        Children.Add(_singleInstance);
        Children.Add(_clipboardWatch);
        Children.Add(_recentFiles);
        Children.Add(_includeSubfolders);
        Children.Add(BuildHotkeyEditor(initial.CaptureHotkey));
        Children.Add(BuildApplicationInformation());
        Children.Add(_validation);
    }

    public AppSettings InitialSettings => _initial;
    public event EventHandler? CheckForUpdatesRequested;

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

    private StackPanel BuildApplicationInformation()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.SettingsApplicationInformation,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(
                CultureInfo.CurrentCulture,
                AppStrings.SettingsCurrentVersion,
                AppServices.ApplicationVersion),
        });
        _checkForUpdates.Content = AppStrings.SettingsCheckForUpdates;
        _checkForUpdates.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_checkForUpdates, AppStrings.SettingsCheckForUpdates);
        _checkForUpdates.Click += (_, _) =>
            CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_checkForUpdates);
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
    }
}
