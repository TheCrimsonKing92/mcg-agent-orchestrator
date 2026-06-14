using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerSkillRequirement(
    string Name,
    string RelativePath,
    string Usage,
    bool Available,
    string Reason);

public static class WorkerContextArtifacts
{
    private const int PriorVerificationMaxChars = 40000;
    private const int GuidanceFileMaxChars = 30000;
    private const int CurrentEvidenceMaxChars = 20000;
    private const int DigestTextMaxChars = 700;
    private const int DigestEvidenceMaxChars = 500;
    private const int SummaryFieldMaxChars = 350;
    private const int SourceSurveyMaxFiles = 60;
    private const int SourceSurveyMaxAnalysisFiles = 90;
    private const int SourceSurveyMaxFileLines = 500;
    private const int DiffSummaryMaxChars = 6000;
    private static readonly Regex PublicTypeRegex = new(
        @"^\s*(public|internal|protected internal|protected)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*(class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PublicMemberRegex = new(
        @"^\s*(public|internal|protected internal|protected)\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+)*(?!class\b|interface\b|record\b|struct\b|enum\b)[A-Za-z_][A-Za-z0-9_<>,\[\].?\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(\(|\{|=>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] SourceSurveyExtensions =
    [
        ".cs", ".csproj", ".sln", ".props", ".targets", ".json", ".md", ".ps1", ".cmd",
        ".razor", ".cshtml", ".html", ".css", ".js", ".ts", ".yml", ".yaml"
    ];

    private static readonly SkillCandidate[] KnownSkills =
    [
        new(
            "dotnet-windows-build-hygiene",
            Path.Combine(".agents", "skills", "dotnet-windows-build-hygiene", "SKILL.md"),
            "Use for .NET build/test work, CS2012 file locks, VBCSCompiler/MSBuild server cleanup, dashboard apphost locks, and Windows PowerShell verification hygiene."),
        new(
            "orchestrator-dogfood",
            Path.Combine(".agents", "skills", "orchestrator-dogfood", "SKILL.md"),
            "Use for orchestrator dogfood goals, backlog changes, goal/workspace lifecycle commands, subscription dispatches, dashboard validation, acceptance gates, and DOGFOOD_LOG evidence."),
        new(
            "orchestrator-worker-verification",
            Path.Combine(".agents", "skills", "orchestrator-worker-verification", "SKILL.md"),
            "Use for reviewing worker completions, dispatch logs, goal worktree diffs, dirty recovery, false-positive completion risk, manual verification, and acceptance readiness."),
        new(
            "aspnet-core",
            Path.Combine(".agents", "skills", "aspnet-core", "SKILL.md"),
            "Use for ASP.NET Core, Blazor, Razor Pages, MVC, Minimal APIs, middleware, SignalR, authentication, authorization, and .NET web application changes."),
        new(
            "playwright",
            Path.Combine(".agents", "skills", "playwright", "SKILL.md"),
            "Use for browser automation, dashboard UI flows, screenshots, end-to-end smoke checks, and Playwright-based validation."),
        new(
            "skill-authoring",
            Path.Combine(".agents", "skills", "skill-authoring", "SKILL.md"),
            "Use for repo-scoped worker skill authoring, SKILL.md edits, skill routing rules, skill selection tests, and worker skill usage evidence.")
    ];

    private static readonly JsonSerializerOptions RegistryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Write(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string>? preflightFindings = null)
    {
        var scratchRoot = Path.Combine(workingDirectory, ".orchestrator-context");
        var contextDirectory = Path.Combine(scratchRoot, goal.Id.Value);
        Directory.CreateDirectory(contextDirectory);

        // Self-ignore the scratch tree so it never dirties the worktree, even in
        // repositories/worktrees whose root .gitignore lacks an orchestrator rule.
        // A ".gitignore" containing "*" ignores every file in the directory
        // (including itself), so "git status" stays clean before acceptance.
        var scratchIgnore = Path.Combine(scratchRoot, ".gitignore");
        if (!File.Exists(scratchIgnore))
        {
            File.WriteAllText(scratchIgnore, "*" + Environment.NewLine);
        }

        WriteText(Path.Combine(contextDirectory, "objective.md"), BuildObjective(goal));
        WriteText(Path.Combine(contextDirectory, "current-task.md"), BuildCurrentTask(task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "prior-task-summaries.md"), BuildPriorTaskSummaries(goal.Tasks, task.Id));
        WriteText(Path.Combine(contextDirectory, "prior-task-evidence.md"), BuildPriorTaskEvidence(goal.Tasks, task.Id));
        WriteText(Path.Combine(contextDirectory, "deterministic-verification.md"), BuildDeterministicVerification(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "workflow-brokers.md"), BuildWorkflowBrokers(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "context-budget.md"), BuildContextBudget(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "selected-skills.md"), BuildSelectedSkills(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "source-survey.md"), BuildSourceSurvey(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "diff-summary.md"), BuildDiffSummary(workingDirectory));
        if (preflightFindings is { Count: > 0 })
        {
            WriteText(Path.Combine(contextDirectory, "subscription-preflight.md"), BuildPreflight(preflightFindings));
        }

        WriteText(Path.Combine(contextDirectory, "digest.md"), BuildDigest(goal, task, workingDirectory, preflightFindings));

        var guidanceFiles = CopyGuidanceFiles(workingDirectory, contextDirectory);
        WriteText(Path.Combine(contextDirectory, "manifest.md"), BuildManifest(goal, task, workingDirectory, guidanceFiles, preflightFindings));
        WriteText(Path.Combine(contextDirectory, "context-package.json"), BuildContextPackage(goal, task, contextDirectory));
        WriteArtifactRegistry(contextDirectory, goal, task, workingDirectory, guidanceFiles, preflightFindings);
        SnapshotCurrentPackage(contextDirectory, task.Id);

        return contextDirectory;
    }

    private static string BuildObjective(Goal goal)
    {
        var lines = new List<string>
        {
            "# Goal Objective",
            string.Empty,
            goal.Objective,
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Goal status: {goal.Status}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPreflight(IReadOnlyList<string> findings)
    {
        var lines = new List<string>
        {
            "# Subscription Preflight",
            string.Empty
        };
        lines.AddRange(findings.Select(finding => $"- {finding}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDigest(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string>? preflightFindings)
    {
        var priorTasks = goal.Tasks
            .TakeWhile(t => t.Id != task.Id)
            .Where(t => t.Status == WorkTaskStatus.Completed)
            .ToList();
        var openRisks = BuildOpenRiskLines(goal, task);
        var lines = new List<string>
        {
            "# Worker Context Digest",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Current task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Objective",
            TrimDigestText(goal.Objective),
            string.Empty,
            "## Current Task",
            TrimDigestText(task.Description),
            string.Empty,
            "## Role Focus",
            BuildRoleFocus(task.RequiredRole)
        };

        lines.Add(string.Empty);
        lines.Add("## Role Artifact Priorities");
        lines.AddRange(BuildRoleArtifactPriorities(task.RequiredRole));

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add(string.Empty);
            lines.Add("## Verification Plan");
            lines.Add(TrimDigestText(task.VerificationPlan));
        }

        lines.Add(string.Empty);
        lines.Add("## Prior Completed Outcomes");
        if (priorTasks.Count == 0)
        {
            lines.Add("- None.");
        }
        else
        {
            foreach (var priorTask in priorTasks.TakeLast(5))
            {
                lines.Add($"- {priorTask.RequiredRole}: {TrimDigestTitle(priorTask.Description)}");
                if (priorTask.LastVerification is null)
                {
                    lines.Add("  Verification: no verification record.");
                    continue;
                }

                lines.Add($"  Verification: exit {priorTask.LastVerification.ExitCode} from `{priorTask.LastVerification.Command}`.");
                if (!string.IsNullOrWhiteSpace(priorTask.LastVerification.ModelFitNote))
                {
                    lines.Add($"  Model fit: {priorTask.LastVerification.ModelFitNote}");
                }

                var evidence = TrimDigestEvidence(priorTask.LastVerification.StandardOutput);
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    lines.Add($"  Outcome: {evidence}");
                }

                AddEvidencePointers(lines, priorTask.LastVerification);
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Evidence Pointers");
        lines.Add("- current-task.md: current task brief and verification plan.");
        lines.Add("- objective.md: full goal objective.");
        lines.Add("- deterministic-verification.md: deterministic checklist of manifest presence, verification records, worker result contracts, and generated-path risk.");
        lines.Add("- selected-skills.md: deterministic local skill selection with paths, reasons, and usage notes.");
        lines.Add("- source-survey.md: compact repository source inventory and task-term matches.");
        lines.Add("- diff-summary.md: compact git status and changed-file summary for current workspace state.");
        if (preflightFindings is { Count: > 0 })
        {
            lines.Add("- subscription-preflight.md: deterministic profile, sandbox, worktree, retry, and capability status for this dispatch.");
        }

        lines.Add("- prior-task-summaries.md: compact prior task summaries; read before full evidence.");
        lines.Add("- prior-task-evidence.md: fuller prior verification output; read after summaries when needed.");
        if (File.Exists(Path.Combine(workingDirectory, ".orchestrator-handoff.md")))
        {
            lines.Add("- .orchestrator-handoff.md: prior task handoff in the working directory.");
        }

        lines.Add(string.Empty);
        lines.Add("## Open Risks And Blockers");
        if (openRisks.Count == 0)
        {
            lines.Add("- None recorded.");
        }
        else
        {
            lines.AddRange(openRisks);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCurrentTask(TaskSpec task, string workingDirectory)
    {
        var lines = new List<string>
        {
            "# Current Task",
            string.Empty,
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Status: {task.Status}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Description",
            task.Description
        };

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add(string.Empty);
            lines.Add("## Verification Plan");
            lines.Add(task.VerificationPlan);
        }

        if (task.LastExecution is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Last Model Output");
            lines.Add(TrimArtifactBlock(task.LastExecution.Output, CurrentEvidenceMaxChars));
        }

        if (task.LastDispatch is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Last Dispatch");
            lines.Add($"Worker: {task.LastDispatch.WorkerName}");
            lines.Add($"Command: {task.LastDispatch.Command}");
            lines.Add($"Working directory: {task.LastDispatch.WorkingDirectory}");
        }

        if (task.LastVerification is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Last Verification");
            lines.Add($"Command: {task.LastVerification.Command}");
            lines.Add($"Working directory: {task.LastVerification.WorkingDirectory}");
            lines.Add($"Exit code: {task.LastVerification.ExitCode}");
            lines.Add($"Verification history count: {task.VerificationHistory.Count}");
            if (!string.IsNullOrWhiteSpace(task.LastVerification.ModelFitNote))
            {
                lines.Add($"Model fit: {task.LastVerification.ModelFitNote}");
            }

            lines.Add(string.Empty);
            lines.Add("### Stdout");
            lines.Add(TrimArtifactBlock(task.LastVerification.StandardOutput, CurrentEvidenceMaxChars));
            if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("### Stderr");
                lines.Add(TrimArtifactBlock(task.LastVerification.StandardError, CurrentEvidenceMaxChars));
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Worker Result Contract");
        lines.Add("End final output with:");
        lines.AddRange(AgentOutputDirectives.WorkerResultTemplateLines);

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDeterministicVerification(Goal goal, TaskSpec task, string workingDirectory)
    {
        var priorTasks = goal.Tasks.TakeWhile(t => t.Id != task.Id).ToList();
        var completedPriorTasks = priorTasks.Where(t => t.Status == WorkTaskStatus.Completed).ToList();
        var lines = new List<string>
        {
            "# Deterministic Verification Checklist",
            string.Empty,
            $"Working directory: {workingDirectory}",
            $"Acceptance manifest: {(File.Exists(Path.Combine(workingDirectory, "config", "acceptance-manifest.json")) ? "present: config/acceptance-manifest.json" : "missing")}",
            $"Current verification plan: {(string.IsNullOrWhiteSpace(task.VerificationPlan) ? "missing" : "present")}",
            $"Prior tasks inspected: {priorTasks.Count}",
            $"Prior completed tasks: {completedPriorTasks.Count}",
            string.Empty,
            "## Findings"
        };

        if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester)
        {
            var goalPrefix = goal.Id.Value.Length <= 8 ? goal.Id.Value : goal.Id.Value[..8];
            var attemptName = $"{task.RequiredRole.ToString().ToLowerInvariant()}-{task.Id.Value[..8]}";
            lines.Add(string.Empty);
            lines.Add("## Isolated .NET Verification");
            lines.Add($"- Use `.\\scripts\\Invoke-IsolatedDotnet.ps1 -GoalPrefix {goalPrefix} -AttemptName {attemptName} test <project-or-sln> --verbosity minimal` instead of raw `dotnet test` for .NET checks.");
            lines.Add($"- Goal build artifacts are isolated under `{DotnetBuildEnvironmentManager.GoalRoot(goal.Id)}` and reused across tasks in this goal.");
        }

        var changedFiles = ReadChangedFilesForTestImpact(workingDirectory);
        var testImpactPlan = RepositoryTestImpactPlanner.Plan(changedFiles);
        var verificationPolicy = VerificationPolicyCompiler.Compile(
            task.RequiredRole,
            goal.Objective,
            task.Description,
            task.VerificationPlan,
            changedFiles);
        lines.Add(string.Empty);
        lines.Add("## Test Impact Plan");
        lines.Add(testImpactPlan.Summary);
        foreach (var check in testImpactPlan.Checks)
        {
            lines.Add($"- {check.Name}: `{check.CommandLine}`");
            lines.Add($"  Reason: {check.Reason}");
        }

        lines.Add(string.Empty);
        lines.Add("## Required Verification Policy");
        lines.Add(verificationPolicy.Summary);
        lines.Add($"Requires tests: {(verificationPolicy.RequiresTests ? "yes" : "no")}");
        lines.Add($"Requires human review: {(verificationPolicy.RequiresHumanReview ? "yes" : "no")}");
        foreach (var check in verificationPolicy.Checks)
        {
            lines.Add($"- {(check.Required ? "required" : "optional")}: {check.Name} [{check.Kind}] `{check.CommandLine}`");
            lines.Add($"  Reason: {check.Reason}");
        }

        if (string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add("- fail: current task has no verification plan.");
        }

        if (priorTasks.Count == 0)
        {
            lines.Add("- ok: no prior tasks to verify.");
        }

        foreach (var priorTask in priorTasks)
        {
            if (priorTask.Status != WorkTaskStatus.Completed)
            {
                lines.Add($"- fail: prior {priorTask.RequiredRole} task {priorTask.Id.Value} is {priorTask.Status}.");
                continue;
            }

            if (priorTask.LastVerification is null)
            {
                lines.Add($"- fail: prior {priorTask.RequiredRole} task {priorTask.Id.Value} completed without verification record.");
                continue;
            }

            var verification = priorTask.LastVerification;
            lines.Add(verification.Succeeded
                ? $"- ok: prior {priorTask.RequiredRole} verification passed: `{verification.Command}`."
                : $"- fail: prior {priorTask.RequiredRole} verification failed with exit {verification.ExitCode}: `{verification.Command}`.");

            if (SummarizeModelFit(verification).Equals("Not reported.", StringComparison.Ordinal))
            {
                lines.Add($"- warn: prior {priorTask.RequiredRole} task {priorTask.Id.Value} has no model-fit evidence.");
            }

            if (priorTask.RequiredRole is AgentRole.Developer or AgentRole.Tester)
            {
                AddWorkerResultContractFindings(lines, priorTask, verification);
            }
        }

        if (task.LastVerification is { } currentVerification)
        {
            lines.Add(currentVerification.Succeeded
                ? $"- ok: current task latest verification passed: `{currentVerification.Command}`."
                : $"- fail: current task latest verification failed with exit {currentVerification.ExitCode}: `{currentVerification.Command}`.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildWorkflowBrokers(Goal goal, TaskSpec task, string workingDirectory)
    {
        var changedFiles = ReadChangedFilesForTestImpact(workingDirectory);
        var testImpactPlan = RepositoryTestImpactPlanner.Plan(changedFiles);
        var verificationPolicy = VerificationPolicyCompiler.Compile(
            task.RequiredRole,
            goal.Objective,
            task.Description,
            task.VerificationPlan,
            changedFiles);
        var goalPrefix = goal.Id.Value.Length <= 8 ? goal.Id.Value : goal.Id.Value[..8];
        var attemptName = $"{task.RequiredRole.ToString().ToLowerInvariant()}-{task.Id.Value[..8]}";
        var lines = new List<string>
        {
            "# Workflow Brokers",
            string.Empty,
            "Use deterministic broker artifacts before rediscovering common workflow steps in the prompt. If a broker artifact is missing, stale, or reports a failure, report that as a blocker in WORKER_RESULT instead of guessing.",
            string.Empty,
            "## Available Broker Actions",
            "- build-test-selection",
            "  Artifact: deterministic-verification.md",
            "  Use for: choosing focused tests, identifying required checks, and avoiding raw unisolated `dotnet test`.",
            $"  Suggested isolated command shape: `.\\scripts\\Invoke-IsolatedDotnet.ps1 -GoalPrefix {goalPrefix} -AttemptName {attemptName} test <project-or-sln> --verbosity minimal`",
            $"  Current recommendation: {testImpactPlan.Summary}",
            "  Failure handling: failing required checks are actionable verification failures; include the command and exit evidence.",
            "- static-policy-checks",
            "  Artifact: deterministic-verification.md",
            "  Use for: required verification policy, generated-path risk, worker-result contract checks, and manifest presence.",
            $"  Current policy: {verificationPolicy.Summary}",
            "  Failure handling: required policy failures block acceptance until fixed or explicitly explained.",
            "- source-survey",
            "  Artifact: source-survey.md",
            "  Use for: bounded source discovery, likely tests, public symbols, call-site hints, and ownership hints.",
            "  Failure handling: if source-survey.md is missing or stale, run a narrow `rg --files` scoped to the task path before editing.",
            "- diff-summary",
            "  Artifact: diff-summary.md",
            "  Use for: existing workspace changes, changed files, diff stat, and generated-output noise checks.",
            "  Failure handling: unexpected dirty files are blockers unless they are part of the assigned task.",
            "- acceptance-evidence",
            "  Artifact: deterministic-verification.md plus current-task.md",
            "  Use for: acceptance manifest status, verification history, model-fit evidence, and WORKER_RESULT completeness.",
            "  Failure handling: missing acceptance evidence should become an explicit blocker or a verify command, not a silent pass.",
            "- backlog-log-evidence",
            "  Artifact: BACKLOG.md and DOGFOOD_LOG.md when present in artifact-registry.json",
            "  Use for: closing backlog items, filing follow-ups, and recording Model fit at goal boundaries.",
            "  Failure handling: missing backlog/log artifacts require a direct file read before editing those files.",
            string.Empty,
            "## Broker Output Contract",
            "- Prefer broker artifact paths in your evidence over repeating full artifact contents.",
            "- Include broker failures in WORKER_RESULT blockers.",
            "- Include broker commands and required checks in WORKER_RESULT commands/tests."
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string[] ReadChangedFilesForTestImpact(string workingDirectory)
    {
        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            return [];
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryRunGit(workingDirectory, ["diff", "--name-only", "HEAD", "--"], out var diffOutput))
        {
            foreach (var line in SplitGitOutput(diffOutput))
            {
                files.Add(line);
            }
        }

        if (TryRunGit(workingDirectory, ["status", "--short"], out var statusOutput))
        {
            foreach (var line in SplitGitOutput(statusOutput))
            {
                if (line.Length < 4)
                {
                    continue;
                }

                var path = line[3..].Trim();
                var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
                files.Add(renameIndex >= 0 ? path[(renameIndex + 4)..] : path);
            }
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SplitGitOutput(string output) =>
        output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddWorkerResultContractFindings(List<string> lines, TaskSpec task, TaskVerificationRecord verification)
    {
        if (!TryParseWorkerResultContract(verification, out var fields))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} has no WORKER_RESULT contract.");
            return;
        }

        lines.Add($"- ok: prior {task.RequiredRole} task {task.Id.Value} reported WORKER_RESULT contract.");
        if (!fields.TryGetValue("files", out var files) || string.IsNullOrWhiteSpace(files))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} contract has no files field.");
        }
        else
        {
            var generated = SplitContractList(files).Where(IsGeneratedPath).ToArray();
            if (generated.Length > 0)
            {
                lines.Add($"- fail: prior {task.RequiredRole} task {task.Id.Value} reported generated path changes: {string.Join(", ", generated)}.");
            }
        }

        if (!fields.TryGetValue("tests", out var tests) ||
            string.IsNullOrWhiteSpace(tests) ||
            tests.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} contract has no test evidence.");
        }

        if (!fields.TryGetValue("blockers", out var blockers) ||
            !blockers.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- fail: prior {task.RequiredRole} task {task.Id.Value} reported blockers: {blockers}.");
        }

        if (!fields.TryGetValue("skills", out var skills) ||
            string.IsNullOrWhiteSpace(skills) ||
            skills.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} did not report skill usage.");
        }
    }

    private static string BuildSelectedSkills(Goal goal, TaskSpec task, string workingDirectory)
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

    private static string BuildContextBudget(Goal goal, TaskSpec task, string workingDirectory)
    {
        var selected = SelectSkillRequirements(goal, task, workingDirectory);
        var completedPriorTasks = goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id)
            .Count(candidate => candidate.Status == WorkTaskStatus.Completed);
        var lines = new List<string>
        {
            "# Context Budget Policy",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Budget",
            $"- digest.md inline target: {DigestTextMaxChars} chars per text section, {DigestEvidenceMaxChars} chars per evidence section.",
            $"- prior-task-evidence.md retrieval budget: {PriorVerificationMaxChars} chars.",
            $"- diff-summary.md retrieval budget: {DiffSummaryMaxChars} chars.",
            $"- guidance file retrieval budget: {GuidanceFileMaxChars} chars per copied guidance artifact.",
            "- Large paid prompt risk is evaluated before dispatch; prefer handles below over copying large artifacts into task prose.",
            string.Empty,
            "## Retrieval Handles",
            "- artifact-registry.json: authoritative list of artifacts, byte counts, hashes, freshness, and role visibility.",
            "- manifest.md: human-readable artifact descriptions and role priorities.",
            "- context-package.json: task package path and missing-artifact fallback guidance.",
            string.Empty,
            "## Decisions",
            "- embed: digest.md, current-task.md, and role artifact priorities because they are compact mandatory orientation.",
            "- summarize: prior-task-summaries.md, source-survey.md, diff-summary.md, selected-skills.md, workflow-brokers.md, and deterministic-verification.md before opening full content.",
            "- retrieve by handle: prior-task-evidence.md and copied guidance files only when their summaries, hashes, or task needs require them.",
            "- omit from prompt prose: full logs, full diffs, generated artifacts, and unrelated guidance; use artifact-registry.json handles instead.",
            string.Empty,
            "## Current Estimate Inputs",
            $"- completed prior tasks before this task: {completedPriorTasks}",
            $"- selected skills: {(selected.Count == 0 ? "none" : string.Join(", ", selected.Select(skill => $"{skill.Name}:{(skill.Available ? "available" : "missing")}")))}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    public static IReadOnlyList<WorkerSkillRequirement> SelectSkillRequirements(
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        return SelectSkills(goal, task)
            .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new WorkerSkillRequirement(
                skill.Name,
                skill.RelativePath,
                skill.Usage,
                File.Exists(Path.Combine(workingDirectory, skill.RelativePath)),
                BuildSkillReason(skill.Name, task)))
            .ToArray();
    }

    private static IEnumerable<SkillCandidate> SelectSkills(Goal goal, TaskSpec task)
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
            "apphost",
            "dashboard");
        var dogfoodSignals = ContainsAny(
            text,
            "orchestrator",
            "dogfood",
            "backlog",
            "goal",
            "run-goal",
            "simple-goal",
            "workspace",
            "subscription",
            "dispatch",
            "acceptance",
            "DOGFOOD_LOG",
            "dashboard");
        var verificationSignals = task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer ||
            ContainsAny(text, "verify", "verification", "review", "worker result", "dispatch log", "worktree diff", "acceptance");
        var aspNetSignals = ContainsAny(
            text,
            "asp.net",
            "aspnet",
            "blazor",
            "razor",
            "mvc",
            "minimal api",
            "controller",
            "middleware",
            "signalr",
            "authentication",
            "authorization");
        var browserAutomationSignals = ContainsAny(
            text,
            "playwright",
            "browser automation",
            "browser smoke",
            "dashboard ui",
            "ui flow",
            "e2e",
            "end-to-end smoke",
            "screenshot",
            "prototype-ui");
        var skillAuthoringSignals = ContainsAny(
            text,
            ".agents/skills",
            ".agents\\skills",
            "SKILL.md",
            "skill authoring",
            "skill routing",
            "selected-skills",
            "worker skill",
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

        if (aspNetSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "aspnet-core");
        }

        if (browserAutomationSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "playwright");
        }

        if (skillAuthoringSignals)
        {
            yield return KnownSkills.Single(skill => skill.Name == "skill-authoring");
        }
    }

    private static string BuildSkillReason(string skillName, TaskSpec task)
    {
        return skillName switch
        {
            "dotnet-windows-build-hygiene" => task.RequiredRole is AgentRole.Developer or AgentRole.Tester
                ? "Developer/Tester work in this repository usually needs .NET build/test hygiene and Windows lock avoidance."
                : "Task text references .NET, build, test, dashboard, or known Windows build-lock failure modes.",
            "orchestrator-dogfood" => "Task or goal text references orchestrator dogfood, backlog, goal lifecycle, subscription dispatch, dashboard, or acceptance workflow.",
            "orchestrator-worker-verification" => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer
                ? "Tester/Reviewer work must verify worker output, logs, diffs, and acceptance evidence before trusting task status."
                : "Task text references verification, review, worker result contracts, dispatch evidence, or worktree diffs.",
            "aspnet-core" => "Task text references ASP.NET Core or .NET web application concepts.",
            "playwright" => "Task text references browser automation, dashboard UI validation, end-to-end smoke checks, screenshots, or Playwright.",
            "skill-authoring" => "Task text references repo-scoped worker skills, SKILL.md files, skill routing, selected skills, or skill usage evidence.",
            _ => "Selected by deterministic task metadata rule."
        };
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSourceSurvey(Goal goal, TaskSpec task, string workingDirectory)
    {
        var sourceFiles = EnumerateSourceFiles(workingDirectory).ToList();
        var terms = BuildSearchTerms(goal, task);
        var matches = sourceFiles
            .Where(path => terms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(SourceSurveyMaxFiles)
            .ToList();
        var publicSymbols = BuildPublicApiSymbols(workingDirectory, sourceFiles);
        var likelyTests = BuildLikelyTests(sourceFiles, terms);
        var callSiteHints = BuildCallSiteHints(workingDirectory, sourceFiles, terms);
        var ownershipHints = BuildOwnershipHints(workingDirectory, sourceFiles);
        var groups = sourceFiles
            .GroupBy(GetSurveyDirectory)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var sample = sourceFiles
            .Take(SourceSurveyMaxFiles)
            .ToList();
        var lines = new List<string>
        {
            "# Source Survey",
            string.Empty,
            $"Working directory: {workingDirectory}",
            $"Source files indexed: {sourceFiles.Count}",
            $"Returned file limit: {SourceSurveyMaxFiles}",
            "Regeneration: generated at dispatch preparation; treat as stale when source files, git status, objective, task text, or verification plan changes after dispatch.",
            string.Empty,
            "## Directory Counts"
        };

        if (groups.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(groups.Select(group => $"- {group.Key}: {group.Count()}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Task-Term Matches");
        if (matches.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(matches.Select(path => $"- {path}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Source Sample");
        if (sample.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(sample.Select(path => $"- {path}"));
        }

        AddSurveySection(lines, "Likely Tests", likelyTests);
        AddSurveySection(lines, "Public API Symbols", publicSymbols);
        AddSurveySection(lines, "Call-Site Hints", callSiteHints);
        AddSurveySection(lines, "Ownership Hints", ownershipHints);

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddSurveySection(List<string> lines, string heading, List<string> values)
    {
        lines.Add(string.Empty);
        lines.Add($"## {heading}");
        if (values.Count == 0)
        {
            lines.Add("- none");
            return;
        }

        lines.AddRange(values.Select(value => $"- {value}"));
    }

    private static List<string> BuildPublicApiSymbols(string workingDirectory, IReadOnlyList<string> sourceFiles)
    {
        var symbols = new List<string>();
        foreach (var relativePath in sourceFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(SourceSurveyMaxAnalysisFiles))
        {
            foreach (var line in ReadSourceLines(workingDirectory, relativePath))
            {
                var typeMatch = PublicTypeRegex.Match(line);
                if (typeMatch.Success)
                {
                    symbols.Add($"{relativePath}: {typeMatch.Groups[1].Value} {typeMatch.Groups[2].Value} {typeMatch.Groups[3].Value}");
                    break;
                }

                var memberMatch = PublicMemberRegex.Match(line);
                if (memberMatch.Success)
                {
                    symbols.Add($"{relativePath}: {memberMatch.Groups[1].Value} member {memberMatch.Groups[2].Value}");
                    break;
                }
            }

            if (symbols.Count >= SourceSurveyMaxFiles)
            {
                break;
            }
        }

        return symbols;
    }

    private static List<string> BuildLikelyTests(IReadOnlyList<string> sourceFiles, IReadOnlyList<string> terms)
    {
        return sourceFiles
            .Where(IsLikelyTestPath)
            .OrderByDescending(path => terms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(SourceSurveyMaxFiles)
            .ToList();
    }

    private static List<string> BuildCallSiteHints(string workingDirectory, IReadOnlyList<string> sourceFiles, List<string> terms)
    {
        if (terms.Count == 0)
        {
            return [];
        }

        var hints = new List<string>();
        foreach (var relativePath in sourceFiles.Take(SourceSurveyMaxAnalysisFiles))
        {
            var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ReadSourceLines(workingDirectory, relativePath))
            {
                foreach (var term in terms)
                {
                    if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedTerms.Add(term);
                    }
                }

                if (matchedTerms.Count >= 4)
                {
                    break;
                }
            }

            if (matchedTerms.Count > 0)
            {
                hints.Add($"{relativePath}: {string.Join(", ", matchedTerms.OrderBy(term => term, StringComparer.OrdinalIgnoreCase))}");
            }

            if (hints.Count >= SourceSurveyMaxFiles)
            {
                break;
            }
        }

        return hints;
    }

    private static List<string> BuildOwnershipHints(string workingDirectory, IReadOnlyList<string> sourceFiles)
    {
        var hints = sourceFiles
            .GroupBy(GetOwnershipBucket, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(group => $"{group.Key}: {group.Count()} file(s)")
            .ToList();

        if (File.Exists(Path.Combine(workingDirectory, "AGENTS.md")))
        {
            hints.Insert(0, "AGENTS.md: repository-local operator and worker instructions");
        }

        return hints;
    }

    private static IEnumerable<string> ReadSourceLines(string workingDirectory, string relativePath)
    {
        var path = Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            yield break;
        }

        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            yield return line;
            count++;
            if (count >= SourceSurveyMaxFileLines)
            {
                yield break;
            }
        }
    }

    private static bool IsLikelyTestPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        return normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOwnershipBucket(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}: production source" : "src: production source";
        }

        if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}: test source" : "tests: test source";
        }

        if (normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            return "scripts: operator tooling";
        }

        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            return "docs: repository documentation";
        }

        if (normalized.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase))
        {
            return ".agents: worker skills and guidance";
        }

        return ".: repository root";
    }

    private static string BuildDiffSummary(string workingDirectory)
    {
        var lines = new List<string>
        {
            "# Diff Summary",
            string.Empty,
            $"Working directory: {workingDirectory}",
            "Regeneration: generated at dispatch preparation; treat as stale when git status or HEAD changes after dispatch.",
            string.Empty,
            "## Git Status"
        };

        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Changed Files Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Diff Stat Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.AddRange(ReadGitSection(workingDirectory, ["status", "--short"]));
        lines.Add(string.Empty);
        lines.Add("## Changed Files Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--name-status", "HEAD", "--"]));
        lines.Add(string.Empty);
        lines.Add("## Diff Stat Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--stat", "HEAD", "--"]));

