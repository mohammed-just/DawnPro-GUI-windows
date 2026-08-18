using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Hardware;
using Moondrop.Wpf;

namespace Moondrop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WpfRuntimeTests
{
    [TestMethod]
    public void ButtonThemeTokensResolveLegiblyForBothLightAndDarkThemes()
    {
        WpfTestHost.Run(() =>
        {
            try
            {
                WpfTheme.Apply("Light", Application.Current, null);
                var lightBg = TokenColor("ButtonBackgroundBrush");
                var lightFg = TokenColor("ButtonForegroundBrush");
                var lightDisabledFg = TokenColor("DisabledButtonForegroundBrush");
                Assert.IsLessThan(0.5, Luminance(lightFg), $"light foreground must be dark; got {lightFg}");
                Assert.IsGreaterThan(0.8, Luminance(lightBg), $"light background must be light; got {lightBg}");
                Assert.AreNotEqual(lightFg, lightDisabledFg, "light enabled vs disabled foreground must differ");

                WpfTheme.Apply("Dark", Application.Current, null);
                var darkBg = TokenColor("ButtonBackgroundBrush");
                var darkFg = TokenColor("ButtonForegroundBrush");
                var darkDisabledFg = TokenColor("DisabledButtonForegroundBrush");
                var darkDisabledBg = TokenColor("DisabledButtonBackgroundBrush");
                Assert.IsGreaterThan(0.6, Luminance(darkFg), $"dark foreground must be light; got {darkFg}");
                Assert.IsLessThan(0.45, Luminance(darkBg), $"dark background must be dark; got {darkBg}");
                Assert.IsLessThanOrEqualTo(0.75, Luminance(darkBg), "dark mode must never use a near-white button background");
                Assert.AreNotEqual(darkFg, darkDisabledFg, "dark enabled vs disabled foreground must differ");
                Assert.AreNotEqual(darkBg, darkDisabledBg, "dark enabled vs disabled background must differ");

                var implicitStyle = Application.Current.TryFindResource(typeof(System.Windows.Controls.Button)) as System.Windows.Style;
                Assert.IsNotNull(implicitStyle, "a shared implicit Button style must exist");
                Assert.IsNotNull(
                    implicitStyle!.Setters.OfType<System.Windows.Setter>()
                        .FirstOrDefault(setter => setter.Property == System.Windows.Controls.Control.TemplateProperty),
                    "the shared Button style must define an explicit template so it never falls back to a default browser look");

                foreach (var stateToken in new[] { "ButtonHoverBackgroundBrush", "ButtonPressedBackgroundBrush", "ButtonFocusBorderBrush" })
                    Assert.IsNotNull(Application.Current.Resources[stateToken], $"missing interaction token {stateToken}");
            }
            finally
            {
                WpfTheme.Apply("Light", Application.Current, null);
            }
        });
    }

    private static System.Windows.Media.Color TokenColor(string key)
    {
        var value = Application.Current.Resources[key] as System.Windows.Media.SolidColorBrush
                    ?? throw new InvalidDataException($"missing theme token {key}");
        return value.Color;
    }

    private static double Luminance(System.Windows.Media.Color color) =>
        0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB;

