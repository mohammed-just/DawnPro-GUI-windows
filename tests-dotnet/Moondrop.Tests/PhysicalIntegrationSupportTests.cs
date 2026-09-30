using Moondrop.PhysicalWatchdog;
using System.Diagnostics;

namespace Moondrop.Tests;

[TestClass]
public sealed class PhysicalIntegrationSupportTests
{
    [TestMethod]
    public void PhysicalPhaseDiagnosticsRedactSessionSecretsAndEscapeControls()
    {
        const string oneRunToken = "one-run-RAW-SECRET";
        const string confirmation = "confirmation-RAW-SECRET";
        var diagnostic = PhysicalPhaseDiagnostic.Sanitize(
            $"failure\r\nFORGED=1\0 token={oneRunToken}; confirmation={confirmation}",
            oneRunToken,
            confirmation);

        Assert.IsFalse(diagnostic.Contains(oneRunToken, StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Contains(confirmation, StringComparison.Ordinal));
        Assert.AreEqual(-1, diagnostic.IndexOf('\r'));
        Assert.AreEqual(-1, diagnostic.IndexOf('\n'));
        Assert.AreEqual(-1, diagnostic.IndexOf('\0'));
        StringAssert.Contains(diagnostic, "[REDACTED]");
        StringAssert.Contains(diagnostic, "\\u000D\\u000A");
    }

    [TestMethod]
    public void WindowsPhysicalIdentityProviderReadsAnotherRealProcessUnderNet10()
    {
        // Child-side EXECUTE lineage gate: the watchdog-launched runner must authenticate its own
        // and its parent's identity through WindowsPhysicalProcessIdentityProvider. The historical
        // dynamic-COM WMI implementation deterministically threw COMException 0x80004005 under
        // .NET 10 for these reads, blocking EXECUTE immediately after the child started. This
        // regression requires the reader to obtain another real process's full identity; the parent
        // relationship of a truly spawned child is the independent oracle.
        DateTimeOffset expectedStart;
        using (var spawned = Process.Start("cmd.exe", "/c ping -n 3 127.0.0.1 >nul")!)
        {
            try
            {
                expectedStart = spawned.StartTime.ToUniversalTime();
                var expectedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var provider = new WindowsPhysicalProcessIdentityProvider();
                var identity = provider.Get(spawned.Id);
                Assert.AreEqual(spawned.Id, identity.ProcessId, "The reader must report the requested PID.");
                Assert.AreEqual(Environment.ProcessId, identity.ParentProcessId, "The spawned child's parent must be the test host.");
                Assert.AreEqual(expectedStart, identity.StartedAtUtc, "The reader must report the exact process creation identity.");
                Assert.IsTrue(string.Equals(Path.GetFullPath(expectedExe), Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase), "The executable path must match the spawned child.");
            }
            finally
            {
                try { spawned.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void WindowsProcessConflictQueryReadsAnotherRealProcessRowUnderNet10()
    {
        // Process-conflict inspection used by the physical preflight enumerates other processes via
        // WindowsPhysicalProcessQuery.QueryWmi. The historical dynamic-COM WMI implementation threw
        // COMException 0x80004005 when reading any row's properties under .NET 10. This requires the
        // managed replacement to enumerate and read another real process's row.
        var currentName = Process.GetCurrentProcess().ProcessName;
        var query = new WindowsPhysicalProcessQuery();
        var wql = $"SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE '%{currentName}%'";
        var rows = query.QueryWmi(wql);
        try
        {
            Assert.IsGreaterThanOrEqualTo(rows.Count, 1, "The narrow query must match at least the calling process.");
            IPhysicalWmiProcess? selfRow = null;
            foreach (var row in rows)
            {
                var pid = Convert.ToInt32(row.ReadProperty("ProcessId"), System.Globalization.CultureInfo.InvariantCulture);
                if (pid == Environment.ProcessId)
                {
                    selfRow = row;
                    break;
                }
            }
            Assert.IsNotNull(selfRow, "The query must include the calling process row.");
            var name = Convert.ToString(selfRow!.ReadProperty("Name"), System.Globalization.CultureInfo.InvariantCulture);
            StringAssert.Contains(name, currentName, StringComparison.OrdinalIgnoreCase);
            var commandLine = Convert.ToString(selfRow.ReadProperty("CommandLine"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsNotNull(commandLine, "CommandLine must be readable for the calling process.");
        }
        finally
        {
            foreach (var row in rows)
                row.Dispose();
        }
    }

    [TestMethod]
    public void DurablePhysicalDiagnosticsNeverPersistRawExceptionSecretsOrControls()
    {
        const string secret = "one-run-DURABLE-SECRET";
        var error = new InvalidOperationException($@"failed at C:\sessions\{secret}\session.json" + "\r\nFORGED=1");

        var diagnostic = PhysicalDurableDiagnostic.FromException(error, secret)!;

        Assert.AreEqual(-1, diagnostic.IndexOf(secret, StringComparison.Ordinal));
        Assert.AreEqual(-1, diagnostic.IndexOf('\r'));
        Assert.AreEqual(-1, diagnostic.IndexOf('\n'));
        StringAssert.Contains(diagnostic, "[REDACTED]");
        StringAssert.Contains(diagnostic, "\\u000D\\u000A");
    }

    [TestMethod]
    public void CoherentProcessIdentityRejectsPidReuseAndMidReadDrift()
    {
        var started = DateTimeOffset.Parse("2026-08-09T09:00:00Z");
        var stable = new PhysicalProcessIdentity(200, 100, started, @"C:\runtime\Moondrop.PhysicalWatchdog.exe");
        var reused = stable with { StartedAtUtc = started.AddSeconds(1) };
        var remapped = stable with { ProcessId = 999 };

        var reuseError = AssertEx.ThrowsException<InvalidOperationException>(() =>
            new CoherentPhysicalProcessIdentityProvider(new SequenceIdentitySnapshotReader(stable, reused)).Get(200));
        StringAssert.Contains(reuseError.Message, "drift");

        var remapError = AssertEx.ThrowsException<InvalidOperationException>(() =>
            new CoherentPhysicalProcessIdentityProvider(new SequenceIdentitySnapshotReader(stable, remapped)).Get(200));
        StringAssert.Contains(remapError.Message, "drift");
    }

    [TestMethod]
    public void DiagnosticsEscapeControlsAndCannotInjectLinesOrLeakExplicitSecrets()
    {
        const string secret = "raw-secret-value";
        var sanitized = DiagnosticText.Sanitize("path=C:\\safe\r\nFORGED=1\0token=" + secret, secret);
        Assert.AreEqual(-1, sanitized.IndexOf('\r'));
        Assert.AreEqual(-1, sanitized.IndexOf('\n'));
        Assert.AreEqual(-1, sanitized.IndexOf('\0'));
        Assert.IsFalse(sanitized.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "\\u000D\\u000A");
        StringAssert.Contains(sanitized, "[REDACTED]");

        var root = Path.Combine(Path.GetTempPath(), $"moondrop-diagnostic-injection-{Guid.NewGuid():N}");
        var injectedPath = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "candidate", "physical-tests", "Moondrop.PhysicalTests.exe\r\nFORGED=1");
        var result = PhysicalWatchdogProcessGate.Evaluate(
            authorization: null,
            root,
            new FakeProcessIdentityProvider(new PhysicalProcessIdentity(300, 200, DateTimeOffset.UtcNow, injectedPath)));
        Assert.AreEqual(-1, result.Diagnostic.IndexOf('\r'));
        Assert.AreEqual(-1, result.Diagnostic.IndexOf('\n'));
        StringAssert.Contains(result.Diagnostic, "\\u000D\\u000A");
    }
    [TestMethod]
    public void ProcessConflictGuardPassesWhenNarrowCandidateQueriesFindNothing()
    {
        var query = new FakePhysicalProcessQuery();

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        Assert.IsEmpty(conflicts);
        CollectionAssert.AreEqual(new[] { "Moondrop.Wpf" }, query.NativeNames.ToArray());
        Assert.HasCount(2, query.WmiQueries);
        Assert.IsTrue(query.WmiQueries.All(item => item.Contains(" WHERE ", StringComparison.Ordinal)));
        Assert.IsTrue(query.WmiQueries.Any(item => item.Contains("Name LIKE '%DawnPro%'", StringComparison.Ordinal)));
        Assert.IsTrue(query.WmiQueries.Any(item => item.Contains("Name = 'python.exe'", StringComparison.Ordinal)));
        Assert.IsTrue(query.WmiQueries.Any(item => item.Contains("Name = 'pythonw.exe'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProcessConflictGuardReportsKnownCompiledWpfApp()
    {
        var query = new FakePhysicalProcessQuery
        {
            NativeProcesses = [new PhysicalNativeProcess(410, "Moondrop.Wpf.exe")]
        };

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        CollectionAssert.AreEqual(new[] { "Moondrop.Wpf.exe (PID 410)" }, conflicts.ToArray());
    }

    [TestMethod]
    public void ProcessConflictGuardReportsDawnProLikeCompiledCandidate()
    {
        var query = new FakePhysicalProcessQuery
        {
            DawnProRows =
            [
                new FakePhysicalWmiProcess(new Dictionary<string, object?>
                {
                    ["ProcessId"] = (uint)420,
                    ["Name"] = "DawnPro-Legacy.exe"
                })
            ]
        };

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        CollectionAssert.AreEqual(new[] { "DawnPro-Legacy.exe (PID 420)" }, conflicts.ToArray());
    }

    [TestMethod]
    public void ProcessConflictGuardReportsLegacyPythonMoondropApp()
    {
        var query = new FakePhysicalProcessQuery
        {
            PythonRows =
            [
                new FakePhysicalWmiProcess(new Dictionary<string, object?>
                {
                    ["ProcessId"] = (uint)430,
                    ["Name"] = "pythonw.exe",
                    ["CommandLine"] = "pythonw.exe \"C:\\Users\\mohammed\\Documents\\moondrop gui\\main.py\""
                })
            ]
        };

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        CollectionAssert.AreEqual(new[] { "pythonw.exe (PID 430)" }, conflicts.ToArray());
    }

    [TestMethod]
    public void ProcessConflictGuardFailsClosedPreciselyWhenRelevantCandidatePidIsInaccessible()
    {
        var query = new FakePhysicalProcessQuery
        {
            PythonRows =
            [
                new FakePhysicalWmiProcess(new Dictionary<string, object?>
                {
                    ["ProcessId"] = new System.Runtime.InteropServices.COMException("Unspecified error")
                })
            ]
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalProcessGuard.FindConflictingApps(query));

        StringAssert.Contains(error.Message, "relevant Python candidate");
        StringAssert.Contains(error.Message, "ProcessId");
        StringAssert.Contains(error.Message, "before HID access");
        Assert.IsInstanceOfType<System.Runtime.InteropServices.COMException>(error.InnerException);
    }

    [TestMethod]
    public void ProcessConflictGuardIgnoresCandidateThatExitedDuringPropertyInspection()
    {
        var query = new FakePhysicalProcessQuery
        {
            PythonRows =
            [
                new FakePhysicalWmiProcess(new Dictionary<string, object?>
                {
                    ["ProcessId"] = (uint)440,
                    ["Name"] = new System.Runtime.InteropServices.COMException("process no longer exists")
                })
            ]
        };

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        Assert.IsEmpty(conflicts);
        CollectionAssert.AreEqual(new[] { 440 }, query.RunningChecks.ToArray());
    }

    [TestMethod]
    public void ProcessConflictGuardFailsClosedWhenRelevantRunningCandidateDataIsInaccessible()
    {
        var query = new FakePhysicalProcessQuery
        {
            RunningProcessIds = [450],
            PythonRows =
            [
                new FakePhysicalWmiProcess(new Dictionary<string, object?>
                {
                    ["ProcessId"] = (uint)450,
                    ["Name"] = "python.exe",
                    ["CommandLine"] = new System.Runtime.InteropServices.COMException("access denied")
                })
            ]
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalProcessGuard.FindConflictingApps(query));

        StringAssert.Contains(error.Message, "CommandLine");
        StringAssert.Contains(error.Message, "relevant Python candidate PID 450");
        StringAssert.Contains(error.Message, "before HID access");
        Assert.IsInstanceOfType<System.Runtime.InteropServices.COMException>(error.InnerException);
    }

    [TestMethod]
    public void ProcessConflictGuardNeverDereferencesUnrelatedInaccessibleProcessRows()
    {
        var unrelated = new FakePhysicalWmiProcess(new Dictionary<string, object?>
        {
            ["ProcessId"] = new System.Runtime.InteropServices.COMException("Unspecified error")
        });
        var query = new FakePhysicalProcessQuery { UnrelatedRows = [unrelated] };

        var conflicts = PhysicalProcessGuard.FindConflictingApps(query);

        Assert.IsEmpty(conflicts);
        Assert.AreEqual(0, unrelated.ReadCount);
        Assert.IsTrue(query.WmiQueries.All(item => item.Contains(" WHERE ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProcessConflictGuardFailsClosedPreciselyWhenKnownCompiledLookupIsInaccessible()
    {
        var query = new FakePhysicalProcessQuery
        {
            NativeError = new System.ComponentModel.Win32Exception(5, "Access is denied")
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalProcessGuard.FindConflictingApps(query));

        StringAssert.Contains(error.Message, "Moondrop.Wpf.exe");
        StringAssert.Contains(error.Message, "exact-name process lookup");
        StringAssert.Contains(error.Message, "before HID access");
        Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(error.InnerException);
    }

    [TestMethod]
    public void ProcessConflictGuardFailsClosedPreciselyWhenNarrowCandidateQueryFails()
    {
        var query = new FakePhysicalProcessQuery
        {
            PythonQueryError = new System.Runtime.InteropServices.COMException("WMI provider failure")
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalProcessGuard.FindConflictingApps(query));

        StringAssert.Contains(error.Message, "narrow Python candidate query");
        StringAssert.Contains(error.Message, "before HID access");
        Assert.IsInstanceOfType<System.Runtime.InteropServices.COMException>(error.InnerException);
    }

    [TestMethod]
    public void DefaultUnfilteredRunExcludesPhysicalTestsEvenWhenOptInsAreInherited()
    {
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var defaultSettings = System.Xml.Linq.XDocument.Load(Path.Combine(root, "tests-dotnet", "default.runsettings"));
        var physicalSettings = System.Xml.Linq.XDocument.Load(Path.Combine(root, "tests-dotnet", "physical.runsettings"));
        var filter = defaultSettings.Descendants("TestCaseFilter").Single().Value;

        StringAssert.Contains(filter, "TestCategory!=PhysicalHardwarePrepare");
        StringAssert.Contains(filter, "TestCategory!=PhysicalHardwareRecovery");
        StringAssert.Contains(filter, "TestCategory!=PhysicalHardware");
        Assert.IsFalse(PhysicalRunSettingsGate.IsDedicated(
            defaultSettings.Descendants("Parameter").Single(element => (string?)element.Attribute("name") == "PhysicalHarnessEnabled").Attribute("value")?.Value,
            prepareOptIn: "1",
            executeOptIn: "1",
            recoveryOptIn: "1"));
        Assert.IsTrue(PhysicalRunSettingsGate.IsDedicated(
            physicalSettings.Descendants("Parameter").Single(element => (string?)element.Attribute("name") == "PhysicalHarnessEnabled").Attribute("value")?.Value,
            prepareOptIn: "1",
            executeOptIn: "1",
            recoveryOptIn: "1"));
    }

    [TestMethod]
    public async Task ExecuteAndRecoveryRequireTheActualDirectWatchdogParent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-parent-auth-{Guid.NewGuid():N}");
        var token = "dawn-pro2-watchdog-0123456789abcdef0123456789abcdef";
        var sessionId = "0123456789abcdef0123456789abcdef";
        var binding = new PhysicalWatchdogSessionBinding(
            sessionId,
            "one-run-token",
            new string('A', 64),
            new string('B', 64),
            new string('C', 64));
        var heartbeat = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", token, "heartbeat.json");
        var watchdogExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved-launcher", "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var runnerExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved-launcher", "physical-tests", "Moondrop.PhysicalTests.exe");
        var started = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
        Directory.CreateDirectory(Path.GetDirectoryName(watchdogExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(runnerExe)!);
        await File.WriteAllBytesAsync(watchdogExe, [1]);
        await File.WriteAllBytesAsync(runnerExe, [2]);
        var manifest = RuntimeApphostManifestBinding.CreateManifest(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2])),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
        binding = binding with { RuntimeManifestSha256 = manifest.AggregateSha256 };
        var manifestPath = PhysicalRuntimeManifestStore.WriteCreateNew(Path.GetDirectoryName(Path.GetDirectoryName(runnerExe)!)!, manifest);
        await PhysicalArtifactWriter.WriteJsonAsync(
            heartbeat,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                200,
                started,
                watchdogExe,
                token,
                binding.SessionId,
                binding.OneRunToken,
                binding.SourceFingerprint,
                binding.RuntimeManifestSha256,
                binding.LineageFingerprint,
                OwnerExecutableSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]))));
        var authorization = new PhysicalWatchdogAuthorization(token, heartbeat, binding, 200, started, watchdogExe, manifestPath);
        var matching = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, DateTimeOffset.UtcNow, runnerExe),
            new PhysicalProcessIdentity(200, 100, started, watchdogExe));
        var forgedRawDotnet = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 201, DateTimeOffset.UtcNow, Path.Combine(root, "testhost.exe")),
            new PhysicalProcessIdentity(201, 100, started, Path.Combine(root, "dotnet.exe")));
        try
        {
            Assert.IsTrue(PhysicalWatchdogProcessGate.IsAuthorized(authorization, root, matching));
            Assert.IsFalse(PhysicalWatchdogProcessGate.IsAuthorized(authorization, root, forgedRawDotnet));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnexpectedIntermediaryRejectionReportsTheCompleteRedactedLineage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-lineage-diagnostic-{Guid.NewGuid():N}");
        var token = "dawn-pro2-watchdog-0123456789abcdef0123456789abcdef";
        var oneRunToken = "one-run-secret-must-not-leak";
        var binding = new PhysicalWatchdogSessionBinding(
            "0123456789abcdef0123456789abcdef",
            oneRunToken,
            new string('A', 64),
            new string('B', 64),
            new string('C', 64));
        var heartbeat = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", token, "heartbeat.json");
        var watchdogExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved-launcher", "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var intermediaryExe = Path.Combine(root, "unexpected", "dotnet.exe");
        var runnerExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved-launcher", "physical-tests", "Moondrop.PhysicalTests.exe");
        var started = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
        Directory.CreateDirectory(Path.GetDirectoryName(watchdogExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(intermediaryExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(runnerExe)!);
        await File.WriteAllBytesAsync(watchdogExe, [1]);
        await File.WriteAllBytesAsync(intermediaryExe, [2]);
        await File.WriteAllBytesAsync(runnerExe, [3]);
        var manifest = RuntimeApphostManifestBinding.CreateManifest(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([3])),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
        binding = binding with { RuntimeManifestSha256 = manifest.AggregateSha256 };
        var manifestPath = PhysicalRuntimeManifestStore.WriteCreateNew(Path.GetDirectoryName(Path.GetDirectoryName(runnerExe)!)!, manifest);
        await PhysicalArtifactWriter.WriteJsonAsync(
            heartbeat,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                200,
                started,
                watchdogExe,
                token,
                binding.SessionId,
                binding.OneRunToken,
                binding.SourceFingerprint,
                binding.RuntimeManifestSha256,
                binding.LineageFingerprint));
        var authorization = new PhysicalWatchdogAuthorization(token, heartbeat, binding, 200, started, watchdogExe, manifestPath);
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 201, started.AddSeconds(2), runnerExe),
            new PhysicalProcessIdentity(201, 200, started.AddSeconds(1), intermediaryExe),
            new PhysicalProcessIdentity(200, 100, started, watchdogExe));

        try
        {
            var result = PhysicalWatchdogProcessGate.Evaluate(authorization, root, identities);

            Assert.IsFalse(result.IsAuthorized);
            StringAssert.Contains(result.Diagnostic, "predicate=direct-parent-pid");
            StringAssert.Contains(result.Diagnostic, "expected.watchdog.pid=200");
            StringAssert.Contains(result.Diagnostic, "actual.runner.pid=300");
            StringAssert.Contains(result.Diagnostic, "actual.runner.parentPid=201");
            StringAssert.Contains(result.Diagnostic, Path.GetFullPath(watchdogExe));
            StringAssert.Contains(result.Diagnostic, Path.GetFullPath(intermediaryExe));
            StringAssert.Contains(result.Diagnostic, Path.GetFullPath(runnerExe));
            StringAssert.Contains(result.Diagnostic, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
            StringAssert.Contains(result.Diagnostic, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2])));
            StringAssert.Contains(result.Diagnostic, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([3])));
            Assert.IsFalse(result.Diagnostic.Contains(token, StringComparison.Ordinal));
            Assert.IsFalse(result.Diagnostic.Contains(oneRunToken, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DirectParentDiagnosticUsesManifestAuthoritativeExpectedRoleIdentities()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var expectedRunnerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2]));
        var actualRunnerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([9]));
        await File.WriteAllBytesAsync(fixture.RunnerPath, [9]);
        var intermediaryPath = Path.Combine(fixture.Root, "unexpected", "dotnet.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(intermediaryPath)!);
        await File.WriteAllBytesAsync(intermediaryPath, [8]);
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 201, fixture.StartedAtUtc.AddSeconds(2), fixture.RunnerPath),
            new PhysicalProcessIdentity(201, 200, fixture.StartedAtUtc.AddSeconds(1), intermediaryPath),
            new PhysicalProcessIdentity(200, 100, fixture.StartedAtUtc, fixture.WatchdogPath));

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=direct-parent-pid");
        StringAssert.Contains(result.Diagnostic, $"expected.runner.path={Path.GetFullPath(fixture.RunnerPath)}");
        StringAssert.Contains(result.Diagnostic, $"expected.runner.sha256={expectedRunnerHash}");
        StringAssert.Contains(result.Diagnostic, $"actual.runner.path={Path.GetFullPath(fixture.RunnerPath)}");
        StringAssert.Contains(result.Diagnostic, $"actual.runner.sha256={actualRunnerHash}");
    }

    [TestMethod]
    public async Task MalformedRuntimeManifestReportsAnExactManifestPredicate()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Authorization.RuntimeManifestPath,
            "{\"Algorithm\":\"SHA-256\",\"AggregateSha256\":\"" + fixture.Binding.RuntimeManifestSha256 + "\",\"Files\":null}");

        var result = PhysicalWatchdogProcessGate.Evaluate(
            fixture.Authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=runtime-manifest-schema");
        Assert.IsFalse(result.Diagnostic.Contains("predicate=process-identity-readable", StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WatchdogExecutableHashMismatchReportsExpectedAndActualHashes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-lineage-hash-{Guid.NewGuid():N}");
        var token = "dawn-pro2-watchdog-0123456789abcdef0123456789abcdef";
        var binding = new PhysicalWatchdogSessionBinding(
            "0123456789abcdef0123456789abcdef",
            "redacted-one-run-secret",
            new string('A', 64),
            new string('B', 64),
            new string('C', 64));
        var heartbeat = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", token, "heartbeat.json");
        var watchdogExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved", "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var runnerExe = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved", "physical-tests", "Moondrop.PhysicalTests.exe");
        var started = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
        Directory.CreateDirectory(Path.GetDirectoryName(watchdogExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(runnerExe)!);
        await File.WriteAllBytesAsync(watchdogExe, [1]);
        await File.WriteAllBytesAsync(runnerExe, [3]);
        var approvedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]));
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2]));
        var manifest = RuntimeApphostManifestBinding.CreateManifest(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([3])),
            approvedHash);
        binding = binding with { RuntimeManifestSha256 = manifest.AggregateSha256 };
        var manifestPath = PhysicalRuntimeManifestStore.WriteCreateNew(Path.GetDirectoryName(Path.GetDirectoryName(runnerExe)!)!, manifest);
        await PhysicalArtifactWriter.WriteJsonAsync(
            heartbeat,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                200,
                started,
                watchdogExe,
                token,
                binding.SessionId,
                binding.OneRunToken,
                binding.SourceFingerprint,
                binding.RuntimeManifestSha256,
                binding.LineageFingerprint,
                OwnerExecutableSha256: new string('F', 64)));
        await File.WriteAllBytesAsync(watchdogExe, [2]);
        var authorization = new PhysicalWatchdogAuthorization(token, heartbeat, binding, 200, started, watchdogExe, manifestPath);
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, started.AddSeconds(1), runnerExe),
            new PhysicalProcessIdentity(200, 100, started, watchdogExe));

        try
        {
            var result = PhysicalWatchdogProcessGate.Evaluate(authorization, root, identities);

            Assert.IsFalse(result.IsAuthorized);
            StringAssert.Contains(result.Diagnostic, "predicate=watchdog-manifest-sha256");
            StringAssert.Contains(result.Diagnostic, $"expected.watchdog.sha256={approvedHash}");
            StringAssert.Contains(result.Diagnostic, $"actual.watchdog.sha256={actualHash}");
            Assert.IsFalse(result.Diagnostic.Contains(token, StringComparison.Ordinal));
            Assert.IsFalse(result.Diagnostic.Contains(binding.OneRunToken, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StaleOrReusedWatchdogPidReportsTheStartTimePredicate()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var staleAuthorization = fixture.Authorization with
        {
            ParentStartedAtUtc = fixture.StartedAtUtc.AddMinutes(-5)
        };

        var result = PhysicalWatchdogProcessGate.Evaluate(
            staleAuthorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=watchdog-start-time");
        StringAssert.Contains(result.Diagnostic, $"expected.watchdog.startedAtUtc={staleAuthorization.ParentStartedAtUtc:O}");
        StringAssert.Contains(result.Diagnostic, $"actual.watchdog.startedAtUtc={fixture.StartedAtUtc:O}");
        StringAssert.Contains(result.Diagnostic, "actual.runner.pid=300");
        StringAssert.Contains(result.Diagnostic, "actual.runner.parentPid=200");
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WrongWatchdogExecutablePathReportsCanonicalExpectedAndActualIdentity()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var wrongPath = Path.Combine(fixture.Root, "wrapper", "Moondrop.PhysicalWatchdog.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongPath)!);
        await File.WriteAllBytesAsync(wrongPath, [9]);
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, fixture.StartedAtUtc.AddSeconds(1), fixture.RunnerPath),
            new PhysicalProcessIdentity(200, 100, fixture.StartedAtUtc, wrongPath));

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=watchdog-executable-path");
        StringAssert.Contains(result.Diagnostic, $"expected.runner.path={Path.GetFullPath(fixture.RunnerPath)}");
        StringAssert.Contains(result.Diagnostic, $"actual.runner.path={Path.GetFullPath(fixture.RunnerPath)}");
        StringAssert.Contains(result.Diagnostic, $"expected.watchdog.path={Path.GetFullPath(fixture.WatchdogPath)}");
        StringAssert.Contains(result.Diagnostic, $"actual.watchdog.path={Path.GetFullPath(wrongPath)}");
        StringAssert.Contains(result.Diagnostic, "actual.runner.pid=300");
        StringAssert.Contains(result.Diagnostic, "actual.watchdog.pid=200");
        StringAssert.Contains(result.Diagnostic, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
    }

    [TestMethod]
    public async Task SameSessionApprovedWatchdogInAnotherIdenticalRuntimeTreeIsAccepted()
    {
        // The supervising watchdog may live in a DIFFERENT tree of the SAME approved runtime
        // (for example the session prepare tree vs the fresh execute tree built during EXECUTE).
        // Both trees must carry the identical approved runtime manifest; a strict runner-tree
        // path requirement alone would make the production EXECUTE lineage gate unreachable.
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-lineage-split-tree-{Guid.NewGuid():N}");
        var token = "dawn-pro2-watchdog-0123456789abcdef0123456789abcdef";
        var binding = new PhysicalWatchdogSessionBinding(
            "0123456789abcdef0123456789abcdef",
            "redacted-one-run-secret",
            new string('A', 64),
            new string('B', 64),
            new string('C', 64));
        var heartbeat = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", token, "heartbeat.json");
        var prepareWatchdog = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "0123456789abcdef0123456789abcdef", "prepare-session", "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var executeRunner = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "0123456789abcdef0123456789abcdef", "execute-fresh", "physical-tests", "Moondrop.PhysicalTests.exe");
        var prepareRoot = Path.GetDirectoryName(Path.GetDirectoryName(prepareWatchdog))!;
        var executeRoot = Path.GetDirectoryName(Path.GetDirectoryName(executeRunner))!;
        var started = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
            Directory.CreateDirectory(Path.GetDirectoryName(prepareWatchdog)!);
            Directory.CreateDirectory(Path.GetDirectoryName(executeRunner)!);
            await File.WriteAllBytesAsync(prepareWatchdog, [1]);
            await File.WriteAllBytesAsync(executeRunner, [2]);
            var manifest = RuntimeApphostManifestBinding.CreateManifest(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2])),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
            binding = binding with { RuntimeManifestSha256 = manifest.AggregateSha256 };
            PhysicalRuntimeManifestStore.WriteCreateNew(prepareRoot, manifest);
            PhysicalRuntimeManifestStore.WriteCreateNew(executeRoot, manifest);
            await PhysicalArtifactWriter.WriteJsonAsync(
                heartbeat,
                new PhysicalWatchdogHeartbeatState(
                    "RunnerStarting",
                    DateTimeOffset.UtcNow,
                    200,
                    started,
                    prepareWatchdog,
                    token,
                    binding.SessionId,
                    binding.OneRunToken,
                    binding.SourceFingerprint,
                    binding.RuntimeManifestSha256,
                    binding.LineageFingerprint,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]))));
            var authorization = new PhysicalWatchdogAuthorization(
                token,
                heartbeat,
                binding,
                200,
                started,
                prepareWatchdog,
                Path.Combine(executeRoot, PhysicalRuntimeManifestStore.FileName));
            var identities = new FakeProcessIdentityProvider(
                new PhysicalProcessIdentity(300, 200, started.AddSeconds(1), executeRunner),
                new PhysicalProcessIdentity(200, 100, started, prepareWatchdog));

            var result = PhysicalWatchdogProcessGate.Evaluate(authorization, root, identities);

            Assert.IsTrue(result.IsAuthorized, result.Diagnostic);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnrelatedProcessReturnedForApprovedPidIsRejectedPrecisely()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var identities = new RemappedProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, fixture.StartedAtUtc.AddSeconds(1), fixture.RunnerPath),
            200,
            new PhysicalProcessIdentity(999, 100, fixture.StartedAtUtc, fixture.WatchdogPath));

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=watchdog-process-id");
        StringAssert.Contains(result.Diagnostic, "expected.watchdog.pid=200");
        StringAssert.Contains(result.Diagnostic, "actual.watchdog.pid=999");
    }

    [TestMethod]
    public async Task TesthostOrDotnetWrapperIsNeverAcceptedAsThePhysicalRunnerApphost()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var wrapper = Path.Combine(fixture.Root, "wrapper", "testhost.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        await File.WriteAllBytesAsync(wrapper, [8]);
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, fixture.StartedAtUtc.AddSeconds(1), wrapper),
            new PhysicalProcessIdentity(200, 100, fixture.StartedAtUtc, fixture.WatchdogPath));

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=physical-runner-apphost");
        StringAssert.Contains(result.Diagnostic, "expected.name=Moondrop.PhysicalTests.exe");
        StringAssert.Contains(result.Diagnostic, $"actual.path={Path.GetFullPath(wrapper)}");
        StringAssert.Contains(result.Diagnostic, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([8])));
    }

    [TestMethod]
    public async Task MissingHeartbeatIsRejectedBeforeAnyPhysicalAccessWithExactPredicate()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        File.Delete(fixture.HeartbeatPath);

        var result = PhysicalWatchdogProcessGate.Evaluate(
            fixture.Authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=heartbeat-file-exists");
        StringAssert.Contains(result.Diagnostic, "expected.name=heartbeat.json");
        StringAssert.Contains(result.Diagnostic, "actual.exists=false");
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UntrustedHeartbeatPathIsRejectedBeforeItsContentsAreRead()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var outsideHeartbeat = Path.Combine(fixture.Root, "untrusted-heartbeat.json");
        const string hostileContents = "{malformed:fixture-one-run-secret}";
        await File.WriteAllTextAsync(outsideHeartbeat, hostileContents);
        var authorization = fixture.Authorization with { HeartbeatPath = outsideHeartbeat };

        var result = PhysicalWatchdogProcessGate.Evaluate(
            authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=heartbeat-canonical-path");
        StringAssert.Contains(result.Diagnostic, "expected.name=heartbeat.json");
        Assert.IsFalse(result.Diagnostic.Contains(hostileContents, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MalformedHeartbeatReportsParsePredicateWithoutLeakingContents()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        const string hostileContents = "{malformed:fixture-one-run-secret}";
        await File.WriteAllTextAsync(fixture.HeartbeatPath, hostileContents);

        var result = PhysicalWatchdogProcessGate.Evaluate(
            fixture.Authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=heartbeat-json");
        StringAssert.Contains(result.Diagnostic, "expected=valid schema");
        StringAssert.Contains(result.Diagnostic, "actual=JsonException");
        Assert.IsFalse(result.Diagnostic.Contains(hostileContents, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HeartbeatOwnerMismatchReportsTheExactExpectedAndActualPid()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        await PhysicalArtifactWriter.WriteJsonAsync(
            fixture.HeartbeatPath,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                999,
                fixture.StartedAtUtc,
                fixture.WatchdogPath,
                fixture.Token,
                fixture.Binding.SessionId,
                fixture.Binding.OneRunToken,
                fixture.Binding.SourceFingerprint,
                fixture.Binding.RuntimeManifestSha256,
                fixture.Binding.LineageFingerprint,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]))));

        var result = PhysicalWatchdogProcessGate.Evaluate(
            fixture.Authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=heartbeat-owner-pid");
        StringAssert.Contains(result.Diagnostic, "expected.pid=200");
        StringAssert.Contains(result.Diagnostic, "actual.pid=999");
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HeartbeatOneRunTokenMismatchNamesThePredicateButRedactsBothValues()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        const string wrongSecret = "wrong-one-run-secret";
        await PhysicalArtifactWriter.WriteJsonAsync(
            fixture.HeartbeatPath,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                200,
                fixture.StartedAtUtc,
                fixture.WatchdogPath,
                fixture.Token,
                fixture.Binding.SessionId,
                wrongSecret,
                fixture.Binding.SourceFingerprint,
                fixture.Binding.RuntimeManifestSha256,
                fixture.Binding.LineageFingerprint,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]))));

        var result = PhysicalWatchdogProcessGate.Evaluate(
            fixture.Authorization,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=heartbeat-one-run-token");
        StringAssert.Contains(result.Diagnostic, "expected=[redacted]");
        StringAssert.Contains(result.Diagnostic, "actual=[redacted]");
        Assert.IsFalse(result.Diagnostic.Contains(wrongSecret, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MalformedAuthorizationReportsTheExactShapePredicateAndRedactsSecrets()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var malformed = fixture.Authorization with
        {
            Binding = fixture.Binding with { SessionId = "not-32-hex" }
        };

        var result = PhysicalWatchdogProcessGate.Evaluate(
            malformed,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=session-id-shape");
        StringAssert.Contains(result.Diagnostic, "expected=32 hexadecimal characters");
        StringAssert.Contains(result.Diagnostic, "actual.length=10");
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingAuthorizationStillReportsTheObservedBoundedProcessChain()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();

        var result = PhysicalWatchdogProcessGate.Evaluate(
            authorization: null,
            fixture.Root,
            fixture.MatchingIdentities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=authorization-present");
        StringAssert.Contains(result.Diagnostic, "expected.name=Moondrop.PhysicalWatchdog.exe");
        StringAssert.Contains(result.Diagnostic, "actual.pid=300");
        StringAssert.Contains(result.Diagnostic, "actual.parentPid=200");
        StringAssert.Contains(result.Diagnostic, Path.GetFullPath(fixture.RunnerPath));
        StringAssert.Contains(result.Diagnostic, "chain[1].pid=200");
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Token, StringComparison.Ordinal));
        Assert.IsFalse(result.Diagnostic.Contains(fixture.Binding.OneRunToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectedParentChainStopsAtTheDocumentedDepthBound()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var chain = Enumerable.Range(201, 9)
            .Select(pid => new PhysicalProcessIdentity(
                pid,
                pid + 1,
                fixture.StartedAtUtc.AddSeconds(pid - 199),
                Path.Combine(fixture.Root, "wrappers", $"wrapper-{pid}.exe")))
            .ToArray();
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 201, fixture.StartedAtUtc.AddSeconds(1), fixture.RunnerPath),
            chain);

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "predicate=direct-parent-pid");
        StringAssert.Contains(result.Diagnostic, "chain.truncated=true");
        StringAssert.Contains(result.Diagnostic, "chain.limit=8");
        Assert.IsFalse(result.Diagnostic.Contains("chain[8]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectedParentChainReportsCyclesWithoutLooping()
    {
        await using var fixture = await PhysicalLineageFixture.CreateAsync();
        var identities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 201, fixture.StartedAtUtc.AddSeconds(1), fixture.RunnerPath),
            new PhysicalProcessIdentity(201, 202, fixture.StartedAtUtc.AddSeconds(2), Path.Combine(fixture.Root, "wrapper-a.exe")),
            new PhysicalProcessIdentity(202, 201, fixture.StartedAtUtc.AddSeconds(3), Path.Combine(fixture.Root, "wrapper-b.exe")));

        var result = PhysicalWatchdogProcessGate.Evaluate(fixture.Authorization, fixture.Root, identities);

        Assert.IsFalse(result.IsAuthorized);
        StringAssert.Contains(result.Diagnostic, "chain[3].cyclePid=201");
        Assert.IsFalse(result.Diagnostic.Contains("chain.truncated=true", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LoadedSessionMustMatchTheAuthenticatedWatchdogLineage()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(
            original,
            PhysicalTransitionPlanner.Create(original),
            "one-run-token",
            sourceFingerprint: new string('A', 64),
            runtimeManifestSha256: new string('B', 64),
            sessionId: new string('C', 32));
        var binding = PhysicalWatchdogSessionBinding.FromSession(session);

        Assert.IsTrue(PhysicalWatchdogProcessGate.IsSessionOwned(binding, session));
        Assert.IsFalse(PhysicalWatchdogProcessGate.IsSessionOwned(
            binding,
            session with { SessionId = new string('D', 32) }));
        Assert.IsFalse(PhysicalWatchdogProcessGate.IsSessionOwned(
            binding,
            session with { OneRunToken = "replacement-token" }));
        Assert.IsFalse(PhysicalWatchdogProcessGate.IsSessionOwned(
            binding,
            session with { Original = session.Original with { Firmware = "replacement" } }));
    }

    [TestMethod]
    public void RunLockResolvesTheCanonicalWindowsProgramDataWhenCommonApplicationDataIsUnavailable()
    {
        // The EXECUTE runner child runs under the watchdog's cleared environment (SystemRoot
        // WINDIR, TEMP, TMP only). Under .NET 10 that leaves Environment.GetFolderPath
        // (CommonApplicationData) EMPTY (verified empirically), which previously threw at the
        // machine-wide run lock ("common application-data directory is unavailable") and locked
        // physical testing out before any device access. The canonical ProgramData directory must
        // still be derivable from the validated Windows directory.
        Assert.AreEqual(
            Path.GetFullPath(@"C:\ProgramData"),
            PhysicalRunLock.ResolveCommonApplicationDataPath(null, @"C:\Windows"));
        Assert.AreEqual(
            Path.GetFullPath(@"D:\ProgramData"),
            PhysicalRunLock.ResolveCommonApplicationDataPath(null, @"D:\Windows"));
        var fallbackError = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalRunLock.ResolveCommonApplicationDataPath(null, ""));
        StringAssert.Contains(fallbackError.Message, "common application-data directory is unavailable");
        Assert.AreEqual(
            Path.GetFullPath(@"C:\Other"),
            PhysicalRunLock.ResolveCommonApplicationDataPath(@"C:\Other", @"C:\Windows"));
    }

    [TestMethod]
    public async Task PhysicalRunFileLockSurvivesAsyncContinuationAndReleasesOnDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-lock-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "physical.lock");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.IsTrue(PhysicalRunLock.TryAcquire(path, out var first));
            using (first)
            {
                await Task.Yield();
                Assert.IsFalse(PhysicalRunLock.TryAcquire(path, out var blocked));
                Assert.IsNull(blocked);
            }

            Assert.IsTrue(PhysicalRunLock.TryAcquire(path, out var reacquired));
            reacquired!.Dispose();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    [TestMethod]
    public void PhysicalTestOptInRequiresExactOne()
    {
        Assert.IsTrue(PhysicalTestGate.IsOptedIn("1"));
        Assert.IsFalse(PhysicalTestGate.IsOptedIn(null));
        Assert.IsFalse(PhysicalTestGate.IsOptedIn(""));
        Assert.IsFalse(PhysicalTestGate.IsOptedIn("0"));
        Assert.IsFalse(PhysicalTestGate.IsOptedIn("true"));
        Assert.IsFalse(PhysicalTestGate.IsOptedIn("1 "));
    }

    [TestMethod]
    public void PhysicalExecutionRequiresExactRunOptInAndPersistedOneRunToken()
    {
        Assert.IsTrue(PhysicalExecutionGate.IsAuthorized("1", "one-run-token", "one-run-token"));
        Assert.IsFalse(PhysicalExecutionGate.IsAuthorized(null, "one-run-token", "one-run-token"));
        Assert.IsFalse(PhysicalExecutionGate.IsAuthorized("1", null, "one-run-token"));
        Assert.IsFalse(PhysicalExecutionGate.IsAuthorized("1", "wrong", "one-run-token"));
        Assert.IsFalse(PhysicalExecutionGate.IsAuthorized("true", "one-run-token", "one-run-token"));
    }

    [TestMethod]
    public void RecoveryGateRejectsMissingOptInAndPathsOutsideHardwareSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-recovery-test-{Guid.NewGuid():N}");
        var snapshots = Path.Combine(root, "hardware-snapshots");
        Directory.CreateDirectory(snapshots);
        var valid = Path.Combine(snapshots, "dawn-pro2-session-test.json");
        File.WriteAllText(valid, "{}");
        try
        {
            Assert.AreEqual(Path.GetFullPath(valid), PhysicalRecoveryGate.Validate("1", valid, snapshots));
            AssertEx.ThrowsException<InvalidOperationException>(() => PhysicalRecoveryGate.Validate(null, valid, snapshots));
            AssertEx.ThrowsException<InvalidOperationException>(() => PhysicalRecoveryGate.Validate("1", Path.Combine(root, "outside.json"), snapshots));
            AssertEx.ThrowsException<InvalidOperationException>(() => PhysicalRecoveryGate.Validate("1", Path.Combine(snapshots, "..", "outside.json"), snapshots));
        }
        finally
        {
            File.Delete(valid);
            Directory.Delete(snapshots, recursive: false);
            Directory.Delete(root, recursive: false);
        }
    }

    [TestMethod]
    public void PhysicalSessionStateMachineRejectsSkippedPhysicalCyclePhases()
    {
        Assert.IsTrue(PhysicalSessionStateMachine.CanTransition(
            PhysicalSessionPhase.Prepared,
            PhysicalSessionPhase.TemporaryWritesStarting));
        Assert.IsFalse(PhysicalSessionStateMachine.CanTransition(
            PhysicalSessionPhase.Prepared,
            PhysicalSessionPhase.TemporaryPersistenceVerified));
        Assert.IsFalse(PhysicalSessionStateMachine.CanTransition(
            PhysicalSessionPhase.AwaitingRestorationPhysicalCycle,
            PhysicalSessionPhase.Completed));
        Assert.IsTrue(PhysicalSessionStateMachine.CanTransition(
            PhysicalSessionPhase.RestorationVerified,
            PhysicalSessionPhase.Completed));
    }

    [TestMethod]
    [DataRow(PhysicalSessionPhase.RestorationStarting, PhysicalRecoveryStep.RestoreRam)]
    [DataRow(PhysicalSessionPhase.RestorationWritesVerified, PhysicalRecoveryStep.RestoreRam)]
    [DataRow(PhysicalSessionPhase.RestorationFlashSaveStarting, PhysicalRecoveryStep.RestoreRam)]
    [DataRow(PhysicalSessionPhase.AwaitingRestorationPhysicalCycle, PhysicalRecoveryStep.RestoreRam)]
    [DataRow(PhysicalSessionPhase.RestorationVerified, PhysicalRecoveryStep.VerifyRestoration)]
    [DataRow(PhysicalSessionPhase.Failed, PhysicalRecoveryStep.RestoreRam)]
    [DataRow(PhysicalSessionPhase.Completed, PhysicalRecoveryStep.AlreadyCompleted)]
    public void RecoveryResumePlanStartsAtTruthfulDurablePhase(
        PhysicalSessionPhase phase,
        PhysicalRecoveryStep expectedFirstStep)
    {
        Assert.AreEqual(expectedFirstStep, PhysicalRecoveryResumePlan.For(phase)[0]);
    }

    [TestMethod]
    public void FailedSessionDurablyCarriesTheLastSafePhase()
    {
        var original = RawSnapshot();
        var prepared = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var restoration = prepared with { Phase = PhysicalSessionPhase.RestorationFlashSaveStarting };

        var failed = restoration.Advance(PhysicalSessionPhase.Failed, error: "injected");

        Assert.AreEqual(PhysicalSessionPhase.RestorationFlashSaveStarting, failed.LastSafePhase);
    }

    [TestMethod]
    public void PhysicalSessionBindsApprovedSourceAndCompleteRuntimeManifest()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(
            original,
            PhysicalTransitionPlanner.Create(original),
            "one-run-token",
            sourceFingerprint: new string('A', 64),
            runtimeManifestSha256: new string('B', 64));

        PhysicalSessionStore.Validate(session);

        Assert.AreEqual(new string('A', 64), session.SourceFingerprint);
        Assert.AreEqual(new string('B', 64), session.RuntimeManifestSha256);
        AssertEx.ThrowsException<InvalidDataException>(() =>
            PhysicalSessionStore.Validate(session with { RuntimeManifestSha256 = "missing" }));
    }

    [TestMethod]
    public async Task UsbCycleMonitorDoesNotAcceptHidRestartWhilePhysicalPnpDeviceRemainsPresent()
    {
        var identity = new Moondrop.Hardware.DawnPro2HidIdentity("hid://pinned", "35D8011D251117");
        var probe = new FakePhysicalPresenceProbe(
            new PhysicalPresenceSample(true, [identity]),
            new PhysicalPresenceSample(true, []),
            new PhysicalPresenceSample(true, [identity]),
            new PhysicalPresenceSample(false, []),
            new PhysicalPresenceSample(true, [identity]));

        await PhysicalUsbCycleMonitor.WaitForPhysicalCycleAsync(
            identity,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            probe,
            CancellationToken.None);

        Assert.AreEqual(5, probe.SampleCount);
    }

    [TestMethod]
    public async Task DurableSnapshotPublicationFailurePreservesPreviousArtifactAndRemovesTemp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-artifact-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.json");
        await File.WriteAllTextAsync(path, "previous");
        try
        {
            await AssertEx.ThrowsExceptionAsync<IOException>(() =>
                PhysicalArtifactWriter.WriteSnapshotAsync(path, RawSnapshot(), new ThrowBeforePublishFault()));

            Assert.AreEqual("previous", await File.ReadAllTextAsync(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    [TestMethod]
    public async Task SessionRecoveryCopyIsDiscoveredWhenPrimaryPublicationIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-session-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dawn-pro2-session-fallback.json");
        var session = PhysicalSessionArtifact.Create(RawSnapshot(), PhysicalTransitionPlanner.Create(RawSnapshot()), "one-run-token");
        try
        {
            await PhysicalSessionStore.PersistAsync(path, session);
            var recoveryCopy = PhysicalSessionStore.RecoveryCopyPath(path);
            Assert.IsTrue(File.Exists(recoveryCopy));
            File.Delete(path);

            var recovered = await PhysicalSessionStore.LoadValidatedAsync(path);

            Assert.AreEqual(session.OneRunToken, recovered.OneRunToken);
            Assert.AreEqual(PhysicalSessionPhase.Prepared, recovered.Phase);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
    }

    [TestMethod]
    public async Task ExecuteOrchestratorUsesOneTemporaryPeqWriteThenFullRestorationOnly()
    {
        var original = RawSnapshot() with { ActiveEq = 9 };
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(session, _ => Task.CompletedTask, actions);

        Assert.IsTrue(outcome.Succeeded);
        CollectionAssert.AreEqual(
            new[] { PhysicalExecuteStep.IndividualBand, PhysicalExecuteStep.RestoreOriginalRam },
            actions.Calls);
    }

    [TestMethod]
    public async Task RecoveryOrchestratorUsesFullRawRestorationOnly()
    {
        var original = RawSnapshot() with { ActiveEq = 9 };
        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token") with
        {
            Phase = PhysicalSessionPhase.TemporaryWritesVerified
        };
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);

        var recovered = await PhysicalRecoveryOrchestrator.RunAsync(
            session,
            plan.Individual,
            _ => Task.CompletedTask,
            actions);

        Assert.AreEqual(PhysicalSessionPhase.Completed, recovered.Phase);
        CollectionAssert.AreEqual(new[] { PhysicalExecuteStep.RestoreOriginalRam }, actions.Calls);
    }

    [TestMethod]
    public void PhysicalPrepareExecuteAndRecoveryNeverWriteOrToggleActiveEq()
    {
        var repositoryRoot = PhysicalArtifactPaths.FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests-dotnet",
            "Moondrop.Tests",
            "DawnPro2PhysicalIntegrationTests.cs"));
        var support = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests-dotnet",
            "Moondrop.Tests",
            "PhysicalIntegrationSupport.cs"));

        Assert.IsFalse(source.Contains("WriteActiveEqAsync", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("TemporaryActiveEq", StringComparison.Ordinal));
        Assert.IsFalse(support.Contains("TemporaryActiveEq", StringComparison.Ordinal));
        StringAssert.Contains(
            source,
            "HardwareSnapshotReader.ReadConsistentAsync(device, original.Identity)",
            "Restoration must use a fresh, complete, two-pass raw snapshot before it can report success.");
    }

    [TestMethod]
    [DataRow(PhysicalExecuteStep.IndividualBand)]
    [DataRow(PhysicalExecuteStep.RestoreOriginalRam)]
    public async Task ExecuteOrchestratorManagedFailureImmediatelyRestoresThroughRealControlPath(
        PhysicalExecuteStep injectedStep)
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions(injectedStep, failOnlyOnce: true);
        var persisted = new List<PhysicalSessionArtifact>();

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(
            session,
            state =>
            {
                persisted.Add(state);
                return Task.CompletedTask;
            },
            actions);

        Assert.IsNotNull(outcome.PrimaryError);
        StringAssert.Contains(outcome.PrimaryError.ToString(), $"injected {injectedStep}");
        Assert.IsTrue(outcome.RestorationAttempted);
        Assert.IsTrue(outcome.RestorationVerified);
        Assert.AreEqual(PhysicalSessionPhase.RestorationVerified, outcome.Session.Phase);
        Assert.AreNotEqual(0, actions.Calls.Count(step => step == PhysicalExecuteStep.RestoreOriginalRam));
        Assert.AreEqual(PhysicalSessionPhase.RestorationVerified, persisted[^1].Phase);
    }

    [TestMethod]
    public async Task ExecuteOrchestratorCannotReportSuccessWhenFullRawRestorationFails()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions(PhysicalExecuteStep.RestoreOriginalRam, failOnlyOnce: false);
        var persisted = new List<PhysicalSessionArtifact>();

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(
            session,
            state =>
            {
                persisted.Add(state);
                return Task.CompletedTask;
            },
            actions);

        Assert.IsNotNull(outcome.PrimaryError);
        Assert.IsNotNull(outcome.RestorationError);
        Assert.IsFalse(outcome.RestorationVerified);
        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(PhysicalSessionPhase.Failed, outcome.Session.Phase);
        Assert.AreEqual(PhysicalSessionPhase.Failed, persisted[^1].Phase);
        StringAssert.Contains(persisted[^1].LastError!, "Physical execute failed");
        StringAssert.Contains(persisted[^1].LastError!, "Immediate restoration failed");
    }

    [TestMethod]
    public async Task ImmediateRestorationDurableFailureRedactsSessionConfirmationAndOneRunSecrets()
    {
        const string oneRunToken = "one-run-secret-value";
        const string confirmation = "confirmation-secret-value";
        var sessionPath = Path.Combine(Path.GetTempPath(), "session-secret-value.json");
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), oneRunToken);
        var persisted = new List<PhysicalSessionArtifact>();

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(
            session,
            state =>
            {
                persisted.Add(state);
                return Task.CompletedTask;
            },
            new SecretLeakingPhysicalExecuteActions(sessionPath, confirmation, oneRunToken),
            sessionPath,
            confirmation);

        Assert.IsFalse(outcome.Succeeded);
        Assert.IsNotNull(outcome.Session.LastError);
        Assert.IsFalse(outcome.Session.LastError.Contains(sessionPath, StringComparison.Ordinal));
        Assert.IsFalse(outcome.Session.LastError.Contains(confirmation, StringComparison.Ordinal));
        Assert.IsFalse(outcome.Session.LastError.Contains(oneRunToken, StringComparison.Ordinal));
        StringAssert.Contains(outcome.Session.LastError, "[REDACTED]");
        Assert.AreEqual(outcome.Session.LastError, persisted[^1].LastError);
    }

    [TestMethod]
    [DataRow(PhysicalSessionPhase.TemporaryWritesVerified)]
    [DataRow(PhysicalSessionPhase.RestorationStarting)]
    [DataRow(PhysicalSessionPhase.RestorationWritesVerified)]
    [DataRow(PhysicalSessionPhase.RestorationVerified)]
    [DataRow(PhysicalSessionPhase.Completed)]
    public async Task ExecuteOrchestratorPersistenceFailureAfterWritesStillRestores(
        PhysicalSessionPhase injectedPhase)
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: true);
        var failed = false;

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(
            session,
            state =>
            {
                if (!failed && state.Phase == injectedPhase)
                {
                    failed = true;
                    throw new IOException($"injected persistence {injectedPhase}");
                }
                return Task.CompletedTask;
            },
            actions);

        Assert.IsNotNull(outcome.PrimaryError);
        StringAssert.Contains(outcome.PrimaryError.ToString(), $"injected persistence {injectedPhase}");
        Assert.IsTrue(outcome.RestorationAttempted);
        Assert.IsTrue(outcome.RestorationVerified);
        Assert.AreNotEqual(0, actions.Calls.Count(step => step == PhysicalExecuteStep.RestoreOriginalRam));
    }

    [TestMethod]
    [DataRow(PhysicalSessionPhase.RestorationStarting)]
    [DataRow(PhysicalSessionPhase.RestorationWritesVerified)]
    [DataRow(PhysicalSessionPhase.RestorationVerified)]
    public async Task ImmediateRestorationContinuesAcrossItsOwnPhasePublicationFailure(
        PhysicalSessionPhase injectedPhase)
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions(PhysicalExecuteStep.IndividualBand, failOnlyOnce: true);
        var failed = false;
        var persisted = new List<PhysicalSessionArtifact>();

        var outcome = await PhysicalExecuteOrchestrator.RunAsync(
            session,
            state =>
            {
                if (!failed && state.Phase == injectedPhase)
                {
                    failed = true;
                    throw new IOException($"injected restoration persistence {injectedPhase}");
                }
                persisted.Add(state);
                return Task.CompletedTask;
            },
            actions);

        Assert.IsTrue(outcome.RestorationVerified);
        Assert.IsNotNull(outcome.RestorationError);
        StringAssert.Contains(outcome.RestorationError.ToString(), $"injected restoration persistence {injectedPhase}");
        Assert.AreNotEqual(0, actions.Calls.Count(step => step == PhysicalExecuteStep.RestoreOriginalRam));
        Assert.AreEqual(PhysicalSessionPhase.RestorationVerified, persisted[^1].Phase);
    }

    [TestMethod]
    public async Task PreparedSessionRoundTripsAndRevalidatesRawPlan()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dawn-pro2-session-roundtrip.json");
        var original = RawSnapshot();
        var expected = PhysicalSessionArtifact.Create(
            original,
            PhysicalTransitionPlanner.Create(original),
            "one-run-token",
            sourceFingerprint: new string('A', 64));
        try
        {
            await PhysicalSessionStore.PersistAsync(path, expected);
            var actual = await PhysicalSessionStore.LoadValidatedAsync(path);

            Assert.AreEqual(expected.OneRunToken, actual.OneRunToken);
            Assert.AreEqual(expected.SessionId, actual.SessionId);
            Assert.AreEqual(new string('A', 64), actual.SourceFingerprint);
            Assert.AreEqual(PhysicalSessionPhase.Prepared, actual.Phase);
            Assert.IsEmpty(PhysicalSnapshotComparer.Differences(expected.Original, actual.Original));
            Assert.IsEmpty(PhysicalSnapshotComparer.Differences(expected.Plan.Bulk, actual.Plan.Bulk));
        }
        finally
        {
            File.Delete(path);
            File.Delete(PhysicalSessionStore.RecoveryCopyPath(path));
            Directory.Delete(directory, recursive: false);
        }
    }

    [TestMethod]
    public async Task SessionStoreRejectsDivergentValidPrimaryAndRecoveryLineages()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moondrop-session-lineage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dawn-pro2-session-lineage.json");
        var original = RawSnapshot();
        var primary = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "token-a");
        var recovery = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "token-b") with
        {
            UpdatedAtUtc = primary.UpdatedAtUtc.AddMinutes(1)
        };
        try
        {
            await PhysicalArtifactWriter.WriteJsonAsync(path, primary);
            await PhysicalArtifactWriter.WriteJsonAsync(PhysicalSessionStore.RecoveryCopyPath(path), recovery);

            var error = await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() => PhysicalSessionStore.LoadValidatedAsync(path));

            StringAssert.Contains(error.Message, "lineage");
        }
        finally
        {
            File.Delete(path);
            File.Delete(PhysicalSessionStore.RecoveryCopyPath(path));
            Directory.Delete(directory, recursive: false);
        }
    }

    [TestMethod]
    public void ProfilePlanBuildsTheExactParsedTargetSnapshot()
    {
        var original = RawSnapshot();
        var eq = Moondrop.Core.Eq.EqPresetParser.Parse(
            "Preamp: -4 dB\r\n" +
            "Filter 1: ON LSQ Fc 100 Hz Gain 2.0 dB Q 0.710\r\n" +
            "Filter 2: ON PK Fc 140 Hz Gain 3.0 dB Q 0.900\r\n" +
            "Filter 3: ON PK Fc 780 Hz Gain 0.6 dB Q 2.000\r\n" +
            "Filter 4: ON PK Fc 1350 Hz Gain -1.4 dB Q 1.800\r\n" +
            "Filter 5: ON HSQ Fc 3000 Hz Gain 0.0 dB Q 0.710\r\n" +
            "Filter 6: ON HSQ Fc 10000 Hz Gain 0.0 dB Q 0.710");
        var plan = PhysicalTransitionPlanner.CreateProfilePlan(original, eq);
        Assert.IsNotNull(plan.Profile);
        var profile = plan.Profile!;
        Assert.HasCount(8, profile.Bands);
        var plain = PhysicalTransitionPlanner.Create(original);
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(plain.Baseline, plan.Baseline));
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(plain.Individual, plan.Individual));
        Assert.IsTrue(plain.IndividualBand.RawPayload.SequenceEqual(plan.IndividualBand.RawPayload));
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(plain.Bulk, plan.Bulk));
        var b0 = profile.Bands[0];
        Assert.AreEqual(0, b0.Index);
        Assert.AreEqual(100, b0.Frequency);
        Assert.AreEqual(182, b0.QRaw);
        Assert.AreEqual(512, b0.GainRaw);
        Assert.AreEqual(Moondrop.Core.Devices.PeqFilterType.LowShelf2, b0.FilterType);
        Assert.IsTrue(b0.Enabled);
        Assert.AreEqual(-1024, profile.PreGainRaw);
        Assert.IsTrue(plan.Baseline.Bands[6].RawPayload.SequenceEqual(profile.Bands[6].RawPayload));
        Assert.IsTrue(plan.Baseline.Bands[7].RawPayload.SequenceEqual(profile.Bands[7].RawPayload));
        // The profile target must preserve the device raw report format (register header + active-EQ
        // marker) so a full readback after applying it matches byte-for-byte.
        Assert.AreEqual(plan.Baseline.Bands[0].RawPayload[0], profile.Bands[0].RawPayload[0]);
        Assert.AreEqual(plan.Baseline.Bands[0].RawPayload[35], profile.Bands[0].RawPayload[35]);
        Assert.IsEmpty(PhysicalSnapshotValidator.RestorationProblems(profile));
    }

    [TestMethod]
    public async Task ExecuteOrchestratorAppliesTheProfileTargetThenRestoresToBaseline()
    {
        var original = RawSnapshot();
        var plan = PhysicalTransitionPlanner.CreateProfilePlan(original,
            Moondrop.Core.Eq.EqPresetParser.Parse("Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.0"));
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token");
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);
        var outcome = await PhysicalExecuteOrchestrator.RunAsync(session, _ => Task.CompletedTask, actions);
        CollectionAssert.AreEqual(
            new[] { PhysicalExecuteStep.ApplyProfile, PhysicalExecuteStep.RestoreOriginalRam },
            actions.Calls);
        Assert.IsNull(outcome.PrimaryError);
        Assert.IsTrue(outcome.RestorationVerified);
        Assert.AreEqual(PhysicalSessionPhase.Completed, outcome.Session.Phase);
    }

    [TestMethod]
    public async Task ExecuteOrchestratorWithoutAProfileKeepsTheIndividualBandTemporaryStep()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);
        var outcome = await PhysicalExecuteOrchestrator.RunAsync(session, _ => Task.CompletedTask, actions);
        CollectionAssert.AreEqual(
            new[] { PhysicalExecuteStep.IndividualBand, PhysicalExecuteStep.RestoreOriginalRam },
            actions.Calls);
        Assert.AreEqual(PhysicalSessionPhase.Completed, outcome.Session.Phase);
    }

    [TestMethod]
    public void SessionValidationAcceptsAPersistedProfilePlan()
    {
        var original = RawSnapshot();
        var plan = PhysicalTransitionPlanner.CreateProfilePlan(original,
            Moondrop.Core.Eq.EqPresetParser.Parse("Filter 1: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.0"));
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token");
        PhysicalSessionStore.Validate(session);
    }

    [TestMethod]
    public void SessionValidationRejectsMutatedPersistedIndividualBand()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token");
        var payload = session.Plan.IndividualBand.RawPayload.ToArray();
        payload[7] ^= 0x01;
        var corrupted = session with
        {
            Plan = session.Plan with
            {
                IndividualBand = new HardwareBandSnapshot(session.Plan.IndividualBand.Index, payload)
            }
        };

        var error = AssertEx.ThrowsException<InvalidDataException>(() => PhysicalSessionStore.Validate(corrupted));

        StringAssert.Contains(error.Message, "individual band");
    }

    [TestMethod]
    public void SnapshotValidationAcceptsObservedDawnPro2Firmware15RawActiveEqNine()
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(RawSnapshot() with
        {
            ActiveEq = 9,
            Firmware = "1.5"
        });

        Assert.IsEmpty(problems);
    }

    [TestMethod]
    [DataRow(" 1.5")]
    [DataRow("1.5 ")]
    [DataRow("\t1.5")]
    [DataRow("1.5\r")]
    public void SnapshotValidationRejectsRawActiveEqNineUnlessFirmwareIsExactly15(string firmware)
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(RawSnapshot() with
        {
            ActiveEq = 9,
            Firmware = firmware
        });

        Assert.IsTrue(problems.Any(problem => problem.Contains("firmware must be exactly 1.5", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(problems.Any(problem => problem.Contains("raw value 9", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SnapshotValidationRejectsRawActiveEqNineForWrongDeviceModel()
    {
        var original = RawSnapshot();
        var wrongModel = original with
        {
            ActiveEq = 9,
            Identity = original.Identity with { DeviceKind = Moondrop.Core.Devices.DeviceKind.Legacy }
        };

        var problems = PhysicalSnapshotValidator.RestorationProblems(wrongModel);

        Assert.IsTrue(problems.Any(problem => problem.Contains("DAWN PRO2", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SnapshotValidationRejectsRawActiveEqNineForWrongProductId()
    {
        var original = RawSnapshot();
        var wrongProduct = original with
        {
            ActiveEq = 9,
            Identity = original.Identity with { ProductId = 0x011C }
        };

        var problems = PhysicalSnapshotValidator.RestorationProblems(wrongProduct);

        Assert.IsTrue(problems.Any(problem => problem.Contains("DAWN PRO2", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SnapshotValidationRejectsRawActiveEqNineForWrongFirmware()
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(RawSnapshot() with
        {
            ActiveEq = 9,
            Firmware = "1.6"
        });

        Assert.IsTrue(problems.Any(problem => problem.Contains("firmware must be exactly 1.5", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(problems.Any(problem => problem.Contains("raw value 9", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(8)]
    [DataRow(10)]
    [DataRow(15)]
    public void SnapshotValidationRejectsUnobservedRawActiveEqValues(int activeEq)
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(RawSnapshot() with { ActiveEq = activeEq });

        Assert.IsTrue(problems.Any(problem => problem.Contains($"read {activeEq}", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ActiveEqNineRemainsRawSnapshotStateWhilePeqRegistryProfileRemainsSeven()
    {
        var original = RawSnapshot() with { ActiveEq = 9 };

        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token");

        var registeredPeqProfile = Moondrop.Core.Protocol.DawnPro2Protocol.PeqIndex;
        var rawWrite = Moondrop.Core.Protocol.DawnPro2Protocol.BuildWriteRawBandPayload(original.Bands[0].ToRawState());
        Assert.AreEqual((byte)7, registeredPeqProfile);
        Assert.AreEqual(registeredPeqProfile, rawWrite[35]);
        Assert.AreEqual(9, plan.Baseline.ActiveEq);
        Assert.AreEqual(9, plan.Individual.ActiveEq);
        Assert.AreEqual(9, plan.Bulk.ActiveEq);
        Assert.AreEqual(9, session.Original.ActiveEq);
        Assert.AreEqual(9, session.Plan.Baseline.ActiveEq);
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, session.Original));
    }

    [TestMethod]
    public void TransitionPlanDescribesOnlyOneTemporaryPeqBandMutation()
    {
        var original = RawSnapshot() with { ActiveEq = 9 };

        var plan = PhysicalTransitionPlanner.Create(original);
        var individualDifferences = PhysicalSnapshotComparer.Differences(original, plan.Individual);

        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, plan.Baseline));
        Assert.IsNotEmpty(individualDifferences);
        Assert.IsTrue(individualDifferences.All(difference =>
            difference.StartsWith($"band position {plan.IndividualBand.Index} ", StringComparison.Ordinal)));
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, plan.Bulk));
        Assert.IsEmpty(plan.BulkChanges);
        Assert.AreEqual(9, plan.Individual.ActiveEq);
    }

    [TestMethod]
    public void SnapshotComparisonAndReachableStatesRetainObservedRawActiveEqNine()
    {
        var original = RawSnapshot() with { ActiveEq = 9 };
        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token");

        var differences = PhysicalSnapshotComparer.Differences(original, original with { ActiveEq = 7 });
        var reachable = PhysicalRecoveryCompatibility.ReachableSnapshots(
            session,
            PhysicalSessionPhase.TemporaryWritesStarting);

        Assert.IsTrue(differences.Any(difference =>
            difference.Contains("active EQ: expected 9, actual 7", StringComparison.Ordinal)));
        Assert.IsTrue(reachable.All(snapshot => snapshot.ActiveEq == 9));
    }

    [TestMethod]
    public void SnapshotValidationRejectsRawActiveEqOutsideProfileSevenAndNarrowObservedNine()
    {
        var problems = PhysicalSnapshotValidator.RestorationProblems(Snapshot() with { ActiveEq = 3 });

        Assert.IsTrue(problems.Any(problem => problem.Contains("read 3", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RecoveryCompatibilityRejectsStaleSessionState()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token") with
        {
            Phase = PhysicalSessionPhase.Failed,
            LastSafePhase = PhysicalSessionPhase.TemporaryWritesStarting
        };
        var stale = original with { GlobalGainRaw = (short)(original.GlobalGainRaw + 1) };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalRecoveryCompatibility.RequireReachable(session, stale));

        StringAssert.Contains(error.Message, "not reachable");
    }

    [TestMethod]
    public void RecoveryCompatibilityAcceptsTheSingleReachableTemporaryBandState()
    {
        var original = RawSnapshot();
        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token") with
        {
            Phase = PhysicalSessionPhase.Failed,
            LastSafePhase = PhysicalSessionPhase.RestorationStarting
        };
        PhysicalRecoveryCompatibility.RequireReachable(session, plan.Individual);
    }

    [TestMethod]
    public void RecoveryCompatibilityRejectsFirmwareDrift()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token") with
        {
            Phase = PhysicalSessionPhase.Failed,
            LastSafePhase = PhysicalSessionPhase.TemporaryWritesStarting
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalRecoveryCompatibility.RequireReachable(session, original with { Firmware = "1.6" }));

        StringAssert.Contains(error.Message, "firmware must be exactly 1.5");
    }

    [TestMethod]
    public void FailedRecoveryCompatibilityUsesLastSafePhaseInsteadOfGlobalReachability()
    {
        var original = RawSnapshot();
        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token") with
        {
            Phase = PhysicalSessionPhase.Failed,
            LastSafePhase = PhysicalSessionPhase.RestorationVerified
        };

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => PhysicalRecoveryCompatibility.RequireReachable(session, plan.Individual));

        StringAssert.Contains(error.Message, "durable phase RestorationVerified");
    }

    [TestMethod]
    public async Task RecoveryFromLegacyFlashPhasePerformsOnlyFullRawRestoration()
    {
        var original = RawSnapshot();
        var plan = PhysicalTransitionPlanner.Create(original);
        var session = PhysicalSessionArtifact.Create(original, plan, "one-run-token") with
        {
            Phase = PhysicalSessionPhase.RestorationFlashSaveStarting
        };
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);
        var persisted = new List<PhysicalSessionArtifact>();

        var recovered = await PhysicalRecoveryOrchestrator.RunAsync(
            session,
            original,
            state =>
            {
                persisted.Add(state);
                return Task.CompletedTask;
            },
            actions);

        CollectionAssert.AreEqual(
            new[] { PhysicalExecuteStep.RestoreOriginalRam },
            actions.Calls);
        Assert.AreEqual(PhysicalSessionPhase.Completed, recovered.Phase);
        Assert.AreEqual(PhysicalSessionPhase.Completed, persisted[^1].Phase);
    }

    [TestMethod]
    public async Task RecoveryFromRestorationVerifiedOnlyVerifiesExactOriginalAndCompletesWithoutReflash()
    {
        var original = RawSnapshot();
        var session = PhysicalSessionArtifact.Create(original, PhysicalTransitionPlanner.Create(original), "one-run-token") with
        {
            Phase = PhysicalSessionPhase.RestorationVerified
        };
        var actions = new InjectedPhysicalExecuteActions((PhysicalExecuteStep)(-1), failOnlyOnce: false);

        var recovered = await PhysicalRecoveryOrchestrator.RunAsync(
            session,
            original,
            _ => Task.CompletedTask,
            actions);

        Assert.AreEqual(PhysicalSessionPhase.Completed, recovered.Phase);
        Assert.IsEmpty(actions.Calls);
    }

    [TestMethod]
    [DataRow(-18.0)]
    [DataRow(0.0)]
    [DataRow(11.875)]
    [DataRow(12.0)]
    public void TemporaryGainUsesSafeDistinctQuarterDbQ88Value(double original)
    {
        Assert.IsTrue(PhysicalTemporaryGain.TryChoose(original, out var temporary));
        Assert.IsTrue(temporary is >= -18.0 and <= 12.0);
        Assert.AreEqual(0.25, Math.Abs(temporary - PhysicalTemporaryGain.Quantize(original)), 1e-12);
        Assert.AreNotEqual(PhysicalTemporaryGain.ToRawQ88(original), PhysicalTemporaryGain.ToRawQ88(temporary));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-18.1)]
    [DataRow(12.1)]
    public void TemporaryGainRejectsUnrestorableValues(double original)
    {
        Assert.IsFalse(PhysicalTemporaryGain.TryChoose(original, out _));
    }

    [TestMethod]
    public void SnapshotComparisonRejectsAnyRawQ88Difference()
    {
        var expected = RawSnapshot();
        var oneRawStepAway = expected with { PreGainRaw = (short)(expected.PreGainRaw + 1) };

        Assert.IsNotEmpty(PhysicalSnapshotComparer.Differences(expected, oneRawStepAway));
    }

    [TestMethod]
    public void SnapshotComparisonRequiresExactBandMetadata()
    {
        var expected = RawSnapshot();
        var payload = expected.Bands[0].RawPayload.ToArray();
        payload[27]++;
        payload[33] = (byte)Moondrop.Core.Devices.PeqFilterType.HighShelf2;
        var changed = expected with
        {
            Bands = [new HardwareBandSnapshot(0, payload), .. expected.Bands.Skip(1)]
        };

        var differences = PhysicalSnapshotComparer.Differences(expected, changed);

        Assert.IsTrue(differences.Any(x => x.Contains("payload byte 27", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(differences.Any(x => x.Contains("payload byte 33", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SnapshotComparisonDetectsSingleCoefficientByteAndRawGainStep()
    {
        var expected = RawSnapshot();
        var changedPayload = expected.Bands[0].RawPayload.ToArray();
        changedPayload[7] ^= 0x01;
        var changed = expected with
        {
            PreGainRaw = (short)(expected.PreGainRaw + 1),
            Bands = [new HardwareBandSnapshot(0, changedPayload), .. expected.Bands.Skip(1)]
        };

        var differences = PhysicalSnapshotComparer.Differences(expected, changed);

        Assert.IsTrue(differences.Any(x => x.Contains("pre gain raw", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(differences.Any(x => x.Contains("payload byte 7", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TransitionPlanUsesOneQuarterDbIndividualBandAndNoBulkOrGainChanges()
    {
        var original = RawSnapshot();

        var plan = PhysicalTransitionPlanner.Create(original);

        Assert.AreEqual(64, Math.Abs(plan.IndividualBand.GainRaw - original.Bands[plan.IndividualBand.Index].GainRaw));
        Assert.IsTrue(plan.IndividualBand.FilterType is
            Moondrop.Core.Devices.PeqFilterType.Peaking or
            Moondrop.Core.Devices.PeqFilterType.LowShelf2 or
            Moondrop.Core.Devices.PeqFilterType.HighShelf2);
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, plan.Baseline));
        Assert.HasCount(8, plan.Bulk.Bands);
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, plan.Bulk));
        Assert.IsEmpty(plan.BulkChanges);
    }

    [TestMethod]
    public void TransitionPlanLeavesPassAndDisabledBandsUntouchedAndChangesOneSupportedBand()
    {
        var original = RawSnapshot();
        var replacements = original.Bands.Select(band => band.RawPayload.ToArray()).ToArray();
        replacements[0][33] = (byte)Moondrop.Core.Devices.PeqFilterType.LowPass2;
        replacements[1][33] = (byte)Moondrop.Core.Devices.PeqFilterType.HighPass2;
        replacements[2][33] = (byte)Moondrop.Core.Devices.PeqFilterType.Disabled;
        Array.Clear(replacements[2], 7, 20);
        original = original with
        {
            Bands = replacements.Select((payload, index) => new HardwareBandSnapshot(index, payload)).ToArray()
        };

        var plan = PhysicalTransitionPlanner.Create(original);

        Assert.AreEqual(3, plan.IndividualBand.Index);
        Assert.IsEmpty(PhysicalSnapshotComparer.Differences(original, plan.Bulk));
        Assert.AreEqual(0, original.Bands.Take(3).SelectMany((band, index) =>
            PhysicalSnapshotComparer.Differences(
                original with { Bands = [band] },
                original with { Bands = [plan.Individual.Bands[index]] })).Count());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ReadOnlyPreparePathBuildsConsistentSessionPlanWithoutWatchdogOrStateWrites()
    {
        var watchdogVariables = new[]
        {
            PhysicalWatchdogProcessGate.TokenEnvironmentVariable,
            PhysicalWatchdogProcessGate.HeartbeatEnvironmentVariable,
            PhysicalWatchdogProcessGate.SessionIdEnvironmentVariable,
            PhysicalWatchdogProcessGate.OneRunTokenEnvironmentVariable,
            PhysicalWatchdogProcessGate.SourceFingerprintEnvironmentVariable,
            PhysicalWatchdogProcessGate.RuntimeManifestEnvironmentVariable,
            PhysicalWatchdogProcessGate.LineageFingerprintEnvironmentVariable,
            PhysicalWatchdogProcessGate.ParentPidEnvironmentVariable,
            PhysicalWatchdogProcessGate.ParentStartEnvironmentVariable,
            PhysicalWatchdogProcessGate.ParentExecutableEnvironmentVariable
        };
        var originalEnvironment = watchdogVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var transport = new SupportHidTransport();
        EnqueueSnapshot(transport, mutateLastCoefficient: false);
        EnqueueSnapshot(transport, mutateLastCoefficient: false);
        var frames = new List<Moondrop.Hardware.DawnPro2HidReadFrame>();
        var identity = new Moondrop.Hardware.DawnPro2HidIdentity("hid://pinned", "35D8011D251117");

        try
        {
            foreach (var name in watchdogVariables)
                Environment.SetEnvironmentVariable(name, null);

            await using var device = await PhysicalDawnPro2DeviceOpener.OpenReadOnlyPrepareWithRetriesAsync(
                identity,
                frames,
                attempts: 1,
                _ => transport,
                new ImmediateDeviceDelay());

            var preparation = await PhysicalPreparePlanner.ReadAndPlanAsync(device, identity);
            var session = PhysicalSessionArtifact.Create(
                preparation.Original,
                preparation.Plan,
                "one-run-token",
                frames,
                sourceFingerprint: new string('A', 64),
                runtimeManifestSha256: new string('B', 64),
                sessionId: new string('C', 32));

            Assert.AreEqual(PhysicalSessionPhase.Prepared, session.Phase);
            Assert.AreEqual("1.5", preparation.Original.Firmware);
            Assert.AreEqual(9, preparation.Original.ActiveEq);
            Assert.HasCount(24, transport.Packets);
            Assert.HasCount(24, frames);
            Assert.IsTrue(transport.Packets.All(packet => packet[1] == Moondrop.Core.Protocol.DawnPro2Protocol.Read));
        }
        finally
        {
            foreach (var pair in originalEnvironment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [TestMethod]
    [DataRow(nameof(WatchdogProtectedPhysicalPhase.Execute))]
    [DataRow(nameof(WatchdogProtectedPhysicalPhase.Recovery))]
    public async Task WriteCapablePhaseRejectsMissingOrInvalidWatchdogBeforeTransportOpenOrWrite(
        string phaseName)
    {
        var phase = Enum.Parse<WatchdogProtectedPhysicalPhase>(phaseName);
        var transport = new SupportHidTransport();
        var frames = new List<Moondrop.Hardware.DawnPro2HidReadFrame>();
        var identity = new Moondrop.Hardware.DawnPro2HidIdentity("hid://pinned", "35D8011D251117");
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"moondrop-watchdog-progress-{Guid.NewGuid():N}");
        var binding = new PhysicalWatchdogSessionBinding(
            new string('C', 32),
            "one-run-token",
            new string('A', 64),
            new string('B', 64),
            new string('D', 64));
        var invalid = new PhysicalWatchdogAuthorization(
            "invalid",
            Path.Combine(repositoryRoot, "heartbeat.json"),
            binding,
            42,
            DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
            Path.Combine(repositoryRoot, "Moondrop.PhysicalWatchdog.exe"));
        var openCount = 0;
        Moondrop.Hardware.IDawnPro2HidTransport Open(Moondrop.Hardware.DawnPro2HidIdentity _)
        {
            openCount++;
            return transport;
        }

        var missingError = await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() =>
            PhysicalDawnPro2DeviceOpener.OpenWatchdogProtectedWithRetriesAsync(
                identity,
                frames,
                attempts: 1,
                phase,
                authorization: null,
                repositoryRoot,
                Open,
                new ImmediateDeviceDelay()));
        await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() =>
            PhysicalDawnPro2DeviceOpener.OpenWatchdogProtectedWithRetriesAsync(
                identity,
                frames,
                attempts: 1,
                phase,
                invalid,
                repositoryRoot,
                Open,
                new ImmediateDeviceDelay()));

        StringAssert.Contains(missingError.Message, "predicate=authorization-present");
        Assert.IsFalse(missingError.Message.Contains("one-run-token", StringComparison.Ordinal));
        Assert.AreEqual(0, openCount);
        Assert.IsEmpty(transport.Packets);
    }

    [TestMethod]
    public async Task ConsistentSnapshotReaderRejectsCoefficientChangeBetweenCompletePasses()
    {
        var transport = new SupportHidTransport();
        EnqueueSnapshot(transport, mutateLastCoefficient: false);
        EnqueueSnapshot(transport, mutateLastCoefficient: true);
        var progressCount = 0;
        var device = new Moondrop.Hardware.DawnPro2Device(
            transport,
            new ImmediateDeviceDelay(),
            transactionProgress: () =>
            {
                progressCount++;
                return Task.CompletedTask;
            });
        var identity = new Moondrop.Hardware.DawnPro2HidIdentity("hid://pinned", "35D8011D251117");

        var error = await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(
            () => HardwareSnapshotReader.ReadConsistentAsync(device, identity));

        StringAssert.Contains(error.Message, "inconsistent");
        Assert.AreEqual(24, transport.WriteCount);
        Assert.AreEqual(24, progressCount);
    }

    private static HardwareSnapshot Snapshot() => RawSnapshot();

    private static HardwareSnapshot RawSnapshot()
    {
        var bands = Enumerable.Range(0, 8).Select(index =>
        {
            var payload = new byte[Moondrop.Core.Protocol.DawnPro2Protocol.PayloadLength];
            payload[4] = (byte)index;
            payload[27] = 0xE8;
            payload[28] = 0x03;
            Moondrop.Core.Protocol.DawnPro2Protocol.EncodeFixedPoint(1).CopyTo(payload, 29);
            payload[33] = (byte)Moondrop.Core.Devices.PeqFilterType.Peaking;
            payload[35] = Moondrop.Core.Protocol.DawnPro2Protocol.PeqIndex;
            return new HardwareBandSnapshot(index, payload);
        }).ToArray();
        return new HardwareSnapshot(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new Moondrop.Hardware.DawnPro2HidIdentity("hid://pinned", "35D8011D251117"),
            "1.5",
            9,
            -512,
            256,
            bands);
    }

    private static void EnqueueSnapshot(SupportHidTransport transport, bool mutateLastCoefficient)
    {
        var firmware = new byte[Moondrop.Core.Protocol.DawnPro2Protocol.PayloadLength];
        "1.5"u8.CopyTo(firmware.AsSpan(3));
        transport.Enqueue(firmware);
        var active = new byte[Moondrop.Core.Protocol.DawnPro2Protocol.PayloadLength];
        active[3] = 9;
        transport.Enqueue(active);
        var preGain = new byte[Moondrop.Core.Protocol.DawnPro2Protocol.PayloadLength];
        Moondrop.Core.Protocol.DawnPro2Protocol.EncodeFixedPoint(-2).CopyTo(preGain, 3);
        transport.Enqueue(preGain);
        var globalGain = new byte[Moondrop.Core.Protocol.DawnPro2Protocol.PayloadLength];
        Moondrop.Core.Protocol.DawnPro2Protocol.EncodeFixedPoint(1).CopyTo(globalGain, 3);
        transport.Enqueue(globalGain);
        foreach (var band in RawSnapshot().Bands)
        {
            var payload = band.RawPayload.ToArray();
            if (mutateLastCoefficient && band.Index == 7)
                payload[26] = 1;
            transport.Enqueue(payload);
        }
    }
}


file sealed class FakePhysicalPresenceProbe(params PhysicalPresenceSample[] samples) : IPhysicalPresenceProbe
{
    private readonly Queue<PhysicalPresenceSample> _samples = new(samples);
    public int SampleCount { get; private set; }

    public Task<PhysicalPresenceSample> SampleAsync(Moondrop.Hardware.DawnPro2HidIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SampleCount++;
        if (_samples.Count == 0)
            throw new InvalidOperationException("No scripted presence sample remains.");
        return Task.FromResult(_samples.Dequeue());
    }
}

file sealed class ThrowBeforePublishFault : IPhysicalArtifactFaultInjector
{
    public void BeforePublish() => throw new IOException("injected publication failure");
}

file sealed class ImmediateDeviceDelay : Moondrop.Hardware.IDeviceDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SupportHidTransport : Moondrop.Hardware.IDawnPro2HidTransport
{
    private readonly Queue<IReadOnlyList<byte>> _responses = new();
    public int WriteCount { get; private set; }
    public List<IReadOnlyList<byte>> Packets { get; } = [];

    public void Enqueue(IReadOnlyList<byte> payload) =>
        _responses.Enqueue([Moondrop.Core.Protocol.DawnPro2Protocol.ReportId, .. payload]);

    public Task WriteAsync(IReadOnlyList<byte> packet, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        Packets.Add(packet.ToArray());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<byte>> ReadAsync(int length, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responses.Dequeue());
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class InjectedPhysicalExecuteActions(
    PhysicalExecuteStep injectedStep,
    bool failOnlyOnce) : IPhysicalExecuteActions
{
    private bool _failed;
    public List<PhysicalExecuteStep> Calls { get; } = [];

    public Task RunAsync(PhysicalExecuteStep step)
    {
        Calls.Add(step);
        if (step == injectedStep && (!failOnlyOnce || !_failed))
        {
            _failed = true;
            throw new IOException($"injected {step}");
        }
        return Task.CompletedTask;
    }
}

file sealed class SecretLeakingPhysicalExecuteActions(
    string sessionPath,
    string confirmation,
    string oneRunToken) : IPhysicalExecuteActions
{
    public Task RunAsync(PhysicalExecuteStep step) =>
        throw new IOException($"injected {step}: {sessionPath}; {confirmation}; {oneRunToken}");
}

file sealed class FakeProcessIdentityProvider(
    PhysicalProcessIdentity current,
    params PhysicalProcessIdentity[] other) : IPhysicalProcessIdentityProvider
{
    private readonly IReadOnlyDictionary<int, PhysicalProcessIdentity> _identities =
        other.Append(current).ToDictionary(identity => identity.ProcessId);

    public PhysicalProcessIdentity Current() => current;

    public PhysicalProcessIdentity Get(int processId) => _identities[processId];
}

file sealed class SequenceIdentitySnapshotReader(params PhysicalProcessIdentity[] identities)
    : IPhysicalIdentitySnapshotReader
{
    private int _index;

    public PhysicalProcessIdentity Read(int processId) =>
        identities[Math.Min(_index++, identities.Length - 1)];
}

file sealed class RemappedProcessIdentityProvider(
    PhysicalProcessIdentity current,
    int requestedProcessId,
    PhysicalProcessIdentity returnedIdentity) : IPhysicalProcessIdentityProvider
{
    public PhysicalProcessIdentity Current() => current;

    public PhysicalProcessIdentity Get(int processId) =>
        processId == requestedProcessId
            ? returnedIdentity
            : throw new KeyNotFoundException($"No identity for PID {processId}.");
}

file sealed class PhysicalLineageFixture : IAsyncDisposable
{
    private PhysicalLineageFixture(
        string root,
        string token,
        PhysicalWatchdogSessionBinding binding,
        string heartbeatPath,
        string watchdogPath,
        string runnerPath,
        DateTimeOffset startedAtUtc)
    {
        Root = root;
        Token = token;
        Binding = binding;
        HeartbeatPath = heartbeatPath;
        WatchdogPath = watchdogPath;
        RunnerPath = runnerPath;
        StartedAtUtc = startedAtUtc;
        Authorization = new PhysicalWatchdogAuthorization(
            token,
            heartbeatPath,
            binding,
            200,
            startedAtUtc,
            watchdogPath,
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(runnerPath)!)!, PhysicalRuntimeManifestStore.FileName));
        MatchingIdentities = new FakeProcessIdentityProvider(
            new PhysicalProcessIdentity(300, 200, startedAtUtc.AddSeconds(1), runnerPath),
            new PhysicalProcessIdentity(200, 100, startedAtUtc, watchdogPath));
    }

    public string Root { get; }
    public string Token { get; }
    public PhysicalWatchdogSessionBinding Binding { get; }
    public string HeartbeatPath { get; }
    public string WatchdogPath { get; }
    public string RunnerPath { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public PhysicalWatchdogAuthorization Authorization { get; }
    public FakeProcessIdentityProvider MatchingIdentities { get; }

    public static async Task<PhysicalLineageFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moondrop-lineage-fixture-{Guid.NewGuid():N}");
        var token = "dawn-pro2-watchdog-0123456789abcdef0123456789abcdef";
        var binding = new PhysicalWatchdogSessionBinding(
            "0123456789abcdef0123456789abcdef",
            "fixture-one-run-secret",
            new string('A', 64),
            new string('B', 64),
            new string('C', 64));
        var heartbeat = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", token, "heartbeat.json");
        var watchdog = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved", "watchdog", "Moondrop.PhysicalWatchdog.exe");
        var runner = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", "approved", "physical-tests", "Moondrop.PhysicalTests.exe");
        var started = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeat)!);
        Directory.CreateDirectory(Path.GetDirectoryName(watchdog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(runner)!);
        await File.WriteAllBytesAsync(watchdog, [1]);
        await File.WriteAllBytesAsync(runner, [2]);
        var manifest = RuntimeApphostManifestBinding.CreateManifest(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([2])),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));
        binding = binding with { RuntimeManifestSha256 = manifest.AggregateSha256 };
        PhysicalRuntimeManifestStore.WriteCreateNew(Path.GetDirectoryName(Path.GetDirectoryName(runner)!)!, manifest);
        await PhysicalArtifactWriter.WriteJsonAsync(
            heartbeat,
            new PhysicalWatchdogHeartbeatState(
                "RunnerStarting",
                DateTimeOffset.UtcNow,
                200,
                started,
                watchdog,
                token,
                binding.SessionId,
                binding.OneRunToken,
                binding.SourceFingerprint,
                binding.RuntimeManifestSha256,
                binding.LineageFingerprint,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1]))));
        return new PhysicalLineageFixture(root, token, binding, heartbeat, watchdog, runner, started);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

file sealed class FakePhysicalProcessQuery : IPhysicalProcessQuery
{
    public List<string> NativeNames { get; } = [];
    public List<string> WmiQueries { get; } = [];
    public IReadOnlyList<PhysicalNativeProcess> NativeProcesses { get; init; } = [];
    public Exception? NativeError { get; init; }
    public Exception? PythonQueryError { get; init; }
    public IReadOnlyList<IPhysicalWmiProcess> DawnProRows { get; init; } = [];
    public IReadOnlyList<IPhysicalWmiProcess> PythonRows { get; init; } = [];
    public IReadOnlyList<IPhysicalWmiProcess> UnrelatedRows { get; init; } = [];
    public HashSet<int> RunningProcessIds { get; init; } = [];
    public List<int> RunningChecks { get; } = [];

    public IReadOnlyList<PhysicalNativeProcess> GetProcessesByName(string processName)
    {
        NativeNames.Add(processName);
        if (NativeError is not null)
            throw NativeError;
        return NativeProcesses;
    }

    public IReadOnlyList<IPhysicalWmiProcess> QueryWmi(string query)
    {
        WmiQueries.Add(query);
        if (!query.Contains(" WHERE ", StringComparison.Ordinal))
            return [.. DawnProRows, .. PythonRows, .. UnrelatedRows];
        if (!query.Contains("Name LIKE '%DawnPro%'", StringComparison.Ordinal) && PythonQueryError is not null)
            throw PythonQueryError;
        return query.Contains("Name LIKE '%DawnPro%'", StringComparison.Ordinal)
            ? DawnProRows
            : PythonRows;
    }

    public bool IsRunning(int processId)
    {
        RunningChecks.Add(processId);
        return RunningProcessIds.Contains(processId);
    }
}

file sealed class FakePhysicalWmiProcess(IReadOnlyDictionary<string, object?> properties) : IPhysicalWmiProcess
{
    public int ReadCount { get; private set; }

    public object? ReadProperty(string propertyName)
    {
        ReadCount++;
        var value = properties[propertyName];
        if (value is Exception error)
            throw error;
        return value;
    }

    public void Dispose()
    {
    }
}
