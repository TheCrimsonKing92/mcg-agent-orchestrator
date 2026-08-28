global using static InfrastructureTestSupport;

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class InfrastructureTestSupport
{
private static readonly SemaphoreSlim ProtectedPidEnvironmentLock = new(1, 1);

public static string CaptureConsole(Action action) => AsyncLocalConsoleRouter.Capture(action);

public static string CaptureConsoleError(Action action) => AsyncLocalConsoleRouter.CaptureError(action);

public static ModelRequest TestRequest(ModelOptions? options = null)
{
    return new ModelRequest(
        "system prompt",
        [new ModelMessage("user", "do work")],
        options ?? new ModelOptions(Temperature: 0.2, MaxOutputTokens: 123));
}

public static IEnumerable<string> GetRequiredHeader(HttpRequestMessage request, string name)
{
    return request.Headers.TryGetValues(name, out var values)
        ? values
        : throw new InvalidOperationException($"Header '{name}' was not found.");
}

public static string CreateTempDirectory()
    => SharedTestSupport.CreateTempDirectory();

public static string ResolveDotnetHostPath()
{
    var hostFileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    var candidates = new List<string?>
    {
        string.Equals(Path.GetFileName(Environment.ProcessPath), hostFileName, StringComparison.OrdinalIgnoreCase)
            ? Environment.ProcessPath
            : null,
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    };

    foreach (var rootVariable in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
    {
        var root = Environment.GetEnvironmentVariable(rootVariable);
        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(Path.Combine(root, hostFileName));
        }
    }

    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        candidates.Add(Path.Combine(directory, hostFileName));
    }

    var hostPath = candidates.FirstOrDefault(path =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    return hostPath is null
        ? throw new InvalidOperationException("Could not resolve the dotnet host for a managed test process.")
        : Path.GetFullPath(hostPath);
}

// Compatibility helpers for the existing GoalWorktree fixture commit retry. New verdict paths use
// the typed GitProbeResult directly so stderr, launch, exit, drain, and timeout evidence survive.
public static string? TryGetGitHead(string workingDirectory)
{
    var head = RunGitProbe(workingDirectory, ["rev-parse", "--verify", "HEAD"]);
    return head.Succeeded && !string.IsNullOrWhiteSpace(head.StandardOutput)
        ? head.StandardOutput.Trim()
        : null;
}

public static bool HasNewCommittedCleanGitHead(string workingDirectory, string? previousHead)
{
    var currentHead = TryGetGitHead(workingDirectory);
    if (currentHead is null || string.Equals(currentHead, previousHead, StringComparison.Ordinal))
    {
        return false;
    }

    var status = RunGitProbe(workingDirectory, ["status", "--short"]);
    return status.Succeeded && string.IsNullOrWhiteSpace(status.StandardOutput);
}

internal static WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult RunGitProbe(
    string workingDirectory,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string>? commandEnvironment = null,
    Func<Process, bool>? startProcess = null,
    IReadOnlyDictionary<string, string?>? inheritedEnvironment = null,
    Action<string, string>? beforeOwnedCaptureRead = null)
{
    var executable = ResolveNativeGitExecutable(inheritedEnvironment);
    var effectiveArguments = new[]
    {
        "-c", "core.fsmonitor=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false"
    }.Concat(arguments).ToArray();
    var command = FormatCommand(executable, effectiveArguments);
    var commandEnvironmentNames = commandEnvironment is null
        ? "none"
        : string.Join(',', commandEnvironment.Keys.Order(StringComparer.Ordinal));
    var environmentContract =
        "allow=PATH,PATHEXT,SystemRoot,WINDIR,COMSPEC,TEMP,TMP; " +
        "pinned=GIT_CONFIG_NOSYSTEM,GIT_CONFIG_GLOBAL,GIT_TERMINAL_PROMPT,GCM_INTERACTIVE,GIT_OPTIONAL_LOCKS,LC_ALL,LANG; " +
        "repositorySelection=unset; command=" + commandEnvironmentNames;
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };

    var allowedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
    {
        var found = inheritedEnvironment is null
            ? startInfo.Environment.TryGetValue(variable, out var value)
            : inheritedEnvironment.TryGetValue(variable, out value);
        if (found && !string.IsNullOrWhiteSpace(value))
        {
            allowedEnvironment[variable] = value;
        }
    }

    startInfo.Environment.Clear();
    foreach (var (name, value) in allowedEnvironment)
    {
        startInfo.Environment[name] = value;
    }

    if (commandEnvironment is not null)
    {
        foreach (var (name, value) in commandEnvironment)
        {
            startInfo.Environment[name] = value;
        }
    }

    foreach (var variable in new[]
             {
                 "GIT_DIR",
                 "GIT_WORK_TREE",
                 "GIT_INDEX_FILE",
                 "GIT_OBJECT_DIRECTORY",
                 "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                 "GIT_COMMON_DIR"
             })
    {
        startInfo.Environment.Remove(variable);
    }

    startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    startInfo.Environment["GCM_INTERACTIVE"] = "Never";
    startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
    startInfo.Environment["LC_ALL"] = "C";
    startInfo.Environment["LANG"] = "C";

    foreach (var argument in effectiveArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    if (OperatingSystem.IsWindows() && startProcess is null)
    {
        return RunGitProbeWithOwnedFileCapture(
            startInfo,
            workingDirectory,
            command,
            environmentContract + "; capture=owned-file-handles; inheritedHandles=stdout,stderr",
            beforeOwnedCaptureRead);
    }

    startInfo.RedirectStandardInput = true;
    startInfo.RedirectStandardOutput = true;
    startInfo.RedirectStandardError = true;
    startInfo.StandardOutputEncoding = Encoding.UTF8;
    startInfo.StandardErrorEncoding = Encoding.UTF8;

    Process? process = null;
    var processStarted = false;
    int? childProcessId = null;
    DateTimeOffset? childStartedAt = null;
    try
    {
        process = new Process { StartInfo = startInfo };
        processStarted = (startProcess ?? (static candidate => candidate.Start()))(process);
        command = FormatCommand(startInfo.FileName, startInfo.ArgumentList);
        if (!processStarted)
        {
            return WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.NotStarted(
                command,
                "Process.Start returned false.") with
                {
                    Executable = startInfo.FileName,
                    Arguments = startInfo.ArgumentList.ToArray(),
                    RepositoryDirectory = Path.GetFullPath(workingDirectory),
                    EnvironmentContract = environmentContract
                };
        }

        childProcessId = process.Id;
        try
        {
            childStartedAt = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            childStartedAt = null;
        }

        process.StandardInput.Close();
        var outputTask = CaptureStreamAsync(process.StandardOutput.BaseStream);
        var errorTask = CaptureStreamAsync(process.StandardError.BaseStream);
        var timedOut = false;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        var drainTask = Task.WhenAll(outputTask, errorTask);
        var drainCompleted = Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5)))
            .GetAwaiter()
            .GetResult() == drainTask;
        var drainFailed =
            (drainCompleted && drainTask.IsFaulted) ||
            outputTask.IsFaulted ||
            errorTask.IsFaulted;
        var drainError = drainFailed
            ? string.Join(
                " | ",
                new[] { outputTask.Exception?.GetBaseException().Message, errorTask.Exception?.GetBaseException().Message }
                    .Where(message => !string.IsNullOrWhiteSpace(message)))
            : string.Empty;
        var standardOutputCapture = outputTask.IsCompletedSuccessfully ? outputTask.Result : CapturedStream.Empty;
        var standardErrorCapture = errorTask.IsCompletedSuccessfully ? errorTask.Result : CapturedStream.Empty;
        var standardOutput = Encoding.UTF8.GetString(standardOutputCapture.Prefix);
        var childStandardError = Encoding.UTF8.GetString(standardErrorCapture.Prefix);
        var standardError = errorTask.IsCompletedSuccessfully
            ? string.Join(" | ", new[] { childStandardError, drainError }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : drainError;
        var boundedOutput = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(standardOutput);
        var boundedError = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(standardError);
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            ProcessStarted: true,
            ExitCode: process.HasExited ? process.ExitCode : null,
            StandardOutput: boundedOutput,
            StandardError: boundedError,
            DrainTimedOut: !drainCompleted,
            TimedOut: timedOut,
            DrainFailed: drainFailed,
            EnvironmentContract: environmentContract,
            Executable: startInfo.FileName,
            Arguments: startInfo.ArgumentList.ToArray(),
            RepositoryDirectory: Path.GetFullPath(workingDirectory),
            StandardOutputByteCount: standardOutputCapture.ByteCount,
            StandardErrorByteCount: standardErrorCapture.ByteCount,
            StandardOutputTruncated: standardOutputCapture.PrefixTruncated || boundedOutput.Length != standardOutput.Length,
            StandardErrorTruncated: standardErrorCapture.PrefixTruncated || boundedError.Length != standardError.Length,
            ChildProcessId: childProcessId,
            ChildStartedAt: childStartedAt,
            Classification: ClassifyGitProbe(
                processStarted: true,
                process.HasExited ? process.ExitCode : null,
                timedOut,
                drainTimedOut: !drainCompleted,
                drainFailed));
    }
    catch (Exception ex)
    {
        int? observedExitCode = null;
        if (processStarted && process is not null)
        {
            try
            {
                observedExitCode = process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // The parent-side observation failed after the start seam reported success.
                // That is apparatus evidence, not a child nonzero exit.
            }
        }

        var boundedError = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(ex.Message);
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            processStarted,
            observedExitCode,
            string.Empty,
            boundedError,
            DrainTimedOut: false,
            TimedOut: false,
            DrainFailed: false,
            EnvironmentContract: environmentContract,
            Executable: startInfo.FileName,
            Arguments: startInfo.ArgumentList.ToArray(),
            RepositoryDirectory: Path.GetFullPath(workingDirectory),
            StandardErrorByteCount: Encoding.UTF8.GetByteCount(ex.Message),
            StandardErrorTruncated: boundedError.Length != ex.Message.Length,
            ChildProcessId: childProcessId,
            ChildStartedAt: childStartedAt,
            Classification: processStarted
                ? WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.ProcessObservationFailure
                : WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.LaunchFailure);
    }
    finally
    {
        process?.Dispose();
    }
}

