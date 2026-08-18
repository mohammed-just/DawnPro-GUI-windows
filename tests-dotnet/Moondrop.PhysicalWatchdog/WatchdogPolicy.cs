using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Moondrop.PhysicalWatchdog;

public static partial class DiagnosticText
{
    [GeneratedRegex(@"dawn-pro2-(?:watchdog|recovery)-[0-9A-Fa-f]{32}", RegexOptions.CultureInvariant)]
    private static partial Regex OwnershipTokenPattern();

    public static string Sanitize(string? value, params string[] secrets)
    {
        if (value is null)
            return "[null]";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character))
                builder.Append($"\\u{(int)character:X4}");
            else
                builder.Append(character);
        }
        var sanitized = OwnershipTokenPattern().Replace(builder.ToString(), "[REDACTED]");
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct(StringComparer.Ordinal))
            sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return sanitized;
    }

    public static string SanitizeWatchdogFailure(Exception exception, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(arguments);
        var sensitiveOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--confirmation",
            "--session",
            "--report"
        };
        var secrets = new List<string>();
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (sensitiveOptions.Contains(arguments[index]) && !string.IsNullOrEmpty(arguments[index + 1]))
                secrets.Add(arguments[index + 1]);
        }
        return Sanitize(exception.ToString(), secrets.ToArray());
    }
}

public enum WatchdogMode
{
    Execute,
    Recovery
}

// Numeric values intentionally mirror the durable PhysicalSessionPhase JSON contract.
public enum DurablePhysicalPhase
{
    Prepared = 0,
    TemporaryWritesStarting = 1,
    TemporaryWritesVerified = 2,
    TemporaryFlashSaveStarting = 3,
    AwaitingTemporaryPhysicalCycle = 4,
    TemporaryPersistenceVerified = 5,
    RestorationStarting = 6,
    RestorationWritesVerified = 7,
    RestorationFlashSaveStarting = 8,
    AwaitingRestorationPhysicalCycle = 9,
    RestorationVerified = 10,
    Completed = 11,
    Failed = 12
}

public sealed record PhysicalTestCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string OwnershipToken,
    string ProjectPath,
    string SessionPath,
    WatchdogOwnerIdentity Owner,
    WatchdogMode Mode,
    string? Confirmation,
    DurableSessionState Session,
    string RepositoryRoot);

public sealed record WatchdogOwnerIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath);

public sealed record OwnedPhysicalProcess(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string OwnershipToken,
    string SessionPath,
    string ProjectPath);

public sealed record ObservedPhysicalProcess(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string CommandLine);

public interface IObservedPhysicalProcessSnapshotReader
{
    ObservedPhysicalProcess Read(int processId);
}

public sealed class CoherentObservedPhysicalProcessProvider(
    IObservedPhysicalProcessSnapshotReader reader)
{
    public ObservedPhysicalProcess Get(int processId)
    {
        var first = reader.Read(processId);
        var second = reader.Read(processId);
        if (first.ProcessId != processId || second.ProcessId != processId || first != second)
            throw new InvalidOperationException(
                $"Owned process identity for requested PID {processId} disappeared, was reused, or drifted during coherent acquisition.");
        return second;
    }
}

public sealed class WindowsObservedPhysicalProcessSnapshotReader : IObservedPhysicalProcessSnapshotReader
{
    // Managed .NET 10-compatible replacement for the historical dynamic-COM (SWbemLocator)
    // Win32_Process observation. The dynamic COM path deterministically threw
    // COMException 0x80004005 (E_FAIL) on .NET Core/.NET 10 when reading Win32_Process rows
    // for a process other than the caller, blocking EXECUTE supervision before the suspended
    // child could be authenticated. This implementation uses only managed P/Invoke into
    // kernel32/ntdll and works for both SUSPENDED and running processes. Every failure path
    // is fail-closed; the caller's CoherentObservedPhysicalProcessProvider still requires two
    // byte-for-byte identical reads so PID reuse, disappearance, or identity drift is rejected.

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const int ProcessCommandLineInformation = 60;
    private const int CommandLineBufferSize = 32768;
    private static readonly DateTimeOffset FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
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
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass, IntPtr buffer, int bufferLength, out int returnLength);

    public ObservedPhysicalProcess Read(int processId)
    {
        DateTimeOffset startedAtUtc;
        var limited = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (limited == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Owned PID {processId} disappeared or could not be opened for identity verification (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            if (!GetProcessTimes(limited, out var creation, out _, out _, out _))
                throw new InvalidDataException($"Could not read the creation time for PID {processId} (Win32 error {Marshal.GetLastWin32Error()}).");
            if (creation.Low == 0 && creation.High == 0)
                throw new InvalidDataException($"PID {processId} reported an invalid zero creation time.");
            startedAtUtc = FileTimeEpoch.AddTicks(((long)creation.High << 32) | creation.Low);
            var imageName = new System.Text.StringBuilder(1024);
            var imageLength = imageName.Capacity;
            if (!QueryFullProcessImageNameW(limited, 0, imageName, ref imageLength) || imageLength == 0)
                throw new InvalidDataException($"Could not resolve the executable path for PID {processId} (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            CloseHandle(limited);
        }

        var query = OpenProcess(ProcessQueryInformation, false, processId);
        if (query == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Owned PID {processId} could not be opened for command-line verification (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            var buffer = Marshal.AllocHGlobal(CommandLineBufferSize);
            try
            {
                var status = NtQueryInformationProcess(query, ProcessCommandLineInformation, buffer, CommandLineBufferSize, out _);
                if (status != 0)
                    throw new InvalidDataException($"NtQueryInformationProcess failed for PID {processId} with status 0x{status:X8}.");
                var length = Marshal.ReadInt16(buffer, 0);
                var bufferPointer = Marshal.ReadIntPtr(buffer, 8);
                if (length <= 0 || (length & 1) != 0)
                    throw new InvalidDataException($"PID {processId} reported a malformed command-line length {length}.");
                var offset = checked((int)((long)bufferPointer - (long)buffer));
                if (offset < 0 || (long)offset + length > CommandLineBufferSize)
                    throw new InvalidDataException($"PID {processId} command line escaped the verification buffer; refusing untrusted identity.");
                var commandLine = Marshal.PtrToStringUni(buffer + offset, length / 2);
                if (string.IsNullOrEmpty(commandLine))
                    throw new InvalidDataException($"PID {processId} command line is null or empty.");
                return new ObservedPhysicalProcess(processId, startedAtUtc, commandLine);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(query);
        }
    }
}

public static class PhysicalTestCommandBuilder
{
    public static string DescribeForDryRun(PhysicalTestCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var secrets = new[]
            {
                command.OwnershipToken,
                command.Confirmation,
                command.Session.OneRunToken
            }
            .Where(value => !string.IsNullOrEmpty(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string Redact(string? value)
        {
            if (value is null)
                return "[REDACTED]";
            var redacted = value;
            foreach (var secret in secrets)
                redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            return redacted;
        }
        var environment = command.Environment.ToDictionary(
            pair => pair.Key,
            pair => pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Contains("CONFIRMATION", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key is "MOONDROP_PHYSICAL_SESSION_PATH" or "MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT"
                ? "[REDACTED]"
                : Redact(pair.Value),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(
            new
            {
                FileName = Redact(command.FileName),
                Arguments = command.Arguments.Select(Redact).ToArray(),
                Environment = environment,
                OwnershipToken = "[REDACTED]",
                ProjectPath = Redact(command.ProjectPath),
                SessionPath = "[REDACTED]",
                Owner = new
                {
                    command.Owner.ProcessId,
                    command.Owner.StartedAtUtc,
                    ExecutablePath = Redact(command.Owner.ExecutablePath)
                }
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static PhysicalTestCommand Build(
        WatchdogMode mode,
        string repositoryRoot,
        string sessionPath,
        string? confirmation,
        string ownershipToken,
        DurableSessionState session,
        WatchdogOwnerIdentity owner,
        string? physicalRunnerPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipToken);
        ArgumentNullException.ThrowIfNull(session);
        if (session.SessionId.Length != 32 || !session.SessionId.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(session.OneRunToken) ||
            session.SourceFingerprint.Length != 64 || !session.SourceFingerprint.All(Uri.IsHexDigit) ||
            session.RuntimeManifestSha256.Length != 64 || !session.RuntimeManifestSha256.All(Uri.IsHexDigit) ||
            session.LineageFingerprint.Length != 64 || !session.LineageFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Session binding is incomplete or malformed.", nameof(session));
        ArgumentNullException.ThrowIfNull(owner);
        if (mode == WatchdogMode.Execute && string.IsNullOrWhiteSpace(confirmation))
            throw new ArgumentException("Execute requires the prepared one-run confirmation token.", nameof(confirmation));

        var root = Path.GetFullPath(repositoryRoot);
        var project = physicalRunnerPath is null
            ? Path.Combine(root, "tests-dotnet", "Moondrop.PhysicalTests", "bin", "Release", "net10.0-windows", "Moondrop.PhysicalTests.exe")
            : Path.GetFullPath(physicalRunnerPath);
        var settings = Path.Combine(root, "tests-dotnet", "physical.runsettings");
        var results = Path.Combine(root, "tests-dotnet", "artifacts", "watchdog", ownershipToken);
        var heartbeat = Path.Combine(results, "heartbeat.json");
        var method = mode == WatchdogMode.Execute
            ? "ExecutePreparedDawnPro2PhysicalSessionAsync"
            : "RecoverDawnPro2FromPreparedSnapshotAsync";
        var arguments = new[]
        {
            "--settings", settings,
            "--filter", $"FullyQualifiedName=Moondrop.Tests.DawnPro2PhysicalIntegrationTests.{method}",
            "--results-directory", results,
            "--output", "Detailed",
            "--report-trx"
        };
        var environment = mode == WatchdogMode.Execute
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MOONDROP_RUN_PHYSICAL_TESTS"] = "1",
                ["MOONDROP_PHYSICAL_SESSION_PATH"] = Path.GetFullPath(sessionPath),
                ["MOONDROP_PHYSICAL_CONFIRMATION"] = confirmation!
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MOONDROP_RUN_PHYSICAL_RECOVERY"] = "1",
                ["MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT"] = Path.GetFullPath(sessionPath)
            };
        environment["MOONDROP_PHYSICAL_WATCHDOG_TOKEN"] = ownershipToken;
        environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"] = heartbeat;
        environment["MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID"] = session.SessionId;
        environment["MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN"] = session.OneRunToken;
        environment["MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT"] = session.SourceFingerprint;
        environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256"] = session.RuntimeManifestSha256;
        environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_PATH"] = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(project))!,
            "runtime-manifest.json");
        environment["MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT"] = session.LineageFingerprint;
        environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_PID"] = owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_START_UTC"] = owner.StartedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_EXE"] = Path.GetFullPath(owner.ExecutablePath);
        return new PhysicalTestCommand(
            project,
            arguments,
            environment,
            ownershipToken,
            project,
            Path.GetFullPath(sessionPath),
            owner,
            mode,
            confirmation,
            session,
            root);
    }
}

public static class PhysicalRunnerProcessStartInfo
{
    private static readonly string[] RequiredSystemEnvironmentVariables =
    {
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP"
    };

    private static readonly string[] CommonCommandEnvironmentVariables =
    {
        "MOONDROP_PHYSICAL_WATCHDOG_TOKEN",
        "MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT",
        "MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID",
        "MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN",
        "MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT",
        "MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256",
        "MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_PATH",
        "MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT",
        "MOONDROP_PHYSICAL_WATCHDOG_PARENT_PID",
        "MOONDROP_PHYSICAL_WATCHDOG_PARENT_START_UTC",
        "MOONDROP_PHYSICAL_WATCHDOG_PARENT_EXE"
    };

    private static readonly string[] ExecuteCommandEnvironmentVariables =
    {
        "MOONDROP_RUN_PHYSICAL_TESTS",
        "MOONDROP_PHYSICAL_SESSION_PATH",
        "MOONDROP_PHYSICAL_CONFIRMATION"
    };

    private static readonly string[] RecoveryCommandEnvironmentVariables =
    {
        "MOONDROP_RUN_PHYSICAL_RECOVERY",
        "MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT"
    };

    public static PhysicalProcessLaunchPlan Create(PhysicalTestCommand command)
    {
        var systemEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in RequiredSystemEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name)
                        ?? throw new InvalidDataException($"Required system environment variable {name} is missing.");
            systemEnvironment.Add(name, value);
        }
        return Create(command, systemEnvironment);
    }

    internal static PhysicalProcessLaunchPlan Create(
        PhysicalTestCommand command,
        IReadOnlyDictionary<string, string> systemEnvironment)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(systemEnvironment);
        RequireExactCommandEnvironmentKeys(command.Environment);
        RequireSafeValue(command.FileName, "Physical runner apphost path");
        RequireSafeValue(command.ProjectPath, "Physical runner project identity");
        RequireSafeValue(command.SessionPath, "Physical session path");
        RequireSafeValue(command.RepositoryRoot, "Physical repository root");
        RequireSafeValue(command.OwnershipToken, "Watchdog ownership token");
        RequireSafeValue(command.Owner.ExecutablePath, "Watchdog owner executable path");
        foreach (var argument in command.Arguments)
            RequireSafeValue(argument, "Physical runner argument");
        foreach (var pair in command.Environment)
            RequireSafeValue(pair.Value, pair.Key);
        RequireExactCommandIdentity(command);
        var validatedSystemEnvironment = ValidateSystemEnvironment(systemEnvironment);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in validatedSystemEnvironment)
            environment[pair.Key] = pair.Value;
        foreach (var pair in command.Environment)
            environment[pair.Key] = pair.Value;
        return new PhysicalProcessLaunchPlan(
            command.FileName,
            Path.GetDirectoryName(Path.GetDirectoryName(command.ProjectPath))!,
            command.Arguments.ToArray(),
            environment);
    }

    private static IReadOnlyDictionary<string, string> ValidateSystemEnvironment(
        IReadOnlyDictionary<string, string> environment) =>
        PhysicalSystemEnvironment.Validate(environment, "Physical runner");

    private static void RequireExactCommandEnvironmentKeys(IReadOnlyDictionary<string, string> environment)
    {
        var execute = environment.TryGetValue("MOONDROP_RUN_PHYSICAL_TESTS", out var executeValue) &&
                      string.Equals(executeValue, "1", StringComparison.Ordinal);
        var recovery = environment.TryGetValue("MOONDROP_RUN_PHYSICAL_RECOVERY", out var recoveryValue) &&
                       string.Equals(recoveryValue, "1", StringComparison.Ordinal);
        if (execute == recovery)
            throw new InvalidDataException("Physical runner environment must select exactly one execute or recovery phase.");

        var allowed = new HashSet<string>(CommonCommandEnvironmentVariables, StringComparer.Ordinal);
        allowed.UnionWith(execute ? ExecuteCommandEnvironmentVariables : RecoveryCommandEnvironmentVariables);
        if (environment.Count != allowed.Count || environment.Keys.Any(name => !allowed.Contains(name)))
            throw new InvalidDataException("Physical runner environment contains a missing, unexpected, or non-canonical variable name.");
    }

    private static void RequireSafeValue(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new InvalidDataException($"{description} is missing or contains an unsafe control character.");
    }

    private static void RequireExactCommandIdentity(PhysicalTestCommand command)
    {
        var repositoryRoot = RequireCanonicalAbsolutePath(command.RepositoryRoot, "Physical repository root");
        var runner = RequireCanonicalAbsolutePath(command.FileName, "Physical runner apphost path");
        var project = RequireCanonicalAbsolutePath(command.ProjectPath, "Physical runner project identity");
        TrustedPhysicalPath.RequireNoReparse(repositoryRoot, "Physical repository root");
        var runtimeRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "tests-dotnet", "artifacts", "physical-runtime"));
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, runner, "Physical runner apphost");
        if (!string.Equals(runner, project, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(runner), "Moondrop.PhysicalTests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Physical runner apphost path does not match the exact runner identity.");

        var sessionPath = RequireCanonicalAbsolutePath(command.SessionPath, "Physical session path");
        var artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "tests-dotnet", "artifacts"));
        TrustedPhysicalPath.RequireContainedNoReparse(artifactRoot, sessionPath, "Physical session artifact");
        var environment = command.Environment;
        var phaseSessionPath = command.Mode == WatchdogMode.Execute
            ? environment["MOONDROP_PHYSICAL_SESSION_PATH"]
            : environment["MOONDROP_PHYSICAL_RECOVERY_SNAPSHOT"];
        if (!string.Equals(
                sessionPath,
                RequireCanonicalAbsolutePath(phaseSessionPath, "Physical phase session path"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Physical phase session path does not match the supervised session identity.");

        var expectedPhaseVariable = command.Mode == WatchdogMode.Execute
            ? "MOONDROP_RUN_PHYSICAL_TESTS"
            : "MOONDROP_RUN_PHYSICAL_RECOVERY";
        if (!string.Equals(environment[expectedPhaseVariable], "1", StringComparison.Ordinal))
            throw new InvalidDataException("Physical phase opt-in is not the exact expected value.");
        if (command.Mode == WatchdogMode.Execute &&
            !string.Equals(environment["MOONDROP_PHYSICAL_CONFIRMATION"], command.Confirmation, StringComparison.Ordinal))
            throw new InvalidDataException("Physical execute confirmation does not match the command contract.");
        if (command.Mode == WatchdogMode.Recovery && command.Confirmation is not null)
            throw new InvalidDataException("Physical recovery must not carry an execute confirmation.");

        if (!string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_TOKEN"], command.OwnershipToken, StringComparison.Ordinal) ||
            !string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_SESSION_ID"], command.Session.SessionId, StringComparison.Ordinal) ||
            !string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_ONE_RUN_TOKEN"], command.Session.OneRunToken, StringComparison.Ordinal) ||
            !string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_SOURCE_FINGERPRINT"], command.Session.SourceFingerprint, StringComparison.Ordinal) ||
            !string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_SHA256"], command.Session.RuntimeManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(environment["MOONDROP_PHYSICAL_WATCHDOG_LINEAGE_FINGERPRINT"], command.Session.LineageFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Physical watchdog authorization or durable session binding drifted from the command contract.");

        var runtimeManifestPath = RequireCanonicalAbsolutePath(
            environment["MOONDROP_PHYSICAL_WATCHDOG_RUNTIME_MANIFEST_PATH"],
            "Physical runtime manifest path");
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, runtimeManifestPath, "Physical runtime manifest path");

        var parentExecutable = RequireCanonicalAbsolutePath(
            environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_EXE"],
            "Physical watchdog parent executable");
        var ownerExecutable = RequireCanonicalAbsolutePath(command.Owner.ExecutablePath, "Watchdog owner executable path");
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, ownerExecutable, "Physical watchdog apphost");
        if (!string.Equals(parentExecutable, ownerExecutable, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(parentExecutable), "Moondrop.PhysicalWatchdog.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_PID"],
                command.Owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                environment["MOONDROP_PHYSICAL_WATCHDOG_PARENT_START_UTC"],
                command.Owner.StartedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw new InvalidDataException("Physical watchdog direct-parent identity drifted from the command contract.");

        var heartbeat = RequireCanonicalAbsolutePath(
            environment["MOONDROP_PHYSICAL_WATCHDOG_HEARTBEAT"],
            "Physical watchdog heartbeat path");
        var heartbeatDirectory = Path.GetDirectoryName(heartbeat)!;
        var heartbeatRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "tests-dotnet", "artifacts", "watchdog"));
        TrustedPhysicalPath.RequireContainedNoReparse(heartbeatRoot, heartbeatDirectory, "Physical heartbeat directory");
        TrustedPhysicalPath.RequireContainedNoReparse(heartbeatRoot, heartbeat, "Physical heartbeat file");
        if (!string.Equals(Path.GetFileName(heartbeat), "heartbeat.json", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(heartbeatDirectory), command.OwnershipToken, StringComparison.Ordinal))
            throw new InvalidDataException("Physical watchdog heartbeat path is not bound to the ownership token.");

        var expectedFilter = command.Mode == WatchdogMode.Execute
            ? "FullyQualifiedName=Moondrop.Tests.DawnPro2PhysicalIntegrationTests.ExecutePreparedDawnPro2PhysicalSessionAsync"
            : "FullyQualifiedName=Moondrop.Tests.DawnPro2PhysicalIntegrationTests.RecoverDawnPro2FromPreparedSnapshotAsync";
        var expectedSettings = Path.Combine(repositoryRoot, "tests-dotnet", "physical.runsettings");
        if (command.Arguments.Count != 9 ||
            !string.Equals(command.Arguments[0], "--settings", StringComparison.Ordinal) ||
            !string.Equals(
                RequireCanonicalAbsolutePath(command.Arguments[1], "Physical runsettings path"),
                expectedSettings,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.Arguments[2], "--filter", StringComparison.Ordinal) ||
            !string.Equals(command.Arguments[3], expectedFilter, StringComparison.Ordinal) ||
            !string.Equals(command.Arguments[4], "--results-directory", StringComparison.Ordinal) ||
            !string.Equals(
                RequireCanonicalAbsolutePath(command.Arguments[5], "Physical results directory"),
                heartbeatDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.Arguments[6], "--output", StringComparison.Ordinal) ||
            !string.Equals(command.Arguments[7], "Detailed", StringComparison.Ordinal) ||
            !string.Equals(command.Arguments[8], "--report-trx", StringComparison.Ordinal))
            throw new InvalidDataException("Physical runner arguments drifted from the exact supervised phase contract.");
    }

    private static string RequireCanonicalAbsolutePath(string value, string description)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value))
                throw new InvalidDataException($"{description} is not absolute.");
            var full = Path.GetFullPath(value);
            if (!string.Equals(full, value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{description} is not canonical.");
            return full;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"{description} is invalid.", ex);
        }
    }
}

