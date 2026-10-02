using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerTargetTextRulesTests : WorkerDispatchTestSupport
{
    public static IEnumerable<object[]> TextCases()
    {
        foreach (var row in BriefLintTests.SingleTrapBriefs())
            yield return [row[0], (string)row[2] == "blocks-dispatch"];
        yield return ["Update .gitignore and .agents/skills/research-evidence/SKILL.md.", false];
        yield return ["Inspect .git/objects", true];
        yield return ["Update .agents/skills/example/SKILL.md and .git/config", true];
        yield return ["Inspect .GIT", true];
        yield return ["Update .gitattributes", false];
        yield return ["Inspect .git", true];
        yield return ["Update .agents\\skills\\example\\SKILL.md", false];
        yield return ["Inspect .git2 and .gitλ", false];
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
}
