using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerTargetTextRulesGitSegmentTests : WorkerDispatchTestSupport
{
    public static IEnumerable<object[]> TextCases()
    {
        yield return ["Mcg.AgentOrchestrator.App.dll.git-head", false];
        yield return ["repo.git", false];
        yield return ["name_.git", false];
        yield return ["name-.git ", false];
        yield return ["...git ", false];

        string[] prefixes = ["", "Inspect ", "Inspect`", "Inspect\"", "Inspect'", "Inspect/", "Inspect\\", "Inspect("];
        string[] suffixes = ["/", " ", "`", ".", ""];
        foreach (var prefix in prefixes)
            foreach (var suffix in suffixes)
                yield return [prefix + ".git" + suffix, true];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(TextCases))]
    public void TextRulesLintAndDispatchAgree(string text, bool blocked)
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: ..");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(text, [new TaskSpec(TaskId.New(), text, AgentRole.Developer)]);
        var result = WorkerSandboxCapabilityPlanner.Evaluate(goal, goal.Tasks.Single(),
            WorkerProfileCatalog.Default().GetRequired("codex-cli"), directory,
            sandboxOptions: new(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            commandExists: _ => true);

        Xunit.Assert.Equal(blocked, WorkerTargetTextRules.IsDispatchBlocked(text));
        Xunit.Assert.Equal(blocked, !result.Allowed);
        Xunit.Assert.Equal(blocked, BriefLint.Lint(text).Any(finding => finding.Severity == BriefLintSeverity.BlocksDispatch));
    }

    [Xunit.Fact]
    public void FindGitDirectoryReference_AtFilenameMatch_ReturnsMinusOne()
    {
        const string text = "repo.git config";
        var startIndex = text.IndexOf(".git", StringComparison.Ordinal);

        Xunit.Assert.Equal(-1, WorkerTargetTextRules.FindGitDirectoryReference(text, startIndex));
    }

    [Xunit.Fact]
    public void FindGitDirectoryReference_AtFilenameMatch_ReturnsLaterBoundaryMatch()
    {
        const string text = "repo.git then .git/config";
        var startIndex = text.IndexOf(".git", StringComparison.Ordinal);
        var expectedIndex = text.LastIndexOf(".git", StringComparison.Ordinal);

        Xunit.Assert.Equal(expectedIndex, WorkerTargetTextRules.FindGitDirectoryReference(text, startIndex));
    }
}
