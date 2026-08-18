using System.Text;
using HidSharp;
using LibUsbDotNet.LibUsb;

namespace Moondrop.Hardware;

public static class DeviceDiagnostics
{
    public static string CollectText()
    {
        var builder = new StringBuilder();
        AppendHid(builder);
        builder.AppendLine();
        AppendUsb(builder);
        return builder.ToString();
    }

    private static void AppendHid(StringBuilder builder)
    {
        builder.AppendLine("HID devices");
        try
        {
            var devices = DeviceList.Local.GetHidDevices().OrderBy(x => x.VendorID).ThenBy(x => x.ProductID).ToArray();
            if (devices.Length == 0)
            {
                builder.AppendLine("  No HID devices reported.");
                return;
            }

            foreach (var device in devices)
            {
                builder.Append("  VID=0x").Append(device.VendorID.ToString("X4"))
                    .Append(" PID=0x").Append(device.ProductID.ToString("X4"))
                    .Append(' ').Append(GetHidUsageText(device));
                AppendText(builder, " Product", TryRead(device.GetProductName));
                AppendText(builder, " Manufacturer", TryRead(device.GetManufacturer));
                builder.AppendLine();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            builder.AppendLine("  HID enumeration failed: " + ex.Message);
        }
    }

    private static void AppendUsb(StringBuilder builder)
    {
        builder.AppendLine("USB devices");
        try
        {
            using var context = new UsbContext();
            var devices = context.FindAll(_ => true).OrderBy(x => x.VendorId).ThenBy(x => x.ProductId).ToArray();
            if (devices.Length == 0)
            {
                builder.AppendLine("  No USB devices reported by LibUsbDotNet.");
                return;
            }

            foreach (var device in devices)
            {
                builder.Append("  VID=0x").Append(device.VendorId.ToString("X4"))
                    .Append(" PID=0x").Append(device.ProductId.ToString("X4"));
                AppendText(builder, " Product", TryRead(() => device.Info.Product));
                AppendText(builder, " Manufacturer", TryRead(() => device.Info.Manufacturer));
                builder.AppendLine();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException)
        {
            builder.AppendLine("  USB enumeration failed: " + ex.Message);
        }
    }

    private static void AppendText(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append("=\"").Append(value.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
    }

    private static string? TryRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static string GetHidUsageText(HidDevice device)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            var deviceItems = descriptor.GetType().GetProperty("DeviceItems")?.GetValue(descriptor) as System.Collections.IEnumerable;
            var firstItem = deviceItems?.Cast<object>().FirstOrDefault();
            var usages = firstItem?.GetType().GetProperty("Usages")?.GetValue(firstItem) as System.Collections.IEnumerable;
            var firstUsage = usages?.Cast<object>().FirstOrDefault();
            var page = firstUsage?.GetType().GetProperty("Page")?.GetValue(firstUsage);
            var usage = firstUsage?.GetType().GetProperty("ID")?.GetValue(firstUsage);
            return page is not null && usage is not null ? $"UsagePage={page} Usage={usage}" : "UsagePage=n/a Usage=n/a";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "UsagePage=unavailable Usage=" + ex.GetType().Name;
        }
    }
}
