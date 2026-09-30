# Offline physical-harness lineage diagnostics TDD evidence

Date: 2026-08-09

## Safety boundary

This change and every verification command were offline-only. No `MOONDROP_*` physical opt-in variable was assigned; no HID, USB, DAC, or device enumeration/open/write occurred; no PREPARE, EXECUTE, or RECOVERY phase ran; no physical test category ran; no hardware state changed; and no approval candidate was built or approved. The historical session and raw-frame JSON files were not modified.

The topology probe is structurally separate from physical test execution:

- `Moondrop.PhysicalWatchdog.exe` accepts only the exact five-argument `--offline-topology-probe --physical-apphost <path> --report <path>` form.
- It starts only an executable canonically named `Moondrop.PhysicalTests.exe` with exactly `--offline-topology-probe-child --report <path>`.
- The child environment is cleared and rebuilt with only `SystemRoot`, `WINDIR`, `TEMP`, and `TMP`; no `MOONDROP_*` value can reach it.
- `Moondrop.PhysicalTests.Program.Main` checks the exact offline child argument shape before `TestApplication.CreateBuilderAsync`. The offline branch therefore cannot discover tests, select a physical category, construct a hardware transport, or reach HID code.

## Experimentally observed production-apphost topology

The final experiment used newly published self-contained `win-x64` apphosts in the dedicated, non-approval path `tests-dotnet/artifacts/offline-topology-probe/20260809-real-apphosts-final2`. This was an ordinary direct publish, not `--build-runtime-smoke` and not an approval candidate.

Observed report:

```text
SafetyMode: OFFLINE_ONLY_NO_TEST_PLATFORM_NO_HARDWARE
Watchdog PID: 19676
Watchdog parent PID: 18276
Watchdog start UTC: 2026-08-09T13:17:34.3477360+00:00
Watchdog path: C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\offline-topology-probe\20260809-real-apphosts-final2\watchdog\Moondrop.PhysicalWatchdog.exe
Watchdog apphost SHA-256: 8B56C4DDF9BEA91B9227F588EB7ACE1A860F42A1EC8ED635FFB8CE58431F7503

Physical runner PID: 9856
Physical runner parent PID: 19676
Physical runner start UTC: 2026-08-09T13:17:34.8721622+00:00
Physical runner path: C:\Users\mohammed\Documents\moondrop gui\tests-dotnet\artifacts\offline-topology-probe\20260809-real-apphosts-final2\physical-tests\Moondrop.PhysicalTests.exe
Physical runner apphost SHA-256: 2CD5A16D11BA2CEACF00CB4196DEC7AC6142F9A9D3F9654428D4D81636BB4714
```

The proven production topology is a strict direct edge:

```text
Moondrop.PhysicalWatchdog.exe (PID 19676)
└── Moondrop.PhysicalTests.exe (PID 9856, parent PID 19676)
```

There was no `dotnet.exe`, `testhost.exe`, shell, wrapper, or other intermediary. Lineage authorization therefore remains strict direct-parent authorization; no generic intermediary allowlist was added. A direct invocation of the same published physical apphost through the PowerShell wrapper exited `1` and published no report.

## RED evidence preserved

Production implementation was not added before the first failing test. The tracer-bullet RED command was:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test 'tests-dotnet\Moondrop.Tests\Moondrop.Tests.csproj' --configuration Release --settings 'tests-dotnet\default.runsettings' --filter 'FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.UnexpectedIntermediaryRejectionReportsTheCompleteRedactedLineage' --no-restore --nologo
```

Its exact failure was:

```text
PhysicalIntegrationSupportTests.cs(314,54): error CS0117: 'PhysicalWatchdogProcessGate' does not contain a definition for 'Evaluate'
```

Subsequent vertical RED slices preserved these failures before their minimal GREEN implementation:

| Behavior | RED evidence |
|---|---|
| Offline-only child start contract | `CS0103`: `PhysicalOfflineTopologyProbe` did not exist. |
| Direct topology observation | `CS0246` for `PhysicalOfflineTopologyObservation` and `PhysicalProbeProcessIdentity`; `CS0117` for the validator. |
| Exact offline child argument shape | `CS0117`: `TryGetChildReportPath` did not exist. |
| Atomic observation report | `CS0117`: `WriteObservationAsync` and `ReadObservation` did not exist. |
| Watchdog apphost hash binding | `CS1739`: heartbeat had no `OwnerExecutableSha256` parameter. |
| Reused PID/start time | Rejected only as generic `predicate=authorization-contract`, not `watchdog-start-time`. |
| Wrong watchdog path | Rejected only as generic `predicate=authorization-contract`, not `watchdog-executable-path`. |
| Unrelated PID identity | Rejected only as generic `predicate=authorization-contract`, not `watchdog-process-id`. |
| Runner wrapper | `testhost.exe` was incorrectly authorized (`Assert.IsFalse` failed). |
| Missing heartbeat | Rejected only as generic `predicate=authorization-contract`, not `heartbeat-file-exists`. |
| Malformed heartbeat | Misreported as `process-identity-readable`, not `heartbeat-json`. |
| Malformed binding | Rejected only as generic `predicate=authorization-contract`, not `session-id-shape`. |
| Depth bound | The eight nodes were printed but `chain.truncated=true; chain.limit=8` was absent. |
| Untrusted heartbeat path | The untrusted file was parsed and misreported as `heartbeat-json`, not rejected first as `heartbeat-canonical-path`. |
| Propagated pre-HID diagnostic | Write-capable phase errors omitted `predicate=authorization-present`. |
| Wrapper-parent apphost exit | `CS0117`: `RunPhysicalChildEntryPointAsync` did not exist. |

## Implemented diagnostics and regression coverage

EXECUTE/RECOVERY authorization now rejects before transport open and identifies the exact predicate. Diagnostics include the expected published apphost identity, actual runner PID/parent PID/start time, canonical executable paths, SHA-256 where the file exists, expected-versus-actual values, and a complete relevant parent chain bounded to eight nodes. Cycles are explicitly marked, and a longer chain is explicitly marked truncated. Ownership tokens, one-run tokens, heartbeat contents, and unsafe parse details are redacted.

Covered cases include:

- legitimate direct parent;
- actual self-contained apphosts end to end;
- unexpected intermediary and direct wrapper launch;
- `testhost.exe`/`dotnet.exe`-style runner wrappers;
- wrong watchdog path and apphost name/root membership;
- watchdog apphost SHA-256 drift;
- stale/reused PID start time and unrelated returned PID identity;
- parent-chain cycle and eight-node depth bound;
- missing, malformed, and untrusted-path heartbeat;
- malformed/missing authorization and malformed session binding;
- heartbeat owner mismatch and one-run-token mismatch redaction;
- default settings excluding all three physical categories even when opt-in values are supplied as inherited data.

## GREEN commands and results

All commands used `C:\Users\mohammed\.dotnet\dotnet.exe` version `10.0.302`.

```text
Focused physical-support tests:
dotnet test tests-dotnet\Moondrop.Tests\Moondrop.Tests.csproj --configuration Release --settings tests-dotnet\default.runsettings --filter "FullyQualifiedName~Moondrop.Tests.PhysicalIntegrationSupportTests|FullyQualifiedName~Moondrop.Tests.PhysicalWatchdogTests" --no-restore --nologo
Result: 172 passed, 0 failed, 0 skipped

Release solution build:
dotnet build DawnPro.Wpf.slnx --configuration Release --no-restore --nologo
Result: 0 warnings, 0 errors

Full default-safe suite:
dotnet test DawnPro.Wpf.slnx --configuration Release --settings tests-dotnet\default.runsettings --no-restore --nologo
Result: 280 passed, 0 failed, 0 skipped

Physical runner build:
dotnet build tests-dotnet\Moondrop.PhysicalTests\Moondrop.PhysicalTests.csproj --configuration Release --no-restore --nologo
Result: 0 warnings, 0 errors

Watchdog build:
dotnet build tests-dotnet\Moondrop.PhysicalWatchdog\Moondrop.PhysicalWatchdog.csproj --configuration Release --no-restore --nologo
Result: 0 warnings, 0 errors

Final real-apphost offline probe:
Moondrop.PhysicalWatchdog.exe --offline-topology-probe --physical-apphost <published Moondrop.PhysicalTests.exe> --report <offline report>
Result: exit 0; direct parent edge proven; report written

Unexpected wrapper regression:
Moondrop.PhysicalTests.exe --offline-topology-probe-child --report <offline rejection report>
Result: exit 1; report absent

Published physical apphost startup smoke:
Moondrop.PhysicalTests.exe --help
Result: exit 0
```

No `--build-runtime-smoke`, `--verify-runtime-approval`, PREPARE, EXECUTE, RECOVERY, physical category, approval fill, or hardware command was run.

## Changed files

- `tests-dotnet/Moondrop.Tests/PhysicalIntegrationSupport.cs`
- `tests-dotnet/Moondrop.Tests/PhysicalIntegrationSupportTests.cs`
- `tests-dotnet/Moondrop.Tests/PhysicalWatchdogTests.cs`
- `tests-dotnet/Moondrop.Tests/DawnPro2PhysicalIntegrationTests.cs`
- `tests-dotnet/Moondrop.PhysicalWatchdog/WatchdogPolicy.cs`
- `tests-dotnet/Moondrop.PhysicalWatchdog/Program.cs`
- `tests-dotnet/Moondrop.PhysicalTests/Moondrop.PhysicalTests.csproj`
- `tests-dotnet/Moondrop.PhysicalTests/Program.cs`
- `tests-dotnet/physical-runtime-approval.json`
- `tests-dotnet/OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md`

Generated offline probe publishes also exist under `tests-dotnet/artifacts/offline-topology-probe/`: the retained final evidence is `20260809-real-apphosts-final2`; the superseded `20260809-real-apphosts` and `20260809-real-apphosts-final` directories remain because the managed command policy denied their explicitly scoped recursive cleanup.

The approval manifest is intentionally left fail-closed with `REQUIRES-INDEPENDENT-AUDIT` for both hashes and zero for every count. No commit was created.

## Second-pass strict TDD remediation (2026-08-09)

### Cycle 1 RED - candidate-bound exact topology smoke

Command:

```powershell
& 'C:\Users\mohammed\.dotnet\dotnet.exe' test 'tests-dotnet\Moondrop.Tests\Moondrop.Tests.csproj' --configuration Release --settings 'tests-dotnet\default.runsettings' --filter 'FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.PhysicalRuntimeBuilderProtectsValidatedStageThroughoutBuildSmokeAndManifestCapture' --no-restore --nologo
```

Result: RED, exit 1. Production had no candidate topology-smoke contract. Compiler failures were `CS1061` for `OfflineTopologySmokeCount`, `OfflineTopologyRuntimeManifestSha256`, `OfflineTopologyPhysicalApphostPath`, and `OfflineTopologyWatchdogApphostPath` on `SimulatedPhysicalBuildExecutor`.

GREEN: the same command exited 0 with `Passed: 1, Failed: 0`. Candidate generation now calls one offline topology smoke only after both publish trees and the complete runtime manifest exist, passing the exact two apphosts and manifest aggregate while staged source protection is still held.

### Cycle 2 RED - real MTP entry and exact offline test contract

Command used the Release/default-safe project with the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.OfflineTopologyProbeCanOnlyLaunchOneExactMstestThroughTheProductionLauncherContract`.

