using System.Text;
using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Protocol;

namespace Moondrop.Hardware;

public interface IMoondropDevice
{
    DeviceKind Kind { get; }
    string DisplayName { get; }
}

public interface IDawnPro2Device : IMoondropDevice
{
    bool IsUsable { get; }
    Task<string> ReadFirmwareVersionAsync(CancellationToken cancellationToken = default);
    Task<int> ReadActiveEqAsync(CancellationToken cancellationToken = default);
    Task WriteActiveEqAsync(int index, bool save = false, CancellationToken cancellationToken = default);
    Task<double> ReadPreGainAsync(CancellationToken cancellationToken = default);
    Task WritePreGainAsync(double value, bool save = false, CancellationToken cancellationToken = default);
    Task<double> ReadGlobalGainAsync(CancellationToken cancellationToken = default);
    Task WriteGlobalGainAsync(double value, bool save = false, CancellationToken cancellationToken = default);
    Task<PeqBand> ReadBandAsync(int index, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeqBand>> ReadAllBandsAsync(CancellationToken cancellationToken = default);
    Task WriteBandAsync(PeqBand band, CancellationToken cancellationToken = default);
    Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default);
    Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default);
    Task SaveEqToFlashAsync(CancellationToken cancellationToken = default);
    Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default);
}

public interface ILegacyDawnProDevice : IMoondropDevice
{
    Task<int?> GetVolumeAsync(CancellationToken cancellationToken = default);
    Task<string?> GetLedStatusAsync(CancellationToken cancellationToken = default);
    Task<string?> GetGainAsync(CancellationToken cancellationToken = default);
    Task<string?> GetFilterAsync(CancellationToken cancellationToken = default);
    Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
    Task<bool> SetLedStatusAsync(string status, CancellationToken cancellationToken = default);
    Task<bool> SetGainAsync(string gain, CancellationToken cancellationToken = default);
    Task<bool> SetFilterAsync(string filter, CancellationToken cancellationToken = default);
}

public sealed record DawnPro2HidReadFrame(DateTimeOffset CapturedAtUtc, byte RequestCommand, byte[] RawReport);

