using Moondrop.Core.Devices;

namespace Moondrop.Core.Protocol;

public static class DawnPro2Protocol
{
    public const ushort VendorId = 0x35D8;
    public const ushort ProductId = 0x011D;
    public const byte ReportId = 75;
    public const int ReportLength = 64;
    public const int PayloadLength = 63;
    public const int SampleRate = 96000;
    public const byte PeqIndex = 7;

    public const byte Write = 0x01;
    public const byte Read = 0x80;
    public const byte FirmwareVersion = 12;
    public const byte ActiveEq = 15;
    public const byte UpdateEq = 9;
    public const byte UpdateEqCoeffToReg = 10;
    public const byte SaveEqToFlash = 1;
    public const byte DacOffset = 3;
    public const byte PreGain = 35;
    public const byte SaveOffsetToFlash = 4;

    public static byte[] CreatePacket(IReadOnlyList<byte> payload)
    {
        if (payload.Count > PayloadLength)
            throw new ArgumentException("payload cannot exceed 63 bytes", nameof(payload));
        var packet = new byte[ReportLength];
        packet[0] = ReportId;
        for (var i = 0; i < payload.Count; i++)
            packet[i + 1] = payload[i];
        return packet;
    }

    public static byte[] NormalizeResponse(IReadOnlyList<byte> response)
    {
        if (response.Count != ReportLength)
            throw new IOException($"Invalid Dawn Pro 2 HID response length: expected exactly {ReportLength} bytes, received {response.Count}.");
        if (response[0] != ReportId)
            throw new IOException($"Invalid Dawn Pro 2 HID report ID: expected {ReportId}, received {response[0]}.");
        return response.Skip(1).ToArray();
    }

    public static byte[] EncodeFixedPoint(double value)
    {
        var raw = (short)Math.Round(value * 256.0, MidpointRounding.ToEven);
        return [(byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF)];
    }

    public static double DecodeFixedPoint(byte low, byte high) => (short)(low | (high << 8)) / 256.0;

    public static byte[] GeneratePeqCoefficientBytes(int frequency, double gain, double q, PeqFilterType filterType)
    {
        if (filterType == PeqFilterType.Disabled)
            return new byte[20];
        if (frequency is < 20 or > 20000)
            throw new ArgumentOutOfRangeException(nameof(frequency), "frequency must be between 20 and 20000 Hz");
        ValidateGain(gain);
        if (!double.IsFinite(q) || q <= 0 || q > 127)
            throw new ArgumentOutOfRangeException(nameof(q), "q must be finite, greater than 0, and at most 127");

        var coefficients = filterType switch
        {
            PeqFilterType.LowShelf2 => LowShelf2(frequency, gain, q),
            PeqFilterType.Peaking => Peaking(frequency, gain, q),
            PeqFilterType.HighShelf2 => HighShelf2(frequency, gain, q),
            PeqFilterType.LowPass2 => LowPass2(frequency, q),
            PeqFilterType.HighPass2 => HighPass2(frequency, q),
            _ => throw new ArgumentOutOfRangeException(nameof(filterType), "Invalid filter type")
        };
        var bytes = new byte[20];
        for (var i = 0; i < coefficients.Length; i++)
        {
            var wrapped = unchecked((uint)coefficients[i]);
            BitConverter.GetBytes(wrapped).CopyTo(bytes, i * 4);
        }
        return bytes;
    }

    public static byte[] BuildWriteBandPayload(int index, PeqBand band)
    {
        ValidatePeqIndex(index);
        if (band.Frequency is < 20 or > 20000)
            throw new ArgumentOutOfRangeException(nameof(band), "band frequency must be between 20 and 20000 Hz");
        ValidateGain(band.Gain);
        if (!double.IsFinite(band.Q) || band.Q <= 0 || band.Q > 127)
            throw new ArgumentOutOfRangeException(nameof(band), "band Q must be finite, greater than 0, and at most 127");
        var filterType = band.Enabled ? band.FilterType : PeqFilterType.Disabled;
        if (filterType == PeqFilterType.Unknown)
            throw new InvalidOperationException($"Band {index + 1} uses unknown device filter code {band.RawFilterCode?.ToString() ?? "(missing)"}; select a supported filter before applying it.");
        var payload = new byte[PayloadLength];
        payload[0] = Write;
        payload[1] = UpdateEq;
        payload[4] = (byte)index;
        GeneratePeqCoefficientBytes(band.Frequency, band.Gain, band.Q, filterType).CopyTo(payload, 7);
        payload[27] = (byte)(band.Frequency & 0xFF);
        payload[28] = (byte)((band.Frequency >> 8) & 0xFF);
        EncodeFixedPoint(band.Q).CopyTo(payload, 29);
        EncodeFixedPoint(band.Gain).CopyTo(payload, 31);
        payload[33] = (byte)filterType;
        payload[35] = PeqIndex;
        return payload;
    }

