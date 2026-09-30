using System.Globalization;
using System.Text;
using System.Text.Json;
using Moondrop.Core.Devices;
using Moondrop.Core.Protocol;

namespace Moondrop.Core.Eq;

public sealed record EqConfiguration(PeqBand[] Bands, double PreGain = 0, double GlobalGain = 0)
{
    public static PeqBand DefaultBand(int index) => new(index, 1000, 1, 0, PeqFilterType.Peaking, false);
    public static EqConfiguration Flat() => new(Enumerable.Range(0, 8).Select(DefaultBand).ToArray());
    public EqConfiguration Copy() => this with { Bands = Bands.ToArray() };

    public void Validate()
    {
        if (Bands.Length != 8 || !Bands.Select(x => x.Index).SequenceEqual(Enumerable.Range(0, 8)))
            throw new EqPresetException("A replacement EQ must contain all eight band positions in order.");
        foreach (var band in Bands)
        {
            if (band.Q < 1.0 / 256) throw new EqPresetException($"Band {band.Index + 1}: Q is below device precision (1/256). Increase Q before applying.");
            if (band.FilterType == PeqFilterType.Unknown)
                throw new EqPresetException($"Band {band.Index + 1}: unknown raw filter {band.RawFilterCode}. Preserve it by refreshing, or explicitly select a supported type before applying.");
            _ = DawnPro2Protocol.BuildWriteBandPayload(band.Index, band);
        }
        _ = DawnPro2Protocol.BuildWritePreGainPayload(PreGain);
        _ = DawnPro2Protocol.BuildWriteGlobalGainPayload(GlobalGain);
    }

    public static double Encoded(double value) => Math.Round(value * 256, MidpointRounding.ToEven) / 256;
    public bool SameFilters(EqConfiguration other, bool encoded = false) => Bands.Zip(other.Bands).All(pair =>
        pair.First.Index == pair.Second.Index && pair.First.Frequency == pair.Second.Frequency &&
        (encoded ? Encoded(pair.First.Q) == Encoded(pair.Second.Q) && Encoded(pair.First.Gain) == Encoded(pair.Second.Gain) :
            pair.First.Q == pair.Second.Q && pair.First.Gain == pair.Second.Gain) &&
        (pair.First.Enabled ? pair.First.FilterType : PeqFilterType.Disabled) == (pair.Second.Enabled ? pair.Second.FilterType : PeqFilterType.Disabled) &&
        pair.First.RawFilterCode == pair.Second.RawFilterCode) && Bands.Length == other.Bands.Length;
    public bool SameGains(EqConfiguration other, bool encoded = false) => encoded
        ? Encoded(PreGain) == Encoded(other.PreGain) && Encoded(GlobalGain) == Encoded(other.GlobalGain)
        : PreGain == other.PreGain && GlobalGain == other.GlobalGain;
    public bool Same(EqConfiguration other, bool encoded = false) => SameFilters(other, encoded) && SameGains(other, encoded);
}

public static class EqHeadroom
{
    // Dense log sweep plus filter-centred samples and local maximization. Independent of drawing resolution.
    public static double Peak(IEnumerable<PeqBand> bands)
    {
        var enabled = bands.Where(x => x.Enabled && x.FilterType is not (PeqFilterType.Disabled or PeqFilterType.Unknown)).ToArray();
        var prepared = enabled.Select(DawnPro2Protocol.PrepareMagnitudeResponse).ToArray();
        double Response(double frequency) => prepared.Sum(x => x.MagnitudeDb(frequency));
        if (prepared.Length == 0) return 0;
        var points = Enumerable.Range(0, 8193).Select(i => 20 * Math.Pow(1000, i / 8192.0)).ToList();
        foreach (var band in enabled)
            for (var i = -32; i <= 32; i++)
                points.Add(Math.Clamp(band.Frequency * Math.Exp(i / (64.0 * Math.Max(1, band.Q))), 20, 20000));
        var ordered = points.Distinct().Order().ToArray();
        var values = ordered.Select(Response).ToArray();
        var peak = Math.Max(0, values.Max());
        for (var i = 1; i < ordered.Length - 1; i++)
        {
            if (values[i] < values[i - 1] || values[i] < values[i + 1]) continue;
            var left = Math.Log(ordered[i - 1]); var right = Math.Log(ordered[i + 1]);
            for (var step = 0; step < 24; step++)
            {
                var a = left + (right - left) / 3; var b = right - (right - left) / 3;
                if (Response(Math.Exp(a)) > Response(Math.Exp(b))) right = b; else left = a;
            }
            peak = Math.Max(peak, Response(Math.Exp((left + right) / 2)));
        }
        return peak;
    }
}

public sealed record LocalPreset(Guid Id, string Name, string Source, DateTimeOffset UpdatedAt, EqConfiguration Eq, string ConversionNotes = "")
{
    public string Description => $"Local preset · {Eq.Bands.Count(x => x.Enabled)} active filters";
    public override string ToString() => Name;
}

