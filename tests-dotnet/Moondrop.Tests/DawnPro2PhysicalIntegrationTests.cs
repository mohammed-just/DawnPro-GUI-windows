using Moondrop.Core.Protocol;
using Moondrop.Hardware;
using Moondrop.PhysicalWatchdog;
using System.Runtime.ExceptionServices;

namespace Moondrop.Tests;

[TestClass]
public sealed class DawnPro2PhysicalIntegrationTests
{
    private const int OpenAttempts = 5;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("PhysicalHardwarePrepare")]
    public async Task PrepareDawnPro2PhysicalSessionReadOnlyAsync()
    {
        RequireDedicatedPhysicalRunSettings();
        if (!PhysicalTestGate.IsOptedIn(Environment.GetEnvironmentVariable(PhysicalTestGate.PrepareEnvironmentVariable)))
        {
            Assert.Inconclusive(
                $"Read-only physical preparation is disabled. Set {PhysicalTestGate.PrepareEnvironmentVariable}=1 and filter category PhysicalHardwarePrepare explicitly.");
            return;
        }

        if (!PhysicalRunLock.TryAcquireDefault(out var runLock))
            Assert.Inconclusive("Another DAWN PRO2 physical harness process owns the machine-wide file lock.");

        var frames = new List<DawnPro2HidReadFrame>();
        DawnPro2Device? device = null;
        Exception? failure = null;
        PhysicalArtifactPaths? paths = null;
        PhysicalSessionArtifact? session = null;
        var repositoryRoot = PhysicalArtifactPaths.FindRepositoryRoot();
        var sessionId = Guid.NewGuid().ToString("N");
        var dotnetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
        var freshRuntime = await PhysicalRuntimeBuilder.BuildAsync(
            repositoryRoot,
            dotnetPath,
            sessionId,
            $"prepare-{Guid.NewGuid():N}").ConfigureAwait(false);
        var approvalPath = Path.Combine(
            repositoryRoot,
            PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var initialApproval = PhysicalRuntimeApprovalManifest.ReadStrict(approvalPath);
        PhysicalRuntimeApprovalManifest.RequireMatches(
            initialApproval,
            freshRuntime.SourceFingerprint,
            freshRuntime.RuntimeManifest);
        HarnessBuildFingerprint.RequireCompleteOutputMatches(
            AppContext.BaseDirectory,
            freshRuntime.Plan.PhysicalOutputDirectory,
            "physical-tests");
        var reviewedFingerprint = freshRuntime.SourceFingerprint;
        try
        {
            RequireNoConflictingApps();
            var identity = HidSharpDawnPro2Transport.CaptureSingleIdentity();
            device = await PhysicalDawnPro2DeviceOpener.OpenReadOnlyPrepareWithRetriesAsync(
                identity,
                frames,
                OpenAttempts).ConfigureAwait(false);
            var profilePath = Environment.GetEnvironmentVariable(PhysicalTestGate.ProfilePathEnvironmentVariable);
            Moondrop.Core.Eq.EqPreset? profile = null;
            if (!string.IsNullOrWhiteSpace(profilePath))
                profile = Moondrop.Core.Eq.EqPresetParser.Load(profilePath);
            var preparation = await PhysicalPreparePlanner.ReadAndPlanAsync(device, identity, profile).ConfigureAwait(false);
            var original = preparation.Original;
            var plan = preparation.Plan;

            await device.DisposeAsync().ConfigureAwait(false);
            device = null;
            paths = PhysicalArtifactPaths.Create();
            var finalFingerprint = HarnessBuildFingerprint.CaptureSource(repositoryRoot);
            if (!string.Equals(reviewedFingerprint.AggregateSha256, finalFingerprint.AggregateSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Reviewed harness source/binaries drifted during PREPARE; refusing to publish an executable session.");
            var finalRuntime = HarnessBuildFingerprint.CaptureRuntime(
                repositoryRoot,
                freshRuntime.Plan.PhysicalOutputDirectory,
                freshRuntime.Plan.WatchdogOutputDirectory,
                freshRuntime.Plan.SourceRoot,
                freshRuntime.Plan.MetadataPaths);
            var finalApproval = PhysicalRuntimeApprovalManifest.ReadStrict(approvalPath);
            PhysicalRuntimeApprovalManifest.RequireMatches(finalApproval, finalFingerprint, finalRuntime);
            HarnessBuildFingerprint.RequireCompleteOutputMatches(
                AppContext.BaseDirectory,
                freshRuntime.Plan.PhysicalOutputDirectory,
                "physical-tests");
            session = PhysicalSessionArtifact.Create(
                original,
                plan,
                PhysicalSessionStore.CreateOneRunToken(),
                frames,
                finalFingerprint.AggregateSha256,
                finalRuntime.AggregateSha256,
                sessionId);
            await PhysicalSessionStore.PersistAsync(paths.SessionPath, session).ConfigureAwait(false);
            await PhysicalArtifactWriter.WriteJsonAsync(paths.DiagnosticPath, frames).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            failure = Combine(failure, await TryDisposeAsync(device).ConfigureAwait(false), "Device disposal failed during preparation.");
            try
            {
                runLock!.Dispose();
            }
            catch (Exception ex)
            {
                failure = Combine(failure, ex, "Machine-wide file-lock disposal failed during preparation.");
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        TestContext.WriteLine($"Prepared read-only session: {paths!.SessionPath}");
        TestContext.WriteLine($"Raw HID read-frame diagnostics: {paths.DiagnosticPath}");
        TestContext.WriteLine($"One-run confirmation token: {session!.OneRunToken}");
        TestContext.WriteLine("NO HARDWARE STATE WRITES OR FLASH SAVES WERE PERFORMED BY PREPARE.");
    }

    [TestMethod]
    [TestCategory("PhysicalHardware")]
    public async Task ExecutePreparedDawnPro2PhysicalSessionAsync()
    {
        RequireDedicatedPhysicalRunSettings();
        var watchdogAuthorization = RequireDedicatedWatchdog();
        await PhysicalWatchdogHeartbeat.PulseAsync("PhaseWorkStarting").ConfigureAwait(false);
        var runValue = Environment.GetEnvironmentVariable(PhysicalTestGate.EnvironmentVariable);
        var confirmation = Environment.GetEnvironmentVariable(PhysicalTestGate.ConfirmationEnvironmentVariable);
        var requestedPath = Environment.GetEnvironmentVariable(PhysicalTestGate.SessionPathEnvironmentVariable);
        if (!PhysicalTestGate.IsOptedIn(runValue) || string.IsNullOrEmpty(confirmation) || string.IsNullOrWhiteSpace(requestedPath))
        {
            Assert.Inconclusive(
                $"Physical execute requires {PhysicalTestGate.EnvironmentVariable}=1, {PhysicalTestGate.ConfirmationEnvironmentVariable}=<prepared token>, " +
                $"{PhysicalTestGate.SessionPathEnvironmentVariable}=<prepared session>, and category PhysicalHardware.");
            return;
        }

        var sessionPath = PhysicalSessionStore.ValidateSessionPath(requestedPath);
        var session = await PhysicalSessionStore.LoadValidatedAsync(sessionPath).ConfigureAwait(false);
        if (!PhysicalWatchdogProcessGate.IsSessionOwned(watchdogAuthorization, session))
            Assert.Fail("The loaded execute session does not match the immutable session lineage authenticated by the watchdog; no HID access occurred.");
        RequireLoadedRuntimeMatches(session, watchdogAuthorization);
        if (!PhysicalExecutionGate.IsAuthorized(runValue, confirmation, session.OneRunToken))
        {
            Assert.Inconclusive("The exact one-run confirmation token did not match the prepared session; no HID access occurred.");
            return;
        }
        if (session.Phase != PhysicalSessionPhase.Prepared)
            Assert.Fail($"Execute only accepts a Prepared session; current phase is {session.Phase}. Use recovery when writes may have started.");

        var paths = PhysicalArtifactPaths.FromSessionPath(sessionPath);
        var journal = new PhysicalPhaseJournal(TestContext, session.OneRunToken, confirmation, sessionPath);
        var frames = session.ReadFrames.ToList();
        var startedAt = DateTimeOffset.UtcNow;
        DawnPro2Device? device = null;
        DawnPro2PhysicalExecuteActions? actions = null;
        PhysicalRunLock? runLock = null;
        Exception? failure = null;
        PhysicalExecuteOutcome? outcome = null;

        try
        {
            if (!PhysicalRunLock.TryAcquireDefault(out runLock))
                throw new InvalidOperationException("Another DAWN PRO2 physical harness process owns the machine-wide file lock.");

            RequireNoConflictingApps();
            await PhysicalWatchdogHeartbeat.PulseAsync("NativeOpenStarting").ConfigureAwait(false);
            device = await PhysicalDawnPro2DeviceOpener.OpenWatchdogProtectedWithRetriesAsync(
                session.Original.Identity,
                frames,
                OpenAttempts,
                WatchdogProtectedPhysicalPhase.Execute,
                watchdogAuthorization,
                PhysicalArtifactPaths.FindRepositoryRoot()).ConfigureAwait(false);
            await PhysicalWatchdogHeartbeat.PulseAsync("PhaseWorkStarting").ConfigureAwait(false);
            var current = await HardwareSnapshotReader.ReadConsistentAsync(device, session.Original.Identity).ConfigureAwait(false);
            PhysicalAssertions.SnapshotEquals(session.Original, current, "execute preflight snapshot");
            _ = PhysicalTransitionPlanner.Create(session.Original); // Revalidate every transition before the first write.
            RequireNoConflictingApps(); // Deliberately immediately before the first state-changing report.
            actions = new DawnPro2PhysicalExecuteActions(
                device,
                session,
                frames,
                journal,
                WatchdogProtectedPhysicalPhase.Execute,
                watchdogAuthorization,
                PhysicalArtifactPaths.FindRepositoryRoot());
            device = null;
            outcome = await PhysicalExecuteOrchestrator.RunAsync(
                session,
                state => PhysicalSessionStore.PersistAsync(sessionPath, state with { ReadFrames = frames.ToArray() }),
                actions,
                sessionPath,
                confirmation).ConfigureAwait(false);
            session = outcome.Session;
            failure = Combine(outcome.PrimaryError, outcome.RestorationError, "Physical execute failed and restoration also reported an error.");
        }
        catch (Exception ex)
        {
            failure = Combine(failure, ex, "Physical execute orchestration failed before returning an outcome.");
        }
        finally
        {
            if (actions?.Device is not null || device is not null)
                await PhysicalWatchdogHeartbeat.PulseAsync("NativeDisposeStarting").ConfigureAwait(false);
            failure = Combine(failure, await TryDisposeAsync(actions?.Device).ConfigureAwait(false), "Orchestrated device disposal failed.");
            failure = Combine(failure, await TryDisposeAsync(device).ConfigureAwait(false), "Device disposal failed.");
            if (runLock is not null)
            {
                try
                {
                    runLock.Dispose();
                }
                catch (Exception ex)
                {
                    failure = Combine(failure, ex, "Machine-wide file-lock disposal failed.");
                }
            }
        }

        await PhysicalArtifactWriter.WriteJsonAsync(paths.DiagnosticPath, frames).ConfigureAwait(false);
        var result = new PhysicalTestResult(
            nameof(ExecutePreparedDawnPro2PhysicalSessionAsync),
            startedAt,
            DateTimeOffset.UtcNow,
            failure is null
                ? "passed-after-fresh-full-raw-restoration"
                : outcome?.RestorationVerified == true
                    ? "failed-restoration-verified"
                    : "failed-recovery-required",
            sessionPath,
            outcome?.RestorationAttempted ?? false,
            outcome?.RestorationVerified ?? false,
            PhysicalDurableDiagnostic.FromException(outcome?.PrimaryError ?? failure, session.OneRunToken, confirmation, sessionPath),
            PhysicalDurableDiagnostic.FromException(outcome?.RestorationError, session.OneRunToken, confirmation, sessionPath),
            journal.Phases);
        await PhysicalArtifactWriter.WriteResultAsync(paths.ResultPath, result).ConfigureAwait(false);

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    [TestCategory("PhysicalHardwareRecovery")]
    public async Task RecoverDawnPro2FromPreparedSnapshotAsync()
    {
        RequireDedicatedPhysicalRunSettings();
        var watchdogAuthorization = RequireDedicatedWatchdog();
        await PhysicalWatchdogHeartbeat.PulseAsync("PhaseWorkStarting").ConfigureAwait(false);
        var optIn = Environment.GetEnvironmentVariable(PhysicalTestGate.RecoveryEnvironmentVariable);
        var requestedPath = Environment.GetEnvironmentVariable(PhysicalTestGate.RecoverySnapshotEnvironmentVariable);
        if (!PhysicalTestGate.IsOptedIn(optIn) || string.IsNullOrWhiteSpace(requestedPath))
        {
            Assert.Inconclusive(
                $"Recovery requires {PhysicalTestGate.RecoveryEnvironmentVariable}=1, " +
                $"{PhysicalTestGate.RecoverySnapshotEnvironmentVariable}=<session snapshot>, and category PhysicalHardwareRecovery.");
            return;
        }

        var sessionPath = PhysicalRecoveryGate.Validate(optIn, requestedPath, PhysicalArtifactPaths.HardwareSnapshotsRoot);
        var session = await PhysicalSessionStore.LoadValidatedAsync(sessionPath).ConfigureAwait(false);
        if (!PhysicalWatchdogProcessGate.IsSessionOwned(watchdogAuthorization, session))
            Assert.Fail("The loaded recovery session does not match the immutable session lineage authenticated by the watchdog; no HID access occurred.");
        RequireLoadedRuntimeMatches(session, watchdogAuthorization);
        if (session.Phase == PhysicalSessionPhase.Prepared)
            Assert.Fail("Recovery is not permitted for Prepared; no temporary write is recorded as outstanding.");
        if (session.Phase == PhysicalSessionPhase.Completed)
        {
            TestContext.WriteLine("Recovery no-op: this session is already Completed; no HID access or flash save occurred.");
            return;
        }

        if (!PhysicalRunLock.TryAcquireDefault(out var runLock))
            Assert.Fail("Another DAWN PRO2 physical harness process owns the machine-wide file lock.");

        var frames = session.ReadFrames.ToList();
        var journal = new PhysicalPhaseJournal(TestContext, session.OneRunToken, sessionPath);
        DawnPro2Device? device = null;
        DawnPro2PhysicalExecuteActions? actions = null;
        Exception? failure = null;
        try
        {
            RequireNoConflictingApps();
            await PhysicalWatchdogHeartbeat.PulseAsync("NativeOpenStarting").ConfigureAwait(false);
            device = await PhysicalDawnPro2DeviceOpener.OpenWatchdogProtectedWithRetriesAsync(
                session.Original.Identity,
                frames,
                OpenAttempts,
                WatchdogProtectedPhysicalPhase.Recovery,
                watchdogAuthorization,
                PhysicalArtifactPaths.FindRepositoryRoot()).ConfigureAwait(false);
            await PhysicalWatchdogHeartbeat.PulseAsync("PhaseWorkStarting").ConfigureAwait(false);
            var current = await HardwareSnapshotReader.ReadConsistentAsync(device, session.Original.Identity).ConfigureAwait(false);
            PhysicalRecoveryCompatibility.RequireReachable(session, current);
            RequireNoConflictingApps();
            actions = new DawnPro2PhysicalExecuteActions(
                device,
                session,
                frames,
                journal,
                WatchdogProtectedPhysicalPhase.Recovery,
                watchdogAuthorization,
                PhysicalArtifactPaths.FindRepositoryRoot());
            device = null;

            session = await PhysicalRecoveryOrchestrator.RunAsync(
                session,
                current,
                async state =>
                {
                    session = state with { ReadFrames = frames.ToArray() };
                    await PhysicalSessionStore.PersistAsync(sessionPath, session).ConfigureAwait(false);
                },
                actions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (PhysicalSessionStateMachine.CanTransition(session.Phase, PhysicalSessionPhase.Failed))
            {
                try
                {
                    session = await AdvanceAsync(
                        sessionPath,
                        session,
                        PhysicalSessionPhase.Failed,
                        frames,
                        PhysicalDurableDiagnostic.FromException(ex, session.OneRunToken, sessionPath)).ConfigureAwait(false);
                }
                catch (Exception persistFailure)
                {
                    failure = Combine(failure, persistFailure, "Could not persist recovery failure.");
                }
            }
        }
        finally
        {
            if (actions?.Device is not null || device is not null)
                await PhysicalWatchdogHeartbeat.PulseAsync("NativeDisposeStarting").ConfigureAwait(false);
            failure = Combine(failure, await TryDisposeAsync(actions?.Device).ConfigureAwait(false), "Recovery orchestrated device disposal failed.");
            failure = Combine(failure, await TryDisposeAsync(device).ConfigureAwait(false), "Recovery device disposal failed.");
            try
            {
                runLock!.Dispose();
            }
            catch (Exception ex)
            {
                failure = Combine(failure, ex, "Recovery file-lock disposal failed.");
            }
        }

        var paths = PhysicalArtifactPaths.FromSessionPath(sessionPath);
        await PhysicalArtifactWriter.WriteJsonAsync(paths.DiagnosticPath, frames).ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class DawnPro2PhysicalExecuteActions(
        DawnPro2Device device,
        PhysicalSessionArtifact session,
        List<DawnPro2HidReadFrame> frames,
        PhysicalPhaseJournal journal,
        WatchdogProtectedPhysicalPhase watchdogPhase,
        PhysicalWatchdogAuthorization watchdogAuthorization,
        string repositoryRoot) : IPhysicalExecuteActions
    {
        public DawnPro2Device? Device { get; private set; } = device;

        public Task RunAsync(PhysicalExecuteStep step) => step switch
        {
            PhysicalExecuteStep.IndividualBand => journal.RunAsync("individual coefficient-relevant band command path", WriteIndividualBandAsync),
            PhysicalExecuteStep.ApplyProfile => journal.RunAsync("apply user EQ profile target and full readback", WriteProfileAsync),
            PhysicalExecuteStep.RestoreOriginalRam => journal.RunAsync("exact original raw RAM restoration and readback", RestoreOriginalRamAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };

        private async Task WriteIndividualBandAsync()
        {
            var current = RequireDevice();
            await current.WriteRawBandAsync(session.Plan.IndividualBand.ToRawState()).ConfigureAwait(false);
            var actual = HardwareBandSnapshot.FromRawState(
                await current.ReadRawBandAsync(session.Plan.IndividualBand.Index).ConfigureAwait(false));
            PhysicalAssertions.BandEquals(session.Plan.IndividualBand, actual, "individual raw band readback");
        }

        private async Task WriteProfileAsync()
        {
            var current = RequireDevice();
            var profile = session.Plan.Profile
                          ?? throw new InvalidOperationException("The execute session carries no user EQ profile target to apply.");
            foreach (var band in profile.Bands)
                await current.WriteRawBandAsync(band.ToRawState()).ConfigureAwait(false);
            await current.WritePreGainAsync(profile.PreGain, save: false).ConfigureAwait(false);
            await current.WriteGlobalGainAsync(profile.GlobalGain, save: false).ConfigureAwait(false);
            var actual = await HardwareSnapshotReader.ReadConsistentAsync(current, profile.Identity).ConfigureAwait(false);
            PhysicalAssertions.SnapshotEquals(profile, actual, "user EQ profile target readback");
        }

        private async Task RestoreOriginalRamAsync()
        {
            if (Device is null)
            {
                await PhysicalWatchdogHeartbeat.PulseAsync("NativeOpenStarting").ConfigureAwait(false);
                Device = await PhysicalDawnPro2DeviceOpener.OpenWatchdogProtectedWithRetriesAsync(
                    session.Original.Identity,
                    frames,
                    OpenAttempts,
                    watchdogPhase,
                    watchdogAuthorization,
                    repositoryRoot).ConfigureAwait(false);
                await PhysicalWatchdogHeartbeat.PulseAsync("PhaseWorkStarting").ConfigureAwait(false);
            }
            await RestoreOriginalToRamAsync(Device, session.Original).ConfigureAwait(false);
        }

        private DawnPro2Device RequireDevice() =>
            Device ?? throw new InvalidOperationException("The pinned DAWN PRO2 stream is not open for this physical stage.");
    }

    private static async Task RestoreOriginalToRamAsync(DawnPro2Device device, HardwareSnapshot original)
    {
        // Restore captured raw PEQ bands and gains. Active EQ is read-only harness state: the fresh
        // complete comparison below must prove it stayed at the captured raw value (9 for FW 1.5).
        await device.WriteAllRawBandsAsync(original.Bands.Select(band => band.ToRawState()).ToArray()).ConfigureAwait(false);
        await device.WritePreGainAsync(original.PreGain, save: false).ConfigureAwait(false);
        await device.WriteGlobalGainAsync(original.GlobalGain, save: false).ConfigureAwait(false);
        var restored = await HardwareSnapshotReader.ReadConsistentAsync(device, original.Identity).ConfigureAwait(false);
        PhysicalAssertions.SnapshotEquals(original, restored, "fresh byte-equivalent raw restoration readback");
    }

    private void RequireDedicatedPhysicalRunSettings()
    {
        TestContext.Properties.TryGetValue(PhysicalRunSettingsGate.ParameterName, out var marker);
        if (!PhysicalRunSettingsGate.IsDedicated(
                marker?.ToString(),
                Environment.GetEnvironmentVariable(PhysicalTestGate.PrepareEnvironmentVariable),
                Environment.GetEnvironmentVariable(PhysicalTestGate.EnvironmentVariable),
                Environment.GetEnvironmentVariable(PhysicalTestGate.RecoveryEnvironmentVariable)))
        {
            Assert.Inconclusive(
                "Physical harness tests require the dedicated tests-dotnet/physical.runsettings marker in addition to exact opt-in variables and an explicit category filter.");
        }
    }

    private static PhysicalWatchdogAuthorization RequireDedicatedWatchdog()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var authorization = PhysicalWatchdogAuthorization.FromEnvironment();
        var result = PhysicalWatchdogProcessGate.Evaluate(authorization, root);
        if (!result.IsAuthorized)
        {
            Assert.Fail($"Execute and recovery require the authenticated direct-parent Moondrop.PhysicalWatchdog Release process; raw dotnet test is rejected. {result.Diagnostic}");
        }
        return authorization!;
    }

    private static void RequireLoadedRuntimeMatches(
        PhysicalSessionArtifact session,
        PhysicalWatchdogAuthorization authorization)
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var source = HarnessBuildFingerprint.CaptureSource(root);
        if (!string.Equals(source.AggregateSha256, session.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Loaded physical child source does not match the prepared approved source fingerprint.");
        var metadata = PhysicalRuntimeBuildPlan.Create(root, session.SessionId, "verification").MetadataPaths;
        var runtime = HarnessBuildFingerprint.CaptureRuntime(
            root,
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Path.GetFullPath(authorization.ParentExecutablePath))!,
            metadata);
        if (!string.Equals(runtime.AggregateSha256, session.RuntimeManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Loaded physical child runtime does not match the prepared complete runtime manifest.");
        var approval = PhysicalRuntimeApprovalManifest.ReadStrict(Path.Combine(
            root,
            PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        PhysicalRuntimeApprovalManifest.RequireMatches(approval, source, runtime);
        PhysicalRuntimeApprovalManifest.RequireSessionHashes(
            approval,
            session.SourceFingerprint,
            session.RuntimeManifestSha256);
    }

    private static async Task<PhysicalSessionArtifact> AdvanceAsync(
        string path,
        PhysicalSessionArtifact session,
        PhysicalSessionPhase phase,
        IReadOnlyList<DawnPro2HidReadFrame> frames,
        string? error = null)
    {
        var advanced = session.Advance(phase, frames, error);
        await PhysicalSessionStore.PersistAsync(path, advanced).ConfigureAwait(false);
        return advanced;
    }

    private static void RequireNoConflictingApps()
    {
        var conflicts = PhysicalProcessGuard.FindConflictingApps();
        if (conflicts.Count != 0)
            throw new InvalidOperationException($"Close all recognized Moondrop clients before testing: {string.Join(", ", conflicts)}");
    }

    private static void RequireRestorable(HardwareSnapshot snapshot, string context)
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(snapshot);
        if (problems.Count != 0)
            throw new InvalidOperationException($"{context} is not completely restorable: {string.Join("; ", problems)}");
    }

    private static async Task<Exception?> TryDisposeAsync(DawnPro2Device? device)
    {
        if (device is null)
            return null;
        try
        {
            await device.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? Combine(Exception? primary, Exception? additional, string message)
    {
        if (additional is null)
            return primary;
        return primary is null
            ? new InvalidOperationException(message, additional)
            : new AggregateException(message, primary, additional);
    }
}
