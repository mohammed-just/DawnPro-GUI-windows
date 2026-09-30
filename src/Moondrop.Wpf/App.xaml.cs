using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moondrop.Core.Config;
using Moondrop.Hardware;

namespace Moondrop.Wpf;

public partial class App : Application
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await StartAsync(e.Args.Length > 0 ? e.Args : Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    private async Task StartAsync(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        var smoke = args.FirstOrDefault(x => x.StartsWith("--workspace-smoke="))?[18..];
        if (smoke is not null) options = options with { Demo = true };
        WpfTheme.Apply(options.Theme, this, null);

        MainViewModel model;
        if (smoke is not null) model = MainViewModel.CreateOffline(WorkspaceDirectory(args));
        else if (options.Demo || options.Benchmark)
        {
            var directory = args.FirstOrDefault(x => x.StartsWith("--workspace="))?[12..];
            model = MainViewModel.CreateDemo(directory, directory is not null);
        }
        else if (args.Contains("--offline"))
            model = MainViewModel.CreateOffline(args.FirstOrDefault(x => x.StartsWith("--workspace="))?[12..]);
        else
        {
            try
            {
                var configPath = AppConfig.DefaultConfigPath();
                var configExists = File.Exists(configPath);
                var config = AppConfig.LoadFile(configPath);
                var service = await MoondropDeviceService.SelectAsync(new HardwareDeviceFactory(), config).ConfigureAwait(true);
                model = MainViewModel.CreateHardware(service, config, configExists, configPath, WorkspaceDirectory(args));
            }
            catch (Exception ex)
            {
                model = MainViewModel.CreateOffline(WorkspaceDirectory(args));
                model.Status = "Disconnected workspace: " + ex.Message;
            }
        }
        if (args.Any(x => x.StartsWith("--theme="))) model.ThemeSelection = options.Theme.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : options.Theme.Equals("dark", StringComparison.OrdinalIgnoreCase)
                ? "Dark"
                : "System";
        var window = new MainWindow(model, options);
        WpfTheme.Apply(model.ThemeSelection, this, window);
        WpfTheme.ApplyAccent(model.SystemAccent, this);
        var benchmarkStarted = false;
        async Task RunBenchmarkOnce(long firstRenderMs)
        {
            if (!options.Benchmark || benchmarkStarted)
                return;
            benchmarkStarted = true;
            await BenchmarkRunner.RunAsync(window, model, firstRenderMs).ConfigureAwait(true);
            Shutdown();
        }

        window.ContentRendered += async (_, _) =>
        {
            await model.InitializeHardwareAsync().ConfigureAwait(true);
            if (smoke is not null)
            {
                try { WorkspaceSmoke.Run(window, model, WorkspaceDirectory(args), smoke); Shutdown(0); }
                catch (Exception ex) { Moondrop.Core.Eq.WorkspaceStorage.AtomicWrite(Path.Combine(WorkspaceDirectory(args), "smoke-failure.txt"), ex.ToString()); Shutdown(1); }
                return;
            }
            var page = args.FirstOrDefault(x => x.StartsWith("--page="))?[7..];
            if (Enum.TryParse<ShellPage>(page, true, out var selectedPage)) model.SelectedPage = selectedPage;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (!string.IsNullOrWhiteSpace(options.ScreenshotPath))
            {
                // RenderTargetBitmap has no DWM/Mica surface. Use the official Fluent
                // fallback background so translucent theme brushes composite correctly.
                window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                VisualCapture.SavePng(window, options.ScreenshotPath, options.CaptureScale);
                Shutdown();
                return;
            }
            await RunBenchmarkOnce(_startup.ElapsedMilliseconds).ConfigureAwait(true);
        };
        window.Show();
    }
    private static string WorkspaceDirectory(string[] args) => args.FirstOrDefault(x => x.StartsWith("--workspace="))?[12..] ?? Moondrop.Core.Eq.WorkspaceStorage.DefaultDirectory;
}

public sealed record LaunchOptions(bool Demo, bool Benchmark, string Theme, string? ScreenshotPath, int Width, int Height)
{
    public bool Offline { get; init; }
    public double CaptureScale { get; init; } = 1;
    public static LaunchOptions Parse(string[] args)
    {
        var theme = "system";
        foreach (var arg in args)
        {
            if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                theme = arg["--theme=".Length..].ToLowerInvariant();
        }
        var screenshot = args.FirstOrDefault(x => x.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
        var width = ParseDimension(args, "--width=", 1180, 640, 3840);
        var height = ParseDimension(args, "--height=", 780, 620, 2160);
        var scaleText = args.FirstOrDefault(x => x.StartsWith("--capture-scale="))?[16..];
        var scale = double.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedScale) ? Math.Clamp(parsedScale, 1, 2) : 1;
        return new LaunchOptions(
            args.Contains("--demo"),
            args.Contains("--benchmark"),
            theme,
            screenshot?["--screenshot=".Length..],
            width,
            height) { Offline = args.Contains("--offline"), CaptureScale = scale };
    }

    private static int ParseDimension(string[] args, string prefix, int fallback, int minimum, int maximum)
    {
        var value = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is not null && int.TryParse(value[prefix.Length..], out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}

internal static class VisualCapture
{
    public static void SavePng(Window window, string path, double scale = 1)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

public static class BenchmarkRunner
{
    public static async Task RunAsync(Window window, MainViewModel model, long firstRenderMs)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var idlePrivate = process.PrivateMemorySize64;
        var idleWorking = process.WorkingSet64;
        // Run on the UI dispatcher and drain layout/render work every ten updates.
        // This models input coalescing while ensuring graph rendering is inside the timing.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            model.UpdateBand(i % model.Bands.Count, 80 + i % 12000, (i % 240) / 10.0 - 12.0, 0.5 + (i % 40) / 10.0);
            if ((i + 1) % 10 == 0)
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        sw.Stop();
        var output = new
        {
            startupToFirstRenderMs = firstRenderMs,
            privateBytesAfter3sIdle = idlePrivate,
            workingSetAfter3sIdle = idleWorking,
            graphEditor1000UpdatesMs = sw.ElapsedMilliseconds
        };
        Console.WriteLine(JsonSerializer.Serialize(output));
    }
}
