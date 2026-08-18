namespace Moondrop.Hardware;

public interface IDeviceDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RealDeviceDelay : IDeviceDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public interface ILegacyUsbTransport : IDisposable, IAsyncDisposable
{
    Task<IReadOnlyList<byte>> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, IReadOnlyList<byte> data, CancellationToken cancellationToken);
}

public interface IDawnPro2HidTransport : IDisposable, IAsyncDisposable
{
    Task WriteAsync(IReadOnlyList<byte> packet, TimeSpan timeout, CancellationToken cancellationToken);
    Task<IReadOnlyList<byte>> ReadAsync(int length, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IHidStream : IDisposable
{
    int ReadTimeout { get; set; }
    int WriteTimeout { get; set; }
    void Write(byte[] buffer);
    int Read(byte[] buffer, int offset, int count);
}

public interface IHidStreamFactory
{
    IHidStream Open(string devicePath);
}

public sealed record DawnPro2HidIdentity(string DevicePath, string SerialNumber)
{
    public Core.Devices.DeviceKind DeviceKind { get; init; } = Core.Devices.DeviceKind.DawnPro2;
    public ushort VendorId { get; init; } = Core.Protocol.DawnPro2Protocol.VendorId;
    public ushort ProductId { get; init; } = Core.Protocol.DawnPro2Protocol.ProductId;

    public string PhysicalDeviceInstanceId =>
        $"USB\\VID_{VendorId:X4}&PID_{ProductId:X4}\\{SerialNumber}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DevicePath))
            throw new InvalidOperationException("The pinned DAWN PRO2 HID device path is absent.");
        if (string.IsNullOrWhiteSpace(SerialNumber))
            throw new InvalidOperationException("The pinned DAWN PRO2 HidSharp serial is absent.");
    }
}

public interface IDawnPro2HidDeviceCatalog
{
    IReadOnlyList<DawnPro2HidIdentity> Enumerate();
}