private static WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult RunGitProbeWithOwnedFileCapture(
    ProcessStartInfo startInfo,
    string workingDirectory,
    string command,
    string environmentContract,
    Action<string, string>? beforeCaptureRead)
{
    var repositoryDirectory = Path.GetFullPath(workingDirectory);
    // Capture files must live outside the probed repository tree. Seeded-repository fixtures
    // copy .git while probes are active; putting transient captures in Git metadata makes that
    // copy race probe cleanup even though the files are not tracked.
    var captureDirectory = Path.GetTempPath();
    var captureIdentity = $".mcg-git-probe-{Guid.NewGuid():N}";
    var standardOutputPath = Path.Combine(captureDirectory, captureIdentity + ".stdout");
    var standardErrorPath = Path.Combine(captureDirectory, captureIdentity + ".stderr");
    OwnedProcessGroup.SuspendedProcessStart? launch = null;
    Process? process = null;
    var processStarted = false;
    int? childProcessId = null;
    DateTimeOffset? childStartedAt = null;
    try
    {
        launch = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(
            startInfo,
            standardOutputPath,
            standardErrorPath);
        process = launch.Process;
        processStarted = true;
        command = FormatCommand(startInfo.FileName, startInfo.ArgumentList);
        childProcessId = process.Id;
        try
        {
            childStartedAt = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            childStartedAt = null;
        }

        launch.Resume();
        var timedOut = false;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                launch.Group.Kill();
                try { process.WaitForExit(5000); } catch { }
            }
        }

        beforeCaptureRead?.Invoke(standardOutputPath, standardErrorPath);
        var standardOutputCapture = CaptureFile(standardOutputPath);
        var standardErrorCapture = CaptureFile(standardErrorPath);
        var standardOutput = Encoding.UTF8.GetString(standardOutputCapture.Prefix);
        var standardError = Encoding.UTF8.GetString(standardErrorCapture.Prefix);
        var boundedOutput = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(standardOutput);
        var boundedError = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(standardError);
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            ProcessStarted: true,
            ExitCode: process.HasExited ? process.ExitCode : null,
            StandardOutput: boundedOutput,
            StandardError: boundedError,
            DrainTimedOut: false,
            TimedOut: timedOut,
            DrainFailed: false,
            EnvironmentContract: environmentContract,
            Executable: startInfo.FileName,
            Arguments: startInfo.ArgumentList.ToArray(),
            RepositoryDirectory: repositoryDirectory,
            StandardOutputByteCount: standardOutputCapture.ByteCount,
            StandardErrorByteCount: standardErrorCapture.ByteCount,
            StandardOutputTruncated: standardOutputCapture.PrefixTruncated || boundedOutput.Length != standardOutput.Length,
            StandardErrorTruncated: standardErrorCapture.PrefixTruncated || boundedError.Length != standardError.Length,
            ChildProcessId: childProcessId,
            ChildStartedAt: childStartedAt,
            Classification: ClassifyGitProbe(
                processStarted: true,
                process.HasExited ? process.ExitCode : null,
                timedOut,
                drainTimedOut: false,
                drainFailed: false));
    }
    catch (Exception ex)
    {
        int? observedExitCode = null;
        if (processStarted && process is not null)
        {
            try
            {
                observedExitCode = process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // The parent-side observation failed after the native launch created the Git child.
            }
        }

        var boundedError = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.BoundDiagnostic(ex.Message);
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            processStarted,
            observedExitCode,
            string.Empty,
            boundedError,
            DrainTimedOut: false,
            TimedOut: false,
            DrainFailed: false,
            EnvironmentContract: environmentContract,
            Executable: startInfo.FileName,
            Arguments: startInfo.ArgumentList.ToArray(),
            RepositoryDirectory: repositoryDirectory,
            StandardErrorByteCount: Encoding.UTF8.GetByteCount(ex.Message),
            StandardErrorTruncated: boundedError.Length != ex.Message.Length,
            ChildProcessId: childProcessId,
            ChildStartedAt: childStartedAt,
            Classification: processStarted
                ? WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.ProcessObservationFailure
                : WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.LaunchFailure);
    }
    finally
    {
        launch?.Dispose();
        TryDeleteCaptureFile(standardOutputPath);
        TryDeleteCaptureFile(standardErrorPath);
    }
}

