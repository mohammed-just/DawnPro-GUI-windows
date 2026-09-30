using LibUsbDotNet.LibUsb;

namespace Moondrop.Hardware;

internal sealed class SafeUsbContext : UsbContext
{
    private readonly bool _initialized;

    public SafeUsbContext() => _initialized = true;

    protected override void Dispose(bool disposeManagedObjects)
    {
        // The upstream finalizer assumes its constructor completed. A missing
        // native libusb library can throw before those fields are initialized.
        if (_initialized) base.Dispose(disposeManagedObjects);
    }
}
