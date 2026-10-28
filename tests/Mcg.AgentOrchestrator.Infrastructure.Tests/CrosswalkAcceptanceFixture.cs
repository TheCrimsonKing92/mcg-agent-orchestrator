using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

internal sealed class CrosswalkAcceptanceFixture : GoalAcceptanceVerifierTestBase, IDisposable
{
    internal const string Id = "vitest: A|'$()` case";
    internal string Root { get; }
    internal string Prefix { get; }
    internal string Binary { get; }
    internal List<(string Command, string Root, TimeSpan Budget, CancellationToken Token)> Calls { get; } = [];
    internal Func<string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>>? Handler { get; set; }
    internal string Discovery { get; set; } = List(Id);
    internal string Output { get; set; } = Report((Id, "passed"));
    internal int Exit { get; set; }
    internal string Stderr { get; set; } = "adapter stderr\n";
    internal bool TimedOut { get; set; }
    internal bool CaptureLimited { get; set; }
    internal GoalAcceptanceVerifier Verifier { get; }
    private readonly string? _oldPath, _oldPrefix;

    internal CrosswalkAcceptanceFixture(string[]? selectors = null, string? project = null, TimeProvider? clock = null,
        string type = "crosswalk-test")
    {
        Root = CreateManifestWorkspace(JsonSerializer.Serialize(new
        {
            version = 1, checks = new[] { new { name = "crosswalk selection", type, project, arguments = selectors ?? [], timeoutMinutes = 1,
                command = type == "command" ? "custom-runner" : (string?)null } }
        }));
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        Prefix = Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts", "crosswalk", "attempt-current");
        Binary = Path.Combine(Root, "crosswalk.exe");
        File.WriteAllText(Binary, "injected process boundary");
        _oldPath = Environment.GetEnvironmentVariable(CrosswalkClient.PathVariable);
        _oldPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        Environment.SetEnvironmentVariable(CrosswalkClient.PathVariable, Binary);
        Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, Prefix);
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "candidate-tree";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-tree";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "candidate-commit";
        Verifier = new GoalAcceptanceVerifier(TestOverrides, async (arguments, root, budget, token) =>
        {
            var command = arguments.LastOrDefault() ?? "";
            if (!command.StartsWith("& '", StringComparison.Ordinal)) return new(0, "");
            Calls.Add((command, root, budget, token));
            if (Handler is { } handler) return await handler(command, token);
            return Result(command.Contains("' list '", StringComparison.Ordinal) ? Discovery : Output,
                command.Contains("' list '", StringComparison.Ordinal) ? 0 : Exit);
        }, clock ?? TimeProvider.System);
    }

    internal GoalAcceptanceVerifier.CommandResult Result(string json, int exit = 0) =>
        new(exit, json + Stderr, TimedOut: TimedOut, Stderr: Stderr, CaptureLimited: CaptureLimited) { StandardOutput = json };

    internal async Task<AcceptanceCheckResult> RunAsync()
    {
        var result = await Verifier.RunAsync(Root);
        var check = Assert.Single(result.Checks!, check => check.Name == "crosswalk selection");
        Assert.Equal(check.Passed, result.Passed);
        if (check.NormalizedTestResultPaths is { Count: > 0 })
            Assert.Equal(check.NormalizedTestResultPaths, result.TestResultPaths);
        return check;
    }

    internal static string List(params string[] ids) => JsonSerializer.Serialize(ids.Select(id => new { id, name = id, file = "selections.test.js" }));
    internal static string Report(params (string Id, string Status)[] tests) => JsonSerializer.Serialize(new
    {
        reportFormat = "CTRF", schemaVersion = "mcg-test-result/1",
        results = new { tool = new { name = "vitest" }, tests = tests.Select(test => new
            { testId = test.Id, name = test.Id, status = test.Status, message = "assertion detail", trace = "assertion trace" }) }
    });

    internal Task<(AcceptanceCheckResult Result, bool Retried)> RunOwnedAsync(IAcceptanceRunExecutionContext owner, Check? check = null) =>
        Verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation("crosswalk selection"), check ?? new Check
            { Name = "crosswalk selection", Type = "crosswalk-test", TimeoutMinutes = 1 }, Root, owner.CancellationToken);

    internal AcceptanceAttemptExecutionOwner Owner(CancellationToken token = default, string id = "owned-attempt") => new(
        new(id, "goal", Root, "tree", "main", "commit", Path.Combine(Path.GetDirectoryName(Prefix)!, id), null, Environment.ProcessId, null),
        new AcceptanceGateEngineSettings(), token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CrosswalkClient.PathVariable, _oldPath);
        Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, _oldPrefix);
        DeleteDirectoryWithRetry(Root);
    }
}