private static CapturedStream CaptureFile(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    return CaptureStreamAsync(stream).GetAwaiter().GetResult();
}

private static void TryDeleteCaptureFile(string path)
{
    try
    {
        File.Delete(path);
    }
    catch
    {
        // Best-effort cleanup: do not hide the probe result if a failed child still holds a temporary capture.
    }
}

private static string ResolveNativeGitExecutable(
    IReadOnlyDictionary<string, string?>? inheritedEnvironment)
{
    string? path = null;
    if (inheritedEnvironment is not null)
    {
        path = inheritedEnvironment
            .FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    path ??= Environment.GetEnvironmentVariable("PATH");
    var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
    foreach (var directory in (path ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var candidate = Path.Combine(directory.Trim('"'), executableName);
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }
    }

    return executableName;
}

private static string FormatCommand(string executable, IEnumerable<string> arguments) =>
    $"{executable} {string.Join(' ', arguments)}";

private static async Task<CapturedStream> CaptureStreamAsync(Stream stream)
{
    const int maximumRetainedBytes = 16 * 1024;
    var readBuffer = new byte[8192];
    using var retained = new MemoryStream(maximumRetainedBytes);
    long byteCount = 0;
    var prefixTruncated = false;
    int read;
    while ((read = await stream.ReadAsync(readBuffer).ConfigureAwait(false)) > 0)
    {
        byteCount += read;
        var retainCount = Math.Min(read, maximumRetainedBytes - (int)retained.Length);
        if (retainCount > 0)
        {
            retained.Write(readBuffer, 0, retainCount);
        }

        prefixTruncated |= retainCount != read;
    }

    return new CapturedStream(byteCount, retained.ToArray(), prefixTruncated);
}

private sealed record CapturedStream(long ByteCount, byte[] Prefix, bool PrefixTruncated)
{
    internal static CapturedStream Empty { get; } = new(0, [], false);
}

private static WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification ClassifyGitProbe(
    bool processStarted,
    int? exitCode,
    bool timedOut,
    bool drainTimedOut,
    bool drainFailed)
{
    if (!processStarted)
        return WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.LaunchFailure;
    if (timedOut)
        return WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.ProcessTimeout;
    if (drainTimedOut)
        return WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.DrainTimeout;
    if (drainFailed)
        return WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.DrainFailure;
    return exitCode == 0
        ? WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.Success
        : WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit;
}

public static IDisposable ClearProtectedPidEnvironment()
{
    ProtectedPidEnvironmentLock.Wait();
    var previous = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
    Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, null);
    return new EnvironmentRestore(CliProtectedProcessEnvironment.ProtectedPidVariable, previous, ProtectedPidEnvironmentLock);
}

