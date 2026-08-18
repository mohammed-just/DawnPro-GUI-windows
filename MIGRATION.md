# Dawn Pro WPF Migration

Date: 2026-07-30

## Scope

The .NET solution now contains real protocol, hardware, service, and UI paths for the DAWN PRO2 HID backend and the original Dawn Pro USB backend under:

- `DawnPro.Wpf.slnx`
- `src/Moondrop.Core`
- `src/Moondrop.Hardware`
- `src/Moondrop.Wpf`
- `tests-dotnet/Moondrop.Tests`

Existing Python files were not intentionally edited, normalized, reset, or overwritten.

## Implemented Port

- DAWN PRO2 HID: VID `35D8`, PID `011D`, report ID `75`, exact 64-byte response envelopes, one retained open-by-path HidSharp stream across write/response and the transport lifetime, per-operation native read/write timeouts, firmware read, active EQ read/write `0..15`, pre/global gain read/write/apply, all 8 decoded PEQ reads, a validated complete raw-band capture path retaining all 20 coefficient bytes and metadata, canonical raw writes that copy only proven state bytes 7-33 while setting the validated band selector and slot `7`, write band followed by coefficient-enable command-path transmission, EQ flash save, gain flash save, Python-matching command delays behind injectable `IDeviceDelay`, and an explicit serial-plus-path `OpenByIdentity` API for safety workflows. Complete raw response frames remain diagnostic artifacts; opaque response selectors are never transmitted.
- Legacy USB: VID `2FC6`, PID `F06A`, uppercase-config additional IDs, LibUsbDotNet control transfers using the Python `bmRequestType`, `bRequest`, `wValue`, `wIndex`, payloads, and read lengths. Getters return `null` on I/O failures and setters return `false`.
- Application service: backend priority selects DAWN PRO2 first, then legacy; combined errors are surfaced on total failure; serialized async queue keeps device I/O off the UI thread.
- WPF: normal mode withholds the main UI until backend selection succeeds; total failure shows the combined error and exits. `--demo` and `--benchmark` remain hardware-free. The UI has DAWN PRO2 firmware/EQ/gain/import/flash controls and a separate legacy volume/gain/LED/filter view.
- Desktop packaging: the WPF project uses the Windows GUI subsystem (`WinExe`), so normal launches do not create a console window. Benchmark commands pipe stdout so PowerShell waits for the GUI-subsystem process and captures its single JSON result.
- EQ import: Equalizer APO/AutoEQ parser is used with confirmation, optional preamp application, and no implicit flash save.
- Config: all existing Python config sections and uppercase fields round-trip as JSON. Legacy saved defaults are applied at startup as Python does. DAWN PRO2 defaults are loaded into the initial UI but are not implicitly written to hardware; the immediate device refresh remains authoritative, matching Python. UI edits persist to `%APPDATA%\dawnpro\config.json`; malformed additional-device entries are skipped individually like Python.
- Graph: redraws on band and pre-gain changes, uses 20 Hz to 20 kHz log axis, 96 kHz digital response, excludes disabled bands from combined response, shows 8 handles, supports drag frequency/gain and hovered-handle mouse-wheel Q, supports keyboard frequency/gain/Q and band selection, exposes an accessibility automation peer, caches response geometry, responds to runtime accent changes, and prepares each enabled biquad once per geometry rebuild rather than once per frequency sample.
- Theme/Mica: no third-party theme or WinUI 3; WPF `Application.ThemeMode` is used, and DWM attributes remain `DWMWA_WINDOW_CORNER_PREFERENCE = 33`, `DWMWA_SYSTEMBACKDROP_TYPE = 38`, `DWMSBT_MAINWINDOW = 2`. Unsupported or failed Mica requests switch the normal window to the official opaque `ApplicationBackgroundBrush` fallback.
- Safety/parity: non-finite or mathematically invalid PEQ inputs fail before packet/write construction; bulk apply/import prevalidates every band before changing any band or optional preamp state; unknown legacy bytes retain Python's invalid-state labels; each retained-stream transaction owns its write, owed read, and cancellation surfacing under one async gate; undrained/timed-out/malformed responses poison and dispose the device; and successful Pro2 Apply operations read device state back like Python.

