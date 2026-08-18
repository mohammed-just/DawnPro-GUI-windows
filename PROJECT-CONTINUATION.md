# DAWN PRO / DAWN PRO2 WPF Project Continuation Guide

> **The latest continuation state is in `PROJECT-HANDOFF-NEW-HARNESS.md`. Read that file first before doing any work. For harness setup/installation, see `DEEPSEEK-HARNESS-MIGRATION.md`.**

**Last updated:** 2026-08-14
**Status:** Round-10 offline MTP-evidence remediation is green locally and awaits two fresh independent pre-candidate reviews. Physical work is stopped.
**Audience:** A new Hermes/Codex/engineering agent continuing the project.

---

## 1. Project goal

Deliver and validate a production-quality, lightweight **.NET 10 WPF** replacement for the existing Python Moondrop DAWN PRO / DAWN PRO2 application while preserving the Python program as the behavioral oracle.

The desktop application and Fluent responsive redesign are substantially complete. The remaining high-risk work is the guarded DAWN PRO2 physical-validation workflow:

1. Capture an exact raw state read-only.
2. Apply only one explicitly authorized temporary PEQ mutation.
3. Prove RAM readback.
4. Perform persistence/flash validation only when separately authorized and required by the approved protocol.
5. Restore every original raw byte, including opaque coefficient bytes, gains, and raw `active_eq`.
6. Prove restored state and restored persistence after genuine USB disappearance/reappearance where required.
7. Never publish physical success unless complete restoration succeeds.

The immediate engineering problem is **not device protocol behavior**. It is the EXECUTE watchdog process-lineage authentication gate and its diagnostics.

---

## 2. Project location and required environment

### Repository

- Windows: `C:\Users\mohammed\Documents\moondrop gui`
- WSL: `/mnt/c/Users/mohammed/Documents/moondrop gui`
- Active solution: `DawnPro.Wpf.slnx`
- Current Git base: `main` at historical base commit `3ce0eb2`

The repository has extensive protected pre-existing/unrelated working-tree changes and CRLF noise. Do not clean, reset, normalize, overwrite, or broadly format the tree. Use scoped comparisons and scoped `git diff --check` for files changed by the current work.

### Required .NET SDK

Use only:

```text
C:\Users\mohammed\.dotnet\dotnet.exe
```

Required/verified SDK version: `10.0.302`.

Do not rely on `C:\Program Files\dotnet` or a WSL .NET SDK.

### Python oracle

```powershell
.\.venv-win\Scripts\python.exe -m pytest -q
```

Latest preserved Python result before the current remediation: **124 passed**.

### Important package constraints

- Target: `net10.0-windows` WPF
- HidSharp: `2.6.4`
- LibUsbDotNet: `3.0.224`
- Use official WPF Fluent APIs (`Application.ThemeMode`, `Window.ThemeMode`)
- Suppress `WPF0001` narrowly
- Do not add WinUI 3, Electron, WebView2, chart libraries, browser rendering, heavy UI frameworks, or unnecessary dependencies

---

## 3. Architecture

### Existing Python oracle

- `main.py`
- `device/`
- `tests/`
- `config.json`

Preserve its behavior, tests, packaging, user work, and unrelated changes.

### .NET solution

- `src/Moondrop.Core/`
  - configuration compatibility
  - DAWN PRO/PRO2 protocol and packet encoding
  - fixed-point conversion and PEQ math
  - raw DAWN PRO2 state models
- `src/Moondrop.Hardware/`
  - HidSharp/LibUsbDotNet transports
  - serialized I/O
  - deterministic disposal
  - ambiguous no-response poisoning
  - device orchestration
- `src/Moondrop.Wpf/`
  - Fluent WPF shell
  - responsive layouts
  - accessible in-app modal system
  - diagnostics
  - interactive eight-band logarithmic EQ graph
- `tests-dotnet/Moondrop.Tests/`
  - default-safe software tests
  - protocol/hardware behavior tests
  - WPF tests
  - physical-support and watchdog policy tests
- `tests-dotnet/Moondrop.PhysicalTests/`
  - isolated self-contained physical runner
- `tests-dotnet/Moondrop.PhysicalWatchdog/`
  - isolated self-contained watchdog
  - source/runtime manifests
  - approval verification
  - process supervision