    [TestMethod]
    public void EqEditorsExposeMeaningfulRuntimeAutomationNamesAndVisibleBandFocus()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            try
            {
                window.Show();
                window.UpdateLayout();

                var controls = Descendants<Control>(window).ToArray();
                var activeEq = controls.OfType<ComboBox>()
                    .Single(x => ReferenceEquals(x.ItemsSource, MainViewModel.EqIndexes));
                var firstBandControls = controls
                    .Where(x => x.DataContext is BandViewModel { Index: 0 })
                    .ToArray();
                var bandSelector = firstBandControls.OfType<Button>().Single(x => Equals(x.Tag, 0));
                var applyBand = firstBandControls.OfType<Button>().Single(x => Equals(x.Content, "Apply band"));
                var enabled = firstBandControls.OfType<CheckBox>().Single();
                var filter = firstBandControls.OfType<ComboBox>().Single();
                var editors = firstBandControls.OfType<TextBox>().ToArray();

                Assert.AreEqual("Active EQ", AutomationName(activeEq));
                Assert.AreEqual("Select band 1", AutomationName(bandSelector));
                Assert.AreEqual("Apply band 1", AutomationName(applyBand));
                Assert.AreEqual("Band 1 enabled", AutomationName(enabled));
                Assert.AreEqual("Band 1 filter", AutomationName(filter));
                CollectionAssert.AreEqual(
                    new[] { "Band 1 frequency in hertz", "Band 1 gain in decibels", "Band 1 Q" },
                    editors.Select(AutomationName).ToArray());
                Assert.IsNotNull(bandSelector.FocusVisualStyle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void DeviceAndSettingsControlsExposeUnambiguousRuntimeAutomationNames()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            try
            {
                window.Show();
                model.SelectedPage = ShellPage.Device;
                window.UpdateLayout();

                CollectionAssert.AreEquivalent(
                    new[] { "Pre gain in decibels", "Global gain in decibels" },
                    Descendants<TextBox>(window).Where(x => x.IsVisible).Select(AutomationName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Apply pre gain", "Apply global gain" },
                    Descendants<Button>(window)
                        .Where(x => x.IsVisible && Equals(x.Content, "Apply"))
                        .Select(AutomationName)
                        .ToArray());

                model.SelectedPage = ShellPage.Settings;
                window.UpdateLayout();
                Assert.AreEqual("Theme", AutomationName(Descendants<ComboBox>(window).Single(x => x.IsVisible)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RuntimeThemeChangeInvalidatesEveryCachedGraphThemeResource()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            try
            {
                window.Show();
                window.UpdateLayout();
                var graph = Descendants<EqGraph>(window).Single();
                graph.Width = 640;
                graph.Height = 300;
                Render(graph);
                var cacheFields = GraphDrawingCacheFields().ToArray();
                Assert.IsTrue(cacheFields.All(field => field.GetValue(graph) is not null));

                model.SelectedPage = ShellPage.Settings;
                window.UpdateLayout();
                var theme = Descendants<ComboBox>(window).Single(x => x.IsVisible);
                theme.SelectedValue = model.ThemeSelection == "Light" ? "Dark" : "Light";

                Assert.IsTrue(cacheFields.All(field => field.GetValue(graph) is null));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public async Task ConfirmationOverlayBehavesAsAnAccessibleModalDialog()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            try
            {
                window.Show();
                window.UpdateLayout();
                var shell = (Grid)window.FindName("ShellGrid");
                var activeEq = Descendants<ComboBox>(window)
                    .Single(x => ReferenceEquals(x.ItemsSource, MainViewModel.EqIndexes));
                Assert.IsTrue(activeEq.Focus());

                var decision = model.Dialog.AskAsync("Import EQ", "Import 8 EQ bands? This does not save to flash.", "Import");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                var cancel = Descendants<Button>(window).Single(x => Equals(x.Content, "Cancel"));
                var confirm = Descendants<Button>(window).Single(x => Equals(x.Content, "Import"));
                var dialog = Ancestors<Border>(cancel).First(x => x.Width == 440);
                var peer = UIElementAutomationPeer.CreatePeerForElement(dialog);

                Assert.IsFalse(shell.IsEnabled);
                Assert.IsTrue(dialog.IsKeyboardFocusWithin);
                Assert.IsNotNull(peer);
                Assert.AreEqual(AutomationControlType.Window, peer.GetAutomationControlType());
                Assert.AreEqual("Import EQ", peer.GetName());
                Assert.AreEqual("Import 8 EQ bands? This does not save to flash.", peer.GetHelpText());
                Assert.AreEqual(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(dialog));
                Assert.IsTrue(cancel.IsCancel);
                Assert.IsTrue(confirm.IsDefault);

                Assert.IsTrue(confirm.Focus());
                Assert.IsTrue(confirm.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.IsTrue(dialog.IsKeyboardFocusWithin);

                cancel.Command.Execute(cancel.CommandParameter);
                Assert.IsFalse(await decision);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.IsTrue(shell.IsEnabled);
                Assert.IsTrue(activeEq.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public async Task WindowCloseWaitsForIncompleteAsyncDisposalAndRunsOnlyOnce()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var device = new DeferredDisposePro2Device();
            var service = new MoondropDeviceService(
                new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, ""));
            var model = MainViewModel.CreateHardware(service, new AppConfig(), configFileExists: false);
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            var closedCount = 0;
            window.Closed += (_, _) => closedCount++;
            try
            {
                window.Show();

                window.Close();
                await WaitUntilAsync(() => device.DisposeCallCount == 1);

                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(0, closedCount);

                window.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(1, device.DisposeCallCount);
                Assert.AreEqual(0, closedCount);

                device.CompleteDispose();
                await WaitUntilAsync(() => closedCount == 1);

                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(1, device.DisposeCallCount);
            }
            finally
            {
                device.CompleteDispose();
                if (window.IsVisible)
                    window.Close();
            }
        });
    }

    [TestMethod]
    public void LegacyPresetPageHidesAndDisablesPro2Workflows()
    {
        WpfTestHost.Run(() =>
        {
            var device = new StubLegacyDevice();
            var service = new MoondropDeviceService(
                new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, device.DisplayName, device, ""));
            var model = MainViewModel.CreateHardware(service, new AppConfig(), configFileExists: false);
            model.SelectedPage = ShellPage.Presets;
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1100", "--height=760"]));
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.IsFalse(model.ImportEqCommand.CanExecute(null));
                Assert.IsFalse(model.ApplyAllCommand.CanExecute(null));
                Assert.IsFalse(model.SaveEqCommand.CanExecute(null));

                var buttons = Descendants<Button>(window).ToArray();
                Assert.IsFalse(buttons.Any(x =>
                    x.IsVisible &&
                    (Equals(x.Content, "Import EQ…") ||
                     Equals(x.Content, "Apply all") ||
                     Equals(x.Content, "Save EQ to flash"))));
                Assert.IsTrue(Descendants<TextBlock>(window).Any(x =>
                    x.IsVisible &&
                    x.Text.Contains("not available on the original Dawn Pro", StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(buttons.Any(x => x.IsVisible && Equals(x.Content, "Open Device")));

                model.SelectedPage = ShellPage.Device;
                window.UpdateLayout();
                Assert.AreEqual("Legacy volume", AutomationName(Descendants<Slider>(window).Single(x => x.IsVisible)));
                CollectionAssert.AreEquivalent(
                    new[] { "Legacy gain", "Legacy LED", "Legacy filter" },
                    Descendants<ComboBox>(window).Where(x => x.IsVisible).Select(AutomationName).ToArray());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public async Task ApplyBandCommandStaysBusyAndRejectsRepeatedExecutionUntilWriteCompletes()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var device = new DeferredWritePro2Device();
            var service = new MoondropDeviceService(
                new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, ""));
            var model = MainViewModel.CreateHardware(service, new AppConfig(), configFileExists: false);
            try
            {
                var command = model.Bands[0].ApplyCommand;

                command.Execute(null);
                await WaitUntilAsync(() => device.WriteBandCallCount == 1);

                Assert.IsFalse(command.CanExecute(null));
                command.Execute(null);

                device.CompleteWrite();
                await WaitUntilAsync(() => command.CanExecute(null));
                await Task.Delay(50);

                Assert.AreEqual(1, device.WriteBandCallCount);
            }
            finally
            {
                device.CompleteWrite();
                await model.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void ReplacingGraphBandsRebuildsRenderedResponseGeometry()
    {
        WpfTestHost.Run(() =>
        {
            var graph = new EqGraph
            {
                Width = 640,
                Height = 300,
                Bands = new ObservableCollection<BandViewModel>
                {
                    new(0, 120, 0.7, 8, PeqFilterType.LowShelf2, true)
                }
            };
            Render(graph);
            var geometryField = typeof(EqGraph).GetField(
                "_cachedResponse",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(geometryField);
            var firstGeometry = geometryField.GetValue(graph);
            Assert.IsNotNull(firstGeometry);

            graph.Bands = new ObservableCollection<BandViewModel>
            {
                new(0, 8000, 0.7, -8, PeqFilterType.HighShelf2, true)
            };
            Render(graph);
            var replacementGeometry = geometryField.GetValue(graph);

            Assert.IsNotNull(replacementGeometry);
            Assert.AreNotSame(firstGeometry, replacementGeometry);
        });
    }

    [TestMethod]
    public void HighContrastChangeInvalidatesEveryCachedGraphDrawingResource()
    {
        WpfTestHost.Run(() =>
        {
            var graph = new EqGraph
            {
                Width = 640,
                Height = 300,
                Bands = new ObservableCollection<BandViewModel>
                {
                    new(0, 1000, 1, 6, PeqFilterType.Peaking, true)
                }
            };
            Render(graph);
            var cacheFields = new[]
            {
                "_responsePen",
                "_individualResponsePen",
                "_selectedResponsePen",
                "_gridPen",
                "_zeroPen",
                "_disabledHandleBrush"
            }.Select(name => typeof(EqGraph).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic))
             .ToArray();
            Assert.IsTrue(cacheFields.All(field => field is not null && field.GetValue(graph) is not null));

            var handler = typeof(EqGraph).GetMethod(
                "SystemParametersChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler);
            handler.Invoke(graph, [null, new PropertyChangedEventArgs(nameof(SystemParameters.HighContrast))]);

            Assert.IsTrue(cacheFields.All(field => field!.GetValue(graph) is null));
        });
    }

    [TestMethod]
    public void IndividualGraphCurvesUseContrastingPensAndNonColorSelectionCue()
    {
        WpfTestHost.Run(() =>
        {
            var graph = new EqGraph
            {
                Width = 640,
                Height = 300,
                Bands = new ObservableCollection<BandViewModel>
                {
                    new(0, 500, 1, 6, PeqFilterType.Peaking, true),
                    new(1, 4000, 1, -6, PeqFilterType.Peaking, true)
                }
            };
            Render(graph);

            var individual = CachedPen(graph, "_individualResponsePen");
            var selected = CachedPen(graph, "_selectedResponsePen");
            var combined = CachedPen(graph, "_responsePen");

            Assert.IsGreaterThanOrEqualTo(0.86, individual.Brush.Opacity);
            Assert.IsGreaterThan(individual.Thickness, selected.Thickness);
            Assert.IsNotEmpty(selected.DashStyle.Dashes);
            Assert.IsGreaterThan(selected.Thickness, combined.Thickness);
            Assert.IsEmpty(combined.DashStyle.Dashes);
        });
    }

    [TestMethod]
    public void ConnectionStatusContentStaysInsideItsNavigationPill()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1440", "--height=900"]));
            try
            {
                window.Show();
                window.UpdateLayout();
                var statusText = Descendants<TextBlock>(window)
                    .Single(x =>
                        x.Text == model.ConnectionState &&
                        Ancestors<Border>(x).Any(border => border.ToolTip?.ToString() == model.ConnectionState));
                var pill = Ancestors<Border>(statusText)
                    .First(x => x.ToolTip?.ToString() == model.ConnectionState);
                var bounds = statusText.TransformToAncestor(pill)
                    .TransformBounds(new Rect(statusText.RenderSize));

                Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
                Assert.IsLessThanOrEqualTo(pill.ActualWidth, bounds.Right);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string AutomationName(Control control)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        Assert.IsNotNull(peer, $"No automation peer was created for {control.GetType().Name}.");
        return peer.GetName();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<T> Ancestors<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match)
                yield return match;
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static void Render(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)element.Width,
            (int)element.Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    private static Pen CachedPen(EqGraph graph, string fieldName)
    {
        var field = typeof(EqGraph).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var pen = field.GetValue(graph) as Pen;
        Assert.IsNotNull(pen);
        return pen;
    }

    private static IEnumerable<FieldInfo> GraphDrawingCacheFields() =>
        new[]
        {
            "_responsePen",
            "_individualResponsePen",
            "_selectedResponsePen",
            "_gridPen",
            "_zeroPen",
            "_disabledHandleBrush"
        }.Select(name => typeof(EqGraph).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic))
         .Where(field => field is not null)!;

    private sealed class DeferredDisposePro2Device : IDawnPro2Device, IAsyncDisposable
    {
        private readonly TaskCompletionSource _dispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceKind Kind => DeviceKind.DawnPro2;
        public string DisplayName => "Deferred DAWN PRO2";
        public bool IsUsable => true;
        public int DisposeCallCount { get; private set; }

        public void CompleteDispose() => _dispose.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return new ValueTask(_dispose.Task);
        }

        public Task<string> ReadFirmwareVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test");

        public Task<int> ReadActiveEqAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task WriteActiveEqAsync(int index, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<double> ReadPreGainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0d);

        public Task WritePreGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<double> ReadGlobalGainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0d);

        public Task WriteGlobalGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PeqBand> ReadBandAsync(int index, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PeqBand(index, 1000, 1, 0, PeqFilterType.Peaking));

        public Task<IReadOnlyList<PeqBand>> ReadAllBandsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeqBand>>(
                Enumerable.Range(0, 8)
                    .Select(index => new PeqBand(index, 1000, 1, 0, PeqFilterType.Peaking))
                    .ToArray());

        public Task WriteBandAsync(PeqBand band, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveEqToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubLegacyDevice : ILegacyDawnProDevice
    {
        public DeviceKind Kind => DeviceKind.Legacy;
        public string DisplayName => "Original Dawn Pro";

        public Task<int?> GetVolumeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(30);

        public Task<string?> GetLedStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("On");

        public Task<string?> GetGainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("Low");

        public Task<string?> GetFilterAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("Fast Roll-Off Low Latency");

        public Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SetLedStatusAsync(string status, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SetGainAsync(string gain, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SetFilterAsync(string filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class DeferredWritePro2Device : IDawnPro2Device
    {
        private readonly TaskCompletionSource _write =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceKind Kind => DeviceKind.DawnPro2;
        public bool IsUsable => true;
        public string DisplayName => "Deferred write DAWN PRO2";
        public int WriteBandCallCount { get; private set; }

        public void CompleteWrite() => _write.TrySetResult();

        public Task<string> ReadFirmwareVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test");

        public Task<int> ReadActiveEqAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task WriteActiveEqAsync(int index, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<double> ReadPreGainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0d);

        public Task WritePreGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<double> ReadGlobalGainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0d);

        public Task WriteGlobalGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PeqBand> ReadBandAsync(int index, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PeqBand(index, 1000, 1, 0, PeqFilterType.Peaking));

        public Task<IReadOnlyList<PeqBand>> ReadAllBandsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeqBand>>(
                Enumerable.Range(0, 8)
                    .Select(index => new PeqBand(index, 1000, 1, 0, PeqFilterType.Peaking))
                    .ToArray());

        public async Task WriteBandAsync(PeqBand band, CancellationToken cancellationToken = default)
        {
            WriteBandCallCount++;
            await _write.Task.WaitAsync(cancellationToken);
        }

        public Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveEqToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