## TDD Evidence

### Hardware Slice RED

Command:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests-dotnet/Moondrop.Tests/Moondrop.Tests.csproj --no-restore --filter HardwareBehaviorTests
```

Output:

```text
Project '..\..\src\Moondrop.Hardware\Moondrop.Hardware.csproj' targets 'net10.0-windows'. It cannot be referenced by a project that targets '.NETCoreApp,Version=v10.0'.
```

After retargeting tests to `net10.0-windows`, the same slice failed on missing hardware/service/config members until implemented.

### Hardware Slice GREEN

Command:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests-dotnet/Moondrop.Tests/Moondrop.Tests.csproj --filter HardwareBehaviorTests
```

Output:

```text
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 85 ms - Moondrop.Tests.dll (net10.0)
```

## Final Verification

Command:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore DawnPro.Wpf.slnx
& "$env:USERPROFILE\.dotnet\dotnet.exe" build DawnPro.Wpf.slnx -c Release --no-restore -v:minimal
& "$env:USERPROFILE\.dotnet\dotnet.exe" test DawnPro.Wpf.slnx -c Release --no-build -v:minimal
```

Output:

```text
All projects are up-to-date for restore.
Build succeeded.
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 239 ms - Moondrop.Tests.dll (net10.0)
```

### Audit-fix RED/GREEN evidence

The independent review identified ties-to-even fixed-point parity, unknown filter preservation, duplicate band indexes, malformed additional IDs, and partial legacy refresh failures. Targeted tests were added first. The initial run failed to compile because `PeqFilterType.Unknown`, `PeqBand.RawFilterCode`, and `PrepareMagnitudeResponse` did not yet exist; after implementation, the six parity tests passed:

```text
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 208 ms - Moondrop.Tests.dll (net10.0)
```

The final clean rebuild checkpoint at that stage superseded the earlier 10-test checkpoint:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52, Duration: 336 ms - Moondrop.Tests.dll (net10.0)
```

The final independent audit follow-up added seven regression tests for invalid PEQ math, unknown legacy values, post-write cancellation draining, WPF default initialization/non-finite editor rejection, culture-invariant preset decimals, full bulk-band prevalidation, and import prevalidation before optional preamp writes. A final Release build completed with **0 warnings and 0 errors**, followed by **59/59 passing .NET tests** and **124/124 passing Python tests**. Repository-wide tracked Python/packaging diffs and line-ending changes predated this side-by-side migration and were intentionally left untouched per the preservation constraint; “clean rebuild” refers to deleting generated .NET outputs before rebuilding, not a clean Git worktree.

Command:

```powershell
.\.venv-win\Scripts\python.exe -m pytest -q
```

Output:

```text
........................................................................ [ 58%]
....................................................                     [100%]
124 passed in 0.86s
```

## Release Benchmark

Command:

```powershell
1..5 | ForEach-Object {
  & "C:\Users\mohammed\Documents\moondrop gui\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe" --benchmark | Write-Output
}
```

Output:

```json
{"startupToFirstRenderMs":986,"privateBytesAfter3sIdle":131452928,"workingSetAfter3sIdle":165838848,"graphEditor1000UpdatesMs":3013}
{"startupToFirstRenderMs":760,"privateBytesAfter3sIdle":131592192,"workingSetAfter3sIdle":165711872,"graphEditor1000UpdatesMs":2344}
{"startupToFirstRenderMs":900,"privateBytesAfter3sIdle":131600384,"workingSetAfter3sIdle":165662720,"graphEditor1000UpdatesMs":2364}
{"startupToFirstRenderMs":602,"privateBytesAfter3sIdle":131575808,"workingSetAfter3sIdle":165773312,"graphEditor1000UpdatesMs":2693}
{"startupToFirstRenderMs":1180,"privateBytesAfter3sIdle":132268032,"workingSetAfter3sIdle":168304640,"graphEditor1000UpdatesMs":5316}
```

Five-run medians:

- Startup to first rendered frame: `900 ms` (`+595.646 ms` vs Python baseline `304.354 ms`)
- Private memory after 3 s idle: `125.496 MiB` (`+106.582 MiB` vs Python baseline `18.914 MiB`)
- Working set after 3 s idle: `158.094 MiB` (`+124.727 MiB` vs Python baseline `33.367 MiB`)
- 1000 graph/editor updates with layout/render drained every 10 changes: `2693 ms` (`+2378.413 ms` vs the Python synthetic update baseline `314.587 ms`)

WPF still uses substantially more startup time and memory than the Python baseline in Release. The original `8 ms` graph number was invalid because it timed only property setters and left render work queued; the corrected benchmark includes coalesced layout/render work and is intentionally reported without claiming an improvement. Runs 4 and 5 show substantial variance, so these values should be treated as local measurements rather than guarantees.

## Demo Startup

Command:

```powershell
$p = Start-Process -FilePath "C:\Users\mohammed\Documents\moondrop gui\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe" -ArgumentList "--demo" -PassThru
Start-Sleep -Seconds 3
Get-Process -Id $p.Id | Select-Object Id,MainWindowHandle,Responding
Stop-Process -Id $p.Id -Force
```

Output:

```text
{"Id":33876,"Title":"Moondrop Dawn Pro","Handle":41884018,"Responding":true,"HasExited":false}
```

Dark and light renders were also captured through `--demo --theme=<dark|light> --screenshot=<path>`. Both were inspected: text and control contrast are readable, the graph labels/curve/eight handles are visible without overlap, the right band editor scrolls normally, and the explicit Light selection produces a genuinely light Fluent surface. Screenshot mode substitutes the official `ApplicationBackgroundBrush` because off-screen `RenderTargetBitmap` cannot capture the DWM Mica surface; normal windows remain transparent for Mica.

## Hardware Verification Limits

A physical DAWN PRO2 was subsequently provided and detected by Windows as VID `0x35D8`, PID `0x011D` across its USB, HID, and media interfaces. A read-only production launch selected the DAWN PRO2 backend, displayed `Connected`, read firmware `1.5`, refreshed the active EQ and all eight bands, remained responsive, and closed gracefully through `CloseMainWindow` with no lingering process. This verifies live enumeration, HID opening, startup reads, retained-stream request/response behavior for the refreshed state, and UI integration on this specific device/firmware. Write, apply, coefficient-enable, gain/EQ flash-save, persistence-after-reconnect, and audible/on-device PEQ effects were deliberately not exercised without separate permission; those paths remain covered only by fake-transport protocol and ordering tests. No physical original Dawn Pro USB device was tested.

### Thirteenth physical harness remediation status (2026-08-08)

Accepted-current-user re-review found one remaining medium safety defect: `DawnPro2Device.SendAsync` did not poison or dispose its retained HID stream when a no-response state-changing native write, write timeout/cancellation, caller cancellation after a successful write, or post-write transaction-progress callback failed. A fire-and-forget write or flash command can already have reached the device before any of those failures becomes visible, so reusing that stream for immediate restoration was unsafe. The thirteenth remediation atomically poisons and disposes from no-response native write entry through completed post-write progress and the post-write cancellation check, then propagates the original failure. Cancellation before native entry remains non-poisoning, successful fire-and-forget writes retain their progress and delay behavior, and request-response sequencing remains unchanged. Fake-transport regressions prove write error, timeout, native cancellation, cancellation after a successful write, post-write progress error/cancellation, and subsequent-command rejection, while separate regressions preserve pre-entry cancellation and successful flash-save delays. The source/runtime approval was invalidated and remains reset. The harness remains **NO-GO pending a thirteenth independent audit and complete two-hash approval metadata**. No hardware was accessed, no physical environment variable was assigned, and none of PREPARE, EXECUTE, or RECOVERY was run during or after this remediation.

### Fourteenth physical harness remediation status (2026-08-08)

The user's actual read-only PREPARE was subsequently run twice against the pinned DAWN PRO2 PID `0x011D`, firmware exactly `1.5`; both complete raw preflights were internally consistent and both returned raw Active EQ `9`. This observed firmware field is not proof of a physical default/custom toggle and is not the PEQ registry profile `7` stored in raw band write packets. The validator now accepts `9` only for the exact DAWN PRO2 model, VID/PID, and firmware `1.5`; wrong model, PID, firmware, or any other raw value remains rejected. Identity metadata, snapshots, transition plans, sessions, reachability, and exact comparisons retain raw `9`, while `DawnPro2Protocol.PeqIndex` and raw write byte 35 remain `7`.