- `tests-dotnet/default.runsettings`
  - must always exclude physical categories, even if physical environment variables leak
- `tests-dotnet/physical.runsettings`
  - dedicated explicitly opted-in physical workflow

### Separation invariants

Keep these layers separate:

1. Protocol/configuration
2. Hardware transport/orchestration
3. WPF presentation
4. Physical runner
5. Watchdog/runtime integrity
6. Independent audit and approval

Ordinary application saves must never implicitly write or flash DAWN PRO2 EQ/gain state.

---

## 4. Completed application work

The following is already implemented and verified:

- Python behavior inspected and retained as oracle
- Core/Hardware/WPF/MSTest project structure
- Python-compatible configuration and protocol behavior
- production HID/USB transports
- serialized hardware orchestration and deterministic disposal
- Fluent responsive WPF redesign across wide/medium/narrow/minimum widths
- Light/Dark themes and Mica fallback
- accessible in-app modal UI instead of legacy `MessageBox`
- interactive eight-band logarithmic EQ graph
- ordinary saves kept local and non-flashing
- protocol, ordering, cancellation, disposal, graph, accessibility, async-close, and theme regressions
- extensive physical harness integrity, recovery, staging, manifest, hostile-environment, and process-conflict safeguards
- complete self-contained runner/watchdog publishing and approval-manifest binding

Before the current lineage remediation, latest software status was:

- Default-safe .NET: **260 passed**
- Python: **124 passed**
- Release builds: **0 warnings / 0 errors**

After lineage remediation round 1:

- Focused lineage/watchdog tests: **172 passed**
- Full default-safe suite: **280 passed**
- Full default-safe suite with hostile leaked physical opt-ins: **280 passed**
- Relevant builds: **0 warnings / 0 errors**

These round-1 results do not authorize candidate builds because independent audits found blockers.

After round-2 remediation through cycle 13 and the 2026-08-12 continuation verification:

- Focused lineage/watchdog tests: **180 passed**
- Full default-safe suite: **288 passed**
- Full default-safe suite with hostile leaked physical opt-ins: **288 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

These results still do not authorize candidate builds. Two fresh independent pre-candidate reviewers must issue GO on the current source first.

The next pre-candidate review pair returned NO-GO and identified a probe-only launch-plan constructor, incoherent MTP/termination identity reads, inherited candidate-watchdog environment, incomplete cancellation cleanup and post-MTP tree capture, missing exact-one-test evidence, diagnostic leaks/ambiguity, and reparse/check-use ordering gaps. Round-3 strict TDD remediation through cycle 30 now has:

- Focused lineage/watchdog tests: **195 passed**
- Full default-safe suite: **303 passed**
- Full default-safe suite with hostile leaked physical opt-ins: **303 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

Round 3 provides one immutable launch plan and one materializer for production/offline paths, coherent live and termination identities, a scrubbed/cancellation-safe candidate watchdog, exact-one retained TRX evidence, complete post-MTP runtime recapture, manifest-authoritative role diagnostics, complete secret/control sanitization, validation-before-write ordering, dangling-reparse rejection, and operation-lifetime stable path leases. These results still do not authorize candidate builds until two fresh reviewers issue GO on this exact current source.

The round-3 reviewers returned NO-GO on constrained TRX publication, full descendant-exit proof, canonical topology environment values, live authorization/session check-use leases, first-write ordering, durable exception redaction, and bounded wrapper cleanup. Round-4 remediation through cycle 35 is green: unique empty-directory/leased TRX evidence, one shared canonical environment validator, kill-on-close job accounting to zero, stable read leases for apphost/manifest/heartbeat/session binding, component-by-component reparse-safe directory creation, durable exception sanitization, and bounded direct/wrapper cleanup. Current results are **199 focused**, **307 default-safe**, **307 hostile-opt-in default-safe**, **124 Python**, and all three Release builds at **0 warnings / 0 errors**. Two new independent GO reviews are still mandatory.