Result: RED, exit 1. Compiler failures were `CS0117`: `PhysicalOfflineTopologyProbe` had no `CreateMtpRunnerStartInfo` and no `ExactMtpTestName`; the only available path was still the pre-MTP child branch.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The published runner now receives an exact MTP filter for `Moondrop.PhysicalTests.OfflineTopologyProbeTests.PublishedRunnerCapturesAuthenticatedParentTopology`; `Program.Main` always builds and runs MTP, and both topology smoke and production `RunSupervisedAsync` start children through `PhysicalProcessLauncher.Start`.

### Cycle 3 RED - canonical collision-safe offline reports

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.OfflineTopologyReportIsRootBoundCreateNewAndHasOneConcurrentPublisher`.

Result: RED, exit 1. Four `CS1501` failures proved that report read/write accepted no dedicated-root contract; production still used predictable `.tmp` and overwrite publication.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Reports are now restricted to `<candidate-runtime>/offline-topology/observed-topology.json`, reject an outside path and any pre-existing target, use a GUID-named `FileMode.CreateNew` temporary, and publish with a no-overwrite atomic move; the concurrent regression observed exactly one successful publisher.

### Cycle 4 RED - dry-run secret and token-path redaction

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.WatchdogDryRunDescriptionNeverLeaksSecretsOrTokenBearingPaths`.

Result: RED, exit 1. The assertion reported `Dry-run leaked raw secret owner-RAW-SECRET`; ownership also appeared in environment, result, and heartbeat paths.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Confirmation, ownership, and one-run values are redacted everywhere, session paths are never emitted, secret-bearing environment values are redacted, and token substrings are replaced in arguments, result/heartbeat paths, apphost paths, and owner paths.

### Cycle 5 RED - reparse-safe trusted paths

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.TrustedPhysicalPathsRejectInjectedReparseAncestorsAndFinalTargets`.

Result: RED, exit 1. `CS0246` proved there was no trusted-path inspector or reparse-aware containment contract; only lexical membership existed.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. A shared fail-closed trusted-path policy now inspects every existing ancestor and final target for `FileAttributes.ReparsePoint`; injected ancestor and final-target redirects are rejected. The policy is applied to repository/runtime roots, runner/watchdog apphosts, session artifacts, heartbeat root/directory/file, candidate roots, and offline report root/path.

### Cycle 6 RED - coherent PID identity snapshots

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.CoherentProcessIdentityRejectsPidReuseAndMidReadDrift`.

Result: RED, exit 1. `CS0246` proved no coherent snapshot reader existed; production mixed `Process.StartTime` with a separate WMI PID/path/parent query.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. PID, parent PID, creation time, and executable path now come from one WMI row, are captured twice, and must be byte-for-byte coherent for the requested PID; disappearance, PID reuse, remapping, or mid-read drift fails closed.

### Cycle 7 RED - independently manifest-bound apphost hashes

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.RuntimeManifestNotHeartbeatBindsBothExactApphostHashes`.

Result: RED, exit 1. Six `CS0103` failures proved `RuntimeApphostManifestBinding` did not exist; the watchdog hash expectation still came from mutable heartbeat self-assertion and the runner apphost was not manifest-hash checked at lineage authorization.

GREEN: the manifest regression plus direct-parent and modified-watchdog integrations exited 0 with `Passed: 3, Failed: 0`. Candidate builds persist the exact complete manifest outside the two hashed publish trees; its recomputed aggregate must match the session-approved aggregate, and its exact runner/watchdog entries must match both apphost bytes. A forged heartbeat watchdog hash is ignored as an expected source; wrong aggregate, wrong entry, and modified apphost all fail closed with separate runner/watchdog expected-vs-actual fields.

### Cycle 8 RED - diagnostic control escaping and secret redaction

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.DiagnosticsEscapeControlsAndCannotInjectLinesOrLeakExplicitSecrets`.

Result: RED, exit 1. `CS0103` proved there was no central `DiagnosticText` sanitizer; path-derived CR/LF/control values could create forged diagnostic lines.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Central diagnostic sanitization escapes every Unicode control as `\\uXXXX`, redacts explicit secrets and ownership-token shapes, and is applied to lineage rejections, topology child output/error, candidate-smoke output/error, and top-level watchdog exceptions. The injected CR/LF path could not create a new line.

### Cycle 9 RED - actual deliberate wrapper/intermediary regressions

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.DeliberateWrapperRegressionsLaunchTheExactMtpCommandWithoutAllowlistingWrappers`.

Result: RED, exit 1. `CS0246`/`CS0117` proved there were no deliberate bounded wrapper shapes and no wrapper launcher for the actual exact MTP command; the old test could pass merely because its ordinary unit-test host had the wrong name.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Only two deliberate negative shapes exist (`cmd.exe` and Windows PowerShell); both wrap the exact published apphost/MTP command without becoming authorized intermediaries. Candidate smoke now launches both actual wrappers and requires a retained MTP-produced structured rejection with `predicate=direct-parent-pid`, the exact runner-to-wrapper edge, and the wrapper executable identity.

### Cycle 10 RED - role-separated lineage diagnostics

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.WrongWatchdogExecutablePathReportsCanonicalExpectedAndActualIdentity`.

Result: RED, exit 1. The failure showed the audit defect verbatim: `actual.pid=300` (runner) was combined with `actual.startedAtUtc`/`actual.path` for the watchdog. No `expected.runner`, `actual.runner`, `expected.watchdog`, or `actual.watchdog` identity fields existed.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Direct-edge, watchdog PID, watchdog start-time, and watchdog path failures now emit independent `expected.runner`, `actual.runner`, `expected.watchdog`, and `actual.watchdog` PID/parent/start/path/hash fields; the failed predicate no longer combines a runner PID with watchdog path/start data.

### First real candidate attempt - blocked before publish

The first `--build-runtime-smoke` attempt used session `a8092026feedface0ff11eead00dcafe`, generation `offline-lineage-remediation`, and exited 1 before publishing either apphost. Locked restore attempted `https://api.nuget.org/v3/index.json` and failed `NU1301` because network is unavailable. Therefore MTP topology smoke did not run in this attempt. No physical opt-in or phase ran.

### Cycle 11 RED - genuinely offline locked restore

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.IsolatedPhysicalRestoreIsNetworkFreeAndUsesAnExistingLocalPackageCache`.

Result: RED, exit 1. The assertion proved `physical.NuGet.Config` still contained a remote `https://` source; the isolated package cache was empty and the build was not actually offline-capable.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The protected NuGet config now has no package source URI, `NuGetAudit=false` prevents vulnerability-network lookup, and locked restore uses the existing local package cache derived from the explicitly supplied `.dotnet` installation. Missing cache/packages still fail closed.

The next fresh candidate (`b8092026feedface0ff11eead00dcafe`) restored and published the runner, but the parent watchdog remained CPU-bound while hashing every unrelated artifact as part of startup-smoke mutation detection. It was terminated by exact verified PID after 203.9 seconds. A source-fingerprint regression then disproved the initial suspicion of recursive source staging: generated artifact/candidate source files were already excluded and the regression passed without production change.

### Cycle 12 RED - never open unrelated historical artifact contents

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.StartupSmokeNeverOpensUnrelatedHistoricalArtifactContents`.

Result: RED, exit 1. With a synthetic historical session file held `FileShare.None`, `RunStartupSmokeAsync` threw `IOException` from `File.ReadAllBytes` inside `CaptureSmokeTree`, proving it opened unrelated historical artifact contents.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Startup smoke now hashes only its own exact publish output before/after and verifies its dedicated temporary directory stays empty; it no longer enumerates or opens unrelated artifact content. The synthetic historical file remained exclusively locked throughout the successful smoke.

The next candidate (`c8092026feedface0ff11eead00dcafe`) completed both publishes and the real direct MTP probe. Its retained report proved the direct watchdog to runner edge, but the deliberate `cmd.exe` wrapper command exited before entering MTP and produced no rejection report, so the candidate correctly remained ineligible.

### Cycle 13 RED - wrapper process invocation and exit propagation

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.DeliberateWrapperShapesActuallyInvokeTheExactNamedExecutableAndPropagateItsExit`.

Result: RED, exit 1. The real Command Prompt wrapper returned 1 instead of the exact named child executable's exit 7, reproducing the candidate failure at process level.

GREEN: after correcting both wrapper command constructions, the same process-level regression exited 0 with `Passed: 1, Failed: 0`; Command Prompt and Windows PowerShell each invoked the exact named child and propagated exit 7.

### Cycle 14 RED - root pytest discovery excludes generated physical artifacts

On 2026-08-12, the exact documented Python-oracle command failed before executing tests because pytest recursively collected the intentionally inaccessible generated directory `tests-dotnet/artifacts/pytest-offline-20260809-final`.

RED result: `PermissionError: [WinError 5] Access is denied` during collection, exit 1.

GREEN: `pytest.ini` now scopes discovery to the authoritative `tests/` directory. The exact command `.\.venv-win\Scripts\python.exe -m pytest -q` exits 0 with `124 passed`; no protected artifact or ACL was modified.

### 2026-08-12 continuation verification

- Cycle 13 exact regression: **1 passed**
- Focused physical-support/watchdog suite: **180 passed**
- Full default-safe suite: **288 passed**
- Full default-safe suite with leaked PREPARE/EXECUTE/RECOVERY opt-ins supplied only to the test host: **288 passed**
- Python oracle: **124 passed**
- Release solution build: **0 warnings / 0 errors**
- Isolated physical runner Release build: **0 warnings / 0 errors**
- Isolated watchdog Release build: **0 warnings / 0 errors**

The approval manifest remained fail-closed. No candidate build, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, or device access occurred. The next gate is two fresh independent read-only pre-candidate reviews of the current source.

## Third-pass strict TDD remediation (2026-08-12)

Two fresh read-only pre-candidate reviewers returned NO-GO. Both independently found that the offline MTP path still constructed a probe-only `ProcessStartInfo`; sharing only a one-line `Process.Start` wrapper did not exercise the production launch-construction seam. They also identified remaining coherent-identity, reparse/TOCTOU, environment/cleanup, exact-one-test, post-smoke complete-tree, and diagnostic-redaction gaps. Candidate generation and every physical phase remained blocked.

### Cycle 15 RED - one production/offline launch-plan materializer

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.OfflineTopologyProbeCanOnlyLaunchOneExactMstestThroughTheProductionLauncherContract`.

Result: RED, exit 1. Two `CS0246` failures proved that `PhysicalProcessLaunchPlan` did not exist; production and the offline probe each still returned separately constructed `ProcessStartInfo` instances.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Production EXECUTE/RECOVERY, direct MTP, deliberate-wrapper MTP, and the candidate watchdog now submit the same immutable `PhysicalProcessLaunchPlan` contract to `PhysicalProcessLauncher.MaterializeAndStart`, the sole shared materialization seam for those paths. Three adjacent production-environment and real-wrapper regressions passed `3/3` after the test wrapper plan explicitly supplied the same four Windows path essentials required by the real MTP plan.

### Cycle 16 RED - coherent identities inside actual MTP topology evidence

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.OfflineTopologyIdentityCaptureRejectsPidReuseAndMidReadDrift`.

Result: RED, exit 1. `CS0246` proved there was no offline-topology coherent snapshot-reader contract; the actual MTP test combined process ID, parent, start time, executable path, and apphost hash from one unrevalidated observation.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Both the published runner and its actual parent are now captured twice through `CoherentPhysicalProbeProcessIdentityProvider`; requested PID mismatch, disappearance, reuse, path/hash change, parent drift, or start-time drift fails closed before topology evidence is accepted or published as success.

