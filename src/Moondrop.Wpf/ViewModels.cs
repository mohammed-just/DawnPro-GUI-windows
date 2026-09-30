using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Eq;
using Moondrop.Hardware;

namespace Moondrop.Wpf;

public sealed record PresetOption(Guid Id, string Name) { public override string ToString() => Name; }

public enum ShellPage { Eq, Presets, Settings, Device, About }

public sealed class MainViewModel : NotifyObject, IDisposable, IAsyncDisposable
{
    public static IReadOnlyList<PeqFilterType> FilterTypes { get; } = [PeqFilterType.Peaking, PeqFilterType.LowShelf2, PeqFilterType.HighShelf2, PeqFilterType.LowPass2, PeqFilterType.HighPass2, PeqFilterType.Disabled, PeqFilterType.Unknown];
    public static IReadOnlyList<int> EqIndexes { get; } = Enumerable.Range(0, 16).ToArray();
    public static IReadOnlyList<string> LegacyGains { get; } = ["Low", "High"];
    public static IReadOnlyList<string> LegacyLeds { get; } = ["On", "Temporarily Off", "Off"];
    public static IReadOnlyList<string> LegacyFilters { get; } = ["Fast Roll-Off Low Latency", "Fast Roll-Off Phase Compensated", "Slow Roll-Off Low Latency", "Slow Roll-Off Phase Compensated", "Non-Oversampling"];
    private MoondropDeviceService? _service;
    private IMoondropDevice? _session;
    private readonly AppConfig _config;
    private readonly string _workspacePath, _directory;
    private string _configPath = AppConfig.DefaultConfigPath();
    private LegacySnapshot? _legacyLocalBaseline;
    private bool _storageHealthy = true, _suppress, _initialized, _busy, _uncertain, _connected, _inputErrors, _restoredBaseline;
    private EqConfiguration? _observed;
    private LegacySnapshot? _legacyObserved;
    private EqConfiguration _localBaseline = EqConfiguration.Flat(), _lastEdit = EqConfiguration.Flat();
    private readonly Stack<EqConfiguration> _undo = new(), _redo = new();
    private int _editGroup;
    private EqConfiguration? _groupStart;
    private bool _eqPending, _gainPending;
    private string _eqPersistence = "Unknown", _gainPersistence = "Unknown";
    private string _status = "Disconnected — local editing is available.", _firmware = "—", _deviceName = "No device";
    private double _preGain, _globalGain, _peak;
    private int _activeEq, _selectedBandIndex, _legacyVolume = 50;
    private string _legacyGain = "Low", _legacyLed = "On", _legacyFilter = "Fast Roll-Off Low Latency";
    private ShellPage _page;
    private string _presetName = "Untitled";
    private static readonly LocalPreset FlatTarget = new(new Guid("00000000-0000-0000-0000-000000000002"), "Flat", "Built-in target", DateTimeOffset.MinValue, EqConfiguration.Flat());
    private LocalPreset? _selectedPreset, _reference = FlatTarget, _referenceChoice;
    private static readonly LocalPreset NoReference = new(Guid.Empty, "None", "", DateTimeOffset.MinValue, EqConfiguration.Flat());
    private bool _previewEq = true;
    private WorkspacePreferences _preferences = new();
    public event Action? AppearanceChanged;
    public event Action? FeedbackChanged;
    private Guid _editingPresetId;