public static class PhysicalSystemEnvironment
{
    private static readonly string[] RequiredNames = ["SystemRoot", "WINDIR", "TEMP", "TMP"];

    public static IReadOnlyDictionary<string, string> Validate(
        IReadOnlyDictionary<string, string> environment,
        string description)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var required = new HashSet<string>(RequiredNames, StringComparer.OrdinalIgnoreCase);
        if (environment.Count != required.Count || environment.Keys.Any(name => !required.Contains(name)))
            throw new InvalidDataException($"{description} system environment is not exact.");
        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredNames)
        {
            var value = environment[name];
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
                throw new InvalidDataException($"{description} system environment variable {name} is unsafe or not absolute.");
            var canonical = Path.GetFullPath(value);
            if (!Directory.Exists(canonical))
                throw new InvalidDataException($"{description} system environment variable {name} is not an existing directory.");
            TrustedPhysicalPath.RequireNoReparse(canonical, $"{description} system environment variable {name}");
            validated.Add(name, canonical);
        }
        var expectedWindows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.Equals(validated["SystemRoot"], expectedWindows, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(validated["WINDIR"], expectedWindows, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description} SystemRoot and WINDIR do not match the current Windows directory identity.");
        return validated;
    }
}

public static class PhysicalRunnerLaunchPreparation
{
    public static PhysicalProcessLaunchPlan Prepare(PhysicalTestCommand command, Action publishValidatedHeartbeat)
    {
        ArgumentNullException.ThrowIfNull(publishValidatedHeartbeat);
        var plan = PhysicalRunnerProcessStartInfo.Create(command);
        publishValidatedHeartbeat();
        return plan;
    }
}

public sealed record PhysicalProbeProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    string Sha256);

public sealed record PhysicalOfflineTopologyObservation(
    string SafetyMode,
    PhysicalProbeProcessIdentity Watchdog,
    PhysicalProbeProcessIdentity PhysicalRunner,
    PhysicalProbeProcessIdentity? ActualParent = null,
    bool MtpEntered = false,
    string TestFullyQualifiedName = "",
    bool IsAccepted = true,
    string Predicate = "direct-parent");

public interface IPhysicalProbeProcessIdentitySnapshotReader
{
    PhysicalProbeProcessIdentity Read(int processId);
}

public sealed class CoherentPhysicalProbeProcessIdentityProvider(
    IPhysicalProbeProcessIdentitySnapshotReader reader)
{
    public PhysicalProbeProcessIdentity Get(int processId)
    {
        var first = reader.Read(processId);
        var second = reader.Read(processId);
        if (first.ProcessId != processId || second.ProcessId != processId || first != second)
            throw new InvalidOperationException(
                $"Probe process identity for requested PID {processId} disappeared, was reused, or drifted during coherent acquisition.");
        return second;
    }
}

public enum DeliberateOfflineWrapperShape
{
    CommandPrompt,
    WindowsPowerShell
}

public sealed record PhysicalProcessLaunchPlan(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> ArgumentList,
    IReadOnlyDictionary<string, string> Environment,
    bool RedirectStandardOutput = false,
    bool RedirectStandardError = false);

public sealed record PhysicalProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class OwnedPhysicalProcessLaunch(
    Process process,
    PhysicalProcessJob job,
    FileStream? outputStream,
    string? outputPath,
    FileStream? errorStream,
    string? errorPath,
    TrustedPhysicalPath.StablePathLease? captureRootLease) : IDisposable
{
    public Process Process { get; } = process;
    public PhysicalProcessJob Job { get; } = job;
    private bool _capturesClosed;

    public string FinishOutput()
    {
        CloseCaptures();
        return outputPath is null ? "" : ReadCaptured(outputPath);
    }

    public string FinishError()
    {
        CloseCaptures();
        return errorPath is null ? "" : ReadCaptured(errorPath);
    }

    private void CloseCaptures()
    {
        if (_capturesClosed) return;
        outputStream?.Dispose();
        errorStream?.Dispose();
        _capturesClosed = true;
    }

    private static string ReadCaptured(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        CloseCaptures();
        Process.Dispose();
        Job.Dispose();
        foreach (var path in new[] { outputPath, errorPath })
            if (path is not null && File.Exists(path)) File.Delete(path);
        captureRootLease?.Dispose();
    }
}

public static class PhysicalProcessLauncher
{
    public const string SeamIdentity = "PhysicalProcessLauncher.MaterializeAndStart";