### Cycle 17 RED - coherent watchdog termination ownership

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.WatchdogTerminationIdentityCaptureRejectsPidReuseAndMidReadDrift`.

Result: RED, exit 1. `CS0246` proved the timeout/termination path had no coherent observed-process snapshot contract; it previously combined `Process.StartTime` with a separate WMI command-line lookup.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Runner ownership is now established and later revalidated through two identical WMI rows containing PID, `CreationDate`, and command line. Disappearance, PID reuse, remapping, start-time drift, or command-line drift fails closed before the watchdog may terminate a process tree.

### Cycle 18 RED - scrubbed candidate-topology watchdog environment

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.CandidateTopologyWatchdogLaunchDropsEveryHostileAmbientVariable`.

Result: RED, exit 1. `CS0117` proved the candidate topology watchdog had no explicit launch-plan factory; it inherited the parent environment, including possible runtime hooks, profilers, build/test variables, arbitrary values, and physical opt-ins.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The outer candidate watchdog plan now keeps only canonical `SystemRoot`, `WINDIR`, `TEMP`, and `TMP`, requires matching Windows identities and safe values, and drops ambient `DOTNET_*`, profiler, build/test, arbitrary, secret, and every `MOONDROP_*` variable before the published watchdog starts.

### Cycle 19 RED - candidate topology cancellation cleanup

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.CandidateTopologyCancellationKillsAndAwaitsTheEntireStartedProcessTree`.

Result: RED, exit 1. `CS0117` proved the shared launcher had no supervised run-to-exit contract; cancellation in candidate topology smoke could unwind while its watchdog/runner tree remained alive.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Candidate topology smoke now runs through `PhysicalProcessLauncher.RunToExitAsync`; every wait/read/cancellation failure kills the entire launched process tree, awaits confirmed exit, drains output best-effort, and only then rethrows so source protection cannot release around a live child.

### Cycle 20 RED - complete runtime recapture after MTP smoke

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.CandidateBuilderRejectsCompleteRuntimeTreeMutationDuringMtpTopologySmoke`.

Result: RED, exit 1. The injected topology smoke changed `Moondrop.PhysicalTests.dll`, but candidate construction returned successfully because its post-smoke checks covered only staged source and the two apphost hashes.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Candidate construction now recaptures both complete self-contained publish trees plus all metadata immediately after direct and wrapper MTP execution while source protection remains held, and rejects any aggregate drift before returning a candidate or checking approval.

### Cycle 21 RED - positive exactly-one MTP execution evidence

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.OfflineTopologyTrxRequiresExactlyOneExpectedExecutedMtpTest`.

Result: RED, exit 1. Two `CS0117` failures proved there was no retained TRX verification contract; an exact filter and one JSON report could not prove that no additional physical test was selected or reported inconclusive.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Direct and deliberate-wrapper MTP plans now require at least one test and retain a dedicated TRX. Acceptance requires TRX counters `total=1` and `executed=1`, exactly one `UnitTestResult`, the exact offline topology FQN, and the expected direct `Passed` or wrapper `Failed` outcome. A synthetic second physical result is rejected even when marked not executed.

### Cycle 22 RED - top-level secret/path and control-safe failures

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.TopLevelWatchdogFailureRedactsSecretArgumentsAndEscapesControls`.

Result: RED, exit 1. `CS0117` proved top-level watchdog exception handling had no argument-aware sanitizer; confirmation values and token-bearing session/report paths could survive the ownership-token regex and reach stderr.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Top-level exception reporting now redacts values supplied through `--confirmation`, `--session`, and `--report`, applies ownership-token pattern redaction, and escapes CR, LF, NUL, and every other Unicode control before writing stderr.

### Cycle 23 RED - physical phase diagnostics redact durable secrets

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.PhysicalPhaseDiagnosticsRedactSessionSecretsAndEscapeControls`.

Result: RED, exit 1. `CS0103` proved physical phase journaling had no centralized safe diagnostic contract; raw exception/detail text was written to test output and durable result records.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Execute journaling now carries the one-run token, confirmation, and session path as explicit secrets; recovery carries its one-run token and session path. Phase names, statuses, exception messages, console lines, and persisted phase details all pass through control escaping plus explicit and ownership-pattern redaction.

### Cycle 24 RED - manifest-authoritative expected role diagnostics

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.DirectParentDiagnosticUsesManifestAuthoritativeExpectedRoleIdentities`.

Result: RED, exit 1. After the live runner bytes were replaced, the direct-parent rejection reported the replacement hash as both `expected.runner.sha256` and `actual.runner.sha256`; the expected role identity was still derived from the observed process.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The runtime manifest is now parsed and aggregate-validated before direct-edge decisions. Early lineage failures receive canonical expected runner/watchdog paths and hashes from the independently bound manifest while actual fields come only from live process/file observations.

### Cycle 25 RED - exact malformed-manifest failure classification

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalIntegrationSupportTests.MalformedRuntimeManifestReportsAnExactManifestPredicate`.

Result: the new regression was first added against the corrected early manifest-read boundary and passed only after that boundary existed. A manifest with `Files=null` is now rejected as `predicate=runtime-manifest-schema`, never relabeled as `process-identity-readable`; exception content and durable secrets remain redacted. This cycle extends the Cycle 24 production change rather than claiming an independent pre-change compiler failure.

### Cycle 26 RED - dangling reparse entries are inspected

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.TrustedPhysicalPathsInspectDanglingReparseEntriesInsteadOfSkippingThem`.

Result: RED, exit 1. The injected inspector marked a path as a dangling reparse entry (`Exists=false`, attributes=`ReparsePoint`), and the policy skipped it without inspecting attributes.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. Trusted-path traversal now calls the attribute lookup directly and skips only explicit file/path-not-found results; dangling reparse entries that remain addressable through reparse-aware inspection fail closed.

### Cycle 27 RED - validate complete runner launch before heartbeat publication

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.RunnerLaunchPreparationValidatesTheEntireLaunchBeforePublishingHeartbeat`.

Result: RED, exit 1. `CS0103` proved no ordered launch-preparation contract existed; production published `RunnerStarting` before validating runner identity, paths, arguments, exact environment keys, and Windows essentials.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. `PhysicalRunnerLaunchPreparation.Prepare` constructs and fully validates the immutable shared launch plan before invoking the heartbeat publisher. An injected startup hook rejects the launch without publishing a heartbeat.

### Cycle 28 RED - validate build plan before creating the lock path

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.PhysicalRuntimeBuilderValidatesThePlanBeforeCreatingTheBuildLock`.

Result: RED, exit 1. An invalid session identity correctly threw, but the builder had already created `tests-dotnet/artifacts/physical-runtime` and `.build.lock` before plan/path validation.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The prospective build plan and trusted runtime path are validated before lock acquisition or directory creation; the lock path is reparse-checked both before and immediately after creating its parent.

