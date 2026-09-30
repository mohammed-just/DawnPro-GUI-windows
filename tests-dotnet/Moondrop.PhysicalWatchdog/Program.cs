using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Moondrop.PhysicalWatchdog;

return await PhysicalWatchdogProgram.RunAsync(args).ConfigureAwait(false);

internal static class PhysicalWatchdogProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                Console.WriteLine("Moondrop.PhysicalWatchdog --mode <execute|recovery> --session <path> [--repo <path>] [--dotnet <path>] [--confirmation <token>] [--dry-run]");
                Console.WriteLine("Moondrop.PhysicalWatchdog --build-runtime-smoke --repo <path> --session-id <32 hex> --generation <name> [--dotnet <path>]");
                Console.WriteLine("Moondrop.PhysicalWatchdog --verify-runtime-approval --repo <path> --source-root <staged source> --physical-output <path> --watchdog-output <path>");
                return 0;
            }
            if (args.Length == 9 &&
                string.Equals(args[0], "--offline-topology-probe", StringComparison.Ordinal) &&
                string.Equals(args[1], "--physical-apphost", StringComparison.Ordinal) &&
                string.Equals(args[3], "--report", StringComparison.Ordinal) &&
                string.Equals(args[5], "--repo", StringComparison.Ordinal) &&
                string.Equals(args[7], "--runtime-sha256", StringComparison.Ordinal))
            {
                var root = Path.GetFullPath(args[6]);
                var runnerDirectory = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
                var watchdogDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
                var runtimeRoot = Path.GetDirectoryName(runnerDirectory)!;
                var sourceRoot = Path.Combine(runtimeRoot, "source");
                var metadata = PhysicalRuntimeBuildPlan.Create(root, sourceRoot, new string('0', 32), "offline-smoke").MetadataPaths;
                var manifest = HarnessBuildFingerprint.CaptureRuntime(root, runnerDirectory, watchdogDirectory, sourceRoot, metadata);
                if (!string.Equals(manifest.AggregateSha256, args[8], StringComparison.Ordinal))
                    throw new InvalidDataException("Offline topology smoke runtime aggregate does not match the independently captured complete manifest.");
                var observation = await PhysicalOfflineTopologyProbe.RunWatchdogAsync(args[2], args[4], manifest).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(observation, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Contains("--build-runtime-smoke", StringComparer.OrdinalIgnoreCase))
            {
                var auditRepositoryRoot = ReadOption(args, "--repo") ?? FindRepositoryRoot();
                var auditDotnetPath = ReadOption(args, "--dotnet") ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
                var sessionId = ReadValueOption(args, "--session-id")
                                ?? throw new ArgumentException("--build-runtime-smoke requires --session-id.");
                var generation = ReadValueOption(args, "--generation")
                                 ?? throw new ArgumentException("--build-runtime-smoke requires --generation.");
                var built = await PhysicalRuntimeBuilder.BuildAuditCandidateAsync(
                    auditRepositoryRoot,
                    auditDotnetPath,
                    sessionId,
                    generation).ConfigureAwait(false);
                var sourceCounts = HarnessBuildFingerprint.CountSourceInputs(built.SourceFingerprint);
                var runtimeCounts = HarnessBuildFingerprint.CountRuntimeInputs(built.RuntimeManifest);
                Console.WriteLine("AUDIT CANDIDATE ONLY; this command does not approve or authorize a physical session.");
                Console.WriteLine($"Source: {built.SourceFingerprint.AggregateSha256}");
                Console.WriteLine($"Source inputs: {sourceCounts.TotalInputCount}");
                Console.WriteLine($"Source presence sentinels: {sourceCounts.SourcePresenceSentinelCount}");
                Console.WriteLine($"Source content inputs: {sourceCounts.SourceContentInputCount}");
                Console.WriteLine($"Runtime: {built.RuntimeManifest.AggregateSha256}");
                Console.WriteLine($"Runtime inputs: {runtimeCounts.TotalInputCount}");
                Console.WriteLine($"Runner tree inputs: {runtimeCounts.RunnerTreeInputCount}");
                Console.WriteLine($"Watchdog tree inputs: {runtimeCounts.WatchdogTreeInputCount}");
                Console.WriteLine($"Metadata inputs: {runtimeCounts.MetadataInputCount}");
                Console.WriteLine($"Source root: {built.Plan.SourceRoot}");
                Console.WriteLine($"Physical apphost: {Path.Combine(built.Plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe")}");
                Console.WriteLine($"Watchdog apphost: {Path.Combine(built.Plan.WatchdogOutputDirectory, "Moondrop.PhysicalWatchdog.exe")}");
                return 0;
            }
            if (args.Contains("--print-source-fingerprint", StringComparer.OrdinalIgnoreCase))
            {
                var auditRepositoryRoot = ReadOption(args, "--repo") ?? FindRepositoryRoot();
                var source = HarnessBuildFingerprint.CaptureSource(auditRepositoryRoot);
                var counts = HarnessBuildFingerprint.CountSourceInputs(source);
                Console.WriteLine(source.AggregateSha256);
                Console.WriteLine($"Inputs: {counts.TotalInputCount}");
                Console.WriteLine($"Presence sentinels: {counts.SourcePresenceSentinelCount}");
                Console.WriteLine($"Content inputs: {counts.SourceContentInputCount}");
                return 0;
            }
            if (args.Contains("--print-runtime-manifest", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--verify-runtime-approval", StringComparer.OrdinalIgnoreCase))
            {
                var auditRepositoryRoot = ReadOption(args, "--repo") ?? FindRepositoryRoot();
                var physicalOutput = ReadOption(args, "--physical-output")
                                     ?? throw new ArgumentException("--print-runtime-manifest requires --physical-output.");
                var watchdogOutput = ReadOption(args, "--watchdog-output")
                                     ?? throw new ArgumentException("--print-runtime-manifest requires --watchdog-output.");
                var sourceRoot = ReadOption(args, "--source-root") ?? auditRepositoryRoot;
                var metadata = PhysicalRuntimeBuildPlan.Create(
                    auditRepositoryRoot,
                    new string('0', 32),
                    "manifest-audit").MetadataPaths;
                var runtime = HarnessBuildFingerprint.CaptureRuntime(
                    auditRepositoryRoot,
                    physicalOutput,
                    watchdogOutput,
                    sourceRoot,
                    metadata);
                var counts = HarnessBuildFingerprint.CountRuntimeInputs(runtime);
                Console.WriteLine(runtime.AggregateSha256);
                Console.WriteLine($"Inputs: {counts.TotalInputCount}");
                Console.WriteLine($"Runner tree inputs: {counts.RunnerTreeInputCount}");
                Console.WriteLine($"Watchdog tree inputs: {counts.WatchdogTreeInputCount}");
                Console.WriteLine($"Metadata inputs: {counts.MetadataInputCount}");
                if (args.Contains("--verify-runtime-approval", StringComparer.OrdinalIgnoreCase))
                {
                    var source = HarnessBuildFingerprint.CaptureStagedSource(sourceRoot);
                    var approval = PhysicalRuntimeApprovalManifest.ReadStrict(Path.Combine(
                        auditRepositoryRoot,
                        PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    PhysicalRuntimeApprovalManifest.RequireMatches(approval, source, runtime);
                    Console.WriteLine("Independent source/runtime approval matches this staged self-contained build exactly.");
                }
                return 0;
            }
            var options = Options.Parse(args);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The physical watchdog is supported only on Windows.");
            var repositoryRoot = options.RepositoryRoot ?? FindRepositoryRoot();
            var dotnetPath = options.DotnetPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
            var initialState = DurableSessionReader.ReadNewest(options.SessionPath);
            using var watchdogProcess = Process.GetCurrentProcess();
            var owner = new WatchdogOwnerIdentity(
                watchdogProcess.Id,
                watchdogProcess.StartTime.ToUniversalTime(),
                Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the physical watchdog executable path."));
            var token = $"dawn-pro2-watchdog-{Guid.NewGuid():N}";
            var command = PhysicalTestCommandBuilder.Build(
                options.Mode,
                repositoryRoot,
                options.SessionPath,
                options.Confirmation,
                token,
                initialState,
                owner);
            if (options.DryRun)
            {
                Console.WriteLine(PhysicalTestCommandBuilder.DescribeForDryRun(command));
                return 0;
            }

            var fresh = await EnsureFreshReviewedReleaseAsync(
                repositoryRoot,
                dotnetPath,
                owner,
                initialState,
                $"{options.Mode.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}").ConfigureAwait(false);
            command = PhysicalTestCommandBuilder.Build(
                options.Mode,
                repositoryRoot,
                options.SessionPath,
                options.Confirmation,
                token,
                initialState,
                owner,
                Path.Combine(fresh.Plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe"));
            var exit = await RunSupervisedAsync(command).ConfigureAwait(false);
            var state = DurableSessionReader.ReadNewest(command.SessionPath);
            if (options.Mode == WatchdogMode.Recovery)
            {
                for (var attempt = 1; PhysicalWatchdogPolicy.ShouldRetryRecovery(attempt, exit, state.Phase); attempt++)
                {
                    Console.Error.WriteLine($"Supervised recovery attempt {attempt} ended with exit {exit} and durable phase {state.Phase}; retrying within the bounded policy.");
                    fresh = await EnsureFreshReviewedReleaseAsync(
                        repositoryRoot,
                        dotnetPath,
                        owner,
                        initialState,
                        $"recovery-{Guid.NewGuid():N}").ConfigureAwait(false);
                    var retry = PhysicalTestCommandBuilder.Build(
                        WatchdogMode.Recovery,
                        repositoryRoot,
                        options.SessionPath,
                        confirmation: null,
                        $"dawn-pro2-recovery-{Guid.NewGuid():N}",
                        initialState,
                        owner,
                        Path.Combine(fresh.Plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe"));
                    exit = await RunSupervisedAsync(retry).ConfigureAwait(false);
                    state = DurableSessionReader.ReadNewest(command.SessionPath);
                }
                return PhysicalWatchdogPolicy.FinalizeChildExit(exit, initialState, state);
            }
            if (options.Mode == WatchdogMode.Execute && PhysicalWatchdogPolicy.ShouldLaunchRecovery(state.Phase))
            {
                Console.Error.WriteLine($"Execute ended with durable phase {state.Phase}; launching supervised recovery automatically.");
                var recoveryExit = 1;
                DurableSessionState finalState;
                for (var attempt = 1; ; attempt++)
                {
                    fresh = await EnsureFreshReviewedReleaseAsync(
                        repositoryRoot,
                        dotnetPath,
                        owner,
                        initialState,
                        $"recovery-{Guid.NewGuid():N}").ConfigureAwait(false);
                    var recovery = PhysicalTestCommandBuilder.Build(
                        WatchdogMode.Recovery,
                        repositoryRoot,
                        options.SessionPath,
                        confirmation: null,
                        $"dawn-pro2-recovery-{Guid.NewGuid():N}",
                        initialState,
                        owner,
                        Path.Combine(fresh.Plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe"));
                    recoveryExit = await RunSupervisedAsync(recovery).ConfigureAwait(false);
                    finalState = DurableSessionReader.ReadNewest(command.SessionPath);
                    if (!PhysicalWatchdogPolicy.ShouldRetryRecovery(attempt, recoveryExit, finalState.Phase))
                        break;
                    Console.Error.WriteLine($"Supervised recovery attempt {attempt} ended with exit {recoveryExit} and durable phase {finalState.Phase}; retrying within the bounded policy.");
                }
                var combined = PhysicalWatchdogPolicy.CombineExecuteAndRecovery(exit, recoveryExit, initialState, finalState);
                Console.Error.WriteLine(combined.Summary);
                return combined.ExitCode;
            }
            return PhysicalWatchdogPolicy.FinalizeChildExit(exit, initialState, state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(DiagnosticText.SanitizeWatchdogFailure(ex, args));
            return 1;
        }
    }

    private static async Task<PhysicalRuntimeBuildResult> EnsureFreshReviewedReleaseAsync(
        string repositoryRoot,
        string dotnetPath,
        WatchdogOwnerIdentity owner,
        DurableSessionState expectedSession,
        string generation)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var fresh = await PhysicalRuntimeBuilder.BuildAsync(
            root,
            dotnetPath,
            expectedSession.SessionId,
            generation).ConfigureAwait(false);
        if (!string.Equals(fresh.SourceFingerprint.AggregateSha256, expectedSession.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Fresh approved source does not match the prepared session source fingerprint.");
        if (!string.Equals(fresh.RuntimeManifest.AggregateSha256, expectedSession.RuntimeManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Fresh complete runtime manifest does not match the prepared session runtime manifest.");
        var approval = PhysicalRuntimeApprovalManifest.ReadStrict(Path.Combine(
            root,
            PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        PhysicalRuntimeApprovalManifest.RequireSessionHashes(
            approval,
            expectedSession.SourceFingerprint,
            expectedSession.RuntimeManifestSha256);
        HarnessBuildFingerprint.RequirePublishedApphostTree(
            root,
            owner.ExecutablePath,
            fresh.Plan.WatchdogOutputDirectory,
            "Moondrop.PhysicalWatchdog");
        return fresh;
    }

    private static async Task<int> RunSupervisedAsync(PhysicalTestCommand command)
    {
        var startInfo = PhysicalRunnerLaunchPreparation.Prepare(
            command,
            () => WriteOwnerHeartbeat(command, "RunnerStarting"));

        ObservedPhysicalProcess? launched = null;
        using var ownedProcess = PhysicalProcessLauncher.StartOwnedSuspended(
            startInfo,
            processId => launched = WindowsCommandLineReader.Observe(processId));
        var process = ownedProcess.Process;
        var processJob = ownedProcess.Job;
        var owned = new OwnedPhysicalProcess(
            process.Id,
            launched!.StartedAtUtc,
            command.OwnershipToken,
            command.SessionPath,
            command.ProjectPath);
        try
        {
            var lastFingerprint = "";
            var lastProgress = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                DurableSessionState state;
                try
                {
                    state = DurableSessionReader.ReadNewest(command.SessionPath);
                }
                catch
                {
                    state = new DurableSessionState(DurablePhysicalPhase.Failed, DateTimeOffset.MinValue);
                }
                var heartbeat = WatchdogHeartbeatReader.TryRead(
                    command.Environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"],
                    command.Owner,
                    command.OwnershipToken,
                    command.Environment);
                var fingerprint = $"{state.Phase}|{state.UpdatedAtUtc:O}|{heartbeat?.Kind}|{heartbeat?.UpdatedAtUtc:O}";
                if (!string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
                {
                    lastFingerprint = fingerprint;
                    lastProgress.Restart();
                }
                if (lastProgress.Elapsed <= PhysicalWatchdogPolicy.InactivityLimit(state.Phase, heartbeat?.Kind))
                    continue;

                var observed = WindowsCommandLineReader.Observe(process.Id);
                if (!PhysicalWatchdogPolicy.CanTerminate(owned, observed))
                    throw new InvalidOperationException("Watchdog timeout occurred, but exact PID/start-time/command-line/session-token ownership could not be proven; refusing to terminate any process.");
                Console.Error.WriteLine($"Physical phase {state.Phase} was inactive beyond its supported bound; terminating only owned PID {process.Id} and its dedicated descendants.");
                await PhysicalProcessLauncher.RequireOwnedProcessStoppedAsync(process, processJob, terminateImmediately: true).ConfigureAwait(false);
                return 124;
            }
            await PhysicalProcessLauncher.RequireOwnedProcessStoppedAsync(process, processJob, terminateImmediately: false).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception operationFailure)
        {
            try
            {
                await PhysicalProcessLauncher.RequireOwnedProcessStoppedAsync(process, processJob, terminateImmediately: true).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Production watchdog supervision failed and cleanup could not be proven complete.", operationFailure, cleanupFailure);
            }
            throw;
        }
    }

    private static void WriteOwnerHeartbeat(PhysicalTestCommand command, string kind)
    {
        var path = command.Environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"];
        var directory = Path.GetDirectoryName(path)!;
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(command.RepositoryRoot, directory, "Physical watchdog heartbeat directory");
        TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical watchdog heartbeat");
        using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(directory, directory, "Physical watchdog heartbeat directory");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var state = new PhysicalWatchdogHeartbeatState(
            kind,
            DateTimeOffset.UtcNow,
            command.Owner.ProcessId,
            command.Owner.StartedAtUtc,
            Path.GetFullPath(command.Owner.ExecutablePath),
            command.OwnershipToken,
            command.Environment["MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID"],
            command.Environment["MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN"],
            command.Environment["MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT"],
            command.Environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256"],
            command.Environment["MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT"],
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(command.Owner.ExecutablePath))));
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(state));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        lease.Verify();
        TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical watchdog heartbeat");
        File.Move(temporary, path, overwrite: true);
        lease.Verify();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DawnPro.Wpf.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate DawnPro.Wpf.slnx.");
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        var value = ReadValueOption(args, name);
        return value is null ? null : Path.GetFullPath(value);
    }

    private static string? ReadValueOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private sealed record Options(
        WatchdogMode Mode,
        string SessionPath,
        string? Confirmation,
        bool DryRun,
        string? RepositoryRoot,
        string? DotnetPath)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--dry-run")
                {
                    values["--dry-run"] = "1";
                    continue;
                }
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException($"Invalid watchdog argument '{args[index]}'.");
                values[args[index]] = args[++index];
            }
            if (!values.TryGetValue("--mode", out var modeText) ||
                !Enum.TryParse<WatchdogMode>(modeText, ignoreCase: true, out var mode))
                throw new ArgumentException("Use --mode execute or --mode recovery.");
            if (!values.TryGetValue("--session", out var session) || string.IsNullOrWhiteSpace(session))
                throw new ArgumentException("Use --session with the prepared durable session path.");
            values.TryGetValue("--confirmation", out var confirmation);
            values.TryGetValue("--repo", out var repositoryRoot);
            values.TryGetValue("--dotnet", out var dotnetPath);
            return new Options(mode, Path.GetFullPath(session), confirmation, values.ContainsKey("--dry-run"), repositoryRoot, dotnetPath);
        }
    }
}

internal sealed record WatchdogHeartbeatState(string Kind, DateTimeOffset UpdatedAtUtc);
internal sealed record PhysicalWatchdogHeartbeatState(
    string Kind,
    DateTimeOffset UpdatedAtUtc,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAtUtc,
    string OwnerExecutablePath,
    string OwnershipToken,
    string SessionId,
    string OneRunToken,
    string SourceFingerprint,
    string RuntimeManifestSha256,
    string LineageFingerprint,
    string OwnerExecutableSha256 = "");

internal static class WatchdogHeartbeatReader
{
    public static WatchdogHeartbeatState? TryRead(
        string path,
        WatchdogOwnerIdentity owner,
        string ownershipToken,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("OwnerProcessId").GetInt32() != owner.ProcessId ||
                root.GetProperty("OwnerStartedAtUtc").GetDateTimeOffset() != owner.StartedAtUtc ||
                !string.Equals(
                    Path.GetFullPath(root.GetProperty("OwnerExecutablePath").GetString() ?? ""),
                    Path.GetFullPath(owner.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("OwnershipToken").GetString(), ownershipToken, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("SessionId").GetString(), environment["MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID"], StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("OneRunToken").GetString(), environment["MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN"], StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("SourceFingerprint").GetString(), environment["MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT"], StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("RuntimeManifestSha256").GetString(), environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256"], StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("LineageFingerprint").GetString(), environment["MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT"], StringComparison.Ordinal))
                return null;
            return new WatchdogHeartbeatState(
                root.GetProperty("Kind").GetString() ?? "",
                root.GetProperty("UpdatedAtUtc").GetDateTimeOffset());
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}

internal static class WindowsCommandLineReader
{
    private static readonly CoherentObservedPhysicalProcessProvider Provider =
        new(new WindowsObservedPhysicalProcessSnapshotReader());

    public static ObservedPhysicalProcess Observe(int processId) => Provider.Get(processId);
}