        return TrimArtifactBlock(string.Join(Environment.NewLine, lines), DiffSummaryMaxChars);
    }

    private static bool LooksLikeGitWorkspace(string workingDirectory)
    {
        return Directory.Exists(Path.Combine(workingDirectory, ".git")) ||
            File.Exists(Path.Combine(workingDirectory, ".git"));
    }

    private static List<string> ReadGitSection(string workingDirectory, string[] arguments)
    {
        if (!TryRunGit(workingDirectory, arguments, out var output))
        {
            return ["- git data unavailable for this workspace."];
        }

        var lines = output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(80)
            .Select(line => $"- {line}")
            .ToList();
        return lines.Count == 0 ? ["- clean"] : lines;
    }

    private static bool TryRunGit(string workingDirectory, string[] arguments, out string output)
    {
        output = string.Empty;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup; missing git data should not block dispatch preparation.
                }

                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.ExitCode == 0;
        }
        catch
        {
            output = string.Empty;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return [];
        }

        return EnumerateFilesPruned(workingDirectory, workingDirectory)
            .Where(path => SourceSurveyExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesPruned(string root, string directory)
    {
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            var relativeDirectory = Path.GetRelativePath(root, childDirectory).Replace('\\', '/');
            if (IsExcludedSurveyPath(relativeDirectory))
            {
                continue;
            }

            foreach (var childFile in EnumerateFilesPruned(root, childDirectory))
            {
                yield return childFile;
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!IsExcludedSurveyPath(relativePath))
            {
                yield return relativePath;
            }
        }
    }

    private static bool IsExcludedSurveyPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".scratch", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-context", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-prototype", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-worktrees", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("playwright-report", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSurveyDirectory(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "." : normalized[..slash];
    }

    private static List<string> BuildSearchTerms(Goal goal, TaskSpec task)
    {
        var text = $"{goal.Objective} {task.Description} {task.VerificationPlan}";
        return text
            .Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '/', '\\', '-', '_', '`', '\'', '"', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 4)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private static bool TryParseWorkerResultContract(TaskVerificationRecord verification, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = $"{verification.StandardOutput}\n{verification.StandardError}"
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim().Equals("WORKER_RESULT:", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return false;
        }

        var end = Array.FindIndex(lines, start + 1, line => line.Trim().Equals("END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase));
        if (end < 0)
        {
            // Tolerate missing END marker: treat the next blank line, heading, or EOF as the terminator.
            end = Array.FindIndex(lines, start + 1, line => { var t = line.Trim(); return t.Length == 0 || t.StartsWith('#'); });
            if (end < 0) end = lines.Length;
        }

        for (var index = start + 1; index < end; index++)
        {
            var line = lines[index].Trim();
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return fields.Count > 0;
    }

    private static string[] SplitContractList(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/playwright-report/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPriorTaskSummaries(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return "# Prior Task Summaries" + Environment.NewLine + Environment.NewLine + "No prior completed task summaries.";
        }

        var lines = new List<string> { "# Prior Task Summaries" };
        foreach (var priorTask in priorCompletedTasks)
        {
            var verification = priorTask.LastVerification!;
            lines.Add(string.Empty);
            lines.Add($"## {priorTask.RequiredRole}: {TrimDigestTitle(priorTask.Description)}");
            lines.Add($"Task id: {priorTask.Id.Value}");
            lines.Add($"Changed files: {SummarizeEvidenceField(verification, "changed files", "changed file", "files changed")}");
            lines.Add($"Behavior changes: {SummarizeEvidenceField(verification, "behavior changes", "behavior change", "behavior", "changes")}");
            lines.Add($"Verification: `{verification.Command}` in `{verification.WorkingDirectory}` exited {verification.ExitCode}.");
            lines.Add($"Verification result: {SummarizeVerificationResult(verification)}");
            lines.Add($"Risks: {SummarizeRisks(verification)}");
            lines.Add($"Model fit: {SummarizeModelFit(verification)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPriorTaskEvidence(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return "# Prior Task Evidence" + Environment.NewLine + Environment.NewLine + "No prior completed task evidence.";
        }

        var lines = new List<string> { "# Prior Task Evidence" };
        foreach (var priorTask in priorCompletedTasks)
        {
            var verification = priorTask.LastVerification!;
            lines.Add(string.Empty);
            lines.Add($"## {priorTask.RequiredRole}: {priorTask.Description}");
            lines.Add($"Task id: {priorTask.Id.Value}");
            lines.Add($"Verification command: {verification.Command}");
            lines.Add($"Verification working directory: {verification.WorkingDirectory}");
            lines.Add($"Verification exit code: {verification.ExitCode}");
            if (!string.IsNullOrWhiteSpace(verification.ModelFitNote))
            {
                lines.Add($"Model fit: {verification.ModelFitNote}");
            }

            lines.Add(string.Empty);
            lines.Add("### Stdout");
            lines.Add(TrimArtifactBlock(verification.StandardOutput, PriorVerificationMaxChars));
            if (!string.IsNullOrWhiteSpace(verification.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("### Stderr");
                lines.Add(TrimArtifactBlock(verification.StandardError, PriorVerificationMaxChars));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<string> CopyGuidanceFiles(string workingDirectory, string contextDirectory)
    {
        var copied = new List<string>();
        foreach (var fileName in new[] { "AGENTS.md", "BACKLOG.md", "DOGFOOD_LOG.md" })
        {
            var sourcePath = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Combine(contextDirectory, fileName);
            WriteText(targetPath, TrimArtifactBlock(File.ReadAllText(sourcePath), GuidanceFileMaxChars));
            copied.Add(fileName);
        }

        return copied;
    }

    private static string BuildManifest(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string> guidanceFiles,
        IReadOnlyList<string>? preflightFindings)
    {
        var lines = new List<string>
        {
            "# Worker Context Manifest",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Artifacts",
            "- artifact-registry.json: machine-readable artifact registry with relative paths, hashes, byte counts, summaries, freshness, and role visibility.",
            "- digest.md: compact role-aware summary of objective, current task, prior outcomes, verification status, evidence pointers, and open risks/blockers.",
            "- objective.md: full goal objective and goal status.",
            "- current-task.md: current task description, role, working directory, verification plan, and retry evidence when present.",
            "- deterministic-verification.md: deterministic checklist of manifest presence, verification records, worker result contracts, and generated-path risk.",
            "- workflow-brokers.md: deterministic broker actions for build/test selection, source survey, diff summary, acceptance evidence, and backlog/log evidence.",
            "- context-budget.md: deterministic prompt budget policy, retrieval handles, and embed/summarize/retrieve/omit decisions.",
            "- selected-skills.md: deterministic skill selection with skill paths, availability, reasons, and usage notes.",
            "- source-survey.md: compact repository source inventory, directory counts, task-term file matches, public API symbols, likely tests, call-site hints, and ownership hints.",
            "- diff-summary.md: compact git status, changed files, and diff stat for the workspace.",
            "- prior-task-summaries.md: compact prior task summaries with changed files, behavior changes, verification commands/results, risks, and model fit.",
            "- prior-task-evidence.md: prior completed task verification evidence with a larger file budget than inline prompts.",
            "- context-package.json: task-scoped package metadata and fallback guidance for this dispatch."
        };

        if (preflightFindings is { Count: > 0 })
        {
            lines.Add("- subscription-preflight.md: deterministic profile, sandbox, worktree, retry, and capability checks evaluated before dispatch.");
        }

        foreach (var guidanceFile in guidanceFiles)
        {
            lines.Add($"- {guidanceFile}: repository-local guidance copied from the working directory.");
        }

        lines.Add(string.Empty);
        lines.Add("## Role Artifact Priorities");
        lines.AddRange(BuildRoleArtifactPriorities(task.RequiredRole));
        lines.Add(string.Empty);
        lines.Add("## Missing Artifact Fallback");
        lines.Add("If an artifact listed here is missing or has a failed hash in artifact-registry.json, read digest.md first, then current-task.md, prior-task-summaries.md, and diff-summary.md. Treat missing prior-task-evidence.md as a verification gap and report it in WORKER_RESULT blockers instead of guessing.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildContextPackage(Goal goal, TaskSpec task, string contextDirectory)
    {
        var package = new ContextPackageMetadata(
            1,
            goal.Id.Value,
            task.Id.Value,
            Path.Combine(contextDirectory, "packages", task.Id.Value),
            DateTimeOffset.UtcNow,
            "Read digest.md first, then manifest.md and artifact-registry.json. If a referenced artifact is missing or hash verification fails, fall back to current-task.md, prior-task-summaries.md, and diff-summary.md; report the missing artifact as a blocker before using stale or guessed evidence.");
        return JsonSerializer.Serialize(package, RegistryJsonOptions);
    }

    private static void SnapshotCurrentPackage(string contextDirectory, TaskId taskId)
    {
        var packageDirectory = Path.Combine(contextDirectory, "packages", taskId.Value);
        Directory.CreateDirectory(packageDirectory);
        foreach (var file in Directory.EnumerateFiles(contextDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(packageDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void WriteArtifactRegistry(
        string contextDirectory,
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string> guidanceFiles,
        IReadOnlyList<string>? preflightFindings)
    {
        var artifactNames = new List<string>
        {
            "digest.md",
            "objective.md",
            "current-task.md",
            "deterministic-verification.md",
            "workflow-brokers.md",
            "context-budget.md",
            "selected-skills.md",
            "source-survey.md",
            "diff-summary.md",
            "prior-task-summaries.md",
            "prior-task-evidence.md",
            "manifest.md",
            "context-package.json"
        };

        if (preflightFindings is { Count: > 0 })
        {
            artifactNames.Add("subscription-preflight.md");
        }

        artifactNames.AddRange(guidanceFiles);
        var entries = artifactNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => BuildRegistryEntry(contextDirectory, name, task.RequiredRole))
            .ToArray();
        var registry = new ContextArtifactRegistry(
            1,
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole.ToString(),
            workingDirectory,
            DateTimeOffset.UtcNow,
            entries,
            entries.All(entry => entry.Exists && entry.HashVerified));

        WriteText(Path.Combine(contextDirectory, "artifact-registry.json"), JsonSerializer.Serialize(registry, RegistryJsonOptions));
    }

    private static ContextArtifactRegistryEntry BuildRegistryEntry(string contextDirectory, string relativePath, AgentRole currentRole)
    {
        var path = Path.Combine(contextDirectory, relativePath);
        if (!File.Exists(path))
        {
            return new ContextArtifactRegistryEntry(
                relativePath,
                false,
                0,
                string.Empty,
                BuildArtifactSummary(relativePath),
                "missing",
                BuildRoleVisibility(relativePath),
                false);
        }

        var bytes = File.ReadAllBytes(path);
        return new ContextArtifactRegistryEntry(
            relativePath,
            true,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            BuildArtifactSummary(relativePath),
            BuildFreshness(relativePath, currentRole),
            BuildRoleVisibility(relativePath),
            true);
    }

    private static string BuildArtifactSummary(string relativePath)
    {
        return relativePath switch
        {
            "digest.md" => "Compact role-aware current context and evidence pointers.",
            "objective.md" => "Full goal objective and status.",
            "current-task.md" => "Current task, verification plan, retry evidence, and worker result contract schema.",
            "deterministic-verification.md" => "Deterministic verification checklist before LLM review.",
            "workflow-brokers.md" => "Deterministic broker action manifest for common worker chores.",
            "context-budget.md" => "Prompt budget policy with artifact retrieval handles and embed/retrieve decisions.",
            "selected-skills.md" => "Deterministic local skill selection with availability and usage notes.",
            "source-survey.md" => "Compact repository source inventory, task-term matches, symbols, tests, call-site hints, and ownership hints.",
            "diff-summary.md" => "Compact git status, changed files, and diff stat.",
            "prior-task-summaries.md" => "Compact prior task outcomes and verification summaries.",
            "prior-task-evidence.md" => "Larger prior verification evidence for targeted inspection.",
            "manifest.md" => "Human-readable artifact descriptions and role priorities.",
            "context-package.json" => "Task-scoped context package metadata and missing-artifact fallback guidance.",
            "subscription-preflight.md" => "Subscription dispatch preflight findings.",
            "AGENTS.md" => "Repository-local agent instructions.",
            "BACKLOG.md" => "Open orchestrator backlog context.",
            "DOGFOOD_LOG.md" => "Recent dogfood evidence and friction.",
            _ => "Copied repository guidance artifact."
        };
    }

    private static string BuildFreshness(string relativePath, AgentRole currentRole)
    {
        return relativePath switch
        {
            "prior-task-evidence.md" or "prior-task-summaries.md" => "generated from completed prior task verification records at dispatch preparation",
            "deterministic-verification.md" => "generated from current task state and prior verification records at dispatch preparation",
            "workflow-brokers.md" => "generated from current task, changed files, verification policy, and deterministic broker availability at dispatch preparation",
            "context-budget.md" => "generated from role, current task, prompt budget constants, prior task count, and selected skill availability at dispatch preparation",
            "selected-skills.md" => "generated from role, objective, task text, verification plan, and repository skill files at dispatch preparation",
            "source-survey.md" => "generated from source files, objective, task text, and verification plan in the working directory at dispatch preparation",
            "diff-summary.md" => "generated from git status and diff commands in the working directory at dispatch preparation",
            "context-package.json" => "generated with the current task package at dispatch preparation",
            "subscription-preflight.md" => "generated from subscription preflight immediately before dispatch preparation",
            "AGENTS.md" or "BACKLOG.md" or "DOGFOOD_LOG.md" => "copied from working directory at dispatch preparation",
            _ => $"generated for {currentRole} at dispatch preparation"
        };
    }

    private static IReadOnlyList<string> BuildRoleVisibility(string relativePath)
    {
        return relativePath switch
        {
            "deterministic-verification.md" => ["Tester", "Reviewer"],
            "diff-summary.md" => ["Developer", "Tester", "Reviewer"],
            "subscription-preflight.md" => ["Developer", "Tester", "Reviewer"],
            "prior-task-evidence.md" => ["Developer", "Tester", "Reviewer"],
            "AGENTS.md" or "BACKLOG.md" or "DOGFOOD_LOG.md" => ["Planner", "Researcher", "Developer", "Tester", "Reviewer"],
            _ => ["Planner", "Researcher", "Developer", "Tester", "Reviewer"]
        };
    }

    private static string TrimArtifactBlock(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] +
            Environment.NewLine +
            $"...[truncated {trimmed.Length - maxChars} chars for context artifact budget]...";
    }

    private static string TrimDigestText(string value)
    {
        return TrimArtifactBlock(value, DigestTextMaxChars).ReplaceLineEndings(" ");
    }

    private static string TrimDigestEvidence(string value)
    {
        return TrimArtifactBlock(value, DigestEvidenceMaxChars).ReplaceLineEndings(" ");
    }

    private static string TrimDigestTitle(string value)
    {
        var trimmed = value.Trim().ReplaceLineEndings(" ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
    }

    private static string BuildRoleFocus(AgentRole role)
    {
        return role switch
        {
            AgentRole.Planner => "Clarify approach, sequencing, risks, and handoff decisions; do not modify repository files.",
            AgentRole.Researcher => "Inspect source and report evidence-backed findings; do not modify repository files.",
            AgentRole.Developer => "Implement scoped source changes and verify them with repository-local commands.",
            AgentRole.Tester => "Strengthen or run focused verification and report exact failures or coverage gaps.",
            AgentRole.Reviewer => "Prioritize bugs, regressions, risk, and missing tests before summaries.",
            _ => "Complete the assigned task and report evidence."
        };
    }

    private static IReadOnlyList<string> BuildRoleArtifactPriorities(AgentRole role)
    {
        return role switch
        {
            AgentRole.Planner =>
            [
                "- 1. artifact-registry.json: confirm available artifacts, hashes, and freshness before opening content.",
                "- 2. context-budget.md: use retrieval handles before asking for large evidence in prompts.",
                "- 3. objective.md: preserve the goal, scope, and acceptance path.",
                "- 4. digest.md: identify current role focus, blockers, and prior outcomes."
            ],
            AgentRole.Researcher =>
            [
                "- 1. artifact-registry.json: identify relevant artifacts and guidance before broad file reads.",
                "- 2. context-budget.md: choose summaries and handles before opening larger artifacts.",
                "- 3. digest.md: start with role focus, prior outcomes, and open risks.",
                "- 4. prior-task-summaries.md: inspect compact prior findings before full logs."
            ],
            AgentRole.Developer =>
            [
                "- 1. artifact-registry.json: verify current context artifacts and hashes before implementation.",
                "- 2. context-budget.md: prefer artifact handles over copying large evidence into prompts.",
                "- 3. workflow-brokers.md: use deterministic broker actions for test selection, source survey, diff summary, and acceptance evidence.",
                "- 4. selected-skills.md: read only the relevant selected skills before editing or testing.",
                "- 5. source-survey.md: identify likely source and test files before broad reads.",
                "- 6. diff-summary.md: check existing workspace changes before editing.",
                "- 7. current-task.md: anchor implementation scope and verification plan.",
                "- 8. prior-task-summaries.md: read compact prior behavior, file, risk, and model-fit evidence first."
            ],
            AgentRole.Tester =>
            [
                "- 1. artifact-registry.json: verify artifact freshness and locate deterministic evidence.",
                "- 2. context-budget.md: retrieve only evidence needed for the verification decision.",
                "- 3. workflow-brokers.md: use deterministic broker actions for required checks and failure handling.",
                "- 4. selected-skills.md: read selected verification/build hygiene skills before running checks.",
                "- 5. diff-summary.md: identify changed files before selecting tests.",
                "- 6. deterministic-verification.md: identify changed files, behavior claims, risks, and verification gaps.",
                "- 7. current-task.md: map the required checks to the task verification plan."
            ],
            AgentRole.Reviewer =>
            [
                "- 1. artifact-registry.json: verify hashes, freshness, and role-visible artifacts before LLM judgment.",
                "- 2. context-budget.md: use summaries and handles before opening full prior evidence.",
                "- 3. workflow-brokers.md: check deterministic broker failures before judging worker claims.",
                "- 4. selected-skills.md: read selected review/verification skills before judging evidence.",
                "- 5. diff-summary.md: inspect changed files and diff stat before broad review.",
                "- 6. deterministic-verification.md: check deterministic failures, warnings, manifest status, and worker result contracts.",
                "- 7. prior-task-summaries.md: review changed files, behavior changes, verification, risks, and model fit before full logs."
            ],
            _ =>
            [
                "- 1. digest.md: start with compact context.",
                "- 2. current-task.md: confirm the assigned work.",
                "- 3. prior-task-summaries.md: inspect prior completed work before full evidence."
            ]
        };
    }

    private static List<string> BuildOpenRiskLines(Goal goal, TaskSpec task)
    {
        var risks = new List<string>();
        foreach (var priorTask in goal.Tasks.TakeWhile(t => t.Id != task.Id))
        {
            if (priorTask.Status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
            {
                risks.Add($"- Prior {priorTask.RequiredRole} task {priorTask.Id.Value} is {priorTask.Status}.");
            }
            else if (priorTask.LastVerification is { Succeeded: false } verification)
            {
                risks.Add($"- Prior {priorTask.RequiredRole} verification failed with exit {verification.ExitCode}: {TrimDigestTitle(priorTask.Description)}");
            }
        }

        if (task.LastVerification is { Succeeded: false } lastVerification)
        {
            risks.Add($"- Current task last verification failed with exit {lastVerification.ExitCode}: `{lastVerification.Command}`.");
        }

        if (task.Status == WorkTaskStatus.WaitingForHuman)
        {
            risks.Add("- Current task is waiting for human input.");
        }

        return risks;
    }

    private static string SummarizeEvidenceField(TaskVerificationRecord verification, params string[] labels)
    {
        foreach (var line in SplitEvidenceLines(verification))
        {
            foreach (var label in labels)
            {
                if (!TryExtractLabelValue(line, label, out var value))
                {
                    continue;
                }

                return TrimSummaryField(value);
            }
        }

        return "Not reported.";
    }

    private static string SummarizeVerificationResult(TaskVerificationRecord verification)
    {
        var labeledResult = SummarizeEvidenceField(verification, "verification result", "result");
        if (labeledResult != "Not reported.")
        {
            return labeledResult;
        }

        var evidence = SplitEvidenceLines(verification)
            .FirstOrDefault(line => line.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("warning", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            return TrimSummaryField(evidence);
        }

        return verification.Succeeded
            ? "Verification command succeeded."
            : "Verification command failed; inspect prior-task-evidence.md for output.";
    }

    private static string SummarizeRisks(TaskVerificationRecord verification)
    {
        var labelValue = SummarizeEvidenceField(verification, "risks", "risk", "blockers", "blocker");
        if (labelValue != "Not reported.")
        {
            return labelValue;
        }

        if (!verification.Succeeded)
        {
            return "Verification failed.";
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            return TrimSummaryField(verification.StandardError.ReplaceLineEndings(" "));
        }

        return "None reported.";
    }

    private static string SummarizeModelFit(TaskVerificationRecord verification)
    {
        return verification.ModelFitNote ??
            ModelFitEvidence.TryExtractNote(verification.StandardOutput, verification.StandardError) ??
            "Not reported.";
    }

    private static bool TryExtractLabelValue(string line, string label, out string value)
    {
        var normalizedLine = line.Trim().TrimStart('-', '*', ' ');
        var prefix = label + ":";
        if (normalizedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = normalizedLine[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static string TrimSummaryField(string value)
    {
        var trimmed = value.Trim().ReplaceLineEndings(" ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return trimmed.Length <= SummaryFieldMaxChars ? trimmed : trimmed[..(SummaryFieldMaxChars - 3)] + "...";
    }

    private static IEnumerable<string> SplitEvidenceLines(TaskVerificationRecord verification)
    {
        return verification.StandardOutput
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(verification.StandardError
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void AddEvidencePointers(List<string> lines, TaskVerificationRecord verification)
    {
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            lines.Add($"  Stdout path: {verification.StandardOutputPath}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath))
        {
            lines.Add($"  Stderr path: {verification.StandardErrorPath}");
        }
    }

    private static void WriteText(string path, string content)
    {
        File.WriteAllText(path, content);
    }

    private sealed record SkillCandidate(string Name, string RelativePath, string Usage);

    private sealed record ContextArtifactRegistry(
        int Version,
        string GoalId,
        string TaskId,
        string Role,
        string WorkingDirectory,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<ContextArtifactRegistryEntry> Artifacts,
        bool Verified);

    private sealed record ContextArtifactRegistryEntry(
        string Path,
        bool Exists,
        long ByteCount,
        string Sha256,
        string Summary,
        string Freshness,
        IReadOnlyList<string> RoleVisibility,
        bool HashVerified);

    private sealed record ContextPackageMetadata(
        int Version,
        string GoalId,
        string TaskId,
        string PackagePath,
        DateTimeOffset GeneratedAt,
        string MissingArtifactFallback);
}
