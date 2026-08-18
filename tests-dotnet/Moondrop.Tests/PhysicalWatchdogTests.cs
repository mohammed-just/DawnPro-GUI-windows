using Moondrop.PhysicalWatchdog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moondrop.Tests;

[TestClass]
public sealed class PhysicalWatchdogTests
{
    [TestMethod]
    public void OfflineTopologyIdentityCaptureRejectsPidReuseAndMidReadDrift()
    {
        var stable = new PhysicalProbeProcessIdentity(
            401,
            400,
            DateTimeOffset.Parse("2026-08-09T09:00:01Z"),
            @"C:\candidate\physical-tests\Moondrop.PhysicalTests.exe",
            new string('A', 64));
        var drifted = stable with
        {
            ParentProcessId = 999,
            StartedAtUtc = stable.StartedAtUtc.AddSeconds(1),
            ExecutablePath = @"C:\candidate\physical-tests\replacement.exe",
            Sha256 = new string('B', 64)
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(() =>
            new CoherentPhysicalProbeProcessIdentityProvider(
                new SequenceProbeIdentitySnapshotReader(stable, drifted)).Get(stable.ProcessId));

        StringAssert.Contains(error.Message, "drift");
    }

    [TestMethod]
    public void WatchdogTerminationIdentityCaptureRejectsPidReuseAndMidReadDrift()
    {
        var stable = new ObservedPhysicalProcess(
            4242,
            DateTimeOffset.Parse("2026-08-09T09:00:01Z"),
            @"C:\candidate\physical-tests\Moondrop.PhysicalTests.exe --filter exact");
        var drifted = stable with
        {
            StartedAtUtc = stable.StartedAtUtc.AddSeconds(1),
            CommandLine = @"C:\candidate\physical-tests\replacement.exe --filter other"
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(() =>
            new CoherentObservedPhysicalProcessProvider(
                new SequenceObservedPhysicalProcessSnapshotReader(stable, drifted)).Get(stable.ProcessId));

        StringAssert.Contains(error.Message, "drift");
    }

    [TestMethod]
    public void ProductionWatchdogObservesAnotherRealSuspendedProcessIdentityUnderNet10()
    {
        // Regression: the watchdog must authenticate its freshly created SUSPENDED child before it is
        // allowed to execute. The reader must therefore work against another real process under the
        // same .NET 10 runtime the watchdog uses. The historical dynamic-COM WMI implementation
        // deterministically threw COMException 0x80004005 here (querying another process's
        // Win32_Process row), blocking EXECUTE before any device access.
        var marker = $"dsh-observe-regression-{Guid.NewGuid():N}";
        var commandLine = $"cmd.exe /c echo {marker} & ping -n 3 127.0.0.1 >nul";
        var startup = new SuspendedStartupInfo { cb = Marshal.SizeOf<SuspendedStartupInfo>() };
        if (!CreateSuspendedProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspendedFlag, IntPtr.Zero, null, ref startup, out var processInformation))
            Assert.Fail($"Could not spawn the suspended regression child (Win32 error {Marshal.GetLastWin32Error()}).");
        var childPid = processInformation.dwProcessId;
        try
        {
            DateTimeOffset expectedStart;
            try
            {
                using var oracle = Process.GetProcessById(childPid);
                expectedStart = oracle.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                Assert.Fail($"The suspended regression child could not be read by the oracle: {ex.GetType().Name}.");
                return;
            }

            // Production entry semantics: two coherent reads through the shared provider.
            var observed = new CoherentObservedPhysicalProcessProvider(
                new WindowsObservedPhysicalProcessSnapshotReader()).Get(childPid);

            Assert.AreEqual(childPid, observed.ProcessId, "The reader must report the exact requested PID.");
            Assert.AreEqual(expectedStart, observed.StartedAtUtc, "The reader must report the exact process creation identity.");
            StringAssert.Contains(observed.CommandLine, marker, "The reader must report the exact child command line.");
        }
        finally
        {
            _ = ResumeThread(processInformation.hThread);
            _ = TerminateProcess(processInformation.hProcess, 0);
            _ = CloseHandle(processInformation.hProcess);
            _ = CloseHandle(processInformation.hThread);
        }
    }

    [TestMethod]
    public void WindowsObservedPhysicalProcessSnapshotReaderFailsClosedForMissingProcess()
    {
        // A nonexistent PID must fail closed (no identity is ever fabricated).
        var reader = new WindowsObservedPhysicalProcessSnapshotReader();
        var error = AssertEx.ThrowsException<InvalidOperationException>(() => reader.Read(int.MaxValue));
        StringAssert.Contains(error.Message, "disappeared or could not be opened");
    }

    private const uint CreateSuspendedFlag = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SuspendedStartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SuspendedProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSuspendedProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref SuspendedStartupInfo startupInfo,
        out SuspendedProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [TestMethod]
    public void CandidateTopologyWatchdogLaunchDropsEveryHostileAmbientVariable()
    {
        var ambient = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name)!,
            StringComparer.OrdinalIgnoreCase);
        ambient["DOTNET_STARTUP_HOOKS"] = @"C:\hostile\startup-hook.dll";
        ambient["CORECLR_PROFILER"] = "hostile-profiler";
        ambient["MSBUILD_EXE_PATH"] = @"C:\hostile\msbuild.exe";
        ambient["MOONDROP_RUN_PHYSICAL_TESTS"] = "1";
        ambient["ARBITRARY_SECRET"] = "must-not-cross";

        var launch = PhysicalRuntimeBuilder.CreateOfflineTopologyWatchdogLaunchPlan(
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate\physical-tests\Moondrop.PhysicalTests.exe",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate\watchdog\Moondrop.PhysicalWatchdog.exe",
            new string('A', 64),
            ambient);

        CollectionAssert.AreEquivalent(
            new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" },
            launch.Environment.Keys.ToArray());
        Assert.IsFalse(launch.Environment.Keys.Any(name => name.StartsWith("MOONDROP_", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CandidateTopologyEnvironmentRequiresCurrentExistingNonReparseWindowsPaths()
    {
        var ambient = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name)!,
            StringComparer.OrdinalIgnoreCase);
        var wrongWindows = new Dictionary<string, string>(ambient, StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = ambient["TEMP"],
            ["WINDIR"] = ambient["TEMP"]
        };
        var missingTemp = new Dictionary<string, string>(ambient, StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")
        };

        AssertEx.ThrowsException<InvalidDataException>(() => CreateCandidateTopologyPlan(wrongWindows));
        AssertEx.ThrowsException<InvalidDataException>(() => CreateCandidateTopologyPlan(missingTemp));
    }

    private static PhysicalProcessLaunchPlan CreateCandidateTopologyPlan(IReadOnlyDictionary<string, string> ambient) =>
        PhysicalRuntimeBuilder.CreateOfflineTopologyWatchdogLaunchPlan(
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate\physical-tests\Moondrop.PhysicalTests.exe",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\candidate\watchdog\Moondrop.PhysicalWatchdog.exe",
            new string('A', 64),
            ambient);

    [TestMethod]
    public async Task CandidateTopologyCancellationKillsAndAwaitsTheEntireStartedProcessTree()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var childPidFile = Path.Combine(Path.GetTempPath(), $"moondrop-child-pid-{Guid.NewGuid():N}.txt");
        var launch = new PhysicalProcessLaunchPlan(
            Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.GetTempPath(),
            ["-NoProfile", "-Command", $"$p=Start-Process ping.exe -ArgumentList '127.0.0.1','-n','60' -PassThru; Set-Content -LiteralPath '{childPidFile}' -Value $p.Id; Wait-Process -Id $p.Id"],
            new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                name => name,
                name => Environment.GetEnvironmentVariable(name)!,
                StringComparer.Ordinal),
            RedirectStandardOutput: true,
            RedirectStandardError: true);
        using var cancellation = new CancellationTokenSource();
        var startedPid = 0;
        var childPid = 0;

        try
        {
            await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() =>
            PhysicalProcessLauncher.RunToExitInKillOnCloseJobAsync(
                launch,
                cancellation.Token,
                processId =>
                {
                    startedPid = processId;
                    Assert.IsTrue(SpinWait.SpinUntil(() =>
                    {
                        try
                        {
                            childPid = int.Parse(File.ReadAllText(childPidFile), System.Globalization.CultureInfo.InvariantCulture);
                            return true;
                        }
                        catch (IOException) { return false; }
                    }, TimeSpan.FromSeconds(10)));
                    cancellation.Cancel();
                }));

