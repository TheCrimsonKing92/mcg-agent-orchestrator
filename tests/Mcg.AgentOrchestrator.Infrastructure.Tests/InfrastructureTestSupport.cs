global using static InfrastructureTestSupport;

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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
    Func<Process, bool>? startProcess = null)
{
    var command = $"git {string.Join(' ', arguments)}";
    var commandEnvironmentNames = commandEnvironment is null
        ? "none"
        : string.Join(',', commandEnvironment.Keys.Order(StringComparer.Ordinal));
    var environmentContract =
        "allow=PATH,PATHEXT,SystemRoot,WINDIR,COMSPEC,TEMP,TMP; " +
        "pinned=GIT_CONFIG_NOSYSTEM,GIT_CONFIG_GLOBAL,GIT_TERMINAL_PROMPT,GCM_INTERACTIVE,LC_ALL,LANG; " +
        "repositorySelection=unset; command=" + commandEnvironmentNames;
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };

    var inherited = startInfo.Environment;
    var allowedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
    {
        if (inherited.TryGetValue(variable, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            allowedEnvironment[variable] = value;
        }
    }

    startInfo.Environment.Clear();
    foreach (var (name, value) in allowedEnvironment)
    {
        startInfo.Environment[name] = value;
    }

    startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    startInfo.Environment["GCM_INTERACTIVE"] = "Never";
    startInfo.Environment["LC_ALL"] = "C";
    startInfo.Environment["LANG"] = "C";
    if (commandEnvironment is not null)
    {
        foreach (var (name, value) in commandEnvironment)
        {
            startInfo.Environment[name] = value;
        }
    }

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    Process? process = null;
    var processStarted = false;
    try
    {
        process = new Process { StartInfo = startInfo };
        processStarted = (startProcess ?? (static candidate => candidate.Start()))(process);
        if (!processStarted)
        {
            return WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult.NotStarted(
                command,
                "Process.Start returned false.") with { EnvironmentContract = environmentContract };
        }

        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
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
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            ProcessStarted: true,
            ExitCode: process.HasExited ? process.ExitCode : null,
            StandardOutput: outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty,
            StandardError: errorTask.IsCompletedSuccessfully
                ? string.Join(" | ", new[] { errorTask.Result, drainError }.Where(value => !string.IsNullOrWhiteSpace(value)))
                : drainError,
            DrainTimedOut: !drainCompleted,
            TimedOut: timedOut,
            DrainFailed: drainFailed,
            EnvironmentContract: environmentContract);
    }
    catch (Exception ex)
    {
        return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
            command,
            processStarted,
            processStarted && process is { HasExited: true } ? process.ExitCode : null,
            string.Empty,
            ex.Message,
            DrainTimedOut: false,
            TimedOut: false,
            DrainFailed: false,
            EnvironmentContract: environmentContract);
    }
    finally
    {
        process?.Dispose();
    }
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