public sealed class DawnPro2Device(
    IDawnPro2HidTransport transport,
    IDeviceDelay? delay = null,
    Action<DawnPro2HidReadFrame>? readFrameSink = null,
    Func<Task>? transactionProgress = null) : IDawnPro2Device, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    private readonly IDeviceDelay _delay = delay ?? new RealDeviceDelay();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private int _poisoned;

    public DeviceKind Kind => DeviceKind.DawnPro2;
    public string DisplayName => "Moondrop DAWN PRO2";
    public bool IsUsable => Volatile.Read(ref _poisoned) == 0;

    public async Task<string> ReadFirmwareVersionAsync(CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadFirmwarePayload(),
            ParseFirmwareResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReadActiveEqAsync(CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadActiveEqPayload(),
            response =>
            {
                RequireLength(response, 4, "active EQ");
                if (response[3] > 15)
                    throw new InvalidDataException($"Dawn Pro 2 active EQ response {response[3]} is outside 0..15.");
                return (int)response[3];
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteActiveEqAsync(int index, bool save = false, CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildWriteActiveEqPayload(index), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        if (save)
            await SaveEqToFlashAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<double> ReadPreGainAsync(CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadPreGainPayload(),
            response => ParseGainResponse(response, "pre gain"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WritePreGainAsync(double value, bool save = false, CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildWritePreGainPayload(value), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        if (save)
            await SaveGainsToFlashAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<double> ReadGlobalGainAsync(CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadGlobalGainPayload(),
            response => ParseGainResponse(response, "global gain"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteGlobalGainAsync(double value, bool save = false, CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildWriteGlobalGainPayload(value), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        if (save)
            await SaveGainsToFlashAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeqBand> ReadBandAsync(int index, CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadBandPayload(index),
            response => DawnPro2Protocol.ParseRawBandPayload(index, response).ToPeqBand(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PeqBand>> ReadAllBandsAsync(CancellationToken cancellationToken = default)
    {
        var bands = new List<PeqBand>(8);
        for (var i = 0; i < 8; i++)
            bands.Add(await ReadBandAsync(i, cancellationToken).ConfigureAwait(false));
        return bands;
    }

    public async Task<RawPeqBandState> ReadRawBandAsync(int index, CancellationToken cancellationToken = default)
    {
        return await SendParsedAsync(
            DawnPro2Protocol.BuildReadBandPayload(index),
            response => DawnPro2Protocol.ParseRawBandPayload(index, response),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RawPeqBandState>> ReadAllRawBandsAsync(CancellationToken cancellationToken = default)
    {
        var bands = new List<RawPeqBandState>(8);
        for (var index = 0; index < 8; index++)
            bands.Add(await ReadRawBandAsync(index, cancellationToken).ConfigureAwait(false));
        return bands;
    }

    public async Task WriteBandAsync(PeqBand band, CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildWriteBandPayload(band.Index, band), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        await EnableBandCoefficientsAsync(band.Index, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteRawBandAsync(RawPeqBandState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = DawnPro2Protocol.BuildWriteRawBandPayload(state);
        await SendAsync(payload, false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        await EnableBandCoefficientsAsync(state.Index, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAllRawBandsAsync(IReadOnlyList<RawPeqBandState> states, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count != 8)
            throw new InvalidOperationException($"Bit-faithful bulk write requires exactly 8 raw bands; received {states.Count}.");
        var indexes = new HashSet<int>();
        foreach (var state in states)
        {
            DawnPro2Protocol.ValidateRawBandState(state);
            if (!indexes.Add(state.Index))
                throw new InvalidOperationException($"Raw bulk write contains duplicate band index {state.Index}.");
        }
        if (!indexes.SetEquals(Enumerable.Range(0, 8)))
            throw new InvalidOperationException("Raw bulk write must contain the complete band index set 0..7.");

        foreach (var state in states.OrderBy(state => state.Index))
            await WriteRawBandAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildEnableBandPayload(index), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default)
    {
        _ = DawnPro2Protocol.BuildWriteAllBandPayloads(bands, save: false);
        foreach (var band in bands)
            await WriteBandAsync(band, cancellationToken).ConfigureAwait(false);
        if (save)
            await SaveEqToFlashAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveEqToFlashAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildSaveEqToFlashPayload(), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(DawnPro2Protocol.BuildSaveOffsetToFlashPayload(), false, cancellationToken).ConfigureAwait(false);
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<byte>> SendAsync(
        IReadOnlyList<byte> payload,
        bool expectResponse,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<byte>>? responseValidator = null)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _poisoned) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!expectResponse)
            {
                try
                {
                    await transport.WriteAsync(DawnPro2Protocol.CreatePacket(payload), WriteTimeout, cancellationToken).ConfigureAwait(false);
                    if (transactionProgress is not null)
                        await transactionProgress().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    PoisonTransport();
                    throw;
                }
                return [];
            }
            // Entering the native request write makes every failure ambiguous: the device may
            // have accepted the request and still owe this retained stream a late response.
            // Cancellation is checked before entry, then deferred until the response is drained.
            byte[] normalized;
            try
            {
                await transport.WriteAsync(
                    DawnPro2Protocol.CreatePacket(payload),
                    WriteTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                var response = await transport.ReadAsync(DawnPro2Protocol.ReportLength, TimeSpan.FromMilliseconds(2000), CancellationToken.None).ConfigureAwait(false);
                readFrameSink?.Invoke(new DawnPro2HidReadFrame(
                    DateTimeOffset.UtcNow,
                    payload.Count > 1 ? payload[1] : byte.MaxValue,
                    response.ToArray()));
                normalized = DawnPro2Protocol.NormalizeResponse(response);
                responseValidator?.Invoke(normalized);
            }
            catch
            {
                PoisonTransport();
                throw;
            }
            if (transactionProgress is not null)
                await transactionProgress().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return normalized;
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    private async Task<T> SendParsedAsync<T>(
        IReadOnlyList<byte> payload,
        Func<IReadOnlyList<byte>, T> parser,
        CancellationToken cancellationToken)
    {
        T? parsed = default;
        await SendAsync(
            payload,
            true,
            cancellationToken,
            response => parsed = parser(response)).ConfigureAwait(false);
        return parsed!;
    }

    private void PoisonTransport()
    {
        Interlocked.Exchange(ref _poisoned, 1);
        transport.Dispose();
    }

    private static void RequireLength(IReadOnlyList<byte> payload, int minimum, string responseName)
    {
        if (payload.Count < minimum)
            throw new IOException($"Dawn Pro 2 {responseName} response was too short: expected at least {minimum} payload bytes, received {payload.Count}.");
    }

    private static double ParseGainResponse(IReadOnlyList<byte> payload, string responseName)
    {
        RequireLength(payload, 5, responseName);
        var gain = DawnPro2Protocol.DecodeFixedPoint(payload[3], payload[4]);
        if (gain is < -18 or > 12)
            throw new InvalidDataException($"Dawn Pro 2 {responseName} response {gain} dB is outside -18..12 dB.");
        return gain;
    }

    private static string ParseFirmwareResponse(IReadOnlyList<byte> payload)
    {
        RequireLength(payload, 4, "firmware");
        var raw = payload.Skip(3).TakeWhile(value => value != 0).ToArray();
        if (raw.Length == 0)
            throw new InvalidDataException("Dawn Pro 2 firmware response is empty.");
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Dawn Pro 2 firmware response is not valid UTF-8.", ex);
        }
    }

    public void Dispose() => transport.Dispose();

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

public sealed class LegacyDawnProDevice(string displayName, ILegacyUsbTransport transport, AppConfig config, IDeviceDelay? delay = null) : ILegacyDawnProDevice, IDisposable, IAsyncDisposable
{
    private readonly DeviceConstants _constants = config.DeviceConstants;
    private readonly IDeviceDelay _delay = delay ?? new RealDeviceDelay();

    public DeviceKind Kind => DeviceKind.Legacy;
    public string DisplayName { get; } = displayName;

    public async Task<int?> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                await RefreshVolumeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Python's refresh_volume handles its own failure and still attempts the IN read.
            }
            var response = await InAsync(cancellationToken).ConfigureAwait(false);
            return response.Count > 4 ? LegacyProtocol.ConvertVolumeToPercent(response[4]) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<string?> GetLedStatusAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetDataAsync(cancellationToken).ConfigureAwait(false);
        return data.Count > 5 ? LegacyProtocol.ConvertLedStatusToString(data[5]) : null;
    }

    public async Task<string?> GetGainAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetDataAsync(cancellationToken).ConfigureAwait(false);
        return data.Count > 4 ? LegacyProtocol.ConvertGainToString(data[4]) : null;
    }

    public async Task<string?> GetFilterAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetDataAsync(cancellationToken).ConfigureAwait(false);
        return data.Count > 3 ? LegacyProtocol.ConvertFilterPayloadToString(data[3]) : null;
    }

    public async Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default) =>
        await SetAsync(LegacyProtocol.SetVolumePayload(volume), true, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SetLedStatusAsync(string status, CancellationToken cancellationToken = default) =>
        await SetAsync(LegacyProtocol.SetLedPayload(status), false, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SetGainAsync(string gain, CancellationToken cancellationToken = default) =>
        await SetAsync(LegacyProtocol.SetGainPayload(gain), true, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SetFilterAsync(string filter, CancellationToken cancellationToken = default) =>
        await SetAsync(LegacyProtocol.SetFilterPayload(filter), false, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<byte>> GetDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await OutAsync(LegacyProtocol.GetDataPayload, cancellationToken).ConfigureAwait(false);
            return await InAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task<bool> SetAsync(IReadOnlyList<byte> data, bool refreshVolume, CancellationToken cancellationToken)
    {
        try
        {
            await OutAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        if (refreshVolume)
        {
            try
            {
                await RefreshVolumeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Python treats the primary write as successful even when this refresh fails.
            }
        }
        return true;
    }

    private Task<IReadOnlyList<byte>> RefreshVolumeAsync(CancellationToken cancellationToken) =>
        OutAsync(_constants.VolumeRefreshData.Select(x => (byte)x).ToArray(), cancellationToken);

    private async Task<IReadOnlyList<byte>> OutAsync(IReadOnlyList<byte> data, CancellationToken cancellationToken)
    {
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        return await transport.ControlTransferAsync((byte)_constants.BmRequestTypeOut, (byte)_constants.BRequest, (ushort)_constants.WValue, (ushort)_constants.WIndex, data, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<byte>> InAsync(CancellationToken cancellationToken)
    {
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        return await transport.ControlTransferAsync((byte)_constants.BmRequestTypeIn, (byte)_constants.BRequestGet, (ushort)_constants.WValue, (ushort)_constants.WIndex, _constants.DataLengthBytes(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => transport.Dispose();

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

file static class DeviceConstantsExtensions
{
    public static IReadOnlyList<byte> DataLengthBytes(this DeviceConstants constants) => new byte[constants.DataLength];
}