    public static byte[] BuildEnableBandPayload(int index)
    {
        ValidatePeqIndex(index);
        var payload = new byte[PayloadLength];
        payload[0] = Write;
        payload[1] = UpdateEqCoeffToReg;
        payload[2] = (byte)index;
        payload[4] = 0xFF;
        payload[5] = 0xFF;
        payload[6] = 0xFF;
        return payload;
    }

    public static byte[] BuildReadFirmwarePayload() => [Read, FirmwareVersion, 0];

    public static byte[] BuildReadActiveEqPayload() => [Read, ActiveEq, 0];

    public static byte[] BuildWriteActiveEqPayload(int index)
    {
        ValidateActiveEq(index);
        return [Write, ActiveEq, 0, (byte)index];
    }

    public static byte[] BuildReadPreGainPayload() => [Read, PreGain, 0];

    public static byte[] BuildWritePreGainPayload(double value)
    {
        ValidateGain(value);
        return [Write, PreGain, 0, .. EncodeFixedPoint(value)];
    }

    public static byte[] BuildReadGlobalGainPayload() => [Read, DacOffset, 0];

    public static byte[] BuildWriteGlobalGainPayload(double value)
    {
        ValidateGain(value);
        return [Write, DacOffset, 0, .. EncodeFixedPoint(value)];
    }

    public static byte[] BuildReadBandPayload(int index)
    {
        ValidatePeqIndex(index);
        return [Read, UpdateEq, 0, 0, (byte)index];
    }

    public static byte[] BuildSaveEqToFlashPayload() => [Write, SaveEqToFlash, 0];

    public static byte[] BuildSaveOffsetToFlashPayload() => [Write, SaveOffsetToFlash, 0];

    public static IReadOnlyList<byte[]> BuildWriteAllBandPayloads(IReadOnlyList<PeqBand> bands, bool save)
    {
        if (bands.Count > 8)
            throw new ArgumentException("cannot write more than 8 PEQ bands", nameof(bands));
        var seen = new HashSet<int>();
        foreach (var band in bands)
        {
            ValidatePeqIndex(band.Index);
            if (!seen.Add(band.Index))
                throw new ArgumentException($"duplicate PEQ band index: {band.Index}", nameof(bands));
        }

        var payloads = new List<byte[]>(bands.Count * 2 + (save ? 1 : 0));
        foreach (var band in bands)
        {
            payloads.Add(BuildWriteBandPayload(band.Index, band));
            payloads.Add(BuildEnableBandPayload(band.Index));
        }
        if (save)
            payloads.Add([Write, SaveEqToFlash, 0]);
        return payloads;
    }

    public static PeqBand ParseBandPayload(int index, IReadOnlyList<byte> payload)
    {
        ValidatePeqIndex(index);
        if (payload.Count < 34)
            throw new ArgumentException("PEQ band payload must include Python read offsets through byte 33", nameof(payload));
        var frequency = payload[27] | (payload[28] << 8);
        var q = DecodeFixedPoint(payload[29], payload[30]);
        var gain = DecodeFixedPoint(payload[31], payload[32]);
        var rawFilter = payload[33];
        var filter = rawFilter <= (byte)PeqFilterType.HighPass2 ? (PeqFilterType)rawFilter : PeqFilterType.Unknown;
        return new PeqBand(index, frequency, q, gain, filter, filter != PeqFilterType.Disabled, filter == PeqFilterType.Unknown ? rawFilter : null);
    }

    public static RawPeqBandState ParseRawBandPayload(int index, IReadOnlyList<byte> payload)
    {
        ValidatePeqIndex(index);
        if (payload.Count != PayloadLength)
            throw new ArgumentException($"Raw PEQ band payload must contain exactly {PayloadLength} bytes.", nameof(payload));
        var state = new RawPeqBandState(index, payload);
        ValidateRawBandState(state);
        return state;
    }

