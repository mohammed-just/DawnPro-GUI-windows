# .NET WPF Build Instructions

Use the Windows .NET SDK from WSL.

Release restore/build/test:

```powershell
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" restore DawnPro.Wpf.slnx --locked-mode'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" restore tests-dotnet\Moondrop.PhysicalTests\Moondrop.PhysicalTests.csproj --locked-mode'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" restore tests-dotnet\Moondrop.PhysicalWatchdog\Moondrop.PhysicalWatchdog.csproj --locked-mode'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build DawnPro.Wpf.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build tests-dotnet\Moondrop.PhysicalTests\Moondrop.PhysicalTests.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build tests-dotnet\Moondrop.PhysicalWatchdog\Moondrop.PhysicalWatchdog.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true'
powershell.exe -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test DawnPro.Wpf.slnx -c Release --no-build'
```

Run demo mode without touching hardware:

```powershell
powershell.exe -NoProfile -Command '& "C:\Users\mohammed\Documents\moondrop gui\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe" --demo'
```

Run benchmark mode without touching hardware. It prints one JSON line and exits; run it five times because first-render and WPF runtime measurements vary between processes:

```powershell
powershell.exe -NoProfile -Command '& "C:\Users\mohammed\Documents\moondrop gui\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe" --benchmark | Write-Output'
```

Capture a hardware-free render for theme/layout checks:

```powershell
powershell.exe -NoProfile -Command '& "C:\Users\mohammed\Documents\moondrop gui\src\Moondrop.Wpf\bin\Release\net10.0-windows\Moondrop.Wpf.exe" --demo --theme=dark "--screenshot=$env:TEMP\moondrop-wpf.png"'
```

Screenshot mode uses the official solid Fluent fallback because `RenderTargetBitmap` cannot capture the DWM Mica surface.
Use `--width=<DIPs>` and `--height=<DIPs>` for deterministic responsive captures, for example
`--width=1440 --height=900`, `--width=1100 --height=760`, or `--width=760 --height=900`.

Normal launch enumerates hardware asynchronously and selects DAWN PRO2 HID before original Dawn Pro USB. If neither backend opens, the combined error is shown and the app exits.

Theme selection:

```powershell
--theme=system
--theme=light
--theme=dark
```

## DAWN PRO2 physical harness: runtime-integrity remediation PAUSED / NO-GO

Do not run prepare, execute, or recovery yet. Two earlier actual read-only PREPARE preflights on the pinned DAWN PRO2 (PID `0x011D`, firmware exactly `1.5`) completed full consistent raw snapshots and both returned raw Active EQ `9`. That observation is not evidence that the device is in a physical default/custom mode and must not be conflated with PEQ registry profile `7`, which remains the raw-band write selector. The narrow exception permits raw Active EQ `9` only for the exact DAWN PRO2 model, VID/PID, and raw firmware string exactly ordinal-equal to `1.5`; leading or trailing space, tab, carriage return, or any other preserved non-NUL UTF-8 content fails closed. Wrong model, PID, firmware, or any other raw value also remains rejected. The physical workflow performs one minimum quarter-dB PEQ-band mutation, immediately restores all eight captured raw bands plus captured pre/global gains, never writes Active EQ, never flash-saves, and requires a fresh two-pass byte-equivalent full snapshot whose Active EQ still equals the original `9`. Any mismatch, disconnect, unexpected state, or restoration error prevents success. The source/runtime approval is reset. Physical activity remains **NO-GO** pending a complete fifteenth independent audit and complete two-hash approval metadata. No hardware was accessed, no physical opt-in variable was assigned, and no physical category was run during or after this coding remediation.

The offline runtime-integrity remediation on 2026-08-08 did not access the DAC. It ran no PREPARE, EXECUTE, RECOVERY, or physical test category and assigned no physical opt-in variable. `tests-dotnet/tools/Compare-RuntimeTrees.ps1` reports every differing relative path with both absolute paths, existence, SHA-256, byte size, and diagnostic UTC timestamp. The retained normal `bin` tree versus a self-contained publish had 339 differences because framework-dependent/nested-RID output is not the supported flat self-contained apphost tree. A retained older sealed candidate versus a fresh candidate had four changed compiled outputs, traced to five changed staged source files. Same-source retained candidates matched all 331 runner files. Two new isolated candidate generations also matched all 58 staged files, all 331 runner files, and all 192 watchdog files. Timestamps differed between generations but content matched; timestamps remain diagnostic-only. The complete-output guard still covers every file and now emits every mismatch instead of a generic exception. Physical work remains **PAUSED / NO-GO**, and the approval JSON contains only fail-closed placeholders and zero counts.

