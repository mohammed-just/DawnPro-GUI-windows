using HidSharp;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Moondrop.Core.Config;
using Moondrop.Core.Protocol;

namespace Moondrop.Hardware;

public sealed class HidSharpDawnPro2Transport : IDawnPro2HidTransport
{
    private const int ReadTimeoutMilliseconds = 2000;
    private readonly string _devicePath;
    private readonly IHidStream _stream;
    private bool _disposed;

    public HidSharpDawnPro2Transport(string devicePath)
        : this(devicePath, new HidSharpStreamFactory())
    {
    }

    public HidSharpDawnPro2Transport(string devicePath, IHidStreamFactory streamFactory)
    {
        _devicePath = devicePath;
        _stream = streamFactory.Open(devicePath);
        _stream.ReadTimeout = ReadTimeoutMilliseconds;
    }

    public static HidSharpDawnPro2Transport OpenFirst()
    {
        var device = DeviceList.Local.GetHidDevices(DawnPro2Protocol.VendorId, DawnPro2Protocol.ProductId).FirstOrDefault()
            ?? throw new InvalidOperationException("Dawn Pro 2 HID interface not found. Ensure DAWN PRO2 is connected and visible as HID VID=0x35D8 PID=0x011D.");
        return new HidSharpDawnPro2Transport(device.DevicePath);
    }

    public static HidSharpDawnPro2Transport OpenByIdentity(
        DawnPro2HidIdentity identity,
        IDawnPro2HidDeviceCatalog? catalog = null,
        IHidStreamFactory? streamFactory = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        catalog ??= new HidSharpDawnPro2DeviceCatalog();
        var matches = catalog.Enumerate()
            .Where(candidate =>
                candidate.DeviceKind == identity.DeviceKind &&
                candidate.VendorId == identity.VendorId &&
                candidate.ProductId == identity.ProductId &&
                string.Equals(candidate.SerialNumber, identity.SerialNumber, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.DevicePath, identity.DevicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one DAWN PRO2 with the exact pinned HID identity; found {matches.Length}. " +
                "The device may be absent, ambiguous, or its identity may have changed.");
        }

        return new HidSharpDawnPro2Transport(matches[0].DevicePath, streamFactory ?? new HidSharpStreamFactory());
    }

    public static DawnPro2HidIdentity CaptureSingleIdentity(IDawnPro2HidDeviceCatalog? catalog = null)
    {
        catalog ??= new HidSharpDawnPro2DeviceCatalog();
        var candidates = catalog.Enumerate();
        if (candidates.Count != 1)
            throw new InvalidOperationException($"Expected exactly one DAWN PRO2 HID interface; found {candidates.Count}.");
        candidates[0].Validate();
        return candidates[0];
    }

