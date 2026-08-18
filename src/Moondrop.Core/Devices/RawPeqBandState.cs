using System.Buffers.Binary;

namespace Moondrop.Core.Devices;

public sealed class RawPeqBandState
{
    private readonly byte[] _normalizedPayload;

    public RawPeqBandState(int index, IReadOnlyList<byte> normalizedPayload)
    {
        Index = index;
        _normalizedPayload = normalizedPayload.ToArray();
    }

    public int Index { get; }
    public IReadOnlyList<byte> NormalizedPayload => _normalizedPayload.ToArray();
    public IReadOnlyList<byte> CoefficientBytes => _normalizedPayload.AsSpan(7, 20).ToArray();
    public int Frequency => BinaryPrimitives.ReadUInt16LittleEndian(_normalizedPayload.AsSpan(27, 2));
    public short QRaw => BinaryPrimitives.ReadInt16LittleEndian(_normalizedPayload.AsSpan(29, 2));
    public short GainRaw => BinaryPrimitives.ReadInt16LittleEndian(_normalizedPayload.AsSpan(31, 2));
    public byte FilterCode => _normalizedPayload[33];
    public byte Metadata34 => _normalizedPayload[34];
    public byte OpaqueByte4 => _normalizedPayload[4];
    public byte OpaqueByte35 => _normalizedPayload[35];

    public PeqBand ToPeqBand()
    {
        var filter = FilterCode <= (byte)PeqFilterType.HighPass2
            ? (PeqFilterType)FilterCode
            : PeqFilterType.Unknown;
        return new PeqBand(
            Index,
            Frequency,
            QRaw / 256.0,
            GainRaw / 256.0,
            filter,
            filter != PeqFilterType.Disabled,
            filter == PeqFilterType.Unknown ? FilterCode : null);
    }
}