The physical workflow was narrowed to one minimum quarter-dB mutation of one supported PEQ band followed immediately by complete RAM restoration. PREPARE is read-only. EXECUTE and RECOVERY never call the Active-EQ write API, never toggle physical EQ mode, never flash-save, and never request a physical cycle. Restoration rewrites all eight captured raw bands plus captured pre/global gains, then reads two fresh complete snapshots and demands byte-equivalence across identity, firmware, raw Active EQ `9`, both raw gains, and every retained raw band byte. A mismatch, disconnect, unexpected reachable-state failure, or restoration error prevents success. The approval manifest was reset to both placeholders and all zero counts. This coding remediation performed no hardware access, physical opt-in, PREPARE, EXECUTE, RECOVERY, result publication, commit, or push; the harness remains **NO-GO pending a fourteenth independent audit and complete two-hash approval metadata**.

### Fifteenth physical harness remediation status (2026-08-08)

Independent re-review found that `PhysicalSnapshotValidator.RestorationProblems` used `Trim()` for both its general firmware check and the firmware condition of the raw Active EQ `9` exception. Because firmware parsing preserves non-NUL UTF-8 content, that broadened the exception beyond the required raw string exactly `1.5`. A focused RED proved that `" 1.5"`, `"1.5 "`, `"\t1.5"`, and `"1.5\r"` were incorrectly accepted while exact `"1.5"` remained accepted for the intended DAWN PRO2 model, VID/PID, and raw value `9`. The minimal GREEN removed both trims and retained ordinal comparison and every other identity/value condition unchanged. The approval manifest remains reset to both placeholders and all zero counts. This remediation performed no hardware access, physical opt-in, PREPARE, EXECUTE, RECOVERY, approval population, device write, result publication, commit, or push; the harness remains **NO-GO pending a fifteenth independent audit and complete two-hash approval metadata**.

`tests-dotnet/physical-runtime-approval.json` intentionally remains the strict schema-v1 placeholder: both hash fields are `INDEPENDENT_AUDIT_REQUIRED` and every count is zero. The approval binds runtime identifier, source hash plus total/sentinel/content counts, and complete runtime hash plus total/runner-tree/watchdog-tree/metadata counts. The approval file is excluded from the source hash. Missing, partial, malformed, mismatched, or source-only approval fails closed. No reviewed fingerprint is embedded in documentation.

The exact lifecycle requires an independent auditor to produce a clean staged locked self-contained `win-x64` candidate, run both direct apphost smokes with shared-runtime paths made hostile, independently reproduce both calculations and all counts, repeat in a second clean stage, and require exact equality. Only then may both placeholders and all zero counts be replaced together. `--build-runtime-smoke` is explicitly candidate-only and never grants physical authority; `--verify-runtime-approval` compares filled metadata with a retained staged source and both exact output trees. Any later source, control, lock, SDK/runtime output, dependency, metadata, or count change restarts the whole two-hash lifecycle. The JSON is a trusted audit policy input, not a digital signature or an owner-proof OS boundary.

The actual execute entry point calls the same `PhysicalExecuteOrchestrator` exercised by failure injection. Its observable action sequence is exactly the single individual-band mutation followed by full original-RAM restoration. Failures at either action and failures while publishing every current post-write durable phase prove immediate restoration attempts, no success without `RestorationVerified`, and separate primary/restoration errors. Former gain-test, bulk-test, Active-EQ, flash, and physical-cycle actions were removed from the physical action surface.

