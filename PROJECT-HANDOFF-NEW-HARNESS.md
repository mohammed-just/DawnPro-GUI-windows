# DAWN PRO / DAWN PRO2 — Continuation Handoff for a New Harness

This is the authoritative continuation document for the next agent/harness. Read this file first, in full, before doing any work. It is self-contained and does not depend on the previous Codex conversation, prior subagent messages, or memory.

---

## 1. Repository identity

- **Authoritative working tree (work ONLY here):**
  `C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16`
- **Protected original (DO NOT modify, build, test, or use as the working tree):**
  `C:\Users\mohammed\Documents\moondrop gui`

The protected original must remain untouched. Do not switch to it because an older document calls it "authoritative." Do not retrieve artifacts from it merely to avoid recreating evidence. Establish evidence in the copy.

---

## 2. Project objective

This is the **DAWN PRO / DAWN PRO2 .NET 10 WPF** project. The Python program is the behavioral oracle; the `.NET 10` WPF application is the replacement. The current safety objective is to validate the guarded physical-hardware workflow (process-lineage, topology, runtime integrity, and read-only state capture) without weakening the lineage guard.

Four distinct phases exist and must not be conflated:

- **Offline verification** — builds, tests, fingerprints, manifests, and the offline MTP topology/wrapper smoke. No hardware access.
- **PREPARE** — a single, read-only, physical capture of the DAWN PRO2 raw state (two byte-identical snapshots, 24 HID reads, zero writes). This is the only physical phase authorized so far.
- **EXECUTE** — one authorized temporary PEQ mutation followed by full raw restoration. **NOT authorized/completed in this continuation.**
- **RECOVERY** — restoration of an in-progress session through the watchdog. **NOT authorized/completed in this continuation.**

---

## 3. Current exact source state

Current source fingerprint:

```text
1517740058390FFF866E8225D1B2DF120AA4F4D1D60004BD66E457E7EDE0B305
```

Counts:

- total source inputs: `176`
- presence sentinels: `117`
- content inputs: `59`

Current runtime fingerprint:

```text
46B1DFF291FB603BC4969718D8EE867F474390CB6E59EE3FBE322E8F83C077A6
```

Counts:

- runtime inputs: `532`
- runner: `331`
- watchdog: `192`
- metadata: `9`

These are the values to use for approval. **Older fingerprints are obsolete** because source changed after the TRX visibility-race fix:

- obsolete source `9A01A4AFCF92E7CA9F6F8A66B1882524396344390A9C48F0DB44739E54798E64`
- obsolete runtime `E73A4D393D5A5A681AC435540222DA519BC40DDB9AD16E467633897DD832CB8E`

Do not substitute the obsolete values.

---

## 4. Latest source change (TRX visibility race)

A read-only PREPARE orchestration invocation was attempted, but it failed **BEFORE any HID/device access**, during the offline `cmd.exe` wrapper topology smoke.

Root cause: the nested Microsoft Testing Platform child produced `observed-topology.json`, but its `.trx` file was not yet visible when `PreparedMtpEvidence.RequireExactlyOne` attempted to acquire the strict-existing lease.

The observed failure was:

```text
FileNotFoundException: Offline MTP TRX expected target is missing.
```

No hardware access occurred during that attempt.

The fix: `PreparedMtpEvidence.RequireExactlyOne` now performs a bounded `RequireTrxTargetPresent()` wait before acquiring the existing-file lease.

Fix properties:

- maximum wait: `10` seconds
- polling interval: `50` ms
- reparse containment is revalidated on every poll
- the strict-existing lease is still acquired afterward
- exact-one evidence checks remain unchanged
- the pre/post-parse entry-set comparison remains unchanged
- fail-closed behavior must remain intact

The RED regression is `OfflineTopologyTrxWaitsForLateTrxTargetWithinBound` (in `tests-dotnet\Moondrop.Tests\PhysicalWatchdogTests.cs`). RED reproduced the exact production failure (`FileNotFoundException: Offline MTP TRX expected target is missing.`); GREEN passed after the fix.

---

## 5. Files changed for the latest fix

- `tests-dotnet\Moondrop.PhysicalWatchdog\WatchdogPolicy.cs` — added the bounded `RequireTrxTargetPresent()` wait and its call at the top of `PreparedMtpEvidence.RequireExactlyOne`.
- `tests-dotnet\Moondrop.Tests\PhysicalWatchdogTests.cs` — added the `OfflineTopologyTrxWaitsForLateTrxTargetWithinBound` regression.
- `tests-dotnet\OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md` — recorded the Round-11 Cycle 51 RED/GREEN evidence and post-fix verification.
- `tests-dotnet\physical-runtime-approval.json` — reset to fail-closed because the source fingerprint changed.

