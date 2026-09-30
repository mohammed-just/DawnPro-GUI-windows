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
    public async Task InvalidNumericInputBlocksApplyUntilCorrected()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var device = new WorkspaceDevice();
            await using var model = MainViewModel.CreateHardware(new(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, "")), new(), false);
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); await model.RefreshAsync(); model.PreGain = -2; window.UpdateLayout(); Assert.IsTrue(model.CanApply);
                var box = Descendants<TextBox>(window).Single(x => AutomationName(x) == "Pre gain");
                box.Text = "NaN"; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.IsTrue(Validation.GetHasError(box)); Assert.IsTrue(model.HasInputErrors); Assert.IsFalse(model.CanApply); Assert.AreEqual(-2, model.PreGain);
                box.Text = "-2"; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.IsFalse(model.HasInputErrors); Assert.IsTrue(model.CanApply); Assert.AreEqual(0, device.Writes);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void EqEditorsExposeMeaningfulRuntimeAutomationNamesAndVisibleBandFocus()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); window.UpdateLayout();
                var grid = (DataGrid)window.FindName("BandTable");
                Assert.AreEqual(8, grid.Items.Count);
                Assert.HasCount(6, grid.Columns);
                Assert.AreEqual("Eight band draft table", AutomationName(grid));
                var editor = (Border)window.FindName("SelectedBandEditor");
                var names = Descendants<TextBox>(editor).Select(AutomationName).ToArray();
                CollectionAssert.AreEquivalent(new[] { "Selected band frequency", "Selected band gain", "Selected band Q" }, names);
                model.SelectedBandIndex = 5;
                Assert.AreEqual(5, grid.SelectedIndex);
                Assert.AreSame(model.Bands[5], editor.DataContext);
                Assert.IsTrue(model.Bands[5].IsSelected);
                Assert.IsFalse(Descendants<Button>(window).Any(x => Equals(x.Content, "Apply band")));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DeviceAndSettingsControlsExposeUnambiguousRuntimeAutomationNames()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.IsTrue(Descendants<TextBox>(window).Any(x => AutomationName(x) == "Pre gain"));
                Assert.AreEqual(1, Descendants<Button>(window).Count(x => x.IsVisible && Equals(x.Content, "Apply changes")));
                model.SelectedPage = ShellPage.Settings; window.UpdateLayout();
                Assert.AreEqual("Theme", AutomationName(Descendants<ComboBox>(window).Single(x => x.IsVisible)));
                Assert.AreEqual(4, ((System.Windows.Controls.Primitives.UniformGrid)window.FindName("SettingsGroups")).Children.Count);
                Assert.IsGreaterThanOrEqualTo(9, Descendants<CheckBox>(window).Count(x => x.IsVisible));
            }
            finally { window.Close(); }
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
                    .Single(x => AutomationName(x) == "Target");
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
    public void LegacyPresetPageAllowsLocalLibraryAndGatesHardwareEq()
    {
        WpfTestHost.Run(() =>
        {
            var device = new StubLegacyDevice();
            using var model = MainViewModel.CreateHardware(new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, device.DisplayName, device, "")), new AppConfig(), false);
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); model.SelectedPage = ShellPage.Presets; window.UpdateLayout();
                Assert.IsTrue(model.ImportEqCommand.CanExecute(null));
                Assert.IsFalse(model.ApplyAllCommand.CanExecute(null));
                Assert.IsFalse(model.SaveEqCommand.CanExecute(null));
                Assert.IsTrue(Descendants<Button>(window).Any(x => x.IsVisible && Equals(x.Content, "Import")));
                model.SelectedPage = ShellPage.Eq; window.UpdateLayout();
                Assert.IsFalse(Descendants<EqGraph>(window).Single().IsVisible);
                Assert.AreEqual("Legacy volume", AutomationName(Descendants<Slider>(window).Single(x => x.IsVisible)));
                CollectionAssert.AreEquivalent(new[] { "Legacy gain", "Legacy LED", "Legacy filter" }, Descendants<ComboBox>(window).Where(x => x.IsVisible).Select(AutomationName).ToArray());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public async Task UnifiedApplyStaysBusyAndRejectsConflictingActionsUntilWriteCompletes()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var device = new DeferredWritePro2Device();
            await using var model = MainViewModel.CreateHardware(new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, "")), new AppConfig(), false);
            try
            {
                await model.RefreshAsync();
                var command = model.ApplyAllCommand;
                command.Execute(null);
                await WaitUntilAsync(() => device.WriteBandCallCount == 1);
                Assert.IsTrue(model.IsBusy);
                Assert.IsFalse(command.CanExecute(null)); Assert.IsFalse(model.RefreshCommand.CanExecute(null));
                var before = model.Bands[0].Gain; model.Bands[0].Gain = 4;
                Assert.AreEqual(before, model.Bands[0].Gain);
                command.Execute(null); device.CompleteWrite();
                await WaitUntilAsync(() => !model.IsBusy);
                Assert.AreEqual(1, device.WriteBandCallCount);
            }
            finally { device.CompleteWrite(); }
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
    public void CompactHeaderAndActionBarStayInsideMinimumWindow()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=640", "--height=620"]));
            try
            {
                window.Show(); window.UpdateLayout();
                var statusText = Descendants<TextBlock>(window).Single(x => x.IsVisible && x.Text == model.DeviceSummary);
                var bounds = statusText.TransformToAncestor(window).TransformBounds(new Rect(statusText.RenderSize));
                Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
                Assert.IsLessThanOrEqualTo(window.ActualWidth, bounds.Right);
                foreach (var action in Descendants<Button>(window).Where(x => x.IsVisible && (Equals(x.Content, "Apply changes") || Equals(x.Content, "Save to device"))))
                {
                    var rect = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
                    Assert.IsLessThanOrEqualTo(window.ActualHeight, rect.Bottom);
                }
                var editor = (Border)window.FindName("SelectedBandEditor");
                Assert.AreEqual(1, Grid.GetRow(editor));
            }
            finally { window.Close(); }
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

        public async Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default) { WriteBandCallCount++; await _write.Task.WaitAsync(cancellationToken); }

        public Task SaveEqToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