### Cycle 29 RED - validate candidate topology paths before report-directory creation

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.CandidateTopologyValidatesEveryPathBeforeCreatingReportDirectories`.

Result: RED, exit 1. A malformed manifest hash was rejected only after `offline-topology` had already been created.

GREEN: the same exact-filter command exited 0 with `Passed: 1, Failed: 0`. The scrubbed watchdog launch plan, runtime/apphost/report containment, manifest hash, and required Windows environment are now validated before any candidate report directory is created; the created directory is revalidated before launch.

### Cycle 30 RED - operation-lifetime trusted-path identity lease

Command used the exact filter `FullyQualifiedName=Moondrop.Tests.PhysicalWatchdogTests.TrustedPhysicalPathLeaseDetectsAncestorReplacementBeforeCommit`.

Result: RED, exit 1. `CS0117` proved no stable path lease existed. A point-in-time attribute check could not detect a directory identity change during a sensitive operation.

GREEN: the renamed exact-filter command exited 0 with `Passed: 1, Failed: 0`. Windows now opens every existing ancestor/target with `FILE_FLAG_OPEN_REPARSE_POINT`, verifies handle-level attributes, omits delete sharing, retains the handles across the operation, and compares every final handle path with its original canonical identity before commit. A live ancestor rename is detected. Offline reports, candidate topology, the complete candidate build, watchdog heartbeats, and physical phase artifacts retain and verify these leases across their sensitive read/write/publication windows.

### Third-pass focused verification

The focused `PhysicalWatchdogTests` plus `PhysicalIntegrationSupportTests` default-safe run passed `195/195`. No physical opt-in, phase, HID/USB enumeration, candidate build, approval write, or device access occurred.

## Fourth-pass strict TDD remediation (2026-08-12)

Two fresh read-only reviewers returned NO-GO on the round-3 snapshot. Shared findings were unconstrained authoritative TRX publication and incompletely trusted four-variable topology environments. Additional findings covered descendant-exit proof, live authorization/session check-use windows, first-write ordering, durable raw exception strings, and unbounded wrapper waits. Candidate generation and every physical phase remained blocked.

### Cycle 31 RED - canonical topology environment identity

The exact regression `CandidateTopologyEnvironmentRequiresCurrentExistingNonReparseWindowsPaths` failed because a hostile `SystemRoot`/`WINDIR` pair pointing at TEMP was accepted.

GREEN: the exact test passed after production and offline topology paths were unified on `PhysicalSystemEnvironment.Validate`. Exactly four keys are accepted; all values must be absolute, existing, reparse-free directories, and both Windows variables must equal the current Windows directory identity.

### Cycle 32 RED - unique constrained authoritative TRX evidence

The exact regression `OfflineTopologyTrxEvidenceRejectsPreexistingOrAdditionalResultFiles` initially failed to compile with `CS0117` because no evidence-publication boundary existed.

GREEN: the exact test passed. Every direct/wrapper launch now gets an unpredictable GUID TRX filename; its results directory must initially be empty and is held by a stable path lease. Acceptance requires exactly that one file, no additional entry, reparse-free file/root identities, a stable read lease, and the existing exact-one semantic parser.

### Cycle 33 RED - confirmed descendant cleanup

The strengthened `CandidateTopologyCancellationKillsAndAwaitsTheEntireStartedProcessTree` failed to compile with `CS0117` because no kill-on-close job runner existed. The regression launches a real PowerShell root and `ping.exe` descendant and retains both PIDs.

GREEN: the exact test passed. Supervised direct, wrapper, candidate, and production runner paths now use a Windows kill-on-close job; cancellation/timeout terminates the job and polls authoritative active-process accounting to zero before unwinding. The regression verifies both retained PIDs are absent.

### Cycle 34 RED - durable exception redaction

The exact regression `DurablePhysicalDiagnosticsNeverPersistRawExceptionSecretsOrControls` failed to compile with `CS0103` because no durable exception sanitizer existed.

GREEN: the exact test passed. Execute results, recovery `LastError`, and immediate-restoration aggregate failures now redact one-run/confirmation/session values and escape every control before persistence.

### Cycle 35 RED - authenticated-read target stability

The exact regression `TrustedPhysicalPathLeasePreventsTargetMutationDuringAuthenticatedRead` failed because a leased manifest target could still be overwritten: the attribute-only handle did not request read data and therefore did not create a write-sharing conflict.

GREEN: the exact test passed after file targets were reopened with `GENERIC_READ` and read-only sharing. Live authorization now retains repository/runtime/runner/watchdog/manifest/heartbeat leases through binding and hashing; session loading leases its root and each candidate through read/validation; manifest/apphost binding performs its own stable reads. Directory creation now proceeds one component at a time under a verified parent lease, with immediate reparse validation, before any descendant write.

### Fourth-pass verification

- Focused physical-support/watchdog suite: **199 passed**
- Full default-safe suite: **307 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **307 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

Approval remained fail-closed. No candidate, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, or device access occurred. Any subsequent source/document edit requires two fresh independent reviews before candidate generation.

## Fifth-pass strict TDD remediation (2026-08-12)

Two fresh round-4 reviewers returned NO-GO. Both found a process-creation race: the child was started before assignment to the kill-on-close job, so a first instruction or descendant could escape ownership. The topology reviewer also found that authoritative TRX exclusivity was checked before parsing but not after it, and that wrapper argument transport was not proven for spaced native arguments. The security reviewer additionally required session-path and confirmation redaction in the immediate-restoration durable aggregate. Candidate generation and every physical phase remained blocked.

### Cycle 36 RED - atomic suspended creation, job assignment, and resume

The review-derived RED identified `Process.Start` followed by `AssignProcessToJobObject`, leaving an executable pre-assignment interval. The regressions `SupervisedChildCannotExecuteBeforeItsJobOwnershipCallbackCompletes` and `SuspendedLaunchCallbackFailureTerminatesBeforeTheChildCanExecute` retain a real marker-writing child PID and require that no child instruction runs while identity/ownership validation is in progress or when that validation fails.

GREEN: both tests passed after the sole supervised start path moved to `CreateProcessW(CREATE_SUSPENDED)`, assigned the still-suspended root to the kill-on-close job, performed coherent identity observation while suspended, and resumed only after successful assignment/validation. Every failure terminates the job or the never-resumed root and proves exit before unwinding. Production, direct topology, wrappers, and candidate topology all use this path; the legacy unowned start method was removed.

### Cycle 37 RED - real wrapper native argument fidelity

The strengthened exact regression `DeliberateWrapperShapesActuallyInvokeTheExactNamedExecutableAndPropagateItsExit` first failed with Command Prompt `Expected:<7>. Actual:<1>` and `The filename, directory name, or volume label syntax is incorrect.` After Command Prompt was corrected, it exposed the PowerShell branch with `Expected:<7>. Actual:<0>` and a missing spaced-path marker.

GREEN: the same exact-filter command passed. The regression copies a real Windows PowerShell executable to the exact `Moondrop.PhysicalTests.exe` name, launches it through each deliberate wrapper, passes script and marker paths containing spaces, requires the marker, and requires exit `7`. Command Prompt expands one collision-checked controlled command variable with delayed expansion disabled. PowerShell uses `Diagnostics.ProcessStartInfo` plus the shared Windows argument serializer, waits for the exact child, and propagates its exit code; it no longer uses `Start-Process -ArgumentList`.

### Cycle 38 RED - post-parse authoritative TRX exclusivity

The review-derived RED showed that a second result file created after the initial directory enumeration could survive TRX parsing and still be accepted. The deterministic regression `OfflineTopologyTrxEvidenceRejectsASecondFileCreatedAfterParsing` injects `late.trx` at that exact boundary.

GREEN: the regression passed. After semantic parsing, the TRX target lease and results-directory lease are reverified and the directory is enumerated again; acceptance fails unless the unique authoritative TRX remains the only entry.

### Cycle 39 RED - complete immediate-restoration durable redaction

The review-derived RED found that immediate-restoration aggregation carried only the one-run token as an explicit secret. The regression `ImmediateRestorationDurableFailureRedactsSessionConfirmationAndOneRunSecrets` injects all three values into both execute and restoration exceptions and inspects the persisted failure state.

GREEN: the regression passed. The production execute call supplies session path and confirmation to the orchestrator, which combines them with the one-run token for the durable aggregate sanitizer. The persisted `LastError` contains redaction markers and none of the three raw values.

### Cycle 40 GREEN - bounded cleanup and trusted capture root

The supervised launcher now creates redirected-output files only beneath the absolute, reparse-free `TEMP` supplied by the validated launch plan, retains a stable root lease through capture cleanup, and closes both capture streams before either is read. Job-assignment failure no longer attempts an unverified best-effort tree kill: the never-resumed native root is terminated and its exit is required. Production supervision catches exceptional monitoring paths, terminates remaining job members, and awaits the root before unwinding.

### Fifth-pass verification

- Focused physical-support/watchdog suite: **203 passed**
- Full default-safe suite: **311 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **311 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

Approval remains fail-closed. No candidate, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, or device access occurred. Two fresh independent GO reviews are required on this exact source before candidate generation.

## Sixth-pass strict TDD remediation (2026-08-14)

The fresh round-5 topology review returned NO-GO on one remaining lifecycle window. `CreateProcessW` had succeeded, but `Process.GetProcessById` was evaluated before the cleanup guard; a managed acquisition exception could abandon the still-suspended native root. The review also correctly noted that termination/accounting errors in shared and production supervision had to be followed by an independent root-exit wait rather than short-circuiting cleanup.

### Cycle 41 RED - native root cleanup covers managed process acquisition failure

The exact regression `SuspendedLaunchManagedProcessAcquisitionFailureTerminatesBeforeTheChildCanExecute` initially failed to compile with `CS1739`: the shared launcher had no controlled process-acquisition seam. The regression launches a real suspended PowerShell marker writer, injects acquisition failure immediately after native creation, retains the PID, and requires both no marker and a vanished PID.

GREEN: the exact filter passed after the post-`CreateProcessW` ownership guard was moved ahead of every managed lookup. Every non-transferred native launch now attempts job termination, closes the kill-on-close job, and independently terminates/waits for the native root even if one cleanup action fails; failures are aggregated only after all bounded cleanup attempts. `RequireOwnedProcessStoppedAsync` is now the shared cleanup contract for direct/wrapper/candidate launch and production watchdog timeout, normal completion, and exceptional unwind: it always awaits the root even if job termination/accounting reports an error and surfaces any incomplete proof as an aggregate failure.

### Sixth-pass focused verification

- Acquisition-failure exact regression: **1 passed**
- Atomic ownership/callback/cancellation regression set: **4 passed**
- Focused physical-support/watchdog suite: **204 passed**

Approval remains fail-closed. No candidate, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, or device access occurred. Fresh independent reviews are required on this new exact source before candidate generation.

### Seventh-pass full offline verification

- Full default-safe suite: **313 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **313 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

The hostile values were scoped only to the default-safe test-host command. Default runsettings excluded every physical category, and no physical runner, phase, HID/USB enumeration, or device access occurred.

### Sixth-pass full offline verification

- Full default-safe suite: **312 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **312 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

The hostile values were scoped only to the default-safe test-host command. Default runsettings excluded every physical category, and no physical runner, phase, HID/USB enumeration, or device access occurred.

## Seventh-pass strict TDD remediation (2026-08-14)

Both fresh round-6 reviewers independently returned NO-GO on the same remaining issue: after job termination/accounting failed, the shared root-exit proof used `CancellationToken.None` and could hang indefinitely before surfacing the cleanup failure.

### Cycle 42 RED - root-exit proof remains bounded after job cleanup failure

The exact regression `BoundedRootExitProofForcesKnownRootTermination` initially failed to compile with `CS0117`, proving the launcher had no bounded root-exit contract. It launches a known local PowerShell root that sleeps for sixty seconds, gives the proof a 100 ms initial deadline, and requires the root PID to be absent after the forced bounded path.

GREEN: the exact regression passed. `RequireOwnedProcessStoppedAsync` now uses `RequireBoundedRootExitAsync` in every direct/wrapper/candidate and production cleanup path. It gives the known root a bounded exit window, performs one explicit `Kill(entireProcessTree: true)` only for that known supervised root if needed, then requires exit within a second bounded window. Any job/accounting, kill, or final-wait failure is aggregated only after all bounded attempts finish.

### Seventh-pass focused verification

- Bounded-root/acquisition-failure/cancellation regression set: **3 passed**
- Focused physical-support/watchdog suite: **205 passed**

Approval remains fail-closed. No candidate, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, or device access occurred. Fresh independent reviews are required on this new exact source before candidate generation.

## Eighth-pass strict TDD remediation (2026-08-14)

After two independent round-7 reviewers issued GO, retained candidate A stopped at its mandatory offline MTP topology gate. The candidate's observation itself proved the expected direct watchdog parent and its retained TRX recorded one passed `PublishedRunnerCapturesAuthenticatedParentTopology` result. The failure was in the watchdog's semantic TRX parser: Microsoft Testing Platform stores the short display name on `UnitTestResult@testName`, while its fully qualified identity is bound by `UnitTestResult@testId` to `TestDefinitions/UnitTest/TestMethod`. No physical phase ran and candidate B was not started.

### Cycle 43 RED - authoritative MTP result identity resolution

The new exact regression `OfflineTopologyTrxAcceptsMtpShortResultNameWhenDefinitionProvesExpectedFqn` supplied the retained candidate's real MTP shape: one passed short-name result with `testId`, and one matching definition whose `className` plus `name` is `Moondrop.PhysicalTests.OfflineTopologyProbeTests.PublishedRunnerCapturesAuthenticatedParentTopology`. It failed RED with `Offline MTP topology smoke did not execute the exact expected test and outcome.`, proving that the pre-existing parser compared the wrong field.

GREEN: the exact regression and the existing exact-one TRX regressions passed after the parser required every result carrying a `testId` to resolve to exactly one `UnitTest` definition and exactly one `TestMethod`, then compared the reconstructed fully qualified class-plus-method identity and outcome. A result with a test ID cannot bypass its authoritative definition mapping; missing, ambiguous, or malformed mappings fail closed. Legacy synthetic evidence without a test ID continues to require the exact fully qualified result name.

### Eighth-pass full offline verification

- Focused physical-support/watchdog suite: **206 passed**
- Full default-safe suite: **314 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **314 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

Candidate A remains retained at `tests-dotnet/artifacts/physical-runtime/c42f9e7d8a6b5c4d3e2f1a0b9c8d7e6f/candidate-a` as offline failure evidence. This source/document change invalidates the prior review pair. Approval remains fail-closed; candidate B, approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two new independent GO reviews on this exact source.

### Cycle 44 RED - fail-closed MTP definition-container binding

The fresh security/lineage review returned NO-GO on Cycle 43 because a present-but-blank `UnitTestResult@testId` was treated as absent, and an ID lookup could match a `UnitTest` outside `TestDefinitions`. The reviewer-derived regression `OfflineTopologyTrxRejectsBlankOrOutOfScopeTestIdBinding` first failed RED because the blank-ID result fell back to its fully qualified display name without a definition.

GREEN: every present `testId` must now be nonblank, the TRX must contain exactly one `TestDefinitions` container, and the result ID must resolve to exactly one direct `UnitTest` child with exactly one direct `TestMethod` child. A forged matching `UnitTest` outside that container cannot satisfy topology evidence. The exact topology-TRX regression group passed **5/5**.

### Eighth-pass post-review full offline verification

- Focused physical-support/watchdog suite: **207 passed**
- Full default-safe suite: **315 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **315 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

The original round-8 review pair was invalidated by this security fix. Approval remains fail-closed; candidate B, approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two new independent GO reviews on this exact source.

### Cycle 45 RED - canonical TRX root and hierarchy binding

The fresh topology/MTP review returned NO-GO because the semantic parser still searched XML descendants by local name. An untrusted nested subtree or forged namespace could therefore supply a complete-looking result, definition, and counter set. The reviewer-derived regression `OfflineTopologyTrxRejectsNestedOrWrongNamespaceMtpIdentitySubtrees` first failed RED when a nested `UntrustedExtension` subtree was accepted.

GREEN: the parser now requires the official TRX namespace and direct `TestRun` root, then exactly one direct `Results`, `TestDefinitions`, and `ResultSummary` container. It reads only direct `Results/UnitTestResult`, `TestDefinitions/UnitTest/TestMethod`, and `ResultSummary/Counters` elements in that namespace. Nested impostor evidence and namespace spoofs fail closed. The retained candidate-A TRX has this canonical shape; the complete topology-TRX group passed **6/6**.

### Eighth-pass post-topology-review full offline verification

- Focused physical-support/watchdog suite: **208 passed**
- Full default-safe suite: **316 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **316 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This production/test/document change invalidates every prior review snapshot. Approval remains fail-closed; retained candidate A is invalid for current source, candidate B does not exist, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two new independent GO reviews on this exact source.

## Ninth-pass strict TDD remediation (2026-08-14)

The new isolated candidate A (`f30daa7b7e874e8bae0f21726d46396c`) stopped at its mandatory offline deliberate-wrapper evidence gate. Its authoritative wrapper TRX correctly recorded the one selected test as `Failed` with the expected direct-parent rejection, but Microsoft Testing Platform also emitted one direct `Deploy_ 20260814T022245_4284` directory for that expected failure. The prior exact-directory policy rejected the normal MTP deployment artifact. Candidate B was not started; no approval or physical phase ran.

### Cycle 46 RED - constrained expected MTP deployment artifact

The new regression `OfflineTopologyWrapperEvidenceAllowsOnlyTheMtpDeploymentDirectoryBesideItsAuthoritativeTrx` initially failed to compile with `CS1739`, proving no narrow wrapper-only allowance existed. It creates a canonical failed one-test TRX plus one direct `Deploy_ ...` directory, requires acceptance only when explicitly requested, then writes `unexpected.txt` and requires rejection.

GREEN: `PreparedMtpEvidence.RequireExactlyOne` retains the default strict exact-one-file rule. Only the deliberate wrapper-rejection path explicitly allows one direct, reparse-free directory whose name starts exactly `Deploy_ ` beside the authoritative TRX; it still requires the unique leased TRX before and after parsing, rejects any other entry or a second deployment directory, and preserves the explicit post-parse changed-directory failure. The topology evidence group passed **7/7**.

### Ninth-pass full offline verification

- Focused physical-support/watchdog suite: **209 passed**
- Full default-safe suite: **317 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **317 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This source/test/document change invalidates every prior review snapshot. The new failed candidate A remains retained as offline evidence only; candidate B is absent. Approval remains fail-closed, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two fresh independent GO reviews on this exact source.

### Cycle 47 RED - allowed deployment directory remains stable through parsing

The fresh topology/MTP review returned NO-GO because the Cycle 46 allowance validated entry shape before and after parsing but did not bind the exact pre-parse deployment-directory set. The strengthened wrapper-evidence regression removes the pre-existing allowed deployment directory, creates a late `Deploy_ late` directory only in the post-parse callback, and initially failed RED because that new allowed entry was accepted.

GREEN: pre-parse evidence capture now canonicalizes and snapshots every accepted top-level entry, acquires a stable no-reparse lease for each allowed deployment directory, verifies those leases after parsing, and requires the final accepted path set to be exactly identical. Thus no allowed deployment directory may appear, disappear, or be replaced during parsing; direct topology still accepts only the authoritative TRX. The topology evidence group remains **7/7** green.

### Ninth-pass post-review full offline verification

- Focused physical-support/watchdog suite: **209 passed**
- Full default-safe suite: **317 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **317 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This source/test/document change invalidates every prior review snapshot. The retained failed candidates remain offline evidence only; candidate B is absent. Approval remains fail-closed, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two fresh independent GO reviews on this exact source.

## Round-11 strict TDD remediation (2026-08-16)

The first authorized read-only PREPARE attempt failed OFFLINE (before any HID/device access) at the deliberate `cmd.exe` wrapper smoke. The wrapper's nested Microsoft Testing Platform child wrote its `observed-topology.json` correctly, but its `.trx` was not yet visible when `PreparedMtpEvidence.RequireExactlyOne` acquired the strict-existing lease, so `AcquireExistingContainedNoReparseLease` raised `FileNotFoundException: Offline MTP TRX expected target is missing.` No physical category ran and no device state was touched.

### Cycle 51 RED - offline MTP TRX target wait

The new regression `OfflineTopologyTrxWaitsForLateTrxTargetWithinBound` creates a `PreparedMtpEvidence`, schedules the authoritative TRX to be written 400 ms later, and calls `RequireExactlyOne`. It failed RED with the exact production failure:

```text
System.IO.FileNotFoundException: Offline MTP TRX expected target is missing.
   at Moondrop.PhysicalWatchdog.TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(...)
   at Moondrop.PhysicalWatchdog.PhysicalOfflineTopologyProbe.PreparedMtpEvidence.RequireExactlyOne(...)
