using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One planned handoff's migration: the successor owns the inventory and all writes.
internal sealed class ConductorHandoffSchemaUpgrade(
    string orchestratorDirectory,
    string workingDirectory,
    Func<string, IReadOnlyList<string>, CancellationToken, ConductorHandoffSchemaUpgrade.ProcessOutput>? run = null,
    string? dotnetPath = null,
    TimeProvider? timeProvider = null,
    TimeSpan? setupTimeout = null)
{
    internal sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr,
        bool TerminationConfirmed = true);

    internal sealed record Outcome(bool Applied, bool Failed, bool SetupStarted,
        bool TerminationConfirmed, string Reason, string BackupDirectory,
        IReadOnlyList<StoreSetupPlanEntry> Before, IReadOnlyList<StoreSetupPlanEntry>? After,
        string Detail)
    {
        internal string Stores => string.Join(",", Before.Where(e => e.RequiresMigration)
            .Select(e => $"{e.StoreName}:{e.RecordedVersion ?? 0}->{e.RequiredVersion}"));
        internal string Progress => "completed=" + string.Join(",", Before.Where(e =>
                After?.Any(a => a.StoreName == e.StoreName && a.RecordedVersion == e.RequiredVersion) == true)
            .Select(e => e.StoreName)) + " pending=" + string.Join(",", Before.Where(e =>
                After?.Any(a => a.StoreName == e.StoreName && a.RecordedVersion == e.RequiredVersion) != true)
            .Select(e => e.StoreName));
    }

    internal Outcome Execute(string successorDll, string commit, IReadOnlyList<string> bindingArguments,
        bool previousChildExited, string stdoutPath, CancellationToken cancellationToken)
    {
        var before = TryPlan(successorDll, bindingArguments, cancellationToken);
        if (before is null)
            return new(false, false, false, true, "plan-unavailable", "none", [], null, "");
        if (before.Any(e => e.IsNewer) || !before.Any(e => e.RequiresMigration))
            return new(false, false, false, true, "unchanged", "none", before, null, "");

        var backup = "none";
        var reason = "quiescence";
        var setupStarted = false;
        var terminationConfirmed = true;
        var replanned = false;
        IReadOnlyList<StoreSetupPlanEntry>? after = null;
        try
        {
            if (!previousChildExited || ConductorLoopLease.IsActive(orchestratorDirectory))
                throw new InvalidOperationException("Previous child or conductor owner is still active.");
            Directory.CreateDirectory(orchestratorDirectory);
            // Probe exclusive ownership without declaring the supervisor a loop owner.
            // DeleteOnClose releases/removes our probe atomically before setup takes its own lease.
            using (var loopLock = new FileStream(Path.Combine(orchestratorDirectory, "conduct-loop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                if (loopLock.Length > 0)
                {
                    using var reader = new StreamReader(loopLock, leaveOpen: true);
                    var pidText = reader.ReadLine();
                    _ = reader.ReadLine();
                    var startedText = reader.ReadLine();
                    if (!int.TryParse(pidText, out var pid))
                        throw new InvalidOperationException("Invalid conductor lock owner.");
                    var probe = new ConductLockPidProbe();
                    var active = DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var started)
                        ? probe.IsSameProcess(pid, started) : probe.IsRunning(pid);
                    if (active) throw new InvalidOperationException("Conductor lock names a live owner.");
                }
                reason = DrainFailure(stdoutPath);
                if (reason.Length > 0) throw new InvalidOperationException("Exited child's relaunch log does not prove quiescence.");
                reason = "backup";
                var safeCommit = string.Concat(commit.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
                if (safeCommit.Length == 0) safeCommit = "unknown";
                backup = Path.Combine(orchestratorDirectory, "backups",
                    $"pre-upgrade-{(timeProvider ?? TimeProvider.System).GetUtcNow():yyyyMMddTHHmmssZ}-{safeCommit}");
                if (Directory.Exists(backup)) throw new IOException("Pre-upgrade backup directory already exists.");
                Directory.CreateDirectory(backup);
                foreach (var path in before.Where(e => e.RequiresMigration).Select(e => e.DatabasePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
                {
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                    }.ToString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(backup, Path.GetFileName(path)), Pooling = false
                    }.ToString());
                    source.Open();
                    target.Open();
                    source.BackupDatabase(target);
                }
            }

            reason = "setup";
            setupStarted = true;
            var setup = RunChild(successorDll, ["conductor", "setup", .. bindingArguments], cancellationToken);
            terminationConfirmed = setup.TerminationConfirmed;
            if (terminationConfirmed)
            {
                replanned = true;
                after = TryPlan(successorDll, bindingArguments, cancellationToken);
            }
            if (setup.ExitCode != 0 || !terminationConfirmed)
                throw new InvalidOperationException($"exit={setup.ExitCode} terminationConfirmed={terminationConfirmed} {setup.Stderr} {setup.Stdout}");
            reason = "re-plan";
            if (after is null || before.Any(e => !after.Any(a => a.StoreName == e.StoreName && a.RequiredVersion == e.RequiredVersion)) ||
                after.Any(e => e.IsNewer || e.RequiresMigration))
                throw new InvalidOperationException("Successor re-plan does not report every store current.");
            return new(true, false, true, true, "applied", backup, before, after, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (setupStarted && terminationConfirmed && !replanned)
                after = TryPlan(successorDll, bindingArguments, cancellationToken);
            return new(false, true, setupStarted, terminationConfirmed, reason, backup, before, after, ex.Message);
        }
    }

    private static string DrainFailure(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Any(l => l.StartsWith("LOOP_RELAUNCH_DETACH ", StringComparison.Ordinal)))
            return "detached-workers";
        var drain = Array.FindLastIndex(lines, l => l.StartsWith("LOOP_RELAUNCH_DRAIN ", StringComparison.Ordinal));
        var rebuild = Array.FindLastIndex(lines, l => l.StartsWith("LOOP_RELAUNCH_REBUILD ", StringComparison.Ordinal));
        static bool Has(string line, string field) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(field);
        return drain >= 0 && Has(lines[drain], "active=0") && Has(lines[drain], "liveGates=0") || rebuild > drain
            ? "" : "quiescence";
    }

    private IReadOnlyList<StoreSetupPlanEntry>? TryPlan(string dll, IReadOnlyList<string> arguments, CancellationToken token)
    {
        try
        {
            var output = RunChild(dll, ["conductor", "setup", "--plan", .. arguments], token);
            return output.TerminationConfirmed ? ParsePlan(output.Stdout) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal IReadOnlyList<StoreSetupPlanEntry>? ParsePlan(string text)
    {
        var entries = new List<StoreSetupPlanEntry>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("repository: ", StringComparison.Ordinal) ||
                line.StartsWith("Forward migration requires exclusive conductor ownership: ", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line,
                @"^(?<name>[\w-]+): recorded (?<from>missing|[0-9]+); required (?<to>[0-9]+); action (?<action>refuse-newer|migrate|current) \((?<path>.+)\)$");
            if (!match.Success || !int.TryParse(match.Groups["to"].Value, out var required) || required < 1)
                return null;
            int? recorded = null;
            if (match.Groups["from"].Value != "missing")
            {
                if (!int.TryParse(match.Groups["from"].Value, out var version)) return null;
                recorded = version;
            }
            var path = Path.GetFullPath(match.Groups["path"].Value);
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(orchestratorDirectory), StringComparison.OrdinalIgnoreCase))
                return null;
            var entry = new StoreSetupPlanEntry(match.Groups["name"].Value, path, recorded, required);
            if (entry.Action != match.Groups["action"].Value || entries.Any(e => e.StoreName == entry.StoreName)) return null;
            entries.Add(entry);
        }
        return entries.Count > 0 ? entries : null;
    }

    private ProcessOutput RunChild(string dll, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(setupTimeout ?? TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        if (run is not null) return run(dll, arguments, linked.Token);
        var prefix = Path.Combine(orchestratorDirectory, "continuity", $"schema-{Guid.NewGuid():N}");
        var request = new ConductorSupervisorProcessRequest(arguments, workingDirectory, prefix + ".json",
            prefix + ".out.log", prefix + ".err.log",
            [dotnetPath ?? Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet", dll]);
        try
        {
            var result = new SystemConductorSupervisorProcessHost().RunAsync(request, linked.Token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            return new(result.ExitCode, File.ReadAllText(request.StdoutPath), File.ReadAllText(request.StderrPath), result.TerminationConfirmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A process-host exception cannot prove a started setup child has stopped.
            return new(-1, "", ex.Message, TerminationConfirmed: false);
        }
    }
}