The source fingerprint is deterministic and source-only. It binds `DawnPro.Wpf.slnx`, both runsettings files, the four files under `tests-dotnet/build-isolation`, and every `.cs`, `.csproj`, `.xaml`, `.rsp`, and `packages.lock.json` below the four source trees. It also includes a fixed presence/absence sentinel for every `.config/dotnet-tools.json`, `Directory.Build.props`, `Directory.Build.rsp`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `MSBuild.rsp`, `NuGet.Config`, and `nuget.config` candidate at every project/solution search directory, repository level, and searched ancestor through the volume root; a present control also contributes its content hash. Entries use stable ordinal labels, every `bin`/`obj` segment and the approval manifest itself are excluded, and independent review plus staging use the same canonical label/content aggregation.

The exact approval lifecycle is two-hash and independent:

1. An independent auditor starts from the reviewed source and uses the candidate-only `--build-runtime-smoke` mode to create a new staged, locked, self-contained `win-x64` runner/watchdog build. That mode performs both hostile-shared-runtime apphost smokes and reports the source hash, source total/sentinel/content counts, runtime hash, runtime total, runner-tree count, watchdog-tree count, metadata count, and retained staged/output paths. It never grants physical authority.
2. The auditor independently reproduces the source and complete runtime calculations, repeats the build in a second clean stage, and requires both hashes and every count to match across generations. Approval is never based merely on source and never copied from a PREPARE observation.
3. Only after that review may the auditor replace both `INDEPENDENT_AUDIT_REQUIRED` values and every zero count in `tests-dotnet/physical-runtime-approval.json` in one reviewed change. The strict schema, runtime identifier, both hashes, and all count relationships must validate. The approval file is excluded from the source hash.
4. The auditor runs `--verify-runtime-approval` against a retained staged source plus its exact runner/watchdog output trees. Any later source, control, SDK/runtime output, lock, dependency, metadata, or manifest-count change invalidates approval and restarts this lifecycle.

The approval JSON is the explicit trusted independent-audit policy input; it is not a digital signature or an OS-enforced separation from the current user. Operational review must therefore control changes to that file. Candidate hashes are reported at audit time and are never embedded in this documentation.

### Default software-only tests