```

GREEN: `RequireExactlyOne` now performs a bounded (10-second) wait for the authoritative TRX target to appear before acquiring the strict-existing lease, re-validating reparse containment on every poll. The same regression passes, and the strict-existing lease plus the exact-one pre/post-parse entry comparison are unchanged, so the fail-closed exact-one guarantee is not weakened.

### Round-11 focused/full verification

- Focused physical-support/watchdog suite: **211 passed**
- Full default-safe suite: **319 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **319 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**
- Fresh candidate-only build re-ran the full direct/wrapper topology smoke successfully and retained complete `wrapper-cmd` and `wrapper-powershell` TRX evidence.

This source/test/document change invalidates every prior review snapshot and the prior candidate pair. Approval has been reset to fail-closed, and candidate generation, approval population, and physical access remain blocked pending two fresh independent GO reviews on this exact new source.

### Cycle 50 RED-to-GREEN review follow-up - lease before accepted evidence snapshot

The next security review correctly noted that a path-string enumeration cannot itself establish object identity. Acceptance now starts only after the authoritative TRX has been acquired with its strict-existing lease. Optional deployment candidates are then strictly leased, immediately re-enumerated while those leases are held, and only that stable exact path set becomes the pre-parse evidence snapshot. A discovery-time replacement can never be treated as the previously observed object: the accepted object is the handle-locked object, and any entry-set change while acquiring those locks fails closed.

### Tenth-pass final offline verification

- Focused physical-support/watchdog suite: **210 passed**
- Full default-safe suite: **318 passed** (one teardown-only `OfflineSmoke.exe` lock was absent on diagnosis and the immediate rerun passed)
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **318 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This source/test/document change invalidates every prior review snapshot. The retained failed candidates remain offline evidence only; candidate B is absent. Approval remains fail-closed, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two fresh independent GO reviews on this exact source.
### Cycle 49 RED-to-GREEN review follow-up - authoritative TRX strict target lease

The follow-up security review correctly applied the Cycle 48 missing-target analysis to the authoritative TRX itself: it was still acquired with the intentionally permissive general lease. The existing strict-existing lease regression established the missing-target RED/green behavior; this follow-up applies that same strict contract to the already-published authoritative TRX before semantic parsing. The direct and wrapper paths now both fail closed if the TRX disappears between evidence enumeration and handle acquisition, so neither can parse a same-path recreated file through an ancestor-only lease.

### Tenth-pass post-review full offline verification

- Focused physical-support/watchdog suite: **210 passed**
- Full default-safe suite: **318 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **318 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This source/test/document change invalidates every prior review snapshot. The retained failed candidates remain offline evidence only; candidate B is absent. Approval remains fail-closed, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two fresh independent GO reviews on this exact source.

## Tenth-pass strict TDD remediation (2026-08-14)

The fresh security/lineage review returned NO-GO on Cycle 47 because the general stable-path lease intentionally tolerates a missing target. A deployment directory removed after enumeration but before lease acquisition could therefore yield an ancestor-only lease and later be recreated at the same path.

### Cycle 48 RED - accepted deployment target must exist at lease acquisition

The new regression `TrustedPhysicalPathExistingLeaseRejectsMissingAcceptedTarget` initially failed RED with `CS0117`, proving no strict-existing lease API existed. It creates a trusted root, requests an accepted deployment-target lease for a missing direct target, and requires a `FileNotFoundException` rather than an ancestor-only lease.

GREEN: `AcquireExistingContainedNoReparseLease` now uses the same reparse-safe handle chain as the general lease but fails closed if its target itself is missing during handle acquisition. Wrapper evidence uses this strict-existing variant for every accepted deployment directory. The pre-parse snapshot, target lease, and exact final set comparison therefore bind the deployment directory identity across parsing; a disappearance/replacement cannot become an accepted recreated path. The topology and lease regression group passed **8/8**.

### Tenth-pass full offline verification

- Focused physical-support/watchdog suite: **210 passed**
- Full default-safe suite: **318 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **318 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**

This source/test/document change invalidates every prior review snapshot. The retained failed candidates remain offline evidence only; candidate B is absent. Approval remains fail-closed, and approval population, PREPARE, EXECUTE, RECOVERY, HID/USB enumeration, and device access remain blocked pending two fresh independent GO reviews on this exact source.
## Round-12 strict TDD remediation (2026-08-17)

An authorized read-only PREPARE orchestration attempt again failed **offline, before any HID/device access**, during the deliberate `cmd.exe` wrapper smoke of the fresh candidate build. The wrapper's nested Microsoft Testing Platform child wrote `observed-topology.json` and its TRX exists on disk, but the single strict-existing lease attempt threw the same `FileNotFoundException: Offline MTP TRX expected target is missing.` This proved the Round-11 Cycle-51 wait was incomplete: it polled `File.Exists` and then performed exactly **one** strict-existing lease acquisition. If the authoritative TRX transiently vanishes between the existence poll and the handle open (MTP shutdown/cleanup behavior), the operation fails closed even though the file becomes stable moments later. No physical category ran, no device was opened, and no device state was touched.

### Cycle 52 RED - strict-existing TRX lease must retry a transiently missing target

The new deterministic regression `OfflineTopologyTrxStrictLeaseRetriesTransientMissingTargetWithinBound` creates a `PreparedMtpEvidence`, writes the authoritative TRX, and deletes it through an injectable `beforeTrxLeaseAttempt` seam at the exact pre-lease boundary while a writer recreates it 400 ms later. It failed RED with the exact production failure:

```text
System.IO.FileNotFoundException: Offline MTP TRX expected target is missing.
   at Moondrop.PhysicalWatchdog.TrustedPhysicalPath.AcquireContainedNoReparseLease(...)
   at Moondrop.PhysicalWatchdog.TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(...)
   at Moondrop.PhysicalWatchdog.PhysicalOfflineTopologyProbe.PreparedMtpEvidence.RequireTrxTargetPresent(...)
