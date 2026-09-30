using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moondrop.Core.Config;

public sealed record DeviceConstants
{
    [JsonPropertyName("BM_REQUEST_TYPE_OUT")]
    public int BmRequestTypeOut { get; init; } = 0x43;
    [JsonPropertyName("BM_REQUEST_TYPE_IN")]
    public int BmRequestTypeIn { get; init; } = 0xC3;
    [JsonPropertyName("B_REQUEST")]
    public int BRequest { get; init; } = 160;
    [JsonPropertyName("B_REQUEST_GET")]
    public int BRequestGet { get; init; } = 161;
    [JsonPropertyName("W_VALUE")]
    public int WValue { get; init; } = 0x0000;
    [JsonPropertyName("W_INDEX")]
    public int WIndex { get; init; } = 0x09A0;
    [JsonPropertyName("VOLUME_REFRESH_DATA")]
    public IReadOnlyList<int> VolumeRefreshData { get; init; } = [0xC0, 0xA5, 0xA2];
    [JsonPropertyName("DATA_LENGTH")]
    public int DataLength { get; init; } = 7;
    [JsonPropertyName("LED_STATUS_ENABLED")]
    public int LedStatusEnabled { get; init; } = 0;
    [JsonPropertyName("LED_STATUS_TEMP_OFF")]
    public int LedStatusTempOff { get; init; } = 1;
    [JsonPropertyName("LED_STATUS_OFF")]
    public int LedStatusOff { get; init; } = 2;
}

public sealed record AdditionalDeviceId(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("vendor_id")] int VendorId,
    [property: JsonPropertyName("product_id")] int ProductId);

public sealed class AdditionalDeviceIdListConverter : JsonConverter<IReadOnlyList<AdditionalDeviceId>>
{
    public override IReadOnlyList<AdditionalDeviceId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<AdditionalDeviceId>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("vendor_id", out var vendorElement) ||
                !element.TryGetProperty("product_id", out var productElement) ||
                !TryReadInt(vendorElement, out var vendorId) ||
                !TryReadInt(productElement, out var productId))
                continue;

            var name = element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? "Additional device"
                : "Additional device";
            result.Add(new AdditionalDeviceId(name, vendorId, productId));
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<AdditionalDeviceId> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            JsonSerializer.Serialize(writer, item, options);
        writer.WriteEndArray();
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);
        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), out value);
        value = 0;
        return false;
    }
}

public sealed record DeviceIdentifiers
{
    [JsonPropertyName("MOONDROP_VID")]
    public int MoondropVid { get; init; } = 0x2FC6;
    [JsonPropertyName("DAWN_PRO_PID")]
    public int DawnProPid { get; init; } = 0xF06A;
    [JsonPropertyName("ADDITIONAL_DEVICE_IDS")]
    [JsonConverter(typeof(AdditionalDeviceIdListConverter))]
    public IReadOnlyList<AdditionalDeviceId> AdditionalDeviceIds { get; init; } = [];
    [JsonPropertyName("VOLUME_MAX")]
    public int VolumeMax { get; init; } = 0x00;
    [JsonPropertyName("VOLUME_MIN")]
    public int VolumeMin { get; init; } = 0x70;
}

public sealed record DefaultSettings
{
    [JsonPropertyName("DEFAULT_VOLUME")]
    public int DefaultVolume { get; init; } = 50;
    [JsonPropertyName("DEFAULT_LED_STATUS")]
    public string DefaultLedStatus { get; init; } = "On";
    [JsonPropertyName("DEFAULT_GAIN")]
    public string DefaultGain { get; init; } = "Low";
    [JsonPropertyName("DEFAULT_FILTER")]
    public string DefaultFilter { get; init; } = "Fast Roll-Off Low Latency";
}

public sealed record DawnPro2Settings
{
    [JsonPropertyName("DEFAULT_EQ_INDEX")]
    public int DefaultEqIndex { get; init; } = 0;
    [JsonPropertyName("DEFAULT_PRE_GAIN")]
    public double DefaultPreGain { get; init; } = 0.0;
    [JsonPropertyName("DEFAULT_GLOBAL_GAIN")]
    public double DefaultGlobalGain { get; init; } = 0.0;
}

public sealed record UiMetrics
{
    [JsonPropertyName("WINDOW_WIDTH")]
    public int WindowWidth { get; init; } = 400;
    [JsonPropertyName("WINDOW_HEIGHT")]
    public int WindowHeight { get; init; } = 300;
    [JsonPropertyName("MARGIN_TOP")]
    public int MarginTop { get; init; } = 10;
    [JsonPropertyName("MARGIN_BOTTOM")]
    public int MarginBottom { get; init; } = 20;
    [JsonPropertyName("MARGIN_START")]
    public int MarginStart { get; init; } = 10;
    [JsonPropertyName("MARGIN_END")]
    public int MarginEnd { get; init; } = 10;
    [JsonPropertyName("SPACING")]
    public int Spacing { get; init; } = 10;
}

public sealed record LoggingConfig
{
    [JsonPropertyName("LOG_LEVEL")]
    public string LogLevel { get; init; } = "INFO";
    [JsonPropertyName("LOG_FORMAT")]
    public string LogFormat { get; init; } = "%(asctime)s - %(levelname)s - %(message)s";
    [JsonPropertyName("LOG_FILE")]
    public string? LogFile { get; init; }
}

public sealed record AppConfig
{
    [JsonPropertyName("device_constants")]
    public DeviceConstants DeviceConstants { get; init; } = new();
    [JsonPropertyName("device_identifiers")]
    public DeviceIdentifiers DeviceIdentifiers { get; init; } = new();
    [JsonPropertyName("default_settings")]
    public DefaultSettings DefaultSettings { get; init; } = new();
    [JsonPropertyName("dawn_pro2_settings")]
    public DawnPro2Settings DawnPro2Settings { get; init; } = new();
    [JsonPropertyName("ui_metrics")]
    public UiMetrics UiMetrics { get; init; } = new();
    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static AppConfig LoadJson(string json) => JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();

    public static AppConfig LoadFile(string path) => File.Exists(path) ? LoadJson(File.ReadAllText(path)) : new AppConfig();

    public void SaveFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, SaveJson());
    }

    public AppConfig WithLegacyDefaults(int volume, string gain, string ledStatus, string filter) =>
        this with
        {
            DefaultSettings = new DefaultSettings
            {
                DefaultVolume = Math.Clamp(volume, 0, 60),
                DefaultGain = gain,
                DefaultLedStatus = ledStatus,
                DefaultFilter = filter
            }
        };

    public AppConfig WithDawnPro2Defaults(int activeEq, double preGain, double globalGain) =>
        this with
        {
            DawnPro2Settings = new DawnPro2Settings
            {
                DefaultEqIndex = Math.Clamp(activeEq, 0, 15),
                DefaultPreGain = Math.Clamp(preGain, -18, 12),
                DefaultGlobalGain = Math.Clamp(globalGain, -18, 12)
            }
        };

    public string SaveJson() => JsonSerializer.Serialize(this, Options);

    public static string DefaultConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "dawnpro");
    }

    public static string DefaultConfigPath() => Path.Combine(DefaultConfigDirectory(), "config.json");
}
