namespace Moondrop.Core.Devices;

public enum DeviceKind
{
    DawnPro2,
    Legacy
}

public sealed record BackendSelection<T>(DeviceKind Kind, string DisplayName, T Device, string CombinedErrors);

public static class BackendSelector
{
    public static BackendSelection<object> Select(Func<object> dawnPro2Factory, Func<object> legacyFactory)
    {
        var errors = new List<string>();
        try
        {
            return new BackendSelection<object>(DeviceKind.DawnPro2, "Moondrop DAWN PRO2", dawnPro2Factory(), string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            errors.Add($"Dawn Pro 2 HID: {ex.Message}");
        }

        try
        {
            return new BackendSelection<object>(DeviceKind.Legacy, "Moondrop Dawn Pro", legacyFactory(), string.Join(Environment.NewLine, errors));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            errors.Add($"Original Dawn Pro USB: {ex.Message}");
        }

        throw new InvalidOperationException("No supported Moondrop device found." + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }
}
