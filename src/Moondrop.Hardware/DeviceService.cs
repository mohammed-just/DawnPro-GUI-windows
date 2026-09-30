using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Protocol;
using Moondrop.Core.Eq;

namespace Moondrop.Hardware;

public interface IMoondropDeviceFactory
{
    Task<IMoondropDevice> CreateDawnPro2Async(CancellationToken cancellationToken);
    Task<IMoondropDevice> CreateLegacyAsync(AppConfig config, CancellationToken cancellationToken);
}

public sealed class MoondropDeviceService : IDisposable, IAsyncDisposable
{
    private readonly SerializedDeviceQueue _queue = new();
    private int _disposed;
    private BackendSelection<IMoondropDevice> _selection;
    private Func<CancellationToken, Task<IMoondropDevice>>? _reconnectFactory;

    public MoondropDeviceService(BackendSelection<IMoondropDevice> selection)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public BackendSelection<IMoondropDevice> Selection => _selection;
    public IMoondropDevice Device => _selection.Device;

    // UI operations pin the observed connection object; writes never silently reopen another device.
    public Task<Pro2Snapshot> ApplyWorkspaceAsync(IMoondropDevice session, EqConfiguration expected, bool filters, bool gains, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            expected.Validate();
            var device = RequireSession(session);
            if (gains) await device.WritePreGainAsync(expected.PreGain, false, token).ConfigureAwait(false);
            if (filters) await device.WriteAllBandsAsync(expected.Bands, false, token).ConfigureAwait(false);
            if (gains) await device.WriteGlobalGainAsync(expected.GlobalGain, false, token).ConfigureAwait(false);
            return await ReadWorkspaceAsync(device, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<FlashOutcome> SaveWorkspaceAsync(IMoondropDevice session, EqConfiguration expected, bool filters, bool gains, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            var device = RequireSession(session);
            var current = await ReadWorkspaceAsync(device, token).ConfigureAwait(false);
            var state = new EqConfiguration(current.Bands.ToArray(), current.PreGain, current.GlobalGain);
            if (!expected.Same(state, encoded: true)) throw new InvalidOperationException("Device RAM changed since Apply. Refresh and reconcile before saving.");
            string eq = "Not requested", gain = "Not requested";
            if (filters)
            {
                try { await device.SaveEqToFlashAsync(token).ConfigureAwait(false); eq = "Command completed — persistence unverified"; }
                catch (Exception ex) { eq = "Failed: " + ex.Message; }
            }
            if (gains)
            {
                try { await device.SaveGainsToFlashAsync(token).ConfigureAwait(false); gain = "Command completed — persistence unverified"; }
                catch (Exception ex) { gain = "Failed: " + ex.Message; }
            }
            return new FlashOutcome(eq, gain);
        }, cancellationToken);

    private IDawnPro2Device RequireSession(IMoondropDevice session)
    {
        if (!ReferenceEquals(session, Device) || session is not IDawnPro2Device { IsUsable: true } device)
            throw new InvalidOperationException("Connection session changed or is unusable. Refresh before any write.");
        return device;
    }

    private static async Task<Pro2Snapshot> ReadWorkspaceAsync(IDawnPro2Device device, CancellationToken token)
    {
        var firmware = await device.ReadFirmwareVersionAsync(token).ConfigureAwait(false);
        var active = await device.ReadActiveEqAsync(token).ConfigureAwait(false);
        var pre = await device.ReadPreGainAsync(token).ConfigureAwait(false);
        var global = await device.ReadGlobalGainAsync(token).ConfigureAwait(false);
        var bands = await device.ReadAllBandsAsync(token).ConfigureAwait(false);
        return new(firmware, active, pre, global, bands);
    }

    public void RegisterReconnectFactory(Func<CancellationToken, Task<IMoondropDevice>> factory)
        => _reconnectFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    public static async Task<MoondropDeviceService> SelectAsync(IMoondropDeviceFactory factory, AppConfig config, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        try
        {
            var device = await factory.CreateDawnPro2Async(cancellationToken).ConfigureAwait(false);
            var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, ""));
            service.RegisterReconnectFactory(factory.CreateDawnPro2Async);
            return service;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            errors.Add($"Dawn Pro 2 HID: {ex.Message}");
        }

        try
        {
            var device = await factory.CreateLegacyAsync(config, cancellationToken).ConfigureAwait(false);
            return new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, device.DisplayName, device, string.Join(Environment.NewLine, errors)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            errors.Add($"Original Dawn Pro USB: {ex.Message}");
        }

        throw new InvalidOperationException("No supported Moondrop device found." + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    public Task<Pro2Snapshot> RefreshPro2Async(CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            var device = await AcquirePro2Async(token).ConfigureAwait(false);
            var firmware = await device.ReadFirmwareVersionAsync(token).ConfigureAwait(false);
            var activeEq = await device.ReadActiveEqAsync(token).ConfigureAwait(false);
            var preGain = await device.ReadPreGainAsync(token).ConfigureAwait(false);
            var globalGain = await device.ReadGlobalGainAsync(token).ConfigureAwait(false);
            var bands = await device.ReadAllBandsAsync(token).ConfigureAwait(false);
            return new Pro2Snapshot(firmware, activeEq, preGain, globalGain, bands);
        }, cancellationToken);

