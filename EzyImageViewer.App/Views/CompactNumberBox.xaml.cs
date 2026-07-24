using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace EzyImageViewer.App.Views;

/// <summary>컨텍스트 막대용 작은 숫자 입력칸.
/// 늘 보이는 미니 화살표와 지우기(X) 버튼 없는 구성. Enter·포커스 이탈은 확정, 화살표·위아래 키는 즉시 적용.</summary>
internal sealed partial class CompactNumberBox : UserControl
{
    public event TypedEventHandler<CompactNumberBox, double>? ValueChanged;

    private double _value;

    public CompactNumberBox()
    {
        InitializeComponent();
        UpdateText();
    }

    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    public double SmallChange { get; set; } = 1d;

        // NumberBox처럼 코드에서 값이 바뀌어도 ValueChanged 발생.
    public double Value
    {
        get => _value;
        set => Apply(value);
    }

    private void Apply(double candidate)
    {
        if (!double.IsFinite(candidate))
        {
            UpdateText();
            return;
        }
        var next = Math.Clamp(candidate, Minimum, Maximum);
        var changed = next != _value;
        _value = next;
        UpdateText();
        if (changed)
            ValueChanged?.Invoke(this, next);
    }

    private void UpdateText() =>
        ValueText.Text = _value.ToString("0.###", CultureInfo.CurrentCulture);

    private void CommitText()
    {
        if (double.TryParse(
            ValueText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
            Apply(typed);
        else
            UpdateText();
    }

    private void OnUpClicked(object sender, RoutedEventArgs e) => Apply(_value + SmallChange);

    private void OnDownClicked(object sender, RoutedEventArgs e) => Apply(_value - SmallChange);

    private void OnTextGotFocus(object sender, RoutedEventArgs e) => ValueText.SelectAll();

    private void OnTextLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitText();
                ValueText.SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                Apply(_value + SmallChange);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                Apply(_value - SmallChange);
                e.Handled = true;
                break;
        }
    }
}