    public Task WriteAsync(IReadOnlyList<byte> packet, TimeSpan timeout, CancellationToken cancellationToken) =>
        BlockingWork.RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _stream.WriteTimeout = Math.Clamp((int)Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
            _stream.Write(packet.ToArray());
        }, cancellationToken);

    public Task<IReadOnlyList<byte>> ReadAsync(int length, TimeSpan timeout, CancellationToken cancellationToken) =>
        BlockingWork.RunAsync<IReadOnlyList<byte>>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _stream.ReadTimeout = Math.Clamp((int)Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
            var buffer = new byte[length];
            var read = _stream.Read(buffer, 0, buffer.Length);
            return buffer.Take(read).ToArray();
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class HidSharpDawnPro2DeviceCatalog : IDawnPro2HidDeviceCatalog
{
    public IReadOnlyList<DawnPro2HidIdentity> Enumerate() =>
        DeviceList.Local.GetHidDevices(DawnPro2Protocol.VendorId, DawnPro2Protocol.ProductId)
            .Select(device => new DawnPro2HidIdentity(device.DevicePath, ReadSerial(device)))
            .ToArray();

    private static string ReadSerial(HidDevice device)
    {
        try
        {
            return device.GetSerialNumber() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

public sealed class HidSharpStreamFactory : IHidStreamFactory
{
    public IHidStream Open(string devicePath)
    {
        var device = DeviceList.Local.GetAllDevices().OfType<HidDevice>().FirstOrDefault(x => x.DevicePath == devicePath)
            ?? throw new IOException("Dawn Pro 2 HID interface is no longer available.");
        return new HidSharpStreamAdapter(device.Open());
    }
}

public sealed class HidSharpStreamAdapter(HidStream stream) : IHidStream
{
    public int ReadTimeout
    {
        get => stream.ReadTimeout;
        set => stream.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => stream.WriteTimeout;
        set => stream.WriteTimeout = value;
    }

    public void Write(byte[] buffer) => stream.Write(buffer);

    public int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);

    public void Dispose() => stream.Dispose();
}

public sealed class LibUsbLegacyTransport : ILegacyUsbTransport, IDisposable
{
    private readonly UsbContext _context;
    private readonly IUsbDevice _device;

    private LibUsbLegacyTransport(UsbContext context, IUsbDevice device)
    {
        _context = context;
        _device = device;
    }

    public static LibUsbLegacyTransport Open(AppConfig config, out string displayName)
    {
        var context = new UsbContext();
        foreach (var candidate in CandidateIds(config))
        {
            var device = context.Find(new UsbDeviceFinder { Vid = candidate.Vid, Pid = candidate.Pid });
            if (device is null)
                continue;
            displayName = candidate.Name;
            return new LibUsbLegacyTransport(context, device);
        }

        context.Dispose();
        var supported = string.Join(", ", CandidateIds(config).Select(x => $"{x.Name} (VID=0x{x.Vid:X4}, PID=0x{x.Pid:X4})"));
        throw new InvalidOperationException($"Original Dawn Pro USB device not found. Supported IDs: {supported}.");
    }

    public async Task<IReadOnlyList<byte>> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, IReadOnlyList<byte> data, CancellationToken cancellationToken)
    {
        var packet = new UsbSetupPacket(requestType, request, value, index, data.Count);
        var buffer = data.ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transferred = await _device.ControlTransferAsync(packet, buffer, 0, buffer.Length).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return buffer.Take(transferred).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"USB control transfer failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        _device.Dispose();
        _context.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<(string Name, int Vid, int Pid)> CandidateIds(AppConfig config)
    {
        yield return ("Moondrop Dawn Pro", config.DeviceIdentifiers.MoondropVid, config.DeviceIdentifiers.DawnProPid);
        foreach (var item in config.DeviceIdentifiers.AdditionalDeviceIds)
            yield return (string.IsNullOrWhiteSpace(item.Name) ? "Additional device" : item.Name, item.VendorId, item.ProductId);
    }
}

public sealed class HardwareDeviceFactory : IMoondropDeviceFactory
{
    public Task<IMoondropDevice> CreateDawnPro2Async(CancellationToken cancellationToken) =>
        BlockingWork.RunAsync<IMoondropDevice>(() => new DawnPro2Device(HidSharpDawnPro2Transport.OpenFirst()), cancellationToken);

    public Task<IMoondropDevice> CreateLegacyAsync(AppConfig config, CancellationToken cancellationToken) =>
        BlockingWork.RunAsync<IMoondropDevice>(() =>
        {
            var transport = LibUsbLegacyTransport.Open(config, out var name);
            return new LegacyDawnProDevice(name, transport, config);
        }, cancellationToken);
}

internal static class BlockingWork
{
    public static Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync<object?>(() =>
        {
            action();
            return null;
        }, cancellationToken);
    }

    public static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new BlockingWorkItem<T>(action, cancellationToken, completion);
        if (!ThreadPool.QueueUserWorkItem(
                static item => item.Execute(),
                work,
                preferLocal: false))
        {
            completion.TrySetException(new InvalidOperationException("Could not queue blocking device work."));
        }
        return completion.Task;
    }

    private sealed class BlockingWorkItem<T>(
        Func<T> action,
        CancellationToken cancellationToken,
        TaskCompletionSource<T> completion)
    {
        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }
}