    public Task ApplyBandAsync(PeqBand band, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).WriteBandAsync(band, token).ConfigureAwait(false), cancellationToken);

    public Task ImportEqAsync(IReadOnlyList<PeqBand> bands, double? preamp, bool applyPreamp, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            var device = await AcquirePro2Async(token).ConfigureAwait(false);
            _ = DawnPro2Protocol.BuildWriteAllBandPayloads(bands, save: false);
            if (applyPreamp && preamp.HasValue)
                await device.WritePreGainAsync(preamp.Value, save: false, token).ConfigureAwait(false);
            await device.WriteAllBandsAsync(bands, save: false, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetActiveEqAsync(int index, bool save, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).WriteActiveEqAsync(index, save, token).ConfigureAwait(false), cancellationToken);

    public Task SetPreGainAsync(double value, bool save, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).WritePreGainAsync(value, save, token).ConfigureAwait(false), cancellationToken);

    public Task SetGlobalGainAsync(double value, bool save, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).WriteGlobalGainAsync(value, save, token).ConfigureAwait(false), cancellationToken);

    public Task SaveEqToFlashAsync(CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).SaveEqToFlashAsync(token).ConfigureAwait(false), cancellationToken);

    public Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).SaveGainsToFlashAsync(token).ConfigureAwait(false), cancellationToken);


    public Task<LegacySnapshot> RefreshLegacyAsync(CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            var device = RequireLegacy();
            var volume = await device.GetVolumeAsync(token).ConfigureAwait(false);
            var gain = await device.GetGainAsync(token).ConfigureAwait(false);
            var filter = await device.GetFilterAsync(token).ConfigureAwait(false);
            var led = await device.GetLedStatusAsync(token).ConfigureAwait(false);
            return new LegacySnapshot(volume, gain, filter, led);
        }, cancellationToken);

    public Task<bool> SetLegacyVolumeAsync(int value, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(token => RequireLegacy().SetVolumeAsync(value, token), cancellationToken);

    public Task<bool> SetLegacyGainAsync(string value, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(token => RequireLegacy().SetGainAsync(value, token), cancellationToken);

    public Task<bool> SetLegacyFilterAsync(string value, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(token => RequireLegacy().SetFilterAsync(value, token), cancellationToken);

    public Task<bool> SetLegacyLedAsync(string value, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(token => RequireLegacy().SetLedStatusAsync(value, token), cancellationToken);

    public Task<bool> ApplyLegacyDefaultsAsync(AppConfig config, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
        {
            var defaults = config.DefaultSettings;
            var device = RequireLegacy();
            var ok = await device.SetVolumeAsync(defaults.DefaultVolume, token).ConfigureAwait(false);
            ok &= await device.SetGainAsync(defaults.DefaultGain, token).ConfigureAwait(false);
            ok &= await device.SetLedStatusAsync(defaults.DefaultLedStatus, token).ConfigureAwait(false);
            ok &= await device.SetFilterAsync(defaults.DefaultFilter, token).ConfigureAwait(false);
            return ok;
        }, cancellationToken);

    public Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(async token =>
            await (await AcquirePro2Async(token).ConfigureAwait(false)).EnableBandCoefficientsAsync(index, token).ConfigureAwait(false), cancellationToken);

    private async Task<IDawnPro2Device> AcquirePro2Async(CancellationToken cancellationToken)
    {
        if (_selection.Kind == DeviceKind.DawnPro2 && Device is IDawnPro2Device current && current.IsUsable)
            return current;
        if (_selection.Kind != DeviceKind.DawnPro2)
            throw new InvalidOperationException("The selected device does not support DAWN PRO2 controls.");
        if (_reconnectFactory is null)
            throw new InvalidOperationException("The DAWN PRO2 device became unusable and no reconnect factory is registered; restart the application.");
        var reopened = await _reconnectFactory(cancellationToken).ConfigureAwait(false);
        _selection = new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, reopened.DisplayName, reopened, _selection.CombinedErrors);
        return (IDawnPro2Device)reopened;
    }

    private ILegacyDawnProDevice RequireLegacy() =>
        Device as ILegacyDawnProDevice ?? throw new InvalidOperationException("The selected device does not support legacy controls.");

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _queue.RunAsync(async _ =>
        {
            if (Device is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (Device is IDisposable disposable)
                disposable.Dispose();
        }).ConfigureAwait(false);
    }
}

public sealed record Pro2Snapshot(string FirmwareVersion, int ActiveEq, double PreGain, double GlobalGain, IReadOnlyList<PeqBand> Bands);

public sealed record LegacySnapshot(int? Volume, string? Gain, string? Filter, string? LedStatus);

public sealed record FlashOutcome(string Eq, string Gains);