The round-4 reviewers returned NO-GO on the process-start-before-job-assignment race, missing post-parse TRX directory re-enumeration, wrapper argument splitting, and incomplete immediate-restoration durable redaction. Round-5 remediation through cycle 40 now creates every supervised child suspended, assigns it to the kill-on-close job before coherent observation and first execution, proves cleanup for validation failure, rechecks unique TRX publication after parsing, carries exact spaced native arguments through real wrappers, and redacts the one-run token, confirmation, and session path from restoration aggregates. Current results are **203 focused**, **311 default-safe**, **311 hostile-opt-in default-safe**, **124 Python**, and all three Release builds at **0 warnings / 0 errors**. Two fresh independent GO reviews remain mandatory on this exact source.

The first fresh round-5 topology review found one remaining no-ownership-transfer cleanup window: a managed process lookup after successful `CreateProcessW` could throw before the guard, and termination errors could short-circuit root-exit waiting. Round-6 remediation through cycle 41 wraps every post-native-create step in the transfer guard, proves native-root exit even if job cleanup reports a failure, and uses one shared root-waiting cleanup contract in direct/wrapper/candidate and production supervision paths. Current results are **204 focused**, **312 default-safe**, **312 hostile-opt-in default-safe**, **124 Python**, and all three Release builds at **0 warnings / 0 errors**. Fresh independent GO reviews remain mandatory on this exact source.

Both fresh round-6 reviewers returned NO-GO on an unbounded root wait after job cleanup failure. Round-7 remediation through cycle 42 gives every owned root a bounded exit window, forces only that known root tree if necessary, then requires bounded exit before reporting aggregated cleanup failure. Current results are **205 focused**, **313 default-safe**, **313 hostile-opt-in default-safe**, **124 Python**, and all three Release builds at **0 warnings / 0 errors**. Fresh independent GO reviews remain mandatory on this exact source.

---

## 5. Physical device and protocol facts

Prepared/connected hardware identity:

- Model: `DAWN PRO2`
- VID: `0x35D8`
- PID: `0x011D`
- Serial: `35D8011D251117`
- Physical instance: `USB\VID_35D8&PID_011D\35D8011D251117`
- Firmware: exact ordinal string `"1.5"`
- Report ID: `75`
- Report length: `64`
- Observed raw active EQ: `9`

### Critical active-EQ rule

Raw `active_eq == 9` is accepted only for the exact identity above and firmware exactly `"1.5"` using ordinal equality. Do not trim, normalize, case-fold, alias, or coerce it.

Protocol PEQ selector/profile `7` is separate from raw active-EQ readback `9`. Never conflate them.

The workflow must not call:

- `set-eq-index`
- `WriteActiveEqAsync`
- `SetActiveEqAsync`
- any operation that toggles the DAC physical EQ mode

### Planned temporary mutation (future, not currently authorized to run)

Only:

```text
Band 0 gain: raw 1536 / +6.00 dB → raw 1600 / +6.25 dB
```

No other band, pre-gain, global gain, active EQ, profile, or unrelated command may change.

---

## 6. Historical Prepared session — audit only

The previous successful read-only PREPARE is preserved for audit/history but is **invalid for future EXECUTE because source changes have begun**.

### Session

Windows:

```text
C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-20260809T082427471Z-459d6e7995004a658b92ffebf33a0230.json
```

SHA-256:

```text
27dde295e79e09e314b7c3e15879e420eb56a1fd202897c6eb0441a089dfb7ef
```

### Raw read frames

Windows:

```text
C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\hardware-results\dawn-pro2-frames-20260809T082427471Z-459d6e7995004a658b92ffebf33a0230.json
```

SHA-256:

```text
b4b0a914e8c1b29110aba0c8827fd89ca55c2c66b6feec988c42f891e55625e8
```

Do not edit, delete, move, regenerate, or reuse these artifacts for EXECUTE.

Historical PREPARE result:

- phase `Prepared`
- 24 HID reads
- two complete byte-identical snapshots
- zero writes
- zero gain writes
- zero active-EQ writes
- zero flash saves
- no error

The historical EXECUTE attempt failed before HID access at the watchdog direct-parent authentication gate. It issued zero mutation/restoration/flash commands.

---

## 7. Current approval state

`tests-dotnet/physical-runtime-approval.json` is intentionally fail-closed:

```json
{
  "SchemaVersion": 1,
  "RuntimeIdentifier": "win-x64",
  "SourceSha256": "REQUIRES-INDEPENDENT-AUDIT",
  "SourceInputCount": 0,
  "SourcePresenceSentinelCount": 0,
  "SourceContentInputCount": 0,
  "RuntimeSha256": "REQUIRES-INDEPENDENT-AUDIT",
  "RuntimeInputCount": 0,
  "RunnerTreeInputCount": 0,
  "WatchdogTreeInputCount": 0,
  "MetadataInputCount": 0
}
```

Do not populate it until two independent, matching, current-source isolated candidates pass all audits. Any source/test/harness/diagnostic/document change after candidate generation invalidates that pair.

Historical approved hashes (`5B5...` / `608F...`) are obsolete and must not be restored.

---

## 8. Current blocker: watchdog process-lineage remediation

### Original failure

The approved EXECUTE watchdog launched the physical test, but the physical test rejected authorization:

```text
Execute and recovery require the authenticated direct-parent
Moondrop.PhysicalWatchdog Release process; raw dotnet test is rejected.
```

It failed before session load/HID open/write.

### Round-1 remediation

Round 1 added:

- exact predicate diagnostics
- PID/parent/start/path/hash reporting
- bounded parent-chain reporting
- offline topology probe
- wrapper/stale/wrong-path/cycle/depth regressions
- fail-closed approval placeholders

Evidence file:

```text
tests-dotnet/OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md
```

Round 1 observed this specialized probe edge:

```text
Moondrop.PhysicalWatchdog.exe
└── Moondrop.PhysicalTests.exe
```

However, this did not conclusively exercise actual Microsoft Testing Platform test execution or the exact production EXECUTE/RECOVERY launcher seam.

### Independent audit verdict

Two independent auditors issued **BLOCKED / NO-GO**. Full reports:

```text
/home/mohammed/.hermes/cache/delegation/subagent-summary-0-20260809_163307_338207.txt
/home/mohammed/.hermes/cache/delegation/subagent-summary-1-20260809_163307_339988.txt
```

Live traces:

```text
/home/mohammed/.hermes/cache/delegation/live/deleg_ff2f7cc3/task-0.log
/home/mohammed/.hermes/cache/delegation/live/deleg_ff2f7cc3/task-1.log
```

### Blocking findings to resolve

1. **Wrong experimental seam**
   - The probe branches before `TestApplication.CreateBuilderAsync`.
   - It uses a separate `ProcessStartInfo` instead of the production `RunSupervisedAsync` launch seam.
   - It does not prove actual MTP test-execution topology.

2. **Not bound to the exact future candidate**
   - Probe publishes are separate from protected `physical-runtime` candidate trees.
   - Only apphost EXE hashes were retained, not complete candidate runner/watchdog trees and runtime manifest.

3. **Wrapper regression can pass for the wrong reason**
   - Unit test runs the child routine in the ordinary test process, which already has the wrong executable name.
   - Must launch the real published physical apphost through an actual wrapper and retain structured rejection evidence.

4. **Dry-run credential disclosure**
   - `DescribeForDryRun` exposes ownership token and token-bearing paths/environment values.
   - All ownership, one-run, confirmation, heartbeat/results path secrets must be redacted.

5. **Lexical-only path trust**
   - `Path.GetFullPath`/`GetRelativePath` does not resolve or reject junction/reparse redirection.
   - Trusted executable, runtime, heartbeat, and report paths need final-target containment or strict reparse rejection.

6. **Unsafe offline report publication**
   - Arbitrary report path overwrite is possible.
   - Predictable `<report>.tmp` creates a race.
   - Reports must remain under a canonical dedicated root, reject reparse ancestors and existing targets, use unpredictable create-new temporary files, and atomically publish without overwrite.

7. **Mixed process identities / PID reuse race**
   - Start time and WMI path/parent are read separately without coherent revalidation.
   - Must fail on disappearance, PID reuse, or identity drift.

8. **Diagnostics mix runner/watchdog fields**
   - Expected runner, actual runner, expected watchdog, and actual watchdog must be separate coherent records.

9. **Heartbeat self-asserts expected hash**
   - Expected apphost hashes must come from the independently bound complete runtime manifest/session provenance, not a mutable heartbeat field.
   - Both watchdog and runner apphost entries must be manifest-covered.

10. **Log/control-character injection**
    - Diagnostic values must escape or reject CR/LF/NUL/control characters.

