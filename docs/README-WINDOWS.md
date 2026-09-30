# Moondrop for Windows

Native Windows control app for Moondrop DAWN PRO2 and the original Dawn Pro. The current app uses WPF and .NET 10, with three pages: Equalizer, Presets, and Settings.

Developed and maintained by **[mohammed-just](https://github.com/mohammed-just)**. Based on the original [shaypower/DawnPro-GUI](https://github.com/shaypower/DawnPro-GUI) project.

![Equalizer workspace with the combined EQ curve](images/eq.png)

## Equalizer

- Interactive frequency-response graph, eight-band table, and selected-band editor.
- The default graph shows one combined response with draggable numbered markers. Individual band response lines are an optional setting and are off by default.
- Device identity, firmware, refresh, pre-gain, and global gain in the editing workspace.
- Preset picker, edited indicator, undo/redo, reset, and local preset saving.
- A flat 0 dB target is selected by default. Imported or saved EQ curves can also be selected as a separate target overlay; choosing None hides the target.
- Discard edits, Apply changes, and Save to device are distinct actions.

Editing and opening presets change a local workspace. **Apply changes** writes to the connected device and checks active values through readback. **Save to device** sends the available persistence commands separately. A completed save command does not establish that settings survive power loss; the UI preserves this distinction.

The target overlay compares EQ filter shapes. Acoustic targets such as Harman or diffuse field need compatible headphone measurements and are not supplied as fabricated EQ curves. Hardware bypass and inactive device-slot operations remain unavailable until their behavior is established.

## Presets and settings

Presets are horizontal rows with names, filter counts, Open, Apply, and a secondary menu for rename, duplicate, export, and local deletion. Import supports the validated Equalizer APO text subset and native JSON. Opening a preset does not write to hardware. Apply writes the chosen preset to the active device EQ, retains global gain, and checks readback without saving to device memory. Unstored draft edits are protected before switching presets.

EQ 9 means the active EQ identifier reported by the DAC; it is separate from the eight filter bands and local presets. Click the EQ badge for help. Click the information button beside Save to device for the difference between applying, saving on the DAC, and saving a preset on this PC.

Settings use Appearance, Behavior, EQ, and Advanced groups. Theme, Windows accent, reconnect behavior, remembered editing workspace, preamp protection, curve visibility, and logging are persisted locally. Diagnostics and log export are available here.

## Build on Windows

Install the .NET SDK version pinned in `global.json` (10.0.302), then run from the repository root:

```powershell
dotnet restore DawnPro.Wpf.slnx --locked-mode
dotnet build DawnPro.Wpf.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test DawnPro.Wpf.slnx -c Release --no-build --settings tests-dotnet/default.runsettings
```

The default test settings exclude physical hardware categories. The ordinary suite covers protocol behavior using fake transports, workspace persistence, hardware failure handling, and actual WPF layout/control behavior without device writes. Physical testing has a separate approval procedure described in [BUILD-DOTNET.md](BUILD-DOTNET.md).

Run without hardware:

```powershell
.\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe --demo
```

Build a portable x64 app with its runtime included:

```powershell
dotnet publish src/Moondrop.Wpf/Moondrop.Wpf.csproj -c Release -r win-x64 --self-contained true -o artifacts/windows-preview -p:ContinuousIntegrationBuild=true
```

Run `artifacts/windows-preview/Moondrop.Wpf.exe`. Keep the entire published directory together. This build command creates local files; it does not create a GitHub release.

## UI verification

The app supports hardware-free captures, for example:

```powershell
.\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe --demo --theme=dark --page=Eq --screenshot=artifacts/equalizer.png
```

Supported pages are `Eq`, `Presets`, and `Settings`. `--width=640 --height=620` exercises the narrow layout. `--workspace=<directory>` selects an isolated editing workspace; with `--demo`, it still disables hardware. Raster capture scaling is not a substitute for testing actual Windows display scaling.

See [the redesign completion notes](ui-redesign.md) for the changes and remaining hardware-dependent requirements.
