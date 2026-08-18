using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Moondrop.Wpf;

public sealed class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(string diagnosticsText)
    {
        Title = "Diagnostics";
        Width = 780;
        Height = 540;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        Owner = Application.Current?.MainWindow;
        SourceInitialized += (_, _) => DwmBackdrop.TryApply(this);

        var title = new TextBlock
        {
            Text = "Diagnostics",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var subtitle = new TextBlock
        {
            Text = "Read-only connection and transport information.",
            Margin = new Thickness(0, 2, 0, 14)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var text = new TextBox
        {
            Text = diagnosticsText,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1),
            Child = text
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel();
        header.Children.Add(title);
        header.Children.Add(subtitle);
        grid.Children.Add(header);
        Grid.SetRow(card, 1);
        grid.Children.Add(card);
        Content = grid;
    }
}

public sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(string message)
    {
        Title = "Moondrop device not found";
        Width = 520;
        Height = 270;
        MinWidth = 420;
        MinHeight = 230;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SourceInitialized += (_, _) => DwmBackdrop.TryApply(this);

        var heading = new TextBlock
        {
            Text = "No supported device was found",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        };
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 18)
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var close = new Button
        {
            Content = "Close",
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => Close();

        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(close);
        var card = new Border
        {
            Margin = new Thickness(18),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = stack
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = card;
    }
}