Do not weaken the lineage guard or generic-allowlist `dotnet.exe`, `testhost.exe`, PowerShell, shells, or wrappers.

---

## 9. Round-2 status

The earlier round-2 attempt was interrupted before work began, but development later resumed. Current repo-local evidence in `tests-dotnet/OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md` records thirteen completed RED-to-GREEN cycles covering the ten audit blocker classes plus offline restore, unrelated-artifact isolation, and real wrapper invocation/exit propagation.

On 2026-08-12, the current source reproduced cycle 13 GREEN and passed the focused, full default-safe, hostile-environment, Python, and Release-build gates listed above. Root-level pytest initially failed while collecting an intentionally inaccessible generated artifact; a new `pytest.ini` now restricts discovery to the authoritative `tests/` suite, and the exact documented `python -m pytest -q` command passes all 124 tests.

Round 2 has not yet received the two required fresh independent pre-candidate GO reviews. Do not build approval candidates, populate approval metadata, or run a physical phase before those reviews.

---

## 10. Required next development sequence

### Phase A — close the lineage blockers offline

Use strict RED-GREEN-REFACTOR TDD, one vertical behavior at a time:

1. Write one failing regression.
2. Run it and preserve the expected RED output.
3. Write the minimum implementation.
4. Run focused GREEN.
5. Refactor only while green.
6. Repeat.

Required behaviors:

- actual published MTP-contained offline test executed through the shared production launcher seam
- exact observed OS process topology from inside actual test execution
- exact candidate runtime/tree binding
- real published wrapper/intermediary rejection
- complete dry-run secret redaction
- reparse/junction-safe trusted paths
- collision-safe constrained report publication
- coherent process identity capture/revalidation
- unambiguous expected/actual records
- independent runtime-manifest-bound runner/watchdog hashes
- CR/LF/NUL/control-character-safe diagnostics
- missing/malformed/stale/wrong-path/wrong-hash/cycle/depth/PID-reuse cases
- default-safe physical exclusion with hostile leaked opt-ins

The offline MTP probe must be structurally incapable of device access. Do not simply rely on a convention or prompt. It must have exact selection, clean environment, no physical opt-in, and a code path that cannot construct/open a hardware transport.

### Phase B — software verification

Use the pinned Windows SDK and run at least:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test `
  'tests-dotnet\Moondrop.Tests\Moondrop.Tests.csproj' `
  --configuration Release `
  --settings 'tests-dotnet\default.runsettings' `
  --filter 'FullyQualifiedName~Moondrop.Tests.PhysicalIntegrationSupportTests|FullyQualifiedName~Moondrop.Tests.PhysicalWatchdogTests' `
  --no-restore --nologo

& 'C:\Users\mohammed\.dotnet\dotnet.exe' test `
  'DawnPro.Wpf.slnx' `
  --configuration Release `
  --settings 'tests-dotnet\default.runsettings' `
  --no-restore --nologo

.\.venv-win\Scripts\python.exe -m pytest -q
```

Also run:

- full default-safe suite with hostile leaked physical opt-ins
- Release solution build
- isolated physical runner build
- isolated watchdog build
- exact-candidate offline MTP topology smoke
- real wrapper/intermediary rejection smoke
- scoped `git diff --check`
- process cleanup verification
- historical artifact SHA-256 verification

No physical test category may run in these phases.

### Phase C — independent pre-candidate review

Dispatch at least two read-only agents:

1. Security/lineage auditor
2. Real topology/MTP/testability auditor

They must not edit, build, set physical opt-ins, enumerate/open HID, or approve metadata. Resolve every valid finding. Any production/source edit after review requires fresh review.

### Phase D — two independent isolated candidates

Only after both pre-candidate reviews are GO:

1. Generate candidate A using the production locked isolated candidate builder.
2. Wait for it to finish and release the build lock.
3. Generate candidate B sequentially from unchanged source.
4. Retain both full source/runner/watchdog trees and manifests.
5. Recalculate source from each staged `.physical-source-inputs.json` manifest.
6. Recalculate complete runtime manifests.
7. Compare source entries, runner trees, watchdog trees, metadata, live/staged sentinels, and live/staged content.
8. Require zero differences and identical hashes/counts.
9. Exercise the exact candidate apphosts with the offline MTP topology smoke.

Do not run candidates in parallel because the build lock serializes them.