            Assert.AreNotEqual(0, startedPid);
            Assert.AreNotEqual(0, childPid);
            AssertEx.ThrowsException<ArgumentException>(() => Process.GetProcessById(startedPid));
            AssertEx.ThrowsException<ArgumentException>(() => Process.GetProcessById(childPid));
        }
        finally
        {
            if (File.Exists(childPidFile))
                File.Delete(childPidFile);
        }
    }

    [TestMethod]
    public async Task SupervisedChildCannotExecuteBeforeItsJobOwnershipCallbackCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-suspended-start-{Guid.NewGuid():N}");
        var marker = Path.Combine(root, "executed.txt");
        Directory.CreateDirectory(root);
        try
        {
            var escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
            var launch = new PhysicalProcessLaunchPlan(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                root,
                ["-NoProfile", "-Command", $"Set-Content -LiteralPath '{escapedMarker}' -Value executed"],
                new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                    name => name,
                    name => Environment.GetEnvironmentVariable(name)!,
                    StringComparer.Ordinal));
            var ownershipCallbackCompleted = false;

            using var owned = PhysicalProcessLauncher.StartOwnedSuspended(
                launch,
                _ =>
                {
                    Assert.IsFalse(File.Exists(marker));
                    Thread.Sleep(100);
                    Assert.IsFalse(File.Exists(marker));
                    ownershipCallbackCompleted = true;
                });

            Assert.IsTrue(ownershipCallbackCompleted);
            await owned.Process.WaitForExitAsync();
            await owned.Job.TerminateRemainingAndRequireEmptyAsync();
            Assert.AreEqual(0, owned.Process.ExitCode);
            Assert.IsTrue(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SuspendedLaunchCallbackFailureTerminatesBeforeTheChildCanExecute()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-suspended-failure-{Guid.NewGuid():N}");
        var marker = Path.Combine(root, "must-not-exist.txt");
        Directory.CreateDirectory(root);
        var processId = 0;
        try
        {
            var escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
            var launch = new PhysicalProcessLaunchPlan(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                root,
                ["-NoProfile", "-Command", $"Set-Content -LiteralPath '{escapedMarker}' -Value executed"],
                new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                    name => name,
                    name => Environment.GetEnvironmentVariable(name)!,
                    StringComparer.Ordinal));

            AssertEx.ThrowsException<InvalidOperationException>(() =>
                PhysicalProcessLauncher.StartOwnedSuspended(
                    launch,
                    id =>
                    {
                        processId = id;
                        throw new InvalidOperationException("injected suspended identity failure");
                    }));

            Assert.AreNotEqual(0, processId);
            AssertEx.ThrowsException<ArgumentException>(() => Process.GetProcessById(processId));
            Assert.IsFalse(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SuspendedLaunchManagedProcessAcquisitionFailureTerminatesBeforeTheChildCanExecute()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-suspended-acquisition-{Guid.NewGuid():N}");
        var marker = Path.Combine(root, "must-not-exist.txt");
        Directory.CreateDirectory(root);
        var processId = 0;
        try
        {
            var escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
            var launch = new PhysicalProcessLaunchPlan(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                root,
                ["-NoProfile", "-Command", $"Set-Content -LiteralPath '{escapedMarker}' -Value executed"],
                new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                    name => name,
                    name => Environment.GetEnvironmentVariable(name)!,
                    StringComparer.Ordinal));

            AssertEx.ThrowsException<InvalidOperationException>(() =>
                PhysicalProcessLauncher.StartOwnedSuspended(
                    launch,
                    processResolver: id =>
                    {
                        processId = id;
                        throw new InvalidOperationException("injected managed process acquisition failure");
                    }));

            Assert.AreNotEqual(0, processId);
            AssertEx.ThrowsException<ArgumentException>(() => Process.GetProcessById(processId));
            Assert.IsFalse(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task BoundedRootExitProofForcesKnownRootTermination()
    {
        var launch = new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false
        };
        launch.ArgumentList.Add("-NoProfile");
        launch.ArgumentList.Add("-Command");
        launch.ArgumentList.Add("Start-Sleep -Seconds 60");
        using var process = Process.Start(launch)!;
        var processId = process.Id;

        await PhysicalProcessLauncher.RequireBoundedRootExitAsync(process, TimeSpan.FromMilliseconds(100));

        AssertEx.ThrowsException<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [TestMethod]
    public void OfflineTopologyTrxAcceptsMtpShortResultNameWhenDefinitionProvesExpectedFqn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-mtp-shape-{Guid.NewGuid():N}");
        var trx = Path.Combine(root, "offline-topology.trx");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                trx,
                """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testId="topology-test-id" testName="PublishedRunnerCapturesAuthenticatedParentTopology" outcome="Passed" />
                  </Results>
                  <TestDefinitions>
                    <UnitTest id="topology-test-id" name="PublishedRunnerCapturesAuthenticatedParentTopology">
                      <TestMethod className="Moondrop.PhysicalTests.OfflineTopologyProbeTests" name="PublishedRunnerCapturesAuthenticatedParentTopology" />
                    </UnitTest>
                  </TestDefinitions>
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """);

            PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                trx,
                PhysicalOfflineTopologyProbe.ExactMtpTestName,
                expectedOutcome: "Passed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxRejectsBlankOrOutOfScopeTestIdBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-mtp-binding-{Guid.NewGuid():N}");
        var trx = Path.Combine(root, "offline-topology.trx");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                trx,
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testId="" testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                  </Results>
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """);

            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                    trx,
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    expectedOutcome: "Passed"));

            File.WriteAllText(
                trx,
                """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testId="topology-test-id" testName="PublishedRunnerCapturesAuthenticatedParentTopology" outcome="Passed" />
                  </Results>
                  <UntrustedExtension>
                    <UnitTest id="topology-test-id">
                      <TestMethod className="Moondrop.PhysicalTests.OfflineTopologyProbeTests" name="PublishedRunnerCapturesAuthenticatedParentTopology" />
                    </UnitTest>
                  </UntrustedExtension>
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """);

            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                    trx,
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    expectedOutcome: "Passed"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxRejectsNestedOrWrongNamespaceMtpIdentitySubtrees()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-mtp-schema-{Guid.NewGuid():N}");
        var trx = Path.Combine(root, "offline-topology.trx");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                trx,
                """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <UntrustedExtension>
                    <Results>
                      <UnitTestResult testId="topology-test-id" testName="PublishedRunnerCapturesAuthenticatedParentTopology" outcome="Passed" />
                    </Results>
                    <TestDefinitions>
                      <UnitTest id="topology-test-id">
                        <TestMethod className="Moondrop.PhysicalTests.OfflineTopologyProbeTests" name="PublishedRunnerCapturesAuthenticatedParentTopology" />
                      </UnitTest>
                    </TestDefinitions>
                    <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                  </UntrustedExtension>
                </TestRun>
                """);

            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                    trx,
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    expectedOutcome: "Passed"));

            File.WriteAllText(
                trx,
                """
                <TestRun xmlns="urn:forged-trx">
                  <Results>
                    <UnitTestResult testId="topology-test-id" testName="PublishedRunnerCapturesAuthenticatedParentTopology" outcome="Passed" />
                  </Results>
                  <TestDefinitions>
                    <UnitTest id="topology-test-id">
                      <TestMethod className="Moondrop.PhysicalTests.OfflineTopologyProbeTests" name="PublishedRunnerCapturesAuthenticatedParentTopology" />
                    </UnitTest>
                  </TestDefinitions>
                  <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                </TestRun>
                """);

            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                    trx,
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    expectedOutcome: "Passed"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxRequiresExactlyOneExpectedExecutedMtpTest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-{Guid.NewGuid():N}");
        var trx = Path.Combine(root, "offline-topology.trx");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                trx,
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """);

            PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                trx,
                PhysicalOfflineTopologyProbe.ExactMtpTestName,
                expectedOutcome: "Passed");

            File.WriteAllText(
                trx,
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                    <UnitTestResult testName="Moondrop.Tests.DawnPro2PhysicalIntegrationTests.PrepareDawnPro2PhysicalSessionReadOnlyAsync" outcome="NotExecuted" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Completed">
                    <Counters total="2" executed="1" passed="1" failed="0" notExecuted="1" />
                  </ResultSummary>
                </TestRun>
                """);

            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.RequireExactlyOneMtpTest(
                    trx,
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    expectedOutcome: "Passed"));
            StringAssert.Contains(error.Message, "exactly one");

            var launch = PhysicalOfflineTopologyProbe.CreateMtpRunnerStartInfo(
                @"C:\probe\physical-tests\Moondrop.PhysicalTests.exe",
                @"C:\probe\offline-topology\observed-topology.json",
                new PhysicalProbeProcessIdentity(400, 100, DateTimeOffset.Parse("2026-08-09T09:00:00Z"), @"C:\probe\watchdog\Moondrop.PhysicalWatchdog.exe", new string('A', 64)),
                new HarnessFingerprint("SHA-256", new string('C', 64),
                [
                    new HarnessFingerprintEntry("physical-tests/Moondrop.PhysicalTests.exe", new string('B', 64)),
                    new HarnessFingerprintEntry("watchdog/Moondrop.PhysicalWatchdog.exe", new string('A', 64))
                ]),
                new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                    name => name,
                    name => Environment.GetEnvironmentVariable(name)!,
                    StringComparer.Ordinal));
            CollectionAssert.Contains(launch.ArgumentList.ToArray(), "--minimum-expected-tests");
            CollectionAssert.Contains(launch.ArgumentList.ToArray(), "--report-trx");
            CollectionAssert.Contains(launch.ArgumentList.ToArray(), "--report-trx-filename");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxEvidenceRejectsPreexistingOrAdditionalResultFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-boundary-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        var results = Path.Combine(root, "mtp-results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "stale.trx"), "stale");
        try
        {
            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.PrepareMtpEvidence(report));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxEvidenceRejectsASecondFileCreatedAfterParsing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-race-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        Directory.CreateDirectory(root);
        try
        {
            using var evidence = PhysicalOfflineTopologyProbe.PrepareMtpEvidence(report);
            File.WriteAllText(
                evidence.TrxPath,
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """);

            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                evidence.RequireExactlyOne(
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    "Passed",
                    () => File.WriteAllText(Path.Combine(root, "mtp-results", "late.trx"), "late")));

            StringAssert.Contains(error.Message, "changed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxWaitsForLateTrxTargetWithinBound()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-late-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        Directory.CreateDirectory(root);
        try
        {
            using var evidence = PhysicalOfflineTopologyProbe.PrepareMtpEvidence(report);
            var trxText =
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """;
            var writer = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                File.WriteAllText(evidence.TrxPath, trxText);
            });

            evidence.RequireExactlyOne(PhysicalOfflineTopologyProbe.ExactMtpTestName, "Passed");
            writer.GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyTrxStrictLeaseRetriesTransientMissingTargetWithinBound()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-lease-retry-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        Directory.CreateDirectory(root);
        try
        {
            using var evidence = PhysicalOfflineTopologyProbe.PrepareMtpEvidence(report);
            var trxText =
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Passed" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Completed">
                    <Counters total="1" executed="1" passed="1" failed="0" />
                  </ResultSummary>
                </TestRun>
                """;
            File.WriteAllText(evidence.TrxPath, trxText);
            var attempts = 0;
            var writer = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                File.WriteAllText(evidence.TrxPath, trxText);
            });

            evidence.RequireExactlyOne(
                PhysicalOfflineTopologyProbe.ExactMtpTestName,
                "Passed",
                beforeTrxLeaseAttempt: () =>
                {
                    attempts++;
                    if (attempts == 1)
                        File.Delete(evidence.TrxPath);
                });
            writer.GetAwaiter().GetResult();
            Assert.IsGreaterThanOrEqualTo(2, attempts, $"strict-existing TRX lease must retry a transiently missing target; attempts={attempts}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfflineTopologyWrapperEvidenceAllowsOnlyTheMtpDeploymentDirectoryBesideItsAuthoritativeTrx()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-trx-deployment-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        Directory.CreateDirectory(root);
        try
        {
            using var evidence = PhysicalOfflineTopologyProbe.PrepareMtpEvidence(report);
            File.WriteAllText(
                evidence.TrxPath,
                $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="{PhysicalOfflineTopologyProbe.ExactMtpTestName}" outcome="Failed" />
                  </Results>
                  <TestDefinitions />
                  <ResultSummary outcome="Failed">
                    <Counters total="1" executed="1" passed="0" failed="1" />
                  </ResultSummary>
                </TestRun>
                """);
            Directory.CreateDirectory(Path.Combine(root, "mtp-results", "Deploy_ 20260814T022245_4284"));

            evidence.RequireExactlyOne(
                PhysicalOfflineTopologyProbe.ExactMtpTestName,
                "Failed",
                allowMtpDeploymentDirectory: true);

            Directory.Delete(Path.Combine(root, "mtp-results", "Deploy_ 20260814T022245_4284"), recursive: true);
            AssertEx.ThrowsException<InvalidDataException>(() =>
                evidence.RequireExactlyOne(
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    "Failed",
                    afterParseBeforeFinalEnumeration: () => Directory.CreateDirectory(Path.Combine(root, "mtp-results", "Deploy_ late")),
                    allowMtpDeploymentDirectory: true));

            Directory.Delete(Path.Combine(root, "mtp-results", "Deploy_ late"), recursive: true);
            File.WriteAllText(Path.Combine(root, "mtp-results", "unexpected.txt"), "unexpected");
            AssertEx.ThrowsException<InvalidDataException>(() =>
                evidence.RequireExactlyOne(
                    PhysicalOfflineTopologyProbe.ExactMtpTestName,
                    "Failed",
                    allowMtpDeploymentDirectory: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TopLevelWatchdogFailureRedactsSecretArgumentsAndEscapesControls()
    {
        const string confirmation = "confirmation-RAW-SECRET";
        const string session = @"C:\snapshots\one-run-RAW-SECRET\session.json";
        var exception = new InvalidDataException($"Could not load {session}\r\nconfirmation={confirmation}\0FORGED=1");

        var diagnostic = DiagnosticText.SanitizeWatchdogFailure(
            exception,
            ["--mode", "execute", "--session", session, "--confirmation", confirmation]);

        Assert.IsFalse(diagnostic.Contains(session, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostic.Contains(confirmation, StringComparison.Ordinal));
        Assert.AreEqual(-1, diagnostic.IndexOf('\r'));
        Assert.AreEqual(-1, diagnostic.IndexOf('\n'));
        Assert.AreEqual(-1, diagnostic.IndexOf('\0'));
        StringAssert.Contains(diagnostic, "[REDACTED]");
        StringAssert.Contains(diagnostic, "\\u000D\\u000A");
    }

    [TestMethod]
    public void OfflineTopologyProbeCanOnlyLaunchOneExactMstestThroughTheProductionLauncherContract()
    {
        var runner = @"C:\probe\physical-tests\Moondrop.PhysicalTests.exe";
        var report = @"C:\probe\observed-topology.json";
        var startInfo = PhysicalOfflineTopologyProbe.CreateMtpRunnerStartInfo(
            runner,
            report,
            new PhysicalProbeProcessIdentity(400, 100, DateTimeOffset.Parse("2026-08-09T09:00:00Z"), @"C:\probe\watchdog\Moondrop.PhysicalWatchdog.exe", new string('A', 64)),
            new HarnessFingerprint("SHA-256", new string('C', 64),
            [
                new HarnessFingerprintEntry("physical-tests/Moondrop.PhysicalTests.exe", new string('B', 64)),
                new HarnessFingerprintEntry("watchdog/Moondrop.PhysicalWatchdog.exe", new string('A', 64))
            ]),
            new Dictionary<string, string>
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["TEMP"] = Path.GetTempPath(),
                ["TMP"] = Path.GetTempPath()
            });
        var productionCommand = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"),
            physicalRunnerPath: @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\physical-tests\Moondrop.PhysicalTests.exe");
        var productionStartInfo = PhysicalRunnerProcessStartInfo.Create(productionCommand);

        Assert.IsInstanceOfType<PhysicalProcessLaunchPlan>(startInfo);
        Assert.IsInstanceOfType<PhysicalProcessLaunchPlan>(productionStartInfo);

        Assert.AreEqual(Path.GetFullPath(runner), startInfo.FileName);
        var arguments = startInfo.ArgumentList.ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "--filter", $"FullyQualifiedName={PhysicalOfflineTopologyProbe.ExactMtpTestName}",
                "--minimum-expected-tests", "1",
                "--results-directory", @"C:\probe\mtp-results",
                "--report-trx", "--report-trx-filename"
            },
            arguments[..8]);
        StringAssert.Matches(arguments[8], new System.Text.RegularExpressions.Regex("^offline-topology-[0-9a-f]{32}\\.trx$"));
        CollectionAssert.AreEqual(new[] { "--output", "Detailed" }, arguments[9..]);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "SystemRoot", "WINDIR", "TEMP", "TMP",
                "MD_OFFLINE_TOPOLOGY_REPORT",
                "MD_OFFLINE_TOPOLOGY_WATCHDOG_PID",
                "MD_OFFLINE_TOPOLOGY_WATCHDOG_PARENT_PID",
                "MD_OFFLINE_TOPOLOGY_WATCHDOG_START_UTC",
                "MD_OFFLINE_TOPOLOGY_WATCHDOG_EXE",
                "MD_OFFLINE_TOPOLOGY_WATCHDOG_SHA256",
                "MD_OFFLINE_TOPOLOGY_RUNTIME_SHA256",
                "MD_OFFLINE_TOPOLOGY_RUNNER_SHA256",
                "MD_OFFLINE_TOPOLOGY_TRX"
            },
            startInfo.Environment.Keys.ToArray());
        Assert.AreEqual(-1, startInfo.ArgumentList.ToList().IndexOf("--offline-topology-probe-child"));
        Assert.IsFalse(startInfo.Environment.Keys.Any(name => name.StartsWith("MOONDROP_", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void OfflineTopologyProbeAcceptsTheDirectPublishedApphostEdge()
    {
        var started = DateTimeOffset.Parse("2026-08-09T09:00:00Z");
        var watchdogPath = @"C:\probe\watchdog\Moondrop.PhysicalWatchdog.exe";
        var runnerPath = @"C:\probe\physical-tests\Moondrop.PhysicalTests.exe";
        var observation = new PhysicalOfflineTopologyObservation(
            PhysicalOfflineTopologyProbe.SafetyMode,
            new PhysicalProbeProcessIdentity(400, 100, started, watchdogPath, new string('A', 64)),
            new PhysicalProbeProcessIdentity(401, 400, started.AddSeconds(1), runnerPath, new string('B', 64)));

        PhysicalOfflineTopologyProbe.RequireDirectPublishedApphostTopology(
            observation,
            watchdogPath,
            runnerPath);
    }

    [TestMethod]
    public async Task OfflineTopologyReportRoundTripsExactProcessIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-offline-topology-{Guid.NewGuid():N}");
        var report = Path.Combine(root, "observed-topology.json");
        var observation = new PhysicalOfflineTopologyObservation(
            PhysicalOfflineTopologyProbe.SafetyMode,
            new PhysicalProbeProcessIdentity(400, 100, DateTimeOffset.Parse("2026-08-09T09:00:00Z"), @"C:\probe\watchdog\Moondrop.PhysicalWatchdog.exe", new string('A', 64)),
            new PhysicalProbeProcessIdentity(401, 400, DateTimeOffset.Parse("2026-08-09T09:00:01Z"), @"C:\probe\physical-tests\Moondrop.PhysicalTests.exe", new string('B', 64)));
        Directory.CreateDirectory(root);
        try
        {
            await PhysicalOfflineTopologyProbe.WriteObservationAsync(root, report, observation);

            Assert.AreEqual(observation, PhysicalOfflineTopologyProbe.ReadObservation(root, report));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OfflineTopologyReportIsRootBoundCreateNewAndHasOneConcurrentPublisher()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-offline-report-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "offline-topology");
        var report = Path.Combine(root, "observed-topology.json");
        var outside = Path.Combine(sandbox, "outside.json");
        var observation = new PhysicalOfflineTopologyObservation(
            PhysicalOfflineTopologyProbe.SafetyMode,
            new PhysicalProbeProcessIdentity(400, 100, DateTimeOffset.Parse("2026-08-09T09:00:00Z"), @"C:\probe\watchdog\Moondrop.PhysicalWatchdog.exe", new string('A', 64)),
            new PhysicalProbeProcessIdentity(401, 400, DateTimeOffset.Parse("2026-08-09T09:00:01Z"), @"C:\probe\physical-tests\Moondrop.PhysicalTests.exe", new string('B', 64)));
        Directory.CreateDirectory(root);
        try
        {
            await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() =>
                PhysicalOfflineTopologyProbe.WriteObservationAsync(root, outside, observation));

            File.WriteAllText(report, "pre-existing");
            await AssertEx.ThrowsExceptionAsync<IOException>(() =>
                PhysicalOfflineTopologyProbe.WriteObservationAsync(root, report, observation));
            Assert.AreEqual("pre-existing", File.ReadAllText(report));
            File.Delete(report);

            var attempts = await Task.WhenAll(
                Enumerable.Range(0, 2).Select(async _ =>
                {
                    try
                    {
                        await PhysicalOfflineTopologyProbe.WriteObservationAsync(root, report, observation);
                        return true;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                }));
            Assert.AreEqual(1, attempts.Count(success => success));
            Assert.AreEqual(observation, PhysicalOfflineTopologyProbe.ReadObservation(root, report));
            Assert.IsFalse(Directory.EnumerateFiles(root).Any(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedPhysicalPathsRejectInjectedReparseAncestorsAndFinalTargets()
    {
        var root = Path.GetFullPath(@"C:\trusted-physical-runtime");
        var redirected = Path.Combine(root, "candidate", "redirected");
        var target = Path.Combine(redirected, "Moondrop.PhysicalTests.exe");
        foreach (var description in new[]
                 {
                     "runner apphost", "watchdog apphost", "runtime root", "heartbeat directory",
                     "heartbeat file", "offline report root", "offline report path", "runtime manifest"
                 })
        {
            var ancestorError = AssertEx.ThrowsException<InvalidDataException>(() =>
                TrustedPhysicalPath.RequireContainedNoReparse(
                    root, target, description, new InjectedReparseInspector(redirected)));
            StringAssert.Contains(ancestorError.Message, "reparse");

            var finalError = AssertEx.ThrowsException<InvalidDataException>(() =>
                TrustedPhysicalPath.RequireContainedNoReparse(
                    root, target, description, new InjectedReparseInspector(target)));
            StringAssert.Contains(finalError.Message, "reparse");
        }
    }

    [TestMethod]
    public void TrustedPhysicalPathsInspectDanglingReparseEntriesInsteadOfSkippingThem()
    {
        var root = Path.GetFullPath(@"C:\trusted-physical-runtime");
        var dangling = Path.Combine(root, "candidate", "dangling");
        var target = Path.Combine(dangling, "observed-topology.json");

        var error = AssertEx.ThrowsException<InvalidDataException>(() =>
            TrustedPhysicalPath.RequireContainedNoReparse(
                root,
                target,
                "offline topology report",
                new DanglingInjectedReparseInspector(dangling)));

        StringAssert.Contains(error.Message, "reparse");
    }

    [TestMethod]
    public void TrustedPhysicalPathLeaseDetectsAncestorReplacementBeforeCommit()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-path-lease-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "root");
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        var moved = Path.Combine(sandbox, "moved");
        try
        {
            using (var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, child, "lease test"))
            {
                Directory.Move(child, moved);
                Assert.ThrowsExactly<InvalidDataException>(() => lease.Verify());
            }
            Assert.IsTrue(Directory.Exists(moved));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedPhysicalPathLeasePreventsTargetMutationDuringAuthenticatedRead()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-authenticated-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        var target = Path.Combine(sandbox, "runtime-manifest.json");
        File.WriteAllText(target, "trusted");
        try
        {
            using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(sandbox, target, "authenticated read");
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(target, "replaced"));
            Assert.AreEqual("trusted", File.ReadAllText(target));
            lease.Verify();
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedPhysicalPathExistingLeaseRejectsMissingAcceptedTarget()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-existing-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            var missing = Path.Combine(sandbox, "Deploy_ expected");
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(sandbox, missing, "expected deployment directory"));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedPhysicalPathExistingLeaseAcquiresExistingTargetBeyondWindowsMaxPath()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-deep-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            var deep = sandbox;
            foreach (var segment in new[]
                     {
                         "first-component-deep-directory",
                         "second-component-deep-directory",
                         "third-component-deep-directory",
                         "fourth-component-deep-directory",
                         "fifth-component-deep-directory",
                         "sixth-component-deep-directory"
                     })
                deep = Path.Combine(deep, segment);
            var target = Path.Combine(deep, "offline-topology-extended-length-evidence.trx");
            Assert.IsGreaterThan(260, target.Length, $"regression requires a target beyond Windows MAX_PATH; actual length was {target.Length}.");
            Assert.AreEqual(
                @"\\?\" + Path.GetFullPath(target),
                TrustedPhysicalPath.ToExtendedLengthForm(target),
                "the stable-path lease must use the extended-length form required by raw CreateFileW beyond MAX_PATH.");
            Directory.CreateDirectory(deep);
            File.WriteAllText(target, "<TestRun />");
            using var lease = TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(sandbox, target, "deep existing target");
            lease.Verify();
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeManifestNotHeartbeatBindsBothExactApphostHashes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-apphost-manifest-{Guid.NewGuid():N}");
        var runner = Path.Combine(root, "physical-tests", "Moondrop.PhysicalTests.exe");
        var watchdog = Path.Combine(root, "watchdog", "Moondrop.PhysicalWatchdog.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(runner)!);
        Directory.CreateDirectory(Path.GetDirectoryName(watchdog)!);
        File.WriteAllBytes(runner, [1]);
        File.WriteAllBytes(watchdog, [2]);
        var runnerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]));
        var watchdogHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2]));
        var manifest = RuntimeApphostManifestBinding.CreateManifest(runnerHash, watchdogHash);
        try
        {
            RuntimeApphostManifestBinding.Require(
                manifest.AggregateSha256, manifest, runner, watchdog, forgedHeartbeatWatchdogSha256: new string('F', 64));

            File.WriteAllBytes(runner, [3]);
            StringAssert.Contains(
                AssertEx.ThrowsException<InvalidDataException>(() => RuntimeApphostManifestBinding.Require(
                    manifest.AggregateSha256, manifest, runner, watchdog, forgedHeartbeatWatchdogSha256: watchdogHash)).Message,
                "runner");
            File.WriteAllBytes(runner, [1]);

            AssertEx.ThrowsException<InvalidDataException>(() => RuntimeApphostManifestBinding.Require(
                new string('E', 64), manifest, runner, watchdog, forgedHeartbeatWatchdogSha256: watchdogHash));
            var wrongEntry = RuntimeApphostManifestBinding.CreateManifest(new string('D', 64), watchdogHash);
            AssertEx.ThrowsException<InvalidDataException>(() => RuntimeApphostManifestBinding.Require(
                wrongEntry.AggregateSha256, wrongEntry, runner, watchdog, forgedHeartbeatWatchdogSha256: watchdogHash));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DeliberateWrapperRegressionsLaunchTheExactMtpCommandWithoutAllowlistingWrappers()
    {
        var direct = new PhysicalProcessLaunchPlan(
            @"C:\candidate\physical-tests\Moondrop.PhysicalTests.exe",
            @"C:\candidate\physical-tests",
            ["--filter", $"FullyQualifiedName={PhysicalOfflineTopologyProbe.ExactMtpTestName}"],
            new Dictionary<string, string>
            {
                ["MD_OFFLINE_TOPOLOGY_REPORT"] = @"C:\candidate\offline-topology\wrapper\observed-topology.json"
            });

        foreach (var shape in Enum.GetValues<DeliberateOfflineWrapperShape>())
        {
            var wrapped = PhysicalOfflineTopologyProbe.CreateDeliberateWrapperStartInfo(direct, shape);
            Assert.AreNotEqual(direct.FileName, wrapped.FileName);
            Assert.IsTrue(
                wrapped.ArgumentList.Concat(wrapped.Environment.Values)
                    .Any(value => value.Contains(direct.FileName, StringComparison.OrdinalIgnoreCase)),
                $"Wrapper shape {shape} did not retain the exact physical apphost command in either controlled launch surface.");
            Assert.AreEqual(direct.Environment["MD_OFFLINE_TOPOLOGY_REPORT"], wrapped.Environment["MD_OFFLINE_TOPOLOGY_REPORT"]);
        }
        CollectionAssert.AreEquivalent(
            new[] { DeliberateOfflineWrapperShape.CommandPrompt, DeliberateOfflineWrapperShape.WindowsPowerShell },
            Enum.GetValues<DeliberateOfflineWrapperShape>());
    }

    [TestMethod]
    public async Task DeliberateWrapperShapesActuallyInvokeTheExactNamedExecutableAndPropagateItsExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-wrapper-process-{Guid.NewGuid():N}");
        var runner = Path.Combine(root, "Moondrop.PhysicalTests.exe");
        Directory.CreateDirectory(root);
        var markerDirectory = Path.Combine(root, "space path");
        Directory.CreateDirectory(markerDirectory);
        var marker = Path.Combine(markerDirectory, "wrapper marker.txt");
        var script = Path.Combine(markerDirectory, "wrapper script.ps1");
        File.WriteAllText(script, "param([string]$Marker) Set-Content -LiteralPath $Marker -Value invoked; exit 7");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), runner);
        try
        {
            var direct = new PhysicalProcessLaunchPlan(
                runner,
                root,
                ["-NoProfile", "-File", script, marker],
                new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
                    name => name,
                    name => Environment.GetEnvironmentVariable(name)!,
                    StringComparer.Ordinal));
            foreach (var shape in Enum.GetValues<DeliberateOfflineWrapperShape>())
            {
                if (File.Exists(marker)) File.Delete(marker);
                var result = await PhysicalProcessLauncher.RunToExitAsync(
                    PhysicalOfflineTopologyProbe.CreateDeliberateWrapperStartInfo(direct, shape),
                    CancellationToken.None);
                Assert.IsTrue(File.Exists(marker), $"Wrapper shape {shape} split, lost, or did not invoke a native argument containing spaces. stdout={result.StandardOutput}; stderr={result.StandardError}");
                Assert.AreEqual(7, result.ExitCode,
                    $"Wrapper shape {shape} did not invoke and propagate the exact child exit. stdout={result.StandardOutput}; stderr={result.StandardError}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void IsolatedPhysicalRestoreIsNetworkFreeAndUsesAnExistingLocalPackageCache()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var config = System.Xml.Linq.XDocument.Load(Path.Combine(root, "tests-dotnet", "build-isolation", "physical.NuGet.Config"));
        Assert.IsFalse(config.Descendants("add").Any(element =>
            ((string?)element.Attribute("value"))?.Contains("://", StringComparison.Ordinal) == true));

        var plan = PhysicalRuntimeBuildPlan.Create(root, new string('D', 32), "offline-contract");
        var environment = PhysicalRuntimeBuilder.CreateIsolatedBuildEnvironment(plan, @"C:\Users\mohammed\.dotnet\dotnet.exe");
        Assert.IsTrue(Directory.Exists(environment["NUGET_PACKAGES"]));
        Assert.IsFalse(Path.GetFullPath(environment["NUGET_PACKAGES"]).StartsWith(
            Path.GetFullPath(plan.RuntimeRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(plan.Commands.SelectMany(command => command.Arguments)
            .Any(argument => string.Equals(argument, "-p:NuGetAudit=false", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SourceFingerprintNeverRecursesIntoGeneratedArtifactOrCandidateTrees()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        try
        {
            var baseline = HarnessBuildFingerprint.CaptureSource(root);
            var generated = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "old", "source", "tests-dotnet", "Generated.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
            File.WriteAllText(generated, "namespace MustNeverBeFingerprintInput;");

            var after = HarnessBuildFingerprint.CaptureSource(root);

            Assert.AreEqual(baseline.AggregateSha256, after.AggregateSha256);
            Assert.IsFalse(after.Files.Any(file => file.RelativePath.Contains("/artifacts/", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartupSmokeNeverOpensUnrelatedHistoricalArtifactContents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-smoke-scope-{Guid.NewGuid():N}");
        var runtime = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "candidate");
        var output = Path.Combine(runtime, "smoke");
        var historical = Path.Combine(root, "tests-dotnet", "artifacts", "hardware-snapshots", "historical-session.json");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(historical)!);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe"), Path.Combine(output, "OfflineSmoke.exe"));
        File.WriteAllText(historical, "must-not-be-opened");
        try
        {
            using (File.Open(historical, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await PhysicalRuntimeBuilder.RunStartupSmokeAsync(
                    root,
                    runtime,
                    output,
                    new PhysicalRuntimeStartupSmoke("OfflineSmoke", ["/d", "/c", "exit 0"]));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WatchdogDurablePhaseContractMatchesSessionArtifactEnum()
    {
        foreach (var phase in Enum.GetValues<PhysicalSessionPhase>())
        {
            Assert.IsTrue(Enum.TryParse<DurablePhysicalPhase>(phase.ToString(), out var watchdogPhase));
            Assert.AreEqual((int)phase, (int)watchdogPhase);
        }
    }

    [TestMethod]
    public void ExecuteCommandIsDedicatedTokenizedAndUsesPhysicalRunsettings()
    {
        var spec = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\Moondrop.PhysicalWatchdog\bin\Release\net10.0-windows\Moondrop.PhysicalWatchdog.exe"));

        Assert.AreEqual(@"C:\repo\tests-dotnet\Moondrop.PhysicalTests\bin\Release\net10.0-windows\Moondrop.PhysicalTests.exe", spec.FileName);
        CollectionAssert.Contains(spec.Arguments.ToArray(), @"C:\repo\tests-dotnet\physical.runsettings");
        CollectionAssert.Contains(spec.Arguments.ToArray(), "FullyQualifiedName=Moondrop.Tests.DawnPro2PhysicalIntegrationTests.ExecutePreparedDawnPro2PhysicalSessionAsync");
        Assert.IsTrue(spec.Arguments.Any(argument => argument.Contains("session-token", StringComparison.Ordinal)));
        Assert.AreEqual("1", spec.Environment["MOONDROP_RUN_PHYSICAL_TESTS"]);
        Assert.AreEqual("confirmation", spec.Environment["MOONDROP_PHYSICAL_CONFIRMATION"]);
        Assert.AreEqual("session-token", spec.Environment["MOONDROP_PHYSICAL_WATCHDOG_TOKEN"]);
        Assert.AreEqual("42", spec.Environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_PID"]);
        StringAssert.Contains(spec.Environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"], "session-token");
    }

    [TestMethod]
    [DoNotParallelize]
    public void RunnerStartInfoDropsHostileAmbientRuntimeBuildTestAndArbitraryVariables()
    {
        var hostile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = @"C:\hostile-dotnet",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\hostile\startup-hook.dll",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER"] = "{11111111-1111-1111-1111-111111111111}",
            ["COR_ENABLE_PROFILING"] = "1",
            ["COR_PROFILER"] = "{22222222-2222-2222-2222-222222222222}",
            ["COMPlus_ReadyToRun"] = "0",
            ["MSBuildSDKsPath"] = @"C:\hostile\sdks",
            ["NUGET_PACKAGES"] = @"C:\hostile\packages",
            ["VSTEST_HOST_DEBUG"] = "1",
            ["TESTINGPLATFORM_DIAGNOSTIC"] = "1",
            ["UNRELATED_SECRET"] = "must-not-cross-boundary"
        };
        var original = hostile.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in hostile)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var command = PhysicalTestCommandBuilder.Build(
                WatchdogMode.Execute,
                @"C:\repo",
                @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
                "confirmation",
                "session-token",
                DurableState(DurablePhysicalPhase.Prepared),
                new WatchdogOwnerIdentity(
                    42,
                    DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                    @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"),
                physicalRunnerPath: @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\physical-tests\Moondrop.PhysicalTests.exe");

            var startInfo = PhysicalRunnerProcessStartInfo.Create(command);

            foreach (var name in hostile.Keys)
                Assert.IsFalse(startInfo.Environment.ContainsKey(name), $"Hostile ambient variable {name} crossed into the runner environment.");
        }
        finally
        {
            foreach (var pair in original)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [TestMethod]
    public void ExecuteRunnerStartInfoContainsOnlyExactSystemAndAuthenticatedGateValues()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"),
            physicalRunnerPath: @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\physical-tests\Moondrop.PhysicalTests.exe");

        var startInfo = PhysicalRunnerProcessStartInfo.Create(command);
        var expected = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            expected[name] = Environment.GetEnvironmentVariable(name)!;

        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), startInfo.Environment.Keys.ToArray());
        foreach (var pair in expected)
            Assert.AreEqual(pair.Value, startInfo.Environment[pair.Key], $"Unexpected value for {pair.Key}.");
        Assert.IsFalse(startInfo.Environment.ContainsKey("PATH"));
    }

    [TestMethod]
    public void RecoveryRunnerStartInfoContainsOnlyExactRecoveryAndAuthenticatedGateValues()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Recovery,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            confirmation: null,
            "recovery-token",
            DurableState(DurablePhysicalPhase.RestorationStarting),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"),
            physicalRunnerPath: @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\physical-tests\Moondrop.PhysicalTests.exe");

        var startInfo = PhysicalRunnerProcessStartInfo.Create(command);
        var expected = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            expected[name] = Environment.GetEnvironmentVariable(name)!;

        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), startInfo.Environment.Keys.ToArray());
        foreach (var pair in expected)
            Assert.AreEqual(pair.Value, startInfo.Environment[pair.Key], $"Unexpected value for {pair.Key}.");
        Assert.AreEqual("1", startInfo.Environment["MOONDROP_RUN_PHYSICAL_RECOVERY"]);
        Assert.AreEqual(command.SessionPath, startInfo.Environment["MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT"]);
        Assert.AreEqual(command.Session.OneRunToken, startInfo.Environment["MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN"]);
        Assert.IsFalse(startInfo.Environment.ContainsKey("MOONDROP_RUN_PHYSICAL_TESTS"));
        Assert.IsFalse(startInfo.Environment.ContainsKey("MOONDROP_PHYSICAL_CONFIRMATION"));
        Assert.IsFalse(startInfo.Environment.ContainsKey("PATH"));
    }

    [TestMethod]
    public void RunnerStartInfoRejectsAnyExtraCommandEnvironmentVariable()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"));
        var injected = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        injected["DOTNET_STARTUP_HOOKS"] = @"C:\hostile\startup-hook.dll";

        AssertEx.ThrowsException<InvalidDataException>(() =>
            PhysicalRunnerProcessStartInfo.Create(command with { Environment = injected }));
    }

    [TestMethod]
    public void RunnerLaunchPreparationValidatesTheEntireLaunchBeforePublishingHeartbeat()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"));
        var injected = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        injected["DOTNET_STARTUP_HOOKS"] = @"C:\hostile\startup-hook.dll";
        var heartbeatPublished = false;

        AssertEx.ThrowsException<InvalidDataException>(() =>
            PhysicalRunnerLaunchPreparation.Prepare(
                command with { Environment = injected },
                () => heartbeatPublished = true));

        Assert.IsFalse(heartbeatPublished);
    }

    [TestMethod]
    public void RunnerStartInfoRejectsUnsafeControlCharactersInRequiredValues()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"));
        var injected = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        injected["MOONDROP_PHYSICAL_CONFIRMATION"] = "confirmation\r\nDOTNET_STARTUP_HOOKS=C:\\hostile.dll";

        AssertEx.ThrowsException<InvalidDataException>(() =>
            PhysicalRunnerProcessStartInfo.Create(command with { Environment = injected }));
    }

    [TestMethod]
    public void RunnerStartInfoRejectsRunnerSessionTokenOrParentIdentityDrift()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"));

        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(
            command with { FileName = @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\physical-tests\Other.exe" }));
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(
            command with { SessionPath = @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\other-session.json" }));
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(
            command with { OwnershipToken = "other-token" }));
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(
            command with { Owner = command.Owner with { ExecutablePath = @"C:\repo\forged-watchdog.exe" } }));
        var driftedArguments = command.Arguments.ToArray();
        driftedArguments[1] = @"C:\different-root\tests-dotnet\physical.runsettings";
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(
            command with { Arguments = driftedArguments }));
    }

    [TestMethod]
    public void RunnerStartInfoRejectsMissingUnsafeOrWrongSystemEssentials()
    {
        var command = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\hardware-snapshots\dawn-pro2-session-one.json",
            "confirmation",
            "session-token",
            DurableState(DurablePhysicalPhase.Prepared),
            new WatchdogOwnerIdentity(
                42,
                DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
                @"C:\repo\tests-dotnet\artifacts\physical-runtime\approved-launcher\watchdog\Moondrop.PhysicalWatchdog.exe"));
        var essentials = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name)!,
            StringComparer.Ordinal);

        var missing = new Dictionary<string, string>(essentials, StringComparer.Ordinal);
        missing.Remove("TMP");
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(command, missing));

        var unsafeValue = new Dictionary<string, string>(essentials, StringComparer.Ordinal)
        {
            ["TEMP"] = essentials["TEMP"] + "\r\nDOTNET_ROOT=C:\\hostile"
        };
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(command, unsafeValue));

        var wrongWindowsIdentity = new Dictionary<string, string>(essentials, StringComparer.Ordinal)
        {
            ["WINDIR"] = essentials["TEMP"]
        };
        AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRunnerProcessStartInfo.Create(command, wrongWindowsIdentity));
    }

    [TestMethod]
    public void WatchdogDryRunDescriptionNeverLeaksSecretsOrTokenBearingPaths()
    {
        const string confirmation = "confirm-RAW-SECRET";
        const string ownership = "owner-RAW-SECRET";
        const string oneRun = "one-run-RAW-SECRET";
        var spec = PhysicalTestCommandBuilder.Build(
            WatchdogMode.Execute,
            @"C:\repo",
            @"C:\sessions\one-run-RAW-SECRET\session.json",
            confirmation,
            ownership,
            DurableState(DurablePhysicalPhase.Prepared) with { OneRunToken = oneRun },
            new WatchdogOwnerIdentity(42, DateTimeOffset.Parse("2026-08-01T08:00:00Z"), @"C:\watchdog.exe"));

        var description = PhysicalTestCommandBuilder.DescribeForDryRun(spec);

        foreach (var secret in new[] { confirmation, ownership, oneRun })
            Assert.IsFalse(description.Contains(secret, StringComparison.Ordinal), $"Dry-run leaked raw secret {secret}.");
        Assert.IsFalse(description.Contains(spec.SessionPath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(description.Contains(spec.Environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"], StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(description.Contains(spec.Arguments[5], StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(description, "[REDACTED]");
    }

    [TestMethod]
    [DataRow(DurablePhysicalPhase.Prepared, false)]
    [DataRow(DurablePhysicalPhase.Completed, false)]
    [DataRow(DurablePhysicalPhase.TemporaryWritesStarting, true)]
    [DataRow(DurablePhysicalPhase.AwaitingRestorationPhysicalCycle, true)]
    [DataRow(DurablePhysicalPhase.RestorationVerified, true)]
    [DataRow(DurablePhysicalPhase.Failed, true)]
    public void RecoveryDecisionUsesOnlyDurableSessionPhase(DurablePhysicalPhase phase, bool expected)
    {
        Assert.AreEqual(expected, PhysicalWatchdogPolicy.ShouldLaunchRecovery(phase));
    }

    [TestMethod]
    public void ExecuteFailureRemainsNonzeroAfterSuccessfulVerifiedRecovery()
    {
        var result = PhysicalWatchdogPolicy.CombineExecuteAndRecovery(
            executeExitCode: 124,
            recoveryExitCode: 0,
            finalPhase: DurablePhysicalPhase.Completed);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("EXECUTE FAILED; RECOVERY VERIFIED", result.Summary);
    }

    [TestMethod]
    public void RecoveryCannotClaimVerifiedWhenDurableLineageWasReplaced()
    {
        var initial = DurableState(DurablePhysicalPhase.Prepared);
        var replaced = DurableState(DurablePhysicalPhase.Completed) with { SessionId = new string('D', 32) };

        var result = PhysicalWatchdogPolicy.CombineExecuteAndRecovery(124, 0, initial, replaced);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("EXECUTE FAILED; RECOVERY FAILED", result.Summary);
    }

    [TestMethod]
    public void SuccessfulChildExitRequiresCompletedDurableStateWithOriginalLineage()
    {
        var initial = DurableState(DurablePhysicalPhase.Prepared);

        Assert.AreNotEqual(0, PhysicalWatchdogPolicy.FinalizeChildExit(0, initial, initial));
        Assert.AreNotEqual(0, PhysicalWatchdogPolicy.FinalizeChildExit(
            0,
            initial,
            DurableState(DurablePhysicalPhase.Completed) with { OneRunToken = "replacement-token" }));
        Assert.AreEqual(0, PhysicalWatchdogPolicy.FinalizeChildExit(
            0,
            initial,
            DurableState(DurablePhysicalPhase.Completed)));
        Assert.AreEqual(124, PhysicalWatchdogPolicy.FinalizeChildExit(
            124,
            initial,
            DurableState(DurablePhysicalPhase.Completed)));
    }

    [TestMethod]
    [DataRow(1, 124, DurablePhysicalPhase.RestorationStarting, true)]
    [DataRow(2, 1, DurablePhysicalPhase.AwaitingRestorationPhysicalCycle, true)]
    [DataRow(3, 124, DurablePhysicalPhase.RestorationStarting, false)]
    [DataRow(1, 1, DurablePhysicalPhase.Completed, false)]
    public void RecoveryRetryPolicyIsBoundedAndRequiresRecoverableDurableState(
        int attempt,
        int exitCode,
        DurablePhysicalPhase phase,
        bool expected)
    {
        Assert.AreEqual(expected, PhysicalWatchdogPolicy.ShouldRetryRecovery(attempt, exitCode, phase));
    }

    [TestMethod]
    public void WatchdogTerminationRequiresExactOwnedPidStartTimeCommandAndSessionToken()
    {
        var expected = new OwnedPhysicalProcess(
            4242,
            DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
            "session-token",
            @"C:\session.json",
            @"C:\repo\tests-dotnet\Moondrop.PhysicalTests\bin\Release\net10.0-windows\Moondrop.PhysicalTests.exe");
        var matching = new ObservedPhysicalProcess(
            4242,
            expected.StartedAtUtc,
            "C:\\repo\\tests-dotnet\\Moondrop.PhysicalTests\\bin\\Release\\net10.0-windows\\Moondrop.PhysicalTests.exe --results-directory C:\\watchdog\\session-token --settings C:\\repo\\tests-dotnet\\physical.runsettings");

        Assert.IsTrue(PhysicalWatchdogPolicy.CanTerminate(expected, matching));
        Assert.IsFalse(PhysicalWatchdogPolicy.CanTerminate(expected, matching with { ProcessId = 4243 }));
        Assert.IsFalse(PhysicalWatchdogPolicy.CanTerminate(expected, matching with { CommandLine = matching.CommandLine.Replace("session-token", "other-token", StringComparison.Ordinal) }));
        Assert.IsFalse(PhysicalWatchdogPolicy.CanTerminate(expected, matching with { StartedAtUtc = matching.StartedAtUtc.AddSeconds(1) }));
    }

    [TestMethod]
    public void PhysicalCyclePhasesGetUserWindowButNativeOpenDisposeRemainShortBounded()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(6), PhysicalWatchdogPolicy.InactivityLimit(DurablePhysicalPhase.AwaitingRestorationPhysicalCycle));
        Assert.AreEqual(TimeSpan.FromSeconds(15), PhysicalWatchdogPolicy.InactivityLimit(DurablePhysicalPhase.RestorationStarting));
        Assert.AreEqual(TimeSpan.FromMinutes(6), PhysicalWatchdogPolicy.InactivityLimit(DurablePhysicalPhase.AwaitingRestorationPhysicalCycle, "PhysicalCycleWaiting"));
        Assert.AreEqual(TimeSpan.FromSeconds(15), PhysicalWatchdogPolicy.InactivityLimit(DurablePhysicalPhase.AwaitingRestorationPhysicalCycle, "NativeOpenStarting"));
    }

    [TestMethod]
    public void DurableReaderUsesValidRecoveryCopyWhenPrimaryIsMalformed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-watchdog-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primary = Path.Combine(directory, "session.json");
        var recovery = Path.ChangeExtension(primary, ".recovery.json");
        try
        {
            File.WriteAllText(primary, "{ malformed");
            WriteDurableSession(recovery, "session-a", "token-a", DurablePhysicalPhase.RestorationStarting, DateTimeOffset.Parse("2026-08-01T08:00:00Z"));

            var state = DurableSessionReader.ReadNewest(primary);

            Assert.AreEqual(DurablePhysicalPhase.RestorationStarting, state.Phase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void DurableReaderRejectsDivergentValidCopiesInsteadOfChoosingNewestTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-watchdog-lineage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primary = Path.Combine(directory, "session.json");
        var recovery = Path.ChangeExtension(primary, ".recovery.json");
        try
        {
            WriteDurableSession(primary, "session-a", "token-a", DurablePhysicalPhase.RestorationStarting, DateTimeOffset.Parse("2026-08-01T08:00:00Z"));
            WriteDurableSession(recovery, "session-b", "token-b", DurablePhysicalPhase.Completed, DateTimeOffset.Parse("2026-08-01T09:00:00Z"));

            var error = AssertEx.ThrowsException<InvalidDataException>(() => DurableSessionReader.ReadNewest(primary));

            StringAssert.Contains(error.Message, "lineage");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void HarnessFingerprintRejectsAnyReviewedSourceOrBinaryDrift()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-fingerprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.cs");
        var binary = Path.Combine(directory, "binary.dll");
        File.WriteAllText(source, "reviewed source");
        File.WriteAllBytes(binary, [1, 2, 3, 4]);
        try
        {
            var reviewed = HarnessBuildFingerprint.Capture(directory, ["source.cs", "binary.dll"]);
            File.WriteAllBytes(binary, [1, 2, 3, 5]);

            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequireMatches(reviewed.AggregateSha256, directory, ["source.cs", "binary.dll"]));

            StringAssert.Contains(error.Message, "drift");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeApprovalManifestRequiresStrictMetadataBothHashesAndAllCounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-runtime-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "physical-runtime-approval.json");
        var source = new HarnessFingerprint("SHA-256", new string('A', 64),
        [
            new HarnessFingerprintEntry("build-controls/repository/global.json.presence", new string('1', 64)),
            new HarnessFingerprintEntry("src/Source.cs", new string('2', 64))
        ]);
        var runtime = new HarnessFingerprint("SHA-256", new string('B', 64),
        [
            new HarnessFingerprintEntry("physical-tests/runner.exe", new string('3', 64)),
            new HarnessFingerprintEntry("watchdog/watchdog.exe", new string('4', 64)),
            new HarnessFingerprintEntry("metadata/global.json", new string('5', 64))
        ]);
        try
        {
            AssertEx.ThrowsException<FileNotFoundException>(() => PhysicalRuntimeApprovalManifest.ReadStrict(path));
            File.WriteAllText(path, "{}");
            AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRuntimeApprovalManifest.ReadStrict(path));
            WriteApproval(path, source, runtime, sourceSha256: PhysicalRuntimeApprovalManifest.Placeholder);
            AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRuntimeApprovalManifest.ReadStrict(path));
            WriteApproval(path, source, runtime, runtimeSha256: PhysicalRuntimeApprovalManifest.Placeholder);
            AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRuntimeApprovalManifest.ReadStrict(path));
            WriteApproval(path, source, runtime, sourceInputCount: 3);
            AssertEx.ThrowsException<InvalidDataException>(() => PhysicalRuntimeApprovalManifest.ReadStrict(path));
            WriteApproval(path, source, runtime);

            var approval = PhysicalRuntimeApprovalManifest.ReadStrict(path);
            PhysicalRuntimeApprovalManifest.RequireMatches(approval, source, runtime);
            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalRuntimeApprovalManifest.RequireSessionHashes(approval, new string('C', 64), runtime.AggregateSha256));
            AssertEx.ThrowsException<InvalidDataException>(() =>
                PhysicalRuntimeApprovalManifest.RequireSessionHashes(approval, source.AggregateSha256, new string('D', 64)));
            PhysicalRuntimeApprovalManifest.RequireSessionHashes(approval, source.AggregateSha256, runtime.AggregateSha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void SourceFingerprintInputsAreStableSourceOnlyAndExcludeApprovalManifest()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();

        var first = HarnessBuildFingerprint.CaptureSource(root);
        var second = HarnessBuildFingerprint.CaptureSource(root);

        Assert.AreEqual(first.AggregateSha256, second.AggregateSha256);
        CollectionAssert.AreEqual(
            first.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            first.Files.Select(file => file.RelativePath).ToArray());
        Assert.IsFalse(first.Files.Any(file => string.Equals(
            file.RelativePath,
            PhysicalRuntimeApprovalManifest.RelativePath,
            StringComparison.Ordinal)));
        Assert.IsFalse(first.Files.Any(file => file.RelativePath.Split('/').Any(segment => segment is "bin" or "obj")));
        Assert.IsTrue(first.Files.All(file =>
            file.RelativePath.StartsWith("build-controls/", StringComparison.Ordinal) ||
            Path.GetExtension(file.RelativePath).ToLowerInvariant() is ".cs" or ".csproj" or ".xaml" or ".slnx" or ".runsettings" or ".json" or ".rsp" or ".props" or ".targets" or ".config"));
        Assert.IsTrue(first.Files.Any(file => file.RelativePath.EndsWith(".presence", StringComparison.Ordinal)));
        foreach (var auditedControl in new[]
                 {
                     "tests-dotnet/build-isolation/physical.Directory.Build.props",
                     "tests-dotnet/build-isolation/physical.Directory.Build.targets",
                     "tests-dotnet/build-isolation/physical.Directory.Packages.props",
                     "tests-dotnet/build-isolation/physical.NuGet.Config"
                 })
            Assert.IsTrue(first.Files.Any(file => file.RelativePath == auditedControl));
        Assert.AreEqual(
            first.Files.Count,
            first.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(first.Files.Any(file => Path.GetExtension(file.RelativePath) == ".xaml"));
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentRepositoryDirectoryBuildTargetsAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("Directory.Build.targets");
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentRepositoryDirectoryBuildPropsAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("Directory.Build.props");
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentRepositoryDirectoryPackagesPropsAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("Directory.Packages.props");
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentRepositoryNuGetConfigAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("NuGet.Config");
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentRepositoryGlobalJsonAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("global.json");
    }

    [TestMethod]
    public void SourceFingerprintChangesWhenPreviouslyAbsentAncestorBuildControlAppears()
    {
        AssertSourceFingerprintChangesWhenControlAppears("Directory.Build.targets", inAncestor: true);
    }

    [TestMethod]
    public void StagedSourceRemainsBoundWhenLiveInputsAreChangedAddedAndRemoved()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        var staging = Path.Combine(sandbox, "isolated-staging");
        var changed = Path.Combine(root, "src", "Source.cs");
        var removed = Path.Combine(root, "tests-dotnet", "Moondrop.PhysicalTests", "Source.cs");
        var changedControl = Path.Combine(root, "tests-dotnet", "build-isolation", "physical.Directory.Build.props");
        var removedControl = Path.Combine(root, "tests-dotnet", "build-isolation", "physical.NuGet.Config");
        try
        {
            var live = HarnessBuildFingerprint.CaptureSource(root);
            var staged = HarnessBuildFingerprint.StageSource(root, staging);

            File.WriteAllText(changed, "namespace ChangedAfterStaging;");
            File.WriteAllText(Path.Combine(root, "src", "AddedAfterStaging.cs"), "namespace AddedAfterStaging;");
            File.Delete(removed);
            File.WriteAllText(changedControl, "<Project><PropertyGroup><Tampered>true</Tampered></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");
            File.Delete(removedControl);

            var recaptured = HarnessBuildFingerprint.CaptureStagedSource(staging);
            Assert.AreEqual(live.AggregateSha256, staged.Fingerprint.AggregateSha256);
            Assert.AreEqual(staged.Fingerprint.AggregateSha256, recaptured.AggregateSha256);
            Assert.AreEqual("namespace FingerprintFixture;", File.ReadAllText(Path.Combine(staging, "src", "Source.cs")));
            Assert.IsFalse(File.Exists(Path.Combine(staging, "src", "AddedAfterStaging.cs")));
            Assert.IsTrue(File.Exists(Path.Combine(staging, "tests-dotnet", "Moondrop.PhysicalTests", "Source.cs")));
            Assert.AreEqual(
                "<Project />",
                File.ReadAllText(Path.Combine(staging, "tests-dotnet", "build-isolation", "physical.Directory.Build.props")));
            Assert.IsFalse(File.Exists(Path.Combine(staging, "Directory.Build.targets")));
            Assert.IsTrue(File.Exists(Path.Combine(staging, "tests-dotnet", "build-isolation", "physical.NuGet.Config")));
            var sentinelManifest = File.ReadAllText(staged.ManifestPath);
            StringAssert.Contains(sentinelManifest, ".presence");
            StringAssert.Contains(sentinelManifest, "absent");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void StagedSourceMutationIsDetectedBeforeItCanBeTrusted()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        var staging = Path.Combine(sandbox, "isolated-staging");
        try
        {
            var staged = HarnessBuildFingerprint.StageSource(root, staging);
            File.WriteAllText(Path.Combine(staging, "src", "Source.cs"), "namespace TamperedStaging;");

            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequireStagedSourceMatches(
                    staged.Fingerprint.AggregateSha256,
                    staging));

            StringAssert.Contains(error.Message, "staged source drift");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task WindowsStagedSourceProtectionDeniesMutationButPreservesReadAccessUntilReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-protected-source-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        var source = Path.Combine(nested, "Source.cs");
        Directory.CreateDirectory(nested);
        File.WriteAllText(source, "namespace ProtectedSource;");
        try
        {
            await using (var protection = WindowsPhysicalSourceProtection.ProtectAndVerify(root))
            {
                protection.RequireProtected();
                Assert.AreEqual("namespace ProtectedSource;", File.ReadAllText(source));
                AssertEx.ThrowsException<UnauthorizedAccessException>(() =>
                    File.WriteAllText(source, "namespace Tampered;"));
                AssertEx.ThrowsException<UnauthorizedAccessException>(() => File.Delete(source));
                AssertEx.ThrowsException<UnauthorizedAccessException>(() =>
                    Directory.CreateDirectory(Path.Combine(root, "created-while-protected")));
            }

            File.WriteAllText(source, "namespace WritableAfterRelease;");
            Directory.CreateDirectory(Path.Combine(root, "created-after-release"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WindowsStagedSourceProtectionFailsClosedWhenAWriterAlreadyHoldsAStagedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-preopened-source-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "Source.cs");
        Directory.CreateDirectory(root);
        File.WriteAllText(source, "namespace PreopenedSource;");
        try
        {
            using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var error = AssertEx.ThrowsException<IOException>(() =>
                    WindowsPhysicalSourceProtection.ProtectAndVerify(root));
                StringAssert.Contains(error.Message, "integrity handle");
            }

            File.WriteAllText(source, "namespace WritableAfterFailedProtection;");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalRuntimeBuilderProtectsValidatedStageThroughoutBuildSmokeAndManifestCapture()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        foreach (var relative in new[]
                 {
                     "src/Moondrop.Core/packages.lock.json",
                     "src/Moondrop.Hardware/packages.lock.json",
                     "tests-dotnet/Moondrop.PhysicalTests/packages.lock.json",
                     "tests-dotnet/Moondrop.PhysicalWatchdog/packages.lock.json"
                 })
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"version\":1,\"dependencies\":{}}");
        }
        File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.302\"}}");
        var approvalPath = Path.Combine(root, PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(approvalPath)!);
        try
        {
            var auditState = new SimulatedPhysicalProcessState();
            var auditProtection = new SimulatedPhysicalSourceProtectionLayer(auditState);
            var auditExecutor = new SimulatedPhysicalBuildExecutor(auditState, auditProtection);
            var candidate = await PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                root,
                InstalledAuditedDotnetPath(),
                "0123456789abcdef0123456789abcdef",
                "audit-generation",
                auditProtection,
                auditExecutor);
            WriteApproval(approvalPath, candidate.SourceFingerprint, candidate.RuntimeManifest);
            var processState = new SimulatedPhysicalProcessState();
            var protection = new SimulatedPhysicalSourceProtectionLayer(processState);
            var executor = new SimulatedPhysicalBuildExecutor(processState, protection);
            var built = await PhysicalRuntimeBuilder.BuildAsync(
                root,
                InstalledAuditedDotnetPath(),
                "0123456789abcdef0123456789abcdef",
                "test-generation",
                protection,
                executor);

            Assert.AreEqual(1, protection.InvocationCount);
            Assert.AreEqual(1, executor.OfflineTopologySmokeCount);
            Assert.AreEqual(built.RuntimeManifest.AggregateSha256, executor.OfflineTopologyRuntimeManifestSha256);
            Assert.AreEqual(
                Path.Combine(built.Plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe"),
                executor.OfflineTopologyPhysicalApphostPath);
            Assert.AreEqual(
                Path.Combine(built.Plan.WatchdogOutputDirectory, "Moondrop.PhysicalWatchdog.exe"),
                executor.OfflineTopologyWatchdogApphostPath);
            Assert.IsTrue(protection.MutationDenied);
            Assert.IsGreaterThanOrEqualTo(built.Plan.Commands.Count + 2, protection.RequireProtectedCount);
            Assert.IsTrue(protection.Released);
            Assert.IsFalse(protection.ProcessWasActiveAtRelease);
            Assert.AreEqual(candidate.SourceFingerprint.AggregateSha256, built.SourceFingerprint.AggregateSha256);
            Assert.AreEqual(
                candidate.SourceFingerprint.AggregateSha256,
                HarnessBuildFingerprint.CaptureStagedSource(built.Plan.SourceRoot).AggregateSha256);
            Assert.IsFalse(Directory.EnumerateFiles(built.Plan.SourceRoot, "*", SearchOption.AllDirectories)
                .Any(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj")));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalRuntimeBuilderValidatesThePlanBeforeCreatingTheBuildLock()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-build-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var runtimeArtifacts = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime");
        var processState = new SimulatedPhysicalProcessState();
        var protection = new SimulatedPhysicalSourceProtectionLayer(processState);
        var executor = new SimulatedPhysicalBuildExecutor(processState, protection);
        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                    root,
                    InstalledAuditedDotnetPath(),
                    "invalid-session",
                    "audit-generation",
                    protection,
                    executor));

            Assert.IsFalse(Directory.Exists(runtimeArtifacts));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CandidateTopologyValidatesEveryPathBeforeCreatingReportDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-topology-order-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", new string('a', 32), "candidate");
        Directory.CreateDirectory(runtimeRoot);
        var reportDirectory = Path.Combine(runtimeRoot, "offline-topology");
        var invalidRunner = Path.Combine(runtimeRoot, "physical-tests", "Not-The-Runner.exe");
        var watchdog = Path.Combine(runtimeRoot, "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var manifest = RuntimeApphostManifestBinding.CreateManifest(new string('A', 64), new string('B', 64)) with
        {
            AggregateSha256 = "malformed"
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                PhysicalRuntimeBuilder.RunOfflineTopologySmokeAsync(
                    root,
                    runtimeRoot,
                    invalidRunner,
                    watchdog,
                    manifest,
                    CancellationToken.None));

            Assert.IsFalse(Directory.Exists(reportDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CandidateBuilderRejectsCompleteRuntimeTreeMutationDuringMtpTopologySmoke()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        AddRequiredRuntimeMetadata(root);
        try
        {
            var processState = new SimulatedPhysicalProcessState();
            var protection = new SimulatedPhysicalSourceProtectionLayer(processState);
            var executor = new SimulatedPhysicalBuildExecutor(
                processState,
                protection,
                changeRuntimeDuringOfflineTopologySmoke: true);

            var error = await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() =>
                PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                    root,
                    InstalledAuditedDotnetPath(),
                    "3456789abcdef0123456789abcdef012",
                    "topology-runtime-drift",
                    protection,
                    executor));

            Assert.IsTrue(executor.RuntimeChangedDuringOfflineTopologySmoke);
            StringAssert.Contains(error.Message, "topology");
            StringAssert.Contains(error.Message, "runtime");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransientSameOwnerStageTamperThenRestoreChangesRuntimeAndPrepareAuthorizationRejects()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        AddRequiredRuntimeMetadata(root);
        var approvalPath = Path.Combine(root, PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(approvalPath)!);
        try
        {
            var baselineState = new SimulatedPhysicalProcessState();
            var baselineProtection = new SimulatedPhysicalSourceProtectionLayer(baselineState);
            var baseline = await PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                root,
                InstalledAuditedDotnetPath(),
                "11111111111111111111111111111111",
                "baseline",
                baselineProtection,
                new SimulatedPhysicalBuildExecutor(baselineState, baselineProtection));
            WriteApproval(approvalPath, baseline.SourceFingerprint, baseline.RuntimeManifest);

            var tamperState = new SimulatedPhysicalProcessState();
            var tamperProtection = new SimulatedPhysicalSourceProtectionLayer(tamperState);
            var tamperExecutor = new SimulatedPhysicalBuildExecutor(
                tamperState,
                tamperProtection,
                transientStageTamper: true);
            var error = await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() =>
                PhysicalRuntimeBuilder.BuildAsync(
                    root,
                    InstalledAuditedDotnetPath(),
                    "22222222222222222222222222222222",
                    "tampered",
                    tamperProtection,
                    tamperExecutor));

            StringAssert.Contains(error.Message, "runtime");
            Assert.IsTrue(tamperExecutor.TransientTamperRestored);
            Assert.IsTrue(tamperExecutor.PublishedFromTamperedSource);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApprovedSourceWithChangedPublishedRuntimeFileIsRejectedBeforePhysicalSessionCanExecute()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        AddRequiredRuntimeMetadata(root);
        var approvalPath = Path.Combine(root, PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(approvalPath)!);
        try
        {
            var baselineState = new SimulatedPhysicalProcessState();
            var baselineProtection = new SimulatedPhysicalSourceProtectionLayer(baselineState);
            var baseline = await PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                root,
                InstalledAuditedDotnetPath(),
                "33333333333333333333333333333333",
                "baseline",
                baselineProtection,
                new SimulatedPhysicalBuildExecutor(baselineState, baselineProtection));
            WriteApproval(approvalPath, baseline.SourceFingerprint, baseline.RuntimeManifest);

            var changedState = new SimulatedPhysicalProcessState();
            var changedProtection = new SimulatedPhysicalSourceProtectionLayer(changedState);
            var changedExecutor = new SimulatedPhysicalBuildExecutor(
                changedState,
                changedProtection,
                changePublishedRuntimeFile: true);
            var error = await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() =>
                PhysicalRuntimeBuilder.BuildAsync(
                    root,
                    InstalledAuditedDotnetPath(),
                    "44444444444444444444444444444444",
                    "changed-output",
                    changedProtection,
                    changedExecutor));

            StringAssert.Contains(error.Message, "runtime");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingEitherApprovalHashStopsAuthorizedBuildBeforeAnyRunnerOutputIsProduced()
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        AddRequiredRuntimeMetadata(root);
        var approvalPath = Path.Combine(root, PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(approvalPath)!);
        var source = HarnessBuildFingerprint.CaptureSource(root);
        var runtime = new HarnessFingerprint("SHA-256", new string('B', 64),
        [
            new HarnessFingerprintEntry("physical-tests/runner.exe", new string('1', 64)),
            new HarnessFingerprintEntry("watchdog/watchdog.exe", new string('2', 64)),
            new HarnessFingerprintEntry("metadata/global.json", new string('3', 64))
        ]);
        try
        {
            foreach (var missingSource in new[] { true, false })
            {
                WriteApproval(
                    approvalPath,
                    source,
                    runtime,
                    sourceSha256: missingSource ? PhysicalRuntimeApprovalManifest.Placeholder : null,
                    runtimeSha256: missingSource ? null : PhysicalRuntimeApprovalManifest.Placeholder);
                var state = new SimulatedPhysicalProcessState();
                var protection = new SimulatedPhysicalSourceProtectionLayer(state);
                var executor = new SimulatedPhysicalBuildExecutor(state, protection);

                await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() =>
                    PhysicalRuntimeBuilder.BuildAsync(
                        root,
                        InstalledAuditedDotnetPath(),
                        missingSource ? "55555555555555555555555555555555" : "66666666666666666666666666666666",
                        missingSource ? "missing-source" : "missing-runtime",
                        protection,
                        executor));

                Assert.AreEqual(0, executor.InvocationCount);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancelledBuildCommandIsTerminatedBeforeSourceProtectionCouldBeReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-build-child-{Guid.NewGuid():N}");
        var lockPath = Path.Combine(root, "child.lock");
        var readyPath = Path.Combine(root, "child.pid");
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        int? childPid = null;
        try
        {
            var escapedLock = lockPath.Replace("'", "''", StringComparison.Ordinal);
            var escapedReady = readyPath.Replace("'", "''", StringComparison.Ordinal);
            var script =
                $"$stream=[IO.File]::Open('{escapedLock}',[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                $"[IO.File]::WriteAllText('{escapedReady}',$PID);" +
                "try { Start-Sleep -Seconds 30 } finally { $stream.Dispose() }";
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var environment = PhysicalRuntimeBuilder.CreateStartupSmokeEnvironment(
                Path.Combine(root, "missing-runtime"),
                root);
            var running = PhysicalRuntimeBuilder.RunDotnetAsync(
                powershell,
                root,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                environment,
                cancellation.Token);
            var readyDeadline = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && readyDeadline.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
            Assert.IsTrue(File.Exists(readyPath));
            childPid = int.Parse(File.ReadAllText(readyPath), System.Globalization.CultureInfo.InvariantCulture);

            cancellation.Cancel();
            await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() => running);

            using var exclusive = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            if (childPid is not null)
            {
                try
                {
                    using var process = Process.GetProcessById(childPid.Value);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PackageBearingPhysicalProjectsRequireCommittedLockFiles()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var projects = new[]
        {
            "src/Moondrop.Core/Moondrop.Core.csproj",
            "src/Moondrop.Hardware/Moondrop.Hardware.csproj",
            "src/Moondrop.Wpf/Moondrop.Wpf.csproj",
            "tests-dotnet/Moondrop.Tests/Moondrop.Tests.csproj",
            "tests-dotnet/Moondrop.PhysicalTests/Moondrop.PhysicalTests.csproj",
            "tests-dotnet/Moondrop.PhysicalWatchdog/Moondrop.PhysicalWatchdog.csproj"
        };

        foreach (var relativeProject in projects)
        {
            var projectPath = Path.Combine(root, relativeProject.Replace('/', Path.DirectorySeparatorChar));
            var project = System.Xml.Linq.XDocument.Load(projectPath);
            Assert.AreEqual("true", project.Descendants("RestorePackagesWithLockFile").Single().Value);
            Assert.AreEqual("true", project.Descendants("RestoreLockedMode").Single().Value);
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, "packages.lock.json")));
        }
    }

    [TestMethod]
    public void PhysicalRunnerLockedGraphKeepsMSTestAndTrxWithoutCodeCoverageExtension()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var lockPath = Path.Combine(root, "tests-dotnet", "Moondrop.PhysicalTests", "packages.lock.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        var dependencies = document.RootElement
            .GetProperty("dependencies")
            .EnumerateObject()
            .Single(property => !property.Name.Contains('/', StringComparison.Ordinal))
            .Value;

        Assert.IsTrue(dependencies.TryGetProperty("MSTest.TestAdapter", out _));
        Assert.IsTrue(dependencies.TryGetProperty("MSTest.TestFramework", out _));
        Assert.IsTrue(dependencies.TryGetProperty("Microsoft.Testing.Extensions.TrxReport", out _));
        Assert.IsFalse(dependencies.TryGetProperty("Microsoft.Testing.Extensions.CodeCoverage", out _));
    }

    [TestMethod]
    public void RuntimeManifestIsStableAcrossStagingRootsAndRejectsEveryRuntimeOrLockedControlChange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-runtime-manifest-{Guid.NewGuid():N}");
        var firstStage = Path.Combine(root, "generation-one", "source");
        var secondStage = Path.Combine(root, "different", "generation-two", "source");
        var physical = Path.Combine(root, "generation-one", "physical-tests");
        var watchdog = Path.Combine(root, "generation-one", "watchdog");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(watchdog);
        WriteRuntimeSkeleton(physical, "Moondrop.PhysicalTests", selfContained: true);
        WriteRuntimeSkeleton(watchdog, "Moondrop.PhysicalWatchdog", selfContained: true);
        File.WriteAllText(Path.Combine(physical, "HidSharp.dll"), "HidSharp");
        File.WriteAllText(Path.Combine(watchdog, "native-helper.dll"), "native-helper");
        foreach (var stage in new[] { firstStage, secondStage })
        {
            Directory.CreateDirectory(Path.Combine(stage, "obj"));
            File.WriteAllText(Path.Combine(stage, "packages.lock.json"), "lock-v1");
            File.WriteAllText(Path.Combine(stage, "build-control.props"), "control-v1");
        }
        File.WriteAllText(
            Path.Combine(firstStage, "obj", "project.assets.json"),
            $"{{\"projectPath\":\"{firstStage.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}");
        File.WriteAllText(
            Path.Combine(secondStage, "obj", "project.assets.json"),
            $"{{\"projectPath\":\"{secondStage.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}");
        try
        {
            var metadata = new[] { "packages.lock.json", "build-control.props" };
            var baseline = HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, firstStage, metadata);
            var repeated = HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, firstStage, metadata);
            Assert.AreEqual(baseline.AggregateSha256, repeated.AggregateSha256);
            var outputFileCount = Directory.EnumerateFiles(physical, "*", SearchOption.AllDirectories).Count() +
                                  Directory.EnumerateFiles(watchdog, "*", SearchOption.AllDirectories).Count();
            Assert.HasCount(outputFileCount + metadata.Length, baseline.Files);
            CollectionAssert.AreEqual(
                baseline.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray(),
                baseline.Files.Select(file => file.RelativePath).ToArray());
            var secondPhysical = Path.Combine(root, "different", "generation-two", "physical-tests");
            var secondWatchdog = Path.Combine(root, "different", "generation-two", "watchdog");
            CopyDirectory(physical, secondPhysical);
            CopyDirectory(watchdog, secondWatchdog);
            Assert.AreEqual(
                baseline.AggregateSha256,
                HarnessBuildFingerprint.CaptureRuntime(
                    root,
                    secondPhysical,
                    secondWatchdog,
                    secondStage,
                    metadata).AggregateSha256);
            Assert.IsFalse(baseline.Files.Any(file => file.RelativePath.Contains("project.assets.json", StringComparison.Ordinal)));

            foreach (var changed in new[]
                     {
                         Path.Combine(physical, "Moondrop.PhysicalTests.exe"),
                         Path.Combine(physical, "Moondrop.PhysicalTests.dll"),
                         Path.Combine(physical, "HidSharp.dll"),
                         Path.Combine(physical, "coreclr.dll"),
                         Path.Combine(physical, "Moondrop.PhysicalTests.deps.json"),
                         Path.Combine(watchdog, "Moondrop.PhysicalWatchdog.runtimeconfig.json"),
                         Path.Combine(watchdog, "native-helper.dll")
                     })
            {
                var original = File.ReadAllBytes(changed);
                File.WriteAllBytes(changed, [.. original, 0x20]);
                Assert.AreNotEqual(
                    baseline.AggregateSha256,
                    HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, firstStage, metadata).AggregateSha256);
                File.WriteAllBytes(changed, original);
            }

            foreach (var changed in metadata.Select(path => Path.Combine(firstStage, path)))
            {
                var original = File.ReadAllBytes(changed);
                File.WriteAllBytes(changed, [.. original, 0x20]);
                Assert.AreNotEqual(
                    baseline.AggregateSha256,
                    HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, firstStage, metadata).AggregateSha256);
                File.WriteAllBytes(changed, original);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeManifestRejectsFrameworkDependentPublishTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-framework-dependent-{Guid.NewGuid():N}");
        var physical = Path.Combine(root, "physical-tests");
        var watchdog = Path.Combine(root, "watchdog");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(watchdog);
        WriteRuntimeSkeleton(physical, "Moondrop.PhysicalTests", selfContained: false);
        WriteRuntimeSkeleton(watchdog, "Moondrop.PhysicalWatchdog", selfContained: false);
        File.WriteAllText(Path.Combine(root, "metadata.json"), "metadata");
        try
        {
            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, ["metadata.json"]));

            StringAssert.Contains(error.Message, "self-contained");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeManifestRejectsDeclaredStartupDependencyMissingFromPublishTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-missing-startup-dependency-{Guid.NewGuid():N}");
        var physical = Path.Combine(root, "physical-tests");
        var watchdog = Path.Combine(root, "watchdog");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(watchdog);
        WriteRuntimeSkeleton(physical, "Moondrop.PhysicalTests", selfContained: true);
        WriteRuntimeSkeleton(watchdog, "Moondrop.PhysicalWatchdog", selfContained: true);
        File.WriteAllText(
            Path.Combine(physical, "Moondrop.PhysicalTests.deps.json"),
            """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/win-x64" },
              "targets": {
                ".NETCoreApp,Version=v10.0/win-x64": {
                  "Missing.Startup/1.0.0": {
                    "runtime": { "lib/net10.0/Missing.Startup.dll": {} }
                  }
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(root, "metadata.json"), "metadata");
        try
        {
            var error = AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, ["metadata.json"]));

            StringAssert.Contains(error.Message, "Missing.Startup.dll");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimeManifestVerificationRejectsChangedAndMissingRuntimeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-runtime-drift-{Guid.NewGuid():N}");
        var physical = Path.Combine(root, "physical-tests");
        var watchdog = Path.Combine(root, "watchdog");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(watchdog);
        WriteRuntimeSkeleton(physical, "Moondrop.PhysicalTests", selfContained: true);
        WriteRuntimeSkeleton(watchdog, "Moondrop.PhysicalWatchdog", selfContained: true);
        File.WriteAllText(Path.Combine(root, "metadata.json"), "metadata");
        try
        {
            var metadata = new[] { "metadata.json" };
            var baseline = HarnessBuildFingerprint.CaptureRuntime(root, physical, watchdog, metadata);
            foreach (var changed in new[]
                     {
                         Path.Combine(physical, "coreclr.dll"),
                         Path.Combine(physical, "Moondrop.PhysicalTests.deps.json"),
                         Path.Combine(watchdog, "Moondrop.PhysicalWatchdog.runtimeconfig.json")
                     })
            {
                var original = File.ReadAllBytes(changed);
                File.WriteAllBytes(changed, [.. original, 0x20]);
                AssertEx.ThrowsException<InvalidDataException>(() =>
                    HarnessBuildFingerprint.RequireRuntimeMatches(
                        baseline.AggregateSha256, root, physical, watchdog, metadata));
                File.WriteAllBytes(changed, original);
            }

            File.Delete(Path.Combine(watchdog, "hostpolicy.dll"));
            AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequireRuntimeMatches(
                    baseline.AggregateSha256, root, physical, watchdog, metadata));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanUsesLockedRestoreAndSessionIsolatedPublishOutputs()
    {
        var first = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            "0123456789abcdef0123456789abcdef",
            "execute-one");
        var second = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            "fedcba9876543210fedcba9876543210",
            "execute-two");

        Assert.IsTrue(first.Commands.Where(command => command.Arguments[0] == "restore")
            .All(command => command.Arguments.Contains("--locked-mode")));
        Assert.IsTrue(first.Commands.Where(command => command.Arguments[0] == "publish")
            .All(command => command.Arguments.Contains("--no-restore") &&
                            command.Arguments.Contains("Release") &&
                            command.Arguments.Contains("-p:ContinuousIntegrationBuild=true")));
        StringAssert.Contains(first.PhysicalOutputDirectory, "0123456789abcdef0123456789abcdef");
        StringAssert.Contains(first.WatchdogOutputDirectory, "0123456789abcdef0123456789abcdef");
        Assert.AreNotEqual(first.RuntimeRoot, second.RuntimeRoot);
        Assert.AreNotEqual(first.PhysicalOutputDirectory, first.WatchdogOutputDirectory);
        Assert.IsFalse(first.MetadataPaths.Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal)));
        Assert.IsTrue(first.MetadataPaths.Any(path => path.EndsWith("packages.lock.json", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanRedirectsEveryBuildWriteOutsideProtectedSource()
    {
        var plan = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\session\generation\source",
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        foreach (var command in plan.Commands)
        {
            CollectionAssert.Contains(command.Arguments.ToArray(), "-p:UseArtifactsOutput=true");
            var argument = command.Arguments.Single(value =>
                value.StartsWith("-p:ArtifactsPath=", StringComparison.Ordinal));
            var path = argument[(argument.IndexOf('=') + 1)..];
            Assert.IsFalse(Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(plan.SourceRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
            StringAssert.StartsWith(Path.GetFullPath(path), Path.GetFullPath(plan.RuntimeRoot));
        }
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanSuppliesStablePathInputsThroughAuditedBuildProps()
    {
        var plan = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\session\generation\source",
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        foreach (var command in plan.Commands)
        {
            CollectionAssert.Contains(command.Arguments.ToArray(), "-p:Deterministic=true");
            var sourceRoot = command.Arguments.Single(argument =>
                argument.StartsWith("-p:PhysicalProtectedSourceRoot=", StringComparison.Ordinal));
            Assert.AreEqual(Path.GetFullPath(plan.SourceRoot), sourceRoot[(sourceRoot.IndexOf('=') + 1)..]);
            Assert.IsFalse(command.Arguments.Any(argument => argument.StartsWith("-p:PathMap=", StringComparison.Ordinal)));
        }

        var repositoryRoot = PhysicalArtifactPaths.FindRepositoryRoot();
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(
            repositoryRoot,
            "tests-dotnet", "build-isolation", "physical.Directory.Build.props"));
        var pathMap = props.Descendants("PathMap").Single().Value;
        StringAssert.Contains(pathMap, "$(PhysicalProtectedSourceRoot)=/_/source");
        StringAssert.Contains(pathMap, "$(ArtifactsPath)=/_/artifacts");
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanReferencesOnlyStagedSourceProjectsAndControls()
    {
        var liveRoot = @"C:\live-repository";
        var stagedRoot = @"C:\isolated-staging\source";
        var plan = PhysicalRuntimeBuildPlan.Create(
            liveRoot,
            stagedRoot,
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        Assert.AreEqual(stagedRoot, plan.SourceRoot);
        foreach (var command in plan.Commands)
        {
            foreach (var argument in command.Arguments.Where(argument =>
                         argument.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                         argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                         argument.StartsWith("-p:DirectoryBuild", StringComparison.Ordinal) ||
                         argument.StartsWith("-p:DirectoryPackages", StringComparison.Ordinal) ||
                         argument.StartsWith("-p:RestoreConfigFile=", StringComparison.Ordinal)))
                StringAssert.StartsWith(argument[(argument.IndexOf('=') + 1)..], stagedRoot);
        }
        Assert.IsFalse(plan.Commands.SelectMany(command => command.Arguments).Any(argument =>
            argument == Path.Combine(liveRoot, "DawnPro.Wpf.slnx") ||
            argument.EndsWith(Path.Combine("live-repository", "tests-dotnet", "Moondrop.PhysicalTests", "Moondrop.PhysicalTests.csproj"), StringComparison.OrdinalIgnoreCase) ||
            argument.EndsWith(Path.Combine("live-repository", "tests-dotnet", "Moondrop.PhysicalWatchdog", "Moondrop.PhysicalWatchdog.csproj"), StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanBuildsOnlyTheRunnerAndWatchdogThatArePublishedAndExecuted()
    {
        var plan = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            @"C:\repo\tests-dotnet\artifacts\physical-runtime\session\generation\source",
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        var commandProjects = plan.Commands
            .Select(command => command.Arguments[1])
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "Moondrop.PhysicalTests.csproj", "Moondrop.PhysicalWatchdog.csproj" },
            commandProjects);
        Assert.IsFalse(plan.Commands.SelectMany(command => command.Arguments)
            .Any(argument => argument.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                             argument.Contains("Moondrop.Wpf", StringComparison.OrdinalIgnoreCase) ||
                             argument.Contains("Moondrop.Tests.csproj", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PhysicalRuntimeBuildPlanUsesExplicitAuditedControlsAndSelfContainedWinX64Publishes()
    {
        var plan = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        Assert.IsTrue(plan.Commands.All(command => command.Arguments.Contains("-noAutoResponse")));
        Assert.IsTrue(plan.Commands.All(command => command.Arguments.Contains("--disable-build-servers")));
        Assert.IsTrue(plan.Commands.All(command => command.Arguments.Any(argument => argument.StartsWith("-p:DirectoryBuildPropsPath=", StringComparison.Ordinal))));
        Assert.IsTrue(plan.Commands.All(command => command.Arguments.Any(argument => argument.StartsWith("-p:DirectoryBuildTargetsPath=", StringComparison.Ordinal))));
        Assert.IsTrue(plan.Commands.All(command => command.Arguments.Any(argument => argument.StartsWith("-p:DirectoryPackagesPropsPath=", StringComparison.Ordinal))));
        Assert.IsTrue(plan.Commands.Where(command => command.Arguments[0] == "restore")
            .All(command => command.Arguments.Contains("--configfile") &&
                            command.Arguments.Contains("-p:RestoreLockedMode=true")));
        Assert.IsTrue(plan.Commands.Where(command => command.Arguments[0] == "publish")
            .All(command => command.Arguments.Contains("-r") &&
                            command.Arguments.Contains(PhysicalRuntimeBuildPlan.RuntimeIdentifier) &&
                            command.Arguments.Contains("--self-contained") &&
                            command.Arguments.Contains("true") &&
                            command.Arguments.Contains("-p:UseAppHost=true") &&
                            command.StartupSmoke is not null &&
                            command.StartupSmoke.Arguments.Contains("--help")));
        CollectionAssert.AreEquivalent(
            new[] { "Moondrop.PhysicalTests", "Moondrop.PhysicalWatchdog" },
            plan.Commands.Where(command => command.Arguments[0] == "publish")
                .Select(command => command.StartupSmoke!.ApplicationName)
                .ToArray());
        Assert.IsTrue(plan.MetadataPaths.Any(path => path.EndsWith("physical.NuGet.Config", StringComparison.Ordinal)));
        Assert.IsTrue(plan.MetadataPaths.Any(path => path.EndsWith("physical.Directory.Build.props", StringComparison.Ordinal)));
        Assert.IsTrue(plan.MetadataPaths.Any(path => path.EndsWith("physical.Directory.Build.targets", StringComparison.Ordinal)));
        Assert.IsTrue(plan.MetadataPaths.Any(path => path.EndsWith("physical.Directory.Packages.props", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PhysicalBuildChildEnvironmentIsMinimalExplicitAndGenerationIsolated()
    {
        var plan = PhysicalRuntimeBuildPlan.Create(
            @"C:\repo",
            "0123456789abcdef0123456789abcdef",
            "execute-one");

        var environment = PhysicalRuntimeBuilder.CreateIsolatedBuildEnvironment(
            plan,
            InstalledAuditedDotnetPath());

        CollectionAssert.AreEquivalent(
            new[]
            {
                "ComSpec", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "DOTNET_CLI_HOME",
                "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE",
                "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_NOLOGO", "DOTNET_ROOT",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "MSBUILDDISABLENODEREUSE",
                "MSBUILDNOINPROCNODE", "NUGET_HTTP_CACHE_PATH", "NUGET_PACKAGES",
                "NUGET_XMLDOC_MODE", "PATH", "SystemRoot", "TEMP", "TMP", "WINDIR",
                "APPDATA", "LOCALAPPDATA", "ProgramData", "PROGRAMFILES", "PROGRAMFILES(X86)", "USERPROFILE"
            },
            environment.Keys.ToArray());
        Assert.AreEqual(Path.GetDirectoryName(InstalledAuditedDotnetPath()), environment["DOTNET_ROOT"]);
        Assert.IsTrue(environment["DOTNET_CLI_HOME"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Directory.Exists(environment["NUGET_PACKAGES"]));
        Assert.IsFalse(environment["NUGET_PACKAGES"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["USERPROFILE"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["APPDATA"].StartsWith(environment["USERPROFILE"], StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["LOCALAPPDATA"].StartsWith(environment["USERPROFILE"], StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["ProgramData"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["PROGRAMFILES"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["PROGRAMFILES(X86)"].StartsWith(plan.RuntimeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(environment.ContainsKey("MSBuildSDKsPath"));
        Assert.IsFalse(environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.IsFalse(environment.ContainsKey("NUGET_PLUGIN_PATHS"));
    }

    [TestMethod]
    public void StartupSmokeEnvironmentHasHostileSharedRuntimePathsAndNoPhysicalOptIns()
    {
        var missingRuntime = @"C:\provably-missing-dotnet-runtime";
        var temporary = @"C:\isolated-smoke-temp";

        var environment = PhysicalRuntimeBuilder.CreateStartupSmokeEnvironment(missingRuntime, temporary);

        Assert.AreEqual(missingRuntime, environment["DOTNET_ROOT"]);
        Assert.AreEqual(missingRuntime, environment["DOTNET_ROOT_X64"]);
        Assert.AreEqual(missingRuntime, environment["DOTNET_SHARED_STORE"]);
        Assert.AreEqual(temporary, environment["TEMP"]);
        Assert.AreEqual(temporary, environment["TMP"]);
        Assert.IsFalse(environment.Keys.Any(name => name.StartsWith("MOONDROP_", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void WatchdogSelfCheckCoversExeDllRuntimeConfigDepsAndDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-watchdog-self-check-{Guid.NewGuid():N}");
        var running = Path.Combine(root, "running");
        var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(running);
        foreach (var name in new[]
                 {
                     "Moondrop.PhysicalWatchdog.exe",
                     "Moondrop.PhysicalWatchdog.dll",
                     "Moondrop.PhysicalWatchdog.deps.json",
                     "Moondrop.PhysicalWatchdog.runtimeconfig.json",
                     "dependency.dll"
                 })
            File.WriteAllText(Path.Combine(running, name), name);
        CopyDirectory(running, fresh);
        try
        {
            HarnessBuildFingerprint.RequireCompleteOutputMatches(running, fresh, "watchdog");
            File.AppendAllText(Path.Combine(fresh, "Moondrop.PhysicalWatchdog.deps.json"), "changed");

            AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequireCompleteOutputMatches(running, fresh, "watchdog"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CompleteOutputMismatchReportsEveryPathHashSizeExistenceAndDiagnosticTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-complete-output-diagnostics-{Guid.NewGuid():N}");
        var expected = Path.Combine(root, "expected");
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(actual);
        var changedExpected = Path.Combine(expected, "changed.dll");
        var changedActual = Path.Combine(actual, "changed.dll");
        File.WriteAllText(changedExpected, "expected");
        File.WriteAllText(changedActual, "actual-longer");
        File.WriteAllText(Path.Combine(expected, "expected-only.dll"), "expected-only");
        File.WriteAllText(Path.Combine(actual, "actual-only.dll"), "actual-only");
        File.SetLastWriteTimeUtc(changedExpected, new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(changedActual, new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc));
        try
        {
            var exception = AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequireCompleteOutputMatches(expected, actual, "physical-tests"));
            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(changedExpected)));
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(changedActual)));

            StringAssert.Contains(exception.Message, Path.GetFullPath(expected));
            StringAssert.Contains(exception.Message, Path.GetFullPath(actual));
            StringAssert.Contains(exception.Message, "mismatchCount=3");
            StringAssert.Contains(exception.Message, "relativePath=changed.dll");
            StringAssert.Contains(exception.Message, $"sha256={expectedHash}");
            StringAssert.Contains(exception.Message, $"sha256={actualHash}");
            StringAssert.Contains(exception.Message, "size=8");
            StringAssert.Contains(exception.Message, "size=13");
            StringAssert.Contains(exception.Message, "lastWriteTimeUtc=2026-08-08T10:00:00.0000000Z");
            StringAssert.Contains(exception.Message, "lastWriteTimeUtc=2026-08-08T11:00:00.0000000Z");
            StringAssert.Contains(exception.Message, "relativePath=expected-only.dll");
            StringAssert.Contains(exception.Message, "relativePath=actual-only.dll");
            StringAssert.Contains(exception.Message, "exists=false; path=<missing>; sha256=<missing>; size=<missing>; lastWriteTimeUtc=<missing>");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CompleteOutputMatchingTreatsTimestampsAsDiagnosticOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-complete-output-timestamps-{Guid.NewGuid():N}");
        var expected = Path.Combine(root, "expected");
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(actual);
        var expectedFile = Path.Combine(expected, "runner.dll");
        var actualFile = Path.Combine(actual, "runner.dll");
        File.WriteAllText(expectedFile, "identical-content");
        File.WriteAllText(actualFile, "identical-content");
        File.SetLastWriteTimeUtc(expectedFile, new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(actualFile, new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc));
        try
        {
            HarnessBuildFingerprint.RequireCompleteOutputMatches(expected, actual, "physical-tests");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PowerShellRuntimeTreeComparerReportsFullPathsAndUsesSizeAsAMatchingInput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-runtime-tree-script-{Guid.NewGuid():N}");
        var expected = Path.Combine(root, "expected");
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(expected, "changed.dll"), "expected");
        File.WriteAllText(Path.Combine(actual, "changed.dll"), "actual-longer");
        File.WriteAllText(Path.Combine(expected, "expected-only.dll"), "expected-only");
        File.WriteAllText(Path.Combine(actual, "actual-only.dll"), "actual-only");
        var repositoryRoot = PhysicalArtifactPaths.FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "tests-dotnet", "tools", "Compare-RuntimeTrees.ps1");
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ExpectedDirectory");
            startInfo.ArgumentList.Add(expected);
            startInfo.ArgumentList.Add("-ActualDirectory");
            startInfo.ArgumentList.Add(actual);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell comparer did not start.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.AreEqual(1, process.ExitCode, standardError);
            using var document = JsonDocument.Parse(standardOutput);
            var mismatches = document.RootElement.GetProperty("Mismatches").EnumerateArray().ToArray();
            Assert.HasCount(3, mismatches);
            foreach (var mismatch in mismatches)
            {
                var relativePath = mismatch.GetProperty("RelativePath").GetString()!;
                Assert.AreEqual(Path.GetFullPath(Path.Combine(expected, relativePath.Replace('/', Path.DirectorySeparatorChar))), mismatch.GetProperty("ExpectedFullPath").GetString());
                Assert.AreEqual(Path.GetFullPath(Path.Combine(actual, relativePath.Replace('/', Path.DirectorySeparatorChar))), mismatch.GetProperty("ActualFullPath").GetString());
            }

            var script = File.ReadAllText(scriptPath);
            StringAssert.Contains(script, "$expectedEntry.Size -ne $actualEntry.Size");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PhysicalOwnerSelfCheckRequiresPublishedWatchdogApphostAndEntireSelfContainedTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-watchdog-owner-{Guid.NewGuid():N}");
        var running = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved-launcher", "watchdog");
        var fresh = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "session", "generation", "watchdog");
        Directory.CreateDirectory(running);
        WriteRuntimeSkeleton(running, "Moondrop.PhysicalWatchdog", selfContained: true);
        CopyDirectory(running, fresh);
        try
        {
            var apphost = Path.Combine(running, "Moondrop.PhysicalWatchdog.exe");
            HarnessBuildFingerprint.RequirePublishedApphostTree(root, apphost, fresh, "Moondrop.PhysicalWatchdog");
            AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequirePublishedApphostTree(
                    root,
                    Path.Combine(running, "Moondrop.PhysicalWatchdog.dll"),
                    fresh,
                    "Moondrop.PhysicalWatchdog"));
            File.AppendAllText(Path.Combine(fresh, "hostfxr.dll"), "changed");
            AssertEx.ThrowsException<InvalidDataException>(() =>
                HarnessBuildFingerprint.RequirePublishedApphostTree(root, apphost, fresh, "Moondrop.PhysicalWatchdog"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteDurableSession(
        string path,
        string sessionId,
        string token,
        DurablePhysicalPhase phase,
        DateTimeOffset updatedAtUtc)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 3,
            SessionId = sessionId == "session-a" ? new string('A', 32) : new string('B', 32),
            OneRunToken = token,
            Phase = phase,
            UpdatedAtUtc = updatedAtUtc,
            SourceFingerprint = new string('F', 64),
            RuntimeManifestSha256 = new string('E', 64),
            Original = new { Identity = new { DevicePath = "hid://pinned", SerialNumber = "serial" }, Bands = new[] { 1, 2, 3 } },
            Plan = new { Source = "plan" }
        }));
    }

    private static DurableSessionState DurableState(DurablePhysicalPhase phase) => new(
        phase,
        DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
        new string('A', 32),
        "one-run-token",
        new string('A', 64),
        new string('B', 64),
        new string('C', 64));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string CreateMinimalFingerprintRepository()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"moondrop-source-controls-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "ancestor", "repository");
        Directory.CreateDirectory(root);
        foreach (var relative in new[]
                 {
                     "src",
                     "tests-dotnet/Moondrop.Tests",
                     "tests-dotnet/Moondrop.PhysicalTests",
                     "tests-dotnet/Moondrop.PhysicalWatchdog"
                 })
        {
            var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Source.cs"), "namespace FingerprintFixture;");
        }
        File.WriteAllText(Path.Combine(root, "DawnPro.Wpf.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(root, "tests-dotnet", "default.runsettings"), "<RunSettings />");
        File.WriteAllText(Path.Combine(root, "tests-dotnet", "physical.runsettings"), "<RunSettings />");
        var isolation = Path.Combine(root, "tests-dotnet", "build-isolation");
        Directory.CreateDirectory(isolation);
        File.WriteAllText(Path.Combine(isolation, "physical.Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(isolation, "physical.Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(isolation, "physical.Directory.Packages.props"), "<Project />");
        File.WriteAllText(Path.Combine(isolation, "physical.NuGet.Config"), "<configuration />");
        return sandbox;
    }

    private static void AddRequiredRuntimeMetadata(string root)
    {
        foreach (var relative in new[]
                 {
                     "src/Moondrop.Core/packages.lock.json",
                     "src/Moondrop.Hardware/packages.lock.json",
                     "tests-dotnet/Moondrop.PhysicalTests/packages.lock.json",
                     "tests-dotnet/Moondrop.PhysicalWatchdog/packages.lock.json"
                 })
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"version\":1,\"dependencies\":{}}");
        }
        File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.302\"}}");
    }

    private static void WriteApproval(
        string path,
        HarnessFingerprint source,
        HarnessFingerprint runtime,
        string? sourceSha256 = null,
        string? runtimeSha256 = null,
        int? sourceInputCount = null)
    {
        var sourceCounts = HarnessBuildFingerprint.CountSourceInputs(source);
        var runtimeCounts = HarnessBuildFingerprint.CountRuntimeInputs(runtime);
        File.WriteAllText(path, JsonSerializer.Serialize(new PhysicalRuntimeApproval(
            1,
            PhysicalRuntimeBuildPlan.RuntimeIdentifier,
            sourceSha256 ?? source.AggregateSha256,
            sourceInputCount ?? sourceCounts.TotalInputCount,
            sourceCounts.SourcePresenceSentinelCount,
            sourceCounts.SourceContentInputCount,
            runtimeSha256 ?? runtime.AggregateSha256,
            runtimeCounts.TotalInputCount,
            runtimeCounts.RunnerTreeInputCount,
            runtimeCounts.WatchdogTreeInputCount,
            runtimeCounts.MetadataInputCount)));
    }

    private static void WriteRuntimeSkeleton(string directory, string applicationName, bool selfContained)
    {
        foreach (var name in new[]
                 {
                     $"{applicationName}.exe",
                     $"{applicationName}.dll",
                     "coreclr.dll",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "System.Private.CoreLib.dll"
                 })
            File.WriteAllText(Path.Combine(directory, name), name);
        File.WriteAllText(Path.Combine(directory, $"{applicationName}.deps.json"), "{}");
        var runtimeOptions = selfContained
            ? "\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.10\"}]"
            : "\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.10\"}";
        File.WriteAllText(
            Path.Combine(directory, $"{applicationName}.runtimeconfig.json"),
            $"{{\"runtimeOptions\":{{\"tfm\":\"net10.0\",{runtimeOptions}}}}}");
    }

    private static string InstalledAuditedDotnetPath()
    {
        for (var directory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "dotnet.exe");
            var profile = directory.Parent?.FullName;
            if (File.Exists(candidate) && profile is not null &&
                Directory.Exists(Path.Combine(profile, ".nuget", "packages")))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the installed audited dotnet executable and its offline package cache.");
    }

    private static void AssertSourceFingerprintChangesWhenControlAppears(string fileName, bool inAncestor = false)
    {
        var sandbox = CreateMinimalFingerprintRepository();
        var root = Path.Combine(sandbox, "ancestor", "repository");
        var controlDirectory = inAncestor ? Path.GetDirectoryName(root)! : root;
        try
        {
            var baseline = HarnessBuildFingerprint.CaptureSource(root);
            File.WriteAllText(Path.Combine(controlDirectory, fileName), "<Project />");

            var changed = HarnessBuildFingerprint.CaptureSource(root);

            Assert.AreNotEqual(baseline.AggregateSha256, changed.AggregateSha256);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}

file sealed class InjectedReparseInspector(params string[] reparsePaths) : ITrustedPhysicalPathInspector
{
    private readonly HashSet<string> _reparsePaths = reparsePaths
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => true;

    public FileAttributes GetAttributes(string path) =>
        _reparsePaths.Contains(Path.GetFullPath(path)) ? FileAttributes.ReparsePoint : FileAttributes.Normal;
}

file sealed class DanglingInjectedReparseInspector(string reparsePath) : ITrustedPhysicalPathInspector
{
    private readonly string _reparsePath = Path.GetFullPath(reparsePath);

    public bool Exists(string path) => !string.Equals(Path.GetFullPath(path), _reparsePath, StringComparison.OrdinalIgnoreCase);

    public FileAttributes GetAttributes(string path) =>
        string.Equals(Path.GetFullPath(path), _reparsePath, StringComparison.OrdinalIgnoreCase)
            ? FileAttributes.ReparsePoint
            : FileAttributes.Normal;
}

file sealed class SimulatedPhysicalProcessState
{
    public bool Active { get; set; }
}

file sealed class SimulatedPhysicalSourceProtectionLayer(SimulatedPhysicalProcessState processState)
    : IPhysicalSourceProtectionLayer
{
    private readonly SimulatedPhysicalProcessState _processState = processState;
    private string? _sourceRoot;
    public int InvocationCount { get; private set; }
    public int RequireProtectedCount { get; private set; }
    public bool MutationDenied { get; private set; }
    public bool Released { get; private set; }
    public bool ProcessWasActiveAtRelease { get; private set; }
    public bool IsProtected { get; private set; }

    public IPhysicalSourceProtectionLease ProtectAndVerify(string sourceRoot)
    {
        InvocationCount++;
        _sourceRoot = Path.GetFullPath(sourceRoot);
        IsProtected = true;
        return new Lease(this);
    }

    public void AttemptNonCooperatingMutation(string path)
    {
        if (!IsProtected)
            throw new InvalidOperationException("The simulated staged source was not protected.");
        if (_sourceRoot is null || !Path.GetFullPath(path).StartsWith(
                _sourceRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The simulated mutation did not target the protected staged source.");
        try
        {
            throw new UnauthorizedAccessException("simulated ACL denial");
        }
        catch (UnauthorizedAccessException)
        {
            MutationDenied = true;
        }
    }

    private sealed class Lease(SimulatedPhysicalSourceProtectionLayer owner) : IPhysicalSourceProtectionLease
    {
        public void RequireProtected()
        {
            if (!owner.IsProtected)
                throw new IOException("Simulated staged source protection was released early.");
            owner.RequireProtectedCount++;
        }

        public ValueTask DisposeAsync()
        {
            owner.ProcessWasActiveAtRelease = owner._processState.Active;
            owner.IsProtected = false;
            owner.Released = true;
            return ValueTask.CompletedTask;
        }
    }
}

file sealed class SequenceProbeIdentitySnapshotReader(params PhysicalProbeProcessIdentity[] identities)
    : IPhysicalProbeProcessIdentitySnapshotReader
{
    private readonly Queue<PhysicalProbeProcessIdentity> _identities = new(identities);

    public PhysicalProbeProcessIdentity Read(int processId) =>
        _identities.Count == 0
            ? throw new InvalidOperationException("Probe process disappeared during identity capture.")
            : _identities.Dequeue();
}

file sealed class SequenceObservedPhysicalProcessSnapshotReader(params ObservedPhysicalProcess[] identities)
    : IObservedPhysicalProcessSnapshotReader
{
    private readonly Queue<ObservedPhysicalProcess> _identities = new(identities);

    public ObservedPhysicalProcess Read(int processId) =>
        _identities.Count == 0
            ? throw new InvalidOperationException("Owned process disappeared during identity capture.")
            : _identities.Dequeue();
}

file sealed class SimulatedPhysicalBuildExecutor(
    SimulatedPhysicalProcessState processState,
    SimulatedPhysicalSourceProtectionLayer protection,
    bool transientStageTamper = false,
    bool changePublishedRuntimeFile = false,
    bool changeRuntimeDuringOfflineTopologySmoke = false) : IPhysicalRuntimeBuildExecutor
{
    public int InvocationCount { get; private set; }
    public bool TransientTamperRestored { get; private set; }
    public bool PublishedFromTamperedSource { get; private set; }
    public int OfflineTopologySmokeCount { get; private set; }
    public string? OfflineTopologyRuntimeManifestSha256 { get; private set; }
    public string? OfflineTopologyPhysicalApphostPath { get; private set; }
    public string? OfflineTopologyWatchdogApphostPath { get; private set; }
    public bool RuntimeChangedDuringOfflineTopologySmoke { get; private set; }
    public Task RunDotnetAsync(
        string dotnetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        InvocationCount++;
        processState.Active = true;
        try
        {
            protection.AttemptNonCooperatingMutation(Path.Combine(workingDirectory, "src", "Source.cs"));
            if (string.Equals(arguments[0], "publish", StringComparison.Ordinal))
            {
                var outputIndex = arguments.ToList().IndexOf("-o");
                var output = arguments[outputIndex + 1];
                Directory.CreateDirectory(output);
                var application = arguments.Any(argument => argument.Contains("PhysicalTests", StringComparison.Ordinal))
                    ? "Moondrop.PhysicalTests"
                    : "Moondrop.PhysicalWatchdog";
                var sourcePath = Path.Combine(workingDirectory, "src", "Source.cs");
                var originalSource = File.ReadAllBytes(sourcePath);
                try
                {
                    if (transientStageTamper && string.Equals(application, "Moondrop.PhysicalTests", StringComparison.Ordinal))
                        File.WriteAllText(sourcePath, "namespace TransientSameOwnerTamper;");
                    WriteRuntimeSkeleton(output, application);
                    if (transientStageTamper && string.Equals(application, "Moondrop.PhysicalTests", StringComparison.Ordinal))
                    {
                        File.AppendAllText(Path.Combine(output, $"{application}.dll"), File.ReadAllText(sourcePath));
                        PublishedFromTamperedSource = true;
                    }
                    if (changePublishedRuntimeFile && string.Equals(application, "Moondrop.PhysicalTests", StringComparison.Ordinal))
                        File.AppendAllText(Path.Combine(output, "hostpolicy.dll"), "changed-after-approved-source");
                }
                finally
                {
                    if (transientStageTamper && string.Equals(application, "Moondrop.PhysicalTests", StringComparison.Ordinal))
                    {
                        File.WriteAllBytes(sourcePath, originalSource);
                        TransientTamperRestored = true;
                    }
                }
            }
            return Task.CompletedTask;
        }
        finally
        {
            processState.Active = false;
        }
    }

    public Task RunStartupSmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string outputDirectory,
        PhysicalRuntimeStartupSmoke smoke,
        CancellationToken cancellationToken)
    {
        Assert.IsTrue(protection.IsProtected);
        Assert.IsFalse(processState.Active);
        return Task.CompletedTask;
    }

    public Task RunOfflineTopologySmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string physicalApphostPath,
        string watchdogApphostPath,
        HarnessFingerprint runtimeManifest,
        CancellationToken cancellationToken)
    {
        Assert.IsTrue(protection.IsProtected);
        Assert.IsFalse(processState.Active);
        OfflineTopologySmokeCount++;
        OfflineTopologyRuntimeManifestSha256 = runtimeManifest.AggregateSha256;
        OfflineTopologyPhysicalApphostPath = physicalApphostPath;
        OfflineTopologyWatchdogApphostPath = watchdogApphostPath;
        if (changeRuntimeDuringOfflineTopologySmoke)
        {
            File.AppendAllText(
                Path.Combine(Path.GetDirectoryName(physicalApphostPath)!, "Moondrop.PhysicalTests.dll"),
                "changed-during-offline-topology-smoke");
            RuntimeChangedDuringOfflineTopologySmoke = true;
        }
        return Task.CompletedTask;
    }

    private static void WriteRuntimeSkeleton(string directory, string applicationName)
    {
        foreach (var name in new[]
                 {
                     $"{applicationName}.exe",
                     $"{applicationName}.dll",
                     "coreclr.dll",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "System.Private.CoreLib.dll"
                 })
            File.WriteAllText(Path.Combine(directory, name), name);
        File.WriteAllText(Path.Combine(directory, $"{applicationName}.deps.json"), "{}");
        File.WriteAllText(
            Path.Combine(directory, $"{applicationName}.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.10\"}]}}");
    }
}