    private MainViewModel(MoondropDeviceService? service, AppConfig config, bool demo, string? directory, bool loadWorkspace)
    {
        _service = service; _config = config; IsDemo = demo;
        _connected = service is not null; _session = service?.Device;
        _deviceName = service?.Selection.DisplayName ?? (demo ? "DAWN PRO2" : "No device");
        // Non-launch test/demo constructors get isolated storage; actual launches pass an explicit directory.
        _directory = directory ?? Path.Combine(Path.GetTempPath(), "Moondrop", Guid.NewGuid().ToString("N"));
        _workspacePath = Path.Combine(_directory, "workspace-v1.json");
        Bands = new(Enumerable.Range(0, 8).Select(i => new BandViewModel(i, 1000, 1, 0, PeqFilterType.Peaking, false)));
        Bands[0].IsSelected = true; Dialog = new(); Banner = new();
        LocalPresets.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ReferenceOptions)); OnPropertyChanged(nameof(ActiveConfiguration)); SyncPresetOptions(); OnPropertyChanged(nameof(HasNoLocalPresets)); };
        RefreshCommand = new RelayCommand(RefreshAsync, () => !IsBusy && !IsDemo);
        ApplyAllCommand = new RelayCommand(ApplyAsync, () => CanApply);
        SaveEqCommand = new RelayCommand(SaveAsync, () => CanSave); SaveGainsCommand = SaveEqCommand;
        ApplyPreGainCommand = ApplyAllCommand; ApplyGlobalGainCommand = ApplyAllCommand; ApplyLegacyCommand = ApplyAllCommand;
        ApplyActiveEqCommand = new RelayCommand(() => Task.CompletedTask, () => false);
        EnableCoefficientsCommand = new RelayCommand(() => Task.CompletedTask, () => false);
        SaveSettingsCommand = new RelayCommand(() => { Persist(); return Task.CompletedTask; });
        ShowDiagnosticsCommand = new RelayCommand(() => { new DiagnosticsWindow(DiagnosticsText()).Show(); return Task.CompletedTask; });
        ImportEqCommand = new RelayCommand(ImportEqAsync, () => !IsBusy);
        NewPresetCommand = new RelayCommand(NewPresetAsync, () => !IsBusy);
        SaveLocalCommand = new RelayCommand(() => { SaveLocalInteractive(); return Task.CompletedTask; }, () => !IsBusy);
        OpenPresetCommand = new RelayCommand(OpenSelectedPresetAsync, () => SelectedPreset is not null && !IsBusy);
        ApplyPresetCommand = new RelayCommand(ApplySelectedPresetAsync, () => SelectedPreset is not null && CanApplyPreset);
        ExportPresetCommand = new RelayCommand(() => { ExportSelected(); return Task.CompletedTask; }, () => SelectedPreset is not null && !IsBusy);
        RenamePresetCommand = new RelayCommand(() => { RenameSelected(); return Task.CompletedTask; }, () => SelectedPreset is not null && !IsBusy);
        DuplicatePresetCommand = new RelayCommand(() => { DuplicateSelected(); return Task.CompletedTask; }, () => SelectedPreset is not null && !IsBusy);
        DeletePresetCommand = new RelayCommand(DeleteSelectedAsync, () => SelectedPreset is not null && !IsBusy);
        DiscardCommand = new RelayCommand(() => { Discard(); return Task.CompletedTask; }, () => CanDiscard);
        ResetBandCommand = new RelayCommand(() => { Edit(() => Bands[SelectedBandIndex].Load(EqConfiguration.DefaultBand(SelectedBandIndex))); return Task.CompletedTask; }, () => !IsBusy);
        ResetEqCommand = new RelayCommand(() => { Edit(() => { foreach (var b in Bands) b.Load(EqConfiguration.DefaultBand(b.Index)); PreGain = 0; }); return Task.CompletedTask; }, () => !IsBusy);
        UndoCommand = new RelayCommand(() => { Undo(); return Task.CompletedTask; }, () => !IsBusy && _undo.Count > 0);
        RedoCommand = new RelayCommand(() => { Redo(); return Task.CompletedTask; }, () => !IsBusy && _redo.Count > 0);
        ClearReferenceCommand = new RelayCommand(() => { ReferencePreset = null; return Task.CompletedTask; });
        OpenObservedCommand = new RelayCommand(() => { if (_observed is not null && ProtectLocalEdits()) { OpenLocalPreset(new(Guid.Empty, ActiveConfiguration, "Device EQ", DateTimeOffset.UtcNow, _observed.Copy())); } return Task.CompletedTask; }, () => !IsBusy && _connected && _observed is not null);
        OpenLogsCommand = new RelayCommand(() => { Directory.CreateDirectory(_directory); Process.Start(new ProcessStartInfo(_directory) { UseShellExecute = true }); return Task.CompletedTask; });
        ExportLogsCommand = new RelayCommand(() => { ExportLogs(); return Task.CompletedTask; });
        ImportTargetCommand = new RelayCommand(ImportTargetAsync, () => !IsBusy);
        OpenGitHubCommand = new RelayCommand(() => { OpenLink("https://github.com/mohammed-just/DawnPro-GUI-windows"); return Task.CompletedTask; });
        CheckUpdatesCommand = new RelayCommand(() => { OpenLink("https://github.com/mohammed-just/DawnPro-GUI-windows/releases"); return Task.CompletedTask; });
        foreach (var band in Bands) { band.PropertyChanged += BandChanged; band.SetApplyHandler(_ => ApplyAsync()); }
        if (loadWorkspace) Restore();
        if (!_restoredBaseline) _localBaseline = Capture(); _lastEdit = Capture(); Recalculate();
        _legacyLocalBaseline = new(LegacyVolume, LegacyGain, LegacyFilter, LegacyLed);
    }
    public static MainViewModel CreateDemo(string? directory = null, bool loadWorkspace = false)
    {
        var model = new MainViewModel(null, new(), true, directory, loadWorkspace);
        if (!loadWorkspace)
        {
            model._suppress = true;
            var frequencies = new[] { 25, 105, 160, 1350, 1900, 3250, 5400, 11000 };
            var gains = new[] { 6.0, 4.5, -3, -2.2, 4.5, -3.8, -7, -4 };
            for (var i = 0; i < 8; i++) model.Bands[i].Load(new(i, frequencies[i], 1, gains[i], PeqFilterType.Peaking));
            model._suppress = false; model._localBaseline = model.Capture(); model._lastEdit = model.Capture(); model.Recalculate();
        }
        return model;
    }
    public static MainViewModel CreateOffline(string? directory = null) => new(null, new(), false, directory ?? WorkspaceStorage.DefaultDirectory, true);
    public static MainViewModel CreateHardware(MoondropDeviceService service, AppConfig config, bool configFileExists, string? configPath = null, string? workspaceDirectory = null)
    {
        var model = new MainViewModel(service, config, false, workspaceDirectory, workspaceDirectory is not null);
        model._configPath = configPath ?? AppConfig.DefaultConfigPath();
        if (!File.Exists(model._workspacePath))
        {
            if (service.Selection.Kind == DeviceKind.Legacy && configFileExists)
            { model._legacyVolume = config.DefaultSettings.DefaultVolume; model._legacyGain = config.DefaultSettings.DefaultGain; model._legacyLed = config.DefaultSettings.DefaultLedStatus; model._legacyFilter = config.DefaultSettings.DefaultFilter; }
            else if (service.Selection.Kind == DeviceKind.DawnPro2)
            { model._activeEq = config.DawnPro2Settings.DefaultEqIndex; model._preGain = config.DawnPro2Settings.DefaultPreGain; model._globalGain = config.DawnPro2Settings.DefaultGlobalGain; }
        }
        model._legacyLocalBaseline = new(model.LegacyVolume, model.LegacyGain, model.LegacyFilter, model.LegacyLed);
        model._lastEdit = model.Capture(); if (!model._restoredBaseline) model._localBaseline = model.Capture(); model.Recalculate(); return model;
    }

    public string Title => "Moondrop";
    public ObservableCollection<BandViewModel> Bands { get; }
    public ObservableCollection<LocalPreset> LocalPresets { get; } = [];
    public bool IsDemo { get; }
    public bool IsLegacy => _service?.Selection.Kind == DeviceKind.Legacy;
    public bool IsPro2 => !IsLegacy;
    public bool IsHardwareConnected => _connected;
    public bool IsBusy { get => _busy; private set { SetField(ref _busy, value); ChangedState(); } }
    public bool IsEditable => !IsBusy;
    public bool IsDirty => IsLegacy ? _legacyObserved is null || !LegacyMatches(_legacyObserved) : _observed is null ? !Capture().Same(_localBaseline) : !Capture().Same(_observed, true);
    public bool HasLocalEdits => IsLegacy ? _legacyLocalBaseline is null || !LegacyMatches(_legacyLocalBaseline) : !Capture().Same(_localBaseline);
    public string LocalSaveLabel => IsLegacy ? "Save local defaults" : "Save as preset";
    public bool HasInputErrors { get => _inputErrors; set { SetField(ref _inputErrors, value); ChangedState(); } }
    public bool CanApply => _connected && !IsBusy && !_uncertain && !HasInputErrors && (IsLegacy ? _legacyObserved is not null : _observed is not null) && IsDirty && (IsLegacy || ValidationMessage.Length == 0);
    public bool CanSave => _connected && !IsBusy && !IsLegacy && !_uncertain && !HasInputErrors && !IsDirty && (_eqPending || _gainPending);
    public bool CanApplyPreset => _connected && IsPro2 && !IsBusy && !_uncertain && !HasInputErrors && _observed is not null;
    public string PresetApplyHint => CanApplyPreset ? "Apply this preset to the active device EQ and check the result. Global gain is kept. This does not save to device memory." : !_connected ? "Connect a device to apply a preset." : IsLegacy ? "This device does not support parametric EQ presets." : HasInputErrors ? "Correct the marked numeric fields before applying a preset." : IsBusy ? "Wait for the current operation." : "Refresh the device before applying a preset.";
    public string ActiveEqLabel => IsPro2 && _connected && _observed is not null ? $"EQ {_activeEq}" : "";
    public bool HasActiveEq => ActiveEqLabel.Length > 0;
    public string ActiveEqExplanation => $"EQ {_activeEq} is the active EQ identifier reported by your device. It is separate from the eight filter bands and the presets saved on this PC. Apply updates this active configuration; it does not switch to another device EQ slot.";
    public string SaveToDeviceExplanation => "Apply changes updates the sound for the current device session. Save to device asks the DAC to store the applied EQ and gains in its own memory. Save as preset stores a file on this PC instead. Save-command completion does not verify persistence after unplugging.";
    public string ConnectionState => IsDemo ? "Demo · hardware disabled" : _connected ? "Connected" : "Disconnected · local workspace";
    public string HardwareActionHint => !_connected ? "Connect a device and Refresh to enable hardware actions." : _uncertain ? "Device state uncertain. Refresh to reconcile before retrying." : "Apply changes to RAM. Save flashes only the applied configuration.";
    public string DeviceSummary => $"{_deviceName} · {ConnectionState}";
    public string PersistenceSummary => IsLegacy ? "Legacy controls require Apply; flash persistence is unavailable." : $"EQ: {_eqPersistence} · Gains: {_gainPersistence}";
    public string DraftState => !_connected ? "Local draft — hardware unavailable" : _uncertain ? "Device state uncertain" : IsDirty ? "Unapplied changes" : _eqPending || _gainPending ? "Applied — not saved" : "Draft matches device RAM";
    public string ActiveConfiguration => _connected && _observed is not null ? $"EQ {_activeEq} — {LocalPresets.FirstOrDefault(p => p.Eq.SameFilters(_observed, true) && EqConfiguration.Encoded(p.Eq.PreGain) == EqConfiguration.Encoded(_observed.PreGain))?.Name ?? "Custom"}" : "Device EQ not loaded";
    public bool HasNoLocalPresets => LocalPresets.Count == 0;
    public bool IsPresetEdited => HasLocalEdits;
    public bool CanDiscard => !IsBusy && IsDirty;
    public ObservableCollection<PresetOption> PresetOptions { get; } = [new(Guid.Empty, "Unsaved EQ")];
    private bool _syncingPresetOptions;
    private void SyncPresetOptions()
    {
        var desired = new[] { new PresetOption(Guid.Empty, "Unsaved EQ") }
            .Concat(_connected && _observed is not null ? new[] { new PresetOption(new Guid("00000000-0000-0000-0000-000000000001"), ActiveConfiguration) } : Array.Empty<PresetOption>())
            .Concat(LocalPresets.Select(p => new PresetOption(p.Id, p.Name))).ToArray();
        _syncingPresetOptions = true;
        try {
            for (var i = PresetOptions.Count - 1; i >= 0; i--) if (!desired.Any(p => p.Id == PresetOptions[i].Id)) PresetOptions.RemoveAt(i);
            foreach (var option in desired) {
                var existing = PresetOptions.FirstOrDefault(p => p.Id == option.Id);
                if (existing is null) PresetOptions.Add(option);
                else if (existing.Name != option.Name) PresetOptions[PresetOptions.IndexOf(existing)] = option;
            }
        } finally { _syncingPresetOptions = false; }
        OnPropertyChanged(nameof(EditingPresetId));
    }
    public Guid EditingPresetId { get => _editingPresetId; set {
        if (_editingPresetId == value || IsBusy || _syncingPresetOptions) return;
        if (value == Guid.Empty) { if (NewPresetCommand.CanExecute(null)) NewPresetCommand.Execute(null); }
        else if (value == new Guid("00000000-0000-0000-0000-000000000001")) { if (OpenObservedCommand.CanExecute(null)) OpenObservedCommand.Execute(null); }
        else if (LocalPresets.FirstOrDefault(p => p.Id == value) is { } preset && ProtectLocalEdits()) OpenLocalPreset(preset);
        OnPropertyChanged();
    } }
    public string StateTitle => IsBusy ? "Device operation in progress" : !_connected ? (IsDemo ? "Demo · local editing" : "Disconnected · local editing")
        : _uncertain ? "Device values need checking" : IsDirty ? "Unapplied changes" : _eqPending || _gainPending ? "Changes applied · not saved"
        : _eqPersistence.StartsWith("Command completed") || _gainPersistence.StartsWith("Command completed") ? "Save command completed" : "Changes match device";
    public string StateDescription => !_connected ? "Connect a device to apply your EQ." : _uncertain ? "Refresh the device before applying or saving again."
        : IsDirty ? "Your edits are only in the local preview." : _eqPending || _gainPending ? "Active on the device. Save to keep the applied changes."
        : _eqPersistence.StartsWith("Command completed") || _gainPersistence.StartsWith("Command completed") ? "Save requested; persistence after power loss is not verified." : "Current values read from the device; previous saves are not verified.";
    public string SaveActionHint => ( !_connected ? "Connect a device to save." : IsLegacy ? "Permanent saving is unavailable for this device." : _uncertain ? "Refresh to check the device before saving." : IsDirty ? "Apply changes before saving to the device." : !CanSave ? "No newly applied changes to save." : "Save the applied EQ and gains to device memory.") + "\n\n" + SaveToDeviceExplanation;
    public string ApplyActionHint => !_connected ? "Connect a device to apply changes." : _uncertain ? "Refresh to check the device before applying." : ValidationMessage.Length > 0 ? ValidationMessage : !IsDirty ? "No changes to apply." : "Apply your local edits and read back the device values.";
    public string TargetLabel => ReferenceName;
    public bool HasHeadroom => _peak + PreGain <= 0;
    public string HeadroomValue => FormatDb(-(_peak + PreGain));
    public string RecommendedPreGainText => FormatDb(RecommendedPreGain);
    public string AutoProtectionLabel => AutoPreamp ? "Auto protection on" : "Auto protection off";
    private static string FormatDb(double value) => $"{(Math.Abs(value) < 0.05 ? 0 : value):0.0} dB";
    public string ValidationMessage { get { if (HasInputErrors) return "Correct the marked numeric fields before Apply or Save."; try { Capture().Validate(); return ""; } catch (Exception ex) { return ex.Message; } } }
    public DialogState Dialog { get; }
    public StatusBannerState Banner { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ApplyAllCommand { get; }
    public ICommand SaveEqCommand { get; }
    public ICommand SaveGainsCommand { get; }
    public ICommand ApplyPreGainCommand { get; }
    public ICommand ApplyGlobalGainCommand { get; }
    public ICommand ApplyActiveEqCommand { get; }
    public ICommand EnableCoefficientsCommand { get; }
    public ICommand ApplyLegacyCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ShowDiagnosticsCommand { get; }
    public ICommand ImportEqCommand { get; }
    public ICommand NewPresetCommand { get; }
    public ICommand SaveLocalCommand { get; }
    public ICommand OpenPresetCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand ExportPresetCommand { get; }
    public ICommand RenamePresetCommand { get; }
    public ICommand DuplicatePresetCommand { get; }
    public ICommand DeletePresetCommand { get; }
    public ICommand ResetBandCommand { get; }
    public ICommand ResetEqCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ClearReferenceCommand { get; }
    public ICommand OpenObservedCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ExportLogsCommand { get; }
    public ICommand ImportTargetCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ShellPage SelectedPage { get => _page; set => SetField(ref _page, value is ShellPage.Device or ShellPage.About ? ShellPage.Settings : value); }
    public string Status { get => _status; set { if (SetField(ref _status, value)) FeedbackChanged?.Invoke(); if (DebugLogging) Log(value); } }
    public string SelectedDevice { get => _deviceName; set => SetField(ref _deviceName, value); }
    public string FirmwareVersion { get => _firmware; private set => SetField(ref _firmware, value); }
    public int ActiveEq { get => _activeEq; set => SetField(ref _activeEq, value); }
    public string PresetName { get => _presetName; private set => SetField(ref _presetName, value); }
    public LocalPreset? SelectedPreset { get => _selectedPreset; set { SetField(ref _selectedPreset, value); RaiseCommands(); } }
    public int SelectedBandIndex { get => _selectedBandIndex; set { if (SetField(ref _selectedBandIndex, Math.Clamp(value, 0, 7))) { foreach (var b in Bands) b.IsSelected = b.Index == _selectedBandIndex; OnPropertyChanged(nameof(SelectedBand)); } } }
    public BandViewModel SelectedBand => Bands[SelectedBandIndex];
    public double PreGain { get => _preGain; set { if (!_suppress && IsBusy || !double.IsFinite(value) || value is < -18 or > 12) return; if (SetField(ref _preGain, value)) { if (!_suppress && AutoPreamp) { _preferences = _preferences with { AutoPreamp = false }; OnPropertyChanged(nameof(AutoPreamp)); OnPropertyChanged(nameof(AutoProtectionLabel)); Status = "Auto preamp protection turned off after your manual adjustment."; } DraftEdited(); } } }
    public double GlobalGain { get => _globalGain; set { if (!_suppress && IsBusy || !double.IsFinite(value) || value is < -18 or > 12) return; if (SetField(ref _globalGain, value)) DraftEdited(); } }
    public int LegacyVolume { get => _legacyVolume; set { if (!IsBusy && SetField(ref _legacyVolume, Math.Clamp(value, 0, 60))) LegacyEdited(); } }
    public string LegacyGain { get => _legacyGain; set { if (!IsBusy && LegacyGains.Contains(value) && SetField(ref _legacyGain, value)) LegacyEdited(); } }
    public string LegacyLed { get => _legacyLed; set { if (!IsBusy && LegacyLeds.Contains(value) && SetField(ref _legacyLed, value)) LegacyEdited(); } }
    public string LegacyFilter { get => _legacyFilter; set { if (!IsBusy && LegacyFilters.Contains(value) && SetField(ref _legacyFilter, value)) LegacyEdited(); } }
    public bool PreviewEq { get => _previewEq; set => SetField(ref _previewEq, value); }
    public double RecommendedPreGain => _peak <= 0 ? 0 : -Math.Ceiling(_peak * 256) / 256;
    public string HeadroomText => $"Recommended pre-gain: {RecommendedPreGainText} · Headroom: {HeadroomValue}";
    public string HeadroomDetail => AutoPreamp && _peak > 18 ? "Required attenuation exceeds −18 dB: unresolved headroom." : "EQ-only estimate; excludes global gain. No automatic level increase.";
    public string ReferenceName => _reference?.Name ?? "None";
    public IReadOnlyList<LocalPreset> ReferenceOptions => new[] { NoReference, FlatTarget }.Concat(LocalPresets.Select(p => p.Id == _reference?.Id ? _reference : p)).Concat(_reference is not null && _reference.Id != FlatTarget.Id && LocalPresets.All(p => p.Id != _reference.Id) ? new[] { _reference } : Array.Empty<LocalPreset>()).ToArray();
    public Guid ReferenceId { get => _reference?.Id ?? Guid.Empty; set { if (value == ReferenceId) return; ReferencePreset = value == Guid.Empty ? null : value == FlatTarget.Id ? FlatTarget : LocalPresets.FirstOrDefault(p => p.Id == value); } }
    public LocalPreset? ReferenceChoice { get => _referenceChoice; set { if (SetField(ref _referenceChoice, value)) ReferencePreset = value; } }
    public LocalPreset? ReferencePreset { get => _reference; set { _reference = value is null ? null : value with { Eq = value.Eq.Copy() }; _preferences = _preferences with { NoTarget = value is null }; if (value is null) { _referenceChoice = null; OnPropertyChanged(nameof(ReferenceChoice)); } OnPropertyChanged(); OnPropertyChanged(nameof(ReferenceName)); OnPropertyChanged(nameof(ReferenceId)); OnPropertyChanged(nameof(ReferenceOptions)); OnPropertyChanged(nameof(ReferenceBands)); OnPropertyChanged(nameof(TargetLabel)); Persist(); } }
    public IReadOnlyList<BandViewModel> ReferenceBands => (_reference?.Eq.Bands ?? []).Select(b => new BandViewModel(b.Index, b.Frequency, b.Q, b.Gain, b.FilterType, b.Enabled)).ToArray();
    public string ThemeSelection { get => _preferences.Theme; set { _preferences = _preferences with { Theme = value }; PreferenceChanged(nameof(ThemeSelection), true); } }
    public bool SystemAccent { get => _preferences.SystemAccent; set { _preferences = _preferences with { SystemAccent = value }; PreferenceChanged(nameof(SystemAccent), true); } }
    public bool ReconnectAutomatically { get => _preferences.Reconnect; set { _preferences = _preferences with { Reconnect = value }; PreferenceChanged(nameof(ReconnectAutomatically)); } }
    public bool ConfirmFlash { get => _preferences.ConfirmFlash; set { _preferences = _preferences with { ConfirmFlash = value }; PreferenceChanged(nameof(ConfirmFlash)); } }
    public bool RememberEq { get => _preferences.RememberEq; set { _preferences = _preferences with { RememberEq = value }; PreferenceChanged(nameof(RememberEq)); } }
    public bool IndividualCurves { get => _preferences.IndividualCurves; set { _preferences = _preferences with { IndividualCurves = value }; PreferenceChanged(nameof(IndividualCurves)); } }
    public bool ShowReferenceCurve { get => _preferences.ReferenceCurve; set { _preferences = _preferences with { ReferenceCurve = value }; PreferenceChanged(nameof(ShowReferenceCurve)); } }
    public bool DebugLogging { get => _preferences.DebugLogging; set { _preferences = _preferences with { DebugLogging = value }; PreferenceChanged(nameof(DebugLogging)); } }
    public bool AutoRefreshOnConnect { get => _preferences.AutoRefresh; set { _preferences = _preferences with { AutoRefresh = value }; PreferenceChanged(nameof(AutoRefreshOnConnect)); } }
    public bool AutoPreamp { get => _preferences.AutoPreamp; set { if (IsBusy) return; _preferences = _preferences with { AutoPreamp = value }; if (value) Edit(ProtectPreamp); PreferenceChanged(nameof(AutoPreamp)); OnPropertyChanged(nameof(AutoProtectionLabel)); } }
    public string AppVersion => $"Moondrop {typeof(MainViewModel).Assembly.GetName().Version} · native WPF / .NET 10";
    public EqConfiguration Capture() => new(Bands.Select(b => b.ToCoreBand()).ToArray(), PreGain, GlobalGain);
    public async Task InitializeHardwareAsync() { if (_initialized) return; _initialized = true; if (_service is not null && AutoRefreshOnConnect) await RefreshAsync(); }
    public void UpdateBand(int index, int frequency, double gain, double q) => Edit(() => { Bands[index].Frequency = frequency; Bands[index].Gain = gain; Bands[index].Q = q; });
    private void BandChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is not (nameof(BandViewModel.IsSelected) or nameof(BandViewModel.IsEditable))) DraftEdited(); }
    private void DraftEdited()
    {
        if (_suppress) return;
        if (_editGroup > 0) { _lastEdit = Capture(); ChangedState(); return; }
        if (_editGroup == 0) { _undo.Push(_lastEdit.Copy()); _redo.Clear(); }
        _suppress = true; try { if (AutoPreamp) ProtectPreamp(); } finally { _suppress = false; }
        _lastEdit = Capture(); Recalculate(); ChangedState(); if (_editGroup == 0) Persist();
    }
    private void LegacyEdited() { if (_suppress) return; ChangedState(); Persist(); }
    public void BeginEdit() { if (_editGroup++ == 0) _groupStart = Capture(); }
    public void EndEdit()
    {
        if (_editGroup == 0 || --_editGroup > 0) return;
        _suppress = true; try { if (AutoPreamp) ProtectPreamp(); } finally { _suppress = false; }
        if (_groupStart is not null && !_groupStart.Same(Capture())) { _undo.Push(_groupStart); _redo.Clear(); }
        _groupStart = null; _lastEdit = Capture(); Recalculate(); ChangedState(); Persist();
    }
    public void Edit(Action action) { BeginEdit(); try { action(); } finally { EndEdit(); } }
    public void Undo() { if (_undo.Count == 0 || IsBusy) return; _redo.Push(Capture()); LoadDraft(_undo.Pop()); }
    public void Redo() { if (_redo.Count == 0 || IsBusy) return; _undo.Push(Capture()); LoadDraft(_redo.Pop()); }
    private void LoadDraft(EqConfiguration eq)
    {
        _suppress = true;
        try { foreach (var b in eq.Bands) Bands[b.Index].Load(b); _preGain = eq.PreGain; _globalGain = eq.GlobalGain; }
        finally { _suppress = false; }
        OnPropertyChanged(nameof(PreGain)); OnPropertyChanged(nameof(GlobalGain)); _lastEdit = Capture(); Recalculate(); ChangedState(); if (_editGroup == 0) Persist();
    }
    private void Recalculate() { _peak = EqHeadroom.Peak(Bands.Select(b => b.ToCoreBand())); OnPropertyChanged(nameof(HeadroomText)); OnPropertyChanged(nameof(HeadroomDetail)); OnPropertyChanged(nameof(HasHeadroom)); OnPropertyChanged(nameof(HeadroomValue)); OnPropertyChanged(nameof(RecommendedPreGainText)); }
    private void ProtectPreamp()
    {
        _peak = EqHeadroom.Peak(Bands.Select(b => b.ToCoreBand())); var needed = Math.Max(-18, RecommendedPreGain);
        if (_preGain > needed) { _preGain = needed; OnPropertyChanged(nameof(PreGain)); Status = $"Pre-gain reduced to {FormatDb(needed)} to prevent EQ clipping. Undo restores the edit."; }
    }
    private void PreferenceChanged(string property, bool appearance = false) { OnPropertyChanged(property); Persist(); if (appearance) AppearanceChanged?.Invoke(); }
    private void ChangedState()
    {
        foreach (var property in new[] { nameof(IsDirty), nameof(CanApply), nameof(CanSave), nameof(CanApplyPreset), nameof(PresetApplyHint), nameof(ActiveEqLabel), nameof(HasActiveEq), nameof(ActiveEqExplanation), nameof(IsEditable), nameof(IsHardwareConnected), nameof(IsLegacy), nameof(IsPro2), nameof(LocalSaveLabel), nameof(ConnectionState), nameof(DeviceSummary), nameof(DraftState), nameof(PersistenceSummary), nameof(HardwareActionHint), nameof(ActiveConfiguration), nameof(ValidationMessage), nameof(StateTitle), nameof(StateDescription), nameof(SaveActionHint), nameof(ApplyActionHint), nameof(IsPresetEdited), nameof(CanDiscard), nameof(EditingPresetId) }) OnPropertyChanged(property);
        foreach (var band in Bands) band.IsEditable = !IsBusy; RaiseCommands();
    }
    private void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, ApplyAllCommand, SaveEqCommand, ImportEqCommand, NewPresetCommand, SaveLocalCommand, OpenPresetCommand, ApplyPresetCommand, OpenObservedCommand, ExportPresetCommand, RenamePresetCommand, DuplicatePresetCommand, DeletePresetCommand, ResetBandCommand, ResetEqCommand, DiscardCommand, UndoCommand, RedoCommand, ImportTargetCommand }) (command as RelayCommand)?.RaiseCanExecuteChanged();
    }
    public async Task RefreshAsync()
    {
        if (IsBusy || IsDemo) return;
        IsBusy = true;
        try
        {
            if (_service is null) { _service = await MoondropDeviceService.SelectAsync(new HardwareDeviceFactory(), _config); _session = null; _observed = null; _legacyObserved = null; }
            var dirty = IsDirty || _observed is null;
            if (IsPro2)
            {
                var snapshot = await _service.RefreshPro2Async();
                if (!ReferenceEquals(_session, _service.Device)) { _eqPending = _gainPending = false; _eqPersistence = _gainPersistence = "Unknown (new connection session)"; dirty = true; }
                _session = _service.Device;
                if (_observed is not null && !new EqConfiguration(snapshot.Bands.ToArray(), snapshot.PreGain, snapshot.GlobalGain).Same(_observed, true))
                {
                    var fresh = new EqConfiguration(snapshot.Bands.ToArray(), snapshot.PreGain, snapshot.GlobalGain);
                    if (!fresh.SameFilters(_observed, true)) { _eqPending = false; _eqPersistence = "Unknown (external RAM change observed)"; }
                    if (!fresh.SameGains(_observed, true)) { _gainPending = false; _gainPersistence = "Unknown (external RAM change observed)"; }
                }
                Observe(snapshot); if (!dirty) LoadDraft(_observed!);
            }
            else { _legacyObserved = await _service.RefreshLegacyAsync(); _session = _service.Device; if (!dirty) LoadLegacy(_legacyObserved); }
            _deviceName = _service.Selection.DisplayName; _connected = true; _uncertain = false;
            Status = "Device values refreshed. Your local edits were kept.";
        }
        catch (Exception ex)
        {
            _connected = false; _uncertain = true; _observed = null; _legacyObserved = null;
            _eqPending = _gainPending = false; _eqPersistence = _gainPersistence = "Unknown (connection lost)";
            ShowError("Refresh failed", ex);
            if (_service is not null) { await _service.DisposeAsync(); _service = null; _session = null; }
        }
        finally { IsBusy = false; }
    }
    public async Task TryReconnectAsync()
    {
        if (!ReconnectAutomatically || _connected || IsBusy || IsDemo) return;
        if (AutoRefreshOnConnect) { await RefreshAsync(); return; }
        IsBusy = true;
        try
        {
            if (_service is not null) await _service.DisposeAsync();
            _service = await MoondropDeviceService.SelectAsync(new HardwareDeviceFactory(), _config);
            _session = _service.Device; _observed = null; _legacyObserved = null;
            _eqPending = _gainPending = false; _eqPersistence = _gainPersistence = "Unknown (new connection session)";
            _connected = true; _uncertain = false; _deviceName = _service.Selection.DisplayName;
            Status = "Device connected. Auto refresh is off; use Refresh before Apply. Draft preserved.";
        }
        catch (Exception ex) { _connected = false; ShowError("Reconnect failed", ex); }
        finally { IsBusy = false; }
    }
    private void Observe(Pro2Snapshot snapshot) { FirmwareVersion = snapshot.FirmwareVersion; ActiveEq = snapshot.ActiveEq; _observed = new(snapshot.Bands.OrderBy(x => x.Index).ToArray(), snapshot.PreGain, snapshot.GlobalGain); SyncPresetOptions(); }
    public async Task ApplyAsync()
    {
        if (!CanApply || _service is null || _session is null) return;
        var desired = Capture(); var session = _session; var previous = _observed; var active = _activeEq;
        IsBusy = true;
        try
        {
            if (IsLegacy)
            {
                var ok = await _service.SetLegacyVolumeAsync(LegacyVolume); ok &= await _service.SetLegacyGainAsync(LegacyGain);
                ok &= await _service.SetLegacyLedAsync(LegacyLed); ok &= await _service.SetLegacyFilterAsync(LegacyFilter);
                _legacyObserved = await _service.RefreshLegacyAsync();
                if (!ok || !LegacyMatches(_legacyObserved)) throw new IOException("Legacy write or readback mismatch; desired controls retained.");
                Status = "Legacy controls applied and read back. Persistence unverified.";
            }
            else
            {
                desired.Validate(); var filters = !desired.SameFilters(previous!, true); var gains = !desired.SameGains(previous!, true);
                if (filters) { _eqPending = true; _eqPersistence = "Applied — not saved (readback pending)"; }
                if (gains) { _gainPending = true; _gainPersistence = "Applied — not saved (readback pending)"; }
                var read = await _service.ApplyWorkspaceAsync(session, desired, filters, gains); Observe(read);
                if (!desired.Same(_observed!, true) || read.ActiveEq != active) throw new IOException("RAM readback differs from requested values. Device state uncertain; draft retained.");
                _uncertain = false; if (filters) _eqPersistence = "Applied — not saved"; if (gains) _gainPersistence = "Applied — not saved";
                Status = "Changes applied and checked. Save to keep them on the device.";
            }
        }
        catch (Exception ex)
        {
            _uncertain = true; ShowError("Apply failed — desired draft retained", ex);
            if (_eqPending) _eqPersistence = "Device state uncertain — reconcile before saving";
            if (_gainPending) _gainPersistence = "Device state uncertain — reconcile before saving";
            try { if (IsPro2 && ReferenceEquals(session, _service.Device) && _service.Device is IDawnPro2Device { IsUsable: true }) Observe(await _service.RefreshPro2Async()); }
            catch { _connected = false; }
            if (_service.Device is IDawnPro2Device { IsUsable: false }) _connected = false;
        }
        finally { IsBusy = false; }
    }
    public async Task SaveAsync()
    {
        if (!CanSave || _service is null || _session is null) return;
        IsBusy = true;
        try
        {
            if (ConfirmFlash && !await Dialog.AskAsync("Save applied configuration to device", $"Flash destination: {_deviceName}. Groups: {(_eqPending ? "EQ " : "")}{(_gainPending ? "gains" : "")}. This saves applied RAM values. Persistence after power loss cannot be verified by this command.", "Save to device")) return;
            var result = await _service.SaveWorkspaceAsync(_session, Capture(), _eqPending, _gainPending);
            if (_eqPending) { _eqPersistence = result.Eq; _eqPending = result.Eq.StartsWith("Failed"); }
            if (_gainPending) { _gainPersistence = result.Gains; _gainPending = result.Gains.StartsWith("Failed"); }
            Status = "Save command completed. Persistence after power loss has not been verified.";
            if (_eqPending || _gainPending) { _uncertain = true; Banner.ShowError("Some changes could not be saved", "Refresh the device before retrying. Detailed save results are in Diagnostics."); }
        }
        catch (Exception ex) { _uncertain = true; ShowError("Save failed", ex); }
        finally { IsBusy = false; }
    }
    private bool LegacyMatches(LegacySnapshot state) => state.Volume == LegacyVolume && state.Gain == LegacyGain && state.Filter == LegacyFilter && state.LedStatus == LegacyLed;
    private void LoadLegacy(LegacySnapshot state)
    {
        _legacyVolume = state.Volume ?? _legacyVolume; _legacyGain = state.Gain ?? _legacyGain; _legacyFilter = state.Filter ?? _legacyFilter; _legacyLed = state.LedStatus ?? _legacyLed;
        foreach (var p in new[] { nameof(LegacyVolume), nameof(LegacyGain), nameof(LegacyFilter), nameof(LegacyLed) }) OnPropertyChanged(p);
    }
    public void Discard()
    {
        if (IsLegacy && _legacyObserved is not null) LoadLegacy(_legacyObserved); else Edit(() => LoadDraft((_connected ? _observed : null) ?? _localBaseline));
        Status = "Local edits discarded. Previous values restored."; ChangedState(); Persist();
    }
    public void OpenLocalPreset(LocalPreset preset)
    {
        var global = GlobalGain; Edit(() => LoadDraft(preset.Eq with { GlobalGain = global }));
        _localBaseline = Capture(); PresetName = preset.Name; _editingPresetId = preset.Id == Guid.Empty ? new Guid("00000000-0000-0000-0000-000000000001") : preset.Id; SelectedPreset = preset; SelectedPage = ShellPage.Eq;
        Status = "Preset opened. Apply changes when you are ready."; Persist(); ChangedState();
    }
    public LocalPreset SaveLocal(string name)
    {
        var eq = Capture() with { GlobalGain = 0 }; eq.Validate();
        var preset = new LocalPreset(Guid.NewGuid(), CleanName(name), "Created locally", DateTimeOffset.UtcNow, eq); LocalPresets.Add(preset);
        if (!Persist()) { LocalPresets.Remove(preset); throw new IOException("Preset could not be saved; original storage retained."); }
        _localBaseline = Capture(); PresetName = preset.Name; _editingPresetId = preset.Id == Guid.Empty ? new Guid("00000000-0000-0000-0000-000000000001") : preset.Id; SelectedPreset = preset; ChangedState(); Persist(); return preset;
    }
    private static string CleanName(string name) => string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim()[..Math.Min(100, name.Trim().Length)];
    private bool ProtectLocalEdits()
    {
        if (!HasLocalEdits) return true;
        var choice = MessageBox.Show("Save the current draft as a local preset before opening another?\nYes: Save locally · No: Discard draft · Cancel: Keep editing", "Unstored draft edits", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false; return choice != MessageBoxResult.Yes || SaveLocalInteractive();
    }
    private bool SaveLocalInteractive()
    {
        if (IsLegacy)
        {
            try
            {
                WorkspaceStorage.AtomicWrite(_configPath, _config.WithLegacyDefaults(LegacyVolume, LegacyGain, LegacyLed, LegacyFilter).SaveJson());
                _legacyLocalBaseline = new(LegacyVolume, LegacyGain, LegacyFilter, LegacyLed);
                Status = "Legacy defaults saved locally; startup will not apply them."; return true;
            }
            catch (Exception ex) { ShowError("Local defaults save failed", ex); return false; }
        }
        var name = LocalDialogs.AskName("Save local preset", PresetName); if (name is null) return false;
        try { SaveLocal(name); Status = "Local preset saved. Global gain remains device-specific."; return true; }
        catch (Exception ex) { ShowError("Local save failed", ex); return false; }
    }
    private Task OpenSelectedPresetAsync() { if (SelectedPreset is { } p && ProtectLocalEdits()) OpenLocalPreset(p); return Task.CompletedTask; }
    public async Task ApplySelectedPresetAsync()
    {
        if (!CanApplyPreset || SelectedPreset is not { } preset) return;
        try { preset.Eq.Validate(); }
        catch (Exception ex) { ShowError("Preset could not be applied", ex); return; }
        if (!ProtectLocalEdits()) return;
        var page = SelectedPage;
        OpenLocalPreset(preset);
        SelectedPage = page;
        if (CanApply) await ApplyAsync();
        else Status = "This preset already matches the active device EQ. Nothing was written.";
    }
    private Task NewPresetAsync()
    {
        if (!ProtectLocalEdits()) return Task.CompletedTask;
        var global = GlobalGain; Edit(() => LoadDraft(EqConfiguration.Flat() with { GlobalGain = global }));
        PresetName = "Untitled"; _editingPresetId = Guid.Empty; _localBaseline = Capture(); ChangedState(); Status = "New EQ ready. Your global gain was kept."; SelectedPage = ShellPage.Eq; Persist(); return Task.CompletedTask;
    }
    public Task ImportEqAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Native or APO subset (*.json;*.txt;*.peq)|*.json;*.txt;*.peq|All files|*.*" }; if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try
        {
            var preset = WorkspaceStorage.Import(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName)); var result = LocalDialogs.PreviewImport(preset); if (result is null) return Task.CompletedTask;
            preset = preset with { Name = CleanName(result.Value.Name) }; LocalPresets.Add(preset);
            if (!Persist()) { LocalPresets.Remove(preset); return Task.CompletedTask; }
            SelectedPreset = preset; Status = "Import saved locally; unused positions disabled. Device unchanged.";
            if (result.Value.Open && ProtectLocalEdits()) OpenLocalPreset(preset);
        }
        catch (Exception ex) { ShowError("Import failed", ex); }
        return Task.CompletedTask;
    }
    private void ExportSelected()
    {
        if (SelectedPreset is not { } p) return;
        var dialog = new SaveFileDialog { Filter = "Native preset (*.json)|*.json|APO supported subset (*.txt)|*.txt", FileName = "preset", DefaultExt = ".json" }; if (dialog.ShowDialog() != true) return;
        try { WorkspaceStorage.AtomicWrite(dialog.FileName, dialog.FilterIndex == 2 ? WorkspaceStorage.ExportApo(p) : WorkspaceStorage.ExportNative(p)); Status = $"Exported {p.Name} to {dialog.FileName}. APO omits global gain and metadata."; }
        catch (Exception ex) { ShowError("Export failed", ex); }
    }
    private void RenameSelected()
    {
        if (SelectedPreset is not { } p) return; var name = LocalDialogs.AskName("Rename local preset", p.Name); if (name is null) return;
        var index = LocalPresets.IndexOf(p); var changed = p with { Name = CleanName(name), UpdatedAt = DateTimeOffset.UtcNow }; LocalPresets[index] = changed;
        if (!Persist()) LocalPresets[index] = p; else { SelectedPreset = changed; if (_editingPresetId == p.Id) PresetName = changed.Name; ChangedState(); }
    }
    private void DuplicateSelected() { if (SelectedPreset is not { } p) return; var duplicate = p with { Id = Guid.NewGuid(), Name = p.Name + " copy", Eq = p.Eq.Copy(), UpdatedAt = DateTimeOffset.UtcNow }; LocalPresets.Add(duplicate); if (!Persist()) LocalPresets.Remove(duplicate); else SelectedPreset = duplicate; }
    private async Task DeleteSelectedAsync()
    {
        if (SelectedPreset is not { } p || !await Dialog.AskAsync("Delete local preset", $"Delete “{p.Name}” from this PC? Device memory and open draft are unaffected.", "Delete locally")) return;
        var index = LocalPresets.IndexOf(p); LocalPresets.Remove(p);
        if (!Persist()) LocalPresets.Insert(index, p);
        else { SelectedPreset = null; if (_editingPresetId == p.Id) { _editingPresetId = Guid.Empty; PresetName = "Untitled"; } ChangedState(); Persist(); }
    }
    private void Restore()
    {
        try
        {
            var file = WorkspaceStorage.Load(_workspacePath); _preferences = file.Preferences;
            foreach (var p in file.Presets) LocalPresets.Add(p); _reference = file.Reference ?? (_preferences.NoTarget ? null : FlatTarget);
            if (RememberEq && file.Draft is not null && file.LocalBaseline is { } baseline) { _localBaseline = baseline.Copy(); _restoredBaseline = true; }
            if (RememberEq && file.Draft is { Bands.Length: 8 } draft) { _suppress = true; foreach (var b in draft.Bands) Bands[b.Index].Load(b); _preGain = draft.PreGain; _globalGain = draft.GlobalGain; _suppress = false; _presetName = file.DraftName; _legacyVolume = file.LegacyVolume; _legacyGain = file.LegacyGain; _legacyLed = file.LegacyLed; _legacyFilter = file.LegacyFilter; _editingPresetId = LocalPresets.FirstOrDefault(p => p.Name == _presetName)?.Id ?? Guid.Empty; }
        }
        catch (Exception ex) { _suppress = false; _storageHealthy = false; ShowError("Workspace could not be loaded; original file protected", ex); }
    }
    public bool Persist()
    {
        if (!_storageHealthy) return false;
        try { WorkspaceStorage.Save(_workspacePath, new(1, _preferences, LocalPresets.ToArray(), RememberEq ? Capture() : null, PresetName, _reference, LegacyVolume, LegacyGain, LegacyLed, LegacyFilter, _localBaseline.Copy())); return true; }
        catch (Exception ex) { ShowError("Workspace save failed — original file retained", ex); return false; }
    }
    public bool PrepareClose() { if (IsBusy) { Status = "Wait for the active device operation before closing."; return false; } if (RememberEq && Persist()) return true; return ProtectLocalEdits(); }
    private void Log(string message) { try { Directory.CreateDirectory(_directory); File.AppendAllText(Path.Combine(_directory, "workspace.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { } }
    private void ExportLogs()
    {
        var dialog = new SaveFileDialog { Filter = "Diagnostics text (*.txt)|*.txt", FileName = "moondrop-diagnostics.txt" }; if (dialog.ShowDialog() != true) return;
        try { var path = Path.Combine(_directory, "workspace.log"); WorkspaceStorage.AtomicWrite(dialog.FileName, DiagnosticsText() + "\n" + (File.Exists(path) ? File.ReadAllText(path) : "Debug logging has no entries.")); Status = "Diagnostics/logs exported."; }
        catch (Exception ex) { ShowError("Log export failed", ex); }
    }
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Could not open the browser", ex); }
    }
    private string DiagnosticsText() => DeviceDiagnostics.CollectText() + $"\n\nWorkspace\n{DeviceSummary}\nActive EQ: {ActiveConfiguration}\n{PersistenceSummary}\n{Status}\nInactive device slots and hardware bypass have not been established. Targets compare EQ filter shapes; acoustic response matching requires measurement data.";
    public Task ImportTargetAsync()
    {
        var dialog = new OpenFileDialog { Title = "Import EQ target", Filter = "EQ presets (*.json;*.txt;*.peq)|*.json;*.txt;*.peq" };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try {
            var target = WorkspaceStorage.Import(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            LocalPresets.Add(target);
            if (!Persist()) { LocalPresets.Remove(target); return Task.CompletedTask; }
            ReferencePreset = target; ShowReferenceCurve = true;
            Status = "Target imported. Your edited EQ and device were kept unchanged.";
        } catch (Exception ex) { ShowError("Target import failed", ex); }
        return Task.CompletedTask;
    }
    private void ShowError(string title, Exception ex) { Status = title + ": " + ex.Message; Banner.ShowError(title, ex.Message); }
    public void Dispose() => _service?.Dispose();
    public async ValueTask DisposeAsync() { if (_service is not null) await _service.DisposeAsync(); }
}
