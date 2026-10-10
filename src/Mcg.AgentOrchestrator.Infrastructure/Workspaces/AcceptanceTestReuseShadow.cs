using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Attempt-scoped observation only: no shadow state participates in cache or gate decisions.
internal sealed class AcceptanceTestReuseShadow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly object _gate = new();
    private readonly Dictionary<(string Class, string Check), AcceptanceTestReuseShadowObservation> _rows = [];
    private readonly Lazy<PlanState> _plan;
    private readonly string _worktreePath;
    private readonly string _goalId;
    private readonly string _attemptId;
    private readonly string _mainSha;
    private readonly string _candidateTreeSha;

    internal AcceptanceTestReuseShadow(
        string worktreePath, string goalId, string attemptId, string mainSha,
        string candidateTreeSha, string verifyingCommitSha,
        Func<string, string, string, IReadOnlyList<string>> resolveChangedFiles)
    {
        _worktreePath = worktreePath;
        _goalId = goalId;
        _attemptId = attemptId;
        _mainSha = mainSha;
        _candidateTreeSha = candidateTreeSha;
        _plan = new Lazy<PlanState>(() => ResolvePlan(resolveChangedFiles, verifyingCommitSha));
    }

    internal static IReadOnlyList<string> ResolveChangedFilesFromGit(
        string worktreePath, string mainSha, string verifyingCommitSha)
    {
        // NUL delimiters preserve git paths containing spaces, quotes, or line breaks.
        var result = GitCli.Run(worktreePath, "diff", "--name-only", "-z", mainSha, verifyingCommitSha, "--");
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"git diff failed: exit={result.ExitCode}, drain_timeout={result.DrainTimedOut}");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
    }

    private PlanState ResolvePlan(
        Func<string, string, string, IReadOnlyList<string>> resolver, string verifyingCommitSha)
    {
        IReadOnlyList<string> files;
        try
        {
            files = resolver(_worktreePath, _mainSha, verifyingCommitSha);
            if (files.Count == 0)
                return Unavailable("no-changed-files", 0);
        }
        catch (Exception ex)
        {
            return Unavailable($"changed-files:{ex.GetType().Name}", null);
        }

        try
        {
            var plan = RepositoryTestImpactPlanner.Plan(files, _worktreePath);
            var status = plan.RequiresBroadVerification ? "broad-verification" :
                plan.ReverseDependencyDegradation is { } degradation ? $"selection-degraded:{degradation.Kind}" :
                "resolved";
            var selected = plan.Checks.SelectMany(check => check.TestClassSelections ?? [])
                .ToHashSet(StringComparer.Ordinal);
            // An unfiltered project check selects every class; it is not an empty selection.
            var allInfrastructureSelected = plan.Checks.Any(check =>
                check.Command.Count > 0 && check.TestProject == RepositoryTestProject.Infrastructure &&
                (check.TestClassSelections is null || check.TestClassSelections.Count == 0));
            return new PlanState(status, files.Count, selected, allInfrastructureSelected);
        }
        catch (Exception ex)
        {
            return Unavailable($"impact-plan:{ex.GetType().Name}", files.Count);
        }
    }

    private static PlanState Unavailable(string cause, int? changedFileCount)
    {
        Log($"TEST_REUSE_SHADOW_UNAVAILABLE {cause}");
        return new PlanState($"unavailable:{cause}", changedFileCount, [], false);
    }

    internal void Observe(AcceptanceManifestCheck check, AcceptanceCheckResult result)
    {
        try
        {
            foreach (var path in result.TestResultPaths ?? [])
            {
                try { ObserveTrx(check, path); }
                catch (Exception ex) { Log($"TEST_REUSE_SHADOW_UNAVAILABLE trx:{ex.GetType().Name}"); }
            }
        }
        catch (Exception ex)
        {
            Log($"TEST_REUSE_SHADOW_UNAVAILABLE observation:{ex.GetType().Name}");
        }
    }

    private void ObserveTrx(AcceptanceManifestCheck check, string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
            .Where(element => !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            var outcome = result.Attribute("outcome")?.Value;
            var failed = AcceptanceTrxOutcomeTaxonomy.IsFatal(outcome);
            // Skipped and unknown outcomes are not positive evidence of a passed execution.
            if (!failed && !string.Equals(outcome?.Trim(), "Passed", StringComparison.OrdinalIgnoreCase))
                continue;
            definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
            var method = definition?.Descendants().FirstOrDefault(element => element.Name.LocalName == "TestMethod");
            if (string.IsNullOrWhiteSpace(method?.Attribute("className")?.Value) ||
                string.IsNullOrWhiteSpace(method.Attribute("name")?.Value))
                continue;
            var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
            if (identity is null || ReduceToClass(identity) is not { Length: > 0 } className)
                continue;

            var row = new AcceptanceTestReuseShadowObservation(className, check.Name, failed,
                check.ExclusiveResourceKeys.Count > 0);
            lock (_gate)
            {
                var key = (className, check.Name);
                if (_rows.TryGetValue(key, out var prior) && prior.Failed)
                    row = row with { Failed = true };
                _rows[key] = row;
            }
        }
    }

    private static string? ReduceToClass(string identity)
    {
        var normalized = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var methodStart = normalized.LastIndexOf('.');
        if (methodStart < 0) return null;
        var type = normalized[..methodStart];
        var nestedStart = type.IndexOf('+');
        if (nestedStart >= 0) type = type[..nestedStart];
        var name = type[(type.LastIndexOf('.') + 1)..];
        var arityStart = name.IndexOf('`');
        return arityStart < 0 ? name : name[..arityStart];
    }

    internal void Complete()
    {
        string? temporaryPath = null;
        try
        {
            var plan = _plan.Value;
            AcceptanceTestReuseShadowObservation[] observations;
            lock (_gate)
                observations = _rows.Values.ToArray();
            var rows = observations.Select(observation =>
            {
                var reason = plan.Status != "resolved" ?
                    (plan.Status.StartsWith("unavailable:", StringComparison.Ordinal) ? "selection-degraded:unavailable" : plan.Status) :
                    plan.AllInfrastructureSelected || plan.Selected.Contains(observation.Class) ? "selected" :
                    observation.ExclusiveResourceLane ? "exclusive-resource-lane" : "unselected";
                return new ClassRow(observation.Class, observation.Check, reason == "unselected" ? "would-skip" : "run",
                    reason, observation.Failed ? "failed" : "passed");
            }).OrderBy(row => row.Check, StringComparer.Ordinal)
                .ThenBy(row => row.Class, StringComparer.Ordinal).ToArray();
            var skipped = rows.Where(row => row.Decision == "would-skip").ToArray();
            var failed = skipped.Where(row => row.Outcome == "failed").ToArray();
            var record = new ShadowRecord(_mainSha, _candidateTreeSha, plan.ChangedFileCount, plan.Status, rows,
                new Summary(skipped.Length, failed.Length,
                    failed.Select(row => row.Class).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
            var directory = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(_worktreePath),
                ".orchestrator", "test-reuse-shadow", SafeFileName(_goalId));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{SafeFileName(_attemptId)}.json");
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"TEST_REUSE_SHADOW_WRITE_FAILED {ex.GetType().Name}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) { Log($"TEST_REUSE_SHADOW_WRITE_FAILED cleanup:{ex.GetType().Name}"); }
            }
        }
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static void Log(string message)
    {
        // Even a failed diagnostic sink must not escape into the acceptance gate.
        try { Console.WriteLine(message); }
        catch (Exception) { }
    }

    private sealed record PlanState(string Status, int? ChangedFileCount, HashSet<string> Selected, bool AllInfrastructureSelected);
    private sealed record ClassRow([property: JsonPropertyName("class")] string Class,
        string Check, string Decision, string Reason, string Outcome);
    private sealed record Summary(int WouldSkipCount, int WouldSkipFailedCount, IReadOnlyList<string> FailedWouldSkipClasses);
    private sealed record ShadowRecord(string MainSha, string CandidateTreeSha, int? ChangedFileCount,
        string PlanStatus, IReadOnlyList<ClassRow> Classes, Summary Summary);
}
