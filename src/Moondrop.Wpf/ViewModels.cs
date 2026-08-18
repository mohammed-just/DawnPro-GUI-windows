using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Eq;
using Moondrop.Hardware;

namespace Moondrop.Wpf;

public enum ShellPage
{
    Eq,
    Device,
    Presets,
    Settings,
    About
}

public sealed class MainViewModel : NotifyObject, IDisposable, IAsyncDisposable
{
    public static IReadOnlyList<PeqFilterType> FilterTypes { get; } =
    [
        PeqFilterType.Disabled, PeqFilterType.LowShelf2, PeqFilterType.Peaking,
        PeqFilterType.HighShelf2, PeqFilterType.LowPass2, PeqFilterType.HighPass2,
        PeqFilterType.Unknown
    ];
    public static IReadOnlyList<int> EqIndexes { get; } = Enumerable.Range(0, 16).ToArray();
    public static IReadOnlyList<string> LegacyGains { get; } = ["Low", "High"];
    public static IReadOnlyList<string> LegacyLeds { get; } = ["On", "Temporarily Off", "Off"];
    public static IReadOnlyList<string> LegacyFilters { get; } =
    [
        "Fast Roll-Off Low Latency", "Fast Roll-Off Phase Compensated",
        "Slow Roll-Off Low Latency", "Slow Roll-Off Phase Compensated", "Non-Oversampling"
    ];

    private readonly MoondropDeviceService? _service;
    private readonly string _configPath;
    private AppConfig _config;
    private CancellationTokenSource? _legacyVolumeWrite;
    private int _selectedBandIndex;
    private bool _suppressHardwareWrites;
    private readonly bool _configFileExists;
    private bool _initialized;
    private ShellPage _selectedPage = ShellPage.Eq;
    private string _themeSelection = "System";
    private string _selectedDevice = "DAWN PRO2";
    private string _status;
    private double _preGain;
    private double _globalGain;
    private int _activeEq;
    private string _firmwareVersion = "—";
    private int _legacyVolume;
    private string _legacyGain = "Low";
    private string _legacyLed = "On";
    private string _legacyFilter = "Fast Roll-Off Low Latency";

    private MainViewModel(IEnumerable<BandViewModel> bands, string status, MoondropDeviceService? service, AppConfig config, string configPath, bool demo, bool configFileExists = false)
    {
        Bands = new ObservableCollection<BandViewModel>(bands);
        if (Bands.Count > 0)
            Bands[0].IsSelected = true;
        _status = status;
        _service = service;
        _config = config;
        _configPath = configPath;
        _configFileExists = configFileExists;
        IsDemo = demo;
        Dialog = new DialogState();
        Banner = new StatusBannerState();
        IsLegacy = service?.Selection.Kind == DeviceKind.Legacy;
        IsPro2 = !IsLegacy;
        if (service is not null)
            _selectedDevice = service.Selection.DisplayName;

        RefreshCommand = new RelayCommand(RefreshAsync, () => _service is not null);
        ApplyAllCommand = new RelayCommand(() => RunPro2Async("Apply all bands", s => s.ImportEqAsync(Bands.Select(x => x.ToCoreBand()).ToArray(), null, false), refreshAfter: true), () => IsPro2);
        SaveEqCommand = new RelayCommand(() => RunPro2Async("Save EQ to flash", s => s.SaveEqToFlashAsync()), () => IsPro2);
        SaveGainsCommand = new RelayCommand(() => RunPro2Async("Save gains to flash", s => s.SaveGainsToFlashAsync()), () => IsPro2);
        SaveSettingsCommand = new RelayCommand(SaveSettingsAsync);
        ApplyPreGainCommand = new RelayCommand(() => RunPro2Async("Apply pre gain", s => s.SetPreGainAsync(PreGain, false), refreshAfter: true), () => IsPro2);
        ApplyGlobalGainCommand = new RelayCommand(() => RunPro2Async("Apply global gain", s => s.SetGlobalGainAsync(GlobalGain, false), refreshAfter: true), () => IsPro2);
        ApplyActiveEqCommand = new RelayCommand(() => RunPro2Async("Apply active EQ", s => s.SetActiveEqAsync(ActiveEq, false), refreshAfter: true), () => IsPro2);
        EnableCoefficientsCommand = new RelayCommand(() => RunPro2Async($"Enable coefficients for band {SelectedBandIndex + 1}", s => s.EnableBandCoefficientsAsync(SelectedBandIndex)), () => IsPro2);
        ImportEqCommand = new RelayCommand(ImportEqAsync, () => IsPro2);
        ApplyLegacyCommand = new RelayCommand(ApplyLegacyAsync);
        ShowDiagnosticsCommand = new RelayCommand(ShowDiagnosticsAsync);

        foreach (var band in Bands)
            band.SetApplyHandler(b => RunPro2Async($"Apply band {b.DisplayIndex}", s => s.ApplyBandAsync(b.ToCoreBand()), refreshAfter: true));
    }