Execute and recovery remain supported only through a published self-contained `win-x64` watchdog apphost under `tests-dotnet/artifacts/physical-runtime`, which directly parents the isolated self-contained runner apphost. Framework-dependent `dotnet <dll>`, raw `dotnet test`, forged values, and file-replacement races fail closed. The watchdog now clears the runner `ProcessStartInfo.Environment`, copies only validated canonical current `SystemRoot`, `WINDIR`, `TEMP`, and `TMP`, and adds only the exact phase/session and authenticated direct-parent values. `PATH` and every ambient runtime, profiler, host, build, NuGet, test-runner, `MOONDROP_*`, or arbitrary variable are absent. Exact key membership, NUL/CR/LF rejection, canonical paths, apphost/parent identity, heartbeat ownership, session ID, one-run token, source/runtime hashes, lineage, and runner arguments are checked before launch. After the child loads the session, it compares the same binding with the authenticated parent heartbeat before any HID open. Child zero is accepted only for freshly loaded matching `Completed`; recovery after execute failure remains nonzero and is called verified only for matching Completed lineage.

All six projects use committed `packages.lock.json`, `RestorePackagesWithLockFile`, and `RestoreLockedMode`; affected locks include the audited `win-x64` runtime graph. The protected transaction builds only the runner and watchdog that are published and executed. It uses the explicit staged NuGet/configuration controls, disabled auto-response and build servers, and a cleared minimal child environment before locked restores and self-contained publishes. SDK artifacts, NuGet assets/cache, outputs, profiles, and temporary paths are generation-local outside the source tree; audited `PathMap` settings normalize the distinct source/artifacts roots for deterministic DLL/PDB bytes.

Per-entry Windows ACLs still deny ordinary write, delete, rename, and create rights to the invoking token identities; read-only attributes and native read-only/read-shared handles add checked defense-in-depth and fail closed where configured. Build/smoke children are killed and awaited before the lease unwinds. The current owner can reset the DACL, so these mechanisms are not the security invariant. Instead, the approval is snapshotted, the staged source/counts must match it before build, both exact apphosts are smoked, and the complete runtime hash/counts must match it after capture. Regression coverage simulates transient staged source tamper followed by restoration and separately changes a runtime output file; both are rejected before an executable physical session can be returned.

Schema v3 hashes every file recursively in both self-contained publish trees, including apphosts, native runtime/host files, managed dependencies, PDBs, deps/runtimeconfig, and any other published file, plus path-independent package locks, `global.json`, NuGet configuration, and explicit build controls. Raw `obj/project.assets.json` is excluded because it embeds generation paths. Runtimeconfig must prove self-contained `Microsoft.NETCore.App` and contain no shared-framework request. PREPARE requires the captured runtime to equal the independent approval before enumeration/open, and recaptures before session publication. Every EXECUTE/RECOVERY fresh build must match the session source/runtime hashes and both current approval hashes; the loaded child repeats actual source/runtime and approval equality before HID.

Every completed bounded execute/recovery device transaction emits authenticated watchdog progress, including all 24 transactions in each consistent double snapshot and the fresh restoration verification. Direct read-only PREPARE performs the same bounded transactions without requiring or emitting watchdog progress. The watchdog applies its ordinary 15-second inactivity bound; this narrow flow has no physical-cycle wait. It verifies the exact owned child PID/start time/command line/token before terminating that tree. Execute failure/timeout remains nonzero even when recovery succeeds and is reported as `EXECUTE FAILED; RECOVERY VERIFIED`. Execute-triggered and standalone recovery are supervised and relaunched at most three total attempts while durable state remains recoverable. HidSharp open/dispose remain truthfully non-cancellable in process.

Recovery reads two consistent current snapshots and requires firmware `1.5`, the pinned DAWN PRO2 model/VID/PID/serial/path, raw Active EQ `9`, and either the original or single-band temporary state reachable for the durable phase. Except for terminal cases it idempotently rewrites and freshly verifies exact original RAM without writing Active EQ or flash. `Failed` carries `LastSafePhase` for compatibility constraints. `RestorationVerified` accepts only exact original state and advances to `Completed`; `Completed` is a no-HID/no-write no-op.

Ordinary unfiltered tests automatically use `tests-dotnet/default.runsettings`, excluding all three physical categories before execution. Explicit physical selection requires `tests-dotnet/physical.runsettings`; execute/recovery additionally require the watchdog. Standard suite totals therefore report selected software tests honestly rather than three physical skips.