### Phase E — independent approval

Send both completed retained candidates to two independent read-only auditors. Require both to approve the exact same:

- source SHA-256 and counts
- runtime SHA-256 and counts
- runner/watchdog tree contents
- metadata inputs
- live/staged source equivalence
- exact-candidate topology evidence
- current-user operational trust-model limitations

Only then replace `tests-dotnet/physical-runtime-approval.json` with the exact agreed pair. Immediately verify strict approval against both retained trees.

### Phase F — exactly one fresh read-only PREPARE

After approval:

1. Ensure no runner/watchdog/client conflict already exists.
2. Release any official browser/WebUSB client without killing unrelated browser processes.
3. Run exactly one approved read-only PREPARE.
4. Require exact device identity/firmware/raw-active-EQ gates.
5. Require two complete byte-equivalent snapshots.
6. Require 24 reads and zero writes/flash saves.
7. Preserve the new session and raw frames durably.
8. **Stop and report. Do not start EXECUTE.**

If PREPARE fails for any reason, stop immediately and diagnose offline. Do not retry automatically or weaken a guard.

---

## 11. Agent roles recommended for continuation

### Primary implementation agent

Responsibilities:

- strict TDD implementation
- surgical source changes
- no physical access during remediation
- preserve line endings and unrelated work
- maintain evidence file

Preferred runtime when available:

- Mohammed's authenticated native Windows Codex installation
- Fast mode (`service_tier="fast"` or `/fast` where supported)
- pinned working directory
- explicit safe sandbox/approval settings

Current Codex usage is exhausted until the date noted above, so Hermes-native tools or another explicitly authorized coding agent may be used in the meantime.

### Security/lineage audit agent

Review:

- process ancestry and PID-reuse safety
- start-time/path/hash coherence
- manifest trust source
- wrapper/intermediary handling
- reparse/junction containment
- diagnostic secret redaction/log injection
- TOCTOU and report publication races
- fail-before-HID behavior

Read-only only. No physical opt-ins or hardware access.

### Testability/topology agent

Review:

- actual MTP-contained experiment
- shared production launcher seam
- false-positive/false-negative test risks
- exact published candidate used by smoke
- wrapper tests use real apphosts
- probe cannot discover physical tests or reach transport code
- default-safe suite excludes physical tests under hostile environment leakage

### Code-quality agent

Review:

- separation of responsibilities
- API clarity
- deterministic disposal
- async correctness
- diagnostic structure and maintainability
- duplication introduced by remediation
- no unrelated changes

Use the `requesting-code-review` and `simplify-code` workflows only after behavior is green. Simplification must never weaken guards or alter physical semantics.

### Performance agent

Performance is secondary to correctness and safety. Review only after functional/security approval:

- startup cost
- WPF responsiveness
- hardware I/O serialization latency
- no blocking UI thread work
- no unnecessary polling
- no heavy framework/dependency additions
- manifest hashing/build time only where optimization preserves complete integrity

Never optimize away full hashing, fresh isolated builds, exact snapshots, identity checks, or recovery safeguards.

### Independent approval agents

Two separate read-only reviewers must independently reproduce and approve the same candidate pair. Their output is policy evidence, not a replacement for the parent agent's verification.

---

## 12. Skills used and where they are stored

Hermes skills are procedural guidance. Load relevant skills before acting.

### Core skills used for this project

- `test-driven-development`
  - strict RED-GREEN-REFACTOR
  - stored under: `/home/mohammed/.hermes/skills/software-development/test-driven-development/`
- `systematic-debugging`
  - root-cause-first debugging
  - stored under: `/home/mohammed/.hermes/skills/software-development/systematic-debugging/`
- `production-change-safety`
  - stateful hardware/change ledger/independent audit
  - stored under: `/home/mohammed/.hermes/skills/devops/production-change-safety/`
- `physical-hardware-validation`
  - safe real-hardware validation
  - stored under: `/home/mohammed/.hermes/skills/devops/physical-hardware-validation/`
- `requesting-code-review`
  - pre-release/pre-commit quality review
  - stored under: `/home/mohammed/.hermes/skills/software-development/requesting-code-review/`
- `codex`
  - native Codex delegation workflow
  - stored under: `/home/mohammed/.hermes/skills/autonomous-ai-agents/codex/`
