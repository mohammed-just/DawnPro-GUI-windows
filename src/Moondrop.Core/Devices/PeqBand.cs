namespace Moondrop.Core.Devices;

public enum PeqFilterType : byte
{
    Disabled = 0,
    LowShelf2 = 1,
    Peaking = 2,
    HighShelf2 = 3,
    LowPass2 = 4,
    HighPass2 = 5,
    Unknown = 255
}

public sealed record PeqBand(
    int Index,
    int Frequency,
    double Q,
    double Gain,
    PeqFilterType FilterType,
    bool Enabled = true,
    byte? RawFilterCode = null);
