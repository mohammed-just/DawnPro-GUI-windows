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
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _closeApproved;
    private bool _gesture;
    private readonly HashSet<ValidationError> _inputErrors = [];

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
        model.AppearanceChanged += ApplyAppearance;
        model.FeedbackChanged += ShowFeedback;
        _feedbackTimer.Tick += (_, _) => { FeedbackToast.Visibility = Visibility.Collapsed; _feedbackTimer.Stop(); };
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        AddHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>((_, e) =>
        {
            if (e.Action == ValidationErrorEventAction.Added) _inputErrors.Add(e.Error); else _inputErrors.Remove(e.Error);
            model.HasInputErrors = _inputErrors.Count > 0;
        }));
        LostMouseCapture += (_, _) => { if (_gesture) { _gesture = false; model.EndEdit(); } };
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _reconnectTimer.Tick += async (_, _) => await model.TryReconnectAsync();
        if (!options.Demo && !options.Offline && !options.Benchmark && options.ScreenshotPath is null) _reconnectTimer.Start();
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
        var mode = width >= 1000 ? ShellLayoutMode.Wide : ResponsiveShell.Classify(width);
        CurrentLayoutMode = mode;
        NavigationColumn.Width = new GridLength(width >= 1000 ? 174 : 64);
        DeviceLogo.Width = width >= 1000 ? 96 : 44;
        DeviceLogo.Height = width >= 1000 ? 64 : 38;

        if (mode == ShellLayoutMode.Narrow)
        {
            EditorColumn.Width = new GridLength(0);
            Grid.SetColumn(SelectedBandEditor, 0);
            Grid.SetRow(SelectedBandEditor, 1);
            SelectedBandEditor.Margin = new Thickness(0, 8, 8, 0);
            GraphCard.Height = 222;
            SettingsGroups.Columns = 1;
        }
        else
        {
            EditorColumn.Width = new GridLength(260);
            Grid.SetColumn(SelectedBandEditor, 1);
            Grid.SetRow(SelectedBandEditor, 0);
            SelectedBandEditor.Margin = new Thickness(0);
            GraphCard.Height = Math.Clamp((ActualHeight > 0 ? ActualHeight : Height) - 582, 180, 310);
            SettingsGroups.Columns = 2;
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

    private void ShowFeedback()
    {
        if (DataContext is not MainViewModel model) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowFeedback); return; }
        FeedbackText.Text = model.Status;
        FeedbackToast.Visibility = Visibility.Visible;
        _feedbackTimer.Stop(); _feedbackTimer.Start();
    }

    private void ApplyAppearance()
    {
        if (DataContext is not MainViewModel model) return;
        WpfTheme.Apply(model.ThemeSelection, Application.Current, this);
        WpfTheme.ApplyAccent(model.SystemAccent, Application.Current);
        EqGraphControl.RefreshThemeResources();
    }
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.WindowGlassColor) or nameof(SystemParameters.WindowGlassBrush)) Dispatcher.BeginInvoke(ApplyAppearance);
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.Z) { model.Undo(); e.Handled = true; }
        if (e.Key == Key.Y) { model.Redo(); e.Handled = true; }
    }
    private void StartEditGesture(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: false } model && !_gesture) { model.BeginEdit(); _gesture = true; }
    }
    private void EndEditGesture(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel model && _gesture) { _gesture = false; model.EndEdit(); }
    }
    private void SelectPreset(object sender, ICommand command)
    {
        if (sender is FrameworkElement { DataContext: Moondrop.Core.Eq.LocalPreset preset } && DataContext is MainViewModel model)
        { model.SelectedPreset = preset; if (command.CanExecute(null)) command.Execute(null); }
    }
    private void PresetOpenClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.OpenPresetCommand); }
    private void PresetApplyClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.ApplyPresetCommand); }
    private void SaveHelpClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) MessageBox.Show(this, m.SaveToDeviceExplanation, "Save to device", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void ActiveEqHelpClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) MessageBox.Show(this, m.ActiveEqExplanation, m.ActiveEqLabel, MessageBoxButton.OK, MessageBoxImage.Information); }
    private void PresetRenameClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.RenamePresetCommand); }
    private void PresetDuplicateClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.DuplicatePresetCommand); }
    private void PresetExportClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.ExportPresetCommand); }
    private void PresetDeleteClick(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel m) SelectPreset(sender, m.DeletePresetCommand); }

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
        if (!_closeApproved && DataContext is MainViewModel model)
        {
            if (!model.PrepareClose()) { e.Cancel = true; return; }
            _closeApproved = true; _reconnectTimer.Stop();
        }
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
        {
            model.Dialog.PropertyChanged -= DialogPropertyChanged;
            model.AppearanceChanged -= ApplyAppearance;
            model.FeedbackChanged -= ShowFeedback;
        }
        _reconnectTimer.Stop(); _feedbackTimer.Stop();
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
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
    private static ResourceDictionary? _highContrastTokens;
    public static void ApplyAccent(bool system, Application? application)
    {
        if (application is null) return;
        var color = SystemParameters.HighContrast ? SystemColors.HighlightColor : system ? SystemParameters.WindowGlassColor : System.Windows.Media.Color.FromRgb(0x35, 0xB8, 0xEF);
        var brush = new System.Windows.Media.SolidColorBrush(color);
        application.Resources["AccentFillColorDefaultBrush"] = brush;
        application.Resources["SystemAccentColorPrimaryBrush"] = brush;
        var luminance = .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;
        application.Resources["AccentTextBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : new System.Windows.Media.SolidColorBrush(luminance > .35 ? System.Windows.Media.Color.FromRgb(0x10, 0x23, 0x30) : System.Windows.Media.Colors.White);
        application.Resources["AccentSurfaceBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B));
    }
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
        if (_highContrastTokens is not null) { merged.Remove(_highContrastTokens); _highContrastTokens = null; }
        var themeDictionaries = merged
            .Where(dictionary => dictionary.Source is not null &&
                ThemeSourceNames.Any(name =>
                    dictionary.Source.OriginalString.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var dictionary in themeDictionaries)
            merged.Remove(dictionary);
        var resource = resolved == ThemeMode.Dark ? DarkThemePackUri : LightThemePackUri;
        merged.Add(new ResourceDictionary { Source = new Uri(resource, UriKind.Absolute) });
        if (SystemParameters.HighContrast)
        {
            _highContrastTokens = new ResourceDictionary();
            foreach (var key in new[] { "ApplicationBackgroundBrush", "LayerFillColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlSolidFillColorDefaultBrush", "ButtonBackgroundBrush", "ButtonHoverBackgroundBrush", "ButtonPressedBackgroundBrush", "DisabledButtonBackgroundBrush" }) _highContrastTokens[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "ButtonForegroundBrush", "CardStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush", "ButtonBorderBrush", "ButtonFocusBorderBrush" }) _highContrastTokens[key] = SystemColors.WindowTextBrush;
            _highContrastTokens["DisabledButtonForegroundBrush"] = SystemColors.GrayTextBrush;
            merged.Add(_highContrastTokens);
        }
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