No other source files were changed for this fix.

---

## 6. Latest verified offline test/build state

- focused physical-support/watchdog: `211 passed`
- full default-safe .NET suite: `319 passed`
- hostile leaked `MOONDROP_*` default-safe suite: `319 passed`
- Python oracle (`python -m pytest -q`): `124 passed`
- Release solution build: `0 warnings / 0 errors`
- isolated physical-runner build: green (`0 warnings / 0 errors`)
- isolated watchdog build: green (`0 warnings / 0 errors`)
- fresh candidate-only direct + wrapper topology smoke: succeeded
- `cmd.exe` wrapper rejection evidence retained
- PowerShell wrapper rejection evidence retained
- process cleanup verified (no lingering physical/dotnet processes)
- approval file fail-closed
- no physical writes occurred

Known environment issue: PowerShell 7's `PSModulePath` can shadow Windows PowerShell 5.1 `Get-FileHash`. Before relevant `.NET` commands, set the sanitized module path:

```powershell
$env:PSModulePath = 'C:\Users\mohammed\Documents\WindowsPowerShell\Modules;C:\Program Files\WindowsPowerShell\Modules;C:\Windows\system32\WindowsPowerShell\v1.0\Modules'
```

Pinned .NET executable:

```text
C:\Users\mohammed\.dotnet\dotnet.exe
```

Expected SDK: `10.0.302`.

Offline NuGet cache: `C:\Users\mohammed\.nuget\packages`.

The `.venv-win` virtualenv is NOT present in this copy; use the system Python 3.12 (`python` on PATH) for the Python oracle.

---

## 7. Process topology

Experimentally demonstrated intended topology:

```text
Moondrop.PhysicalWatchdog.exe -> Moondrop.PhysicalTests.exe
```

Accepted predicate: `direct-parent`.

The exact MTP test is:

```text
Moondrop.PhysicalTests.OfflineTopologyProbeTests.PublishedRunnerCapturesAuthenticatedParentTopology
```

Real `cmd.exe` and PowerShell wrapper/intermediary cases are rejected with predicate `direct-parent-pid`.

The offline topology path is intended to remain structurally incapable of physical hardware access (clean environment, no `MOONDROP_*` opt-in, and a code path that cannot construct/open a hardware transport).

---

## 8. Current fresh candidate pair

Both generated sequentially from unchanged current source:

- Candidate A: `tests-dotnet\artifacts\physical-runtime\d4444444444444444444444444444444\candidate-a`
- Candidate B: `tests-dotnet\artifacts\physical-runtime\e5555555555555555555555555555555\candidate-b`

Reproducibility (recursive SHA-256):

- staged source: `60/60`, zero differences
- runner tree: `331/331`, zero differences
- watchdog tree: `192/192`, zero differences
- runtime manifest: byte-identical
- overall: zero differences

Runtime-manifest file SHA-256 (verified read-only from both candidates, identical):

```text
00B3A2BB60B124EDD5EC79E09D6F33E9D7E5E06B2C8B9AA3C8AB3527F60CFF6A
```

---

## 9. Independent review status

**Old reviews are invalidated.** The implementation reviews that returned `GO` before the TRX-race fix were against the old source and are now invalid.

The latest source therefore still requires fresh independent review if the next harness chooses to retain that gate.

Important context: the current Codex harness had a **persistent subagent transport failure** — newly spawned agents repeatedly received empty tasks ("I don't see a task"). This is an orchestration/infrastructure problem in this harness, **not a code review failure**. The code was not rejected on its merits. The next harness must NOT assume its own reviewer/subagent mechanism is broken; it may perform independent reviews using whatever reliable independent mechanism it provides.

One review result that must be understood correctly: a topology/testability review reported a `NO-GO` with a "CRITICAL" claim that `SelfRegisteredExtensions` was undefined and `Moondrop.PhysicalTests` could not compile. That finding is a **false positive** — `SelfRegisteredExtensions` is generated by `Microsoft.Testing.Platform.MSBuild` into `tests-dotnet\Moondrop.PhysicalTests\obj\Release\net10.0-windows\SelfRegisteredExtensions.cs`, and the project builds with `0 warnings / 0 errors` and runs the MTP test (proven by the retained topology evidence).

---

## 10. Candidate approval status

Previous candidate approvals applied to the **old** source/runtime pair and were invalidated by the source change.

The new pair:

- Source: `1517740058390FFF866E8225D1B2DF120AA4F4D1D60004BD66E457E7EDE0B305`
- Runtime: `46B1DFF291FB603BC4969718D8EE867F474390CB6E59EE3FBE322E8F83C077A6`

