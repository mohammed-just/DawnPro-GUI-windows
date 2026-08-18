using Moondrop.Core.Devices;
using Moondrop.Core.Protocol;
using Moondrop.Hardware;
using Moondrop.PhysicalWatchdog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Moondrop.Tests;

internal static class PhysicalTestGate
{
    public const string EnvironmentVariable = "MOONDROP_RUN_PHYSICAL_TESTS";
    public const string PrepareEnvironmentVariable = "MOONDROP_PREPARE_PHYSICAL_TESTS";
    public const string ConfirmationEnvironmentVariable = "MOONDROP_PHYSICAL_CONFIRMATION";
    public const string SessionPathEnvironmentVariable = "MOONDROP_PHYSICAL_SESSION_PATH";
    public const string RecoveryEnvironmentVariable = "MOONDROP_RUN_PHYSICAL_RECOVERY";
    public const string RecoverySnapshotEnvironmentVariable = "MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT";
    public const string ProfilePathEnvironmentVariable = "MOONDROP_EXECUTE_PROFILE_PATH";

    public static bool IsOptedIn(string? value) => string.Equals(value, "1", StringComparison.Ordinal);
}

internal static class PhysicalRunSettingsGate
{
    public const string ParameterName = "PhysicalHarnessEnabled";

    public static bool IsDedicated(
        string? runSettingsValue,
        string? prepareOptIn,
        string? executeOptIn,
        string? recoveryOptIn) =>
        string.Equals(runSettingsValue, "1", StringComparison.Ordinal) &&
        (PhysicalTestGate.IsOptedIn(prepareOptIn) ||
         PhysicalTestGate.IsOptedIn(executeOptIn) ||
         PhysicalTestGate.IsOptedIn(recoveryOptIn));
}

internal sealed record PhysicalLineageAuthorizationResult(bool IsAuthorized, string Diagnostic);

internal static class PhysicalWatchdogProcessGate
{
    private const int DiagnosticParentDepthLimit = 8;
    public const string TokenEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_TOKEN";
    public const string HeartbeatEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT";
    public const string SessionIdEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID";
    public const string OneRunTokenEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN";
    public const string SourceFingerprintEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT";
    public const string RuntimeManifestEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256";
    public const string RuntimeManifestPathEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_PATH";
    public const string LineageFingerprintEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT";
    public const string ParentPidEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_PARENT_PID";
    public const string ParentStartEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_PARENT_START_UTC";
    public const string ParentExecutableEnvironmentVariable = "MOONDROP_PHYSICAL_WATCHDOG_PARENT_EXE";

    public static bool IsAuthorized(
        PhysicalWatchdogAuthorization? authorization,
        string repositoryRoot,
        IPhysicalProcessIdentityProvider? processIdentityProvider = null) =>
        Evaluate(authorization, repositoryRoot, processIdentityProvider).IsAuthorized;

