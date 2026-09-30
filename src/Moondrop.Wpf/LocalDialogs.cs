using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Moondrop.Core.Eq;

namespace Moondrop.Wpf;

internal static class LocalDialogs
{
    public static string? AskName(string title, string initial)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Preset name", Margin = new Thickness(0, 0, 0, 8) });
        var name = new TextBox { Text = initial, MaxLength = 100 }; AutomationProperties.SetName(name, "Preset name"); panel.Children.Add(name);
        var window = Create(title, panel, 420, 190);
        AddActions(panel, window, "Save locally", () => !string.IsNullOrWhiteSpace(name.Text));
        window.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return window.ShowDialog() == true ? name.Text : null;
    }
    public static (string Name, bool Open)? PreviewImport(LocalPreset preset)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Import preview · local storage only", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        var text = preset.ConversionNotes + $"\nPreamp {preset.Eq.PreGain:0.####} dB → device {EqConfiguration.Encoded(preset.Eq.PreGain):0.####} dB\n";
        foreach (var b in preset.Eq.Bands) text += $"{b.Index + 1}  {(b.Enabled ? "On" : "Off")}  {b.FilterType}  {b.Frequency} Hz  {b.Gain:0.####} dB  Q {b.Q:0.####}\n     Encoding: gain {EqConfiguration.Encoded(b.Gain):0.####}, Q {EqConfiguration.Encoded(b.Q):0.####}\n";
        text += $"\nUnused positions are disabled. Rounded source frequency uses whole Hz. Gain/Q encoding is 1/256.\nGlobal gain in native file: {preset.Eq.GlobalGain:0.####} dB. Opening preserves current device global gain.\nSupported APO subset: PK, LS, HS, LP, HP; native JSON preserves metadata.";
        panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Height = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "Local preset name", Margin = new Thickness(0, 12, 0, 6) });
        var name = new TextBox { Text = preset.Name, MaxLength = 100 }; AutomationProperties.SetName(name, "Imported preset name"); panel.Children.Add(name);
        var open = new CheckBox { Content = "Open in editor after saving (no hardware write)", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(open);
        var window = Create("Import preset", panel, 580, 640);
        AddActions(panel, window, "Save locally", () => !string.IsNullOrWhiteSpace(name.Text));
        return window.ShowDialog() == true ? (name.Text, open.IsChecked == true) : null;
    }
    private static Window Create(string title, UIElement content, int width, int height)
    {
        var window = new Window { Title = title, Content = content, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) { window.Owner = owner; WpfTheme.Apply((owner.DataContext as MainViewModel)?.ThemeSelection ?? "System", null, window); }
        return window;
    }
    private static void AddActions(Panel panel, Window window, string label, Func<bool> valid)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = label, IsDefault = true }; accept.Click += (_, _) => { if (valid()) window.DialogResult = true; };
        row.Children.Add(cancel); row.Children.Add(accept); panel.Children.Add(row);
    }
}

public sealed class NumericRule : ValidationRule
{
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public bool Integer { get; set; }
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        return double.TryParse(value?.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, cultureInfo, out var number) && double.IsFinite(number) && number >= Minimum && number <= Maximum && (!Integer || number == Math.Truncate(number))
            ? ValidationResult.ValidResult : new ValidationResult(false, $"Enter {(Integer ? "a whole number" : "a number")} from {Minimum} to {Maximum}.");
    }
}
