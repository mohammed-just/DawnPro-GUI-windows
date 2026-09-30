using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Eq;
using Moondrop.Core.Protocol;
using Moondrop.Hardware;
using Moondrop.Wpf;

namespace Moondrop.Tests;

[TestClass]
public sealed class WorkspaceTests
{
    private static MainViewModel Model(WorkspaceDevice device) => MainViewModel.CreateHardware(new(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, device.DisplayName, device, "")), new(), false);

    [TestMethod]
    public async Task ApplyingFromPresetsKeepsPageAndGlobalGainChecksReadbackAndNeverFlashes()
    {
        var device = new WorkspaceDevice { State = EqConfiguration.Flat() with { GlobalGain = -2 } };
        await using var model = Model(device); await model.RefreshAsync(); model.Discard();
        model.SaveLocal("Current device");
        var eq = EqConfiguration.Flat() with { PreGain = -3, GlobalGain = 7 };
        eq.Bands[0] = new(0, 105, 0.71, 3, PeqFilterType.LowShelf2);
        var preset = new LocalPreset(Guid.NewGuid(), "Direct apply", "Test", DateTimeOffset.UtcNow, eq);
        model.LocalPresets.Add(preset); model.SelectedPreset = preset; model.SelectedPage = ShellPage.Presets;
        Assert.IsTrue(model.ApplyPresetCommand.CanExecute(null));
        await model.ApplySelectedPresetAsync();
        Assert.AreEqual(ShellPage.Presets, model.SelectedPage);
        Assert.AreEqual(-2, device.State.GlobalGain); Assert.AreEqual(-3, device.State.PreGain);
        Assert.AreEqual(3, device.State.Bands[0].Gain); Assert.IsFalse(model.IsDirty);
        Assert.AreEqual(0, device.FlashWrites); Assert.IsTrue(model.CanSave);
        StringAssert.Contains(model.ActiveConfiguration, "Direct apply");
        var writes = device.Writes; await model.ApplySelectedPresetAsync(); Assert.AreEqual(writes, device.Writes);
        Assert.AreEqual("EQ 9", model.ActiveEqLabel);
    }

    [TestMethod]
    public async Task ApplyingPresetReadbackFailureKeepsDraftAndRequiresRefresh()
    {
        var device = new WorkspaceDevice { Mismatch = true };
        await using var model = Model(device); await model.RefreshAsync();
        var eq = EqConfiguration.Flat(); eq.Bands[0] = new(0, 1000, 1, 3, PeqFilterType.Peaking);
        model.SelectedPreset = new(Guid.NewGuid(), "Failed readback", "Test", DateTimeOffset.UtcNow, eq);
        await model.ApplySelectedPresetAsync();
        Assert.AreEqual(3, model.Bands[0].Gain); Assert.IsTrue(model.Banner.IsVisible);
        Assert.IsFalse(model.CanApplyPreset); Assert.IsFalse(model.CanSave); Assert.AreEqual(0, device.FlashWrites);
    }

    [TestMethod]
    public void DefaultFlatTargetAndExplicitNoneSurviveRestartWithoutChangingEq()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var model = MainViewModel.CreateOffline(directory))
            {
                Assert.AreEqual("Flat", model.ReferenceName); Assert.HasCount(8, model.ReferenceBands);
                Assert.IsTrue(model.ReferenceBands.All(b => !b.Enabled && b.Gain == 0));
                var before = model.Capture(); var flatId = model.ReferenceId;
                model.ReferenceId = Guid.Empty; Assert.AreEqual("None", model.ReferenceName);
                model.ReferenceId = flatId; Assert.AreEqual("Flat", model.ReferenceName);
                Assert.HasCount(1, model.ReferenceOptions.Where(p => p.Id == flatId).ToArray());
                Assert.IsTrue(before.Same(model.Capture())); model.Persist();
            }
            using (var flat = MainViewModel.CreateOffline(directory))
            {
                Assert.AreEqual("Flat", flat.ReferenceName); flat.ReferenceId = Guid.Empty;
            }
            using var none = MainViewModel.CreateOffline(directory);
            Assert.AreEqual("None", none.ReferenceName); Assert.IsEmpty(none.ReferenceBands);
            Assert.IsFalse(none.CanApplyPreset);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task EditingNavigationAndRefreshPreserveDraftWithoutWrites()
    {
        var device = new WorkspaceDevice(); await using var model = Model(device);
        await model.RefreshAsync(); model.Bands[0].Enabled = true; model.Bands[0].Gain = 6; model.PreGain = -6;
        model.SelectedPage = ShellPage.Presets; model.SelectedPage = ShellPage.Settings;
        await model.RefreshAsync();
        Assert.AreEqual(6, model.Bands[0].Gain); Assert.AreEqual(-6, model.PreGain);
        Assert.AreEqual(0, device.Writes); Assert.IsTrue(model.IsDirty); Assert.IsFalse(model.CanSave);
    }
    [TestMethod]
    public async Task ApplyReadsBackCompleteStateWithEncodingToleranceAndNeverFlashes()
    {
        var device = new WorkspaceDevice(); await using var model = Model(device); await model.RefreshAsync();
        model.Edit(() => { model.Bands[0].Enabled = true; model.Bands[0].Q = 0.05; model.Bands[0].Gain = 3.1; model.PreGain = -3.1; model.GlobalGain = 0.1; });
        await model.ApplyAsync();
        Assert.IsFalse(model.IsDirty); Assert.IsTrue(model.CanSave); Assert.AreEqual(0, device.FlashWrites);
        Assert.HasCount(8, device.State.Bands); Assert.AreEqual(0.05, model.Bands[0].Q);
        Assert.AreEqual("Applied — not saved", model.DraftState);
    }
    [TestMethod]
    public async Task ReadbackMismatchKeepsDraftAndRequiresRefreshBeforeRetry()
    {
        var device = new WorkspaceDevice { Mismatch = true }; await using var model = Model(device); await model.RefreshAsync();
        model.Bands[0].Enabled = true; model.Bands[0].Gain = 5; await model.ApplyAsync();
        Assert.AreEqual(5, model.Bands[0].Gain); Assert.AreEqual("Device state uncertain", model.DraftState);
        Assert.IsFalse(model.CanSave); Assert.IsFalse(model.CanApply); device.Mismatch = false;
        await model.RefreshAsync(); Assert.IsTrue(model.CanApply);
    }
    [TestMethod]
    public async Task PartialApplyFailureKeepsDesiredValuesAndReconcilesBeforeRetry()
    {
        var device = new WorkspaceDevice { PartialFailure = true }; await using var model = Model(device); await model.RefreshAsync();
        model.Edit(() => { model.Bands[0].Enabled = true; model.Bands[0].Gain = 5; model.Bands[1].Enabled = true; model.Bands[1].Gain = 4; });
        await model.ApplyAsync();
        Assert.AreEqual(5, model.Bands[0].Gain); Assert.AreEqual(4, model.Bands[1].Gain); Assert.IsFalse(model.CanSave);
        Assert.IsFalse(model.CanApply); Assert.AreEqual(0, device.FlashWrites);
        device.PartialFailure = false; await model.RefreshAsync(); await model.ApplyAsync(); Assert.IsFalse(model.IsDirty);
    }
    [TestMethod]
    public async Task SeparateFlashFailureReportsBothGroupsAndNeverClaimsVerifiedPersistence()
    {
        var device = new WorkspaceDevice { FailEqSave = true }; await using var model = Model(device); await model.RefreshAsync();
        model.Bands[0].Enabled = true; model.Bands[0].Gain = 2; model.PreGain = -2; await model.ApplyAsync(); model.ConfirmFlash = false;
        await model.SaveAsync();
        Assert.AreEqual(2, device.FlashWrites); StringAssert.Contains(model.PersistenceSummary, "EQ: Failed");
        StringAssert.Contains(model.PersistenceSummary, "Gains: Command completed — persistence unverified"); Assert.IsFalse(model.CanSave);
    }
    [TestMethod]
    public async Task SaveRefusesDirtyDraftOrExternallyChangedRam()
    {
        var device = new WorkspaceDevice(); await using var model = Model(device); await model.RefreshAsync();
        model.PreGain = -2; await model.ApplyAsync(); model.PreGain = -3; model.ConfirmFlash = false;
        await model.SaveAsync(); Assert.AreEqual(0, device.FlashWrites);
        model.PreGain = -2; device.State = device.State with { PreGain = -4 }; await model.SaveAsync();
        Assert.AreEqual(0, device.FlashWrites); Assert.AreEqual("Device state uncertain", model.DraftState);
    }
    [TestMethod]
    public async Task NewConnectionInvalidatesPersistenceAndCannotBeWrittenThroughOldSession()
    {
        var first = new WorkspaceDevice(); var next = new WorkspaceDevice();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, first.DisplayName, first, ""));
        service.RegisterReconnectFactory(_ => Task.FromResult<IMoondropDevice>(next));
        await using var model = MainViewModel.CreateHardware(service, new(), false); await model.RefreshAsync(); model.PreGain = -2; await model.ApplyAsync();
        first.IsUsable = false; await model.RefreshAsync();
        Assert.IsFalse(model.CanSave); StringAssert.Contains(model.PersistenceSummary, "new connection session"); Assert.AreEqual(-2, model.PreGain);
        await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() => service.ApplyWorkspaceAsync(first, EqConfiguration.Flat(), true, true));
        Assert.AreEqual(0, next.Writes);
    }
    [TestMethod]
    public async Task LegacyStartupAndSettersAreLocalUntilExplicitApply()
    {
        var device = new WorkspaceLegacy(); var config = new AppConfig().WithLegacyDefaults(40, "High", "Off", "Non-Oversampling");
        await using var model = MainViewModel.CreateHardware(new(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, device.DisplayName, device, "")), config, true);
        await model.InitializeHardwareAsync(); model.LegacyVolume = 42; model.LegacyLed = "Temporarily Off";
        Assert.AreEqual(0, device.Writes); Assert.IsTrue(model.CanApply); await model.ApplyAsync();
        Assert.AreEqual(4, device.Writes); Assert.IsFalse(model.IsDirty); Assert.IsFalse(model.CanSave);
    }
    [TestMethod]
    public void UndoGroupsDragAndResetScopesPreserveGlobalGain()
    {
        using var model = MainViewModel.CreateDemo(); var before = model.Capture(); model.BeginEdit();
        for (var i = 0; i < 10; i++) { model.Bands[0].Gain = i; model.Bands[0].Frequency = 100 + i; }
        model.EndEdit(); model.Undo(); Assert.IsTrue(before.Same(model.Capture()));
        model.Redo(); Assert.AreEqual(9, model.Bands[0].Gain); model.GlobalGain = -2;
        model.ResetEqCommand.Execute(null); Assert.IsTrue(model.Bands.All(b => !b.Enabled)); Assert.AreEqual(0, model.PreGain); Assert.AreEqual(-2, model.GlobalGain);
        model.Undo(); Assert.IsTrue(model.Bands.Any(b => b.Enabled));
    }
    [TestMethod]
    public void HeadroomCombinesOverlappingBoostsAndFindsNarrowPeak()
    {
        var bands = new[] { new PeqBand(0, 1234, 127, 8, PeqFilterType.Peaking), new PeqBand(1, 1234, 127, 7, PeqFilterType.Peaking) };
        Assert.AreEqual(15, EqHeadroom.Peak(bands), 0.001);
        Assert.AreEqual(0, EqHeadroom.Peak(bands.Select(b => b with { Enabled = false })), 0.001);
    }
    [TestMethod]
    public void AutoProtectionIsUndoableDoesNotRaiseLevelAndAllowsManualOverride()
    {
        using var model = MainViewModel.CreateDemo(); var before = model.PreGain;
        model.AutoPreamp = true; Assert.IsLessThan(before, model.PreGain); model.Undo(); Assert.AreEqual(before, model.PreGain);
        model.AutoPreamp = true; var protectedGain = model.PreGain; model.Bands[0].Enabled = false;
        Assert.IsLessThanOrEqualTo(protectedGain, model.PreGain); model.PreGain = -1; Assert.IsFalse(model.AutoPreamp);
        model.Bands[1].Gain = 10; Assert.AreEqual(-1, model.PreGain);
    }
    [TestMethod]
    public void ImportFillsMissingPositionsAndNativeAndApoRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "example.txt"); File.WriteAllText(path, "Preamp: -3.1 dB\nFilter 3: ON PK Fc 1234 Hz Gain 3.1 dB Q 0.05");
            var preset = WorkspaceStorage.Import(path, "Example"); Assert.HasCount(8, preset.Eq.Bands);
            Assert.AreEqual(1, preset.Eq.Bands.Count(b => b.Enabled)); Assert.AreEqual(0.05, preset.Eq.Bands[2].Q);
            File.WriteAllText(path, WorkspaceStorage.ExportApo(preset)); var apo = WorkspaceStorage.Import(path, "Apo"); Assert.IsTrue(preset.Eq.Same(apo.Eq));
            var native = Path.Combine(directory, "example.json"); File.WriteAllText(native, WorkspaceStorage.ExportNative(preset)); Assert.IsTrue(preset.Eq.Same(WorkspaceStorage.Import(native, "Native").Eq));
            AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 9: ON PK Fc 1000 Hz Gain 0 dB Q 1"));
            AssertEx.ThrowsException<EqPresetException>(() => EqPresetParser.Parse("Filter 1: ON NOTREAL Fc 1000 Hz Gain 0 dB Q 1"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public void PresetsPreferencesDraftAndReferenceSurviveRestartWithoutChangingGlobalGain()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var model = MainViewModel.CreateOffline(directory))
            {
                model.Bands[0].Enabled = true; model.Bands[0].Gain = 4; model.PreGain = -4; model.GlobalGain = -2;
                var preset = model.SaveLocal("Keep"); model.ReferencePreset = preset; model.ThemeSelection = "Dark"; model.IndividualCurves = false;
                model.Bands[0].Gain = 5; Assert.AreEqual(4, model.ReferenceBands[0].Gain);
                model.OpenLocalPreset(preset); Assert.AreEqual(-2, model.GlobalGain);
                Assert.IsFalse(model.CanApply); Assert.IsFalse(model.CanSave); model.Persist();
            }
            using var restored = MainViewModel.CreateOffline(directory);
            Assert.HasCount(1, restored.LocalPresets); Assert.AreEqual(4, restored.Bands[0].Gain); Assert.AreEqual(-2, restored.GlobalGain);
            Assert.AreEqual("Dark", restored.ThemeSelection); Assert.IsFalse(restored.IndividualCurves); Assert.AreEqual(4, restored.ReferenceBands[0].Gain);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [TestMethod]
    public void InvalidStorageAndUnknownFiltersArePreservedAndBlockUnsafeOperations()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace-v1.json"); File.WriteAllText(path, "{malformed");
            using var model = MainViewModel.CreateOffline(directory); model.Bands[0].Gain = 2; Assert.IsFalse(model.Persist());
            Assert.AreEqual("{malformed", File.ReadAllText(path));
            model.Bands[1].Load(new(1, 300, 0.05, 3, PeqFilterType.Unknown, true, 99));
            Assert.AreEqual(0.05, model.Bands[1].Q); Assert.AreEqual((byte)99, model.Bands[1].ToCoreBand().RawFilterCode);
            StringAssert.Contains(model.ValidationMessage, "unknown raw filter");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void DirtyRecoveryRetainsOfflineDiscardBaselineAndReferenceSnapshotAfterRename()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var model = MainViewModel.CreateOffline(directory))
            {
                model.Bands[0].Enabled = true; model.Bands[0].Gain = 4;
                var preset = model.SaveLocal("Original"); model.ReferencePreset = preset;
                model.LocalPresets[0] = preset with { Name = "Renamed", Eq = preset.Eq with { PreGain = -8 } };
                model.Bands[0].Gain = 7; model.Persist();
            }
            using var restored = MainViewModel.CreateOffline(directory);
            Assert.AreEqual(7, restored.Bands[0].Gain); Assert.AreEqual("Original", restored.ReferenceName);
            Assert.AreEqual(0, restored.ReferencePreset!.Eq.PreGain); restored.Discard(); Assert.AreEqual(4, restored.Bands[0].Gain);
            Assert.AreEqual("Original", restored.ReferenceOptions.Single(p => p.Id == restored.ReferenceId).Name);
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal sealed class WorkspaceDevice : IDawnPro2Device
{
    public DeviceKind Kind => DeviceKind.DawnPro2;
    public string DisplayName => "Software test DAWN PRO2";
    public bool IsUsable { get; set; } = true;
    public EqConfiguration State { get; set; } = EqConfiguration.Flat();
    public int Writes, FlashWrites;
    public bool Mismatch, PartialFailure, FailEqSave;
    public Task<string> ReadFirmwareVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult("software");
    public Task<int> ReadActiveEqAsync(CancellationToken cancellationToken = default) => Task.FromResult(9);
    public Task<double> ReadPreGainAsync(CancellationToken cancellationToken = default) => Task.FromResult(State.PreGain);
    public Task<double> ReadGlobalGainAsync(CancellationToken cancellationToken = default) => Task.FromResult(State.GlobalGain);
    public Task<PeqBand> ReadBandAsync(int index, CancellationToken cancellationToken = default) => Task.FromResult(State.Bands[index]);
    public Task<IReadOnlyList<PeqBand>> ReadAllBandsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PeqBand>>(State.Bands.ToArray());
    public Task WriteActiveEqAsync(int index, bool save = false, CancellationToken cancellationToken = default) => throw new AssertFailedException("Hidden active-EQ write");
    public Task WritePreGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) { Assert.IsFalse(save); Writes++; State = State with { PreGain = EqConfiguration.Encoded(value) }; return Task.CompletedTask; }
    public Task WriteGlobalGainAsync(double value, bool save = false, CancellationToken cancellationToken = default) { Assert.IsFalse(save); Writes++; State = State with { GlobalGain = EqConfiguration.Encoded(value) }; return Task.CompletedTask; }
    public Task WriteBandAsync(PeqBand band, CancellationToken cancellationToken = default) { Writes++; State.Bands[band.Index] = band with { Q = EqConfiguration.Encoded(band.Q), Gain = EqConfiguration.Encoded(band.Gain), FilterType = band.Enabled ? band.FilterType : PeqFilterType.Disabled }; return Task.CompletedTask; }
    public Task EnableBandCoefficientsAsync(int index, CancellationToken cancellationToken = default) => throw new AssertFailedException("Hidden coefficient write");
    public async Task WriteAllBandsAsync(IReadOnlyList<PeqBand> bands, bool save = false, CancellationToken cancellationToken = default)
    {
        Assert.IsFalse(save); foreach (var band in bands) { if (Mismatch && band.Index == 0) continue; await WriteBandAsync(band); if (PartialFailure) throw new IOException("Injected partial write"); }
    }
    public Task SaveEqToFlashAsync(CancellationToken cancellationToken = default) { FlashWrites++; if (FailEqSave) throw new IOException("Injected EQ flash failure"); return Task.CompletedTask; }
    public Task SaveGainsToFlashAsync(CancellationToken cancellationToken = default) { FlashWrites++; return Task.CompletedTask; }
}

internal sealed class WorkspaceLegacy : ILegacyDawnProDevice
{
    public DeviceKind Kind => DeviceKind.Legacy;
    public string DisplayName => "Software test legacy";
    public int Writes;
    private int _volume = 50;
    private string _gain = "Low", _led = "On", _filter = "Fast Roll-Off Low Latency";
    public Task<int?> GetVolumeAsync(CancellationToken cancellationToken = default) => Task.FromResult<int?>(_volume);
    public Task<string?> GetGainAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(_gain);
    public Task<string?> GetLedStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(_led);
    public Task<string?> GetFilterAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(_filter);
    public Task<bool> SetVolumeAsync(int value, CancellationToken cancellationToken = default) { _volume = value; Writes++; return Task.FromResult(true); }
    public Task<bool> SetGainAsync(string value, CancellationToken cancellationToken = default) { _gain = value; Writes++; return Task.FromResult(true); }
    public Task<bool> SetLedStatusAsync(string value, CancellationToken cancellationToken = default) { _led = value; Writes++; return Task.FromResult(true); }
    public Task<bool> SetFilterAsync(string value, CancellationToken cancellationToken = default) { _filter = value; Writes++; return Task.FromResult(true); }
}