Ordinary unfiltered tests use `tests-dotnet/default.runsettings` automatically. The filter excludes `PhysicalHardwarePrepare`, `PhysicalHardware`, and `PhysicalHardwareRecovery` before execution, even if physical opt-in variables were inherited. These tests are excluded, not counted as skipped. Each physical method also requires a marker found only in `tests-dotnet/physical.runsettings`.

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test '.\DawnPro.Wpf.slnx' -c Release --no-build
```

### 1. Read-only prepare

Prepare is the only read-only physical category after approval. Before any restore or HID access, the builder snapshots the strict two-hash approval metadata, takes the cooperative build lock, creates a unique isolated source staging tree, preserves repository-relative paths, copies external build controls into an audit-only namespace, and writes an explicit manifest for every content input and presence/absence sentinel. The staged hash and all source counts must equal the approval before compilation. The builder then applies and verifies per-entry Windows ACL denials for write, delete, rename, and child creation to the invoking user and its token identities, plus read-only attributes, while retaining read/traverse access. Read-only native handles held with read-only sharing for every staged file and directory reject pre-existing or later write/delete-capable handles. These controls fail closed where configured, but the current owner can reset the DACL; they are defense-in-depth, not the authorization invariant.

All restore intermediates, NuGet assets/cache, compiler outputs, publish outputs, profiles, and temporary files are generation-local and outside the staged source tree. The audited build props map the distinct source and generated roots to stable compiler paths, and only the physical runner and watchdog projects that are actually published are built inside this transaction. The build lock remains held across staging, approval snapshot, defense-in-depth protection, locked restore, publish, both direct apphost smokes, source/runtime manifests, child termination on failure, and ACL/attribute restoration.

Immediately after each isolated publish and before the runtime manifest is accepted, the builder directly executes that exact apphost with `--help`. The child has a cleared environment, no physical opt-ins, and `DOTNET_ROOT`, `DOTNET_ROOT_X64`, and `DOTNET_SHARED_STORE` pointed at nonexistent paths. Exit `0`, an unchanged publish tree, no surrounding physical artifacts, and no smoke temp files are mandatory for both `Moondrop.PhysicalTests.exe` and `Moondrop.PhysicalWatchdog.exe`. The builder then captures every file in both complete self-contained trees plus required path-independent metadata and requires exact equality with the independently approved runtime hash and all runner/watchdog/metadata counts. It does not record or bless an observed output. A transient staged edit that changes compilation output, even if the staged bytes are restored before the final source recheck, changes the runtime manifest and fails before HID. Dependency-closure validation also rejects a `.deps.json` runtime assembly missing from the publish tree.

Only after the approved runtime proof may PREPARE send reads. It pins the exact DAWN PRO2 model, VID/PID, serial, and HID path, opens through the explicit read-only/no-watchdog progress path, and captures two complete consistent firmware-`1.5` snapshots. Raw Active EQ `9` is accepted only for that precise identity and a firmware string exactly ordinal-equal to `1.5`; profile `7` remains a separate PEQ registry/write-packet property. PREPARE preserves raw `9` in the snapshot, plan, and session and sends no state-changing command. Before publishing a session it recaptures live source, both fresh runtime trees, and metadata, requires both approval hashes/counts again, and requires the loaded runner tree to equal the fresh runner tree. The session binds the approved source and runtime hashes. No PREPARE command is documented while fifteenth-remediation status remains NO-GO.

Copy the emitted session path and one-run confirmation. Do not edit either the primary JSON or its deterministic `.recovery.json` copy.

### 2. Execute only through the external watchdog

Raw `dotnet test`, `dotnet <app>.dll`, and framework-dependent execution are unsupported and fail before HID access. Execute/recovery require the direct parent to be the published self-contained watchdog apphost under `tests-dotnet/artifacts/physical-runtime`. The child verifies the real WMI parent PID/start time/executable and heartbeat, then after loading the durable file compares its session ID, one-run token, source hash, runtime hash, and canonical Original/Plan lineage with that authenticated heartbeat before any HID open/write. The watchdog launches the isolated self-contained MSTest apphost directly, with no `dotnet`/`testhost` intermediary. Immediately before process creation it calls `ProcessStartInfo.Environment.Clear()` and adds only canonical current `SystemRoot`, `WINDIR`, `TEMP`, and `TMP` plus the exact phase opt-in, session/confirmation or recovery snapshot, direct-parent identity, heartbeat, ownership token, session ID, one-run token, source hash, runtime hash, and lineage values. `PATH` is unnecessary for the absolute self-contained apphost and is omitted. Any extra key, unsafe NUL/CR/LF value, non-canonical path, or identity mismatch fails before launch; ambient `DOTNET_*`, `COMPlus_*`, `CORECLR_*`, `COR_*`, profiler, MSBuild, NuGet, test-runner, `MOONDROP_*`, and arbitrary variables are not inherited.

Review the redacted command plan without launching tests:

```powershell
$session = 'C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-REPLACE.json'
$confirmation = 'REPLACE_WITH_ONE_RUN_TOKEN'
& '.\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe' --mode execute --session $session --confirmation $confirmation --dry-run
```

Future approved execution uses the same command without `--dry-run`:

```powershell
& '.\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe' --mode execute --session $session --confirmation $confirmation
```

Before every execute or recovery child launch, the watchdog reacquires the build lock and repeats the staged locked build and both smokes in a new durable-session/generation directory. The fresh source hash and runtime hash/counts must match both the session and the current strict approval metadata. Each `dotnet` child receives a cleared explicit environment; generation-local profile, AppData, CLI home, temp, package cache, HTTP cache, and SDK artifacts root; staged `physical.NuGet.Config`; locked mode; staged Directory.Build/Packages controls; central-package disablement; no shared compiler/build server; and `-noAutoResponse`. The runtime manifest hashes every file recursively under both self-contained publish trees plus path-independent `packages.lock.json`, `global.json`, NuGet, and explicit build-control bytes. Raw `obj/project.assets.json` is deliberately excluded because it contains generation paths. Runtimeconfig validation rejects shared `framework`/`frameworks` and requires the self-contained `includedFrameworks` contract. The watchdog self-check covers its entire tree; the child recomputes actual source/runtime equality and requires both session hashes to equal both approval hashes before HID access. Missing or changed apphost, managed DLL, native, deps, runtimeconfig, other published, lock, control, approval, or declared count fails closed.

Execute failure or timeout always remains a nonzero overall result. Successful supervised recovery is reported distinctly as `EXECUTE FAILED; RECOVERY VERIFIED`; it never converts execution into success.

EXECUTE first requires a fresh exact preflight match, then performs only the planned quarter-dB write to one supported PEQ band. It immediately persists `RestorationStarting`, rewrites every captured raw band plus captured pre/global gains, and performs a fresh two-pass complete raw readback. Active EQ is never written; exact comparison requires it to remain the captured raw `9`. Only byte-equivalent bands, gains, identity, firmware, and Active EQ may advance through `RestorationWritesVerified`, `RestorationVerified`, and `Completed`. There is no flash save or physical cycle in this test. Primary and restoration errors remain separate, and no primary mismatch/error can be converted into success by a later successful restore.

### 3. Emergency recovery only through the watchdog

For an existing non-Completed session whose writes may be outstanding, leave both session files untouched. Recovery reopens the exact identity, reads two consistent snapshots, revalidates firmware `1.5`, and rejects every write unless the full current state is exactly reachable for that session's durable phase. There is no force override.

```powershell
$session = 'C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-REPLACE.json'
& '.\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe' --mode recovery --session $session
```

Except for terminal cases, recovery validates the full current snapshot against only the original state or the single planned temporary-band state reachable for that durable phase, persists/re-enters `RestorationStarting`, rewrites all captured raw bands plus captured pre/global gains, and requires a fresh two-pass byte-equivalent full readback. It never writes Active EQ and never flash-saves. `Failed` carries `LastSafePhase`, which constrains compatibility. `RestorationVerified` accepts only the exact original snapshot, including original raw Active EQ `9`, and advances to `Completed`; `Completed` is a no-HID/no-write no-op.

### Exact limits and residual risks

- HidSharp native read/write calls have two-second native timeouts. HidSharp open/dispose are not cancellable in process; boundedness comes only from the external watchdog process boundary.
- A watchdog refuses to terminate anything if PID/start-time/command-line/token ownership cannot be proven. If WMI ownership verification is unavailable, automatic termination/recovery cannot be guaranteed.
- Request correlation is not proven beyond the exact 64-byte envelope and report ID. The retained device serializes each complete write/owed-read/parser/cancellation transaction. Once a response-expected native write begins, any write error/timeout is ambiguous and atomically poisons/disposes the retained transport; cancellation before native entry remains non-poisoning, while successful post-write cancellation drains the owed response first. Envelope failures and semantically invalid firmware, active-EQ, gain, or band payloads also poison by policy, even when fully drained. A valid-shaped unrelated unsolicited frame remains a residual until read-only captures establish correlation fields.
- Firmware Active EQ readback and PEQ registry profile are separate fields. The precise DAWN PRO2 PID `0x011D` / firmware `1.5` raw readback `9` is retained and never written by this harness; it is not proof of a physical default/custom toggle. Raw band writes still start from a zeroed canonical writer packet with validated band selector at byte 4, registry profile `7` at byte 35, and only proven bytes 7-33 copied from the capture.
- Schema-v3 session content uses write-through, `Flush(true)`, and atomic rename. Primary and `.recovery.json` copies are parsed independently, so one malformed copy cannot block the other. Two valid copies must have identical session/token/source/runtime/original/plan lineage before timestamp selection. After supervision, exit zero is possible only for freshly loaded `Completed` with the same complete lineage; child zero while Prepared/inconclusive/replaced is nonzero and cannot claim success. This code still cannot claim a supported Windows directory-fsync guarantee; simultaneous loss of both names remains an OS/filesystem/power-loss residual.
- Coefficient-enable remains command-path-only coverage; no independent DSP/register acknowledgement is claimed. Active-EQ writes and physical EQ-mode toggles are outside this harness.
- An arbitrary HID client can still race after the final process check. The machine-wide lock coordinates this harness only.
- The current Windows owner can reset the staged DACL, and the approval JSON itself is not signed. ACL/handle checks reduce accidental or ordinary concurrent mutation but do not establish same-user invulnerability; operational control of independent approval metadata is part of the trust model.
- This narrow harness performs no flash operation, physical cycle, clear-flash, erase-config, or firmware upgrade.