    public static ProcessStartInfo Materialize(PhysicalProcessLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var startInfo = new ProcessStartInfo(plan.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = plan.RedirectStandardOutput,
            RedirectStandardError = plan.RedirectStandardError
        };
        foreach (var argument in plan.ArgumentList)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var pair in plan.Environment)
            startInfo.Environment[pair.Key] = pair.Value;
        return startInfo;
    }

    internal static OwnedPhysicalProcessLaunch StartOwnedSuspended(
        PhysicalProcessLaunchPlan plan,
        Action<int>? whileSuspended = null,
        Func<int, Process>? processResolver = null)
    {
        _ = Materialize(plan);
        FileStream? output = null, error = null;
        string? outputPath = null, errorPath = null;
        TrustedPhysicalPath.StablePathLease? captureRootLease = null;
        try
        {
            string? captureRoot = null;
            if (plan.RedirectStandardOutput || plan.RedirectStandardError)
            {
                if (!plan.Environment.TryGetValue("TEMP", out var configuredTemp) ||
                    string.IsNullOrWhiteSpace(configuredTemp) || !Path.IsPathFullyQualified(configuredTemp))
                    throw new InvalidDataException("Supervised output capture requires an absolute TEMP directory from the validated launch environment.");
                captureRoot = Path.GetFullPath(configuredTemp);
                TrustedPhysicalPath.RequireNoReparse(captureRoot, "Supervised output capture root");
                captureRootLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(captureRoot, captureRoot, "Supervised output capture root");
            }
            if (plan.RedirectStandardOutput)
                (output, outputPath) = CreateInheritedCapture(captureRoot!, "stdout");
            if (plan.RedirectStandardError)
                (error, errorPath) = CreateInheritedCapture(captureRoot!, "stderr");
            captureRootLease?.Verify();
            var startup = new NativeStartupInfo { cb = Marshal.SizeOf<NativeStartupInfo>() };
            if (output is not null || error is not null)
            {
                startup.dwFlags = 0x00000100;
                startup.hStdInput = GetStdHandle(-10);
                startup.hStdOutput = output?.SafeFileHandle.DangerousGetHandle() ?? GetStdHandle(-11);
                startup.hStdError = error?.SafeFileHandle.DangerousGetHandle() ?? GetStdHandle(-12);
            }
            var environment = Marshal.StringToHGlobalUni(
                string.Join('\0', plan.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            try
            {
                var commandLine = new StringBuilder(string.Join(' ', new[] { plan.FileName }.Concat(plan.ArgumentList).Select(QuoteWindowsArgument)));
                if (!CreateProcessW(plan.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, output is not null || error is not null,
                        0x00000004 | 0x00000400, environment, plan.WorkingDirectory, ref startup, out var native))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the supervised process suspended.");
                using var nativeProcess = new SafeFileHandle(native.hProcess, ownsHandle: true);
                using var nativeThread = new SafeFileHandle(native.hThread, ownsHandle: true);
                Process? process = null;
                PhysicalProcessJob? job = null;
                var ownershipTransferred = false;
                Exception? launchFailure = null;
                try
                {
                    process = (processResolver ?? Process.GetProcessById)((int)native.dwProcessId);
                    job = PhysicalProcessJob.Assign(process);
                    whileSuspended?.Invoke(process.Id);
                    var owned = new OwnedPhysicalProcessLaunch(process, job, output, outputPath, error, errorPath, captureRootLease);
                    if (ResumeThread(native.hThread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the job-owned supervised process.");
                    ownershipTransferred = true;
                    return owned;
                }
                catch (Exception ex)
                {
                    launchFailure = ex;
                    throw;
                }
                finally
                {
                    if (!ownershipTransferred)
                    {
                        var cleanupFailure = CleanupUnhandedOffLaunch(job, native.hProcess);
                        process?.Dispose();
                        job?.Dispose();
                        if (cleanupFailure is not null)
                            throw new AggregateException(
                                "Supervised suspended launch did not complete and cleanup could not be proven complete.",
                                launchFailure ?? new InvalidOperationException("Suspended launch exited before ownership transfer."),
                                cleanupFailure);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(environment); }
        }
        catch
        {
            output?.Dispose(); error?.Dispose();
            foreach (var path in new[] { outputPath, errorPath }) if (path is not null && File.Exists(path)) File.Delete(path);
            captureRootLease?.Dispose();
            throw;
        }
    }

    public static async Task<PhysicalProcessResult> RunToExitAsync(
        PhysicalProcessLaunchPlan plan,
        CancellationToken cancellationToken,
        Action<int>? onStarted = null) =>
        await RunToExitInKillOnCloseJobAsync(plan, cancellationToken, onStarted).ConfigureAwait(false);

    public static async Task<PhysicalProcessResult> RunToExitInKillOnCloseJobAsync(
        PhysicalProcessLaunchPlan plan,
        CancellationToken cancellationToken,
        Action<int>? onStarted = null)
    {
        using var owned = StartOwnedSuspended(plan);
        var process = owned.Process;
        try
        {
            onStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await RequireOwnedProcessStoppedAsync(process, owned.Job, terminateImmediately: false).ConfigureAwait(false);
            return new PhysicalProcessResult(
                process.ExitCode,
                owned.FinishOutput(),
                owned.FinishError());
        }
        catch (Exception operationFailure)
        {
            try
            {
                await RequireOwnedProcessStoppedAsync(process, owned.Job, terminateImmediately: true).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Supervised process operation failed and cleanup could not be proven complete.", operationFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal static async Task RequireOwnedProcessStoppedAsync(
        Process process,
        PhysicalProcessJob job,
        bool terminateImmediately)
    {
        var failures = new List<Exception>();
        try
        {
            if (terminateImmediately)
                await job.TerminateAndRequireEmptyAsync().ConfigureAwait(false);
            else
                await job.TerminateRemainingAndRequireEmptyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            try { job.Dispose(); }
            catch (Exception disposeFailure) { failures.Add(disposeFailure); }
        }
        try
        {
            await RequireBoundedRootExitAsync(process).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Supervised process cleanup encountered multiple failures.", failures);
    }

    internal static async Task RequireBoundedRootExitAsync(Process process, TimeSpan? waitLimit = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        var limit = waitLimit ?? TimeSpan.FromSeconds(10);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitLimit));
        using var firstWait = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(firstWait.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (firstWait.IsCancellationRequested)
        {
            // The owned root did not exit under the first bounded proof window; force only this known root tree.
        }

        var failures = new List<Exception>();
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        using var finalWait = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(finalWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (finalWait.IsCancellationRequested)
        {
            failures.Add(new TimeoutException("Known supervised root did not exit after the bounded forced-termination attempt.", ex));
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Known supervised root could not be proven exited after bounded cleanup.", failures);
    }

    private static (FileStream Stream, string Path) CreateInheritedCapture(string captureRoot, string suffix)
    {
        var path = Path.Combine(captureRoot, $"moondrop-supervised-{Guid.NewGuid():N}.{suffix}");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        if (!SetHandleInformation(stream.SafeFileHandle.DangerousGetHandle(), 1, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the supervised capture handle inheritable.");
        return (stream, path);
    }

    internal static string QuoteWindowsArgument(string value)
    {
        if (value.Length != 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { builder.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            builder.Append('\\', slashes).Append(character); slashes = 0;
        }
        return builder.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static Exception? CleanupUnhandedOffLaunch(PhysicalProcessJob? job, IntPtr processHandle)
    {
        var failures = new List<Exception>();
        if (job is not null)
        {
            try { job.TerminateAndRequireEmptyAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { failures.Add(ex); }
            try { job.Dispose(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        try { TerminateNativeProcessAndRequireExit(processHandle); }
        catch (Exception ex) { failures.Add(ex); }
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Suspended launch cleanup encountered multiple failures.", failures)
        };
    }

    private static void TerminateNativeProcessAndRequireExit(IntPtr processHandle)
    {
        var wait = WaitForSingleObject(processHandle, 100);
        if (wait == 0) return;
        if (wait == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not wait for the supervised process after launch failure.");
        var terminateError = 0;
        if (!TerminateProcess(processHandle, 1))
        {
            terminateError = Marshal.GetLastWin32Error();
            if (WaitForSingleObject(processHandle, 10000) == 0) return;
            throw new Win32Exception(terminateError, "Could not terminate the supervised process after launch failure.");
        }
        wait = WaitForSingleObject(processHandle, 10000);
        if (wait != 0)
            throw new Win32Exception(
                wait == uint.MaxValue ? Marshal.GetLastWin32Error() : 0,
                "The supervised process did not reach a proven exit after launch failure.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeStartupInfo { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref NativeStartupInfo startupInfo, out NativeProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}

internal sealed class PhysicalProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int BasicAccountingInformationClass = 1;
    private const int ExtendedLimitInformationClass = 9;
    private readonly SafeFileHandle _handle;

    private PhysicalProcessJob(SafeFileHandle handle) => _handle = handle;

    public static PhysicalProcessJob Assign(Process process)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the supervised process job.");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, pointer, (uint)size) ||
                !AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind the supervised process to its kill-on-close job.");
            return new PhysicalProcessJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public async Task TerminateAndRequireEmptyAsync()
    {
        if (!TerminateJobObject(_handle, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate the supervised process job.");
        await RequireEmptyAsync().ConfigureAwait(false);
    }

    public async Task TerminateRemainingAndRequireEmptyAsync()
    {
        if (ActiveProcesses() != 0 && !TerminateJobObject(_handle, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate remaining supervised descendants.");
        await RequireEmptyAsync().ConfigureAwait(false);
    }

    private async Task RequireEmptyAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (ActiveProcesses() != 0)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Supervised process job still contains live processes after termination.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private uint ActiveProcesses()
    {
        if (!QueryInformationJobObject(_handle, BasicAccountingInformationClass, out JobObjectBasicAccountingInformationData data,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationData>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify the supervised process job is empty.");
        return data.ActiveProcesses;
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationData
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobObjectBasicAccountingInformationData info, uint length, IntPtr returnLength);
}

internal interface ITrustedPhysicalPathInspector
{
    FileAttributes GetAttributes(string path);
}

internal sealed class WindowsTrustedPhysicalPathInspector : ITrustedPhysicalPathInspector
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}

internal static class TrustedPhysicalPath
{
    private const uint OpenExisting = 3;
    private const uint FileReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);

    public static string RequireContainedNoReparse(
        string root,
        string path,
        string description,
        ITrustedPhysicalPathInspector? inspector = null)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var canonicalPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"{description} escapes its trusted root.");
        RequireNoReparse(canonicalRoot, $"{description} root", inspector);
        RequireNoReparse(canonicalPath, description, inspector);
        return canonicalPath;
    }

    public static string RequireNoReparse(
        string path,
        string description,
        ITrustedPhysicalPathInspector? inspector = null)
    {
        var canonical = Path.GetFullPath(path);
        inspector ??= new WindowsTrustedPhysicalPathInspector();
        var ancestors = new Stack<string>();
        for (string? current = canonical; current is not null; current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
        }
        foreach (var candidate in ancestors)
        {
            FileAttributes attributes;
            try
            {
                attributes = inspector.GetAttributes(candidate);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"{description} reparse inspection failed closed.", ex);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{description} contains a reparse point and is not trusted.");
        }
        return canonical;
    }

    public static string CreateDirectoryContainedNoReparse(string existingRoot, string path, string description)
    {
        var root = Path.GetFullPath(existingRoot);
        var target = RequireContainedNoReparse(root, path, description);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"{description} trusted root is missing: {root}.");
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, component);
            using var parentLease = AcquireContainedNoReparseLease(root, current, $"{description} parent");
            if (!Directory.Exists(next))
                Directory.CreateDirectory(next);
            RequireContainedNoReparse(root, next, description);
            parentLease.Verify();
            current = next;
        }
        return target;
    }

    public static string CreateDirectoryNoReparse(string path, string description)
    {
        var target = Path.GetFullPath(path);
        var existing = target;
        while (!Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing)
                       ?? throw new DirectoryNotFoundException($"Could not locate an existing trusted ancestor for {description}.");
        RequireNoReparse(existing, $"{description} existing ancestor");
        return CreateDirectoryContainedNoReparse(existing, target, description);
    }

    public static StablePathLease AcquireContainedNoReparseLease(string root, string path, string description)
        => AcquireContainedNoReparseLease(root, path, description, requireTargetExists: false);

    public static StablePathLease AcquireExistingContainedNoReparseLease(string root, string path, string description)
        => AcquireContainedNoReparseLease(root, path, description, requireTargetExists: true);

    private static StablePathLease AcquireContainedNoReparseLease(
        string root,
        string path,
        string description,
        bool requireTargetExists)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Stable physical path leases are Windows-only.");
        var canonical = RequireContainedNoReparse(root, path, description);
        var handles = new List<StablePathHandle>();
        try
        {
            var ancestors = new Stack<string>();
            for (string? current = canonical; current is not null; current = Path.GetDirectoryName(current))
            {
                ancestors.Push(current);
                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
            }
            foreach (var candidate in ancestors)
            {
                var handle = CreateFileW(
                    ToExtendedLengthForm(candidate),
                    desiredAccess: FileReadAttributes,
                    FileShare.Read,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is ErrorFileNotFound or ErrorPathNotFound)
                    {
                        if (requireTargetExists && string.Equals(candidate, canonical, StringComparison.OrdinalIgnoreCase))
                            throw new FileNotFoundException($"{description} expected target is missing.", canonical);
                        continue;
                    }
                    throw new Win32Exception(error, $"{description} stable handle acquisition failed closed.");
                }
                if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, $"{description} stable handle inspection failed closed.");
                }
                if ((info.FileAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    handle.Dispose();
                    throw new InvalidDataException($"{description} contains a reparse point and is not trusted.");
                }
                if ((info.FileAttributes & FileAttributes.Directory) == 0)
                {
                    handle.Dispose();
                    handle = CreateFileW(
                        ToExtendedLengthForm(candidate),
                        GenericRead,
                        FileShare.Read,
                        IntPtr.Zero,
                        OpenExisting,
                        FileFlagOpenReparsePoint,
                        IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new Win32Exception(error, $"{description} stable read handle acquisition failed closed.");
                    }
                    if (!GetFileInformationByHandleEx(handle, 9, out info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                        (info.FileAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new InvalidDataException($"{description} stable read handle inspection failed closed.");
                    }
                }
                handles.Add(new StablePathHandle(candidate, FinalPath(handle), handle));
            }
            RequireContainedNoReparse(root, path, description);
            return new StablePathLease(handles);
        }
        catch
        {
            foreach (var entry in handles)
                entry.Handle.Dispose();
            throw;
        }
    }

    internal sealed class StablePathLease(List<StablePathHandle> handles) : IDisposable
    {
        private List<StablePathHandle>? _handles = handles;

        public void Verify()
        {
            var current = _handles ?? throw new ObjectDisposedException(nameof(StablePathLease));
            foreach (var entry in current)
            {
                var observed = FinalPath(entry.Handle);
                if (!string.Equals(entry.ExpectedFinalPath, observed, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Stable physical path identity changed during the protected operation.");
            }
        }

        public void Dispose()
        {
            var handlesToDispose = Interlocked.Exchange(ref _handles, null);
            if (handlesToDispose is null)
                return;
            foreach (var entry in handlesToDispose)
                entry.Handle.Dispose();
        }
    }

    internal sealed record StablePathHandle(string OriginalPath, string ExpectedFinalPath, SafeFileHandle Handle);

    private static string FinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Stable physical path identity verification failed closed.");
        return NormalizeFinalPath(builder.ToString());
    }

    private static string NormalizeFinalPath(string value)
    {
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(@"\\" + value[8..]);
        if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(value[4..]);
        return Path.GetFullPath(value);
    }

    public static string ToExtendedLengthForm(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return full;
        if (full.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }
}

public static class PhysicalOfflineTopologyProbe
{
    public const string SafetyMode = "OFFLINE_ONLY_MTP_NO_PHYSICAL_CATEGORIES_NO_HARDWARE";
    public const string ExactMtpTestName = "Moondrop.PhysicalTests.OfflineTopologyProbeTests.PublishedRunnerCapturesAuthenticatedParentTopology";
    private static readonly string[] RequiredEnvironmentVariables =
    {
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP"
    };
    private static readonly CoherentPhysicalProbeProcessIdentityProvider ProcessIdentities =
        new(new WindowsProbeIdentitySnapshotReader());

    public static PhysicalProcessLaunchPlan CreateMtpRunnerStartInfo(
        string physicalApphostPath,
        string reportPath,
        PhysicalProbeProcessIdentity expectedWatchdog,
        HarnessFingerprint runtimeManifest,
        IReadOnlyDictionary<string, string> systemEnvironment)
    {
        ArgumentNullException.ThrowIfNull(expectedWatchdog);
        ArgumentNullException.ThrowIfNull(runtimeManifest);
        ArgumentNullException.ThrowIfNull(systemEnvironment);
        var apphost = Path.GetFullPath(physicalApphostPath);
        var report = Path.GetFullPath(reportPath);
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(apphost)!)!;
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, apphost, "Offline topology runner apphost");
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, report, "Offline topology report");
        if (!string.Equals(Path.GetFileName(apphost), "Moondrop.PhysicalTests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The offline topology smoke requires the exact published Moondrop.PhysicalTests.exe apphost.");
        var runnerEntry = runtimeManifest.Files.SingleOrDefault(entry =>
            string.Equals(entry.RelativePath, "physical-tests/Moondrop.PhysicalTests.exe", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The complete runtime manifest does not cover the physical runner apphost.");
        var watchdogEntry = runtimeManifest.Files.SingleOrDefault(entry =>
            string.Equals(entry.RelativePath, "watchdog/Moondrop.PhysicalWatchdog.exe", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The complete runtime manifest does not cover the watchdog apphost.");
        if (!string.Equals(watchdogEntry.Sha256, expectedWatchdog.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The expected watchdog identity is not bound to the complete runtime manifest.");
        var environment = new Dictionary<string, string>(
            PhysicalSystemEnvironment.Validate(systemEnvironment, "Offline topology runner"),
            StringComparer.OrdinalIgnoreCase);
        environment.Add("MD_OFFLINE_TOPOLOGY_REPORT", report);
        environment.Add("MD_OFFLINE_TOPOLOGY_WATCHDOG_PID", expectedWatchdog.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        environment.Add("MD_OFFLINE_TOPOLOGY_WATCHDOG_PARENT_PID", expectedWatchdog.ParentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        environment.Add("MD_OFFLINE_TOPOLOGY_WATCHDOG_START_UTC", expectedWatchdog.StartedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        environment.Add("MD_OFFLINE_TOPOLOGY_WATCHDOG_EXE", expectedWatchdog.ExecutablePath);
        environment.Add("MD_OFFLINE_TOPOLOGY_WATCHDOG_SHA256", expectedWatchdog.Sha256);
        environment.Add("MD_OFFLINE_TOPOLOGY_RUNTIME_SHA256", runtimeManifest.AggregateSha256);
        environment.Add("MD_OFFLINE_TOPOLOGY_RUNNER_SHA256", runnerEntry.Sha256);
        var trxPath = Path.Combine(MtpResultsDirectory(report), $"offline-topology-{Guid.NewGuid():N}.trx");
        environment.Add("MD_OFFLINE_TOPOLOGY_TRX", trxPath);
        return new PhysicalProcessLaunchPlan(
            apphost,
            Path.GetDirectoryName(apphost)!,
            [
                "--filter", $"FullyQualifiedName={ExactMtpTestName}",
                "--minimum-expected-tests", "1",
                "--results-directory", MtpResultsDirectory(report),
                "--report-trx",
                "--report-trx-filename", Path.GetFileName(trxPath),
                "--output", "Detailed"
            ],
            environment,
            RedirectStandardOutput: true,
            RedirectStandardError: true);
    }

    public static PhysicalProcessLaunchPlan CreateDeliberateWrapperStartInfo(
        PhysicalProcessLaunchPlan direct,
        DeliberateOfflineWrapperShape shape)
    {
        ArgumentNullException.ThrowIfNull(direct);
        if (!string.Equals(Path.GetFileName(direct.FileName), "Moondrop.PhysicalTests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Deliberate wrapper regression must target the exact physical-test apphost.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var wrapper = shape switch
        {
            DeliberateOfflineWrapperShape.CommandPrompt => Path.Combine(windows, "System32", "cmd.exe"),
            DeliberateOfflineWrapperShape.WindowsPowerShell => Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var arguments = new List<string>();
        var environment = new Dictionary<string, string>(direct.Environment, StringComparer.OrdinalIgnoreCase);
        if (shape == DeliberateOfflineWrapperShape.CommandPrompt)
        {
            static string CommandPromptArgument(string value)
            {
                if (value.IndexOfAny(['\r', '\n', '\0', '\"', '%']) >= 0)
                    throw new InvalidDataException("Command Prompt wrapper arguments cannot contain control, quote, or expansion characters.");
                return $"\"{value}\"";
            }
            const string commandVariable = "MD_OFFLINE_WRAPPED_COMMAND";
            if (environment.ContainsKey(commandVariable))
                throw new InvalidDataException("The deliberate wrapper command environment variable must not already exist.");
            environment[commandVariable] =
                "call " + CommandPromptArgument(direct.FileName) + " " +
                string.Join(' ', direct.ArgumentList.Select(CommandPromptArgument));
            arguments.Add("/d");
            arguments.Add("/v:off");
            arguments.Add("/s");
            arguments.Add("/c");
            arguments.Add($"%{commandVariable}%");
        }
        else
        {
            arguments.Add("-NoLogo");
            arguments.Add("-NoProfile");
            arguments.Add("-NonInteractive");
            arguments.Add("-Command");
            static string PowerShellLiteral(string value) =>
                $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            var nativeArguments = string.Join(' ', direct.ArgumentList.Select(PhysicalProcessLauncher.QuoteWindowsArgument));
            arguments.Add(
                "$startInfo=New-Object Diagnostics.ProcessStartInfo; $startInfo.UseShellExecute=$false; " +
                $"$startInfo.FileName={PowerShellLiteral(direct.FileName)}; " +
                $"$startInfo.Arguments={PowerShellLiteral(nativeArguments)}; " +
                "$child=[Diagnostics.Process]::Start($startInfo); if($null -eq $child){exit 1}; " +
                "try{$child.WaitForExit(); $childExitCode=$child.ExitCode}finally{$child.Dispose()}; exit $childExitCode");
        }
        return new PhysicalProcessLaunchPlan(
            wrapper,
            direct.WorkingDirectory,
            arguments,
            environment,
            RedirectStandardOutput: true,
            RedirectStandardError: true);
    }

    public static async Task WriteObservationAsync(
        string offlineArtifactRoot,
        string reportPath,
        PhysicalOfflineTopologyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var root = Path.GetFullPath(offlineArtifactRoot);
        var report = Path.GetFullPath(reportPath);
        RequireExactOfflineReportPath(root, report);
        TrustedPhysicalPath.RequireNoReparse(root, "Offline topology artifact root");
        TrustedPhysicalPath.RequireContainedNoReparse(root, report, "Offline topology report");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Dedicated offline topology artifact root is missing: {root}.");
        using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, root, "Offline topology artifact root");
        if (File.Exists(report))
            throw new IOException("Refusing to overwrite a pre-existing offline topology report.");
        var temporary = Path.Combine(root, $".{Path.GetFileNameWithoutExtension(report)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, observation).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            lease.Verify();
            TrustedPhysicalPath.RequireContainedNoReparse(root, report, "Offline topology report");
            File.Move(temporary, report, overwrite: false);
            lease.Verify();
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static PhysicalOfflineTopologyObservation ReadObservation(string offlineArtifactRoot, string reportPath)
    {
        var root = Path.GetFullPath(offlineArtifactRoot);
        var report = Path.GetFullPath(reportPath);
        RequireExactOfflineReportPath(root, report);
        TrustedPhysicalPath.RequireNoReparse(root, "Offline topology artifact root");
        TrustedPhysicalPath.RequireContainedNoReparse(root, report, "Offline topology report");
        using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(root, report, "Offline topology report");
        var observation = JsonSerializer.Deserialize<PhysicalOfflineTopologyObservation>(File.ReadAllBytes(report))
                          ?? throw new InvalidDataException("Offline topology observation is empty.");
        lease.Verify();
        return observation;
    }

    private static void RequireExactOfflineReportPath(string root, string report)
    {
        var expected = Path.Combine(root, "observed-topology.json");
        if (!string.Equals(report, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline topology reports are restricted to the canonical dedicated artifact root and filename.");
    }

    public static async Task<PhysicalOfflineTopologyObservation> RunWatchdogAsync(
        string physicalApphostPath,
        string reportPath,
        HarnessFingerprint runtimeManifest)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The offline physical-apphost topology probe is Windows-only.");
        using var watchdogProcess = Process.GetCurrentProcess();
        var watchdog = ProcessIdentities.Get(watchdogProcess.Id);
        if (!string.Equals(Path.GetFileName(watchdog.ExecutablePath), "Moondrop.PhysicalWatchdog.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The offline topology probe must run through the real published Moondrop.PhysicalWatchdog.exe apphost.");
        var systemEnvironment = RequiredEnvironmentVariables.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name)
                    ?? throw new InvalidDataException($"Required Windows environment variable {name} is missing."),
            StringComparer.Ordinal);
        var offlineRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(physicalApphostPath)))!, "offline-topology");
        RequireExactOfflineReportPath(offlineRoot, Path.GetFullPath(reportPath));
        var startInfo = CreateMtpRunnerStartInfo(physicalApphostPath, reportPath, watchdog, runtimeManifest, systemEnvironment);
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(Path.GetDirectoryName(offlineRoot)!, offlineRoot, "Offline topology artifact root");
        using var mtpEvidence = PrepareMtpEvidence(reportPath, startInfo.Environment["MD_OFFLINE_TOPOLOGY_TRX"]);
        using var offlineLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(offlineRoot, offlineRoot, "Offline topology artifact root");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var childId = 0;
        var childResult = await PhysicalProcessLauncher.RunToExitAsync(
            startInfo,
            timeout.Token,
            processId => childId = processId).ConfigureAwait(false);
        var observation = ReadObservation(offlineRoot, reportPath);
        if (childResult.ExitCode != 0)
            throw new InvalidDataException($"Offline physical-test MTP smoke exited {childResult.ExitCode}; predicate={DiagnosticText.Sanitize(observation.Predicate)}. stdout={DiagnosticText.Sanitize(childResult.StandardOutput)}; stderr={DiagnosticText.Sanitize(childResult.StandardError)}");
        mtpEvidence.RequireExactlyOne(ExactMtpTestName, "Passed");
        RequireDirectPublishedApphostTopology(observation, watchdog.ExecutablePath, physicalApphostPath);
        if (!observation.MtpEntered || !string.Equals(observation.TestFullyQualifiedName, ExactMtpTestName, StringComparison.Ordinal))
            throw new InvalidDataException("Offline topology evidence was not produced inside the exact MTP-executed regression test.");
        if (observation.Watchdog.ProcessId != watchdog.ProcessId ||
            observation.Watchdog.ParentProcessId != watchdog.ParentProcessId ||
            observation.Watchdog.StartedAtUtc != watchdog.StartedAtUtc ||
            !string.Equals(observation.Watchdog.Sha256, watchdog.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Offline topology observation did not match the live watchdog process identity.");
        if (observation.PhysicalRunner.ProcessId != childId)
            throw new InvalidDataException("Offline topology observation did not match the exact launched physical-test PID.");
        foreach (var shape in Enum.GetValues<DeliberateOfflineWrapperShape>())
            await RequireDeliberateWrapperRejectedAsync(
                shape,
                offlineRoot,
                physicalApphostPath,
                watchdog,
                runtimeManifest,
                systemEnvironment).ConfigureAwait(false);
        offlineLease.Verify();
        return observation;
    }

    private static async Task RequireDeliberateWrapperRejectedAsync(
        DeliberateOfflineWrapperShape shape,
        string offlineRoot,
        string physicalApphostPath,
        PhysicalProbeProcessIdentity expectedWatchdog,
        HarnessFingerprint runtimeManifest,
        IReadOnlyDictionary<string, string> systemEnvironment)
    {
        var shapeName = shape == DeliberateOfflineWrapperShape.CommandPrompt ? "cmd" : "powershell";
        var reportRoot = Path.Combine(offlineRoot, $"wrapper-{shapeName}");
        var report = Path.Combine(reportRoot, "observed-topology.json");
        var direct = CreateMtpRunnerStartInfo(physicalApphostPath, report, expectedWatchdog, runtimeManifest, systemEnvironment);
        var wrapped = CreateDeliberateWrapperStartInfo(direct, shape);
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(offlineRoot, reportRoot, $"Offline {shapeName} wrapper report root");
        using var mtpEvidence = PrepareMtpEvidence(report, direct.Environment["MD_OFFLINE_TOPOLOGY_TRX"]);
        using var reportLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(reportRoot, reportRoot, $"Offline {shapeName} wrapper report root");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var wrapperResult = await PhysicalProcessLauncher.RunToExitAsync(wrapped, timeout.Token).ConfigureAwait(false);
        if (wrapperResult.ExitCode == 0)
            throw new InvalidDataException($"Deliberate {shapeName} wrapper unexpectedly succeeded. stdout={DiagnosticText.Sanitize(wrapperResult.StandardOutput)}; stderr={DiagnosticText.Sanitize(wrapperResult.StandardError)}");
        mtpEvidence.RequireExactlyOne(ExactMtpTestName, "Failed", allowMtpDeploymentDirectory: true);
        var rejected = ReadObservation(reportRoot, report);
        if (!rejected.MtpEntered || rejected.IsAccepted ||
            !string.Equals(rejected.TestFullyQualifiedName, ExactMtpTestName, StringComparison.Ordinal) ||
            !string.Equals(rejected.Predicate, "direct-parent-pid", StringComparison.Ordinal) ||
            rejected.ActualParent is null ||
            !string.Equals(Path.GetFullPath(rejected.ActualParent.ExecutablePath), Path.GetFullPath(wrapped.FileName), StringComparison.OrdinalIgnoreCase) ||
            rejected.PhysicalRunner.ParentProcessId != rejected.ActualParent.ProcessId)
            throw new InvalidDataException($"Deliberate {shapeName} wrapper did not retain the exact structured rejection predicate and chain.");
        reportLease.Verify();
    }

    private static string MtpResultsDirectory(string reportPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "mtp-results");

    internal static PreparedMtpEvidence PrepareMtpEvidence(string reportPath) =>
        PrepareMtpEvidence(
            reportPath,
            Path.Combine(MtpResultsDirectory(reportPath), $"offline-topology-{Guid.NewGuid():N}.trx"));

    private static PreparedMtpEvidence PrepareMtpEvidence(string reportPath, string trxPath)
    {
        var reportRoot = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        var results = MtpResultsDirectory(reportPath);
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(reportRoot, results, "Offline MTP results directory");
        if (Directory.EnumerateFileSystemEntries(results).Any())
            throw new InvalidDataException("Offline MTP results directory must be empty before launch.");
        var canonicalTrx = TrustedPhysicalPath.RequireContainedNoReparse(results, trxPath, "Offline MTP TRX");
        return new PreparedMtpEvidence(results, canonicalTrx,
            TrustedPhysicalPath.AcquireContainedNoReparseLease(results, results, "Offline MTP results directory"));
    }

    internal sealed class PreparedMtpEvidence(string resultsDirectory, string trxPath, TrustedPhysicalPath.StablePathLease lease) : IDisposable
    {
        internal string TrxPath => trxPath;

        public void RequireExactlyOne(
            string expectedTestFullyQualifiedName,
            string expectedOutcome,
            Action? afterParseBeforeFinalEnumeration = null,
            bool allowMtpDeploymentDirectory = false,
            Action? beforeTrxLeaseAttempt = null)
        {
            lease.Verify();
            using var trxLease = RequireTrxTargetPresent(beforeTrxLeaseAttempt);
            using var preParseEvidence = CaptureAuthoritativeEvidenceEntries(allowMtpDeploymentDirectory);
            RequireExactlyOneMtpTest(trxPath, expectedTestFullyQualifiedName, expectedOutcome);
            afterParseBeforeFinalEnumeration?.Invoke();
            trxLease.Verify();
            preParseEvidence.Verify();
            lease.Verify();
            var finalEntries = EnumerateAuthoritativeEvidenceEntries(allowMtpDeploymentDirectory, afterParsing: true);
            if (!preParseEvidence.Paths.SequenceEqual(finalEntries, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Offline MTP results directory changed while authoritative TRX evidence was accepted.");
        }

        private TrustedPhysicalPath.StablePathLease RequireTrxTargetPresent(Action? beforeTrxLeaseAttempt)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                beforeTrxLeaseAttempt?.Invoke();
                try
                {
                    return TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(resultsDirectory, trxPath, "Offline MTP TRX");
                }
                catch (FileNotFoundException) when (DateTimeOffset.UtcNow >= deadline)
                {
                    throw;
                }
                catch (FileNotFoundException)
                {
                    TrustedPhysicalPath.RequireContainedNoReparse(resultsDirectory, trxPath, "Offline MTP TRX");
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        private AcceptedEvidenceEntries CaptureAuthoritativeEvidenceEntries(bool allowMtpDeploymentDirectory)
        {
            var discoveredEntries = EnumerateAuthoritativeEvidenceEntries(allowMtpDeploymentDirectory, afterParsing: false);
            var deploymentLeases = discoveredEntries
                .Where(entry => !string.Equals(entry, trxPath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => TrustedPhysicalPath.AcquireExistingContainedNoReparseLease(
                    resultsDirectory,
                    entry,
                    "Offline MTP deployment directory"))
                .ToArray();
            try
            {
                var acceptedEntries = EnumerateAuthoritativeEvidenceEntries(allowMtpDeploymentDirectory, afterParsing: false);
                if (!discoveredEntries.SequenceEqual(acceptedEntries, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Offline MTP results directory changed while authoritative evidence was being leased.");
                return new AcceptedEvidenceEntries(acceptedEntries, deploymentLeases);
            }
            catch
            {
                foreach (var deploymentLease in deploymentLeases)
                    deploymentLease.Dispose();
                throw;
            }
        }

        private string[] EnumerateAuthoritativeEvidenceEntries(bool allowMtpDeploymentDirectory, bool afterParsing)
        {
            var entries = Directory.EnumerateFileSystemEntries(resultsDirectory)
                .Select(Path.GetFullPath)
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var deploymentDirectoryCount = 0;
            foreach (var entry in entries)
            {
                if (string.Equals(entry, trxPath, StringComparison.OrdinalIgnoreCase))
                {
                    TrustedPhysicalPath.RequireContainedNoReparse(resultsDirectory, trxPath, "Offline MTP TRX");
                    continue;
                }
                if (allowMtpDeploymentDirectory &&
                    Directory.Exists(entry) &&
                    Path.GetFileName(entry).StartsWith("Deploy_ ", StringComparison.Ordinal))
                {
                    TrustedPhysicalPath.RequireContainedNoReparse(resultsDirectory, entry, "Offline MTP deployment directory");
                    deploymentDirectoryCount++;
                    continue;
                }
                throw new InvalidDataException(afterParsing
                    ? "Offline MTP results directory changed while authoritative TRX evidence was accepted."
                    : "Offline MTP results directory contains an unexpected evidence entry.");
            }
            if (!entries.Any(entry => string.Equals(entry, trxPath, StringComparison.OrdinalIgnoreCase)) || deploymentDirectoryCount > 1)
                throw new InvalidDataException(afterParsing
                    ? "Offline MTP results directory changed while authoritative TRX evidence was accepted."
                    : "Offline MTP results directory must contain only the unique authoritative TRX and, only for deliberate wrapper rejection, one MTP deployment directory.");
            return entries;
        }

        private sealed class AcceptedEvidenceEntries(
            string[] paths,
            IReadOnlyList<TrustedPhysicalPath.StablePathLease> deploymentLeases) : IDisposable
        {
            internal string[] Paths => paths;

            internal void Verify()
            {
                foreach (var deploymentLease in deploymentLeases)
                    deploymentLease.Verify();
            }

            public void Dispose()
            {
                foreach (var deploymentLease in deploymentLeases)
                    deploymentLease.Dispose();
            }
        }

        public void Dispose() => lease.Dispose();
    }

    public static void RequireExactlyOneMtpTest(
        string trxPath,
        string expectedTestFullyQualifiedName,
        string expectedOutcome)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(Path.GetFullPath(trxPath), settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var trxNamespace = XNamespace.Get("http://microsoft.com/schemas/VisualStudio/TeamTest/2010");
        var testRun = document.Root;
        if (testRun is null || testRun.Name != trxNamespace + "TestRun")
            throw new InvalidDataException("Offline MTP TRX must have the canonical TestRun root and namespace.");
        var resultContainers = testRun.Elements(trxNamespace + "Results").ToArray();
        var definitionContainers = testRun.Elements(trxNamespace + "TestDefinitions").ToArray();
        var summaries = testRun.Elements(trxNamespace + "ResultSummary").ToArray();
        if (resultContainers.Length != 1 || definitionContainers.Length != 1 || summaries.Length != 1)
            throw new InvalidDataException("Offline MTP TRX must contain exactly one canonical results, definitions, and summary container.");
        var counters = summaries[0].Elements(trxNamespace + "Counters").SingleOrDefault()
                       ?? throw new InvalidDataException("Offline MTP TRX is missing result counters.");
        static int Counter(XElement element, string name) =>
            int.TryParse(element.Attribute(name)?.Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new InvalidDataException($"Offline MTP TRX counter {name} is missing or malformed.");
        var results = resultContainers[0].Elements(trxNamespace + "UnitTestResult").ToArray();
        if (Counter(counters, "total") != 1 || Counter(counters, "executed") != 1 || results.Length != 1)
            throw new InvalidDataException("Offline MTP topology smoke must contain exactly one selected and executed test result.");
        var result = results[0];
        var resultTestName = result.Attribute("testName")?.Value;
        var actualTestFullyQualifiedName = resultTestName;
        var resultTestIdAttribute = result.Attribute("testId");
        if (resultTestIdAttribute is not null)
        {
            var resultTestId = resultTestIdAttribute.Value;
            if (string.IsNullOrWhiteSpace(resultTestId))
                throw new InvalidDataException("Offline MTP TRX selected result has a blank test ID.");
            var matchingDefinitions = definitionContainers[0].Elements()
                .Where(element => element.Name == trxNamespace + "UnitTest" &&
                                  string.Equals(element.Attribute("id")?.Value, resultTestId, StringComparison.Ordinal))
                .ToArray();
            if (matchingDefinitions.Length != 1)
                throw new InvalidDataException("Offline MTP TRX must map the selected result to exactly one test definition.");
            var methods = matchingDefinitions[0].Elements()
                .Where(element => element.Name == trxNamespace + "TestMethod")
                .ToArray();
            if (methods.Length != 1)
                throw new InvalidDataException("Offline MTP TRX test definition must contain exactly one test method.");
            var className = methods[0].Attribute("className")?.Value;
            var methodName = methods[0].Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(methodName))
                throw new InvalidDataException("Offline MTP TRX test definition is missing its class or method identity.");
            actualTestFullyQualifiedName = $"{className}.{methodName}";
        }
        if (!string.Equals(actualTestFullyQualifiedName, expectedTestFullyQualifiedName, StringComparison.Ordinal) ||
            !string.Equals(result.Attribute("outcome")?.Value, expectedOutcome, StringComparison.Ordinal))
            throw new InvalidDataException("Offline MTP topology smoke did not execute the exact expected test and outcome.");
    }

    public static async Task RunMtpTestAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The offline MTP topology test is Windows-only.");
        var report = Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_REPORT")
                     ?? throw new InvalidDataException("The offline MTP topology report path is missing.");
        var expectedWatchdog = new PhysicalProbeProcessIdentity(
            int.Parse(Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_WATCHDOG_PID") ?? "", System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_WATCHDOG_PARENT_PID") ?? "", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_WATCHDOG_START_UTC") ?? "", System.Globalization.CultureInfo.InvariantCulture),
            Path.GetFullPath(Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_WATCHDOG_EXE") ?? ""),
            Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_WATCHDOG_SHA256") ?? "");
        using var current = Process.GetCurrentProcess();
        var runner = ProcessIdentities.Get(current.Id);
        var actualParent = ProcessIdentities.Get(runner.ParentProcessId);
        var expectedRunnerSha = Environment.GetEnvironmentVariable("MD_OFFLINE_TOPOLOGY_RUNNER_SHA256") ?? "";
        var predicate = !string.Equals(runner.Sha256, expectedRunnerSha, StringComparison.Ordinal)
            ? "runner-manifest-sha256"
            : actualParent.ProcessId != expectedWatchdog.ProcessId
                ? "direct-parent-pid"
                : actualParent.StartedAtUtc != expectedWatchdog.StartedAtUtc
                    ? "watchdog-start-time"
                    : !string.Equals(actualParent.ExecutablePath, expectedWatchdog.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        ? "watchdog-executable-path"
                        : !string.Equals(actualParent.Sha256, expectedWatchdog.Sha256, StringComparison.Ordinal)
                            ? "watchdog-manifest-sha256"
                            : "direct-parent";
        var accepted = string.Equals(predicate, "direct-parent", StringComparison.Ordinal);
        var observation = new PhysicalOfflineTopologyObservation(
            SafetyMode,
            expectedWatchdog,
            runner,
            actualParent,
            MtpEntered: true,
            ExactMtpTestName,
            accepted,
            predicate);
        await WriteObservationAsync(Path.GetDirectoryName(report)!, report, observation).ConfigureAwait(false);
        if (!accepted)
            throw new InvalidDataException($"Offline MTP topology rejected; predicate={predicate}.");
    }

    public static void RequireDirectPublishedApphostTopology(
        PhysicalOfflineTopologyObservation observation,
        string expectedWatchdogPath,
        string expectedPhysicalApphostPath)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var watchdog = Path.GetFullPath(expectedWatchdogPath);
        var runner = Path.GetFullPath(expectedPhysicalApphostPath);
        if (!string.Equals(observation.SafetyMode, SafetyMode, StringComparison.Ordinal))
            throw new InvalidDataException("Offline topology observation did not carry the structural MTP/no-hardware sentinel.");
        if (!string.Equals(Path.GetFullPath(observation.Watchdog.ExecutablePath), watchdog, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(watchdog), "Moondrop.PhysicalWatchdog.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline topology observation did not identify the exact published watchdog apphost.");
        if (!string.Equals(Path.GetFullPath(observation.PhysicalRunner.ExecutablePath), runner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(runner), "Moondrop.PhysicalTests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline topology observation did not identify the exact published physical-test apphost.");
        if (!observation.IsAccepted)
            throw new InvalidDataException($"Offline topology observation was rejected; predicate={observation.Predicate}.");
        var actualParent = observation.ActualParent ?? observation.Watchdog;
        if (observation.PhysicalRunner.ParentProcessId != observation.Watchdog.ProcessId ||
            actualParent.ProcessId != observation.Watchdog.ProcessId ||
            actualParent.StartedAtUtc != observation.Watchdog.StartedAtUtc ||
            !string.Equals(actualParent.ExecutablePath, observation.Watchdog.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualParent.Sha256, observation.Watchdog.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Offline topology observation contains an intermediary between the physical-test apphost and watchdog apphost.");
        if (!IsSha256(observation.Watchdog.Sha256) || !IsSha256(observation.PhysicalRunner.Sha256))
            throw new InvalidDataException("Offline topology observation is missing an apphost SHA-256 identity.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static PhysicalProbeProcessIdentity CaptureSnapshot(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var executable = Path.GetFullPath(
            process.MainModule?.FileName
            ?? throw new InvalidDataException($"Could not resolve executable path for probe PID {processId}."));
        return new PhysicalProbeProcessIdentity(
            processId,
            GetParentProcessId(process),
            process.StartTime.ToUniversalTime(),
            executable,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))));
    }

    private sealed class WindowsProbeIdentitySnapshotReader : IPhysicalProbeProcessIdentitySnapshotReader
    {
        public PhysicalProbeProcessIdentity Read(int processId) => CaptureSnapshot(processId);
    }

    private static int GetParentProcessId(Process process)
    {
        var information = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            ref information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0)
            throw new InvalidDataException($"NtQueryInformationProcess failed for PID {process.Id} with status 0x{status:X8}.");
        return checked((int)information.InheritedFromUniqueProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}

public static class PhysicalWatchdogPolicy
{
    public const int MaximumRecoveryAttempts = 3;
    public sealed record CombinedResult(int ExitCode, string Summary);

    public static bool ShouldLaunchRecovery(DurablePhysicalPhase phase) =>
        phase is not DurablePhysicalPhase.Prepared and not DurablePhysicalPhase.Completed;

    public static bool ShouldRetryRecovery(int completedAttempt, int exitCode, DurablePhysicalPhase phase) =>
        completedAttempt < MaximumRecoveryAttempts &&
        (exitCode != 0 || phase != DurablePhysicalPhase.Completed) &&
        ShouldLaunchRecovery(phase);

    public static TimeSpan InactivityLimit(DurablePhysicalPhase phase) => phase is
        DurablePhysicalPhase.AwaitingTemporaryPhysicalCycle or
        DurablePhysicalPhase.AwaitingRestorationPhysicalCycle
            ? TimeSpan.FromMinutes(6)
            : TimeSpan.FromSeconds(15);

    public static TimeSpan InactivityLimit(DurablePhysicalPhase phase, string? heartbeatKind) =>
        string.Equals(heartbeatKind, "PhysicalCycleWaiting", StringComparison.Ordinal)
            ? TimeSpan.FromMinutes(6)
            : TimeSpan.FromSeconds(15);

    public static bool CanTerminate(OwnedPhysicalProcess expected, ObservedPhysicalProcess observed) =>
        expected.ProcessId == observed.ProcessId &&
        expected.StartedAtUtc == observed.StartedAtUtc &&
        observed.CommandLine.Contains("Moondrop.PhysicalTests", StringComparison.OrdinalIgnoreCase) &&
        observed.CommandLine.Contains(expected.OwnershipToken, StringComparison.Ordinal) &&
        observed.CommandLine.Contains(expected.ProjectPath, StringComparison.OrdinalIgnoreCase);

    public static CombinedResult CombineExecuteAndRecovery(
        int executeExitCode,
        int recoveryExitCode,
        DurablePhysicalPhase finalPhase)
    {
        var verified = recoveryExitCode == 0 && finalPhase == DurablePhysicalPhase.Completed;
        return new CombinedResult(
            executeExitCode == 0 ? 1 : executeExitCode,
            verified ? "EXECUTE FAILED; RECOVERY VERIFIED" : "EXECUTE FAILED; RECOVERY FAILED");
    }

    public static CombinedResult CombineExecuteAndRecovery(
        int executeExitCode,
        int recoveryExitCode,
        DurableSessionState initialState,
        DurableSessionState finalState)
    {
        var verified = FinalizeChildExit(recoveryExitCode, initialState, finalState) == 0;
        return new CombinedResult(
            executeExitCode == 0 ? 1 : executeExitCode,
            verified ? "EXECUTE FAILED; RECOVERY VERIFIED" : "EXECUTE FAILED; RECOVERY FAILED");
    }

    public static int FinalizeChildExit(
        int childExitCode,
        DurableSessionState initialState,
        DurableSessionState finalState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(finalState);
        if (childExitCode != 0)
            return childExitCode;
        return finalState.Phase == DurablePhysicalPhase.Completed && SameLineage(initialState, finalState)
            ? 0
            : 1;
    }

    public static bool SameLineage(DurableSessionState expected, DurableSessionState actual) =>
        string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal) &&
        string.Equals(expected.OneRunToken, actual.OneRunToken, StringComparison.Ordinal) &&
        string.Equals(expected.SourceFingerprint, actual.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(expected.RuntimeManifestSha256, actual.RuntimeManifestSha256, StringComparison.Ordinal) &&
        string.Equals(expected.LineageFingerprint, actual.LineageFingerprint, StringComparison.Ordinal);
}

public sealed record DurableSessionState(
    DurablePhysicalPhase Phase,
    DateTimeOffset UpdatedAtUtc,
    string SessionId = "",
    string OneRunToken = "",
    string SourceFingerprint = "",
    string RuntimeManifestSha256 = "",
    string LineageFingerprint = "");

public static class DurableLineageFingerprint
{
    public static string Compute(
        int schemaVersion,
        string sessionId,
        string oneRunToken,
        string sourceFingerprint,
        string runtimeManifestSha256,
        JsonElement original,
        JsonElement plan)
    {
        var immutable = string.Join('\n',
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sessionId,
            oneRunToken,
            sourceFingerprint,
            runtimeManifestSha256,
            CanonicalJson(original),
            CanonicalJson(plan));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(immutable)));
    }

    private static string CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteCanonical(writer, item);
            writer.WriteEndArray();
            return;
        }
        element.WriteTo(writer);
    }
}

public static class DurableSessionReader
{
    public static DurableSessionState ReadNewest(string primaryPath)
    {
        var primary = Path.GetFullPath(primaryPath);
        var recovery = Path.ChangeExtension(primary, ".recovery.json");
        var candidates = new[] { primary, recovery }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();
        if (candidates.Length == 0)
            throw new FileNotFoundException("Neither the primary physical session nor its recovery copy exists.", primary);

        var valid = new List<(string Path, DurableSessionState State)>();
        var failures = new List<Exception>();
        foreach (var candidate in candidates)
        {
            try
            {
                valid.Add((candidate, Read(candidate)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or
                                       FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                failures.Add(new InvalidDataException($"Could not validate durable session candidate {candidate}.", ex));
            }
        }
        if (valid.Count == 0)
            throw new AggregateException("No valid durable physical session publication could be recovered.", failures);
        if (valid.Select(candidate => candidate.State.LineageFingerprint).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("Valid durable session publications have divergent immutable lineage; refusing timestamp arbitration.");

        return valid
            .OrderByDescending(candidate => candidate.State.UpdatedAtUtc)
            .ThenBy(candidate => string.Equals(candidate.Path, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First()
            .State;
    }

    private static DurableSessionState Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var schemaVersion = root.GetProperty("SchemaVersion").GetInt32();
        if (schemaVersion != 3)
            throw new InvalidDataException($"Unsupported durable physical session schema {schemaVersion}.");
        var phaseElement = root.GetProperty("Phase");
        var phase = phaseElement.ValueKind == JsonValueKind.String
            ? Enum.Parse<DurablePhysicalPhase>(phaseElement.GetString()!, ignoreCase: false)
            : (DurablePhysicalPhase)phaseElement.GetInt32();
        if (!Enum.IsDefined(phase))
            throw new InvalidDataException($"Unknown durable physical phase {phaseElement}.");
        var sessionId = RequiredString(root, "SessionId");
        var token = RequiredString(root, "OneRunToken");
        var sourceFingerprint = RequiredString(root, "SourceFingerprint");
        var runtimeManifestSha256 = RequiredString(root, "RuntimeManifestSha256");
        if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Durable session identity must be exactly 32 hexadecimal characters.");
        if (sourceFingerprint.Length != 64 || !sourceFingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Durable session source fingerprint must be exactly 64 hexadecimal characters.");
        if (runtimeManifestSha256.Length != 64 || !runtimeManifestSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Durable session runtime manifest must be exactly 64 hexadecimal characters.");
        if (root.GetProperty("Original").ValueKind != JsonValueKind.Object ||
            root.GetProperty("Plan").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Durable session original snapshot and transition plan must be JSON objects.");
        var lineage = DurableLineageFingerprint.Compute(
            schemaVersion,
            sessionId,
            token,
            sourceFingerprint,
            runtimeManifestSha256,
            root.GetProperty("Original"),
            root.GetProperty("Plan"));
        return new DurableSessionState(
            phase,
            root.GetProperty("UpdatedAtUtc").GetDateTimeOffset(),
            sessionId,
            token,
            sourceFingerprint,
            runtimeManifestSha256,
            lineage);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Durable session {name} is missing.")
            : value;
    }
}

public sealed record HarnessFingerprintEntry(string RelativePath, string Sha256);

public sealed record HarnessFingerprint(string Algorithm, string AggregateSha256, IReadOnlyList<HarnessFingerprintEntry> Files);

public sealed record RuntimeApphostExpectedIdentities(
    string RunnerPath,
    string RunnerSha256,
    string WatchdogPath,
    string WatchdogSha256);

public static class RuntimeApphostManifestBinding
{
    public const string RunnerEntry = "physical-tests/Moondrop.PhysicalTests.exe";
    public const string WatchdogEntry = "watchdog/Moondrop.PhysicalWatchdog.exe";

    public static HarnessFingerprint CreateManifest(string runnerSha256, string watchdogSha256)
    {
        var files = new[]
        {
            new HarnessFingerprintEntry(RunnerEntry, runnerSha256),
            new HarnessFingerprintEntry(WatchdogEntry, watchdogSha256)
        }.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        return new HarnessFingerprint("SHA-256", Aggregate(files), files);
    }

    public static void Require(
        string expectedRuntimeAggregateSha256,
        HarnessFingerprint runtimeManifest,
        string runnerApphostPath,
        string watchdogApphostPath,
        string? forgedHeartbeatWatchdogSha256 = null)
    {
        _ = forgedHeartbeatWatchdogSha256;
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(runnerApphostPath))!)!;
        var expected = ResolveExpectedIdentities(expectedRuntimeAggregateSha256, runtimeManifest, runtimeRoot);
        if (!string.Equals(Path.GetFullPath(runnerApphostPath), expected.RunnerPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"predicate=runner-manifest-path; expected.runner.path={expected.RunnerPath}; actual.runner.path={Path.GetFullPath(runnerApphostPath)}");
        var actualWatchdogPath = Path.GetFullPath(watchdogApphostPath);
        var watchdogRuntimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(actualWatchdogPath))!;
        if (!string.Equals(actualWatchdogPath, expected.WatchdogPath, StringComparison.OrdinalIgnoreCase))
        {
            // The supervising watchdog may legitimately live in a different tree of the SAME
            // approved runtime (for example the session prepare tree vs the fresh execute tree
            // built during EXECUTE). Its own tree must carry the identical approved runtime
            // manifest; otherwise this is an untrusted watchdog and the lineage gate fails closed.
            var watchdogManifestPath = Path.Combine(watchdogRuntimeRoot, PhysicalRuntimeManifestStore.FileName);
            using var watchdogManifestLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(
                watchdogRuntimeRoot, watchdogManifestPath, "Physical watchdog runtime manifest");
            var watchdogManifest = PhysicalRuntimeManifestStore.ReadStrict(watchdogRuntimeRoot, watchdogManifestPath);
            var watchdogExpected = ResolveExpectedIdentities(expectedRuntimeAggregateSha256, watchdogManifest, watchdogRuntimeRoot);
            watchdogManifestLease.Verify();
            if (!string.Equals(actualWatchdogPath, watchdogExpected.WatchdogPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"predicate=watchdog-manifest-path; expected.watchdog.path={expected.WatchdogPath}; actual.watchdog.path={actualWatchdogPath}");
        }
        using var runnerLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(runtimeRoot, runnerApphostPath, "Physical runner apphost binding");
        using var watchdogLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(watchdogRuntimeRoot, watchdogApphostPath, "Physical watchdog apphost binding");
        var actualRunnerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(runnerApphostPath))));
        if (!string.Equals(expected.RunnerSha256, actualRunnerHash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"predicate=runner-manifest-sha256; expected.runner.sha256={expected.RunnerSha256}; actual.runner.sha256={actualRunnerHash}");
        var actualWatchdogHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(watchdogApphostPath))));
        if (!string.Equals(expected.WatchdogSha256, actualWatchdogHash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"predicate=watchdog-manifest-sha256; expected.watchdog.sha256={expected.WatchdogSha256}; actual.watchdog.sha256={actualWatchdogHash}");
        runnerLease.Verify();
        watchdogLease.Verify();
    }

    public static RuntimeApphostExpectedIdentities ResolveExpectedIdentities(
        string expectedRuntimeAggregateSha256,
        HarnessFingerprint runtimeManifest,
        string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(runtimeManifest);
        if (runtimeManifest.Files is null)
            throw new InvalidDataException("predicate=runtime-manifest-schema; expected.files=array; actual.files=null");
        var computedAggregate = Aggregate(runtimeManifest.Files);
        if (!string.Equals(runtimeManifest.Algorithm, "SHA-256", StringComparison.Ordinal) ||
            !string.Equals(runtimeManifest.AggregateSha256, computedAggregate, StringComparison.Ordinal) ||
            !string.Equals(expectedRuntimeAggregateSha256, computedAggregate, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"predicate=runtime-manifest-aggregate; expected.runtime.sha256={expectedRuntimeAggregateSha256}; actual.runtime.sha256={computedAggregate}");
        var runnerEntries = runtimeManifest.Files.Where(entry => string.Equals(entry.RelativePath, RunnerEntry, StringComparison.Ordinal)).ToArray();
        if (runnerEntries.Length != 1)
            throw new InvalidDataException($"predicate=runner-manifest-entry; expected.entry={RunnerEntry}; actual.count={runnerEntries.Length}");
        var watchdogEntries = runtimeManifest.Files.Where(entry => string.Equals(entry.RelativePath, WatchdogEntry, StringComparison.Ordinal)).ToArray();
        if (watchdogEntries.Length != 1)
            throw new InvalidDataException($"predicate=watchdog-manifest-entry; expected.entry={WatchdogEntry}; actual.count={watchdogEntries.Length}");
        var runnerEntry = runnerEntries[0];
        var watchdogEntry = watchdogEntries[0];
        if (!IsSha256(runnerEntry.Sha256) || !IsSha256(watchdogEntry.Sha256))
            throw new InvalidDataException("predicate=runtime-manifest-schema; expected.apphostSha256=64 hexadecimal characters; actual=invalid");
        var canonicalRoot = Path.GetFullPath(runtimeRoot);
        var runnerPath = Path.GetFullPath(Path.Combine(canonicalRoot, RunnerEntry.Replace('/', Path.DirectorySeparatorChar)));
        var watchdogPath = Path.GetFullPath(Path.Combine(canonicalRoot, WatchdogEntry.Replace('/', Path.DirectorySeparatorChar)));
        TrustedPhysicalPath.RequireContainedNoReparse(canonicalRoot, runnerPath, "Physical runner manifest identity");
        TrustedPhysicalPath.RequireContainedNoReparse(canonicalRoot, watchdogPath, "Physical watchdog manifest identity");
        return new RuntimeApphostExpectedIdentities(runnerPath, runnerEntry.Sha256, watchdogPath, watchdogEntry.Sha256);
    }

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Aggregate(IEnumerable<HarnessFingerprintEntry> files)
    {
        var manifest = string.Join('\n', files
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => $"{entry.RelativePath}\0{entry.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
    }
}

public static class PhysicalRuntimeManifestStore
{
    public const string FileName = "runtime-manifest.json";

    public static string WriteCreateNew(string runtimeRoot, HarnessFingerprint manifest)
    {
        var path = Path.Combine(Path.GetFullPath(runtimeRoot), FileName);
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, path, "Physical runtime manifest");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, manifest);
        stream.Flush(flushToDisk: true);
        return path;
    }

    public static HarnessFingerprint ReadStrict(string runtimeRoot, string path)
    {
        var expected = Path.Combine(Path.GetFullPath(runtimeRoot), FileName);
        var canonical = TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, path, "Physical runtime manifest");
        if (!string.Equals(expected, canonical, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Physical runtime manifest path is not canonical.");
        using var lease = TrustedPhysicalPath.AcquireContainedNoReparseLease(runtimeRoot, canonical, "Physical runtime manifest");
        var manifest = JsonSerializer.Deserialize<HarnessFingerprint>(File.ReadAllBytes(canonical))
                       ?? throw new InvalidDataException("Physical runtime manifest is empty.");
        lease.Verify();
        return manifest;
    }
}

public sealed record HarnessFingerprintCounts(
    int TotalInputCount,
    int SourcePresenceSentinelCount,
    int SourceContentInputCount,
    int RunnerTreeInputCount,
    int WatchdogTreeInputCount,
    int MetadataInputCount);

public sealed record PhysicalRuntimeApproval(
    int SchemaVersion,
    string RuntimeIdentifier,
    string SourceSha256,
    int SourceInputCount,
    int SourcePresenceSentinelCount,
    int SourceContentInputCount,
    string RuntimeSha256,
    int RuntimeInputCount,
    int RunnerTreeInputCount,
    int WatchdogTreeInputCount,
    int MetadataInputCount);

public sealed record StagedPhysicalSource(string SourceRoot, string ManifestPath, HarnessFingerprint Fingerprint);

public sealed record PhysicalRuntimeStartupSmoke(string ApplicationName, IReadOnlyList<string> Arguments);

public sealed record PhysicalRuntimeBuildCommand(
    IReadOnlyList<string> Arguments,
    PhysicalRuntimeStartupSmoke? StartupSmoke = null);

public sealed record PhysicalRuntimeBuildPlan(
    string RuntimeRoot,
    string SourceRoot,
    string PhysicalOutputDirectory,
    string WatchdogOutputDirectory,
    IReadOnlyList<string> MetadataPaths,
    IReadOnlyList<PhysicalRuntimeBuildCommand> Commands)
{
    public const string RuntimeIdentifier = "win-x64";

    public static PhysicalRuntimeBuildPlan Create(string repositoryRoot, string sessionId, string generation) =>
        Create(repositoryRoot, repositoryRoot, sessionId, generation);

    public static PhysicalRuntimeBuildPlan Create(
        string repositoryRoot,
        string stagedSourceRoot,
        string sessionId,
        string generation)
    {
        if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
            throw new ArgumentException("Physical runtime session identity must be exactly 32 hexadecimal characters.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(generation) || generation.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Physical runtime generation must use only ASCII letters, digits, and hyphens.", nameof(generation));
        var root = Path.GetFullPath(repositoryRoot);
        var sourceRoot = Path.GetFullPath(stagedSourceRoot);
        var runtimeRoot = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime", sessionId, generation);
        TrustedPhysicalPath.RequireNoReparse(root, "Physical repository root");
        TrustedPhysicalPath.RequireNoReparse(sourceRoot, "Physical staged source root");
        TrustedPhysicalPath.RequireContainedNoReparse(
            Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime"),
            runtimeRoot,
            "Physical runtime root");
        var physicalOutput = Path.Combine(runtimeRoot, "physical-tests");
        var watchdogOutput = Path.Combine(runtimeRoot, "watchdog");
        var physicalProject = Path.Combine(sourceRoot, "tests-dotnet", "Moondrop.PhysicalTests", "Moondrop.PhysicalTests.csproj");
        var watchdogProject = Path.Combine(sourceRoot, "tests-dotnet", "Moondrop.PhysicalWatchdog", "Moondrop.PhysicalWatchdog.csproj");
        var isolationRoot = Path.Combine(sourceRoot, "tests-dotnet", "build-isolation");
        var nugetConfig = Path.Combine(isolationRoot, "physical.NuGet.Config");
        var directoryBuildProps = Path.Combine(isolationRoot, "physical.Directory.Build.props");
        var directoryBuildTargets = Path.Combine(isolationRoot, "physical.Directory.Build.targets");
        var directoryPackagesProps = Path.Combine(isolationRoot, "physical.Directory.Packages.props");
        var artifactsPath = Path.Combine(runtimeRoot, "build-artifacts");
        var metadata = new[]
        {
            "global.json",
            "src/Moondrop.Core/packages.lock.json",
            "src/Moondrop.Hardware/packages.lock.json",
            "tests-dotnet/Moondrop.PhysicalTests/packages.lock.json",
            "tests-dotnet/Moondrop.PhysicalWatchdog/packages.lock.json",
            "tests-dotnet/build-isolation/physical.Directory.Build.props",
            "tests-dotnet/build-isolation/physical.Directory.Build.targets",
            "tests-dotnet/build-isolation/physical.Directory.Packages.props",
            "tests-dotnet/build-isolation/physical.NuGet.Config"
        };
        var common = new[]
        {
            "-noAutoResponse",
            "--disable-build-servers",
            $"-p:DirectoryBuildPropsPath={directoryBuildProps}",
            "-p:ImportDirectoryBuildProps=true",
            $"-p:DirectoryBuildTargetsPath={directoryBuildTargets}",
            "-p:ImportDirectoryBuildTargets=true",
            $"-p:DirectoryPackagesPropsPath={directoryPackagesProps}",
            "-p:ImportDirectoryPackagesProps=true",
            $"-p:RestoreConfigFile={nugetConfig}",
            "-p:RestorePackagesWithLockFile=true",
            "-p:RestoreLockedMode=true",
            "-p:NuGetAudit=false",
            "-p:ManagePackageVersionsCentrally=false",
            "-p:CentralPackageTransitivePinningEnabled=false",
            "-p:ContinuousIntegrationBuild=true",
            "-p:Deterministic=true",
            "-p:PhysicalProtectedBuild=true",
            $"-p:PhysicalProtectedSourceRoot={sourceRoot}",
            "-p:UseSharedCompilation=false",
            "-p:UseArtifactsOutput=true",
            $"-p:ArtifactsPath={artifactsPath}"
        };
        var commands = new[]
        {
            new PhysicalRuntimeBuildCommand(["restore", physicalProject, "--locked-mode", "--configfile", nugetConfig, "-r", RuntimeIdentifier, .. common]),
            new PhysicalRuntimeBuildCommand(["restore", watchdogProject, "--locked-mode", "--configfile", nugetConfig, "-r", RuntimeIdentifier, .. common]),
            new PhysicalRuntimeBuildCommand(
                ["publish", physicalProject, "-c", "Release", "--no-restore", "-r", RuntimeIdentifier, "--self-contained", "true", "-p:UseAppHost=true", .. common, "-o", physicalOutput],
                new PhysicalRuntimeStartupSmoke("Moondrop.PhysicalTests", ["--help"])),
            new PhysicalRuntimeBuildCommand(
                ["publish", watchdogProject, "-c", "Release", "--no-restore", "-r", RuntimeIdentifier, "--self-contained", "true", "-p:UseAppHost=true", .. common, "-o", watchdogOutput],
                new PhysicalRuntimeStartupSmoke("Moondrop.PhysicalWatchdog", ["--help"]))
        };
        return new PhysicalRuntimeBuildPlan(runtimeRoot, sourceRoot, physicalOutput, watchdogOutput, metadata, commands);
    }
}

public sealed record PhysicalRuntimeBuildResult(
    PhysicalRuntimeBuildPlan Plan,
    HarnessFingerprint SourceFingerprint,
    HarnessFingerprint RuntimeManifest);

internal interface IPhysicalSourceProtectionLease : IAsyncDisposable
{
    void RequireProtected();
}

internal interface IPhysicalSourceProtectionLayer
{
    IPhysicalSourceProtectionLease ProtectAndVerify(string sourceRoot);
}

internal interface IPhysicalRuntimeBuildExecutor
{
    Task RunDotnetAsync(
        string dotnetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    Task RunStartupSmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string outputDirectory,
        PhysicalRuntimeStartupSmoke smoke,
        CancellationToken cancellationToken);

    Task RunOfflineTopologySmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string physicalApphostPath,
        string watchdogApphostPath,
        HarnessFingerprint runtimeManifest,
        CancellationToken cancellationToken);
}

internal sealed class WindowsPhysicalSourceProtectionLayer : IPhysicalSourceProtectionLayer
{
    public IPhysicalSourceProtectionLease ProtectAndVerify(string sourceRoot) =>
        WindowsPhysicalSourceProtection.ProtectAndVerify(sourceRoot);
}

internal sealed class ProcessPhysicalRuntimeBuildExecutor : IPhysicalRuntimeBuildExecutor
{
    public Task RunDotnetAsync(
        string dotnetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        PhysicalRuntimeBuilder.RunDotnetAsync(dotnetPath, workingDirectory, arguments, environment, cancellationToken);

    public Task RunStartupSmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string outputDirectory,
        PhysicalRuntimeStartupSmoke smoke,
        CancellationToken cancellationToken) =>
        PhysicalRuntimeBuilder.RunStartupSmokeAsync(
            repositoryRoot,
            runtimeRoot,
            outputDirectory,
            smoke,
            cancellationToken);

    public Task RunOfflineTopologySmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string physicalApphostPath,
        string watchdogApphostPath,
        HarnessFingerprint runtimeManifest,
        CancellationToken cancellationToken) =>
        PhysicalRuntimeBuilder.RunOfflineTopologySmokeAsync(
            repositoryRoot,
            runtimeRoot,
            physicalApphostPath,
            watchdogApphostPath,
            runtimeManifest,
            cancellationToken);
}

internal sealed class WindowsPhysicalSourceProtection : IPhysicalSourceProtectionLease
{
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const FileSystemRights DeniedMutationRights =
        FileSystemRights.Write |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles;

    private sealed record ProtectedEntry(
        string Path,
        bool IsDirectory,
        FileAttributes Attributes,
        string AccessSddl);

    private readonly string _root;
    private readonly ProtectedEntry[] _entries;
    private readonly SecurityIdentifier[] _identities;
    private SafeFileHandle[] _integrityHandles = [];
    private bool _released;

    private WindowsPhysicalSourceProtection(
        string root,
        ProtectedEntry[] entries,
        SecurityIdentifier[] identities)
    {
        _root = root;
        _entries = entries;
        _identities = identities;
    }

    public static WindowsPhysicalSourceProtection ProtectAndVerify(string sourceRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical staged-source ACL protection requires Windows.");
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Physical staged source is missing: {root}.");

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var identities = new[] { identity.User }
            .Concat(identity.Groups?.Cast<IdentityReference>().Select(group => group as SecurityIdentifier) ?? [])
            .Where(sid => sid is not null)
            .Cast<SecurityIdentifier>()
            .Distinct()
            .ToArray();
        if (identity.User is null || identities.Length == 0)
            throw new InvalidOperationException("The invoking Windows identity could not be resolved for staged-source protection.");

        var entries = EnumerateEntries(root)
            .Select(path => CaptureEntry(path, Directory.Exists(path)))
            .OrderByDescending(entry => EntryDepth(entry.Path))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lease = new WindowsPhysicalSourceProtection(root, entries, identities);
        try
        {
            foreach (var entry in entries)
                lease.Apply(entry);
            lease.AcquireIntegrityHandles();
            lease.RequireProtected();
            return lease;
        }
        catch (Exception protectionError)
        {
            try
            {
                lease.Restore();
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Staged-source protection failed and its ACL/attribute rollback also failed.",
                    protectionError,
                    restoreError);
            }
            throw;
        }
    }

    public void RequireProtected()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        var currentPaths = EnumerateEntries(_root).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var expectedPaths = _entries.Select(entry => entry.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!currentPaths.SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Protected physical staged-source entries changed unexpectedly.");
        if (_integrityHandles.Length != _entries.Length ||
            _integrityHandles.Any(handle => handle.IsInvalid || handle.IsClosed))
            throw new IOException("Protected physical staged-source integrity handles are incomplete or closed.");

        foreach (var entry in _entries)
        {
            if ((File.GetAttributes(entry.Path) & FileAttributes.ReadOnly) == 0)
                throw new IOException($"Protected physical staged-source entry lost its read-only attribute: {entry.Path}.");
            var security = GetAccessSecurity(entry.Path, entry.IsDirectory);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            foreach (var sid in _identities)
            {
                if (!rules.Any(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        sid.Equals(rule.IdentityReference) &&
                        (rule.FileSystemRights & DeniedMutationRights) == DeniedMutationRights))
                    throw new IOException($"Protected physical staged-source ACL is missing a required mutation denial: {entry.Path}.");
            }

            if (entry.IsDirectory)
                _ = Directory.EnumerateFileSystemEntries(entry.Path).ToArray();
            else
                using (File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_released)
            Restore();
        return ValueTask.CompletedTask;
    }

    private void Apply(ProtectedEntry entry)
    {
        File.SetAttributes(entry.Path, File.GetAttributes(entry.Path) | FileAttributes.ReadOnly);
        var security = GetAccessSecurity(entry.Path, entry.IsDirectory);
        foreach (var sid in _identities)
            security.AddAccessRule(new FileSystemAccessRule(sid, DeniedMutationRights, AccessControlType.Deny));
        SetAccessSecurity(entry.Path, entry.IsDirectory, security);
    }

    private void AcquireIntegrityHandles()
    {
        var handles = new List<SafeFileHandle>(_entries.Length);
        try
        {
            foreach (var entry in _entries)
            {
                var handle = CreateFileW(
                    TrustedPhysicalPath.ToExtendedLengthForm(entry.Path),
                    GenericRead,
                    FileShare.Read,
                    IntPtr.Zero,
                    FileMode.Open,
                    entry.IsDirectory ? FileFlagBackupSemantics : 0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException(
                        $"Could not acquire the protected staged-source integrity handle: {entry.Path}.",
                        new Win32Exception(error));
                }
                handles.Add(handle);
            }
            _integrityHandles = handles.ToArray();
        }
        catch
        {
            foreach (var handle in handles)
                handle.Dispose();
            throw;
        }
    }

    private void Restore()
    {
        List<Exception>? failures = null;
        foreach (var entry in _entries
                     .OrderBy(entry => EntryDepth(entry.Path))
                     .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                FileSystemSecurity security = entry.IsDirectory ? new DirectorySecurity() : new FileSecurity();
                security.SetSecurityDescriptorSddlForm(entry.AccessSddl, AccessControlSections.Access);
                SetAccessSecurity(entry.Path, entry.IsDirectory, security);
                File.SetAttributes(entry.Path, entry.Attributes);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
        foreach (var handle in _integrityHandles)
            handle.Dispose();
        _integrityHandles = [];
        _released = true;
        if (failures is not null)
            throw new AggregateException("Could not restore staged-source ACLs and attributes.", failures);
    }

    private static ProtectedEntry CaptureEntry(string path, bool isDirectory)
    {
        var security = GetAccessSecurity(path, isDirectory);
        return new ProtectedEntry(
            path,
            isDirectory,
            File.GetAttributes(path),
            security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    private static FileSystemSecurity GetAccessSecurity(string path, bool isDirectory) => isDirectory
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    private static void SetAccessSecurity(string path, bool isDirectory, FileSystemSecurity security)
    {
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static IEnumerable<string> EnumerateEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root);

    private static int EntryDepth(string path) =>
        path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

public static class PhysicalRuntimeBuilder
{
    public static async Task<PhysicalRuntimeBuildResult> BuildAsync(
        string repositoryRoot,
        string dotnetPath,
        string sessionId,
        string generation,
        CancellationToken cancellationToken = default) =>
        await BuildCoreAsync(
            repositoryRoot,
            dotnetPath,
            sessionId,
            generation,
            new WindowsPhysicalSourceProtectionLayer(),
            new ProcessPhysicalRuntimeBuildExecutor(),
            requireIndependentApproval: true,
            cancellationToken).ConfigureAwait(false);

    public static async Task<PhysicalRuntimeBuildResult> BuildAuditCandidateAsync(
        string repositoryRoot,
        string dotnetPath,
        string sessionId,
        string generation,
        CancellationToken cancellationToken = default) =>
        await BuildCoreAsync(
            repositoryRoot,
            dotnetPath,
            sessionId,
            generation,
            new WindowsPhysicalSourceProtectionLayer(),
            new ProcessPhysicalRuntimeBuildExecutor(),
            requireIndependentApproval: false,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PhysicalRuntimeBuildResult> BuildAsync(
        string repositoryRoot,
        string dotnetPath,
        string sessionId,
        string generation,
        IPhysicalSourceProtectionLayer protectionLayer,
        IPhysicalRuntimeBuildExecutor executor,
        CancellationToken cancellationToken = default)
        => await BuildCoreAsync(
            repositoryRoot,
            dotnetPath,
            sessionId,
            generation,
            protectionLayer,
            executor,
            requireIndependentApproval: true,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PhysicalRuntimeBuildResult> BuildAuditCandidateAsync(
        string repositoryRoot,
        string dotnetPath,
        string sessionId,
        string generation,
        IPhysicalSourceProtectionLayer protectionLayer,
        IPhysicalRuntimeBuildExecutor executor,
        CancellationToken cancellationToken = default)
        => await BuildCoreAsync(
            repositoryRoot,
            dotnetPath,
            sessionId,
            generation,
            protectionLayer,
            executor,
            requireIndependentApproval: false,
            cancellationToken).ConfigureAwait(false);

    private static async Task<PhysicalRuntimeBuildResult> BuildCoreAsync(
        string repositoryRoot,
        string dotnetPath,
        string sessionId,
        string generation,
        IPhysicalSourceProtectionLayer protectionLayer,
        IPhysicalRuntimeBuildExecutor executor,
        bool requireIndependentApproval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protectionLayer);
        ArgumentNullException.ThrowIfNull(executor);
        var root = Path.GetFullPath(repositoryRoot);
        var prospective = PhysicalRuntimeBuildPlan.Create(root, sessionId, generation);
        await using var buildLock = await AcquireBuildLockAsync(root, cancellationToken).ConfigureAwait(false);
        var approval = requireIndependentApproval
            ? PhysicalRuntimeApprovalManifest.ReadStrict(Path.Combine(
                root,
                PhysicalRuntimeApprovalManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
            : null;
        var sourceRoot = Path.Combine(prospective.RuntimeRoot, "source");
        var plan = PhysicalRuntimeBuildPlan.Create(root, sourceRoot, sessionId, generation);
        if (Directory.Exists(plan.RuntimeRoot))
            throw new IOException($"Refusing to reuse physical runtime generation {plan.RuntimeRoot}.");
        var staged = HarnessBuildFingerprint.StageSource(root, plan.SourceRoot);
        using var runtimeLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(
            Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime"),
            plan.RuntimeRoot,
            "Physical runtime build tree");
        if (approval is not null)
            PhysicalRuntimeApprovalManifest.RequireSourceMatches(approval, staged.Fingerprint);
        await using var sourceProtection = protectionLayer.ProtectAndVerify(plan.SourceRoot);
        sourceProtection.RequireProtected();
        HarnessBuildFingerprint.RequireStagedSourceMatches(staged.Fingerprint.AggregateSha256, plan.SourceRoot);
        var buildEnvironment = CreateIsolatedBuildEnvironment(plan, dotnetPath);
        foreach (var directory in new[]
                 {
                      buildEnvironment["DOTNET_CLI_HOME"],
                      buildEnvironment["APPDATA"],
                      buildEnvironment["LOCALAPPDATA"],
                      buildEnvironment["NUGET_HTTP_CACHE_PATH"],
                      buildEnvironment["NUGET_PACKAGES"],
                      buildEnvironment["ProgramData"],
                      buildEnvironment["PROGRAMFILES"],
                      buildEnvironment["PROGRAMFILES(X86)"],
                      buildEnvironment["TEMP"],
                      buildEnvironment["USERPROFILE"]
                 })
            TrustedPhysicalPath.CreateDirectoryNoReparse(directory, "Isolated physical build directory");
        foreach (var command in plan.Commands)
        {
            await executor.RunDotnetAsync(
                dotnetPath,
                plan.SourceRoot,
                command.Arguments,
                buildEnvironment,
                cancellationToken).ConfigureAwait(false);
            runtimeLease.Verify();
            sourceProtection.RequireProtected();
            if (command.StartupSmoke is not null)
            {
                var outputDirectory = string.Equals(
                    command.StartupSmoke.ApplicationName,
                    "Moondrop.PhysicalTests",
                    StringComparison.Ordinal)
                    ? plan.PhysicalOutputDirectory
                    : plan.WatchdogOutputDirectory;
                await executor.RunStartupSmokeAsync(
                    root,
                    plan.RuntimeRoot,
                    outputDirectory,
                    command.StartupSmoke,
                    cancellationToken).ConfigureAwait(false);
                sourceProtection.RequireProtected();
            }
        }
        HarnessBuildFingerprint.RequireStagedSourceMatches(staged.Fingerprint.AggregateSha256, plan.SourceRoot);
        sourceProtection.RequireProtected();
        var runtime = HarnessBuildFingerprint.CaptureRuntime(
            root,
            plan.PhysicalOutputDirectory,
            plan.WatchdogOutputDirectory,
            plan.SourceRoot,
            plan.MetadataPaths);
        PhysicalRuntimeManifestStore.WriteCreateNew(plan.RuntimeRoot, runtime);
        sourceProtection.RequireProtected();
        HarnessBuildFingerprint.RequireStagedSourceMatches(staged.Fingerprint.AggregateSha256, plan.SourceRoot);
        await executor.RunOfflineTopologySmokeAsync(
            root,
            plan.RuntimeRoot,
            Path.Combine(plan.PhysicalOutputDirectory, "Moondrop.PhysicalTests.exe"),
            Path.Combine(plan.WatchdogOutputDirectory, "Moondrop.PhysicalWatchdog.exe"),
            runtime,
            cancellationToken).ConfigureAwait(false);
        sourceProtection.RequireProtected();
        HarnessBuildFingerprint.RequireStagedSourceMatches(staged.Fingerprint.AggregateSha256, plan.SourceRoot);
        var runtimeAfterTopology = HarnessBuildFingerprint.CaptureRuntime(
            root,
            plan.PhysicalOutputDirectory,
            plan.WatchdogOutputDirectory,
            plan.SourceRoot,
            plan.MetadataPaths);
        if (!string.Equals(runtime.AggregateSha256, runtimeAfterTopology.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Candidate topology smoke changed the complete runtime tree: expected {runtime.AggregateSha256}, actual {runtimeAfterTopology.AggregateSha256}.");
        if (approval is not null)
            PhysicalRuntimeApprovalManifest.RequireMatches(approval, staged.Fingerprint, runtime);
        runtimeLease.Verify();
        return new PhysicalRuntimeBuildResult(plan, staged.Fingerprint, runtime);
    }

    internal static async Task RunOfflineTopologySmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string physicalApphostPath,
        string watchdogApphostPath,
        HarnessFingerprint runtimeManifest,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.Combine(Path.GetFullPath(runtimeRoot), "offline-topology");
        var report = Path.Combine(reportDirectory, "observed-topology.json");
        var ambientEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            ambientEnvironment[(string)pair.Key] = (string?)pair.Value ?? "";
        var launch = CreateOfflineTopologyWatchdogLaunchPlan(
            repositoryRoot,
            runtimeRoot,
            physicalApphostPath,
            watchdogApphostPath,
            runtimeManifest.AggregateSha256,
            ambientEnvironment);
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(runtimeRoot, reportDirectory, "Candidate topology report directory");
        TrustedPhysicalPath.RequireContainedNoReparse(runtimeRoot, report, "Candidate topology report");
        using var reportLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(runtimeRoot, reportDirectory, "Candidate topology report directory");
        var result = await PhysicalProcessLauncher.RunToExitAsync(launch, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException(
                $"Candidate offline topology smoke exited {result.ExitCode}. stdout={DiagnosticText.Sanitize(result.StandardOutput)}; stderr={DiagnosticText.Sanitize(result.StandardError)}");
        var observation = PhysicalOfflineTopologyProbe.ReadObservation(reportDirectory, report);
        reportLease.Verify();
        PhysicalOfflineTopologyProbe.RequireDirectPublishedApphostTopology(
            observation,
            watchdogApphostPath,
            physicalApphostPath);
        if (!runtimeManifest.Files.Any(entry =>
                string.Equals(entry.RelativePath, "physical-tests/Moondrop.PhysicalTests.exe", StringComparison.Ordinal) &&
                string.Equals(entry.Sha256, observation.PhysicalRunner.Sha256, StringComparison.Ordinal)) ||
            !runtimeManifest.Files.Any(entry =>
                string.Equals(entry.RelativePath, "watchdog/Moondrop.PhysicalWatchdog.exe", StringComparison.Ordinal) &&
                string.Equals(entry.Sha256, observation.Watchdog.Sha256, StringComparison.Ordinal)))
            throw new InvalidDataException("Candidate offline topology smoke apphost identities are not covered by the exact complete runtime manifest.");
    }

    public static PhysicalProcessLaunchPlan CreateOfflineTopologyWatchdogLaunchPlan(
        string repositoryRoot,
        string runtimeRoot,
        string physicalApphostPath,
        string watchdogApphostPath,
        string runtimeManifestSha256,
        IReadOnlyDictionary<string, string> ambientEnvironment)
    {
        ArgumentNullException.ThrowIfNull(ambientEnvironment);
        if (runtimeManifestSha256.Length != 64 || !runtimeManifestSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Candidate topology runtime manifest SHA-256 is malformed.");
        var root = Path.GetFullPath(repositoryRoot);
        var runtime = Path.GetFullPath(runtimeRoot);
        var physical = Path.GetFullPath(physicalApphostPath);
        var watchdog = Path.GetFullPath(watchdogApphostPath);
        var report = Path.Combine(runtime, "offline-topology", "observed-topology.json");
        TrustedPhysicalPath.RequireContainedNoReparse(runtime, physical, "Candidate topology physical apphost");
        TrustedPhysicalPath.RequireContainedNoReparse(runtime, watchdog, "Candidate topology watchdog apphost");
        TrustedPhysicalPath.RequireContainedNoReparse(runtime, report, "Candidate topology report");
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (ambientEnvironment.TryGetValue(name, out var value))
                retained[name] = value;
        var environment = PhysicalSystemEnvironment.Validate(retained, "Candidate topology watchdog");
        return new PhysicalProcessLaunchPlan(
            watchdog,
            Path.GetDirectoryName(watchdog)!,
            [
                "--offline-topology-probe",
                "--physical-apphost", physical,
                "--report", report,
                "--repo", root,
                "--runtime-sha256", runtimeManifestSha256
            ],
            environment,
            RedirectStandardOutput: true,
            RedirectStandardError: true);
    }

    public static IReadOnlyDictionary<string, string> CreateIsolatedBuildEnvironment(
        PhysicalRuntimeBuildPlan plan,
        string dotnetPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var dotnetRoot = Path.GetDirectoryName(Path.GetFullPath(dotnetPath))
                         ?? throw new InvalidDataException("The audited dotnet executable has no parent directory.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new PlatformNotSupportedException("The physical runtime builder requires a Windows system directory.");
        var system32 = Path.Combine(windows, "System32");
        var temporary = Path.Combine(plan.RuntimeRoot, ".temp");
        var isolatedProfile = Path.Combine(plan.RuntimeRoot, ".profile");
        var isolatedSystemFolders = Path.Combine(plan.RuntimeRoot, ".system-folders");
        var packageProfile = string.Equals(Path.GetFileName(dotnetRoot), ".dotnet", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(dotnetRoot)?.FullName
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(packageProfile))
            throw new DirectoryNotFoundException("Could not derive the offline NuGet package-cache profile.");
        var localPackageCache = Path.Combine(packageProfile, ".nuget", "packages");
        if (!Directory.Exists(localPackageCache))
            throw new DirectoryNotFoundException($"Required offline NuGet package cache is missing: {localPackageCache}.");
        TrustedPhysicalPath.RequireNoReparse(localPackageCache, "Offline NuGet package cache");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPDATA"] = Path.Combine(isolatedProfile, "AppData", "Roaming"),
            ["ComSpec"] = Path.Combine(system32, "cmd.exe"),
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_CLI_HOME"] = Path.Combine(plan.RuntimeRoot, ".dotnet-home"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_ROOT"] = dotnetRoot,
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["MSBUILDNOINPROCNODE"] = "1",
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(plan.RuntimeRoot, ".nuget", "http-cache"),
            ["NUGET_PACKAGES"] = localPackageCache,
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["LOCALAPPDATA"] = Path.Combine(isolatedProfile, "AppData", "Local"),
            ["PATH"] = $"{dotnetRoot}{Path.PathSeparator}{system32}",
            ["ProgramData"] = Path.Combine(isolatedSystemFolders, "ProgramData"),
            ["PROGRAMFILES"] = Path.Combine(isolatedSystemFolders, "ProgramFiles"),
            ["PROGRAMFILES(X86)"] = Path.Combine(isolatedSystemFolders, "ProgramFilesX86"),
            ["SystemRoot"] = windows,
            ["TEMP"] = temporary,
            ["TMP"] = temporary,
            ["USERPROFILE"] = isolatedProfile,
            ["WINDIR"] = windows
        };
    }

    public static IReadOnlyDictionary<string, string> CreateStartupSmokeEnvironment(
        string missingRuntimePath,
        string temporaryPath)
    {
        var missing = Path.GetFullPath(missingRuntimePath);
        var temporary = Path.GetFullPath(temporaryPath);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new PlatformNotSupportedException("The physical runtime startup smoke requires a Windows system directory.");
        var system32 = Path.Combine(windows, "System32");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ComSpec"] = Path.Combine(system32, "cmd.exe"),
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_ROOT"] = missing,
            ["DOTNET_ROOT_X64"] = missing,
            ["DOTNET_SHARED_STORE"] = missing,
            ["PATH"] = system32,
            ["SystemRoot"] = windows,
            ["TEMP"] = temporary,
            ["TMP"] = temporary,
            ["WINDIR"] = windows
        };
    }

    public static async Task RunStartupSmokeAsync(
        string repositoryRoot,
        string runtimeRoot,
        string outputDirectory,
        PhysicalRuntimeStartupSmoke smoke,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(smoke);
        _ = Path.GetFullPath(repositoryRoot);
        var runtime = Path.GetFullPath(runtimeRoot);
        var output = Path.GetFullPath(outputDirectory);
        var executable = Path.Combine(output, $"{smoke.ApplicationName}.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Mandatory startup-smoke apphost is missing for {smoke.ApplicationName}.", executable);
        HarnessBuildFingerprint.RequireInsideRootForBuild(runtime, output, "Startup-smoke publish directory");

        var outputBefore = CaptureSmokeTree(output, _ => true);
        var smokeTemporary = Path.Combine(Path.GetTempPath(), $"moondrop-startup-smoke-{Guid.NewGuid():N}");
        var missingRuntime = Path.Combine(smokeTemporary, "nonexistent-shared-runtime");
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(Path.GetTempPath(), smokeTemporary, "Physical startup-smoke temporary directory");
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = output,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in smoke.Arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment.Clear();
            foreach (var pair in CreateStartupSmokeEnvironment(missingRuntime, smokeTemporary))
                startInfo.Environment[pair.Key] = pair.Value;

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException($"Could not start mandatory {smoke.ApplicationName} apphost smoke.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TerminateAndWaitAsync(process).ConfigureAwait(false);
                throw new TimeoutException($"Mandatory {smoke.ApplicationName} direct-apphost startup smoke exceeded 30 seconds.");
            }
            catch
            {
                await TerminateAndWaitAsync(process).ConfigureAwait(false);
                throw;
            }
            var standardOutput = await outputTask.ConfigureAwait(false);
            var standardError = await errorTask.ConfigureAwait(false);

            var outputAfter = CaptureSmokeTree(output, _ => true);
            if (!string.Equals(outputBefore.AggregateSha256, outputAfter.AggregateSha256, StringComparison.Ordinal) ||
                Directory.EnumerateFileSystemEntries(smokeTemporary).Any())
                throw new InvalidDataException($"Mandatory {smoke.ApplicationName} startup smoke created or changed an artifact.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Mandatory {smoke.ApplicationName} direct-apphost startup smoke failed with exit {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        }
        finally
        {
            if (Directory.Exists(smokeTemporary) && !Directory.EnumerateFileSystemEntries(smokeTemporary).Any())
                Directory.Delete(smokeTemporary);
        }
    }

    private static HarnessFingerprint CaptureSmokeTree(string directory, Func<string, bool> include)
    {
        if (!Directory.Exists(directory))
            return new HarnessFingerprint(
                "SHA-256",
                Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())),
                Array.Empty<HarnessFingerprintEntry>());
        var root = Path.GetFullPath(directory);
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(include)
            .Select(path => new HarnessFingerprintEntry(
                path.Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var manifest = string.Join('\n', entries.Select(entry => $"{entry.RelativePath}\0{entry.Sha256}"));
        return new HarnessFingerprint(
            "SHA-256",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
            entries);
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static async Task<BuildLockLease> AcquireBuildLockAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(repositoryRoot, "tests-dotnet", "artifacts", "physical-runtime");
        var path = Path.Combine(directory, ".build.lock");
        TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical runtime build lock");
        TrustedPhysicalPath.CreateDirectoryContainedNoReparse(repositoryRoot, directory, "Physical runtime build-lock directory");
        TrustedPhysicalPath.RequireContainedNoReparse(directory, path, "Physical runtime build lock");
        var directoryLease = TrustedPhysicalPath.AcquireContainedNoReparseLease(repositoryRoot, directory, "Physical runtime build-lock directory");
        var timeout = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    directoryLease.Verify();
                    return new BuildLockLease(stream, directoryLease);
                }
                catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            directoryLease.Dispose();
            throw;
        }
    }

    private sealed class BuildLockLease(FileStream stream, TrustedPhysicalPath.StablePathLease pathLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            pathLease.Verify();
            pathLease.Dispose();
        }
    }

    internal static async Task RunDotnetAsync(
        string dotnetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Path.GetFullPath(dotnetPath))
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var pair in environment)
            startInfo.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the mandatory locked physical runtime build command.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TerminateAndWaitAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Mandatory locked physical runtime build failed with exit {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static async Task TerminateAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

public static class PhysicalRuntimeApprovalManifest
{
    public const string RelativePath = "tests-dotnet/physical-runtime-approval.json";
    public const string Placeholder = "INDEPENDENT_AUDIT_REQUIRED";
    private const int CurrentSchemaVersion = 1;

    private static readonly string[] RequiredProperties =
    [
        "MetadataInputCount",
        "RunnerTreeInputCount",
        "RuntimeIdentifier",
        "RuntimeInputCount",
        "RuntimeSha256",
        "SchemaVersion",
        "SourceContentInputCount",
        "SourceInputCount",
        "SourcePresenceSentinelCount",
        "SourceSha256",
        "WatchdogTreeInputCount"
    ];

    public static PhysicalRuntimeApproval ReadStrict(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The independent physical runtime-approval manifest is missing.", manifestPath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The physical runtime-approval manifest must be one JSON object.");
            var actualProperties = root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actualProperties.SequenceEqual(RequiredProperties, StringComparer.Ordinal))
                throw new InvalidDataException("The physical runtime-approval manifest has missing, duplicate, or unknown metadata fields.");

            var approval = new PhysicalRuntimeApproval(
                root.GetProperty("SchemaVersion").GetInt32(),
                RequiredString(root, "RuntimeIdentifier"),
                RequiredString(root, "SourceSha256"),
                root.GetProperty("SourceInputCount").GetInt32(),
                root.GetProperty("SourcePresenceSentinelCount").GetInt32(),
                root.GetProperty("SourceContentInputCount").GetInt32(),
                RequiredString(root, "RuntimeSha256"),
                root.GetProperty("RuntimeInputCount").GetInt32(),
                root.GetProperty("RunnerTreeInputCount").GetInt32(),
                root.GetProperty("WatchdogTreeInputCount").GetInt32(),
                root.GetProperty("MetadataInputCount").GetInt32());
            RequireCompleteContract(approval);
            return approval;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The physical runtime-approval manifest is not valid strict JSON metadata.", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The physical runtime-approval manifest contains an invalid metadata type or value.", ex);
        }
    }

    public static void RequireSourceMatches(PhysicalRuntimeApproval approval, HarnessFingerprint source)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(source);
        RequireCompleteContract(approval);
        var counts = HarnessBuildFingerprint.CountSourceInputs(source);
        if (!string.Equals(approval.SourceSha256, source.AggregateSha256, StringComparison.OrdinalIgnoreCase) ||
            approval.SourceInputCount != counts.TotalInputCount ||
            approval.SourcePresenceSentinelCount != counts.SourcePresenceSentinelCount ||
            approval.SourceContentInputCount != counts.SourceContentInputCount)
            throw new InvalidDataException("Physical source fingerprint or declared source input counts do not match the independent runtime approval.");
    }

    public static void RequireMatches(
        PhysicalRuntimeApproval approval,
        HarnessFingerprint source,
        HarnessFingerprint runtime)
    {
        RequireSourceMatches(approval, source);
        var counts = HarnessBuildFingerprint.CountRuntimeInputs(runtime);
        if (!string.Equals(approval.RuntimeSha256, runtime.AggregateSha256, StringComparison.OrdinalIgnoreCase) ||
            approval.RuntimeInputCount != counts.TotalInputCount ||
            approval.RunnerTreeInputCount != counts.RunnerTreeInputCount ||
            approval.WatchdogTreeInputCount != counts.WatchdogTreeInputCount ||
            approval.MetadataInputCount != counts.MetadataInputCount)
            throw new InvalidDataException("Physical complete self-contained runtime hash or declared runtime input counts do not match the independent approval.");
    }

    public static void RequireSessionHashes(
        PhysicalRuntimeApproval approval,
        string sessionSourceSha256,
        string sessionRuntimeSha256)
    {
        RequireCompleteContract(approval);
        if (!string.Equals(approval.SourceSha256, sessionSourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approval.RuntimeSha256, sessionRuntimeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Physical session source/runtime hashes do not both match the independent approval.");
    }

    private static void RequireCompleteContract(PhysicalRuntimeApproval approval)
    {
        if (approval.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported physical runtime-approval schema {approval.SchemaVersion}.");
        if (!string.Equals(approval.RuntimeIdentifier, PhysicalRuntimeBuildPlan.RuntimeIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException("The physical runtime-approval runtime identifier is not the required win-x64 target.");
        if (string.Equals(approval.SourceSha256, Placeholder, StringComparison.Ordinal) ||
            string.Equals(approval.RuntimeSha256, Placeholder, StringComparison.Ordinal))
            throw new InvalidDataException("The physical runtime-approval manifest is a placeholder until an independent audit fills both hashes and all counts.");
        if (!IsSha256(approval.SourceSha256) || !IsSha256(approval.RuntimeSha256))
            throw new InvalidDataException("The physical runtime-approval manifest must contain both exact 64-hex SHA-256 values.");
        if (approval.SourceInputCount <= 0 ||
            approval.SourcePresenceSentinelCount <= 0 ||
            approval.SourceContentInputCount <= 0 ||
            approval.SourceInputCount != approval.SourcePresenceSentinelCount + approval.SourceContentInputCount)
            throw new InvalidDataException("The physical runtime-approval source input counts are incomplete or inconsistent.");
        if (approval.RuntimeInputCount <= 0 ||
            approval.RunnerTreeInputCount <= 0 ||
            approval.WatchdogTreeInputCount <= 0 ||
            approval.MetadataInputCount <= 0 ||
            approval.RuntimeInputCount != approval.RunnerTreeInputCount + approval.WatchdogTreeInputCount + approval.MetadataInputCount)
            throw new InvalidDataException("The physical runtime-approval runtime input counts are incomplete or inconsistent.");
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"The physical runtime-approval {name} value is missing.")
            : value;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public static class HarnessBuildFingerprint
{
    private sealed record SourceFingerprintInput(string Label, string? FullPath, string SentinelValue);
    private sealed record StagedSourceManifest(int SchemaVersion, IReadOnlyList<StagedSourceInput> Inputs);
    private sealed record StagedSourceInput(string Label, string? RelativePath, string? SentinelValue);
    private sealed record CompleteOutputFile(
        string RelativePath,
        string FullPath,
        string Sha256,
        long Size,
        DateTime LastWriteTimeUtc);
    private sealed record CompleteOutputSnapshot(
        string Root,
        HarnessFingerprint Fingerprint,
        IReadOnlyList<CompleteOutputFile> Files);

    private const string StagedSourceManifestFileName = ".physical-source-inputs.json";

    private static readonly string[] SourceRootFiles =
    [
        "DawnPro.Wpf.slnx",
        "tests-dotnet/default.runsettings",
        "tests-dotnet/physical.runsettings",
        "tests-dotnet/build-isolation/physical.Directory.Build.props",
        "tests-dotnet/build-isolation/physical.Directory.Build.targets",
        "tests-dotnet/build-isolation/physical.Directory.Packages.props",
        "tests-dotnet/build-isolation/physical.NuGet.Config"
    ];

    private static readonly string[] SourceDirectories =
    [
        "src",
        "tests-dotnet/Moondrop.Tests",
        "tests-dotnet/Moondrop.PhysicalTests",
        "tests-dotnet/Moondrop.PhysicalWatchdog"
    ];

    private static readonly string[] BuildSearchStartDirectories =
    [
        "src/Moondrop.Core",
        "src/Moondrop.Hardware",
        "src/Moondrop.Wpf",
        "tests-dotnet/Moondrop.Tests",
        "tests-dotnet/Moondrop.PhysicalTests",
        "tests-dotnet/Moondrop.PhysicalWatchdog"
    ];

    private static readonly string[] ImplicitBuildControlPaths =
    [
        ".config/dotnet-tools.json",
        "Directory.Build.props",
        "Directory.Build.rsp",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "MSBuild.rsp",
        "NuGet.Config",
        "nuget.config"
    ];

    public static HarnessFingerprint CaptureSource(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return CaptureSourceInputs(EnumerateSourceInputs(root));
    }

    public static HarnessFingerprintCounts CountSourceInputs(HarnessFingerprint source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sentinels = source.Files.Count(file => file.RelativePath.EndsWith(".presence", StringComparison.Ordinal));
        return new HarnessFingerprintCounts(
            source.Files.Count,
            sentinels,
            source.Files.Count - sentinels,
            0,
            0,
            0);
    }

    public static HarnessFingerprintCounts CountRuntimeInputs(HarnessFingerprint runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var runner = runtime.Files.Count(file => file.RelativePath.StartsWith("physical-tests/", StringComparison.Ordinal));
        var watchdog = runtime.Files.Count(file => file.RelativePath.StartsWith("watchdog/", StringComparison.Ordinal));
        var metadata = runtime.Files.Count(file => file.RelativePath.StartsWith("metadata/", StringComparison.Ordinal));
        if (runner + watchdog + metadata != runtime.Files.Count)
            throw new InvalidDataException("The runtime manifest contains an input outside the runner, watchdog, or metadata namespaces.");
        return new HarnessFingerprintCounts(runtime.Files.Count, 0, 0, runner, watchdog, metadata);
    }

    public static StagedPhysicalSource StageSource(string repositoryRoot, string stagingRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var stage = Path.GetFullPath(stagingRoot);
        if (Directory.Exists(stage) || File.Exists(stage))
            throw new IOException($"Refusing to reuse physical source staging path {stage}.");
        TrustedPhysicalPath.CreateDirectoryNoReparse(stage, "Physical source staging root");

        var stagedInputs = new List<StagedSourceInput>();
        var stagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in EnumerateSourceInputs(root))
        {
            if (input.FullPath is null)
            {
                stagedInputs.Add(new StagedSourceInput(input.Label, null, input.SentinelValue));
                continue;
            }

            if (!File.Exists(input.FullPath))
                throw new FileNotFoundException($"Required source fingerprint input is missing: {input.Label}.", input.FullPath);
            var repositoryRelative = Path.GetRelativePath(root, input.FullPath);
            var relative = IsInsideRoot(repositoryRelative)
                ? NormalizeRelative(repositoryRelative)
                : $".physical-source-controls/{NormalizeRelative(input.Label)}";
            if (!stagedPaths.Add(relative))
                throw new InvalidDataException($"Physical source staging path collision: {relative}.");
            var destination = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            TrustedPhysicalPath.CreateDirectoryContainedNoReparse(stage, Path.GetDirectoryName(destination)!, "Physical staged source directory");
            File.WriteAllBytes(destination, File.ReadAllBytes(input.FullPath));
            stagedInputs.Add(new StagedSourceInput(input.Label, relative, null));
        }

        var manifestPath = Path.Combine(stage, StagedSourceManifestFileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new StagedSourceManifest(1, stagedInputs.OrderBy(input => input.Label, StringComparer.Ordinal).ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
        return new StagedPhysicalSource(stage, manifestPath, CaptureStagedSource(stage));
    }

    public static HarnessFingerprint CaptureStagedSource(string stagingRoot)
    {
        var stage = Path.GetFullPath(stagingRoot);
        var manifestPath = Path.Combine(stage, StagedSourceManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The staged physical source input manifest is missing.", manifestPath);
        var manifest = JsonSerializer.Deserialize<StagedSourceManifest>(File.ReadAllBytes(manifestPath))
                       ?? throw new InvalidDataException("The staged physical source input manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Inputs.Count == 0)
            throw new InvalidDataException("The staged physical source input manifest contract is invalid.");

        var entries = manifest.Inputs.Select(input => input.RelativePath is null
                ? input.SentinelValue is null
                    ? throw new InvalidDataException($"Staged source sentinel {input.Label} has no value.")
                    : new HarnessFingerprintEntry(
                        input.Label,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.SentinelValue))))
                : new HarnessFingerprintEntry(
                    input.Label,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                        RequireStagedPath(stage, input.RelativePath, input.Label))))))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return CreateFingerprint(entries, "Staged harness source fingerprint input set is empty.");
    }

    public static void RequireStagedSourceMatches(string expectedAggregateSha256, string stagingRoot)
    {
        var actual = CaptureStagedSource(stagingRoot);
        if (!string.Equals(expectedAggregateSha256, actual.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Physical staged source drift detected: expected {expectedAggregateSha256}, actual {actual.AggregateSha256}.");
    }

    public static HarnessFingerprint CaptureRuntime(
        string repositoryRoot,
        string physicalOutputDirectory,
        string watchdogOutputDirectory,
        IEnumerable<string> projectMetadataPaths) => CaptureRuntime(
            repositoryRoot,
            physicalOutputDirectory,
            watchdogOutputDirectory,
            repositoryRoot,
            projectMetadataPaths);

    public static HarnessFingerprint CaptureRuntime(
        string repositoryRoot,
        string physicalOutputDirectory,
        string watchdogOutputDirectory,
        string metadataRoot,
        IEnumerable<string> projectMetadataPaths)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var outputDirectories = new[]
        {
            Path.GetFullPath(physicalOutputDirectory),
            Path.GetFullPath(watchdogOutputDirectory)
        };
        foreach (var directory in outputDirectories)
        {
            RequireInsideRoot(root, directory, "Runtime output directory");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Required physical runtime output is missing: {directory}.");
        }
        RequireSelfContainedPublish(outputDirectories[0], "Moondrop.PhysicalTests");
        RequireSelfContainedPublish(outputDirectories[1], "Moondrop.PhysicalWatchdog");
        var metadata = projectMetadataPaths.ToArray();
        var pathsBefore = EnumerateRuntimePaths(Path.GetFullPath(metadataRoot), outputDirectories, metadata);
        var first = CaptureLabeled(pathsBefore);
        RequireSelfContainedPublish(outputDirectories[0], "Moondrop.PhysicalTests");
        RequireSelfContainedPublish(outputDirectories[1], "Moondrop.PhysicalWatchdog");
        var pathsAfter = EnumerateRuntimePaths(Path.GetFullPath(metadataRoot), outputDirectories, metadata);
        var second = CaptureLabeled(pathsAfter);
        if (!pathsBefore.Select(item => item.Label).SequenceEqual(pathsAfter.Select(item => item.Label), StringComparer.Ordinal) ||
            !string.Equals(first.AggregateSha256, second.AggregateSha256, StringComparison.Ordinal))
            throw new IOException("Physical runtime files changed while their complete manifest was being captured.");
        return second;
    }

    public static void RequireRuntimeMatches(
        string expectedAggregateSha256,
        string repositoryRoot,
        string physicalOutputDirectory,
        string watchdogOutputDirectory,
        IEnumerable<string> projectMetadataPaths)
    {
        if (string.IsNullOrWhiteSpace(expectedAggregateSha256))
            throw new InvalidDataException("Prepared physical runtime manifest hash is missing.");
        var actual = CaptureRuntime(
            repositoryRoot,
            physicalOutputDirectory,
            watchdogOutputDirectory,
            projectMetadataPaths);
        if (!string.Equals(expectedAggregateSha256, actual.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Physical self-contained runtime manifest drift detected: expected {expectedAggregateSha256}, actual {actual.AggregateSha256}.");
    }

    private static void RequireSelfContainedPublish(string directory, string applicationName)
    {
        foreach (var fileName in new[]
                 {
                     $"{applicationName}.exe",
                     $"{applicationName}.dll",
                     $"{applicationName}.deps.json",
                     $"{applicationName}.runtimeconfig.json",
                     "coreclr.dll",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "System.Private.CoreLib.dll"
                 })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                throw new InvalidDataException($"The {applicationName} self-contained publish is missing required runtime file {fileName}.");
        }

        try
        {
            using var deps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, $"{applicationName}.deps.json")));
            using var runtimeConfig = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, $"{applicationName}.runtimeconfig.json")));
            RequireDeclaredRuntimeDependenciesPresent(directory, applicationName, deps.RootElement);
            var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _))
                throw new InvalidDataException($"The {applicationName} runtimeconfig is framework-dependent instead of self-contained.");
            if (!options.TryGetProperty("includedFrameworks", out var included) ||
                included.ValueKind != JsonValueKind.Array ||
                !included.EnumerateArray().Any(framework =>
                    string.Equals(
                        framework.TryGetProperty("name", out var name) ? name.GetString() : null,
                        "Microsoft.NETCore.App",
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"The {applicationName} runtimeconfig does not prove a self-contained Microsoft.NETCore.App runtime.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {applicationName} self-contained deps/runtimeconfig JSON is invalid.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException($"The {applicationName} self-contained runtimeconfig contract is incomplete.", ex);
        }
    }

    private static void RequireDeclaredRuntimeDependenciesPresent(
        string directory,
        string applicationName,
        JsonElement dependencies)
    {
        if (!dependencies.TryGetProperty("runtimeTarget", out var runtimeTarget) ||
            !runtimeTarget.TryGetProperty("name", out var runtimeTargetNameElement) ||
            string.IsNullOrWhiteSpace(runtimeTargetNameElement.GetString()) ||
            !dependencies.TryGetProperty("targets", out var targets) ||
            !targets.TryGetProperty(runtimeTargetNameElement.GetString()!, out var target))
            return;

        foreach (var library in target.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out var runtimeAssets))
                continue;
            foreach (var asset in runtimeAssets.EnumerateObject())
            {
                var fileName = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                if (fileName == "_._")
                    continue;
                if (!File.Exists(Path.Combine(directory, fileName)))
                    throw new InvalidDataException(
                        $"The {applicationName} self-contained dependency closure is missing declared runtime file {fileName} from {library.Name}.");
            }
        }
    }

    public static void RequireCompleteOutputMatches(string expectedDirectory, string actualDirectory, string label)
    {
        var expected = CaptureCompleteOutput(expectedDirectory, label);
        var actual = CaptureCompleteOutput(actualDirectory, label);
        var expectedAgain = CaptureCompleteOutput(expectedDirectory, label);
        var actualAgain = CaptureCompleteOutput(actualDirectory, label);
        if (!string.Equals(expected.Fingerprint.AggregateSha256, expectedAgain.Fingerprint.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(FormatCompleteOutputDifferences(
                expected,
                expectedAgain,
                $"The expected {label} output changed while its complete tree was being compared"));
        if (!string.Equals(actual.Fingerprint.AggregateSha256, actualAgain.Fingerprint.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(FormatCompleteOutputDifferences(
                actual,
                actualAgain,
                $"The actual {label} output changed while its complete tree was being compared"));
        if (!string.Equals(expected.Fingerprint.AggregateSha256, actual.Fingerprint.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException(FormatCompleteOutputDifferences(
                expected,
                actual,
                $"Fresh {label} output does not exactly match the complete files loaded by the supported runtime"));
    }

    public static void RequirePublishedApphostTree(
        string repositoryRoot,
        string runningExecutablePath,
        string freshDirectory,
        string applicationName)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var executable = Path.GetFullPath(runningExecutablePath);
        if (!string.Equals(Path.GetFileName(executable), $"{applicationName}.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Physical execution requires the published {applicationName}.exe apphost directly.");
        var runningDirectory = Path.GetDirectoryName(executable)
                               ?? throw new InvalidDataException("The physical apphost has no containing publish tree.");
        var allowedRoot = Path.Combine(root, "tests-dotnet", "artifacts", "physical-runtime");
        RequireInsideRoot(allowedRoot, runningDirectory, "Published physical apphost directory");
        RequireSelfContainedPublish(runningDirectory, applicationName);
        RequireSelfContainedPublish(Path.GetFullPath(freshDirectory), applicationName);
        RequireCompleteOutputMatches(runningDirectory, freshDirectory, applicationName);
    }

    private static CompleteOutputSnapshot CaptureCompleteOutput(string directory, string label)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Required {label} output directory is missing: {root}.");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relativePath = NormalizeRelative(Path.GetRelativePath(root, path));
                var bytes = File.ReadAllBytes(path);
                return new CompleteOutputFile(
                    relativePath,
                    Path.GetFullPath(path),
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    bytes.LongLength,
                    File.GetLastWriteTimeUtc(path));
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var fingerprint = CreateFingerprint(
            files.Select(file => new HarnessFingerprintEntry($"{label}/{file.RelativePath}", file.Sha256)).ToArray(),
            $"Required {label} output directory is empty.");
        return new CompleteOutputSnapshot(root, fingerprint, files);
    }

    private static string FormatCompleteOutputDifferences(
        CompleteOutputSnapshot expected,
        CompleteOutputSnapshot actual,
        string summary)
    {
        var expectedFiles = expected.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var actualFiles = actual.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var mismatches = expectedFiles.Keys.Concat(actualFiles.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Where(path =>
            {
                var expectedExists = expectedFiles.TryGetValue(path, out var expectedFile);
                var actualExists = actualFiles.TryGetValue(path, out var actualFile);
                return !expectedExists ||
                       !actualExists ||
                       expectedFile!.Size != actualFile!.Size ||
                       !string.Equals(expectedFile.Sha256, actualFile.Sha256, StringComparison.Ordinal);
            })
            .ToArray();
        var details = mismatches.Select(path =>
        {
            expectedFiles.TryGetValue(path, out var expectedFile);
            actualFiles.TryGetValue(path, out var actualFile);
            return $"relativePath={path}; expected={FormatCompleteOutputFile(expectedFile)}; actual={FormatCompleteOutputFile(actualFile)}";
        });
        return $"{summary}. expectedDirectory={expected.Root}; actualDirectory={actual.Root}; " +
               $"mismatchCount={mismatches.Length}. Timestamps are diagnostic only and are not matching inputs." +
               Environment.NewLine + string.Join(Environment.NewLine, details);
    }

    private static string FormatCompleteOutputFile(CompleteOutputFile? file) => file is null
        ? "{exists=false; path=<missing>; sha256=<missing>; size=<missing>; lastWriteTimeUtc=<missing>}"
        : $"{{exists=true; path={file.FullPath}; sha256={file.Sha256}; size={file.Size}; " +
          $"lastWriteTimeUtc={file.LastWriteTimeUtc:O}}}";

    private static string[] EnumerateSourcePaths(string root) => SourceRootFiles.Concat(
            SourceDirectories
                .Select(directory => Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)))
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                .Where(path => !HasBuildSegment(path) && IsSourceInput(path))
                .Select(path => NormalizeRelative(Path.GetRelativePath(root, path))))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static SourceFingerprintInput[] EnumerateSourceInputs(string root)
    {
        var inputs = EnumerateSourcePaths(root)
            .Select(relative => new SourceFingerprintInput(
                relative,
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                ""))
            .ToList();
        foreach (var directory in EnumerateBuildControlDirectories(root))
        foreach (var controlPath in ImplicitBuildControlPaths)
            AddControlInput(inputs, directory.FullPath, directory.Label, controlPath);
        return inputs.OrderBy(input => input.Label, StringComparer.Ordinal).ToArray();
    }

    private static (string Label, string FullPath)[] EnumerateBuildControlDirectories(string repositoryRoot)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeStart in BuildSearchStartDirectories.Prepend("."))
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(Path.Combine(repositoryRoot, relativeStart)));
                 current is not null;
                 current = current.Parent)
            {
                directories.Add(current.FullName);
            }
        }

        var ancestors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ancestorIndex = 1;
        for (var current = Directory.GetParent(repositoryRoot); current is not null; current = current.Parent)
            ancestors[current.FullName] = $"ancestor-{ancestorIndex++:D2}";

        return directories.Select(path =>
            {
                var relative = Path.GetRelativePath(repositoryRoot, path);
                if (relative == ".")
                    return (Label: "repository", FullPath: path);
                if (!Path.IsPathRooted(relative) && relative != ".." &&
                    !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    return (Label: $"repository/{NormalizeRelative(relative)}", FullPath: path);
                return (Label: ancestors[path], FullPath: path);
            })
            .OrderBy(item => item.Label, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddControlInput(
        ICollection<SourceFingerprintInput> inputs,
        string directory,
        string location,
        string fileName)
    {
        var path = Path.Combine(directory, fileName.Replace('/', Path.DirectorySeparatorChar));
        var present = File.Exists(path);
        var prefix = $"build-controls/{location}/{fileName}";
        inputs.Add(new SourceFingerprintInput($"{prefix}.presence", null, present ? "present" : "absent"));
        if (present)
            inputs.Add(new SourceFingerprintInput($"{prefix}.content", path, ""));
    }

    private static HarnessFingerprint CaptureSourceInputs(IEnumerable<SourceFingerprintInput> inputs)
    {
        var entries = inputs.Select(input => input.FullPath is null
                ? new HarnessFingerprintEntry(
                    input.Label,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.SentinelValue))))
                : !File.Exists(input.FullPath)
                    ? throw new FileNotFoundException($"Required source fingerprint input is missing: {input.Label}.", input.FullPath)
                    : new HarnessFingerprintEntry(
                        input.Label,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.FullPath)))))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return CreateFingerprint(entries, "Harness source fingerprint input set is empty.");
    }

    private static (string Label, string FullPath)[] EnumerateRuntimePaths(
        string root,
        IReadOnlyList<string> outputDirectories,
        IEnumerable<string> projectMetadataPaths) => outputDirectories
        .Select((directory, index) => (Directory: directory, Label: index == 0 ? "physical-tests" : "watchdog"))
        .SelectMany(output => Directory.EnumerateFiles(output.Directory, "*", SearchOption.AllDirectories)
            .Select(path => (
                Label: $"{output.Label}/{NormalizeRelative(Path.GetRelativePath(output.Directory, path))}",
                FullPath: path)))
        .Concat(projectMetadataPaths.Select(relative => (
            Label: $"metadata/{NormalizeRelative(relative)}",
            FullPath: Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))))
        .OrderBy(item => item.Label, StringComparer.Ordinal)
        .ToArray();

    public static HarnessFingerprint Capture(string repositoryRoot, IEnumerable<string> relativePaths)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var entries = relativePaths
            .Select(NormalizeRelative)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => CaptureFile(root, path))
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidDataException("Harness fingerprint input set is empty.");
        var manifest = string.Join('\n', entries.Select(entry => $"{entry.RelativePath}\0{entry.Sha256}"));
        var aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        return new HarnessFingerprint("SHA-256", aggregate, entries);
    }

    public static void RequireMatches(string expectedAggregateSha256, string repositoryRoot, IEnumerable<string> relativePaths)
    {
        if (string.IsNullOrWhiteSpace(expectedAggregateSha256))
            throw new InvalidDataException("Prepared harness fingerprint is missing.");
        var actual = Capture(repositoryRoot, relativePaths);
        if (!string.Equals(expectedAggregateSha256, actual.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Harness fingerprint drift detected: expected {expectedAggregateSha256}, actual {actual.AggregateSha256}.");
    }

    private static HarnessFingerprintEntry CaptureFile(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Fingerprint input escapes the repository root: {relativePath}.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Required harness fingerprint input is missing: {relativePath}.", fullPath);
        return new HarnessFingerprintEntry(relativePath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))));
    }

    private static HarnessFingerprint CaptureLabeled(IEnumerable<(string Label, string FullPath)> labeledPaths)
    {
        var entries = labeledPaths
            .Select(item => !File.Exists(item.FullPath)
                ? throw new FileNotFoundException($"Required runtime manifest input is missing: {item.Label}.", item.FullPath)
                : new HarnessFingerprintEntry(
                    NormalizeRelative(item.Label),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.FullPath)))))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return CreateFingerprint(entries, "Runtime manifest input set is empty.");
    }

    private static HarnessFingerprint CreateFingerprint(HarnessFingerprintEntry[] entries, string emptyMessage)
    {
        if (entries.Length == 0)
            throw new InvalidDataException(emptyMessage);
        var manifest = string.Join('\n', entries.Select(entry => $"{entry.RelativePath}\0{entry.Sha256}"));
        return new HarnessFingerprint(
            "SHA-256",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
            entries);
    }

    private static void RequireInsideRoot(string root, string path, string description)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"{description} escapes the repository root: {path}.");
    }

    internal static void RequireInsideRootForBuild(string root, string path, string description) =>
        RequireInsideRoot(Path.GetFullPath(root), Path.GetFullPath(path), description);

    private static string RequireStagedPath(string stagingRoot, string relativePath, string label)
    {
        var fullPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        RequireInsideRoot(stagingRoot, fullPath, "Staged physical source input");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Required staged source fingerprint input is missing: {label}.", fullPath);
        return fullPath;
    }

    private static bool IsInsideRoot(string relativePath) =>
        !Path.IsPathRooted(relativePath) &&
        !relativePath.Equals("..", StringComparison.Ordinal) &&
        !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool HasBuildSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    private static bool IsSourceInput(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".rsp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(path), "packages.lock.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').TrimStart('/');
}