public static OrchestratorWorkspace CreateRefinedWorkspace(string root)
    => (OrchestratorWorkspace)SharedTestSupport.CreateRefinedWorkspaceOpaque(root);

public static Goal MarkGoalRefined(
    AgentOrchestratorKernel kernel,
    Goal goal,
    string? decision = null)
{
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        goal.Objective,
        [decision ?? "Test fixture goal is already refined."],
        VerificationClass.TestVerifiable,
        [],
        []));
    return goal;
}

public static void SeedLocalSkillCatalog(string workingDirectory)
    => SharedTestSupport.SeedLocalSkillCatalog(workingDirectory);

public static SqliteOrchestratorStateRepository CreateMigratedStateRepository(
    string databasePath,
    Action<string>? statementObserver = null,
    SqliteWriteTelemetryOptions? telemetryOptions = null,
    Action? beforeOutboxCommit = null)
{
    _ = StateDbMigrations.EnsureUpToDate(databasePath);
    return new SqliteOrchestratorStateRepository(
        databasePath,
        statementObserver,
        telemetryOptions,
        beforeOutboxCommit);
}

public static void CreateVersion7StateOutboxFixture(
    string databasePath,
    bool includeOutboxIndex = true)
{
    _ = StateDbMigrations.EnsureUpToDate(databasePath);
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
    connection.Open();

    static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    Execute(connection, "BEGIN IMMEDIATE");
    try
    {
        Execute(connection, """
            CREATE TABLE state_outbox_v7 (
                id           TEXT PRIMARY KEY,
                kind         TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at   TEXT NOT NULL
            )
            """);
        Execute(connection, """
            INSERT INTO state_outbox_v7 (id, kind, payload_json, created_at)
            SELECT id, kind, payload_json, created_at
            FROM state_outbox
            """);
        Execute(connection, "DROP TABLE state_outbox");
        Execute(connection, "ALTER TABLE state_outbox_v7 RENAME TO state_outbox");
        if (includeOutboxIndex)
            Execute(connection, "CREATE INDEX ix_state_outbox_kind ON state_outbox(kind)");
        Execute(connection, "DELETE FROM schema_migrations WHERE migration_number > 7");
        Execute(connection, "COMMIT");
    }
    catch
    {
        try { Execute(connection, "ROLLBACK"); } catch { }
        throw;
    }
}