Raw response correlation remains intentionally unproven beyond exact 64-byte length and report ID. No echoed opcode, command, band, or preset field is established. Band response bytes 4 and 35 remain opaque in the complete diagnostic capture; canonical write packets set byte 4 from validated local band identity and byte 35 to slot `7`, copy only bytes 7-33, and zero unrelated selector/reserved bytes. The response-expected transaction poison boundary begins before native write and includes read, normalization, and command parser. Ambiguous write errors, envelope failures, and semantically invalid firmware/active-EQ/gain/band payloads dispose the retained channel before reuse. Its successful post-write cancellation still drains without poisoning. The no-response state-changing boundary now begins on native `WriteAsync` entry and extends through post-write transaction progress; write errors, timeout/cancellation, and progress error/cancellation atomically poison and dispose before propagation. Cancellation before either native entry remains non-poisoning. A valid-shaped unrelated unsolicited frame remains a residual until read-only capture analysis establishes correlation fields.

Session schema v3 publication retains write-through, `Flush(true)`, atomic rename, and an independently parsed `.recovery.json` copy. One malformed copy cannot suppress the other; valid copies must share session/token/source/runtime/original/plan lineage before timestamp arbitration. Windows directory/rename metadata still has no claimed directory-fsync guarantee, so simultaneous loss of both names remains a precise filesystem/power-loss residual. Other residuals remain: the current owner can reset staged ACLs and the approval JSON is not signed, WMI ownership verification may fail closed without automatic termination, arbitrary HID clients can race the harness, exact path pinning requires the same USB port, and the harness exposes no flash, clear-flash, erase-config, or firmware-upgrade operation. Exact default and future independently approved lifecycle details are in `BUILD-DOTNET.md`.

## Responsive Fluent Redesign Verification

The presentation layer was redesigned without changing device, protocol, configuration, or explicit apply/save behavior. It now provides EQ, Device, Presets, Settings, and About navigation; wide/medium/narrow responsive layouts; a compact device/action panel; reusable Fluent dynamic-resource styles; selected-band cards; individual and combined cached graph curves; graph hover readout; an in-window import confirmation overlay; and a non-blocking error banner. Icon-only navigation includes tooltips and automation names. Unsupported bypass controls, invented device data, and unofficial product imagery remain intentionally absent.

Eight original state/math UI slices, nine real STA/WPF runtime regressions, and one production-source policy regression now cover navigation state, responsive breakpoints, deterministic screenshot dimensions, confirmation state, error banners, graph projection/readout, individual-plus-combined responses, selected-band synchronization, runtime automation names, modal focus behavior, deferred shutdown, legacy capability, command reentrancy, graph cache invalidation, high-contrast resource invalidation, curve visibility, navigation-pill clipping, and the no-`Task.Run` requirement. The final suite after audit remediation reports:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253
124 passed
```

Real Windows UI Automation selected each navigation destination (`EQ`, `Device`, `Presets`, `Settings`, and `About`) and read back `IsSelected=True`. It also found `Active EQ` as a `ComboBox` plus non-empty names for `Select band 1`, `Band 1 enabled`, `Band 1 filter`, `Band 1 frequency in hertz`, `Band 1 gain in decibels`, and `Band 1 Q`; `WindowPattern.Close` then exited gracefully with no lingering process. Dark and light captures were generated at `1440x900`, `1100x760`, and `760x900`, plus a `640x700` minimum-size dark capture. A minimum-width review found overlapping graph help text; narrow-only caption hiding fixed it. The post-audit review also found a clipped wide navigation status pill; a constrained grid and rendered-layout regression fixed it. The seven final captures and contact sheet are in `tests-dotnet/artifacts/screenshots/`.

## Post-audit remediation evidence

The accessibility and reliability audits were remediated without changing device, protocol, configuration, import, apply, or flash semantics:

- Active EQ and every band selector/enable/filter/frequency/gain/Q editor now expose meaningful runtime automation names. Band selectors use a visible accent focus visual.
- The in-window confirmation is exposed as a UIA dialog-like window with bound name/help, `IsDialog`, assertive live setting, focus entry, cyclic tab navigation, disabled underlying shell, Escape/Cancel, default Enter action, and prior-focus restoration. It remains an in-window confirmation; no system message box was introduced.
- Window close is coordinated through `Closing`: the first close is deferred, async disposal and queued device work complete deterministically, failures are traced safely, repeated close attempts do not double-dispose, and one final synchronous close is allowed.
- Legacy devices cannot execute or see Pro2 Import EQ, Apply all, or Save EQ workflows. The Presets page instead directs users to truthful legacy controls on the Device page.
- Apply band now awaits its async callback, so command busy state covers the complete device write/refresh operation and repeated execution cannot enqueue another write.
- Replacing `EqGraph.Bands` clears/increments response caches. Runtime high-contrast/accent changes invalidate every cached graph pen/brush; grid, zero-line, and disabled-marker resources come from Fluent/system theme brushes.
- Individual curves use `0.88` accent opacity (approximately 3:1 against the audited `#202020` dark and `#F3F3F3` light surfaces), while the selected curve is thicker and dashed and the combined response remains the thickest solid curve.
- Synchronous HidSharp I/O and device discovery remain off the UI thread through a thread-pool/TCS boundary that preserves cancellation-before-start and exception propagation; the four prior `Task.Run` calls were removed.

