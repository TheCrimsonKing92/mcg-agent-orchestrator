using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory and in-memory kernel.
public sealed class GoDeveloperNoChangeCompletionTests
{
    internal static readonly string Base = new('a', 40);
    internal static readonly string Candidate = new('b', 40);
    internal const string TestsField = "deferred - main.TestStdoutIdempotency, main.TestStdoutWithJSONOutput, " +
        "main.TestStderrSeparation; conductor executes go build -buildvcs=false ./..., go vet ./..., go test -count=1 ./...";
    internal const string Output = "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: " + TestsField +
        "\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
        "model_fit: test/model - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Xunit.Theory]
    [Xunit.InlineData("main.TestStdoutIdempotency")]
    [Xunit.InlineData("main:TestStdoutIdempotency")]
    public void OwnEarlierCommitWithGoDeferralIsAccepted(string firstIdentity)
    {
        using var directory = new TestDirectory();
        var task = Scenario();
        var output = Output.Replace("main.TestStdoutIdempotency", firstIdentity, StringComparison.Ordinal);

        Xunit.Assert.True(GoDeveloperNoChangeCompletion.TryAccept(task, directory.Path,
            Candidate, true, false, output, out var marker));
        Xunit.Assert.Equal($"GO_DEVELOPER_NO_CHANGE_ACCEPTED prior_commit={Candidate} candidate_sha={Candidate} " +
            $"tests={firstIdentity},main.TestStdoutWithJSONOutput,main.TestStderrSeparation", marker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-go-mod")]
    [Xunit.InlineData("first-dispatch-no-commit")]
    [Xunit.InlineData("head-c")]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("commit-after-dispatch")]
    [Xunit.InlineData("scope-incomplete")]
    [Xunit.InlineData("blockers-present")]
    [Xunit.InlineData("commit-sha")]
    [Xunit.InlineData("files-changed")]
    [Xunit.InlineData("deferred-without-go-identity")]
    [Xunit.InlineData("tests-pass")]
    public void EachMissingConditionDeclines(string missing)
    {
        using var directory = new TestDirectory(go: missing != "no-go-mod");
        var task = Scenario(resultCommit: missing == "first-dispatch-no-commit" ? Base : Candidate);
        var output = missing switch
        {
            "scope-incomplete" => Output.Replace("assigned_scope_complete: true", "assigned_scope_complete: false"),
            "blockers-present" => Output.Replace("blockers: none", "blockers: source work remains"),
            "commit-sha" => Output.Replace("commit: none", "commit: abc1234"),
            "files-changed" => Output.Replace("files: none", "files: README.md"),
            "deferred-without-go-identity" => Output.Replace(TestsField, "deferred - conductor will verify"),
            "tests-pass" => Output.Replace(TestsField, "pass - main.TestStdoutIdempotency"),
            _ => Output
        };

        Xunit.Assert.False(GoDeveloperNoChangeCompletion.TryAccept(task, directory.Path,
            missing == "head-c" ? new string('c', 40) : Candidate, missing != "dirty",
            missing == "commit-after-dispatch", output, out var marker));
        Xunit.Assert.Equal(string.Empty, marker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("DEFERRED : — `main.TestOne` and main:TestTwo, prose, main.TestIgnored", new[] { "main.TestOne", "main:TestTwo" })]
    [Xunit.InlineData("deferred - main.TestOne, main.TestOne; note", new[] { "main.TestOne", "main.TestOne" })]
    [Xunit.InlineData("deferred - main.TestOne\nmain.TestIgnored", new[] { "main.TestOne" })]
    [Xunit.InlineData("deferred - main.TestOne\rmain.TestIgnored", new[] { "main.TestOne" })]
    [Xunit.InlineData("deferred - Main.TestOne, main.TestIgnored", new string[0])]
    [Xunit.InlineData("deferred - main.Testlower, main.TestIgnored", new string[0])]
    [Xunit.InlineData("pass - main.TestOne", new string[0])]
    public void GoIdentityParserKeepsOnlyTheLeadingRun(string field, string[] expected) =>
        Xunit.Assert.Equal(expected, GoDeveloperNoChangeCompletion.ParseTestIdentities(field));

    [Xunit.Theory]
    [Xunit.InlineData("not-developer")]
    [Xunit.InlineData("no-current-dispatch")]
    [Xunit.InlineData("first-round")]
    [Xunit.InlineData("empty-candidate")]
    [Xunit.InlineData("malformed-final-result")]
    [Xunit.InlineData("blocked-final-result")]
    public void IneligibleHistoryOrFinalResultDeclines(string missing)
    {
        using var directory = new TestDirectory();
        var task = Scenario(role: missing == "not-developer" ? AgentRole.Tester : AgentRole.Developer,
            currentDispatch: missing != "no-current-dispatch", firstRound: missing == "first-round",
            candidate: missing == "empty-candidate" ? string.Empty : Candidate);
        var output = missing switch
        {
            "malformed-final-result" => Output + "\nWORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT",
            "blocked-final-result" => Output + "\n" + Output.Replace("blockers: none", "blockers: source work remains"),
            _ => Output
        };

        Xunit.Assert.False(GoDeveloperNoChangeCompletion.TryAccept(task, directory.Path,
            Candidate, true, false, output, out var marker));
        Xunit.Assert.Empty(marker);
    }

    [Xunit.Fact]
    public void PriorCommitMayDifferFromCandidateAndHeadComparisonIgnoresCase()
    {
        using var directory = new TestDirectory();
        var prior = new string('c', 40);
        var task = Scenario(resultCommit: prior);

        Xunit.Assert.True(GoDeveloperNoChangeCompletion.TryAccept(task, directory.Path,
            Candidate.ToUpperInvariant(), true, false, Output, out var marker));
        Xunit.Assert.Contains($"prior_commit={prior} candidate_sha={Candidate}", marker, StringComparison.Ordinal);
    }

    private static TaskSpec Scenario(string? resultCommit = null, AgentRole role = AgentRole.Developer,
        bool currentDispatch = true, bool firstRound = false, string? candidate = null)
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Recheck own Go work.", role);
        var goal = kernel.CreateGoal("Go no-change completion", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (!currentDispatch) return task;
        if (!firstRound)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec", "C:\\repo", clock.UtcNow, BaseCommit: Base));
            kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit ?? Candidate);
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
                new TaskProcessRecord(123, "codex exec", "C:\\repo", "out", "err", "exit",
                    clock.UtcNow, clock.UtcNow, 0), null);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Own work committed.");
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            kernel.RetryTask(goal.Id, task.Id, "Recheck the candidate.");
        }
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow, BaseCommit: candidate ?? Candidate));
        return task;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "go-no-change-" + Guid.NewGuid().ToString("N"));

        internal TestDirectory(bool go = true)
        {
            Directory.CreateDirectory(Path);
            if (go) File.WriteAllText(System.IO.Path.Combine(Path, "go.mod"), "module example.test/fixture\n");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
