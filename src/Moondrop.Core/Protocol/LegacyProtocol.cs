namespace Moondrop.Core.Protocol;

public static class LegacyProtocol
{
    public const byte BmRequestTypeOut = 0x43;
    public const byte BmRequestTypeIn = 0xC3;
    public const byte BRequest = 160;
    public const byte BRequestGet = 161;
    public const ushort WValue = 0x0000;
    public const ushort WIndex = 0x09A0;
    public const int DataLength = 7;
    public static readonly byte[] GetDataPayload = [0xC0, 0xA5, 0xA3];
    public static readonly byte[] VolumeRefreshPayload = [0xC0, 0xA5, 0xA2];

    public static readonly int[] VolumeTable =
    [
        0xFF, 0xC8, 0xB4, 0xAA, 0xA0, 0x96, 0x8C, 0x82, 0x7A, 0x74,
        0x6E, 0x6A, 0x66, 0x62, 0x5E, 0x5A, 0x58, 0x56, 0x54, 0x52,
        0x50, 0x4E, 0x4C, 0x4A, 0x48, 0x46, 0x44, 0x42, 0x40, 0x3E,
        0x3C, 0x3A, 0x38, 0x36, 0x34, 0x32, 0x30, 0x2E, 0x2C, 0x2A,
        0x28, 0x26, 0x24, 0x22, 0x20, 0x1E, 0x1C, 0x1A, 0x18, 0x16,
        0x14, 0x12, 0x10, 0x0E, 0x0C, 0x0A, 0x08, 0x06, 0x04, 0x02,
        0x00
    ];

    public static byte ConvertVolumeToPayload(int value) =>
        value >= 0 && value < VolumeTable.Length ? (byte)VolumeTable[value] : (byte)0;

    public static int ConvertVolumeToPercent(int value) => Array.IndexOf(VolumeTable, value) is var index && index >= 0 ? index : 0;

    public static byte[] SetVolumePayload(int volume) => [192, 165, 4, ConvertVolumeToPayload(volume)];

    public static byte[] SetGainPayload(string gain) => [192, 165, 2, (byte)(gain == "High" ? 1 : 0)];

    public static byte[] SetLedPayload(string status) => [192, 165, 6, status switch { "Temporarily Off" => (byte)1, "Off" => (byte)2, _ => (byte)0 }];

    public static byte[] SetFilterPayload(string filter) =>
        [192, 165, 1, filter switch
        {
            "Fast Roll-Off Phase Compensated" => (byte)1,
            "Slow Roll-Off Low Latency" => (byte)2,
            "Slow Roll-Off Phase Compensated" => (byte)3,
            "Non-Oversampling" => (byte)4,
            _ => (byte)0
        }];

    public static string ConvertLedStatusToString(int status) => status switch
    {
        0 => "On",
        1 => "Temporarily Off",
        2 => "Off",
        _ => "Invalid LED Status"
    };

    public static string ConvertGainToString(int gain) => gain switch
    {
        0 => "Low",
        1 => "High",
        _ => "Invalid Gain Value"
    };

    public static string ConvertFilterPayloadToString(int filter) => filter switch
    {
        1 => "Fast Roll-Off Phase Compensated",
        2 => "Slow Roll-Off Low Latency",
        3 => "Slow Roll-Off Phase Compensated",
        4 => "Non-Oversampling",
        0 => "Fast Roll-Off Low Latency",
        _ => "Invalid Filter Value"
    };
}