    public string Title => "Moondrop Dawn Pro";
    public ObservableCollection<BandViewModel> Bands { get; }
    public bool IsDemo { get; }
    public bool IsHardwareConnected => _service is not null;
    public bool IsPro2 { get; }
    public bool IsLegacy { get; }
    public string ConnectionState => IsDemo ? "Demo mode · hardware disabled" : "Connected";
    public DialogState Dialog { get; }
    public StatusBannerState Banner { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ApplyAllCommand { get; }
    public ICommand SaveEqCommand { get; }
    public ICommand SaveGainsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ApplyPreGainCommand { get; }
    public ICommand ApplyGlobalGainCommand { get; }
    public ICommand ApplyActiveEqCommand { get; }
    public ICommand EnableCoefficientsCommand { get; }
    public ICommand ImportEqCommand { get; }
    public ICommand ApplyLegacyCommand { get; }
    public ICommand ShowDiagnosticsCommand { get; }

    public ShellPage SelectedPage { get => _selectedPage; set => SetField(ref _selectedPage, value); }
    public int SelectedBandIndex
    {
        get => _selectedBandIndex;
        set
        {
            var selected = Math.Clamp(value, 0, Bands.Count - 1);
            if (!SetField(ref _selectedBandIndex, selected))
                return;
            foreach (var band in Bands)
                band.IsSelected = band.Index == selected;
        }
    }
    public string ThemeSelection { get => _themeSelection; set => SetField(ref _themeSelection, value); }
    public string SelectedDevice { get => _selectedDevice; set => SetField(ref _selectedDevice, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string FirmwareVersion { get => _firmwareVersion; set => SetField(ref _firmwareVersion, value); }
    public int ActiveEq { get => _activeEq; set => SetField(ref _activeEq, Math.Clamp(value, 0, 15)); }
    public double PreGain { get => _preGain; set { if (double.IsFinite(value)) SetField(ref _preGain, Math.Clamp(value, -18, 12)); } }
    public double GlobalGain { get => _globalGain; set { if (double.IsFinite(value)) SetField(ref _globalGain, Math.Clamp(value, -18, 12)); } }
    public int LegacyVolume
    {
        get => _legacyVolume;
        set
        {
            if (SetField(ref _legacyVolume, Math.Clamp(value, 0, 60)) && IsLegacy && !_suppressHardwareWrites)
                QueueLegacyVolumeWrite();
        }
    }

    public string LegacyGain
    {
        get => _legacyGain;
        set
        {
            if (SetField(ref _legacyGain, value) && IsLegacy && !_suppressHardwareWrites)
                _ = SetLegacyValueAsync("Gain", s => s.SetLegacyGainAsync(value));
        }
    }

    public string LegacyLed
    {
        get => _legacyLed;
        set
        {
            if (SetField(ref _legacyLed, value) && IsLegacy && !_suppressHardwareWrites)
                _ = SetLegacyValueAsync("LED", s => s.SetLegacyLedAsync(value));
        }
    }

    public string LegacyFilter
    {
        get => _legacyFilter;
        set
        {
            if (SetField(ref _legacyFilter, value) && IsLegacy && !_suppressHardwareWrites)
                _ = SetLegacyValueAsync("Filter", s => s.SetLegacyFilterAsync(value));
        }
    }

    public static MainViewModel CreateDemo()
    {
        var demoBands = new[]
        {
            new BandViewModel(0, 25, 0.71, 6.0, PeqFilterType.LowShelf2, true),
            new BandViewModel(1, 105, 0.71, 4.5, PeqFilterType.LowShelf2, true),
            new BandViewModel(2, 160, 0.55, -3.0, PeqFilterType.Peaking, true),
            new BandViewModel(3, 1350, 1.5, -2.2, PeqFilterType.Peaking, true),
            new BandViewModel(4, 1900, 0.71, 4.5, PeqFilterType.HighShelf2, true),
            new BandViewModel(5, 3250, 2.1, -3.8, PeqFilterType.Peaking, true),
            new BandViewModel(6, 5400, 3.5, -7.0, PeqFilterType.Peaking, true),
            new BandViewModel(7, 11000, 0.71, -4.0, PeqFilterType.HighShelf2, true),
        };
        return new MainViewModel(demoBands, "Demo mode: hardware transports disabled.", null, new AppConfig(), AppConfig.DefaultConfigPath(), true);
    }

    public static MainViewModel CreateHardware(MoondropDeviceService service, AppConfig config, bool configFileExists, string? configPath = null)
    {
        var model = new MainViewModel(Enumerable.Range(0, 8).Select(i => new BandViewModel(i, 1000, 1, 0, PeqFilterType.Peaking, false)), $"Connected: {service.Selection.DisplayName}. Loading device state…", service, config, configPath ?? AppConfig.DefaultConfigPath(), false, configFileExists);
        if (model.IsPro2)
        {
            model.ActiveEq = config.DawnPro2Settings.DefaultEqIndex;
            model.PreGain = config.DawnPro2Settings.DefaultPreGain;
            model.GlobalGain = config.DawnPro2Settings.DefaultGlobalGain;
        }
        return model;
    }

    public async Task InitializeHardwareAsync()
    {
        if (_initialized || _service is null)
            return;
        _initialized = true;
        if (IsLegacy && _configFileExists)
        {
            LoadLegacyDefaults(_config.DefaultSettings);
            var ok = await _service.ApplyLegacyDefaultsAsync(_config).ConfigureAwait(true);
            Status = ok ? "Saved legacy defaults applied." : "One or more saved legacy defaults failed to apply.";
        }
        await RefreshAsync();
    }

    public void UpdateBand(int index, int frequency, double gain, double q)
    {
        Bands[index].Frequency = frequency;
        Bands[index].Gain = gain;
        Bands[index].Q = q;
    }

    private async Task RefreshAsync()
    {
        if (_service is null)
            return;
        try
        {
            if (IsPro2)
            {
                var snapshot = await _service.RefreshPro2Async();
                FirmwareVersion = snapshot.FirmwareVersion;
                ActiveEq = snapshot.ActiveEq;
                PreGain = snapshot.PreGain;
                GlobalGain = snapshot.GlobalGain;
                foreach (var band in snapshot.Bands)
                    Bands[band.Index].Load(band);
            }
            else
            {
                _suppressHardwareWrites = true;
                try
                {
                    var snapshot = await _service.RefreshLegacyAsync();
                    if (snapshot.Volume.HasValue)
                        LegacyVolume = snapshot.Volume.Value;
                    LegacyGain = snapshot.Gain ?? LegacyGain;
                    LegacyFilter = snapshot.Filter ?? LegacyFilter;
                    LegacyLed = snapshot.LedStatus ?? LegacyLed;
                }
                finally
                {
                    _suppressHardwareWrites = false;
                }
            }
            Status = $"Refreshed {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            ShowError("Refresh failed", ex);
        }
    }

    private async Task ImportEqAsync()
    {
        var dialog = new OpenFileDialog { Filter = "EQ preset (*.txt;*.peq)|*.txt;*.peq|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var preset = EqPresetParser.Load(dialog.FileName);
            var preampText = preset.Preamp.HasValue ? $" Preamp {preset.Preamp.Value:F1} dB will be applied to active memory." : "";
            if (!await Dialog.AskAsync(
                    "Import EQ",
                    $"Import {preset.Bands.Count} EQ bands?{preampText} This does not save to flash.",
                    "Import"))
                return;
            await RunPro2Async("Import EQ", async service =>
            {
                await service.ImportEqAsync(preset.Bands, preset.Preamp, applyPreamp: preset.Preamp.HasValue);
                if (preset.Preamp.HasValue)
                    PreGain = preset.Preamp.Value;
                var snapshot = await service.RefreshPro2Async();
                FirmwareVersion = snapshot.FirmwareVersion;
                ActiveEq = snapshot.ActiveEq;
                PreGain = snapshot.PreGain;
                GlobalGain = snapshot.GlobalGain;
                foreach (var band in snapshot.Bands)
                    Bands[band.Index].Load(band);
            });
        }
        catch (Exception ex)
        {
            ShowError("Import EQ failed", ex);
        }
    }

    private void LoadLegacyDefaults(DefaultSettings defaults)
    {
        _suppressHardwareWrites = true;
        try
        {
            LegacyVolume = defaults.DefaultVolume;
            LegacyGain = defaults.DefaultGain;
            LegacyLed = defaults.DefaultLedStatus;
            LegacyFilter = defaults.DefaultFilter;
        }
        finally
        {
            _suppressHardwareWrites = false;
        }
    }

    private void QueueLegacyVolumeWrite()
    {
        _legacyVolumeWrite?.Cancel();
        _legacyVolumeWrite?.Dispose();
        var cts = new CancellationTokenSource();
        _legacyVolumeWrite = cts;
        _ = WriteLegacyVolumeAfterDelayAsync(cts, LegacyVolume);
    }

    private async Task WriteLegacyVolumeAfterDelayAsync(CancellationTokenSource owner, int value)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), owner.Token).ConfigureAwait(true);
            await SetLegacyValueAsync("Volume", s => s.SetLegacyVolumeAsync(value), owner.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_legacyVolumeWrite, owner))
                _legacyVolumeWrite = null;
            owner.Dispose();
        }
    }

    private async Task SetLegacyValueAsync(string label, Func<MoondropDeviceService, Task<bool>> operation, CancellationToken cancellationToken = default)
    {
        if (_service is null)
            return;
        try
        {
            var ok = await operation(_service).WaitAsync(cancellationToken).ConfigureAwait(true);
            Status = ok ? $"{label} applied." : $"{label} write failed.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError($"{label} write failed", ex);
        }
    }

    private Task ShowDiagnosticsAsync()
    {
        var text = DeviceDiagnostics.CollectText();
        new DiagnosticsWindow(text).Show();
        Status = "Diagnostics opened.";
        return Task.CompletedTask;
    }

    private Task SaveSettingsAsync()
    {
        try
        {
            if (IsLegacy)
            {
                _config = _config.WithLegacyDefaults(LegacyVolume, LegacyGain, LegacyLed, LegacyFilter);
                _config.SaveFile(_configPath);
                Status = "Legacy defaults saved.";
            }
            else
            {
                _config = _config.WithDawnPro2Defaults(ActiveEq, PreGain, GlobalGain);
                _config.SaveFile(_configPath);
                Status = "DAWN PRO2 defaults saved.";
            }
        }
        catch (Exception ex)
        {
            ShowError("Save settings failed", ex);
        }
        return Task.CompletedTask;
    }

    private async Task ApplyLegacyAsync()
    {
        if (_service is null)
            return;
        try
        {
            var ok = await _service.SetLegacyVolumeAsync(LegacyVolume);
            ok &= await _service.SetLegacyGainAsync(LegacyGain);
            ok &= await _service.SetLegacyLedAsync(LegacyLed);
            ok &= await _service.SetLegacyFilterAsync(LegacyFilter);
            Status = ok ? "Legacy settings applied." : "One or more legacy writes failed.";
        }
        catch (Exception ex)
        {
            ShowError("Apply legacy settings failed", ex);
        }
    }

    private async Task RunPro2Async(string action, Func<MoondropDeviceService, Task> operation, bool refreshAfter = false)
    {
        if (_service is null)
        {
            Status = $"{action}: demo mode.";
            return;
        }
        try
        {
            await operation(_service);
            if (refreshAfter)
            {
                await RefreshAsync();
                if (Status.StartsWith("Refresh failed", StringComparison.Ordinal))
                    return;
            }
            Status = $"{action} complete.";
        }
        catch (Exception ex)
        {
            ShowError($"{action} failed", ex);
        }
    }

    private void ShowError(string title, Exception ex)
    {
        Status = $"{title}: {ex.Message}";
        Banner.ShowError(title, ex.Message);
    }

    public void Dispose()
    {
        _legacyVolumeWrite?.Cancel();
        _legacyVolumeWrite?.Dispose();
        _service?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _legacyVolumeWrite?.Cancel();
        _legacyVolumeWrite?.Dispose();
        if (_service is not null)
            await _service.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class BandViewModel : NotifyObject
{
    private int _frequency;
    private double _q;
    private double _gain;
    private PeqFilterType _filterType;
    private byte? _rawFilterCode;
    private bool _enabled;
    private bool _isSelected;
    private Func<BandViewModel, Task>? _applyAsync;

    public BandViewModel(int index, int frequency, double q, double gain, PeqFilterType filterType, bool enabled, byte? rawFilterCode = null)
    {
        Index = index;
        _frequency = frequency;
        _q = q;
        _gain = gain;
        _filterType = filterType;
        _rawFilterCode = rawFilterCode;
        _enabled = enabled;
        ApplyCommand = new RelayCommand(
            () => _applyAsync?.Invoke(this) ?? Task.CompletedTask,
            () => _applyAsync is not null);
    }

    public int Index { get; }
    public int DisplayIndex => Index + 1;
    public ICommand ApplyCommand { get; }
    public int Frequency { get => _frequency; set => SetField(ref _frequency, Math.Clamp(value, 20, 20000)); }
    public double Q { get => _q; set { if (double.IsFinite(value)) SetField(ref _q, Math.Clamp(value, 0.1, 127)); } }
    public double Gain { get => _gain; set { if (double.IsFinite(value)) SetField(ref _gain, Math.Clamp(value, -18, 12)); } }
    public PeqFilterType FilterType
    {
        get => _filterType;
        set
        {
            if (SetField(ref _filterType, value) && value != PeqFilterType.Unknown)
                _rawFilterCode = null;
        }
    }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
    public bool IsSelected { get => _isSelected; internal set => SetField(ref _isSelected, value); }

    internal void SetApplyHandler(Func<BandViewModel, Task> applyAsync)
    {
        _applyAsync = applyAsync;
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
    }

    public void Load(PeqBand band)
    {
        Frequency = band.Frequency;
        Q = band.Q;
        Gain = band.Gain;
        FilterType = band.FilterType;
        _rawFilterCode = band.RawFilterCode;
        Enabled = band.Enabled;
    }

    public PeqBand ToCoreBand() => new(Index, Frequency, Q, Gain, FilterType, Enabled, _rawFilterCode);
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
