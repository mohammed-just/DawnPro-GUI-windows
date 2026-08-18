using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Moondrop.Core.Devices;

namespace Moondrop.Core.Eq;

public sealed record EqPreset(IReadOnlyList<PeqBand> Bands, double? Preamp);

public sealed class EqPresetException(string message) : Exception(message);

public static partial class EqPresetParser
{
    private static readonly Dictionary<string, PeqFilterType> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PK"] = PeqFilterType.Peaking,
        ["PEQ"] = PeqFilterType.Peaking,
        ["LS"] = PeqFilterType.LowShelf2,
        ["LSQ"] = PeqFilterType.LowShelf2,
        ["LSC"] = PeqFilterType.LowShelf2,
        ["HS"] = PeqFilterType.HighShelf2,
        ["HSQ"] = PeqFilterType.HighShelf2,
        ["HSC"] = PeqFilterType.HighShelf2,
        ["LP"] = PeqFilterType.LowPass2,
        ["LPQ"] = PeqFilterType.LowPass2,
        ["HP"] = PeqFilterType.HighPass2,
        ["HPQ"] = PeqFilterType.HighPass2
    };

    public static EqPreset Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Parse(File.ReadAllText(path, Encoding.GetEncoding(1252)));
        }
    }

    public static EqPreset Parse(string text)
    {
        var bands = new SortedDictionary<int, PeqBand>();
        double? preamp = null;
        var lineNumber = 0;
        foreach (var rawLine in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var preampMatch = PreampRegex().Match(line);
            if (preampMatch.Success)
            {
                if (preamp.HasValue)
                    throw new EqPresetException($"Line {lineNumber}: duplicate Preamp line.");
                preamp = double.Parse(preampMatch.Groups["gain"].Value, CultureInfo.InvariantCulture);
                if (preamp is < -18 or > 12)
                    throw new EqPresetException($"Line {lineNumber}: preamp must be between -18 and 12 dB.");
                continue;
            }

            var match = FilterRegex().Match(line);
            if (!match.Success)
                throw new EqPresetException($"Line {lineNumber}: unsupported preset line.\n\n{rawLine}");

            var fileIndex = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            if (bands.ContainsKey(fileIndex))
                throw new EqPresetException($"Line {lineNumber}: duplicate Filter {fileIndex}.");
            if (!Aliases.TryGetValue(match.Groups["filter"].Value, out var type))
                throw new EqPresetException($"Line {lineNumber}: unsupported filter type {match.Groups["filter"].Value.ToUpperInvariant()}.");

            var frequency = (int)Math.Round(double.Parse(match.Groups["frequency"].Value, CultureInfo.InvariantCulture));
            var q = double.Parse(match.Groups["q"].Value, CultureInfo.InvariantCulture);
            var gain = match.Groups["gain"].Success ? double.Parse(match.Groups["gain"].Value, CultureInfo.InvariantCulture) : 0.0;
            ValidateBand(lineNumber, fileIndex, frequency, q, gain);
            bands[fileIndex] = new PeqBand(fileIndex - 1, frequency, q, gain, type, string.Equals(match.Groups["state"].Value, "ON", StringComparison.OrdinalIgnoreCase));
        }

        if (bands.Count == 0)
            throw new EqPresetException("The file does not contain any supported Filter lines.");
        return new EqPreset(bands.Values.ToArray(), preamp);
    }

    private static void ValidateBand(int line, int index, int frequency, double q, double gain)
    {
        if (index is < 1 or > 8)
            throw new EqPresetException($"Line {line}: filter number must be between 1 and 8.");
        if (frequency is < 20 or > 20000)
            throw new EqPresetException($"Line {line}: frequency must be between 20 and 20000 Hz.");
        if (q <= 0 || q > 127)
            throw new EqPresetException($"Line {line}: Q must be greater than 0 and at most 127.");
        if (gain is < -18 or > 12)
            throw new EqPresetException($"Line {line}: gain must be between -18 and 12 dB.");
    }

    [GeneratedRegex(@"^\s*Preamp\s*:\s*(?<gain>[+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*dB\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PreampRegex();

    [GeneratedRegex(@"^\s*Filter\s+(?<index>\d+)\s*:\s*(?<state>ON|OFF)\s+(?<filter>[A-Za-z0-9_]+)\s+Fc\s+(?<frequency>[+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*Hz(?:\s+Gain\s+(?<gain>[+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*dB)?\s+Q\s+(?<q>[+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterRegex();
}
