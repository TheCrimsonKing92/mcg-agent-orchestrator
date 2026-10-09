using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerSkillSelector(WorkerSkillResolver? resolver = null)
{
    private readonly WorkerSkillResolver _resolver = resolver ?? new WorkerSkillResolver();
    private static readonly SkillCandidate[] KnownSkills =
    [
        new(
            "dotnet-windows-build-hygiene",
            Path.Combine(".agents", "skills", "dotnet-windows-build-hygiene", "SKILL.md"),
            "Use Invoke-WorkerBuildCheck.ps1 for normal .NET builds, Invoke-TestSummary.ps1 for tests, and the bounded diagnostic wrapper only for unsupported argument shapes; also covers CS2012/file-lock and Windows PowerShell hygiene."),
        new(
            "orchestrator-dogfood",
            Path.Combine(".agents", "skills", "orchestrator-dogfood", "SKILL.md"),
            "Use for operator-seat commands and artifacts: dogfood, backlog, run-goal, simple-goal, lifecycle-simple-goal, dogfood-log, conduct --loop, Get-OrchestratorSnapshot, workspace create, workspace remove, and subscription-dispatch."),
        new(
            "orchestrator-worker-verification",
            Path.Combine(".agents", "skills", "orchestrator-worker-verification", "SKILL.md"),
            "Use for reviewing worker completions, dispatch logs, goal worktree diffs, dirty recovery, false-positive completion risk, manual verification, and acceptance readiness."),
        new(
            "skill-authoring",
            Path.Combine(".agents", "skills", "skill-authoring", "SKILL.md"),
            "Use for repo-scoped worker skill authoring, SKILL.md edits, skill routing rules, skill selection tests, and worker skill usage evidence."),
        new(
            "research-evidence",
            Path.Combine(".agents", "skills", "research-evidence", "SKILL.md"),
            "Use for evidence-question framing, exact source anchors, fact/inference/unknown classification, unknown settlement evidence, and premise-invalid stops."),
        new(
            "criterion-ownership-planning",
            Path.Combine(".agents", "skills", "criterion-ownership-planning", "SKILL.md"),
            "Use for criterion-by-criterion evidence ownership, role feasibility, owning seams, verification classes, required evidence, and stop conditions."),
        new(
            "systematic-debugging",
            Path.Combine(".agents", "skills", "systematic-debugging", "SKILL.md"),
            "Use the four gated phases for repeated criterion failures or task-specific acceptance-failure retries."),
        new(
            "verification-before-completion",
            Path.Combine(".agents", "skills", "verification-before-completion", "SKILL.md"),
            "Use a fresh execution-evidence gate before claiming Developer work complete.")
    ];

    internal string BuildSelectedSkills(Goal goal, TaskSpec task, string workingDirectory)
    {
        var selected = SelectSkillRequirements(goal, task, workingDirectory);
        var lines = new List<string>
        {
            "# Selected Skills",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "Use only the skills below when they match the actual work. Read each listed SKILL.md before applying it, and report the skills you used in the WORKER_RESULT skills field.",
            string.Empty,
            "## Skills"
        };

        if (selected.Count == 0)
        {
            lines.Add("- none selected: no deterministic skill rule matched this task.");
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var skill in selected)
        {
            lines.Add($"- {skill.Name}");
            lines.Add($"  Path: {skill.RelativePath}");
            lines.Add($"  Status: {(skill.Available ? "available" : "missing")}");
            lines.Add($"  Reason: {skill.Reason}");
            lines.Add($"  Usage: {skill.Usage}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal IReadOnlyList<WorkerSkillRequirement> SelectSkillRequirements(
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        return SelectSkills(goal, task)
            .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => _resolver.Resolve(
                workingDirectory,
                skill.Name,
                skill.RelativePath,
                skill.Usage,
                BuildSkillReason(skill.Name, goal, task)))
            .ToArray();
    }

    private IEnumerable<SkillCandidate> SelectSkills(Goal goal, TaskSpec task)
    {
        var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
        var dotnetSignals = ContainsAny(
            text,
            ".net",
            "dotnet",
            "build",
            "test",
            "xunit",
            "csproj",
            "sln",
            "CS2012",
            "VBCSCompiler",
            "MSBuild",
            "apphost");
        var dogfoodSignals = ContainsAny(
            text,
            "dogfood",
            "backlog",
            "run-goal",
            "simple-goal",
            "lifecycle-simple-goal",
            "dogfood-log",
            "conduct --loop",
            "Get-OrchestratorSnapshot",
            "workspace create",
            "workspace remove",
            "subscription-dispatch");
        var verificationSignals = task.RequiredRole == AgentRole.Tester ||
            (task.RequiredRole != AgentRole.Reviewer &&
             ContainsAny(text, "verify", "verification", "review", "worker result", "dispatch log", "worktree diff", "acceptance"));
        var skillAuthoringSignals = ContainsAny(
            text,
            ".agents/skills",
            ".agents\\skills",
            "SKILL.md",
            "skill authoring",
            "skill routing",
            "selected-skills",
            "worker skill",
            "worker-skill",
            "skill usage");

        if (dotnetSignals || task.RequiredRole is AgentRole.Developer or AgentRole.Tester)
        {
            yield return KnownSkills.Single(skill => skill.Name == "dotnet-windows-build-hygiene");
        }

        if (dogfoodSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "orchestrator-dogfood");
        }

        if (verificationSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "orchestrator-worker-verification");
        }

        if (skillAuthoringSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "skill-authoring");
        }

        if (task.RequiredRole == AgentRole.Researcher)
        {
            yield return KnownSkills.Single(skill => skill.Name == "research-evidence");
        }

        if (task.RequiredRole == AgentRole.Planner)
        {
            yield return KnownSkills.Single(skill => skill.Name == "criterion-ownership-planning");
        }

        if (task.RequiredRole == AgentRole.Developer)
        {
            yield return KnownSkills.Single(skill => skill.Name == "verification-before-completion");
        }

        if (task.RequiredRole == AgentRole.Developer &&
            (task.CriterionRetryCount >= 2 || goal.LatestTaskRetryAfterAcceptanceFailure(task.Id) is not null))
        {
            yield return KnownSkills.Single(skill => skill.Name == "systematic-debugging");
        }
    }

    private string BuildSkillReason(string skillName, Goal goal, TaskSpec task)
    {
        return skillName switch
        {
            "dotnet-windows-build-hygiene" => task.RequiredRole is AgentRole.Developer or AgentRole.Tester
                ? "Developer/Tester work in this repository usually needs .NET build/test hygiene and Windows lock avoidance."
                : "Task text references .NET, build, test, or known Windows build-lock failure modes.",
            "orchestrator-dogfood" => "Task or goal text names an operator-seat command or artifact: dogfood, backlog, run-goal, simple-goal, lifecycle-simple-goal, dogfood-log, conduct --loop, Get-OrchestratorSnapshot, workspace create, workspace remove, or subscription-dispatch.",
            "orchestrator-worker-verification" => task.RequiredRole == AgentRole.Tester
                ? "Tester work must verify worker output, logs, diffs, and acceptance evidence before trusting task status."
                : "Task text references verification, review, worker result contracts, dispatch evidence, or worktree diffs.",
            "skill-authoring" => "Task text references repo-scoped worker skills, SKILL.md files, skill routing, selected skills, or skill usage evidence.",
            "research-evidence" => "Every Researcher must frame a named evidence question, anchor and classify material statements, and stop on an invalid premise.",
            "criterion-ownership-planning" => "Every Planner must map each criterion to a feasible evidence owner, owning seam, verification class, and stop condition.",
            "systematic-debugging" => task.CriterionRetryCount >= 2
                ? $"Developer criterion retry count is {task.CriterionRetryCount}; systematic debugging begins at retry 2."
                : goal.LatestTaskRetryAfterAcceptanceFailure(task.Id) is not null
                    ? "Developer has a task-specific retry at or after the latest acceptance failure."
                    : "Developer retry state requires systematic debugging.",
            "verification-before-completion" => "Every Developer must inspect fresh execution evidence before claiming completion.",
            _ => "Selected by deterministic task metadata rule."
        };
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SkillCandidate(string Name, string RelativePath, string Usage);
}