has reproducible Candidate A/B evidence, but it still requires whatever independent approval policy the new harness/project workflow decides to enforce before approval population.

**Do not claim the new pair has two completed independent approvals.** It does not.

---

## 11. Approval file state

`tests-dotnet\physical-runtime-approval.json` is currently intentionally **FAIL-CLOSED**. It contains:

```json
{
  "SchemaVersion": 1,
  "RuntimeIdentifier": "win-x64",
  "SourceSha256": "INDEPENDENT_AUDIT_REQUIRED",
  "SourceInputCount": 0,
  "SourcePresenceSentinelCount": 0,
  "SourceContentInputCount": 0,
  "RuntimeSha256": "INDEPENDENT_AUDIT_REQUIRED",
  "RuntimeInputCount": 0,
  "RunnerTreeInputCount": 0,
  "WatchdogTreeInputCount": 0,
  "MetadataInputCount": 0
}
```

Do not populate it merely as part of creating this handoff.

---

## 12. Physical-device information

Expected DAWN PRO2 identity:

- model: `DAWN PRO2`
- VID: `0x35D8`
- PID: `0x011D`
- serial: `35D8011D251117`
- firmware (exact ordinal string): `"1.5"`
- expected raw `active_eq`: `9`

Expected read-only PREPARE evidence:

- two complete snapshots
- snapshots byte-identical
- `24` HID reads
- `0` HID writes
- `0` EQ writes
- `0` gain writes
- `0` active-EQ writes
- `0` flash-save operations

---

## 13. Physical activity that actually occurred

A PREPARE orchestration invocation occurred after a previous approval process, but it failed during an offline topology-wrapper precondition **BEFORE HID/device access**.

Therefore, from that attempt:

- no DAC was opened
- no HID reads occurred
- no HID writes occurred
- no EQ/gain/active-EQ mutation occurred
- no flash-save occurred

Do not claim a successful PREPARE occurred. The attempted orchestration invocation is disclosed here and is not hidden.

---

## 14. Historical physical evidence

No historical Prepared-session or raw-frame artifacts are present inside this copy (`tests-dotnet\artifacts\hardware-snapshots` and `tests-dotnet\artifacts\hardware-results` do not exist here).

Do not retrieve historical artifacts from the protected original. Do not reuse an old Prepared session for EXECUTE.

---

## 15. Safety state

- EXECUTE has NOT been started in this continuation.
- RECOVERY has NOT been started.
- No physical write occurred.
- No EQ write occurred.
- No gain write occurred.
- No active-EQ write occurred.
- No flash-save occurred.
- The protected original repository was not modified.
- Approval is currently fail-closed.

---

## 16. Recommended continuation for a NEW harness

1. Read this handoff and inspect current repository state.
2. Recompute the current source fingerprint and require `1517740058390FFF866E8225D1B2DF120AA4F4D1D60004BD66E457E7EDE0B305` unless source has legitimately changed.
3. Verify current runtime/candidates.
4. Inspect the TRX-race fix and its regression.
5. Run the offline verification appropriate to that harness.
6. Perform whatever independent code/security review process the new harness supports.
7. If source changes, invalidate fingerprints/candidates and rebuild.
8. Establish a reproducible candidate pair.
9. Independently approve/finalize the exact source/runtime pair according to the project safety policy.
10. Populate `physical-runtime-approval.json` only for the verified pair.
11. Verify approval against the retained candidates.
12. Perform the final read-only physical preflight.
13. Perform a read-only PREPARE only when all intended gates are satisfied.
14. Inspect the resulting evidence.
15. Do NOT proceed to EXECUTE until the user separately authorizes the execution phase.

The new harness may simplify or redesign the orchestration/review process if it preserves or improves the actual safety properties. It is NOT required to reproduce Codex-specific subagent machinery.

---

## 17. Key safety invariants that must survive a harness migration

- physical operations fail closed
- default tests cannot accidentally become physical from leaked environment variables
- strict process lineage
- authenticated direct parent/topology
- no generic shell/dotnet/testhost allowlisting
- coherent process identity
- complete runtime-manifest binding
- manifest-derived hashes (not mutable heartbeat self-assertions)
- reparse/junction protection
- safe canonical artifact publication
- diagnostic secret/token redaction
- control-character safety
- offline topology tests cannot access hardware
- exact device identity checks
- read-only PREPARE does not write
- EXECUTE requires a separate explicit decision

---

## 18. Known remaining engineering questions

- whether the new TRX wait should remain 10s/50ms or be made configurable while preserving bounded fail-closed behavior
- whether additional timing/race regressions are worthwhile
- whether the approval/review workflow should be simplified in the new harness
- whether PREPARE orchestration should distinguish an offline precondition failure from actual physical-device access more explicitly
- whether physical attempt accounting should begin only once the HID/device boundary is crossed