Each RED and GREEN used the same exact focused command before and after the minimal production change:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.EqEditorsExposeMeaningfulRuntimeAutomationNamesAndVisibleBandFocus" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.ConfirmationOverlayBehavesAsAnAccessibleModalDialog" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.WindowCloseWaitsForIncompleteAsyncDisposalAndRunsOnlyOnce" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.LegacyPresetPageHidesAndDisablesPro2Workflows" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.ApplyBandCommandStaysBusyAndRejectsRepeatedExecutionUntilWriteCompletes" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.ReplacingGraphBandsRebuildsRenderedResponseGeometry" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.HighContrastChangeInvalidatesEveryCachedGraphDrawingResource" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.IndividualGraphCurvesUseContrastingPensAndNonColorSelectionCue" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.ConnectionStatusContentStaysInsideItsNavigationPill" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~HardwareBehaviorTests.DotNetProductionSourcesDoNotUseTaskRun" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.DeviceAndSettingsControlsExposeUnambiguousRuntimeAutomationNames" --no-restore
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --filter "FullyQualifiedName~WpfRuntimeTests.RuntimeThemeChangeInvalidatesEveryCachedGraphThemeResource" --no-restore
```

Observed RED failures, in order, were: empty Active EQ runtime name; enabled underlying shell while confirmation was open; window already invisible before incomplete disposal finished; enabled legacy Import EQ command; Apply band already executable while the first write was incomplete; identical cached geometry after replacing Bands; high-contrast notification left drawing caches populated; individual curve opacity `0.20` was below the required `0.86` floor; navigation status text began at `-16` DIPs outside its pill; and the source-policy regression found `Moondrop.Hardware\Transports.cs` still used `Task.Run`. A final independent review then caught ambiguous/empty Apply-band, gain, legacy-device, and theme automation names plus graph resources retained across an in-app theme switch. Their RED regressions observed `Apply band` instead of `Apply band 1`, empty device control names, and non-null graph caches after switching theme. Every command then passed after its minimal GREEN change. The combined focused runtime command passed `11/11`:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --no-build --filter "FullyQualifiedName~WpfRuntimeTests"
```

Final validation commands and results:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' build DawnPro.Wpf.slnx -c Release --no-restore
# Build succeeded. 0 warnings, 0 errors.

& 'C:\Users\mohammed\.dotnet\dotnet.exe' test DawnPro.Wpf.slnx -c Release --no-build
# Passed: 79, failed: 0, skipped: 0.

& '.\.venv-win\Scripts\python.exe' -m pytest -q
# 124 passed in 0.80s.
```

The final screenshot files were regenerated from that Release build between `2026-07-30 21:20:55` and `21:21:03` local time, with the cursor parked outside the window to prevent transient hover readouts, and visually inspected through `contact-sheet.png`. No graph/caption/status overlap or horizontal clipping was found at the requested sizes. The stronger individual curves, dashed selected curve, and dominant combined response are visible in both themes.

External UIA could not traverse from the Windows 11 native Open picker into the import confirmation in this noninteractive tool session: the picker exposed a native `#32770` window but did not accept UIA, native text injection, or directed SendKeys submission. Native picker windows and apps were closed normally after each attempt. Modal focus, trapping, metadata, Cancel/default behavior, shell disabling, and focus restoration are therefore verified by the deterministic real-window STA regression rather than claimed as a successful external picker-to-modal run.