- `computer-use`
  - background desktop interaction
  - stored under: `/home/mohammed/.hermes/skills/autonomous-ai-agents/computer-use/`
- `windows-ui-automation-from-wsl`
  - Windows UI/CDP/UIPI behavior from WSL
  - stored under: `/home/mohammed/.hermes/skills/software-development/windows-ui-automation-from-wsl/`

### Important production-safety references

Under:

```text
/home/mohammed/.hermes/skills/devops/production-change-safety/references/
```

Relevant references include:

- `stateful-hardware-integration-testing.md`
- `physical-approval-manifest-reproduction.md`

### Loading a skill

From a Hermes agent, call `skill_view(name='<skill-name>')`. Do not rely solely on remembered summaries when the skill can be loaded live.

---

## 13. Safety and threat model

The user accepted the documented **current-user operational trust model**:

- the current Windows owner controls source, staging, and approval metadata
- same-owner ACL reset is an accepted limitation
- approval JSON is trusted policy input, not a cryptographic signature or separate-principal security boundary

This acceptance does not waive:

- source/runtime integrity
- exact device identity
- restoration
- transport poisoning
- watchdog supervision
- complete raw-state preservation
- flash/persistence safety
- independent audit

Residual risks that must remain disclosed:

- unsolicited valid-shaped HID response correlation is not fully proven
- native HID open/dispose cannot be made fully cancellable
- competing HID clients may race
- flash interruption/power loss cannot be atomic
- approval/staging is operational trust, not cryptographic separation
- exact USB path identity is mandatory

---

## 14. Physical workflow hard rules

- Never run a physical category accidentally.
- Never infer EXECUTE authorization from PREPARE success.
- Never reuse a stale Prepared session after source changes.
- Never automatically retry a failed physical phase.
- Never start duplicate PREPARE/EXECUTE/RECOVERY/watchdog/runner processes.
- Never claim HID reopen proves power-loss persistence.
- Genuine USB disappearance/reappearance is required for persistence validation.
- Preserve complete raw state before first write.
- Restore from raw snapshot bytes, not reconstructed floating-point values.
- Preserve all eight raw bands, pre-gain, global gain, opaque coefficient bytes, firmware, identity, and raw active EQ `9`.
- Poison/dispose transport after ambiguous state-changing I/O.
- Enter recovery immediately after ambiguous write/readback/restoration failure.
- Retain recovery artifacts until restored persistence is proven.
- Never claim success before complete restoration and required persistence checks.
- Tell the user to unplug/replug only when an authenticated active phase explicitly waits for that action.

---

## 15. Key project files

### Application/protocol

- `DawnPro.Wpf.slnx`
- `src/Moondrop.Core/Protocol/DawnPro2Protocol.cs`
- `src/Moondrop.Core/Devices/RawPeqBandState.cs`
- `src/Moondrop.Hardware/Devices.cs`
- `src/Moondrop.Hardware/Transports.cs`
- `src/Moondrop.Hardware/TransportContracts.cs`
- `src/Moondrop.Hardware/DeviceService.cs`
- `src/Moondrop.Wpf/`

### Physical workflow

- `tests-dotnet/Moondrop.Tests/DawnPro2PhysicalIntegrationTests.cs`
- `tests-dotnet/Moondrop.Tests/PhysicalIntegrationSupport.cs`
- `tests-dotnet/Moondrop.Tests/PhysicalIntegrationSupportTests.cs`
- `tests-dotnet/Moondrop.Tests/PhysicalWatchdogTests.cs`
- `tests-dotnet/Moondrop.Tests/HardwareBehaviorTests.cs`
- `tests-dotnet/Moondrop.PhysicalTests/Moondrop.PhysicalTests.csproj`
- `tests-dotnet/Moondrop.PhysicalTests/Program.cs`
- `tests-dotnet/Moondrop.PhysicalWatchdog/Moondrop.PhysicalWatchdog.csproj`
- `tests-dotnet/Moondrop.PhysicalWatchdog/Program.cs`
- `tests-dotnet/Moondrop.PhysicalWatchdog/WatchdogPolicy.cs`
- `tests-dotnet/default.runsettings`
- `tests-dotnet/physical.runsettings`
- `tests-dotnet/build-isolation/`
- `tests-dotnet/physical-runtime-approval.json`
- `tests-dotnet/PHYSICAL-HARNESS-TDD-EVIDENCE.md`
- `tests-dotnet/OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md`
- `tests-dotnet/tools/Compare-RuntimeTrees.ps1`
- `BUILD-DOTNET.md`
- `MIGRATION.md`