    public static PhysicalLineageAuthorizationResult Evaluate(
        PhysicalWatchdogAuthorization? authorization,
        string repositoryRoot,
        IPhysicalProcessIdentityProvider? processIdentityProvider = null)
    {
        if (authorization is null)
        {
            try
            {
                processIdentityProvider ??= new WindowsPhysicalProcessIdentityProvider();
                var current = processIdentityProvider.Current();
                return Rejected(
                    "authorization-present",
                    $"expected.authorization=present; expected.name=Moondrop.PhysicalWatchdog.exe; actual.authorization=missing; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.startedAtUtc={current.StartedAtUtc:O}; " +
                    $"actual.path={CanonicalPath(current.ExecutablePath)}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, expectedWatchdogProcessId: -1, processIdentityProvider));
            }
            catch (Exception ex)
            {
                return Rejected(
                    "authorization-present",
                    $"expected.authorization=present; expected.name=Moondrop.PhysicalWatchdog.exe; actual.authorization=missing; actual.processIdentity={ex.GetType().Name}");
            }
        }
        try
        {
            processIdentityProvider ??= new WindowsPhysicalProcessIdentityProvider();
            var current = processIdentityProvider.Current();
            var root = Path.GetFullPath(repositoryRoot);
            var physicalRuntimeRoot = Path.GetFullPath(Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime"));
            var currentPath = CanonicalPath(current.ExecutablePath);
            TrustedPhysicalPath.RequireNoReparse(root, "Physical repository root");
            TrustedPhysicalPath.RequireNoReparse(physicalRuntimeRoot, "Physical runtime root");
            using var repositoryLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, root, "Physical repository root");
            using var runtimeLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, physicalRuntimeRoot, "Physical runtime root");
            var currentRelative = Path.GetRelativePath(physicalRuntimeRoot, currentPath);
            if (!string.Equals(Path.GetFileName(currentPath), "Moondrop.PhysicalTests.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(currentRelative) ||
                currentRelative.Equals("..", StringComparison.Ordinal) ||
                currentRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return Rejected(
                    "physical-runner-apphost",
                    $"expected.name=Moondrop.PhysicalTests.exe; expected.root={physicalRuntimeRoot}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.startedAtUtc={current.StartedAtUtc:O}; " +
                    $"actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            TrustedPhysicalPath.RequireContainedNoReparse(physicalRuntimeRoot, currentPath, "Physical runner apphost");
            using var runnerLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(physicalRuntimeRoot, currentPath, "Physical runner apphost");
            if (authorization.Binding.SessionId.Length != 32 || !authorization.Binding.SessionId.All(Uri.IsHexDigit))
            {
                return Rejected(
                    "session-id-shape",
                    $"expected=32 hexadecimal characters; actual.length={authorization.Binding.SessionId.Length}; actual.value=[redacted]; " +
                    $"expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (string.IsNullOrWhiteSpace(authorization.Binding.OneRunToken))
            {
                return Rejected(
                    "one-run-token-present",
                    $"expected=non-empty secret; actual.present=false; actual.value=[redacted]; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (!IsSha256(authorization.Binding.SourceFingerprint))
                return Rejected("source-fingerprint-shape", $"expected=64 hexadecimal characters; actual.length={authorization.Binding.SourceFingerprint.Length}; actual.value=[redacted]; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
            if (!IsSha256(authorization.Binding.RuntimeManifestSha256))
                return Rejected("runtime-manifest-shape", $"expected=64 hexadecimal characters; actual.length={authorization.Binding.RuntimeManifestSha256.Length}; actual.value=[redacted]; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
            if (!IsSha256(authorization.Binding.LineageFingerprint))
                return Rejected("lineage-fingerprint-shape", $"expected=64 hexadecimal characters; actual.length={authorization.Binding.LineageFingerprint.Length}; actual.value=[redacted]; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
            var tokenPrefix = authorization.Token.StartsWith("dawn-pro2-watchdog-", StringComparison.Ordinal)
                ? "dawn-pro2-watchdog-"
                : authorization.Token.StartsWith("dawn-pro2-recovery-", StringComparison.Ordinal)
                    ? "dawn-pro2-recovery-"
                    : null;
            if (tokenPrefix is null ||
                authorization.Token[tokenPrefix.Length..] is not { Length: 32 } tokenSuffix ||
                !tokenSuffix.All(Uri.IsHexDigit))
            {
                return Rejected(
                    "ownership-token-shape",
                    $"expected=approved prefix plus 32 hexadecimal characters; actual=[redacted]; " +
                    $"expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            var expectedParentPath = Path.GetFullPath(authorization.ParentExecutablePath);
            TrustedPhysicalPath.RequireContainedNoReparse(physicalRuntimeRoot, expectedParentPath, "Physical watchdog apphost");
            var watchdogRelative = Path.GetRelativePath(physicalRuntimeRoot, expectedParentPath);
            if (!string.Equals(Path.GetFileName(expectedParentPath), "Moondrop.PhysicalWatchdog.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(watchdogRelative) ||
                watchdogRelative.Equals("..", StringComparison.Ordinal) ||
                watchdogRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return Rejected(
                    "watchdog-manifest-membership",
                    $"expected.name=Moondrop.PhysicalWatchdog.exe; expected.root={physicalRuntimeRoot}; actual.path={expectedParentPath}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (!File.Exists(expectedParentPath))
            {
                return Rejected(
                    "watchdog-executable-exists",
                    $"expected.path={expectedParentPath}; expected.exists=true; actual.exists=false; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            using var watchdogLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(physicalRuntimeRoot, expectedParentPath, "Physical watchdog apphost");
            var expectedHeartbeatPath = Path.GetFullPath(Path.Combine(
                root,
                "tests-dotnet",
                "artifacts",
                "watchdog",
                authorization.Token,
                "heartbeat.json"));
            var heartbeatRoot = Path.GetFullPath(Path.Combine(root, "tests-dotnet", "artifacts", "watchdog"));
            if (!string.Equals(expectedHeartbeatPath, Path.GetFullPath(authorization.HeartbeatPath), StringComparison.OrdinalIgnoreCase))
            {
                return Rejected(
                    "heartbeat-canonical-path",
                    $"expected.name=heartbeat.json; expected.ownershipDirectory=[redacted]; actual.name={Path.GetFileName(authorization.HeartbeatPath)}; actual.path=[redacted]; " +
                    $"expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            TrustedPhysicalPath.RequireContainedNoReparse(heartbeatRoot, expectedHeartbeatPath, "Physical heartbeat file");
            TrustedPhysicalPath.RequireContainedNoReparse(heartbeatRoot, authorization.HeartbeatPath, "Physical heartbeat file");
            using var heartbeatRootLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, heartbeatRoot, "Physical heartbeat root");
            HarnessFingerprint runtimeManifest;
            RuntimeApphostExpectedIdentities expectedRoles;
            try
            {
                var candidateRuntimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(currentPath)!)!;
                using var manifestLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(
                    candidateRuntimeRoot,
                    authorization.RuntimeManifestPath,
                    "Physical runtime manifest");
                runtimeManifest = PhysicalRuntimeManifestStore.ReadStrict(candidateRuntimeRoot, authorization.RuntimeManifestPath);
                expectedRoles = RuntimeApphostManifestBinding.ResolveExpectedIdentities(
                    authorization.Binding.RuntimeManifestSha256,
                    runtimeManifest,
                    candidateRuntimeRoot);
                manifestLease.Verify();
            }
            catch (Exception ex)
            {
                return Rejected(
                    ManifestFailurePredicate(ex),
                    $"expected.runner.path=[manifest]; actual.runner.path={currentPath}; expected.watchdog.path=[manifest]; actual.watchdog.path={expectedParentPath}; actual.type={ex.GetType().Name}; actual.details=[redacted]");
            }
            if (current.ParentProcessId != authorization.ParentProcessId)
            {
                return Rejected(
                    "direct-parent-pid",
                    FormatRoleIdentities(current, authorization, null, expectedRoles) + "; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            var parent = processIdentityProvider.Get(authorization.ParentProcessId);
            if (parent.ProcessId != authorization.ParentProcessId)
            {
                return Rejected(
                    "watchdog-process-id",
                    FormatRoleIdentities(current, authorization, parent, expectedRoles) + "; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (parent.StartedAtUtc != authorization.ParentStartedAtUtc)
            {
                return Rejected(
                    "watchdog-start-time",
                    FormatRoleIdentities(current, authorization, parent, expectedRoles) + "; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            var actualParentPath = CanonicalPath(parent.ExecutablePath);
            if (!string.Equals(expectedParentPath, actualParentPath, StringComparison.OrdinalIgnoreCase))
            {
                return Rejected(
                    "watchdog-executable-path",
                    FormatRoleIdentities(current, authorization, parent, expectedRoles) + "; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (!File.Exists(authorization.HeartbeatPath))
            {
                return Rejected(
                    "heartbeat-file-exists",
                    $"expected.name=heartbeat.json; expected.ownershipDirectory=[redacted]; actual.exists=false; " +
                    $"expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; expected.watchdogSha256={FileSha256(authorization.ParentExecutablePath)}; " +
                    $"actual.pid={current.ProcessId}; actual.parentPid={current.ParentProcessId}; actual.path={currentPath}; actual.sha256={FileSha256(current.ExecutablePath)}; " +
                    FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
            }
            if (File.Exists(authorization.HeartbeatPath))
            {
                using var heartbeatLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(
                    heartbeatRoot,
                    authorization.HeartbeatPath,
                    "Physical heartbeat file");
                PhysicalWatchdogHeartbeatState? heartbeat;
                try
                {
                    heartbeat = JsonSerializer.Deserialize<PhysicalWatchdogHeartbeatState>(
                        File.ReadAllBytes(Path.GetFullPath(authorization.HeartbeatPath)));
                    heartbeatLease.Verify();
                }
                catch (JsonException)
                {
                    return Rejected(
                        "heartbeat-json",
                        $"expected=valid schema; actual=JsonException; contents=[redacted]; " +
                        $"expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; " +
                        FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
                }
                if (heartbeat is null)
                {
                    return Rejected(
                        "heartbeat-schema",
                        $"expected=non-null object; actual=null; expected.watchdogPath={CanonicalPath(authorization.ParentExecutablePath)}; " +
                        FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
                }
                if (heartbeat.OwnerProcessId != authorization.ParentProcessId)
                {
                    return Rejected(
                        "heartbeat-owner-pid",
                        $"expected.pid={authorization.ParentProcessId}; actual.pid={heartbeat.OwnerProcessId}; " +
                        $"expected.path={CanonicalPath(authorization.ParentExecutablePath)}; expected.sha256={FileSha256(authorization.ParentExecutablePath)}; " +
                        $"actual.runnerPid={current.ProcessId}; actual.runnerParentPid={current.ParentProcessId}; " +
                        FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
                }
                if (heartbeat.OwnerStartedAtUtc != parent.StartedAtUtc)
                {
                    return Rejected(
                        "heartbeat-owner-start-time",
                        $"expected.startedAtUtc={parent.StartedAtUtc:O}; actual.startedAtUtc={heartbeat.OwnerStartedAtUtc:O}; " +
                        $"expected.path={expectedParentPath}; actual.path={actualParentPath}; " +
                        FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
                }
                var heartbeatOwnerPath = CanonicalPath(heartbeat.OwnerExecutablePath);
                if (!string.Equals(heartbeatOwnerPath, expectedParentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Rejected(
                        "heartbeat-owner-executable-path",
                        $"expected.path={expectedParentPath}; actual.path={heartbeatOwnerPath}; " +
                        $"expected.sha256={FileSha256(expectedParentPath)}; actual.sha256={FileSha256(heartbeat.OwnerExecutablePath)}; " +
                        FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider));
                }
                if (!string.Equals(heartbeat.OwnershipToken, authorization.Token, StringComparison.Ordinal))
                    return Rejected("heartbeat-ownership-token", $"expected=[redacted]; actual=[redacted]; match=false; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                if (!string.Equals(heartbeat.SessionId, authorization.Binding.SessionId, StringComparison.Ordinal))
                    return Rejected("heartbeat-session-id", $"expected={authorization.Binding.SessionId}; actual={heartbeat.SessionId}; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                if (!string.Equals(heartbeat.OneRunToken, authorization.Binding.OneRunToken, StringComparison.Ordinal))
                    return Rejected("heartbeat-one-run-token", $"expected=[redacted]; actual=[redacted]; match=false; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                if (!string.Equals(heartbeat.SourceFingerprint, authorization.Binding.SourceFingerprint, StringComparison.Ordinal))
                    return Rejected("heartbeat-source-fingerprint", $"expected={authorization.Binding.SourceFingerprint}; actual={heartbeat.SourceFingerprint}; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                if (!string.Equals(heartbeat.RuntimeManifestSha256, authorization.Binding.RuntimeManifestSha256, StringComparison.Ordinal))
                    return Rejected("heartbeat-runtime-manifest", $"expected={authorization.Binding.RuntimeManifestSha256}; actual={heartbeat.RuntimeManifestSha256}; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                if (!string.Equals(heartbeat.LineageFingerprint, authorization.Binding.LineageFingerprint, StringComparison.Ordinal))
                    return Rejected("heartbeat-lineage-fingerprint", $"expected={authorization.Binding.LineageFingerprint}; actual={heartbeat.LineageFingerprint}; {FormatRelevantParentChain(current, authorization.ParentProcessId, processIdentityProvider)}");
                try
                {
                    RuntimeApphostManifestBinding.Require(
                        authorization.Binding.RuntimeManifestSha256,
                        runtimeManifest,
                        currentPath,
                        expectedParentPath,
                        heartbeat.OwnerExecutableSha256);
                }
                catch (Exception ex)
                {
                    return Rejected(
                        ManifestFailurePredicate(ex),
                        $"expected.runner.path={expectedRoles.RunnerPath}; expected.runner.sha256={expectedRoles.RunnerSha256}; actual.runner.path={currentPath}; actual.runner.sha256={FileSha256(currentPath)}; " +
                        $"expected.watchdog.path={expectedRoles.WatchdogPath}; expected.watchdog.sha256={expectedRoles.WatchdogSha256}; actual.watchdog.path={actualParentPath}; actual.watchdog.sha256={FileSha256(actualParentPath)}; " +
                        $"actual.type={ex.GetType().Name}; actual.details=[redacted]");
                }
                runnerLease.Verify();
                watchdogLease.Verify();
                runtimeLease.Verify();
                repositoryLease.Verify();
                return new PhysicalLineageAuthorizationResult(true, "lineage authorization accepted");
            }
        }
        catch (Exception ex)
        {
            return Rejected(
                "process-identity-readable",
                $"expected=readable exact process/apphost identity; actual.type={ex.GetType().Name}; actual.details=[redacted]");
        }

        return Rejected("heartbeat-file-readable", "expected=readable complete heartbeat; actual=unavailable");
    }

    public static bool IsSessionOwned(PhysicalWatchdogSessionBinding binding, PhysicalSessionArtifact session) =>
        string.Equals(binding.SessionId, session.SessionId, StringComparison.Ordinal) &&
        string.Equals(binding.OneRunToken, session.OneRunToken, StringComparison.Ordinal) &&
        string.Equals(binding.SourceFingerprint, session.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(binding.RuntimeManifestSha256, session.RuntimeManifestSha256, StringComparison.Ordinal) &&
        string.Equals(binding.LineageFingerprint, PhysicalSessionStore.ImmutableLineageFingerprint(session), StringComparison.Ordinal);

    public static bool IsSessionOwned(PhysicalWatchdogAuthorization authorization, PhysicalSessionArtifact session) =>
        IsSessionOwned(authorization.Binding, session);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static PhysicalLineageAuthorizationResult Rejected(string predicate, string details) =>
        new(false, $"lineage authorization rejected; predicate={DiagnosticText.Sanitize(predicate)}; {DiagnosticText.Sanitize(details)}");

    private static string FormatRelevantParentChain(
        PhysicalProcessIdentity current,
        int expectedWatchdogProcessId,
        IPhysicalProcessIdentityProvider provider)
    {
        var entries = new List<string>();
        var visited = new HashSet<int>();
        var node = current;
        var terminated = false;
        for (var depth = 0; depth < DiagnosticParentDepthLimit; depth++)
        {
            if (!visited.Add(node.ProcessId))
            {
                entries.Add($"chain[{depth}].cyclePid={node.ProcessId}");
                terminated = true;
                break;
            }
            entries.Add(
                $"chain[{depth}].pid={node.ProcessId},parentPid={node.ParentProcessId},startedAtUtc={node.StartedAtUtc:O}," +
                $"path={CanonicalPath(node.ExecutablePath)},sha256={FileSha256(node.ExecutablePath)}");
            if (node.ProcessId == expectedWatchdogProcessId || node.ParentProcessId <= 0)
            {
                terminated = true;
                break;
            }
            try
            {
                node = provider.Get(node.ParentProcessId);
            }
            catch (Exception ex)
            {
                entries.Add($"chain[{depth + 1}].pid={node.ParentProcessId},unavailableType={ex.GetType().Name},details=[redacted]");
                terminated = true;
                break;
            }
        }
        if (!terminated)
            entries.Add($"chain.truncated=true; chain.limit={DiagnosticParentDepthLimit}");
        return string.Join("; ", entries);
    }

    private static string FormatRoleIdentities(
        PhysicalProcessIdentity runner,
        PhysicalWatchdogAuthorization authorization,
        PhysicalProcessIdentity? actualWatchdog,
        RuntimeApphostExpectedIdentities expected)
    {
        var runnerPath = CanonicalPath(runner.ExecutablePath);
        var actualWatchdogText = actualWatchdog is null
            ? "actual.watchdog.pid=[not-observed]; actual.watchdog.parentPid=[not-observed]; actual.watchdog.startedAtUtc=[not-observed]; actual.watchdog.path=[not-observed]; actual.watchdog.sha256=[not-observed]"
            : $"actual.watchdog.pid={actualWatchdog.ProcessId}; actual.watchdog.parentPid={actualWatchdog.ParentProcessId}; actual.watchdog.startedAtUtc={actualWatchdog.StartedAtUtc:O}; actual.watchdog.path={CanonicalPath(actualWatchdog.ExecutablePath)}; actual.watchdog.sha256={FileSha256(actualWatchdog.ExecutablePath)}";
        return
            $"expected.runner.name=Moondrop.PhysicalTests.exe; expected.runner.path={expected.RunnerPath}; expected.runner.sha256={expected.RunnerSha256}; " +
            $"actual.runner.pid={runner.ProcessId}; actual.runner.parentPid={runner.ParentProcessId}; actual.runner.startedAtUtc={runner.StartedAtUtc:O}; actual.runner.path={runnerPath}; actual.runner.sha256={FileSha256(runner.ExecutablePath)}; " +
            $"expected.watchdog.pid={authorization.ParentProcessId}; expected.watchdog.startedAtUtc={authorization.ParentStartedAtUtc:O}; expected.watchdog.path={expected.WatchdogPath}; expected.watchdog.sha256={expected.WatchdogSha256}; " +
            actualWatchdogText;
    }

    private static string ManifestFailurePredicate(Exception exception)
    {
        const string marker = "predicate=";
        var predicateStart = exception.Message.IndexOf(marker, StringComparison.Ordinal);
        if (predicateStart < 0)
            return "runtime-apphost-manifest-read";
        predicateStart += marker.Length;
        var predicateEnd = exception.Message.IndexOf(';', predicateStart);
        return exception.Message[predicateStart..(predicateEnd < 0 ? exception.Message.Length : predicateEnd)];
    }

    private static string CanonicalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return "[invalid-path]";
        }
    }

    private static string FileSha256(string path)
    {
        try
        {
            var canonical = Path.GetFullPath(path);
            return File.Exists(canonical)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonical)))
                : "[not-applicable:file-missing]";
        }
        catch
        {
            return "[not-applicable:unreadable]";
        }
    }
}

internal sealed record PhysicalWatchdogSessionBinding(
    string SessionId,
    string OneRunToken,
    string SourceFingerprint,
    string RuntimeManifestSha256,
    string LineageFingerprint)
{
    public static PhysicalWatchdogSessionBinding FromSession(PhysicalSessionArtifact session) => new(
        session.SessionId,
        session.OneRunToken,
        session.SourceFingerprint,
        session.RuntimeManifestSha256,
        PhysicalSessionStore.ImmutableLineageFingerprint(session));
}

internal sealed record PhysicalWatchdogAuthorization(
    string Token,
    string HeartbeatPath,
    PhysicalWatchdogSessionBinding Binding,
    int ParentProcessId,
    DateTimeOffset ParentStartedAtUtc,
    string ParentExecutablePath,
    string RuntimeManifestPath = "")
{
    public static PhysicalWatchdogAuthorization? FromEnvironment()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.ParentPidEnvironmentVariable), out var parentPid) ||
            !DateTimeOffset.TryParse(
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.ParentStartEnvironmentVariable),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parentStarted))
            return null;
        return new PhysicalWatchdogAuthorization(
            Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.TokenEnvironmentVariable) ?? "",
            Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.HeartbeatEnvironmentVariable) ?? "",
            new PhysicalWatchdogSessionBinding(
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.SessionIdEnvironmentVariable) ?? "",
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.OneRunTokenEnvironmentVariable) ?? "",
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.SourceFingerprintEnvironmentVariable) ?? "",
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.RuntimeManifestEnvironmentVariable) ?? "",
                Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.LineageFingerprintEnvironmentVariable) ?? ""),
            parentPid,
            parentStarted,
            Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.ParentExecutableEnvironmentVariable) ?? "",
            Environment.GetEnvironmentVariable(PhysicalWatchdogProcessGate.RuntimeManifestPathEnvironmentVariable) ?? "");
    }
}

internal sealed record PhysicalProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath);

internal interface IPhysicalProcessIdentityProvider
{
    PhysicalProcessIdentity Current();
    PhysicalProcessIdentity Get(int processId);
}

internal interface IPhysicalIdentitySnapshotReader
{
    PhysicalProcessIdentity Read(int processId);
}

internal sealed class CoherentPhysicalProcessIdentityProvider(IPhysicalIdentitySnapshotReader reader)
    : IPhysicalProcessIdentityProvider
{
    public PhysicalProcessIdentity Current() => Get(Environment.ProcessId);

    public PhysicalProcessIdentity Get(int processId)
    {
        var first = reader.Read(processId);
        var second = reader.Read(processId);
        if (first.ProcessId != processId || second.ProcessId != processId || first != second)
            throw new InvalidOperationException($"Process identity for requested PID {processId} disappeared, was reused, or drifted during coherent acquisition.");
        return first;
    }
}

internal sealed class WindowsPhysicalProcessIdentityProvider : IPhysicalProcessIdentityProvider
{
    private readonly CoherentPhysicalProcessIdentityProvider _coherent =
        new(new WindowsPhysicalIdentitySnapshotReader());

    public PhysicalProcessIdentity Current() => _coherent.Current();

    public PhysicalProcessIdentity Get(int processId) => _coherent.Get(processId);
}

internal sealed class WindowsPhysicalIdentitySnapshotReader : IPhysicalIdentitySnapshotReader
{
    // Managed .NET 10-compatible replacement for the historical dynamic-COM (SWbemLocator)
    // Win32_Process identity reads used by the child-side lineage gate. The dynamic COM path
    // deterministically threw COMException 0x80004005 (E_FAIL) on .NET Core/.NET 10 when reading
    // rows for a process other than the caller, blocking EXECUTE immediately after the watchdog
    // launched the child. This implementation uses only managed P/Invoke and is fail-closed.

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const int ProcessBasicInformation = 0;
    private static readonly DateTimeOffset FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationStruct
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr handle, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass, ref ProcessBasicInformationStruct processInformation, int processInformationLength, out int returnLength);

    public PhysicalProcessIdentity Read(int processId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Watchdog parent authentication requires Windows.");

        DateTimeOffset startedAtUtc;
        string executablePath;
        var limited = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (limited == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Process identity for PID {processId} disappeared or could not be opened (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            if (!GetProcessTimes(limited, out var creation, out _, out _, out _))
                throw new InvalidDataException($"Could not read the creation time for PID {processId} (Win32 error {Marshal.GetLastWin32Error()}).");
            if (creation.Low == 0 && creation.High == 0)
                throw new InvalidDataException($"PID {processId} reported an invalid zero creation time.");
            startedAtUtc = FileTimeEpoch.AddTicks(((long)creation.High << 32) | creation.Low);
            var imageName = new System.Text.StringBuilder(1024);
            var imageLength = imageName.Capacity;
            if (!QueryFullProcessImageNameW(limited, 0, imageName, ref imageLength) || imageLength == 0 || imageName.Length == 0)
                throw new InvalidDataException($"Could not resolve the executable path for PID {processId} (Win32 error {Marshal.GetLastWin32Error()}).");
            executablePath = imageName.ToString();
        }
        finally
        {
            CloseHandle(limited);
        }

        int parentProcessId;
        var query = OpenProcess(ProcessQueryInformation, false, processId);
        if (query == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Process identity for PID {processId} could not be opened for parent verification (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            var basic = new ProcessBasicInformationStruct();
            var status = NtQueryInformationProcess(query, ProcessBasicInformation, ref basic, Marshal.SizeOf<ProcessBasicInformationStruct>(), out _);
            if (status != 0)
                throw new InvalidDataException($"NtQueryInformationProcess failed for PID {processId} with status 0x{status:X8}.");
            parentProcessId = checked((int)basic.InheritedFromUniqueProcessId);
        }
        finally
        {
            CloseHandle(query);
        }

        return new PhysicalProcessIdentity(processId, parentProcessId, startedAtUtc, executablePath);
    }
}

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

internal static class PhysicalWatchdogHeartbeat
{
    public static Task PulseAsync(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var root = PhysicalArtifactPaths.FindRepositoryRoot();
        var authorization = PhysicalWatchdogAuthorization.FromEnvironment();
        var result = PhysicalWatchdogProcessGate.Evaluate(authorization, root);
        if (!result.IsAuthorized)
            throw new InvalidOperationException($"The dedicated physical watchdog heartbeat is absent or not owned by this repository session. {result.Diagnostic}");
        return PhysicalArtifactWriter.WriteJsonAsync(
            authorization!.HeartbeatPath,
            new PhysicalWatchdogHeartbeatState(
                kind,
                DateTimeOffset.UtcNow,
                authorization.ParentProcessId,
                authorization.ParentStartedAtUtc,
                authorization.ParentExecutablePath,
                authorization.Token,
                authorization.Binding.SessionId,
                authorization.Binding.OneRunToken,
                authorization.Binding.SourceFingerprint,
                authorization.Binding.RuntimeManifestSha256,
                authorization.Binding.LineageFingerprint,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(authorization.ParentExecutablePath)))));
    }
}

internal sealed class PhysicalDeviceTransactionProgress
{
    private readonly Func<Task> _reportAsync;

    private PhysicalDeviceTransactionProgress(Func<Task> reportAsync) => _reportAsync = reportAsync;

    private static PhysicalDeviceTransactionProgress ReadOnlyPrepare { get; } =
        new(static () => Task.CompletedTask);

    public Task ReportAsync() => _reportAsync();

    public static PhysicalDeviceTransactionProgress RequireWatchdogProtected(
        WatchdogProtectedPhysicalPhase phase,
        PhysicalWatchdogAuthorization? authorization,
        string repositoryRoot,
        IPhysicalProcessIdentityProvider? processIdentityProvider = null)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        var result = PhysicalWatchdogProcessGate.Evaluate(authorization, repositoryRoot, processIdentityProvider);
        if (!result.IsAuthorized)
            throw new InvalidOperationException($"{phase} requires an authenticated direct-parent physical watchdog before HID access. {result.Diagnostic}");
        return new PhysicalDeviceTransactionProgress(
            () => PhysicalWatchdogHeartbeat.PulseAsync("DeviceTransactionCompleted"));
    }

    internal static PhysicalDeviceTransactionProgress ForReadOnlyPrepare() => ReadOnlyPrepare;
}

internal enum WatchdogProtectedPhysicalPhase
{
    Execute,
    Recovery
}

internal static class PhysicalDawnPro2DeviceOpener
{
    public static Task<DawnPro2Device> OpenReadOnlyPrepareWithRetriesAsync(
        DawnPro2HidIdentity identity,
        List<DawnPro2HidReadFrame> frames,
        int attempts,
        Func<DawnPro2HidIdentity, IDawnPro2HidTransport>? transportOpener = null,
        IDeviceDelay? delay = null) =>
        OpenWithRetriesAsync(
            identity,
            frames,
            attempts,
            PhysicalDeviceTransactionProgress.ForReadOnlyPrepare(),
            transportOpener,
            delay);

    public static Task<DawnPro2Device> OpenWatchdogProtectedWithRetriesAsync(
        DawnPro2HidIdentity identity,
        List<DawnPro2HidReadFrame> frames,
        int attempts,
        WatchdogProtectedPhysicalPhase phase,
        PhysicalWatchdogAuthorization? authorization,
        string repositoryRoot,
        Func<DawnPro2HidIdentity, IDawnPro2HidTransport>? transportOpener = null,
        IDeviceDelay? delay = null,
        IPhysicalProcessIdentityProvider? processIdentityProvider = null) =>
        OpenWithRetriesAsync(
            identity,
            frames,
            attempts,
            PhysicalDeviceTransactionProgress.RequireWatchdogProtected(
                phase,
                authorization,
                repositoryRoot,
                processIdentityProvider),
            transportOpener,
            delay);

    private static async Task<DawnPro2Device> OpenWithRetriesAsync(
        DawnPro2HidIdentity identity,
        List<DawnPro2HidReadFrame> frames,
        int attempts,
        PhysicalDeviceTransactionProgress transactionProgress,
        Func<DawnPro2HidIdentity, IDawnPro2HidTransport>? transportOpener,
        IDeviceDelay? delay)
    {
        ArgumentNullException.ThrowIfNull(transactionProgress);
        if (attempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        transportOpener ??= candidate => HidSharpDawnPro2Transport.OpenByIdentity(candidate);
        var failures = new List<Exception>();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return new DawnPro2Device(
                    transportOpener(identity),
                    delay,
                    frames.Add,
                    transactionProgress.ReportAsync);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }
        throw new AggregateException($"Could not open the exact pinned DAWN PRO2 identity after {attempts} attempts.", failures);
    }
}

internal static class PhysicalExecutionGate
{
    public static bool IsAuthorized(string? runValue, string? providedToken, string? persistedToken) =>
        PhysicalTestGate.IsOptedIn(runValue) &&
        !string.IsNullOrEmpty(persistedToken) &&
        string.Equals(providedToken, persistedToken, StringComparison.Ordinal);
}

internal static class PhysicalRecoveryGate
{
    public static string Validate(string? optInValue, string? snapshotPath, string hardwareSnapshotsRoot)
    {
        if (!PhysicalTestGate.IsOptedIn(optInValue))
            throw new InvalidOperationException($"Recovery requires {PhysicalTestGate.RecoveryEnvironmentVariable}=1 exactly.");
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new InvalidOperationException($"Recovery requires {PhysicalTestGate.RecoverySnapshotEnvironmentVariable} to name an existing session snapshot.");
        var root = Path.GetFullPath(hardwareSnapshotsRoot);
        var candidate = Path.GetFullPath(snapshotPath);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery snapshot must remain inside the hardware-snapshots directory.");
        if (!string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).StartsWith("dawn-pro2-session-", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery snapshot must be a dawn-pro2-session-*.json artifact.");
        if (!File.Exists(candidate) && !File.Exists(PhysicalSessionStore.RecoveryCopyPath(candidate)))
            throw new InvalidOperationException("Recovery snapshot and its deterministic recovery copy do not exist.");
        return candidate;
    }
}

internal static class PhysicalTemporaryGain
{
    private const short StepRaw = 64;
    private const short MinimumRaw = -18 * 256;
    private const short MaximumRaw = 12 * 256;

    public static bool TryChoose(double original, out double temporary)
    {
        temporary = default;
        if (!double.IsFinite(original))
            return false;
        var raw = ToRawQ88(original);
        if (Math.Abs(original - raw / 256.0) > 1e-12 || !TryChooseRaw(raw, out var temporaryRaw))
            return false;
        temporary = temporaryRaw / 256.0;
        return true;
    }

    public static bool TryChooseRaw(short original, out short temporary)
    {
        temporary = default;
        if (original is < MinimumRaw or > MaximumRaw)
            return false;
        if (original <= MaximumRaw - StepRaw)
            temporary = (short)(original + StepRaw);
        else if (original >= MinimumRaw + StepRaw)
            temporary = (short)(original - StepRaw);
        else
            return false;
        return temporary != original;
    }

    public static double Quantize(double value)
    {
        var encoded = DawnPro2Protocol.EncodeFixedPoint(value);
        return DawnPro2Protocol.DecodeFixedPoint(encoded[0], encoded[1]);
    }

    public static short ToRawQ88(double value)
    {
        var encoded = DawnPro2Protocol.EncodeFixedPoint(value);
        return (short)(encoded[0] | (encoded[1] << 8));
    }
}

internal sealed record HardwareBandSnapshot(int Index, byte[] RawPayload)
{
    public RawPeqBandState ToRawState() => DawnPro2Protocol.ParseRawBandPayload(Index, RawPayload);
    public PeqBand ToPeqBand() => ToRawState().ToPeqBand();
    public int Frequency => ToRawState().Frequency;
    public double Q => ToRawState().QRaw / 256.0;
    public double Gain => ToRawState().GainRaw / 256.0;
    public short QRaw => ToRawState().QRaw;
    public short GainRaw => ToRawState().GainRaw;
    public PeqFilterType FilterType => ToPeqBand().FilterType;
    public bool Enabled => ToPeqBand().Enabled;
    public byte? RawFilterCode => ToPeqBand().RawFilterCode;
    public IReadOnlyList<byte> CoefficientBytes => ToRawState().CoefficientBytes;

    public static HardwareBandSnapshot FromRawState(RawPeqBandState state) =>
        new(state.Index, state.NormalizedPayload.ToArray());

    public static HardwareBandSnapshot FromPeqBand(PeqBand band)
    {
        var payload = DawnPro2Protocol.BuildWriteBandPayload(band.Index, band);
        return FromRawState(DawnPro2Protocol.ParseRawBandPayload(band.Index, payload));
    }
}

internal sealed record HardwareSnapshot(
    DateTimeOffset CapturedAtUtc,
    DawnPro2HidIdentity Identity,
    string Firmware,
    int ActiveEq,
    short PreGainRaw,
    short GlobalGainRaw,
    IReadOnlyList<HardwareBandSnapshot> Bands)
{
    public double PreGain => PreGainRaw / 256.0;
    public double GlobalGain => GlobalGainRaw / 256.0;
}

internal static class HardwareSnapshotReader
{
    public static Task<HardwareSnapshot> ReadAsync(DawnPro2Device device) =>
        ReadAsync(device, new DawnPro2HidIdentity("untrusted-open-first", "untrusted-open-first"), CancellationToken.None);

    public static async Task<HardwareSnapshot> ReadAsync(
        DawnPro2Device device,
        DawnPro2HidIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var firmware = await device.ReadFirmwareVersionAsync(cancellationToken).ConfigureAwait(false);
        var activeEq = await device.ReadActiveEqAsync(cancellationToken).ConfigureAwait(false);
        var preGain = PhysicalTemporaryGain.ToRawQ88(await device.ReadPreGainAsync(cancellationToken).ConfigureAwait(false));
        var globalGain = PhysicalTemporaryGain.ToRawQ88(await device.ReadGlobalGainAsync(cancellationToken).ConfigureAwait(false));
        var bands = (await device.ReadAllRawBandsAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(band => band.Index)
            .Select(HardwareBandSnapshot.FromRawState)
            .ToArray();
        return new HardwareSnapshot(DateTimeOffset.UtcNow, identity, firmware, activeEq, preGain, globalGain, bands);
    }

    public static async Task<HardwareSnapshot> ReadConsistentAsync(
        DawnPro2Device device,
        DawnPro2HidIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var first = await ReadAsync(device, identity, cancellationToken).ConfigureAwait(false);
        var second = await ReadAsync(device, identity, cancellationToken).ConfigureAwait(false);
        var differences = PhysicalSnapshotComparer.Differences(first, second);
        if (differences.Count != 0)
            throw new InvalidOperationException($"Two complete preflight snapshots were inconsistent: {string.Join("; ", differences)}");
        return second;
    }
}

internal sealed record PhysicalPreparePlan(HardwareSnapshot Original, PhysicalTransitionPlan Plan);

internal static class PhysicalPreparePlanner
{
    public static async Task<PhysicalPreparePlan> ReadAndPlanAsync(
        DawnPro2Device device,
        DawnPro2HidIdentity identity,
        Moondrop.Core.Eq.EqPreset? profile = null,
        CancellationToken cancellationToken = default)
    {
        var original = await HardwareSnapshotReader.ReadConsistentAsync(device, identity, cancellationToken).ConfigureAwait(false);
        var problems = PhysicalSnapshotValidator.RestorationProblems(original);
        if (problems.Count != 0)
            throw new InvalidOperationException($"two-pass original preflight snapshot is not completely restorable: {string.Join("; ", problems)}");
        return new PhysicalPreparePlan(
            original,
            profile is null
                ? PhysicalTransitionPlanner.Create(original)
                : PhysicalTransitionPlanner.CreateProfilePlan(original, profile));
    }
}

internal static class PhysicalSnapshotValidator
{
    public const string SupportedFirmware = "1.5";

    public static IReadOnlyList<string> RestorationProblems(HardwareSnapshot snapshot)
    {
        var problems = new List<string>();
        try
        {
            snapshot.Identity.Validate();
        }
        catch (Exception ex)
        {
            problems.Add($"physical HID identity is not restorable: {ex.Message}");
        }
        if (!string.Equals(snapshot.Firmware, SupportedFirmware, StringComparison.Ordinal))
            problems.Add($"firmware must be exactly {SupportedFirmware}; read '{snapshot.Firmware}'");
        // Two actual read-only PREPARE preflights on this exact device/firmware returned raw 9.
        // That firmware readback is not the slot-7 PEQ registry selector and proves no default/custom-mode toggle.
        var isObservedFirmware15RawActiveEq =
            snapshot.ActiveEq == 9 &&
            string.Equals(snapshot.Firmware, SupportedFirmware, StringComparison.Ordinal) &&
            snapshot.Identity.DeviceKind == Moondrop.Core.Devices.DeviceKind.DawnPro2 &&
            snapshot.Identity.VendorId == DawnPro2Protocol.VendorId &&
            snapshot.Identity.ProductId == DawnPro2Protocol.ProductId;
        if (snapshot.ActiveEq != DawnPro2Protocol.PeqIndex && !isObservedFirmware15RawActiveEq)
            problems.Add($"active EQ must be PEQ profile {DawnPro2Protocol.PeqIndex} or the narrowly observed DAWN PRO2 firmware 1.5 raw value 9; read {snapshot.ActiveEq}");
        ValidateRawGain(problems, "pre gain", snapshot.PreGainRaw);
        ValidateRawGain(problems, "global gain", snapshot.GlobalGainRaw);

        if (snapshot.Bands.Count != 8)
            problems.Add($"expected 8 bands, read {snapshot.Bands.Count}");
        var indexes = new HashSet<int>();
        foreach (var band in snapshot.Bands)
        {
            if (band.Index is < 0 or > 7 || !indexes.Add(band.Index))
                problems.Add($"band index {band.Index} is missing, duplicated, or outside 0..7");
            try
            {
                DawnPro2Protocol.ValidateRawBandState(band.ToRawState());
            }
            catch (Exception ex)
            {
                problems.Add($"band {band.Index} raw state is not restorable: {ex.Message}");
            }
        }
        if (snapshot.Bands.Count == 8 && !indexes.SetEquals(Enumerable.Range(0, 8)))
            problems.Add("band indexes are not the complete set 0..7");
        return problems;
    }

    private static void ValidateRawGain(List<string> problems, string field, short value)
    {
        if (value is < -18 * 256 or > 12 * 256)
            problems.Add($"{field} raw Q8.8 value {value} is outside -18..12 dB");
    }
}

internal static class PhysicalTemporarySnapshot
{
    public static HardwareSnapshot Create(HardwareSnapshot original)
        => PhysicalTransitionPlanner.Create(original).Individual;
}

internal sealed record PhysicalTransitionPlan(
    HardwareSnapshot Baseline,
    HardwareSnapshot Individual,
    HardwareBandSnapshot IndividualBand,
    HardwareSnapshot Bulk,
    IReadOnlyList<string> BulkChanges,
    HardwareSnapshot? Profile = null);

internal static class PhysicalTransitionPlanner
{
    public static PhysicalTransitionPlan Create(HardwareSnapshot original)
    {
        var restorationProblems = PhysicalSnapshotValidator.RestorationProblems(original);
        if (restorationProblems.Count != 0)
            throw new InvalidOperationException($"Original snapshot cannot be safely transitioned: {string.Join("; ", restorationProblems)}");
        var band = original.Bands.FirstOrDefault(candidate =>
                       candidate.Enabled && candidate.FilterType is PeqFilterType.Peaking or PeqFilterType.LowShelf2 or PeqFilterType.HighShelf2)
                   ?? throw new InvalidOperationException("Individual band testing requires an enabled Peaking, LowShelf2, or HighShelf2 band.");
        if (!PhysicalTemporaryGain.TryChooseRaw(band.GainRaw, out var bandGain))
            throw new InvalidOperationException($"A safe reversible 0.25 dB delta could not be selected for band {band.Index}.");

        var changedCoreBand = band.ToPeqBand() with { Gain = bandGain / 256.0 };
        var changedBand = HardwareBandSnapshot.FromRawState(
            DawnPro2Protocol.CreateRawBandStateFromTemplate(band.ToRawState(), changedCoreBand));
        RequireCoefficientTransition(band, changedBand, "individual");

        var baseline = original with
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Bands = original.Bands.Select(CloneBand).ToArray()
        };
        var individual = baseline with
        {
            Bands = baseline.Bands.Select(candidate => candidate.Index == band.Index ? changedBand : CloneBand(candidate)).ToArray()
        };

        var bulk = baseline with { Bands = baseline.Bands.Select(CloneBand).ToArray() };
        return new PhysicalTransitionPlan(baseline, individual, changedBand, bulk, []);
    }

    public static PhysicalTransitionPlan CreateProfilePlan(HardwareSnapshot original, Moondrop.Core.Eq.EqPreset profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var plan = Create(original);
        var target = plan.Baseline with
        {
            PreGainRaw = profile.Preamp is double preamp
                ? PhysicalTemporaryGain.ToRawQ88(preamp)
                : plan.Baseline.PreGainRaw,
            Bands = OverlayProfileBands(plan.Baseline, profile)
        };
        var problems = PhysicalSnapshotValidator.RestorationProblems(target);
        if (problems.Count != 0)
            throw new InvalidDataException($"User EQ profile target is not restorable: {string.Join("; ", problems)}");
        return plan with { Profile = target };
    }

    private static IReadOnlyList<HardwareBandSnapshot> OverlayProfileBands(HardwareSnapshot baseline, Moondrop.Core.Eq.EqPreset profile)
    {
        var bands = baseline.Bands.Select(CloneBand).ToArray();
        foreach (var band in profile.Bands)
        {
            if (band.Index is < 0 or > 7)
                throw new InvalidDataException($"User EQ profile references unsupported band index {band.Index}.");
            var template = baseline.Bands[band.Index].ToRawState();
            var state = Moondrop.Core.Protocol.DawnPro2Protocol.CreateRawBandStateFromTemplate(template, band);
            bands[band.Index] = HardwareBandSnapshot.FromRawState(state);
        }
        return bands;
    }

    private static void RequireCoefficientTransition(HardwareBandSnapshot original, HardwareBandSnapshot changed, string phase)
    {
        if (original.CoefficientBytes.SequenceEqual(changed.CoefficientBytes))
            throw new InvalidOperationException($"Band {original.Index} {phase} plan does not change raw coefficient bytes.");
    }

    private static HardwareBandSnapshot CloneBand(HardwareBandSnapshot band) => new(band.Index, band.RawPayload.ToArray());
}

internal static class PhysicalSnapshotComparer
{
    public static IReadOnlyList<string> Differences(HardwareSnapshot expected, HardwareSnapshot actual)
    {
        var differences = new List<string>();
        Exact(differences, "HID device path", expected.Identity.DevicePath, actual.Identity.DevicePath);
        Exact(differences, "HID serial", expected.Identity.SerialNumber, actual.Identity.SerialNumber);
        Exact(differences, "firmware", expected.Firmware, actual.Firmware);
        Exact(differences, "active EQ", expected.ActiveEq, actual.ActiveEq);
        Exact(differences, "pre gain raw", expected.PreGainRaw, actual.PreGainRaw);
        Exact(differences, "global gain raw", expected.GlobalGainRaw, actual.GlobalGainRaw);

        if (expected.Bands.Count != actual.Bands.Count)
        {
            differences.Add($"band count: expected {expected.Bands.Count}, actual {actual.Bands.Count}");
            return differences;
        }

        for (var position = 0; position < expected.Bands.Count; position++)
        {
            var expectedBand = expected.Bands[position];
            var actualBand = actual.Bands[position];
            var prefix = $"band position {position}";
            Exact(differences, $"{prefix} index", expectedBand.Index, actualBand.Index);
            if (expectedBand.RawPayload.Length != actualBand.RawPayload.Length)
            {
                differences.Add($"{prefix} payload length: expected {expectedBand.RawPayload.Length}, actual {actualBand.RawPayload.Length}");
                continue;
            }
            for (var byteIndex = 0; byteIndex < expectedBand.RawPayload.Length; byteIndex++)
            {
                if (expectedBand.RawPayload[byteIndex] != actualBand.RawPayload[byteIndex])
                    differences.Add($"{prefix} payload byte {byteIndex}: expected {expectedBand.RawPayload[byteIndex]}, actual {actualBand.RawPayload[byteIndex]}");
            }
        }

        return differences;
    }

    private static void Exact<T>(List<string> differences, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            differences.Add($"{field}: expected {expected}, actual {actual}");
    }

}

internal static class PhysicalRecoveryCompatibility
{
    public static void RequireReachable(PhysicalSessionArtifact session, HardwareSnapshot current)
    {
        PhysicalSessionStore.Validate(session);
        var problems = PhysicalSnapshotValidator.RestorationProblems(current);
        if (problems.Count != 0)
            throw new InvalidOperationException($"Recovery current snapshot is unsafe: {string.Join("; ", problems)}");

        var durablePhase = session.Phase == PhysicalSessionPhase.Failed
            ? session.LastSafePhase ?? throw new InvalidDataException("A Failed physical session has no LastSafePhase.")
            : session.Phase;
        if (ReachableSnapshots(session, durablePhase).Any(candidate => PhysicalSnapshotComparer.Differences(candidate, current).Count == 0))
            return;

        throw new InvalidOperationException(
            $"Recovery current snapshot is not reachable for durable phase {durablePhase}; refusing every HID write.");
    }

    internal static IReadOnlyList<HardwareSnapshot> ReachableSnapshots(
        PhysicalSessionArtifact session,
        PhysicalSessionPhase durablePhase)
    {
        var original = session.Original;
        var plan = session.Plan;
        var executionStates = ExecutionStates(original, plan);
        var restorationStates = executionStates;

        return durablePhase switch
        {
            PhysicalSessionPhase.Prepared => [Clone(original)],
            PhysicalSessionPhase.TemporaryWritesStarting => executionStates,
            PhysicalSessionPhase.TemporaryWritesVerified => [Clone(plan.Individual)],
            PhysicalSessionPhase.TemporaryFlashSaveStarting => restorationStates,
            PhysicalSessionPhase.AwaitingTemporaryPhysicalCycle => restorationStates,
            PhysicalSessionPhase.TemporaryPersistenceVerified => restorationStates,
            PhysicalSessionPhase.RestorationStarting => restorationStates,
            PhysicalSessionPhase.RestorationWritesVerified => [Clone(original)],
            PhysicalSessionPhase.RestorationFlashSaveStarting => [Clone(original)],
            PhysicalSessionPhase.AwaitingRestorationPhysicalCycle => [Clone(original)],
            PhysicalSessionPhase.RestorationVerified => [Clone(original)],
            PhysicalSessionPhase.Completed => [Clone(original)],
            PhysicalSessionPhase.Failed => throw new InvalidDataException("Failed compatibility requires a non-Failed LastSafePhase."),
            _ => throw new InvalidDataException($"Unknown durable recovery phase {durablePhase}.")
        };
    }

    private static IReadOnlyList<HardwareSnapshot> ExecutionStates(
        HardwareSnapshot original,
        PhysicalTransitionPlan plan)
    {
        var executionStates = new List<HardwareSnapshot>();
        executionStates.Add(Clone(original));
        executionStates.Add(Clone(plan.Individual));

        return executionStates;
    }

    private static HardwareSnapshot WithBand(HardwareSnapshot snapshot, HardwareBandSnapshot replacement) => snapshot with
    {
        Bands = snapshot.Bands
            .Select(band => band.Index == replacement.Index ? Clone(replacement) : Clone(band))
            .OrderBy(band => band.Index)
            .ToArray()
    };

    private static HardwareSnapshot Clone(HardwareSnapshot snapshot) => snapshot with
    {
        Bands = snapshot.Bands.Select(Clone).ToArray()
    };

    private static HardwareBandSnapshot Clone(HardwareBandSnapshot band) => new(band.Index, band.RawPayload.ToArray());
}

internal static class PhysicalAssertions
{
    public static void SnapshotEquals(HardwareSnapshot expected, HardwareSnapshot actual, string context)
    {
        var differences = PhysicalSnapshotComparer.Differences(expected, actual);
        if (differences.Count != 0)
            Assert.Fail($"{context} mismatch:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
    }

    public static void BandEquals(HardwareBandSnapshot expected, HardwareBandSnapshot actual, string context)
    {
        var identity = new DawnPro2HidIdentity("assertion", "assertion");
        var expectedSnapshot = new HardwareSnapshot(DateTimeOffset.MinValue, identity, "", 0, 0, 0, [expected]);
        var actualSnapshot = new HardwareSnapshot(DateTimeOffset.MinValue, identity, "", 0, 0, 0, [actual]);
        SnapshotEquals(expectedSnapshot, actualSnapshot, context);
    }

    public static void Q88Equals(double expected, double actual, string context)
    {
        if (!double.IsFinite(actual) || PhysicalTemporaryGain.ToRawQ88(expected) != PhysicalTemporaryGain.ToRawQ88(actual))
            Assert.Fail($"{context}: expected {expected:R}, actual {actual:R}");
    }
}

internal sealed class PhysicalRunLock : IDisposable
{
    private const string LockFileName = "dawn-pro2-vid35d8-pid011d.lock";
    private FileStream? _stream;
    private TrustedPhysicalPath.StablePathLease? _pathLease;

    private PhysicalRunLock(FileStream stream, TrustedPhysicalPath.StablePathLease pathLease)
    {
        _stream = stream;
        _pathLease = pathLease;
    }

    public static bool TryAcquireDefault(out PhysicalRunLock? runLock)
    {
        var commonData = ResolveCommonApplicationDataPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return TryAcquire(Path.Combine(commonData, "Moondrop", "PhysicalHarness", LockFileName), out runLock);
    }

    public static string ResolveCommonApplicationDataPath(string? commonApplicationData, string? windowsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(commonApplicationData))
            return Path.GetFullPath(commonApplicationData);
        // The physical runner child runs under the watchdog cleared environment (SystemRoot,
        // WINDIR, TEMP, TMP only). Under .NET 10 GetFolderPath(CommonApplicationData) is then
        // empty, so the canonical machine-wide ProgramData directory is derived from the
        // validated Windows directory (parent-of-Windows\ProgramData).
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(windowsDirectory));
            if (parent is not null)
                return Path.GetFullPath(Path.Combine(parent, "ProgramData"));
        }
        throw new InvalidOperationException("The common application-data directory is unavailable; physical testing is locked out.");
    }

    public static bool TryAcquire(string path, out PhysicalRunLock? runLock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        runLock = null;
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        TrustedPhysicalPath.CreateDirectoryNoReparse(directory, "Physical machine-wide lock directory");
        var pathLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(directory, directory, "Physical machine-wide lock directory");
        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            pathLease.Verify();
            runLock = new PhysicalRunLock(stream, pathLease);
            return true;
        }
        catch (IOException)
        {
            pathLease.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
        var pathLease = Interlocked.Exchange(ref _pathLease, null);
        pathLease?.Verify();
        pathLease?.Dispose();
    }
}

internal sealed record PhysicalNativeProcess(int ProcessId, string Name);

internal interface IPhysicalWmiProcess : IDisposable
{
    object? ReadProperty(string propertyName);
}

internal interface IPhysicalProcessQuery
{
    IReadOnlyList<PhysicalNativeProcess> GetProcessesByName(string processName);
    IReadOnlyList<IPhysicalWmiProcess> QueryWmi(string query);
    bool IsRunning(int processId);
}

internal static class PhysicalProcessGuard
{
    private const string DawnProExecutableQuery =
        "SELECT ProcessId, Name FROM Win32_Process WHERE Name LIKE '%DawnPro%'";
    private const string PythonCommandLineQuery =
        "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'python.exe' OR Name = 'pythonw.exe'";

    public static IReadOnlyList<string> FindConflictingApps()
    {
        if (!OperatingSystem.IsWindows())
            return ["physical DAWN PRO2 integration tests require Windows"];

        try
        {
            return FindConflictingApps(new WindowsPhysicalProcessQuery());
        }
        catch (PhysicalProcessInspectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not verify that no Moondrop application is using the device; aborting before HID access.", ex);
        }
    }

    internal static IReadOnlyList<string> FindConflictingApps(IPhysicalProcessQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var currentPid = Environment.ProcessId;
        IReadOnlyList<PhysicalNativeProcess> compiledCandidates;
        try
        {
            compiledCandidates = query.GetProcessesByName("Moondrop.Wpf");
        }
        catch (Exception ex)
        {
            throw new PhysicalProcessInspectionException(
                "Could not complete the exact-name process lookup for relevant Moondrop.Wpf.exe candidates; aborting before HID access.",
                ex);
        }

        var conflicts = compiledCandidates
            .Where(process => process.ProcessId != currentPid)
            .Select(process => $"{process.Name} (PID {process.ProcessId})")
            .ToList();
        AddDawnProConflicts(
            query,
            QueryRelevantWmi(query, DawnProExecutableQuery, "DawnPro executable"),
            currentPid,
            conflicts);
        AddPythonConflicts(
            query,
            QueryRelevantWmi(query, PythonCommandLineQuery, "Python"),
            currentPid,
            conflicts);
        return conflicts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<IPhysicalWmiProcess> QueryRelevantWmi(
        IPhysicalProcessQuery query,
        string wmiQuery,
        string candidateScope)
    {
        try
        {
            return query.QueryWmi(wmiQuery);
        }
        catch (Exception ex)
        {
            throw new PhysicalProcessInspectionException(
                $"Could not complete the narrow {candidateScope} candidate query; aborting before HID access.",
                ex);
        }
    }

    private static void AddPythonConflicts(
        IPhysicalProcessQuery query,
        IReadOnlyList<IPhysicalWmiProcess> rows,
        int currentPid,
        ICollection<string> conflicts)
    {
        try
        {
            foreach (var row in rows)
            {
                var processId = ReadProcessId(row, "Python");
                if (processId == currentPid)
                    continue;
                if (!TryReadRequiredString(query, row, processId, "Python", "Name", out var name) ||
                    !TryReadRequiredString(query, row, processId, "Python", "CommandLine", out var commandLine))
                    continue;
                if (IsLegacyPythonMoondropApp(commandLine))
                    conflicts.Add($"{name} (PID {processId})");
            }
        }
        finally
        {
            DisposeRows(rows);
        }
    }

    private static bool IsLegacyPythonMoondropApp(string commandLine) =>
        commandLine.Contains("main.py", StringComparison.OrdinalIgnoreCase) &&
        (commandLine.Contains("moondrop gui", StringComparison.OrdinalIgnoreCase) ||
         commandLine.Contains("DawnPro-GUI", StringComparison.OrdinalIgnoreCase));

    private static void AddDawnProConflicts(
        IPhysicalProcessQuery query,
        IReadOnlyList<IPhysicalWmiProcess> rows,
        int currentPid,
        ICollection<string> conflicts)
    {
        try
        {
            foreach (var row in rows)
            {
                var processId = ReadProcessId(row, "DawnPro executable");
                if (processId == currentPid)
                    continue;
                if (TryReadRequiredString(query, row, processId, "DawnPro executable", "Name", out var name))
                    conflicts.Add($"{name} (PID {processId})");
            }
        }
        finally
        {
            DisposeRows(rows);
        }
    }

    private static int ReadProcessId(IPhysicalWmiProcess row, string candidateScope)
    {
        object? value;
        try
        {
            value = row.ReadProperty("ProcessId");
        }
        catch (Exception ex)
        {
            throw new PhysicalProcessInspectionException(
                $"Could not read ProcessId for a relevant {candidateScope} candidate; aborting before HID access.",
                ex);
        }

        try
        {
            var processId = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            if (processId <= 0)
                throw new InvalidDataException($"ProcessId value '{value ?? "<null>"}' is not a positive process ID.");
            return processId;
        }
        catch (Exception ex) when (ex is not PhysicalProcessInspectionException)
        {
            throw new PhysicalProcessInspectionException(
                $"The ProcessId for a relevant {candidateScope} candidate was unavailable or invalid; aborting before HID access.",
                ex);
        }
    }

    private static bool TryReadRequiredString(
        IPhysicalProcessQuery query,
        IPhysicalWmiProcess row,
        int processId,
        string candidateScope,
        string propertyName,
        out string value)
    {
        Exception? failure = null;
        string? converted = null;
        try
        {
            var raw = row.ReadProperty(propertyName);
            converted = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(converted))
                failure = new InvalidDataException($"{propertyName} was null or empty.");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
        {
            value = converted!;
            return true;
        }

        bool isRunning;
        try
        {
            isRunning = query.IsRunning(processId);
        }
        catch (Exception exitCheckError)
        {
            throw new PhysicalProcessInspectionException(
                $"Could not determine whether relevant {candidateScope} candidate PID {processId} exited after {propertyName} became inaccessible; aborting before HID access.",
                new AggregateException(failure, exitCheckError));
        }

        if (!isRunning)
        {
            value = "";
            return false;
        }

        throw new PhysicalProcessInspectionException(
            $"Could not read {propertyName} for relevant {candidateScope} candidate PID {processId}; aborting before HID access.",
            failure);
    }

    private static void DisposeRows(IEnumerable<IPhysicalWmiProcess> rows)
    {
        foreach (var row in rows)
            row.Dispose();
    }
}

internal sealed class PhysicalProcessInspectionException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

internal sealed class WindowsPhysicalProcessQuery : IPhysicalProcessQuery
{
    public IReadOnlyList<PhysicalNativeProcess> GetProcessesByName(string processName)
    {
        var matches = new List<PhysicalNativeProcess>();
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                        matches.Add(new PhysicalNativeProcess(process.Id, $"{processName}.exe"));
                }
                catch (InvalidOperationException)
                {
                    // The exact-name candidate exited after the native snapshot.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
        return matches;
    }

    public IReadOnlyList<IPhysicalWmiProcess> QueryWmi(string query)
    {
        // Managed .NET 10-compatible replacement for the historical dynamic-COM (SWbemLocator)
        // Win32_Process query used by the physical conflict inspection. The dynamic COM path
        // threw COMException under .NET 10 when reading row properties, so property reads are now
        // served from a captured managed snapshot. Only the two documented narrow query shapes
        // (ProcessId[,CommandLine]+Name) are accepted; anything else fails closed.
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var shape = ManagedProcessQueryShape.TryParse(query)
                    ?? throw new InvalidOperationException("The physical preflight only supports its two documented narrow Win32_Process query shapes; refusing an unrecognized query.");
        var rows = new List<IPhysicalWmiProcess>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue; // The candidate exited during enumeration; skipped like WMI per-row behavior.
                }
                if (!shape.Matches(processName))
                    continue;
                string? commandLine = null;
                if (shape.NeedsCommandLine)
                {
                    try
                    {
                        commandLine = new WindowsObservedPhysicalProcessSnapshotReader().Read(process.Id).CommandLine;
                    }
                    catch
                    {
                        commandLine = null; // TryReadRequiredString applies the running/completed oracle fallback.
                    }
                }
                rows.Add(new ManagedProcessWmiProcess(process.Id, $"{processName}.exe", commandLine));
            }
            return rows;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class ManagedProcessQueryShape
{
    public bool NeedsCommandLine { get; init; }
    public bool Like { get; init; }
    public string[] NameFilters { get; init; } = [];

    public bool Matches(string processName)
    {
        foreach (var filter in NameFilters)
        {
            var normalized = filter.Trim();
            if (Like)
            {
                if (processName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                var exeStripped = normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? normalized[..^4]
                    : normalized;
                if (string.Equals(processName, exeStripped, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static ManagedProcessQueryShape? TryParse(string query)
    {
        var selectIdx = query.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        var fromIdx = query.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        var whereIdx = query.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        if (selectIdx < 0 || fromIdx < 0 || whereIdx < 0 || selectIdx >= fromIdx || fromIdx >= whereIdx)
            return null;
        var selectPart = query[(selectIdx + "SELECT".Length)..fromIdx];
        var properties = selectPart.Split(',')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (!properties.Contains("ProcessId", StringComparer.OrdinalIgnoreCase) ||
            !properties.Contains("Name", StringComparer.OrdinalIgnoreCase))
            return null;
        var needsCommandLine = properties.Contains("CommandLine", StringComparer.OrdinalIgnoreCase);
        var wherePart = query[(whereIdx + "WHERE".Length)..].Trim();

        var filters = new List<string>();
        var like = wherePart.Contains("LIKE", StringComparison.OrdinalIgnoreCase);
        if (like)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                wherePart,
                "Name\\s+LIKE\\s+'%(.*?)%'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;
            filters.Add(match.Groups[1].Value);
        }
        else
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                wherePart,
                "Name\\s*=\\s*'([^']*)'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count == 0)
                return null;
            foreach (System.Text.RegularExpressions.Match match in matches)
                filters.Add(match.Groups[1].Value);
        }
        if (filters.Count == 0)
            return null;
        return new ManagedProcessQueryShape
        {
            NeedsCommandLine = needsCommandLine,
            Like = like,
            NameFilters = filters.ToArray()
        };
    }
}

internal sealed class ManagedProcessWmiProcess(int processId, string name, string? commandLine) : IPhysicalWmiProcess
{
    public object? ReadProperty(string propertyName) => propertyName switch
    {
        "ProcessId" => processId,
        "Name" => name,
        "CommandLine" => commandLine,
        _ => throw new InvalidOperationException($"The managed process query does not expose property '{propertyName}'.")
    };

    public void Dispose()
    {
    }
}

internal sealed record PhysicalPresenceSample(
    bool PhysicalPnpDevicePresent,
    IReadOnlyList<DawnPro2HidIdentity> HidIdentities);

internal interface IPhysicalPresenceProbe
{
    Task<PhysicalPresenceSample> SampleAsync(DawnPro2HidIdentity identity, CancellationToken cancellationToken);
}

internal sealed class WindowsPhysicalPresenceProbe(
    IDawnPro2HidDeviceCatalog? catalog = null) : IPhysicalPresenceProbe
{
    private const int CrSuccess = 0;
    private const int CrNoSuchDevnode = 13;
    private readonly IDawnPro2HidDeviceCatalog _catalog = catalog ?? new HidSharpDawnPro2DeviceCatalog();

    public Task<PhysicalPresenceSample> SampleAsync(DawnPro2HidIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical USB cycle detection requires Windows Configuration Manager.");
        identity.Validate();
        var result = CM_Locate_DevNodeW(out _, identity.PhysicalDeviceInstanceId, 0);
        var present = result switch
        {
            CrSuccess => true,
            CrNoSuchDevnode => false,
            _ => throw new InvalidOperationException($"Could not query physical PnP presence for {identity.PhysicalDeviceInstanceId}; Configuration Manager result {result}.")
        };
        return Task.FromResult(new PhysicalPresenceSample(present, _catalog.Enumerate()));
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint deviceInstance, string deviceInstanceId, uint flags);
}

internal static class PhysicalUsbCycleMonitor
{
    public static async Task WaitForPhysicalCycleAsync(
        DawnPro2HidIdentity identity,
        TimeSpan timeout,
        TimeSpan pollInterval,
        IPhysicalPresenceProbe probe,
        CancellationToken cancellationToken)
    {
        identity.Validate();
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (pollInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var deadline = Stopwatch.StartNew();
        var sawPhysicalAbsence = false;
        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await probe.SampleAsync(identity, cancellationToken).ConfigureAwait(false);
            if (!sawPhysicalAbsence)
            {
                if (!sample.PhysicalPnpDevicePresent)
                {
                    if (sample.HidIdentities.Count != 0)
                        throw new InvalidOperationException("PnP reported physical absence while DAWN PRO2 HID interfaces remained; aborting inconsistent cycle detection.");
                    sawPhysicalAbsence = true;
                }
            }
            else if (sample.PhysicalPnpDevicePresent)
            {
                var sameSerial = sample.HidIdentities
                    .Where(candidate => string.Equals(candidate.SerialNumber, identity.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sameSerial.Any(candidate => !string.Equals(candidate.DevicePath, identity.DevicePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The pinned DAWN PRO2 serial reappeared with a changed HID identity.");
                var exact = sameSerial.Count(candidate => string.Equals(candidate.DevicePath, identity.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (exact > 1)
                    throw new InvalidOperationException("The pinned DAWN PRO2 HID identity reappeared ambiguously.");
                if (exact == 1)
                    return;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            sawPhysicalAbsence
                ? "Timed out waiting for the exact pinned DAWN PRO2 HID identity to reappear after physical removal."
                : "Timed out waiting for actual physical DAWN PRO2 USB removal; HID restart/disable does not count.");
    }
}

internal sealed record PhysicalArtifactPaths(string SessionPath, string ResultPath, string DiagnosticPath)
{
    public string SnapshotPath => SessionPath;

    public static PhysicalArtifactPaths Create()
    {
        var root = FindRepositoryRoot();
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        return new PhysicalArtifactPaths(
            Path.Combine(root, "tests-dotnet", "artifacts", "hardware-snapshots", $"dawn-pro2-session-{runId}.json"),
            Path.Combine(root, "tests-dotnet", "artifacts", "hardware-results", $"dawn-pro2-result-{runId}.json"),
            Path.Combine(root, "tests-dotnet", "artifacts", "hardware-results", $"dawn-pro2-frames-{runId}.json"));
    }

    public static PhysicalArtifactPaths FromSessionPath(string sessionPath)
    {
        var fullPath = Path.GetFullPath(sessionPath);
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var suffix = fileName.StartsWith("dawn-pro2-session-", StringComparison.Ordinal)
            ? fileName["dawn-pro2-session-".Length..]
            : throw new InvalidOperationException("Session artifact has an unexpected file name.");
        var root = FindRepositoryRoot();
        return new PhysicalArtifactPaths(
            fullPath,
            Path.Combine(root, "tests-dotnet", "artifacts", "hardware-results", $"dawn-pro2-result-{suffix}.json"),
            Path.Combine(root, "tests-dotnet", "artifacts", "hardware-results", $"dawn-pro2-frames-{suffix}.json"));
    }

    public static string HardwareSnapshotsRoot =>
        Path.Combine(FindRepositoryRoot(), "tests-dotnet", "artifacts", "hardware-snapshots");

    internal static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DawnPro.Wpf.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the repository root for physical-test artifacts.");
    }
}

internal static class PhysicalSessionStore
{
    public static string CreateOneRunToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public static string RecoveryCopyPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(".recovery.json", StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.ChangeExtension(fullPath, ".recovery.json");
    }

    public static async Task PersistAsync(string path, PhysicalSessionArtifact session)
    {
        var primary = Path.GetFullPath(path);
        await PhysicalArtifactWriter.WriteJsonAsync(RecoveryCopyPath(primary), session).ConfigureAwait(false);
        await PhysicalArtifactWriter.WriteJsonAsync(primary, session).ConfigureAwait(false);
    }

    public static async Task<PhysicalSessionArtifact> LoadValidatedAsync(string path, CancellationToken cancellationToken = default)
    {
        var primary = Path.GetFullPath(path);
        var hardwareRoot = Path.GetFullPath(PhysicalArtifactPaths.HardwareSnapshotsRoot);
        var relative = Path.GetRelativePath(hardwareRoot, primary);
        var root = !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? hardwareRoot
            : Path.GetDirectoryName(primary)!;
        var leaseBoundary = string.Equals(root, hardwareRoot, StringComparison.OrdinalIgnoreCase)
            ? PhysicalArtifactPaths.FindRepositoryRoot()
            : root;
        using var rootLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(
            leaseBoundary, root, "Physical session artifact root");
        var candidates = new[] { primary, RecoveryCopyPath(primary) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(candidate => TrustedPhysicalPath.RequireContainedNoReparse(root, candidate, "Physical session candidate"))
            .Where(File.Exists)
            .ToArray();
        if (candidates.Length == 0)
            throw new FileNotFoundException("Physical session and its deterministic recovery copy are both missing.", primary);

        var valid = new List<(string Path, PhysicalSessionArtifact Session)>();
        var failures = new List<Exception>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var candidateLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, candidate, "Physical session candidate");
                var session = await PhysicalArtifactWriter.ReadJsonAsync<PhysicalSessionArtifact>(candidate, cancellationToken).ConfigureAwait(false);
                candidateLease.Verify();
                Validate(session);
                valid.Add((candidate, session));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidDataException($"Could not load physical session candidate {candidate}.", ex));
            }
        }
        if (valid.Count == 0)
            throw new AggregateException("No valid physical session publication could be recovered.", failures);
        if (valid.Select(candidate => ImmutableLineageFingerprint(candidate.Session)).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("Valid physical session publications have divergent immutable lineage; refusing timestamp arbitration.");
        var selected = valid
            .OrderByDescending(candidate => candidate.Session.UpdatedAtUtc)
            .ThenBy(candidate => string.Equals(candidate.Path, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First()
            .Session;
        rootLease.Verify();
        return selected;
    }

    public static string ValidateSessionPath(string? sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
            throw new InvalidOperationException($"{PhysicalTestGate.SessionPathEnvironmentVariable} must name the prepared session artifact.");
        var root = PhysicalArtifactPaths.HardwareSnapshotsRoot;
        var candidate = Path.GetFullPath(sessionPath);
        TrustedPhysicalPath.RequireContainedNoReparse(root, candidate, "Physical session artifact");
        TrustedPhysicalPath.RequireContainedNoReparse(root, RecoveryCopyPath(candidate), "Physical session recovery artifact");
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Physical session path must remain inside hardware-snapshots.");
        if (!Path.GetFileName(candidate).StartsWith("dawn-pro2-session-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase) ||
            (!File.Exists(candidate) && !File.Exists(RecoveryCopyPath(candidate))))
            throw new InvalidOperationException("Physical session path must name an existing dawn-pro2-session-*.json artifact or its deterministic recovery copy.");
        return candidate;
    }

    public static void Validate(PhysicalSessionArtifact session)
    {
        if (session.SchemaVersion != PhysicalSessionArtifact.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported physical session schema {session.SchemaVersion}.");
        if (session.SessionId.Length != 32 || !session.SessionId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Physical session identity must be exactly 32 hexadecimal characters.");
        if (string.IsNullOrWhiteSpace(session.OneRunToken))
            throw new InvalidDataException("Physical session has no one-run confirmation token.");
        if (session.SourceFingerprint.Length != 64 || !session.SourceFingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Physical session source fingerprint must be exactly 64 hexadecimal SHA-256 characters.");
        if (session.RuntimeManifestSha256.Length != 64 || !session.RuntimeManifestSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Physical session runtime manifest must be exactly 64 hexadecimal SHA-256 characters.");
        if (session.Phase == PhysicalSessionPhase.Failed &&
            (session.LastSafePhase is null or PhysicalSessionPhase.Prepared or PhysicalSessionPhase.Failed or PhysicalSessionPhase.Completed))
            throw new InvalidDataException("A Failed physical session must carry a recoverable LastSafePhase.");
        if (session.Phase != PhysicalSessionPhase.Failed && session.LastSafePhase is not null)
            throw new InvalidDataException("Only a Failed physical session may carry LastSafePhase.");
        var problems = PhysicalSnapshotValidator.RestorationProblems(session.Original);
        if (problems.Count != 0)
            throw new InvalidDataException($"Physical session original snapshot is not restorable: {string.Join("; ", problems)}");
        var recomputed = PhysicalTransitionPlanner.Create(session.Original);
        RequireSame("baseline", recomputed.Baseline, session.Plan.Baseline);
        RequireSame("individual", recomputed.Individual, session.Plan.Individual);
        RequireSame("individual band", recomputed.IndividualBand, session.Plan.IndividualBand);
        RequireSame("bulk", recomputed.Bulk, session.Plan.Bulk);
    }

    public static string ImmutableLineageFingerprint(PhysicalSessionArtifact session) =>
        DurableLineageFingerprint.Compute(
            session.SchemaVersion,
            session.SessionId,
            session.OneRunToken,
            session.SourceFingerprint,
            session.RuntimeManifestSha256,
            JsonSerializer.SerializeToElement(session.Original),
            JsonSerializer.SerializeToElement(session.Plan));

    private static void RequireSame(string name, HardwareBandSnapshot expected, HardwareBandSnapshot actual)
    {
        if (expected.Index != actual.Index || !expected.RawPayload.SequenceEqual(actual.RawPayload))
            throw new InvalidDataException($"Persisted {name} plan does not match the freshly recomputed original raw snapshot.");
    }

    private static void RequireSame(string name, HardwareSnapshot expected, HardwareSnapshot actual)
    {
        var differences = PhysicalSnapshotComparer.Differences(expected, actual);
        if (differences.Count != 0)
            throw new InvalidDataException($"Persisted {name} plan does not match the original raw snapshot: {string.Join("; ", differences)}");
    }
}

internal static class PhysicalArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task WriteSnapshotAsync(
        string path,
        HardwareSnapshot snapshot,
        IPhysicalArtifactFaultInjector? faultInjector = null) =>
        WriteJsonAtomicallyAsync(path, snapshot, faultInjector);

    public static Task WriteResultAsync(string path, PhysicalTestResult result) =>
        WriteJsonAtomicallyAsync(path, result, null);

    public static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Artifact {path} contained JSON null.");
    }

    public static Task WriteJsonAsync<T>(string path, T value, IPhysicalArtifactFaultInjector? faultInjector = null) =>
        WriteJsonAtomicallyAsync(path, value, faultInjector);

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, IPhysicalArtifactFaultInjector? faultInjector)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        TrustedPhysicalPath.CreateDirectoryNoReparse(directory, "Physical artifact directory");
        TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical artifact");
        using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(directory, directory, "Physical artifact directory");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            faultInjector?.BeforePublish();
            lease.Verify();
            TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical artifact");
            File.Move(temporaryPath, path, overwrite: true);
            lease.Verify();
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

internal interface IPhysicalArtifactFaultInjector
{
    void BeforePublish();
}

internal sealed record PhysicalPhaseResult(
    string Name,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string? Detail);

public enum PhysicalSessionPhase
{
    Prepared,
    TemporaryWritesStarting,
    TemporaryWritesVerified,
    TemporaryFlashSaveStarting,
    AwaitingTemporaryPhysicalCycle,
    TemporaryPersistenceVerified,
    RestorationStarting,
    RestorationWritesVerified,
    RestorationFlashSaveStarting,
    AwaitingRestorationPhysicalCycle,
    RestorationVerified,
    Completed,
    Failed
}

public enum PhysicalRecoveryStep
{
    RestoreRam,
    VerifyRestoration,
    Complete,
    AlreadyCompleted
}

internal static class PhysicalRecoveryResumePlan
{
    private static readonly PhysicalRecoveryStep[] Full =
    [
        PhysicalRecoveryStep.RestoreRam,
        PhysicalRecoveryStep.VerifyRestoration,
        PhysicalRecoveryStep.Complete
    ];

    public static IReadOnlyList<PhysicalRecoveryStep> For(PhysicalSessionPhase phase) => phase switch
    {
        PhysicalSessionPhase.Prepared => throw new InvalidOperationException("A Prepared session has no outstanding write and cannot be recovered."),
        PhysicalSessionPhase.RestorationVerified => [PhysicalRecoveryStep.VerifyRestoration, PhysicalRecoveryStep.Complete],
        PhysicalSessionPhase.Completed => [PhysicalRecoveryStep.AlreadyCompleted],
        _ => Full
    };
}

internal sealed record PhysicalSessionArtifact(
    int SchemaVersion,
    string SessionId,
    string OneRunToken,
    PhysicalSessionPhase Phase,
    PhysicalSessionPhase? LastSafePhase,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    HardwareSnapshot Original,
    PhysicalTransitionPlan Plan,
    string SourceFingerprint,
    string RuntimeManifestSha256,
    IReadOnlyList<DawnPro2HidReadFrame> ReadFrames,
    string? LastError)
{
    public const int CurrentSchemaVersion = 3;

    public static PhysicalSessionArtifact Create(
        HardwareSnapshot original,
        PhysicalTransitionPlan plan,
        string oneRunToken,
        IReadOnlyList<DawnPro2HidReadFrame>? readFrames = null,
        string? sourceFingerprint = null,
        string? runtimeManifestSha256 = null,
        string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(oneRunToken))
            throw new ArgumentException("A non-empty one-run token is required.", nameof(oneRunToken));
        var now = DateTimeOffset.UtcNow;
        return new PhysicalSessionArtifact(
            CurrentSchemaVersion,
            sessionId ?? Guid.NewGuid().ToString("N"),
            oneRunToken,
            PhysicalSessionPhase.Prepared,
            null,
            now,
            now,
            original,
            plan,
            sourceFingerprint ?? new string('0', 64),
            runtimeManifestSha256 ?? new string('0', 64),
            readFrames?.ToArray() ?? [],
            null);
    }

    public PhysicalSessionArtifact Advance(
        PhysicalSessionPhase next,
        IReadOnlyList<DawnPro2HidReadFrame>? readFrames = null,
        string? error = null)
    {
        PhysicalSessionStateMachine.RequireTransition(Phase, next);
        return this with
        {
            Phase = next,
            LastSafePhase = next == PhysicalSessionPhase.Failed
                ? Phase == PhysicalSessionPhase.Failed ? LastSafePhase : Phase
                : null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ReadFrames = readFrames?.ToArray() ?? ReadFrames,
            LastError = error
        };
    }
}

internal static class PhysicalSessionStateMachine
{
    private static readonly IReadOnlyDictionary<PhysicalSessionPhase, PhysicalSessionPhase[]> Allowed =
        new Dictionary<PhysicalSessionPhase, PhysicalSessionPhase[]>
        {
            [PhysicalSessionPhase.Prepared] = [PhysicalSessionPhase.TemporaryWritesStarting],
            [PhysicalSessionPhase.TemporaryWritesStarting] = [PhysicalSessionPhase.TemporaryWritesVerified, PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.TemporaryWritesVerified] = [PhysicalSessionPhase.TemporaryFlashSaveStarting, PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.TemporaryFlashSaveStarting] = [PhysicalSessionPhase.AwaitingTemporaryPhysicalCycle, PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.AwaitingTemporaryPhysicalCycle] = [PhysicalSessionPhase.TemporaryPersistenceVerified, PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.TemporaryPersistenceVerified] = [PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.RestorationStarting] = [PhysicalSessionPhase.RestorationWritesVerified, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.RestorationWritesVerified] = [PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.RestorationFlashSaveStarting, PhysicalSessionPhase.RestorationVerified, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.RestorationFlashSaveStarting] = [PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.AwaitingRestorationPhysicalCycle, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.AwaitingRestorationPhysicalCycle] = [PhysicalSessionPhase.RestorationStarting, PhysicalSessionPhase.RestorationVerified, PhysicalSessionPhase.Failed],
            [PhysicalSessionPhase.RestorationVerified] = [PhysicalSessionPhase.Completed],
            [PhysicalSessionPhase.Failed] = [PhysicalSessionPhase.RestorationStarting],
            [PhysicalSessionPhase.Completed] = []
        };

    public static bool CanTransition(PhysicalSessionPhase current, PhysicalSessionPhase next) =>
        Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next);

    public static void RequireTransition(PhysicalSessionPhase current, PhysicalSessionPhase next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"Unsafe physical session transition {current} -> {next}.");
    }
}

public enum PhysicalExecuteStep
{
    IndividualBand,
    ApplyProfile,
    RestoreOriginalRam
}

internal interface IPhysicalExecuteActions
{
    Task RunAsync(PhysicalExecuteStep step);
}

internal sealed record PhysicalExecuteOutcome(
    PhysicalSessionArtifact Session,
    Exception? PrimaryError,
    Exception? RestorationError,
    bool RestorationAttempted,
    bool RestorationVerified)
{
    public bool Succeeded => PrimaryError is null && Session.Phase == PhysicalSessionPhase.Completed;
}

internal static class PhysicalRecoveryOrchestrator
{
    public static async Task<PhysicalSessionArtifact> RunAsync(
        PhysicalSessionArtifact session,
        HardwareSnapshot currentSnapshot,
        Func<PhysicalSessionArtifact, Task> persistAsync,
        IPhysicalExecuteActions actions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(actions);
        if (session.Phase == PhysicalSessionPhase.Prepared)
            throw new InvalidOperationException("A Prepared session has no outstanding write and cannot be recovered.");
        if (session.Phase == PhysicalSessionPhase.Completed)
            return session;

        PhysicalRecoveryCompatibility.RequireReachable(session, currentSnapshot);
        var current = session;
        if (current.Phase == PhysicalSessionPhase.RestorationVerified)
        {
            current = current.Advance(PhysicalSessionPhase.Completed);
            await persistAsync(current).ConfigureAwait(false);
            return current;
        }

        if (current.Phase != PhysicalSessionPhase.RestorationStarting)
        {
            current = current.Advance(PhysicalSessionPhase.RestorationStarting);
            await persistAsync(current).ConfigureAwait(false);
        }
        await actions.RunAsync(PhysicalExecuteStep.RestoreOriginalRam).ConfigureAwait(false);
        current = current.Advance(PhysicalSessionPhase.RestorationWritesVerified);
        await persistAsync(current).ConfigureAwait(false);
        current = current.Advance(PhysicalSessionPhase.RestorationVerified);
        await persistAsync(current).ConfigureAwait(false);
        current = current.Advance(PhysicalSessionPhase.Completed);
        await persistAsync(current).ConfigureAwait(false);
        return current;
    }
}

internal static class PhysicalExecuteOrchestrator
{
    public static async Task<PhysicalExecuteOutcome> RunAsync(
        PhysicalSessionArtifact session,
        Func<PhysicalSessionArtifact, Task> persistAsync,
        IPhysicalExecuteActions actions,
        params string[] diagnosticSecrets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(actions);
        if (session.Phase != PhysicalSessionPhase.Prepared)
            throw new InvalidOperationException($"Execute requires a Prepared session, not {session.Phase}.");

        var current = session;
        var writesMayHaveStarted = false;
        try
        {
            current = await AdvanceStrictAsync(current, PhysicalSessionPhase.TemporaryWritesStarting, persistAsync).ConfigureAwait(false);
            writesMayHaveStarted = true;
            var temporaryStep = session.Plan.Profile is not null
                ? PhysicalExecuteStep.ApplyProfile
                : PhysicalExecuteStep.IndividualBand;
            await actions.RunAsync(temporaryStep).ConfigureAwait(false);
            current = await AdvanceStrictAsync(current, PhysicalSessionPhase.TemporaryWritesVerified, persistAsync).ConfigureAwait(false);
            current = await RunNormalRestorationAsync(current, persistAsync, actions).ConfigureAwait(false);
            return new PhysicalExecuteOutcome(current, null, null, true, true);
        }
        catch (Exception primary)
        {
            if (!writesMayHaveStarted)
                return new PhysicalExecuteOutcome(current, primary, null, false, false);

            var restoration = await AttemptImmediateRestorationAsync(current, primary, persistAsync, actions, diagnosticSecrets).ConfigureAwait(false);
            return new PhysicalExecuteOutcome(
                restoration.Session,
                primary,
                restoration.Error,
                true,
                restoration.Verified);
        }
    }

    private static async Task<PhysicalSessionArtifact> RunNormalRestorationAsync(
        PhysicalSessionArtifact session,
        Func<PhysicalSessionArtifact, Task> persistAsync,
        IPhysicalExecuteActions actions)
    {
        var current = await AdvanceStrictAsync(session, PhysicalSessionPhase.RestorationStarting, persistAsync).ConfigureAwait(false);
        await actions.RunAsync(PhysicalExecuteStep.RestoreOriginalRam).ConfigureAwait(false);
        current = await AdvanceStrictAsync(current, PhysicalSessionPhase.RestorationWritesVerified, persistAsync).ConfigureAwait(false);
        current = await AdvanceStrictAsync(current, PhysicalSessionPhase.RestorationVerified, persistAsync).ConfigureAwait(false);
        return await AdvanceStrictAsync(current, PhysicalSessionPhase.Completed, persistAsync).ConfigureAwait(false);
    }

    private static async Task<(PhysicalSessionArtifact Session, Exception? Error, bool Verified)> AttemptImmediateRestorationAsync(
        PhysicalSessionArtifact session,
        Exception primary,
        Func<PhysicalSessionArtifact, Task> persistAsync,
        IPhysicalExecuteActions actions,
        IReadOnlyList<string> diagnosticSecrets)
    {
        var current = session;
        Exception? restorationError = null;
        try
        {
            var steps = PhysicalRecoveryResumePlan.For(current.Phase);
            if (steps.Contains(PhysicalRecoveryStep.RestoreRam))
            {
                if (current.Phase != PhysicalSessionPhase.RestorationStarting)
                    (current, restorationError) = await AdvanceBestEffortAsync(current, PhysicalSessionPhase.RestorationStarting, persistAsync, restorationError).ConfigureAwait(false);
                await actions.RunAsync(PhysicalExecuteStep.RestoreOriginalRam).ConfigureAwait(false);
                (current, restorationError) = await AdvanceBestEffortAsync(current, PhysicalSessionPhase.RestorationWritesVerified, persistAsync, restorationError).ConfigureAwait(false);
            }
            if (steps.Contains(PhysicalRecoveryStep.VerifyRestoration))
                (current, restorationError) = await AdvanceBestEffortAsync(current, PhysicalSessionPhase.RestorationVerified, persistAsync, restorationError).ConfigureAwait(false);

            var finalPhaseDurable = true;
            if (restorationError is not null)
            {
                try
                {
                    await persistAsync(current).ConfigureAwait(false);
                }
                catch (Exception finalPersistFailure)
                {
                    restorationError = Combine(restorationError, finalPersistFailure, "Could not republish the final immediate-restoration phase.");
                    finalPhaseDurable = false;
                }
            }
            return (
                current,
                restorationError,
                finalPhaseDurable && (current.Phase is PhysicalSessionPhase.RestorationVerified or PhysicalSessionPhase.Completed));
        }
        catch (Exception restorationFailure)
        {
            restorationError = Combine(restorationError, restorationFailure, "Immediate restoration failed.");
            var combinedText = PhysicalDurableDiagnostic.FromException(
                new AggregateException("Physical execute failed and immediate restoration did not complete.", primary, restorationError),
                [current.OneRunToken, .. diagnosticSecrets])!;
            try
            {
                if (current.Phase != PhysicalSessionPhase.AwaitingRestorationPhysicalCycle &&
                    PhysicalSessionStateMachine.CanTransition(current.Phase, PhysicalSessionPhase.Failed))
                {
                    current = current.Advance(PhysicalSessionPhase.Failed, error: combinedText);
                }
                else
                {
                    current = current with
                    {
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        LastError = combinedText
                    };
                }
                await persistAsync(current).ConfigureAwait(false);
            }
            catch (Exception persistFailure)
            {
                restorationError = Combine(restorationError, persistFailure, "Could not persist immediate-restoration failure state.");
            }
            return (current, restorationError, false);
        }
    }

    private static async Task<PhysicalSessionArtifact> AdvanceStrictAsync(
        PhysicalSessionArtifact session,
        PhysicalSessionPhase phase,
        Func<PhysicalSessionArtifact, Task> persistAsync)
    {
        var advanced = session.Advance(phase);
        await persistAsync(advanced).ConfigureAwait(false);
        return advanced;
    }

    private static async Task<(PhysicalSessionArtifact Session, Exception? Error)> AdvanceBestEffortAsync(
        PhysicalSessionArtifact session,
        PhysicalSessionPhase phase,
        Func<PhysicalSessionArtifact, Task> persistAsync,
        Exception? error)
    {
        var advanced = session.Advance(phase);
        try
        {
            await persistAsync(advanced).ConfigureAwait(false);
        }
        catch (Exception persistFailure)
        {
            error = Combine(error, persistFailure, $"Could not persist restoration phase {phase} before continuing safety actions.");
        }
        return (advanced, error);
    }

    private static Exception Combine(Exception? primary, Exception additional, string message) =>
        primary is null ? new InvalidOperationException(message, additional) : new AggregateException(message, primary, additional);
}

internal sealed record PhysicalTestResult(
    string TestName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string? SnapshotPath,
    bool RestorationAttempted,
    bool RestorationVerified,
    string? PrimaryError,
    string? RestorationError,
    IReadOnlyList<PhysicalPhaseResult> Phases);

internal static class PhysicalPhaseDiagnostic
{
    public static string Sanitize(string? value, params string[] secrets) =>
        DiagnosticText.Sanitize(value, secrets);
}

internal static class PhysicalDurableDiagnostic
{
    public static string? FromException(Exception? exception, params string[] secrets) =>
        exception is null ? null : DiagnosticText.Sanitize(exception.ToString(), secrets);

    public static string Sanitize(string? value, params string[] secrets) =>
        DiagnosticText.Sanitize(value, secrets);
}

internal sealed class PhysicalPhaseJournal
{
    private readonly List<PhysicalPhaseResult> _phases = [];
    private readonly TestContext _context;
    private readonly string[] _secrets;
    public IReadOnlyList<PhysicalPhaseResult> Phases => _phases;

    public PhysicalPhaseJournal(TestContext context, params string[] secrets)
    {
        _context = context;
        _secrets = secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task RunAsync(string name, Func<Task> action)
    {
        await RunAsync<object?>(name, async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(string name, Func<Task<T>> action)
    {
        var started = DateTimeOffset.UtcNow;
        var safeName = PhysicalPhaseDiagnostic.Sanitize(name, _secrets);
        _context.WriteLine($"[{started:O}] START {safeName}");
        try
        {
            var result = await action().ConfigureAwait(false);
            var completed = DateTimeOffset.UtcNow;
            _phases.Add(new PhysicalPhaseResult(safeName, started, completed, "passed", null));
            _context.WriteLine($"[{completed:O}] PASS {safeName}");
            return result;
        }
        catch (Exception ex)
        {
            var completed = DateTimeOffset.UtcNow;
            var safeError = PhysicalPhaseDiagnostic.Sanitize(ex.Message, _secrets);
            _phases.Add(new PhysicalPhaseResult(safeName, started, completed, "failed", safeError));
            _context.WriteLine($"[{completed:O}] FAIL {safeName}: {safeError}");
            throw;
        }
    }

    public void Record(string name, string status, string? detail = null)
    {
        var now = DateTimeOffset.UtcNow;
        var safeName = PhysicalPhaseDiagnostic.Sanitize(name, _secrets);
        var safeStatus = PhysicalPhaseDiagnostic.Sanitize(status, _secrets);
        var safeDetail = detail is null ? null : PhysicalPhaseDiagnostic.Sanitize(detail, _secrets);
        _phases.Add(new PhysicalPhaseResult(safeName, now, now, safeStatus, safeDetail));
        _context.WriteLine($"[{now:O}] {safeStatus.ToUpperInvariant()} {safeName}{(safeDetail is null ? "" : $": {safeDetail}")}");
    }
}
