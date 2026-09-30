using System.IO;
using System.Text.Json;
using System.Windows;
using Moondrop.Core.Eq;

namespace Moondrop.Wpf;

internal static class WorkspaceSmoke
{
    // A deterministic diagnostic path for testing the actual published WPF process without hardware.
    public static void Run(MainWindow window, MainViewModel model, string directory, string stage)
    {
        if (model.IsHardwareConnected) throw new InvalidOperationException("Workspace smoke must be offline.");
        Directory.CreateDirectory(directory);
        if (stage == "write")
        {
            var input = Path.Combine(directory, "smoke-input.txt");
            File.WriteAllText(input, "Preamp: -6 dB\nFilter 3: ON PK Fc 1234.5 Hz Gain 6 dB Q 0.05");
            var imported = WorkspaceStorage.Import(input, "Smoke import");
            model.LocalPresets.Add(imported); model.OpenLocalPreset(imported); model.GlobalGain = -1;
            var saved = model.SaveLocal("Smoke created"); model.ReferencePreset = saved;
            var native = Path.Combine(directory, "smoke-export.json"); var apo = Path.Combine(directory, "smoke-export.txt");
            WorkspaceStorage.AtomicWrite(native, WorkspaceStorage.ExportNative(saved));
            WorkspaceStorage.AtomicWrite(apo, WorkspaceStorage.ExportApo(saved));
            if (!saved.Eq.Same(WorkspaceStorage.Import(native, "native").Eq) || !saved.Eq.Same(WorkspaceStorage.Import(apo, "APO").Eq)) throw new InvalidOperationException("Export round trip mismatch.");
            model.ThemeSelection = "Dark"; model.IndividualCurves = false; model.Bands[2].Gain = 5;
            if (!model.Persist()) throw new IOException("Smoke workspace did not persist.");
        }
        else if (stage == "read")
        {
            if (model.LocalPresets.Count != 2 || model.Bands[2].Gain != 5 || model.Bands[2].Q != 0.05 || model.GlobalGain != -1 || model.ThemeSelection != "Dark" || model.IndividualCurves || model.ReferenceBands[2].Gain != 6)
                throw new InvalidOperationException("Restart persistence mismatch.");
            model.Discard(); if (model.Bands[2].Gain != 6) throw new InvalidOperationException("Offline discard did not restore the last loaded/saved local baseline.");
            model.Bands[2].Gain = 4; if (model.CanApply || model.CanSave) throw new InvalidOperationException("Hardware controls available while disconnected.");
        }
        else throw new ArgumentException("Smoke stage must be write or read.");
        foreach (var page in new[] { ShellPage.Eq, ShellPage.Presets, ShellPage.Settings }) { model.SelectedPage = page; window.UpdateLayout(); }
        WorkspaceStorage.AtomicWrite(Path.Combine(directory, $"smoke-{stage}-result.json"), JsonSerializer.Serialize(new { stage, passed = true, hardwareAccess = false, presets = model.LocalPresets.Count, draftBands = model.Bands.Count, theme = model.ThemeSelection }));
    }
}