A fresh post-redesign five-process batch produced:

```json
{"startupToFirstRenderMs":1236,"privateBytesAfter3sIdle":131915776,"workingSetAfter3sIdle":161447936,"graphEditor1000UpdatesMs":5483}
{"startupToFirstRenderMs":1199,"privateBytesAfter3sIdle":131788800,"workingSetAfter3sIdle":161189888,"graphEditor1000UpdatesMs":2655}
{"startupToFirstRenderMs":882,"privateBytesAfter3sIdle":131989504,"workingSetAfter3sIdle":162172928,"graphEditor1000UpdatesMs":7011}
{"startupToFirstRenderMs":1315,"privateBytesAfter3sIdle":132308992,"workingSetAfter3sIdle":161513472,"graphEditor1000UpdatesMs":3400}
{"startupToFirstRenderMs":1377,"privateBytesAfter3sIdle":165961728,"workingSetAfter3sIdle":200790016,"graphEditor1000UpdatesMs":1278}
```

Compared with the immediately recorded pre-redesign five-run medians (`941 ms`, `131211264` private bytes, `165715968` working-set bytes, `2581 ms` graph/editor), this batch's medians were `1236 ms`, `131989504` private bytes, `161513472` working-set bytes, and `3400 ms` graph/editor. That is `+295 ms`, `+778240` private bytes, `-4202496` working-set bytes, and `+819 ms` graph/editor. Fresh-process WPF timings varied materially in both batches. The redesigned graph performs more real work by drawing one cached curve per enabled band in addition to the combined response, so these local values are evidence rather than guarantees or an improvement claim.

Ten automated demo launch/capture/close cycles exited successfully with no lingering `Moondrop.Wpf` process. The host session was physically exercised only at 100% DPI (`96 DPI`) with high contrast disabled. WPF device-independent sizing, star/auto layout, wrapping, scrolling, official Fluent resources, and explicit high-contrast card fallbacks were inspected in source, but 125%, 150%, 200%, and a live high-contrast Windows session remain environment-only validation gaps and are not claimed as physically tested.

## Offline runtime-integrity remediation (2026-08-08)

Physical activity is **PAUSED / NO-GO**. This remediation performed no HID open, DAC/device access, API access, PREPARE, EXECUTE, RECOVERY, physical-category run, physical opt-in assignment, approval population, EQ action, write, or flash operation.

The retained-tree diagnosis separated two causes. The normal framework-dependent `bin` layout is not the supported flat self-contained apphost layout and differed from the fresh publish in 339 entries, including the .NET runtime/host closure and nested RID/test-platform content. The retained sealed `0fd...` runner differed from fresh `21d...` in exactly `Moondrop.Hardware.dll`, `Moondrop.Hardware.pdb`, `Moondrop.PhysicalTests.dll`, and `Moondrop.PhysicalTests.pdb`; their staged trees differed in five source files, so this was source drift rather than MSTest/VSTest generation, .NET host variation, publish nondeterminism, copied timestamps, generated configuration, testhost, or apphost/watchdog variation. Retained same-source `21d...` and `6deb...` runner trees matched all 331 files.

The complete-output guard now reports every mismatch with relative path, both full paths, expected/actual existence, SHA-256, byte size, and UTC timestamp. SHA-256, size, path, and existence retain full integrity coverage; timestamp remains diagnostic-only. The targeted diagnostic regression was RED against the former generic exception and GREEN after the minimal reporting fix. Focused watchdog tests passed 63/63, and the explicit default-safe suite passed 259/259 with the three physical categories excluded. Two new isolated candidate-only generations matched all 58 staged-source files, all 331 runner files, and all 192 watchdog files; the independently recaptured runtime manifest was equal at 532 inputs in both generations. The approval JSON was reset to both placeholders and all zero counts after source changes.
