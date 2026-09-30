using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Eq;
using Moondrop.Core.Protocol;

namespace Moondrop.Tests;

[TestClass]
public sealed class ProtocolRegressionTests
{
    [TestMethod]
    public void Pro2CreatePacketRejectsOversizedPayload()
    {
        AssertEx.ThrowsException<ArgumentException>(() => DawnPro2Protocol.CreatePacket(Enumerable.Repeat((byte)1, 64).ToArray()));
    }

    [TestMethod]
    public void Pro2ActiveEqAllowsFullZeroToFifteenRange()
    {
        Assert.AreEqual(0, DawnPro2Protocol.BuildWriteActiveEqPayload(0)[3]);
        Assert.AreEqual(15, DawnPro2Protocol.BuildWriteActiveEqPayload(15)[3]);
    }

    [TestMethod]
    public void Pro2ActiveEqRejectsOutOfRange()
    {
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWriteActiveEqPayload(-1));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWriteActiveEqPayload(16));
    }

    [TestMethod]
    public void Pro2GainValidatorsRejectOutOfRangeAndNonFinite()
    {
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWritePreGainPayload(-18.01));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWriteGlobalGainPayload(12.01));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWritePreGainPayload(double.NaN));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildWriteGlobalGainPayload(double.PositiveInfinity));
    }

    [TestMethod]
    public void Pro2CoefficientValidationRejectsNonFiniteAndInvalidShelfDomains()
    {
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.GeneratePeqCoefficientBytes(1000, double.NaN, 1, PeqFilterType.Peaking));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.GeneratePeqCoefficientBytes(1000, 0, double.NaN, PeqFilterType.Peaking));
        AssertEx.ThrowsException<ArgumentException>(() => DawnPro2Protocol.GeneratePeqCoefficientBytes(1000, 12, 10, PeqFilterType.LowShelf2));
    }

    [TestMethod]
    public void Pro2PeqBandValidatorsRejectInvalidIndexes()
    {
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildReadBandPayload(-1));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildReadBandPayload(8));
        AssertEx.ThrowsException<ArgumentOutOfRangeException>(() => DawnPro2Protocol.BuildEnableBandPayload(8));
    }

    [TestMethod]
    public void Pro2FixedPointEncodesEdgeValues()
    {
        CollectionAssert.AreEqual(new byte[] { 0x00, 0xEE }, DawnPro2Protocol.EncodeFixedPoint(-18));
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x0C }, DawnPro2Protocol.EncodeFixedPoint(12));
        Assert.AreEqual(-18, DawnPro2Protocol.DecodeFixedPoint(0x00, 0xEE));
        Assert.AreEqual(12, DawnPro2Protocol.DecodeFixedPoint(0x00, 0x0C));
    }

    [TestMethod]
    public void Pro2FixedPointUsesPythonTiesToEvenRounding()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, DawnPro2Protocol.EncodeFixedPoint(1.0 / 512));
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, DawnPro2Protocol.EncodeFixedPoint(-1.0 / 512));
        CollectionAssert.AreEqual(new byte[] { 2, 0 }, DawnPro2Protocol.EncodeFixedPoint(3.0 / 512));
    }

    [TestMethod]
    public void Pro2NormalizeResponseStripsReportId()
    {
        var response = new byte[DawnPro2Protocol.ReportLength];
        response[0] = DawnPro2Protocol.ReportId;
        response[1] = 1;
        response[2] = 2;
        response[3] = 3;
        var normalized = DawnPro2Protocol.NormalizeResponse(response);
        Assert.HasCount(DawnPro2Protocol.PayloadLength, normalized);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, normalized.Take(3).ToArray());
    }

    [TestMethod]
    public void Pro2NormalizeResponseRejectsRawPayloadWithoutReportEnvelope()
    {
        var response = Enumerable.Range(0, DawnPro2Protocol.PayloadLength).Select(x => (byte)x).ToArray();
        AssertEx.ThrowsException<IOException>(() => DawnPro2Protocol.NormalizeResponse(response));
    }

    [TestMethod]
    public void Pro2NormalizeResponseRejectsTimeout()
    {
        AssertEx.ThrowsException<IOException>(() => DawnPro2Protocol.NormalizeResponse([]));
    }

    [TestMethod]
    public void Pro2NormalizeResponseRequiresExactReportEnvelope()
    {
        AssertEx.ThrowsException<IOException>(
            () => DawnPro2Protocol.NormalizeResponse(new byte[DawnPro2Protocol.PayloadLength]));
        AssertEx.ThrowsException<IOException>(
            () => DawnPro2Protocol.NormalizeResponse(new byte[DawnPro2Protocol.ReportLength]));
    }

    [TestMethod]
    public void Pro2ReadBandPayloadParsesPythonOffsets()
    {
        var payload = new byte[DawnPro2Protocol.PayloadLength];
        payload[27] = 0x34;
        payload[28] = 0x12;
        DawnPro2Protocol.EncodeFixedPoint(0.75).CopyTo(payload, 29);
        DawnPro2Protocol.EncodeFixedPoint(-3.25).CopyTo(payload, 31);
        payload[33] = (byte)PeqFilterType.LowShelf2;

        var band = DawnPro2Protocol.ParseBandPayload(5, payload);

        Assert.AreEqual(5, band.Index);
        Assert.AreEqual(0x1234, band.Frequency);
        Assert.AreEqual(0.75, band.Q);
        Assert.AreEqual(-3.25, band.Gain);
        Assert.AreEqual(PeqFilterType.LowShelf2, band.FilterType);
        Assert.IsTrue(band.Enabled);
    }

    [TestMethod]
    public void Pro2RawBandWriterCopiesOnlyProvenStateFieldsIntoCanonicalPayload()
    {
        var payload = new byte[DawnPro2Protocol.PayloadLength];
        payload[2] = 0x92;
        payload[4] = 0xA4;
        for (var index = 0; index < 20; index++)
            payload[7 + index] = (byte)(0x40 + index);
        payload[27] = 0xE8;
        payload[28] = 0x03;
        DawnPro2Protocol.EncodeFixedPoint(1.25).CopyTo(payload, 29);
        DawnPro2Protocol.EncodeFixedPoint(-2.5).CopyTo(payload, 31);
        payload[33] = (byte)PeqFilterType.Peaking;
        payload[34] = 0xA5;
        payload[35] = 0xB5;
        for (var index = 36; index < payload.Length; index++)
            payload[index] = (byte)index;

        var state = DawnPro2Protocol.ParseRawBandPayload(3, payload);
        var restorationPayload = DawnPro2Protocol.BuildWriteRawBandPayload(state);

        CollectionAssert.AreEqual(payload.Skip(7).Take(20).ToArray(), state.CoefficientBytes.ToArray());
        Assert.AreEqual((short)320, state.QRaw);
        Assert.AreEqual((short)-640, state.GainRaw);
        Assert.AreEqual(DawnPro2Protocol.Write, restorationPayload[0]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, restorationPayload[1]);
        Assert.AreEqual(3, restorationPayload[4]);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, restorationPayload[35]);
        CollectionAssert.AreEqual(payload.Skip(7).Take(27).ToArray(), restorationPayload.Skip(7).Take(27).ToArray());
        Assert.IsTrue(restorationPayload.Where((_, index) =>
            index is not 0 and not 1 and not 4 and not 35 && (index < 7 || index > 33)).All(value => value == 0));
    }

    [TestMethod]
    public void Pro2RawReadKeepsOpaqueSelectorsForDiagnosticsButNeverReplaysThem()
    {
        var payload = new byte[DawnPro2Protocol.PayloadLength];
        payload[4] = 0xA4;
        payload[27] = 0xE8;
        payload[28] = 0x03;
        DawnPro2Protocol.EncodeFixedPoint(1).CopyTo(payload, 29);
        payload[33] = (byte)PeqFilterType.Peaking;
        payload[35] = 0xB5;

        var state = DawnPro2Protocol.ParseRawBandPayload(3, payload);
        var restorationPayload = DawnPro2Protocol.BuildWriteRawBandPayload(state);

        Assert.AreEqual((byte)0xA4, state.OpaqueByte4);
        Assert.AreEqual((byte)0xB5, state.OpaqueByte35);
        Assert.AreEqual(3, restorationPayload[4]);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, restorationPayload[35]);
    }

    [TestMethod]
    public void Pro2ParseBandPayloadRejectsShortPayload()
    {
        AssertEx.ThrowsException<ArgumentException>(() => DawnPro2Protocol.ParseBandPayload(0, new byte[33]));
    }

    [TestMethod]
    public void Pro2UnknownFilterCodeIsPreservedAndCannotBeSilentlyRewritten()
    {
        var payload = new byte[DawnPro2Protocol.PayloadLength];
        payload[27] = 0xE8;
        payload[28] = 0x03;
        DawnPro2Protocol.EncodeFixedPoint(1).CopyTo(payload, 29);
        payload[33] = 19;

        var band = DawnPro2Protocol.ParseBandPayload(0, payload);

        Assert.AreEqual(PeqFilterType.Unknown, band.FilterType);
        Assert.AreEqual((byte)19, band.RawFilterCode);
        Assert.IsTrue(band.Enabled);
        AssertEx.ThrowsException<InvalidOperationException>(() => DawnPro2Protocol.BuildWriteBandPayload(0, band));
    }

    [TestMethod]
    public void DisabledFilterProducesZeroCoefficientsAndMagnitude()
    {
        CollectionAssert.AreEqual(new byte[20], DawnPro2Protocol.GeneratePeqCoefficientBytes(1000, 9, 1, PeqFilterType.Disabled));
        Assert.AreEqual(0, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 1000, 1, 9, PeqFilterType.Disabled, false), 1000));
    }

    [TestMethod]
    public void PeakingMagnitudeMatchesGainAtCenter()
    {
        Assert.AreEqual(6, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 1000, 1, 6, PeqFilterType.Peaking), 1000), 0.05);
    }

    [TestMethod]
    public void LowShelfMagnitudeTracksLowFrequencyBoost()
    {
        Assert.IsGreaterThan(5, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 200, 0.707, 6, PeqFilterType.LowShelf2), 40));
    }

    [TestMethod]
    public void HighShelfMagnitudeTracksHighFrequencyBoost()
    {
        Assert.IsGreaterThan(5, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 4000, 0.707, 6, PeqFilterType.HighShelf2), 16000));
    }

    [TestMethod]
    public void LowPassMagnitudeAttenuatesAboveCutoff()
    {
        Assert.IsLessThan(-30, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 1000, 0.707, 0, PeqFilterType.LowPass2), 10000));
    }

    [TestMethod]
    public void HighPassMagnitudeAttenuatesBelowCutoff()
    {
        Assert.IsLessThan(-40, DawnPro2Protocol.MagnitudeDb(new PeqBand(0, 1000, 0.707, 0, PeqFilterType.HighPass2), 50));
    }

    [TestMethod]
    public void PreparedBiquadResponseMatchesConvenienceMagnitudePath()
    {
        var band = new PeqBand(0, 1900, 0.71, 4.5, PeqFilterType.HighShelf2);
        var prepared = DawnPro2Protocol.PrepareMagnitudeResponse(band);

        foreach (var frequency in new[] { 20d, 100d, 1000d, 1900d, 10000d, 20000d })
            Assert.AreEqual(DawnPro2Protocol.MagnitudeDb(band, frequency), prepared.MagnitudeDb(frequency), 1e-12);
    }

    [TestMethod]
    public void LegacyVolumeTableAndControlPacketsMatchPython()
    {
        CollectionAssert.AreEqual(
            new[] { 0xFF, 0xC8, 0xB4, 0xAA, 0xA0, 0x96, 0x8C, 0x82, 0x7A, 0x74, 0x6E, 0x6A, 0x66, 0x62, 0x5E, 0x5A, 0x58, 0x56, 0x54, 0x52, 0x50, 0x4E, 0x4C, 0x4A, 0x48, 0x46, 0x44, 0x42, 0x40, 0x3E, 0x3C, 0x3A, 0x38, 0x36, 0x34, 0x32, 0x30, 0x2E, 0x2C, 0x2A, 0x28, 0x26, 0x24, 0x22, 0x20, 0x1E, 0x1C, 0x1A, 0x18, 0x16, 0x14, 0x12, 0x10, 0x0E, 0x0C, 0x0A, 0x08, 0x06, 0x04, 0x02, 0x00 },
            LegacyProtocol.VolumeTable);
        CollectionAssert.AreEqual(new byte[] { 192, 165, 4, 0x3C }, LegacyProtocol.SetVolumePayload(30));
        CollectionAssert.AreEqual(new byte[] { 192, 165, 2, 1 }, LegacyProtocol.SetGainPayload("High"));
        CollectionAssert.AreEqual(new byte[] { 192, 165, 6, 2 }, LegacyProtocol.SetLedPayload("Off"));
        CollectionAssert.AreEqual(new byte[] { 192, 165, 1, 4 }, LegacyProtocol.SetFilterPayload("Non-Oversampling"));
    }

    [TestMethod]
    public void LegacyUnknownDeviceValuesPreservePythonInvalidStateLabels()
    {
        Assert.AreEqual("Invalid LED Status", LegacyProtocol.ConvertLedStatusToString(99));
        Assert.AreEqual("Invalid Gain Value", LegacyProtocol.ConvertGainToString(99));
        Assert.AreEqual("Invalid Filter Value", LegacyProtocol.ConvertFilterPayloadToString(99));
    }

    [TestMethod]
    public void Pro2PacketsOffsetsAndCoefficientWrappingMatchPython()
    {
        var packet = DawnPro2Protocol.CreatePacket(new byte[] { DawnPro2Protocol.Read, DawnPro2Protocol.FirmwareVersion, 0 });
        Assert.HasCount(64, packet);
        CollectionAssert.AreEqual(new byte[] { 75, 128, 12, 0 }, packet.Take(4).ToArray());
        Assert.AreEqual(0, packet[^1]);

        CollectionAssert.AreEqual(
            new byte[] { 0x1F, 0xB7, 0xA4, 0x68, 0x5D, 0xBD, 0x7B, 0x41, 0xDE, 0x38, 0x04, 0x57, 0x70, 0x9E, 0x40, 0x71, 0x36, 0xB4, 0x9A, 0xCD },
            DawnPro2Protocol.GeneratePeqCoefficientBytes(1900, 4.5, 0.71, PeqFilterType.HighShelf2));

        var band = new PeqBand(2, 1000, 1.0, 3.0, PeqFilterType.Peaking, true);
        var payload = DawnPro2Protocol.BuildWriteBandPayload(2, band);
        Assert.HasCount(63, payload);
        Assert.AreEqual(DawnPro2Protocol.Write, payload[0]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, payload[1]);
        Assert.AreEqual(2, payload[4]);
        Assert.AreEqual(0xE8, payload[27]);
        Assert.AreEqual(0x03, payload[28]);
        Assert.AreEqual((byte)PeqFilterType.Peaking, payload[33]);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, payload[35]);
        CollectionAssert.AreEqual(new byte[] { DawnPro2Protocol.Write, DawnPro2Protocol.UpdateEqCoeffToReg, 2, 0, 0xFF, 0xFF, 0xFF }, DawnPro2Protocol.BuildEnableBandPayload(2).Take(7).ToArray());
    }

    [TestMethod]
    public void EqPresetDecimalParsingIsCultureInvariantLikePython()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var preset = EqPresetParser.Parse("Preamp: -1.5 dB\nFilter 1: ON PK Fc 1000.5 Hz Gain 2.5 dB Q 0.75");
            Assert.AreEqual(-1.5, preset.Preamp);
            Assert.AreEqual(1000, preset.Bands[0].Frequency);
            Assert.AreEqual(2.5, preset.Bands[0].Gain);
            Assert.AreEqual(0.75, preset.Bands[0].Q);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public void ConfigAndPresetCompatibilityMatchPython()
    {
        var json = """
        {"default_settings":{"DEFAULT_VOLUME":42,"DEFAULT_LED_STATUS":"Off","DEFAULT_GAIN":"High","DEFAULT_FILTER":"Non-Oversampling","FUTURE":true},"new_root":1}
        """;
        var config = AppConfig.LoadJson(json);
        Assert.AreEqual(42, config.DefaultSettings.DefaultVolume);
        Assert.AreEqual(0, config.DawnPro2Settings.DefaultEqIndex);

        var preset = EqPresetParser.Parse("""
        Preamp: -5.5 dB
        Filter 1: OFF PK Fc 1000 Hz Gain -2 dB Q 1.4
        Filter 5: ON LPQ Fc 15000 Hz Q 0.707
        Filter 8: ON HP Fc 25 Hz Q 0.71
        """);

        Assert.AreEqual(-5.5, preset.Preamp);
        CollectionAssert.AreEqual(new[] { 0, 4, 7 }, preset.Bands.Select(b => b.Index).ToArray());
        Assert.IsFalse(preset.Bands[0].Enabled);
        Assert.AreEqual(PeqFilterType.LowPass2, preset.Bands[1].FilterType);
        Assert.AreEqual(0.0, preset.Bands[1].Gain);
    }

    [TestMethod]
    public void PresetParserReadsUtf8CompatiblePreampAndBands()
    {
        var preset = EqPresetParser.Parse("""
        Preamp: 1.5 dB
        Filter 1: ON PK Fc 1000 Hz Gain -2.0 dB Q 1.25
        """);

        Assert.AreEqual(1.5, preset.Preamp);
        Assert.HasCount(1, preset.Bands);
        Assert.AreEqual(0, preset.Bands[0].Index);
    }

    [TestMethod]
    public void PresetParserRejectsDuplicatePreamp()
    {
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("""
        Preamp: 0 dB
        Preamp: 1 dB
        Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1
        """));
    }

    [TestMethod]
    public void PresetParserRejectsDuplicateFilter()
    {
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("""
        Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1
        Filter 1: ON PK Fc 1200 Hz Gain 0 dB Q 1
        """));
    }

    [TestMethod]
    public void PresetParserRejectsUnsupportedLines()
    {
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("GraphicEQ: 20 0; 40 0"));
    }

    [TestMethod]
    public void PresetParserRejectsFrequencyQGainAndPreampRanges()
    {
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 1: ON PK Fc 19 Hz Gain 0 dB Q 1"));
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 1: ON PK Fc 1000 Hz Gain -18.1 dB Q 1"));
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 0"));
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("""
        Preamp: 12.1 dB
        Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1
        """));
    }

    [TestMethod]
    public void PresetParserRejectsFilterNumberOutsideOneToEight()
    {
        AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 9: ON PK Fc 1000 Hz Gain 0 dB Q 1"));
    }

    [TestMethod]
    public void BackendPriorityAndPro2CommandOrderMatchPython()
    {
        var selection = BackendSelector.Select(
            () => new object(),
            () => throw new InvalidOperationException("legacy should not be tried"));
        Assert.AreEqual(DeviceKind.DawnPro2, selection.Kind);

        var fallback = BackendSelector.Select(
            () => throw new InvalidOperationException("hid missing"),
            () => "legacy");
        Assert.AreEqual(DeviceKind.Legacy, fallback.Kind);
        StringAssert.Contains(fallback.CombinedErrors, "Dawn Pro 2 HID: hid missing");

        InvalidOperationException? failure = null;
        try
        {
            BackendSelector.Select(
                () => throw new InvalidOperationException("hid missing"),
                () => throw new InvalidOperationException("usb missing"));
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }
        Assert.IsNotNull(failure);
        StringAssert.Contains(failure.Message, "No supported Moondrop device found.");
        StringAssert.Contains(failure.Message, "Original Dawn Pro USB: usb missing");

        var bands = new[]
        {
            new PeqBand(2, 1000, 1.0, 0.0, PeqFilterType.Peaking),
            new PeqBand(7, 12000, 0.7, -2.0, PeqFilterType.HighShelf2)
        };
        var commands = DawnPro2Protocol.BuildWriteAllBandPayloads(bands, save: true);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, commands[0][1]);
        Assert.AreEqual(2, commands[0][4]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, commands[1][1]);
        Assert.AreEqual(2, commands[1][2]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, commands[2][1]);
        Assert.AreEqual(7, commands[2][4]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, commands[3][1]);
        Assert.AreEqual(7, commands[3][2]);
        Assert.AreEqual(DawnPro2Protocol.SaveEqToFlash, commands[4][1]);
    }
}