---

## 16. Continuation checklist

Before editing:

- [ ] Read this file fully.
- [ ] Read both independent audit summaries fully.
- [ ] Load TDD, systematic-debugging, and production-change-safety skills.
- [ ] Verify approval JSON is still fail-closed.
- [ ] Verify historical session/frame SHA-256 values.
- [ ] Check no physical runner/watchdog is active.
- [ ] Do not set physical environment variables.

Before candidate generation:

- [x] All audit blocker classes have repo-local RED-to-GREEN tests.
- [ ] Actual MTP-contained topology proven through shared production seam.
- [ ] Exact candidate complete runtime binding exists.
- [ ] Real wrapper/intermediary rejection retained.
- [ ] Full redaction and log-sanitization proven.
- [ ] Reparse/junction and report-race protections proven.
- [ ] Coherent PID identity and manifest-bound hashes proven.
- [ ] Focused, full default-safe, hostile-env, Python, and builds green.
- [ ] Two fresh independent pre-candidate reviewers issue GO.

Before PREPARE:

- [ ] Two sequential candidate builds match exactly.
- [ ] Two independent approval reviewers agree on the identical pair.
- [ ] Approval JSON populated only with that pair.
- [ ] Both retained trees pass strict verification.
- [ ] No source/test/document change after approval.
- [ ] No conflicting runner/watchdog/browser device client.

After PREPARE:

- [ ] Preserve new session and frames.
- [ ] Verify two exact snapshots and zero writes.
- [ ] Stop and report to Mohammed.
- [ ] Do not begin EXECUTE.

---

## 17. One-paragraph handoff prompt

Use this if handing the work to another agent:

> Continue the DAWN PRO2 WPF project at `C:\Users\mohammed\Documents\moondrop gui`. Read `PROJECT-CONTINUATION.md`, both audit summaries under `/home/mohammed/.hermes/cache/delegation/subagent-summary-{0,1}-20260809_163307_*.txt`, and `tests-dotnet/OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md`. Work offline only using strict TDD. Resolve every lineage/security blocker without weakening direct-parent authorization: prove actual MTP test-execution topology through the shared production launcher seam, bind it to the exact complete candidate runtime, use real published wrapper rejection, redact all ownership secrets/token-bearing paths, reject reparse/junction escapes, make reports constrained/collision-safe, capture coherent process identities, separate expected/actual runner/watchdog records, bind hashes to the trusted runtime manifest, and sanitize control characters. Keep approval fail-closed. Do not set physical opt-ins, run PREPARE/EXECUTE/RECOVERY, access HID/DAC, or touch historical artifacts. After software verification, obtain fresh independent GO reviews before candidate generation.

---

## 18. Current stop point

- Round-10 remediation through cycle 50 exists and is locally green.
- Retained candidates A stopped only at offline MTP evidence gates: the first exposed short-name versus definition identity parsing; the second exposed the expected MTP `Deploy_` artifact during deliberate-wrapper rejection. Candidate B was never started.
- All original and fresh-review blocker classes have repo-local RED-to-GREEN evidence but require fresh independent review on the current source.
- Current verification is 210 focused tests, 318 default-safe tests, 318 hostile-environment default-safe tests, 124 Python tests, and warning-free Release builds.
- Approval is fail-closed.
- No physical process is running.
- Historical Prepared artifacts are preserved but invalid for EXECUTE.
- Current next action (authoritative; historical action lines below are obsolete): obtain two fresh independent read-only GO reviews on this exact round-10 source. Only then generate two new sequential isolated candidates; do not run a physical phase.
- Historical session/frame SHA-256 values remain `27DDE295E79E09E314B7C3E15879E420EB56A1FD202897C6EB0441A089DFB7EF` and `B4B0A914E8C1B29110ABA0C8827FD89CA55C2C66B6FEEC988C42F891E55625E8`.
- The next action is two fresh independent read-only pre-candidate reviews on the exact round-5 source—not candidate generation and not physical testing.