Do not solve these by guessing; record them for the next harness.

---

## 19. Commands / entry points

Pinned .NET: `C:\Users\mohammed\.dotnet\dotnet.exe`.
Working directory for all commands: `C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16`.

Sanitized module path (set before any `.NET` command):

```powershell
$env:PSModulePath = 'C:\Users\mohammed\Documents\WindowsPowerShell\Modules;C:\Program Files\WindowsPowerShell\Modules;C:\Windows\system32\WindowsPowerShell\v1.0\Modules'
```

Source fingerprint:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' 'tests-dotnet\Moondrop.PhysicalWatchdog\bin\Release\net10.0-windows\Moondrop.PhysicalWatchdog.dll' --print-source-fingerprint --repo 'C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16'
```

Full default-safe suite:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test 'DawnPro.Wpf.slnx' --configuration Release --settings 'tests-dotnet\default.runsettings' --no-restore --nologo
```

Focused physical-support/watchdog suite:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test 'tests-dotnet\Moondrop.Tests\Moondrop.Tests.csproj' --configuration Release --settings 'tests-dotnet\default.runsettings' --filter 'FullyQualifiedName~Moondrop.Tests.PhysicalIntegrationSupportTests|FullyQualifiedName~Moondrop.Tests.PhysicalWatchdogTests' --no-restore --nologo
```

Hostile leaked physical opt-in default-safe suite (physical categories must still be excluded):

```powershell
$env:MOONDROP_RUN_PHYSICAL_TESTS='1'
$env:MOONDROP_RUN_PHYSICAL_RECOVERY='1'
$env:MOONDROP_PREPARE_PHYSICAL_TESTS='1'
$env:MOONDROP_PHYSICAL_SESSION_PATH='C:\fake\session.json'
$env:MOONDROP_PHYSICAL_CONFIRMATION='hostile'
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test 'DawnPro.Wpf.slnx' --configuration Release --settings 'tests-dotnet\default.runsettings' --no-restore --nologo
# then remove those MOONDROP_* variables
```

Python oracle:

```powershell
python -m pytest -q
```

Release solution build:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' build 'DawnPro.Wpf.slnx' --configuration Release --no-restore --nologo
```

Isolated runner/watchdog restore+build (locked, offline):

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' restore 'tests-dotnet\Moondrop.PhysicalTests\Moondrop.PhysicalTests.csproj' --locked-mode --nologo
& 'C:\Users\mohammed\.dotnet\dotnet.exe' restore 'tests-dotnet\Moondrop.PhysicalWatchdog\Moondrop.PhysicalWatchdog.csproj' --locked-mode --nologo
& 'C:\Users\mohammed\.dotnet\dotnet.exe' build 'tests-dotnet\Moondrop.PhysicalTests\Moondrop.PhysicalTests.csproj' --configuration Release --no-restore --nologo
& 'C:\Users\mohammed\.dotnet\dotnet.exe' build 'tests-dotnet\Moondrop.PhysicalWatchdog\Moondrop.PhysicalWatchdog.csproj' --configuration Release --no-restore --nologo
```

Candidate-only build + full direct/wrapper topology smoke (this is also the candidate generation command; run candidates sequentially, not concurrently):

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' 'tests-dotnet\Moondrop.PhysicalWatchdog\bin\Release\net10.0-windows\Moondrop.PhysicalWatchdog.dll' --build-runtime-smoke --repo 'C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16' --session-id <32-hex> --generation <name>
```

Approval verification against a staged candidate:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' 'tests-dotnet\Moondrop.PhysicalWatchdog\bin\Release\net10.0-windows\Moondrop.PhysicalWatchdog.dll' --verify-runtime-approval --repo 'C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16' --source-root <staged-source> --physical-output <physical-tests-dir> --watchdog-output <watchdog-dir>
```

**PHYSICAL-ACCESS COMMAND (PREPARE) — read-only, single-attempt, only after every gate passes.** This opens the DAWN PRO2 for read-only HID reads:

```powershell
$env:MOONDROP_PREPARE_PHYSICAL_TESTS='1'
& '<candidate>\physical-tests\Moondrop.PhysicalTests.exe' --settings 'C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16\tests-dotnet\physical.runsettings' --filter 'FullyQualifiedName=Moondrop.Tests.DawnPro2PhysicalIntegrationTests.PrepareDawnPro2PhysicalSessionReadOnlyAsync'
Remove-Item Env:\MOONDROP_PREPARE_PHYSICAL_TESTS
```

EXECUTE and RECOVERY are NOT authorized and must not be run by the next harness without separate explicit user authorization.
