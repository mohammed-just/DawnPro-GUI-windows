using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Moondrop.Wpf;

public partial class MainWindow : Window
{
    private IInputElement? _focusBeforeDialog;
    private readonly AsyncCloseCoordinator _closeCoordinator;

    public static readonly DependencyProperty CurrentLayoutModeProperty = DependencyProperty.Register(
        nameof(CurrentLayoutMode),
        typeof(ShellLayoutMode),
        typeof(MainWindow),
        new PropertyMetadata(ShellLayoutMode.Wide));

    public MainWindow(MainViewModel model, LaunchOptions options)
    {
        InitializeComponent();
        DataContext = model;
        Width = options.Width;
        Height = options.Height;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => UpdateResponsiveLayout();
        SourceInitialized += (_, _) =>
        {
            if (!DwmBackdrop.TryApply(this))
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        };
        if (options.Benchmark)
            ShowInTaskbar = false;
        model.Dialog.PropertyChanged += DialogPropertyChanged;
        _closeCoordinator = new AsyncCloseCoordinator(
            DisposeDataContextAsync,
            () => Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                    Close();
            }, DispatcherPriority.Send),
            ex => Trace.TraceError("Shutdown cleanup failed: {0}", ex));
    }

    public ShellLayoutMode CurrentLayoutMode
    {
        get => (ShellLayoutMode)GetValue(CurrentLayoutModeProperty);
        private set => SetValue(CurrentLayoutModeProperty, value);
    }

    private void UpdateResponsiveLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var mode = ResponsiveShell.Classify(width);
        CurrentLayoutMode = mode;
        NavigationColumn.Width = new GridLength(mode == ShellLayoutMode.Wide ? 176 : 64);

        if (mode == ShellLayoutMode.Narrow)
        {
            DeviceColumn.Width = new GridLength(0);
            Grid.SetColumn(DevicePanel, 0);
            Grid.SetRow(DevicePanel, 1);
            DevicePanel.Margin = new Thickness(0, 16, 0, 0);
            GraphCard.Height = 320;
        }
        else
        {
            DeviceColumn.Width = new GridLength(mode == ShellLayoutMode.Wide ? 280 : 248);
            Grid.SetColumn(DevicePanel, 1);
            Grid.SetRow(DevicePanel, 0);
            DevicePanel.Margin = new Thickness(16, 0, 0, 0);
            GraphCard.Height = mode == ShellLayoutMode.Wide ? 356 : 330;
        }
    }

    private void BandCardSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index } && DataContext is MainViewModel model)
            model.SelectedBandIndex = index;
    }

    private void OpenDevicePage(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
            model.SelectedPage = ShellPage.Device;
    }

    private void ThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Application.Current is null || DataContext is not MainViewModel model)
            return;
        WpfTheme.Apply(model.ThemeSelection, Application.Current, this);
        EqGraphControl.RefreshThemeResources();
    }

    private void DialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogState.IsOpen) || DataContext is not MainViewModel model)
            return;

        if (model.Dialog.IsOpen)
        {
            _focusBeforeDialog = Keyboard.FocusedElement;
            ShellGrid.IsEnabled = false;
            StatusBanner.IsEnabled = false;
            Dispatcher.BeginInvoke(() =>
            {
                DialogPrimaryButton.Focus();
                UIElementAutomationPeer.CreatePeerForElement(ConfirmationDialog)
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }, DispatcherPriority.Loaded);
            return;
        }

        ShellGrid.IsEnabled = true;
        StatusBanner.IsEnabled = true;
        var restore = _focusBeforeDialog;
        _focusBeforeDialog = null;
        Dispatcher.BeginInvoke(() => restore?.Focus(), DispatcherPriority.Loaded);
    }

    private void ConfirmationOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel model)
            return;
        model.Dialog.Cancel();
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeCoordinator.ShouldDeferClose())
        {
            e.Cancel = true;
            ShellGrid.IsEnabled = false;
            StatusBanner.IsEnabled = false;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel model)
            model.Dialog.PropertyChanged -= DialogPropertyChanged;
        base.OnClosed(e);
    }

    private ValueTask DisposeDataContextAsync()
    {
        if (DataContext is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}

#pragma warning disable WPF0001 // ThemeMode is the official WPF Fluent theme API.
internal static class WpfTheme
{
    public static void Apply(string theme, Application? application, Window? window)
    {
        var mode = theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeMode.Light
            : theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.Dark
                : ThemeMode.System;
        if (application is not null)
            application.ThemeMode = mode;
        if (window is not null)
            window.ThemeMode = mode;
        ApplyThemeTokens(application, mode);
    }

    private static void ApplyThemeTokens(Application? application, ThemeMode mode)
    {
        if (application is null)
            return;
        var resolved = mode == ThemeMode.System ? (IsSystemUsingLightTheme() ? ThemeMode.Light : ThemeMode.Dark) : mode;
        var merged = application.Resources.MergedDictionaries;
        var themeDictionaries = merged
            .Where(dictionary => dictionary.Source is not null &&
                ThemeSourceNames.Any(name =>
                    dictionary.Source.OriginalString.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var dictionary in themeDictionaries)
            merged.Remove(dictionary);
        var resource = resolved == ThemeMode.Dark ? DarkThemePackUri : LightThemePackUri;
        merged.Add(new ResourceDictionary { Source = new Uri(resource, UriKind.Absolute) });
    }

    private static bool IsSystemUsingLightTheme()
    {
        try
        {
            using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return true;
        }
    }

    private const string LightThemePackUri = "pack://application:,,,/Moondrop.Wpf;component/Themes/Light.xaml";
    private const string DarkThemePackUri = "pack://application:,,,/Moondrop.Wpf;component/Themes/Dark.xaml";
    private static readonly string[] ThemeSourceNames = ["Light.xaml", "Dark.xaml"];
}
#pragma warning restore WPF0001

internal static class DwmBackdrop
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    public static bool TryApply(Window window)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return false;
            var handle = new WindowInteropHelper(window).Handle;
            var rounded = 2;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                var mica = 2;
                return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int)) == 0;
            }
            return false;
        }
        catch
        {
            // DWM styling is cosmetic and must not block startup on unsupported builds.
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