public static void SeedSpecRefinerBinding(OrchestratorWorkspace workspace)
    => SharedTestSupport.SeedSpecRefinerBinding(workspace);

public static string BacklogStorePathFor(string root) =>
    Path.Combine(root, ".orchestrator", "backlog.db");

// Seeds the SQLite backlog store under <root> from a "## Heading\nbody" markdown string, so tests can
// keep their existing fixtures while the planners read from the store instead of a BACKLOG.md file.
public static void SeedBacklog(string root, string markdown)
{
    var store = new BacklogStore(BacklogStorePathFor(root));
    string? title = null;
    var body = new System.Text.StringBuilder();
    void Flush()
    {
        if (title is not null)
        {
            store.AddAsync(title, body.ToString().Trim()).GetAwaiter().GetResult();
        }
    }

    foreach (var raw in markdown.Split('\n'))
    {
        var line = raw.TrimEnd('\r');
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            Flush();
            title = line[3..].Trim();
            body.Clear();
        }
        else if (title is not null && !line.StartsWith("# ", StringComparison.Ordinal))
        {
            body.AppendLine(line);
        }
    }

    Flush();
}

public static Process StartPrototypeDashboardProcess(string appProject, string workingDirectory, string url) =>
    SharedTestSupport.StartPrototypeDashboardProcess(appProject, workingDirectory, url);

public static Process StartDashboardProcess(string appProject, string workingDirectory, string command, string url)
    => SharedTestSupport.StartDashboardProcess(appProject, workingDirectory, command, url);

public static async Task WaitForHealthAsync(HttpClient client, string url, Process process)
    => await SharedTestSupport.WaitForHealthAsync(client, url, process);

public static int GetAvailablePort()
    => SharedTestSupport.GetAvailablePort();

public static string FindRepositoryRoot()
    => SharedTestSupport.FindRepositoryRoot();

}

internal sealed class EnvironmentRestore(string variableName, string? previousValue, SemaphoreSlim gate) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Environment.SetEnvironmentVariable(variableName, previousValue);
        gate.Release();
        _disposed = true;
    }
}

internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return _responseFactory(request);
    }
}