```

### Cycle 52 GREEN - bounded strict-existing lease retry

`RequireTrxTargetPresent` now retries the strict-existing lease acquisition itself inside the same bounded window: it loops attempting `AcquireExistingContainedNoReparseLease`, revalidates reparse containment on every retry, sleeps 50 ms between attempts, and rethrows the `FileNotFoundException` unchanged when the 10-second wall-clock deadline passes. Properties preserved:

- the strict-existing lease is still the only acceptance boundary (the file is opened with read-only sharing, no delete/rename/overwrite possible while held, and the lease's final-path identity is verified after parsing);
- the pre/post-parse entry-set comparison is unchanged;
- every retry revalidates reparse containment (no junction/symlink escape);
- the wait remains bounded (10 s / 50 ms) and fail-closed (deadline expiry rethrows);
- exact-one semantics are not weakened: the same `RequireExactlyOneMtpTest` canonical parsing and the same unique-file/directory-set checks apply to whatever file is ultimately leased;
- a transiently-missing target can only delay acceptance, never cause false acceptance.

GREEN: the same exact-filter regression passed with `attempts >= 2` (observed 8 lease attempts, proving the retry loop engaged until the recreated TRX was acquired).

### Round-12 focused/full verification

- Focused physical-support/watchdog suite: **212 passed** (211 prior + 1 new Cycle-52 regression)
- Full default-safe suite: **320 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **320 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**
- Scoped whitespace/`git diff --check` clean.

This source/test/document change invalidates every prior review snapshot and the prior candidate pair (d444/e555). Approval has been reset to fail-closed, and candidate generation, approval population, and physical access remain blocked pending two fresh independent GO reviews on this exact new source.

### Files changed in Round 12

- `tests-dotnet\Moondrop.PhysicalWatchdog\WatchdogPolicy.cs` - `PreparedMtpEvidence.RequireExactlyOne` gained the test seam `beforeTrxLeaseAttempt`; `RequireTrxTargetPresent` now returns the strict-existing lease acquired through a bounded retry loop instead of a single post-existence attempt.
- `tests-dotnet\Moondrop.Tests\PhysicalWatchdogTests.cs` - added `OfflineTopologyTrxStrictLeaseRetriesTransientMissingTargetWithinBound`.
- `tests-dotnet\OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md` - this record.
- `tests-dotnet\physical-runtime-approval.json` - reset to fail-closed because the source fingerprint changed.

---

## Round-13 strict TDD remediation (2026-08-17) - extended-length stable-path leases

### Production RED reproduced and retained

The single authorized read-only PREPARE prerequisite failed in its OFFLINE mandatory fresh-candidate gate with:

System.IO.FileNotFoundException: Offline MTP TRX expected target is missing.

whose File name was the 267-character ...\offline-topology\wrapper-cmd\mtp-results\offline-topology-<guid>.trx, thrown at TrustedPhysicalPath.AcquireContainedNoReparseLease (WatchdogPolicy.cs:1234) through the RequireTrxTargetPresent bounded retry loop. No HID/USB/device access occurred; no physical category ran. Independent reproduction with the identical pre-fix published watchdog/runner pair failed identically (exit 1), and a 200 ms directory watcher proved the TRX file existed and was visible during the entire 10 s bounded retry window - the retry loop is correct for genuine transient targets but cannot repair a deterministic open failure.

Root cause: the watchdog raw CreateFileW P/Invoke receives the plain path, and the published self-contained apphost manifest declares only asInvoker (no longPathAware), so Windows long-path support is not engaged beyond MAX_PATH (260). The failing PREPARE fresh-build wrapper TRX path was 267 characters while the direct-phase TRX path was 255 (< 260), which is why the direct+smoke phases passed and only the wrapper phase failed. Host facts confirmed by extracting RT_MANIFEST from the published binaries: watchdog/runner apphosts have NO longPathAware; dotnet.exe (the MTP test-host process for dotnet test) and TestHostNetFramework\testhost.exe DO, which is why the in-process unit test host alone cannot reproduce the raw failure on this machine.

### Cycle 53 RED - existing stable-path lease beyond MAX_PATH

New regression TrustedPhysicalPathExistingLeaseAcquiresExistingTargetBeyondWindowsMaxPath in PhysicalWatchdogTests.cs builds a trusted-root target whose absolute path is 323 characters, requires the new TrustedPhysicalPath.ToExtendedLengthForm API (compile-time RED via CS0117 until the API exists, per the Cycle-42 accepted pattern), and requires the strict-existing lease to open and verify that target. Retained production RED: the failing 0babb170.../prepare-5416... PREPARE fresh build plus two independent repro runs of the same pre-fix published pair (each exit 1, exact FileNotFoundException above).

### Cycle 53 GREEN - extended-length form for raw CreateFileW

TrustedPhysicalPath.ToExtendedLengthForm(path) returns the extended-length (\\?\ or \\?\UNC\) absolute form. The stable-path lease passes that form to both CreateFileW sites (attribute-inspection and stable-read handles), the staged-source integrity guard passes it to its CreateFileW site, and the existing NormalizeFinalPath strips the prefix so final-path identity comparison remains unchanged. The bounded TRX retry loop from Round 12 is retained unchanged.

Focused physical-support/watchdog suite: **213 passed** (was 212 + 1 new regression).

### End-to-end GREEN discriminator

Moondrop.PhysicalWatchdog.exe --build-runtime-smoke --repo <root> --session-id c3f000000000000000000000000000c3 --generation prepare-c3f111111111111111111111111111c3 --dotnet <pinned> rebuilt the entire self-contained runtime into the same long-generation layout as the failing PREPARE tree (wrapper TRX path 267 characters) and ran the real MTP direct + cmd-wrapper + PowerShell-wrapper topology evidence on the freshly published apphosts: **SMOKE_EXIT=0** with the exact structured rejection evidence retained.

- Source: A9D0EEEEF4B1185949F3327C8CFCD976278C685AD930EB52470500934DA3A4EC (176 inputs / 117 presence / 59 content)
- Runtime: A1859045D6268588D04D68FA846DAAD33DD6657672B63E0FADBDD0957AC11CE3 (532 total / 331 runner / 192 watchdog / 9 metadata)
- Retained candidate: tests-dotnet\artifacts\physical-runtime\c3f000000000000000000000000000c3\prepare-c3f111111111111111111111111111c3

This source change invalidates the prior Round-12 pair and approval. Approval remains fail-closed; new isolated sequential candidates, two fresh independent GO reviews, approval population, PREPARE, EXECUTE, and RECOVERY remain blocked pending full offline verification on this exact source.

### Files changed in Round 13

- tests-dotnet\Moondrop.PhysicalWatchdog\WatchdogPolicy.cs - added TrustedPhysicalPath.ToExtendedLengthForm; raw CreateFileW calls in the stable-path lease (attribute + stable-read handles) and in the staged-source integrity guard now use the extended-length form.
- tests-dotnet\Moondrop.Tests\PhysicalWatchdogTests.cs - added TrustedPhysicalPathExistingLeaseAcquiresExistingTargetBeyondWindowsMaxPath.
- tests-dotnet\OFFLINE-PHYSICAL-LINEAGE-TDD-EVIDENCE.md - this record.
### Round-13 full offline verification

- Focused physical-support/watchdog suite: **213 passed**
- Full default-safe suite: **321 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **321 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**
- Scoped `git diff --check` clean.

The hostile values were scoped only to the default-safe test-host command. Default runsettings excluded every physical category, and no physical runner, phase, HID/USB enumeration, or device access occurred.

### Round-13 verified read-only PREPARE (2026-08-17)

Exactly one read-only PREPARE was executed after every offline, review, reproducibility, approval, and preflight gate passed. Invocation: tests-dotnet\artifacts\run-prepare.ps1 (round13-candidate-a runner apphost, physical.runsettings, filter PrepareDawnPro2PhysicalSessionReadOnlyAsync, MOONDROP_PREPARE_PHYSICAL_TESTS=1). PREPARE_EXIT=0; MTP summary Passed (1 test, 44 s 629 ms).

- Session: tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-20260817T161620964Z-0afa048f07ae4d3d87763af4b00ccc5a.json
- Recovery snapshot (byte-identical): ...\dawn-pro2-session-20260817T161620964Z-0afa048f07ae4d3d87763af4b00ccc5a.recovery.json (33108 bytes, byte-identical to session)
- Raw frames: tests-dotnet\artifacts\hardware-results\dawn-pro2-frames-20260817T161620964Z-0afa048f07ae4d3d87763af4b00ccc5a.json
- SessionId: d122130384c648d7b8eadbd8b5717791; Phase=Prepared
- Session SourceFingerprint: A9D0EEEEF4B1185949F3327C8CFCD976278C685AD930EB52470500934DA3A4EC (approval match)
- Session RuntimeManifestSha256: A1859045D6268588D04D68FA846DAAD33DD6657672B63E0FADBDD0957AC11CE3 (approval match)
- Device: DAWN PRO2, VID 0x35D8, PID 0x011D, serial 35D8011D251117; firmware ordinal-exact "1.5"; raw active_eq 9; 8 bands; preGainRaw -1024; globalGainRaw 0
- Snapshots: 2 complete, byte-identical (same 33108 bytes)
- HID reads: exactly 24 read-report frames (2 x 12 reads); stored in session ReadFrames = 24
- HID writes: 0; EQ writes: 0; gain writes: 0; active-EQ writes: 0; flash-save operations: 0
- Evidence inspected independently (not just exit code): session JSON fields, frame array content, snapshot byte comparison, identity fields.

The Round-13 extended-length fix was exercised by this PREPARE: its offline fresh-build gate ran the wrapper-cmd/wrapper-powershell topology evidence inside the long prepare-<guid> tree (>260-char wrapper TRX paths) and passed, which is the exact previously failing production path.

STOP: EXECUTE and RECOVERY were NOT started. No physical mutation occurred. EXECUTE requires a separate explicit user decision in a future goal.

---

## Round-14 strict TDD remediation (2026-08-17) - .NET 10 process observation (managed P/Invoke WMI replacement)

### Production RED reproduced and retained

The first controlled EXECUTE attempt (Moondrop.PhysicalWatchdog.exe --mode execute) did NOT reach HID/device access. The watchdog creates its runner suspended and must authenticate/observe the exact child before allowing execution; the historical WindowsCommandLineReader used .NET dynamic COM (WbemScripting.SWbemLocator) against Win32_Process and under the actual .NET 10 runtime deterministically threw System.Runtime.InteropServices.COMException (0x80004005) "Unspecified error" when reading the Win32_Process row of a process OTHER than the caller (the suspended child). The watchdog failed closed, never let the child execute; no DAC/HID access, no write, no flash-save occurred; the Prepared baseline (d1221303 / 0afa048f...) remained intact.

Retained RED evidence (tests-dotnet\artifacts\controlled-write-evidence\): execute-run.log and execute-run2.log (identical COMException at Program.cs:515, stack through CoherentObservedPhysicalProcessProvider.Get -> StartOwnedSuspended whileSuspended -> RunSupervisedAsync), plus wmi-net10-probe outputs and probe3/4/5-stderr.txt. Managed WMI (Get-WmiObject/Get-CimInstance) on another process WORKS, proving the failure is CoreCLR dynamic-COM IDispatch interop, not WMI or the device.

### Root cause

.NET Core/.NET 10 does not provide the dynamic COM IDispatch late-binding that the historical reader relied on: dynamic dispatch against SWbemObject rows of another process raises COMException 0x80004005 (self rows are OK; other-process rows fail), independent of harness context, user, interactive session, MTA/STA, or Task Scheduler. The watchdog therefore could never authenticate its suspended child.

### Cycle 54 GREEN - managed P/Invoke process observation (no new packages, no shell)

Three production observation sites were replaced with managed P/Invoke (kernel32/ntdll only; no new NuGet dependency - locked offline restore preserved):

1. tests-dotnet\Moondrop.PhysicalWatchdog\WatchdogPolicy.cs - new public sealed class WindowsObservedPhysicalProcessSnapshotReader : IObservedPhysicalProcessSnapshotReader (lines ~132-232): OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) -> GetProcessTimes (creation identity, FILETIME epoch) + QueryFullProcessImageNameW (executable path), then OpenProcess(PROCESS_QUERY_INFORMATION) -> NtQueryInformationProcess(ProcessCommandLineInformation = 60) for the command line UNICODE_STRING (validated length parity, buffer-relative pointer bounds, empty/null rejection). Shared by the watchdog (child observation) and the runner (via linked source). Works for both SUSPENDED and running processes; every path fails closed.

2. tests-dotnet\Moondrop.Tests\PhysicalIntegrationSupport.cs - WindowsPhysicalIdentitySnapshotReader.Read: managed P/Invoke (GetProcessTimes + QueryFullProcessImageNameW + NtQueryInformationProcess(ProcessBasicInformation = 0) for InheritedFromUniqueProcessId as the parent PID); old WMI CreationDate string parsing removed.

3. tests-dotnet\Moondrop.Tests\PhysicalIntegrationSupport.cs - WindowsPhysicalProcessQuery.QueryWmi: managed replacement enumerating Process.GetProcesses() and (for the one narrow shape that needs it) reading the command line through WindowsObservedPhysicalProcessSnapshotReader; ManagedProcessQueryShape.TryParse accepts ONLY the two documented narrow shapes (ProcessId[,CommandLine]+Name) and anything else fails closed.

### Cycle 54 regressions (4 added; focused 213 -> 217)

- ProductionWatchdogObservesAnotherRealSuspendedProcessIdentityUnderNet10 (PhysicalWatchdogTests.cs): spawns a real cmd.exe child SUSPENDED via CreateProcessW(CREATE_SUSPENDED), reads it through the exact production abstraction (CoherentObservedPhysicalProcessProvider(new WindowsObservedPhysicalProcessSnapshotReader())), asserts exact PID, exact StartTime (vs Process.GetProcessById oracle) and that the command line contains the unique marker; cleans up via ResumeThread+TerminateProcess. No HID/USB/DAC access.
- WindowsObservedPhysicalProcessSnapshotReaderFailsClosedForMissingProcess (PhysicalWatchdogTests.cs): a nonexistent PID must fail closed (no fabricated identity).
- WindowsPhysicalIdentityProviderReadsAnotherRealProcessUnderNet10 (PhysicalIntegrationSupportTests.cs): child-side lineage gate authenticates a real spawned child (PID, PPID == the spawning test host, StartTime, executable path).
- WindowsProcessConflictQueryReadsAnotherRealProcessRowUnderNet10 (PhysicalIntegrationSupportTests.cs): conflict inspection reads a real other-process row (Name/CommandLine) through the managed replacement.

### Round-14 requalification results

- Focused physical-support/watchdog suite: **217 passed**
- Full default-safe suite: **325 passed**
- Full default-safe suite with hostile leaked PREPARE/EXECUTE/RECOVERY opt-ins: **325 passed**
- Python oracle: **124 passed**
- Solution, physical runner, and watchdog Release builds: **0 warnings / 0 errors**
- Scoped git diff --check clean.

### Round-14 fingerprints (changed source invalidates prior round)

- Source: C7455302C7AA927B3B84747A41704A085DF156F376930A1C6AD7CD0C54600500 (176 inputs / 117 presence / 59 content)
- Runtime: 632A68D6B6E79D2409024AA0BD0615E9F3BDF87EC5E636B0EEC7953C263A2EB5 (532 total / 331 runner / 192 watchdog / 9 metadata)

Approval reset to fail-closed (INDEPENDENT_AUDIT_REQUIRED). Fresh independent reviews and fresh candidate pair required before approval population.

### Round-14 Prepared-session binding decision (2026-08-17)

The verified Round-13 Prepared baseline (0afa048f / d1221303, bound to A9D0EEEE/A1859045) and the intermediate session (574dc02a / ac6ecf8f, bound to 79A5D9F5/B21483B9) are both bound to fingerprints that the new approval (C7455302/632A68D6) cannot match. The production provenance rule (Program.cs EnsureFreshReviewedReleaseAsync: fresh source/runtime must equal the session fingerprints, and approval must equal both) therefore REQUIRES a Prepared session bound to the exact current runtime fingerprint. Per the goal protocol for this case, after the new approval completed and was verified against both fresh candidates, exactly ONE minimal fresh read-only PREPARE was performed and verified; the new session is the device-state baseline for the controlled EXECUTE. No protocol weakening was applied.

### Cycle 55 - runner-side watchdog manifest-path gate reachability (2026-08-17)

The first post-fix EXECUTE attempt (session 4d570611, watchdog from the session prepare tree) reached the runner (the fixed .NET 10 observation let the suspended child execute) but failed closed at the child-side lineage gate with predicate=watchdog-manifest-path: the gate requires the supervising watchdog executable to be located at the RUNNER tree watchdog path (physical-runtime\<session>\execute-<guid>\watchdog\...), while the invoked watchdog lives in the session prepare tree. Both binaries are byte-identical (same manifest SHA), but the strict path equality is unreachable with the documented invocation (the fresh execute tree is created during the run with a random generation). This latent defect was never exercised before because the pre-fix WMI observation always failed earlier.

Cycle 55 RED: new regression SameSessionApprovedWatchdogInAnotherIdenticalRuntimeTreeIsAccepted (PhysicalIntegrationSupportTests.cs) builds a session prepare tree and a fresh execute tree carrying the IDENTICAL approved runtime manifest, a consistent heartbeat, and matching identities, and requires the gate to accept the prepare-tree watchdog supervising the execute-tree runner. RED result: rejected with predicate=watchdog-manifest-path (exact production failure shape).

Cycle 55 GREEN: RuntimeApphostManifestBinding.Require (WatchdogPolicy.cs) now accepts the supervising watchdog from a different tree of the SAME approved runtime: the watchdog's own containing tree must carry the identical approved runtime manifest (aggregate == session runtime SHA; canonical ReadStrict), the resolved manifest watchdog path must equal the actual watchdog path, and the existing per-file SHA checks remain. Containment leases are scoped to each tree's own root. All other predicates (runner path, hashes, heartbeat, direct-parent, membership) unchanged. Focused suite: 218 passed (217 + 1).

### Round-15 - Cycle-55 re-qualification and fresh Prepared session (2026-08-17)

Cycle-55 changed production source (RuntimeApphostManifestBinding.Require), invalidating the Round-14 candidates/approval and the 4d570611 session bound to the Round-14 pair. Re-qualified: focused 218, full default-safe 326, hostile-leaked 326, Python 124, Release builds 0 warnings / 0 errors, git diff --check clean, round15-candidate-a + round15-candidate-b byte-identical (60/60 source, 331/331 runner, 192/192 watchdog, byte-identical runtime-manifest.json), live source 1CFBDA8F... (176/117/59), runtime 0515AD83... (532/331/192/9). Fresh independent security/lineage review and topology/testability review both returned VERDICT: GO for the Cycle-55 delta. Approval populated with 1CFBDA8F/0515AD83 and verified (exit 0) against both fresh candidates.

Per the session-to-runtime fingerprint binding, one minimal fresh read-only PREPARE was run against the newly approved round15 runtime; its session is the device-state baseline for the controlled EXECUTE.

### Cycle 56 - machine-wide run-lock path under the cleared watchdog child environment (2026-08-17)

The second post-fix EXECUTE attempt (session 14713630, round15 runtime) passed the lineage gate entirely (the Round-14 observation fix and the Cycle-55 gate fix both worked) and reached the machine-wide run lock (PhysicalIntegrationSupport.cs PhysicalRunLock.TryAcquireDefault -> DawnPro2PhysicalIntegrationTests.cs:166), which threw InvalidOperationException "The common application-data directory is unavailable; physical testing is locked out." before any device access. Under .NET 10 Environment.GetFolderPath(CommonApplicationData) returns EMPTY in the runner child because the watchdog launches it with a cleared environment restricted to SystemRoot, WINDIR, TEMP, TMP (the exact allowlist contract), and .NET consults the ProgramData/ALLUSERSPROFILE environment for that special folder. PREPARE works because its runner inherits the full interactive environment.

Empirically confirmed by an offline .NET 10 console probe (tests-dotnet\artifacts\envprobe) run under both environments: full env -> CommonAppData=[C:\ProgramData]; minimal env (SystemRoot/WINDIR/TEMP/TMP) -> CommonAppData=[] while Windows=[C:\Windows] and System=[C:\Windows\system32] still resolve.

Cycle 56 GREEN: PhysicalRunLock.TryAcquireDefault now derives the canonical machine-wide directory through ResolveCommonApplicationDataPath(commonApplicationData, windowsDirectory): uses GetFolderPath(CommonApplicationData) when available, otherwise falls back to GetFolderPath(Windows) parent \ ProgramData (volume-relative, canonical), else throws the exact lock-out message. The watchdog child environment allowlist contract is UNCHANGED (still exactly SystemRoot/WINDIR/TEMP/TMP + the Moondrop opt-in/identity variables). New regression RunLockResolvesTheCanonicalWindowsProgramDataWhenCommonApplicationDataIsUnavailable (PhysicalIntegrationSupportTests.cs) covers fallback, second-volume derivation, throw, and passthrough. Retained behavioral RED: tests-dotnet\artifacts\execute-run-20260817233052.log.

Focused suite: 219 passed (218 + 1).

### Round-16 - Cycle-56 re-qualification and fresh Prepared session (2026-08-17)

Cycle-56 changed production source (PhysicalRunLock), invalidating the Round-15 candidates/approval and the 14713630 session. Re-qualified: focused 219, full default-safe 327, hostile-leaked 327, Python 124, Release builds 0 warnings / 0 errors, git diff --check clean, round16-candidate-a + round16-candidate-b byte-identical (60/60 source, 331/331 runner, 192/192 watchdog, byte-identical runtime-manifest.json), live source 628B1146... (176/117/59), runtime 46A22ACC... (532/331/192/9). Fresh independent security/lineage and topology/testability reviews both returned VERDICT: GO for the Cycle-56 delta. Approval populated with 628B1146/46A22ACC and verified (exit 0) against both fresh candidates.

Per the session-to-runtime fingerprint binding, one minimal fresh read-only PREPARE was run against the newly approved round16 runtime; its session is the device-state baseline for the controlled EXECUTE.

### Round-16 controlled EXECUTE - result (2026-08-17)

Invocation: tests-dotnet\artifacts\run-execute.ps1 (watchdog from the session prepare tree; --mode execute; session 62766b3a / 394890434e004fa3be59f489909e95a4). EXECUTE_EXIT=0; MTP summary Passed (ExecutePreparedDawnPro2PhysicalSessionAsync, 1 test, ~7 s). Result record daemon-pro2-result-20260817T205218438Z-62766b3a.json: Status=passed-after-fresh-full-raw-restoration, RestorationAttempted=true, RestorationVerified=true, PrimaryError=null, RestorationError=null; session Phase=Completed (11).

Phases (all passed):
- "individual coefficient-relevant band command path" - wrote PEQ band 0 gain raw 512 -> 576 (+2.00 dB -> +2.25 dB), immediate full readback verified exactly that change with zero unexpected differences.
- "exact original raw RAM restoration and readback" - restored band 0 576 -> 512 with the exact original raw RAM + pre-gain + global gain, read back, verified complete semantic equality with the Prepared baseline.

Structured band-0 report diff (baseline vs temporary): only the gain low byte (report byte 31: 0x00 -> 0x40, raw 512 -> 576) plus the derived biquad coefficient bytes changed; frequency (100), Q (182), filter type (1), enabled, and all other bands/pre-gain/global-gain/active-eq/firmware/identity unchanged. Final full readback equals the Prepared baseline (0 logical differences; harness SnapshotEquals passed in both phases; session Phase=Completed).

Write accounting: the temporary band-0 change issued 2 HID write reports (band payload + enable payload per WriteRawBandAsync); the exact restoration issued 8 band write+enable pairs plus pre-gain and global-gain reports. No flash-save (save:false throughout; no flash-save API invoked); no RECOVERY was necessary; no second experiment was performed.

EXECUTE and RECOVERY were otherwise NOT rerun; this goal performed exactly one controlled volatile write -> verify -> restore -> verify, ending with the DAWN PRO2 returned to the exact Prepared baseline.

### Cycle 57 - apply the user real EQ profile through the approved EXECUTE path (temporary apply -> verify -> restore) (2026-08-17)

The goal authorizes applying the user real DAWN PRO2 EQ profile through the approved production EXECUTE path with existing temporary-apply/verify/restore semantics (no flash-save). The user supplied the profile file C:\Users\mohammed\Desktop\eq\Zero Red oratory1990 target.txt (Preamp: -4 dB; Filter 1 LSQ 100Hz +2.0 Q0.710; 2 PK 140 +3.0 Q0.9; 3 PK 780 +0.6 Q2.0; 4 PK 1350 -1.4 Q1.8; 5 HSQ 3000 0 Q0.710; 6 HSQ 10000 0 Q0.710). The EXECUTE path previously only ever wrote a single auto-generated test band; applying a full profile needed a production extension.

Cycle 57 changes (no weakenings; profile carried inside the session plan, child env contract unchanged):
- PhysicalTransitionPlan gains a nullable HardwareSnapshot? Profile; the four deterministic plan fields (Baseline/Individual/IndividualBand/Bulk) are kept byte-identical so PhysicalSessionStore.Validate recomputation still passes.
- PhysicalTransitionPlanner.CreateProfilePlan(original, EqPreset) builds the profile target = baseline overlaid with the parsed profile bands (FromPeqBand raw payloads) + pre-gain from the profile preamp (Q8.8); validates RestorationProblems fail-closed.
- PhysicalExecuteStep gains ApplyProfile; PhysicalExecuteOrchestrator routes the temporary step to ApplyProfile when Plan.Profile is present, else IndividualBand; the existing full baseline restoration (RestoreOriginalRam) is unchanged.
- DawnPro2PhysicalExecuteActions.WriteProfileAsync writes every profile band (WriteRawBandAsync) + pre-gain + global-gain (save:false) and verifies the complete readback via SnapshotEquals(plan.Profile).
- PREPARE reads MOONDROP_EXECUTE_PROFILE_PATH from its own environment, loads it with the existing validated EqPresetParser (Moondrop.Core), and embeds the profile into the session plan.

Cycle 57 regressions (4): ProfilePlanBuildsTheExactParsedTargetSnapshot, ExecuteOrchestratorAppliesTheProfileTargetThenRestoresToBaseline, ExecuteOrchestratorWithoutAProfileKeepsTheIndividualBandTemporaryStep, SessionValidationAcceptsAPersistedProfilePlan. RED: CS0117 CreateProfilePlan / CS0117 ApplyProfile (compile). GREEN: 4/4. Focused suite: 223 passed (219 + 4).

### Round-17 - Cycle-57 re-qualification (2026-08-17)

Cycle-57 changed production source, invalidating the Round-16 pair. Re-qualified: focused 223, full default-safe 331, hostile-leaked 331, Python 124, Release builds 0 warnings / 0 errors, git diff --check clean; round17-candidate-a + round17-candidate-b byte-identical (60/60 source, 331/331 runner, 192/192 watchdog, byte-identical runtime-manifest.json); live source 0502B76259C7E1FF5D3EE8947269B9187498AFBF6AEF1ECFC006FE2E73018F92 (176/117/59); runtime F884281719A45AA5561FB51D7E07623207F0A9E3EA4D4557C3084C78522DD91B (532/331/192/9). Independent security/topology review: VERDICT GO. Approval populated with 0502B762/F8842817 and verified (exit 0) against both candidates.

A fresh PREPARE was performed with MOONDROP_EXECUTE_PROFILE_PATH = C:\Users\mohammed\Desktop\eq\Zero Red oratory1990 target.txt so the prepared session plan carries the user real profile target (Plan.Profile). EXECUTE then applies that profile as the temporary state, verifies the complete readback equals the profile, and restores the prepared baseline; no flash-save.

### Cycle 58 - profile target raw-format preservation (2026-08-17)

The first profile-apply EXECUTE (session d53d7352) reached the apply step and wrote every profile band + pre-gain + global-gain, but the full readback verification failed on payload FORMAT: the profile target built via HardwareBandSnapshot.FromPeqBand carried the normalized write-form header (payload byte 0 = 1, byte 35 = 7) while the device readback returns the register form (byte 0 = 0x80, byte 35 = the active-EQ marker 9). The watchdog failed closed and its automatic supervised recovery (RestoreOriginalRam) PASSED, restoring the exact prepared baseline - no persistent change, no flash-save.

Cycle 58 RED: ProfilePlanBuildsTheExactParsedTargetSnapshot gained assertions that the profile target band preserves the baseline raw header (payload bytes 0 and 35) - failed with expected 0 / actual 1. GREEN: PhysicalTransitionPlanner.OverlayProfileBands now builds each overlaid band from the baseline band raw template via DawnPro2Protocol.CreateRawBandStateFromTemplate (the exact mechanism the individual-band path uses and that byte-matches the device readback) instead of FromPeqBand. Focused suite 223 remains green.

### Round-18 - Cycle-58 re-qualification and profile-apply EXECUTE (2026-08-17)

Re-qualified after the Cycle-58 source change: focused 223, full default-safe 331, hostile-leaked 331, Python 124, Release builds 0 warnings / 0 errors; round18-candidate-a + round18-candidate-b byte-identical (60/60 source, 331/331 runner, 192/192 watchdog, byte-identical runtime-manifest.json); live source 3255E778C3E84665CD665591B121D6A81FE5044DCFD74909E91E4C00F31DAE08 (176/117/59), runtime 5EF1C9B0449C52EC6895F4361E554D184FA5C84A7CB5135E901F1FBF500FC63F (532/331/192/9). Independent security/topology review: VERDICT GO. Approval populated with 3255E778/5EF1C9B0 and verified (exit 0) against both candidates.

A fresh PREPARE (MOONDROP_EXECUTE_PROFILE_PATH = C:\Users\mohammed\Desktop\eq\Zero Red oratory1990 target.txt) was performed; the prepared session plan carries Plan.Profile = the user real profile target. EXECUTE then applies the full profile, verifies the complete readback equals Plan.Profile, and restores the prepared baseline (no flash-save; watchdog recovery would automatically restore on any failure).

### Round-18 profile-apply EXECUTE - result (2026-08-17)

Invocation: tests-dotnet\artifacts\run-execute.ps1 (watchdog from the session prepare tree; --mode execute; session 4fe0ebe6 / 28c8bc30f8af4035bf99585dc9c2a85b). EXECUTE_EXIT=0; MTP summary Passed (ExecutePreparedDawnPro2PhysicalSessionAsync, ~10 s). Result: Status=passed-after-fresh-full-raw-restoration; RestorationAttempted=true; RestorationVerified=true; no errors. Session Phase=Completed (11).

Phases (both passed):
- "apply user EQ profile target and full readback" - wrote the user real DAWN PRO2 EQ profile (bands 1-6 from C:\Users\mohammed\Desktop\eq\Zero Red oratory1990 target.txt: 100Hz +2 dB LSQ Q0.71; 140Hz +3 dB PK Q0.9; 780Hz +0.6 dB PK Q2.0; 1350Hz -1.4 dB PK Q1.8; 3000Hz 0 dB HSQ; 10000Hz 0 dB HSQ; pre-gain -4 dB) plus pre-gain and global-gain through the approved production write path, then a complete readback verified byte-for-byte equality with the session Plan.Profile (zero differences).
- "exact original raw RAM restoration and readback" - restored the prepared baseline and verified complete equality.

No flash-save (all writes save:false). No RECOVERY triggered (the earlier Cycle-58 fail-closed recovery had already restored baseline; this run succeeded normally). No second experiment. Final device state equals the prepared baseline. The optional official MOONDROP web-UI cross-check was not performed (optional; the harness readback + session baseline are authoritative).

### Personal-test FAIL round - GUI fixes (2026-08-17)

Personal testing of the final WPF executable reported: (1) "Refresh failed: Cannot access a disposed object ... DawnPro2Device"; (2) error banner text overlapping page content; (3) poor button/panel contrast; (4) desire to refresh device state without saving. Root causes and fixes (all in the WPF product path - src/Moondrop.Wpf and src/Moondrop.Hardware MoondropDeviceService only; the approved physical runner/watchdog runtime is NOT linked to MoondropDeviceService, so no new physical candidates/approval/PREPARE are required):
- Reconnect: any transient HID failure poisons the retained DAWN PRO2 device (SendAsync->PoisonTransport->_poisoned), after which every operation threw ObjectDisposedException. MoondropDeviceService now exposes IDawnPro2Device.IsUsable and transparently re-acquires a fresh device via a reconnect factory (registered in SelectAsync) for every operation (refresh/apply/import/pre-gain/global-gain/active-EQ/save/enable), and the GUI refresh (read-only, no save) recovers instead of crashing. New regression: DeviceServiceRefreshTransparentlyReacquiresAPoisonedDawnPro2Device (offline fake-transport test; RED=ObjectDisposedException path reproduced, GREEN).
- Contrast: the Fluent-style brushes (AccentFill/TextFill/ControlFill/CardBackground/CardStroke/ControlStroke/LayerFill/SystemFillCritical) were referenced but never defined, so controls fell back to WPF defaults. Added Themes/Palette.xaml defining the full light palette and merged it before Controls.xaml.
- Overlap: the error banner was a window-level overlay (Panel.ZIndex=20, top margin) that covered the page header and status text. The banner now flows inside the content ScrollViewer above the page grid, so it never overlaps content.
- Refresh-without-save: Refresh was already read-only; it now also appears on the EQ page header, and the reconnect fix makes it reliable.

Verification: focused+hardware+WPF-runtime 288 (incl. new regression), full default-safe 332, hostile-leaked 332, Python 124, Release build 0 warnings / 0 errors, publish to artifacts\user-test with secret scan clean and launch smoke passed. The protected original repository remains untouched.

### Button-theme architecture fix (2026-08-17, personal-test round 2)

Root cause: (1) the shared action buttons (CompactActionButtonStyle/PrimaryActionButtonStyle) carried no template/background/foreground/border, so they fell back to the framework default button look; (2) an earlier static Themes/Palette.xaml shadowed the Fluent theme's adaptive brushes with fixed light values, so under ThemeMode.Dark the theme resolved light TextFill/ControlFill on dark surfaces (near-white buttons with illegible text). Fix: a proper per-theme token architecture - Themes/Light.xaml and Themes/Dark.xaml define the full surface/ink palette plus semantic button tokens (ButtonBackground/Foreground/Border/HoverBackground/Pressed/ FocusBorder/DisabledBackground/DisabledForeground/DisabledBorder); WpfTheme.Apply now swaps the active token dictionary at runtime (Light/Dark resolved, System->registry) and still sets ThemeMode; Controls.xaml gained an implicit Button style with an explicit ControlTemplate and triggers for hover/pressed/keyboard-focus/disabled (disabled declared last so it overrides hover/pressed/focus combinations and never uses hover styles). Keyed styles (Primary/Compact/BandSelect) now BasedOn the shared implicit Button style. Regression: WpfRuntimeTests.ButtonThemeTokensResolveLegiblyForBothLightAndDarkThemes asserts per-theme luminance (light fg dark, light bg light, dark fg light, dark bg dark, no near-white dark button), enabled-vs-disabled token differences, interaction tokens exist, and the shared template setter is present. Moondrop.Wpf gained InternalsVisibleTo("Moondrop.Tests") for the test. These changes are WPF product only; the approved physical runner/watchdog runtime is unaffected.