public sealed record WorkspacePreferences
{
    public string Theme { get; init; } = "System";
    public bool SystemAccent { get; init; } = true;
    public bool Reconnect { get; init; } = true;
    public bool ConfirmFlash { get; init; } = true;
    public bool RememberEq { get; init; } = true;
    public bool AutoPreamp { get; init; }
    public bool IndividualCurves { get; init; } = true;
    public bool ReferenceCurve { get; init; } = true;
    public bool DebugLogging { get; init; }
    public bool AutoRefresh { get; init; } = true;
}

public sealed record WorkspaceFile(int Version, WorkspacePreferences Preferences, LocalPreset[] Presets,
    EqConfiguration? Draft = null, string DraftName = "Untitled", LocalPreset? Reference = null,
    int LegacyVolume = 50, string LegacyGain = "Low", string LegacyLed = "On", string LegacyFilter = "Fast Roll-Off Low Latency",
    EqConfiguration? LocalBaseline = null);

public static class WorkspaceStorage
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moondrop", "Workspace");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static WorkspaceFile Load(string path)
    {
        if (!File.Exists(path)) return new(1, new(), []);
        var file = JsonSerializer.Deserialize<WorkspaceFile>(File.ReadAllText(path), JsonOptions) ?? throw new EqPresetException("Empty workspace file.");
        if (file.Version != 1) throw new EqPresetException($"Unsupported workspace version {file.Version}; original file retained.");
        if (file.Preferences is null || file.Presets is null || file.Presets.Select(x => x.Id).Distinct().Count() != file.Presets.Length)
            throw new EqPresetException("Invalid workspace metadata; original file retained.");
        foreach (var preset in file.Presets) preset.Eq.Validate();
        if (file.Draft is { } draft && (draft.Bands is null || draft.Bands.Length != 8 || !draft.Bands.Select(b => b.Index).SequenceEqual(Enumerable.Range(0, 8)) || !double.IsFinite(draft.PreGain) || !double.IsFinite(draft.GlobalGain))) throw new EqPresetException("Invalid remembered draft; original file retained.");
        if (file.LocalBaseline is { } baseline && (baseline.Bands is null || baseline.Bands.Length != 8 || !baseline.Bands.Select(b => b.Index).SequenceEqual(Enumerable.Range(0, 8)))) throw new EqPresetException("Invalid local baseline; original file retained.");
        file.Reference?.Eq.Validate();
        // Device captures can contain unknown filters. Keep the recoverable draft verbatim.
        return file;
    }
    public static void Save(string path, WorkspaceFile file) => AtomicWrite(path, JsonSerializer.Serialize(file, JsonOptions));

    public static LocalPreset Import(string path, string name)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var document = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(path), JsonOptions) ?? throw new EqPresetException("Empty preset.");
            if (document.Version != 1) throw new EqPresetException("Unsupported native preset version.");
            document.Preset.Eq.Validate();
            return document.Preset with { Id = Guid.NewGuid(), Name = name, Source = Path.GetFileName(path), UpdatedAt = DateTimeOffset.UtcNow };
        }
        var parsed = EqPresetParser.Load(path);
        var replacement = EqConfiguration.Flat();
        foreach (var band in parsed.Bands) replacement.Bands[band.Index] = band;
        replacement = replacement with { PreGain = parsed.Preamp ?? 0 };
        replacement.Validate();
        return new(Guid.NewGuid(), name, Path.GetFileName(path), DateTimeOffset.UtcNow, replacement, parsed.ConversionNotes);
    }
    public static string ExportNative(LocalPreset preset) => JsonSerializer.Serialize(new PresetDocument(1, preset), JsonOptions);
    public static string ExportApo(LocalPreset preset)
    {
        preset.Eq.Validate();
        var text = new StringBuilder("# Supported APO subset; global gain and native metadata omitted.\n");
        text.AppendLine($"Preamp: {preset.Eq.PreGain.ToString("R", CultureInfo.InvariantCulture)} dB");
        foreach (var band in preset.Eq.Bands)
        {
            var alias = band.FilterType switch { PeqFilterType.LowShelf2 => "LS", PeqFilterType.HighShelf2 => "HS", PeqFilterType.LowPass2 => "LP", PeqFilterType.HighPass2 => "HP", _ => "PK" };
            text.AppendLine(FormattableString.Invariant($"Filter {band.Index + 1}: {(band.Enabled && band.FilterType != PeqFilterType.Disabled ? "ON" : "OFF")} {alias} Fc {band.Frequency} Hz Gain {band.Gain:R} dB Q {band.Q:R}"));
        }
        return text.ToString();
    }
    private sealed record PresetDocument(int Version, LocalPreset Preset);
}