    public static byte[] BuildWriteRawBandPayload(RawPeqBandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateRawBandState(state);
        var captured = state.NormalizedPayload;
        var payload = new byte[PayloadLength];
        payload[0] = Write;
        payload[1] = UpdateEq;
        payload[4] = (byte)state.Index;
        for (var index = 7; index <= 33; index++)
            payload[index] = captured[index];
        payload[35] = PeqIndex;
        return payload;
    }

    public static RawPeqBandState CreateRawBandStateFromTemplate(RawPeqBandState template, PeqBand band)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(band);
        ValidateRawBandState(template);
        if (band.Index != template.Index)
            throw new InvalidOperationException($"Temporary band index {band.Index} does not match raw template index {template.Index}.");
        var generated = BuildWriteBandPayload(band.Index, band);
        var payload = template.NormalizedPayload.ToArray();
        Array.Copy(generated, 7, payload, 7, 27);
        return ParseRawBandPayload(template.Index, payload);
    }

    public static void ValidateRawBandState(RawPeqBandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidatePeqIndex(state.Index);
        if (state.NormalizedPayload.Count != PayloadLength)
            throw new InvalidOperationException($"Raw band {state.Index} must retain exactly {PayloadLength} normalized bytes.");
        if (state.Frequency is < 20 or > 20000)
            throw new InvalidOperationException($"Raw band {state.Index} frequency {state.Frequency} is outside 20..20000 Hz.");
        if (state.QRaw <= 0 || state.QRaw > 127 * 256)
            throw new InvalidOperationException($"Raw band {state.Index} Q8.8 value {state.QRaw} is outside (0, 127].");
        if (state.GainRaw is < -18 * 256 or > 12 * 256)
            throw new InvalidOperationException($"Raw band {state.Index} gain Q8.8 value {state.GainRaw} is outside -18..12 dB.");
        if (state.FilterCode > (byte)PeqFilterType.HighPass2)
            throw new InvalidOperationException($"Raw band {state.Index} has unknown filter code {state.FilterCode}.");
    }

    public static BiquadMagnitudeResponse PrepareMagnitudeResponse(PeqBand band)
    {
        if (!band.Enabled || band.FilterType is PeqFilterType.Disabled or PeqFilterType.Unknown)
            return BiquadMagnitudeResponse.Disabled;
        var (b, a) = NormalizedBiquad(band.Frequency, band.Gain, band.Q, band.FilterType);
        return new BiquadMagnitudeResponse(true, b[0], b[1], b[2], a[1], a[2]);
    }

    public static double MagnitudeDb(PeqBand band, double frequencyHz) =>
        PrepareMagnitudeResponse(band).MagnitudeDb(frequencyHz);

    private static long[] Scale(double[] numerator, double[] denominator)
    {
        if (numerator.Any(value => !double.IsFinite(value)) || denominator.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("PEQ parameters produce non-finite biquad coefficients; reduce shelf Q or choose valid finite values.");
        return [(long)Math.Round(denominator[0] * 1073741824), (long)Math.Round(denominator[1] * 1073741824), (long)Math.Round(denominator[2] * 1073741824), -(long)Math.Round(numerator[1] * 1073741824), -(long)Math.Round(numerator[2] * 1073741824)];
    }

    private static (double[] b, double[] a) NormalizedBiquad(int frequency, double gain, double q, PeqFilterType type)
    {
        var scaled = type switch
        {
            PeqFilterType.LowShelf2 => LowShelf2(frequency, gain, q),
            PeqFilterType.Peaking => Peaking(frequency, gain, q),
            PeqFilterType.HighShelf2 => HighShelf2(frequency, gain, q),
            PeqFilterType.LowPass2 => LowPass2(frequency, q),
            PeqFilterType.HighPass2 => HighPass2(frequency, q),
            _ => [1073741824, 0, 0, 0, 0]
        };
        return ([scaled[0] / 1073741824.0, scaled[1] / 1073741824.0, scaled[2] / 1073741824.0], [1, -scaled[3] / 1073741824.0, -scaled[4] / 1073741824.0]);
    }

    private static long[] LowShelf2(int f, double gain, double q)
    {
        var amp = Math.Pow(10, gain / 40);
        var omega = f * Math.PI * 2 / SampleRate;
        var alpha = Math.Sin(omega) / 2 * Math.Sqrt((amp + 1 / amp) * (1 / q - 1) + 2);
        var c = Math.Cos(omega);
        var d = amp + 1 + (amp - 1) * c + 2 * Math.Sqrt(amp) * alpha;
        return Scale([1, -2 * (amp - 1 + (amp + 1) * c) / d, (amp + 1 + (amp - 1) * c - 2 * Math.Sqrt(amp) * alpha) / d], [amp * (amp + 1 - (amp - 1) * c + 2 * Math.Sqrt(amp) * alpha) / d, 2 * amp * (amp - 1 - (amp + 1) * c) / d, amp * (amp + 1 - (amp - 1) * c - 2 * Math.Sqrt(amp) * alpha) / d]);
    }

    private static long[] Peaking(int f, double gain, double q)
    {
        var amp = Math.Sqrt(Math.Pow(10, gain / 20));
        var omega = f * Math.PI * 2 / SampleRate;
        var alpha = Math.Sin(omega) / (2 * q);
        var c = Math.Cos(omega);
        var d = alpha / amp + 1;
        return Scale([1, c * -2 / d, (1 - alpha / amp) / d], [(alpha * amp + 1) / d, c * -2 / d, (1 - alpha * amp) / d]);
    }

    private static long[] HighShelf2(int f, double gain, double q)
    {
        var amp = Math.Pow(10, gain / 40);
        var omega = f * Math.PI * 2 / SampleRate;
        var alpha = Math.Sin(omega) / 2 * Math.Sqrt((amp + 1 / amp) * (1 / q - 1) + 2);
        var c = Math.Cos(omega);
        var d = amp + 1 - (amp - 1) * c + 2 * Math.Sqrt(amp) * alpha;
        return Scale([1, 2 * (amp - 1 - (amp + 1) * c) / d, (amp + 1 - (amp - 1) * c - 2 * Math.Sqrt(amp) * alpha) / d], [amp * (amp + 1 + (amp - 1) * c + 2 * Math.Sqrt(amp) * alpha) / d, -2 * amp * (amp - 1 + (amp + 1) * c) / d, amp * (amp + 1 + (amp - 1) * c - 2 * Math.Sqrt(amp) * alpha) / d]);
    }

    private static long[] LowPass2(int f, double q)
    {
        var omega = f * Math.PI * 2 / SampleRate;
        var alpha = Math.Sin(omega) / (2 * q);
        var c = Math.Cos(omega);
        var d = alpha + 1;
        return Scale([1, c * -2 / d, (1 - alpha) / d], [(1 - c) / 2 / d, (1 - c) / d, (1 - c) / 2 / d]);
    }

    private static long[] HighPass2(int f, double q)
    {
        var omega = f * Math.PI * 2 / SampleRate;
        var alpha = Math.Sin(omega) / (2 * q);
        var c = Math.Cos(omega);
        var d = alpha + 1;
        return Scale([1, c * -2 / d, (1 - alpha) / d], [(1 + c) / 2 / d, (-1 - c) / d, (1 + c) / 2 / d]);
    }

    private static void ValidatePeqIndex(int index)
    {
        if (index is < 0 or >= 8)
            throw new ArgumentOutOfRangeException(nameof(index), "PEQ band index must be between 0 and 7");
    }

    private static void ValidateActiveEq(int index)
    {
        if (index is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(index), "EQ index must be between 0 and 15");
    }

    private static void ValidateGain(double value)
    {
        if (!double.IsFinite(value) || value is < -18 or > 12)
            throw new ArgumentOutOfRangeException(nameof(value), "gain must be between -18 and 12 dB");
    }
}

public readonly record struct BiquadMagnitudeResponse(bool Enabled, double B0, double B1, double B2, double A1, double A2)
{
    public static BiquadMagnitudeResponse Disabled { get; } = new(false, 1, 0, 0, 0, 0);

    public double MagnitudeDb(double frequencyHz)
    {
        if (!Enabled)
            return 0;
        var omega = 2 * Math.PI * frequencyHz / DawnPro2Protocol.SampleRate;
        var cos1 = Math.Cos(omega);
        var sin1 = Math.Sin(omega);
        var cos2 = Math.Cos(2 * omega);
        var sin2 = Math.Sin(2 * omega);
        var nr = B0 + B1 * cos1 + B2 * cos2;
        var ni = -B1 * sin1 - B2 * sin2;
        var dr = 1 + A1 * cos1 + A2 * cos2;
        var di = -A1 * sin1 - A2 * sin2;
        var magnitude = Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        return 20 * Math.Log10(Math.Max(magnitude, 1e-12));
    }
}
