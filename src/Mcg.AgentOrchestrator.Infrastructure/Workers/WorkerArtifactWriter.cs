using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerArtifactWriter
{
    private const int PriorVerificationMaxChars = 40000;
    private const int GuidanceFileMaxChars = 30000;
    private const int CurrentEvidenceMaxChars = 20000;
    private const int DigestTextMaxChars = 700;
    private const int DigestEvidenceMaxChars = 500;
    private const int SummaryFieldMaxChars = 350;

    private readonly WorkerSourceSurvey _sourceSurvey = new();
    private readonly WorkerGitContext _gitContext = new();
    private readonly WorkerSkillSelector _skillSelector = new();
    private readonly WorkerResultContractParser _resultContractParser = new();
    private static readonly JsonSerializerOptions RegistryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal string Write(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string>? preflightFindings = null)
    {
        var scratchRoot = Path.Combine(workingDirectory, ".orchestrator-context");
        var contextDirectory = Path.Combine(scratchRoot, goal.Id.Value);
        Directory.CreateDirectory(contextDirectory);

        // Self-ignore the scratch tree so it never dirties repositories whose root
        // .gitignore lacks the tracked orchestrator rule.
        // A ".gitignore" containing "*" ignores every file in the directory
        // (including itself), so "git status" stays clean before acceptance.
        var scratchIgnore = Path.Combine(scratchRoot, ".gitignore");
        if (!File.Exists(scratchIgnore))
        {
            File.WriteAllText(scratchIgnore, "*" + Environment.NewLine);
        }

        WriteText(Path.Combine(contextDirectory, "objective.md"), BuildObjective(goal));
        WriteText(Path.Combine(contextDirectory, "current-task.md"), BuildCurrentTask(task, workingDirectory));
        var durablePlannerPlans = ResolveDurablePlannerPlans(goal.Tasks, task.Id);
        var durableResearch = ResolveLatestDurableResearch(goal.Tasks, task.Id);
        var durablePlan = durablePlannerPlans.Values.LastOrDefault(resolution => resolution.Succeeded);
        WriteOptionalArtifact(
            Path.Combine(contextDirectory, "research-notes.md"),
            durableResearch.Succeeded ? durableResearch.Research : null);
        WriteOptionalArtifact(
            Path.Combine(contextDirectory, "planner-plan.md"),
            durablePlan?.Succeeded == true ? durablePlan.Plan : null);
        WriteText(
            Path.Combine(contextDirectory, "prior-task-summaries.md"),
            BuildPriorTaskSummaries(goal.Tasks, task.Id, durablePlannerPlans));
        WriteText(
            Path.Combine(contextDirectory, "prior-task-evidence.md"),
            BuildPriorTaskEvidence(goal.Tasks, task.Id, durablePlannerPlans));
        WriteText(Path.Combine(contextDirectory, "deterministic-verification.md"), BuildDeterministicVerification(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "workflow-brokers.md"), BuildWorkflowBrokers(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "context-budget.md"), BuildContextBudget(goal, task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "selected-skills.md"), _skillSelector.BuildSelectedSkills(goal, task, workingDirectory));
        var sourceSurveyPath = Path.Combine(contextDirectory, "source-survey.md");
        if (task.RequiredRole == AgentRole.Planner && durableResearch.Succeeded)
        {
            File.Delete(sourceSurveyPath);
        }
        else
        {
            WriteText(sourceSurveyPath, _sourceSurvey.BuildSourceSurvey(goal, task, workingDirectory));
        }
        WriteText(Path.Combine(contextDirectory, "diff-summary.md"), _gitContext.BuildDiffSummary(workingDirectory));
        if (preflightFindings is { Count: > 0 })
        {
            WriteText(Path.Combine(contextDirectory, "subscription-preflight.md"), BuildPreflight(preflightFindings));
        }

        WriteText(Path.Combine(contextDirectory, "digest.md"), BuildDigest(goal, task, workingDirectory, preflightFindings));

        var guidanceFiles = CopyGuidanceFiles(workingDirectory, contextDirectory);
        WriteText(
            Path.Combine(contextDirectory, "manifest.md"),
            BuildManifest(goal, task, workingDirectory, guidanceFiles, preflightFindings, contextDirectory));
        WriteText(Path.Combine(contextDirectory, "context-package.json"), BuildContextPackage(goal, task, contextDirectory));
        WriteArtifactRegistry(contextDirectory, goal, task, workingDirectory, guidanceFiles, preflightFindings);
        SnapshotCurrentPackage(contextDirectory, task.Id);

        WriteAcceptanceCriteriaIfNonEmpty(goal.Objective, workingDirectory);

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

        if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester)
        {
            lines.InsertRange(6,
            [
                string.Empty,
                "## Build/Test Verification",
                "- Run `.\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]` before writing WORKER_RESULT for every project whose sources you changed; this is the only sanctioned worker-side .NET build check.",
                "- Report the build result in WORKER_RESULT `tests`, for example `tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)` or `tests: fail - <build error>`.",
                "- Do not run raw `dotnet test`, raw `dotnet build`, or `.\\scripts\\Invoke-IsolatedDotnet.ps1` directly from a subscription worker; raw test execution can create per-worktree testhost firewall prompts.",
                "- If no .NET project sources changed, report the non-.NET verification you ran or `tests: not-run - no .NET project sources changed; orchestrator acceptance gate verifies via stable slots`."
            ]);
        }

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add(string.Empty);
            lines.Add("## Verification Plan");
            lines.Add(task.VerificationPlan);
        }

        if (task.CriterionRetryFeedback.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Unmet acceptance criteria from the prior attempt - fix these:");
            foreach (var feedback in task.CriterionRetryFeedback)
            {
                lines.Add($"- {feedback}");
            }
        }

        if (task.LastExecution is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Last Model Output");
            lines.Add(WorkerContextHelpers.TrimArtifactBlock(task.LastExecution.Output, CurrentEvidenceMaxChars));
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
            lines.Add(WorkerContextHelpers.TrimArtifactBlock(task.LastVerification.StandardOutput, CurrentEvidenceMaxChars));
            if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("### Stderr");
                lines.Add(WorkerContextHelpers.TrimArtifactBlock(task.LastVerification.StandardError, CurrentEvidenceMaxChars));
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Worker Result Contract");
        lines.Add("End final output with:");
        lines.AddRange(AgentOutputDirectives.WorkerResultTemplateLinesForRole(task.RequiredRole));

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildDeterministicVerification(Goal goal, TaskSpec task, string workingDirectory)
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
            var toolchain = TargetToolchainDetector.Detect(workingDirectory);
            lines.Add(string.Empty);
            if (toolchain == Toolchain.Dotnet)
            {
                lines.Add("## Worker Build Check");
                lines.Add("- Developer/Tester subscription workers must run `.\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]` for every project whose sources they changed before writing WORKER_RESULT.");
                lines.Add($"- The helper performs build-only verification through isolated artifacts under `{DotnetBuildEnvironmentManager.GoalArtifactsPath(goal.Id)}`; it does not run tests or spawn testhost.");
                lines.Add("- Subscription workers must not run raw `dotnet test`, raw `dotnet build`, or `.\\scripts\\Invoke-IsolatedDotnet.ps1`; raw test execution can create per-worktree testhost firewall prompts.");
                lines.Add("- In WORKER_RESULT, report build evidence such as `tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)` or `tests: fail - <build error>`.");
            }
            else if (toolchain == Toolchain.Go)
            {
                lines.Add("## Go Verification");
                lines.Add("- Use `go build ./...` to verify the project compiles.");
                lines.Add("- Use `go test ./...` to run the full test suite.");
                lines.Add("- Use `go test ./path/to/package/...` for focused package tests.");
            }
            else if (toolchain == Toolchain.Node)
            {
                lines.Add("## Node.js Verification");
                lines.Add("- Use `npm test` (or the package.json test script) to run the test suite.");
                lines.Add("- Use `npm run build` to verify the project builds.");
                lines.Add("- Check package.json scripts for the project's test and lint commands.");
            }
            else if (toolchain == Toolchain.Python)
            {
                lines.Add("## Python Verification");
                lines.Add("- Use `python -m pytest` (or the project test command) to run tests.");
                lines.Add("- Check pyproject.toml or requirements.txt for test configuration.");
            }
            else
            {
                lines.Add("## Verification");
                lines.Add("- Run the project's standard build and test commands.");
                lines.Add("- Check the repository README or CI configuration for the correct commands.");
            }
        }

        var changedFiles = _gitContext.ReadChangedFilesForTestImpact(workingDirectory);
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
                _resultContractParser.AddWorkerResultContractFindings(lines, priorTask, verification);
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

    private string BuildWorkflowBrokers(Goal goal, TaskSpec task, string workingDirectory)
    {
        var changedFiles = _gitContext.ReadChangedFilesForTestImpact(workingDirectory);
        var testImpactPlan = RepositoryTestImpactPlanner.Plan(changedFiles);
        var verificationPolicy = VerificationPolicyCompiler.Compile(
            task.RequiredRole,
            goal.Objective,
            task.Description,
            task.VerificationPlan,
            changedFiles);
        var goalPrefix = goal.Id.Value.Length <= 8 ? goal.Id.Value : goal.Id.Value[..8];
        var attemptName = $"{task.RequiredRole.ToString().ToLowerInvariant()}-{task.Id.Value[..8]}";
        var toolchain = TargetToolchainDetector.Detect(workingDirectory);
        var brokerNote = TargetToolchainDetector.GetBrokerBuildTestNote(toolchain);
        var brokerCommandHint = TargetToolchainDetector.GetBrokerCommandHint(toolchain, goalPrefix, attemptName);
        var lines = new List<string>
        {
            "# Workflow Brokers",
            string.Empty,
            "Use deterministic broker artifacts before rediscovering common workflow steps in the prompt. If a broker artifact is missing, stale, or reports a failure, report that as a blocker in WORKER_RESULT instead of guessing.",
            string.Empty,
            "## Available Broker Actions",
            "- build-test-selection",
            "  Artifact: deterministic-verification.md",
            $"  Use for: choosing focused tests, identifying required checks, and {brokerNote}.",
            $"  Suggested command shape: `{brokerCommandHint}`",
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
            "  Command: `dogfood-log list --limit <n>` / `dogfood-log add <goal-prefix>`",
            "  Store: `.orchestrator/dogfood-log.db` is the durable dogfood evidence source; DOGFOOD_LOG.md is only an operator pointer when present.",
            "  Use for: closing backlog items, filing follow-ups, and recording Model fit at goal boundaries.",
            "  Failure handling: missing backlog/log evidence should be resolved with the SQLite-backed command surface, not file edits.",
            string.Empty,
            "## Broker Output Contract",
            "- Prefer broker artifact paths in your evidence over repeating full artifact contents.",
            "- Include broker failures in WORKER_RESULT blockers.",
            "- Include broker commands and required checks in WORKER_RESULT commands/tests."
        };

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildContextBudget(Goal goal, TaskSpec task, string workingDirectory)
    {
        var selected = _skillSelector.SelectSkillRequirements(goal, task, workingDirectory);
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
            $"- diff-summary.md retrieval budget: {WorkerGitContext.DiffSummaryRetrievalMaxChars} chars.",
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

    private string BuildPriorTaskSummaries(
        IReadOnlyList<TaskSpec> goalTasks,
        TaskId taskId,
        IReadOnlyDictionary<TaskId, DurablePlannerPlanResolution> durablePlannerPlans)
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
            if (priorTask.RequiredRole == AgentRole.Planner)
            {
                var resolution = durablePlannerPlans[priorTask.Id];
                lines.Add(resolution.Succeeded
                    ? "Durable plan: complete Planner plan is in prior-task-evidence.md and is required implementation input."
                    : $"Durable plan: UNAVAILABLE ({resolution.Diagnostic}); retry Planner before implementation.");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPriorTaskEvidence(
        IReadOnlyList<TaskSpec> goalTasks,
        TaskId taskId,
        IReadOnlyDictionary<TaskId, DurablePlannerPlanResolution> durablePlannerPlans)
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
            if (priorTask.RequiredRole == AgentRole.Planner)
            {
                var resolution = durablePlannerPlans[priorTask.Id];
                if (resolution.Succeeded)
                {
                    lines.Add("### Durable Planner Plan");
                    lines.Add(resolution.Plan);
                    lines.Add(string.Empty);
                    lines.Add("### Planner WORKER_RESULT Receipt");
                    lines.Add(WorkerContextHelpers.TrimArtifactBlock(verification.StandardOutput, SummaryFieldMaxChars * 4));
                }
                else
                {
                    lines.Add("### Durable Planner Plan Retrieval Failure");
                    lines.Add($"{resolution.Diagnostic}. Retry Planner before implementation; truncated stdout is not a plan substitute.");
                }
            }
            else
            {
                lines.Add("### Stdout");
                lines.Add(WorkerContextHelpers.TrimArtifactBlock(verification.StandardOutput, PriorVerificationMaxChars));
            }

            if (!string.IsNullOrWhiteSpace(verification.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("### Stderr");
                lines.Add(WorkerContextHelpers.TrimArtifactBlock(verification.StandardError, PriorVerificationMaxChars));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyDictionary<TaskId, DurablePlannerPlanResolution> ResolveDurablePlannerPlans(
        IReadOnlyList<TaskSpec> goalTasks,
        TaskId taskId)
    {
        var resolutions = new Dictionary<TaskId, DurablePlannerPlanResolution>();
        foreach (var priorTask in goalTasks
            .TakeWhile(candidate => candidate.Id != taskId)
            .Where(candidate =>
                candidate.RequiredRole == AgentRole.Planner &&
                candidate.Status == WorkTaskStatus.Completed &&
                candidate.VerificationHistory.Count > 0))
        {
            var succeeded = TryResolveLatestDurablePlannerPlan(
                priorTask,
                out var plan,
                out var diagnostic);
            resolutions.Add(
                priorTask.Id,
                new DurablePlannerPlanResolution(succeeded, plan, diagnostic));
        }

        return resolutions;
    }

    private static bool TryResolveLatestDurablePlannerPlan(
        TaskSpec task,
        out string plan,
        out string diagnostic)
    {
        diagnostic = "no complete Planner artifact exists in verification history";
        foreach (var verification in task.VerificationHistory.Reverse())
        {
            if (TryResolveDurablePlannerPlan(verification, out plan, out var candidateDiagnostic))
            {
                return true;
            }

            diagnostic = candidateDiagnostic;
        }

        plan = string.Empty;
        return false;
    }

    private static DurableResearchResolution ResolveLatestDurableResearch(
        IReadOnlyList<TaskSpec> goalTasks,
        TaskId taskId)
    {
        foreach (var researchTask in goalTasks
                     .TakeWhile(candidate => candidate.Id != taskId)
                     .Where(candidate => candidate.RequiredRole == AgentRole.Researcher)
                     .Reverse())
        {
            foreach (var verification in researchTask.VerificationHistory.Reverse())
            {
                if (TryResolveDurableResearch(verification, out var research, out var diagnostic))
                {
                    return new DurableResearchResolution(true, research, diagnostic);
                }
            }
        }

        return new DurableResearchResolution(
            false,
            string.Empty,
            "no complete Researcher artifact exists before this task");
    }

    internal static bool TryResolveDurableResearch(
        TaskVerificationRecord verification,
        out string research,
        out string diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath) &&
            File.Exists(verification.StandardOutputPath))
        {
            var captured = ResearcherOutputContract.ReadCapturedOutputTail(verification.StandardOutputPath);
            if (ResearcherOutputContract.TryExtractDurableResearch(captured, out research, out diagnostic) &&
                ResearcherOutputContract.TryValidate(research, out research, out diagnostic))
            {
                return true;
            }
        }

        if (ResearcherOutputContract.TryExtractDurableResearch(
                verification.StandardOutput,
                out research,
                out diagnostic) &&
            ResearcherOutputContract.TryValidate(research, out research, out diagnostic))
        {
            return true;
        }

        research = string.Empty;
        return false;
    }

    internal static bool TryResolveDurablePlannerPlan(
        TaskVerificationRecord verification,
        out string plan,
        out string diagnostic)
    {
        plan = string.Empty;
        diagnostic = string.Empty;
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath) &&
            File.Exists(verification.StandardOutputPath))
        {
            var capturedOutput = PlannerOutputContract.ReadCapturedOutputTail(verification.StandardOutputPath);
            if (TryExtractAndRevalidateDurablePlannerPlan(
                    capturedOutput,
                    verification.WorkingDirectory,
                    out plan,
                    out diagnostic))
            {
                return true;
            }
        }

        if (TryExtractAndRevalidateDurablePlannerPlan(
                verification.StandardOutput,
                verification.WorkingDirectory,
                out plan,
                out var verificationDiagnostic))
        {
            return true;
        }

        diagnostic = string.IsNullOrWhiteSpace(diagnostic)
            ? verificationDiagnostic
            : $"{diagnostic}; verification snapshot: {verificationDiagnostic}";
        return false;
    }

    private static bool TryExtractAndRevalidateDurablePlannerPlan(
        string text,
        string workingDirectory,
        out string plan,
        out string diagnostic)
    {
        if (!PlannerOutputContract.TryExtractDurablePlan(text, out var extractedPlan, out diagnostic))
        {
            plan = string.Empty;
            return false;
        }

        if (PlannerOutputContract.TryValidatePlan(
                extractedPlan,
                workingDirectory,
                out plan,
                out diagnostic))
        {
            return true;
        }

        diagnostic = $"durable Planner plan failed retrieval revalidation: {diagnostic}";
        return false;
    }

    private sealed record DurablePlannerPlanResolution(
        bool Succeeded,
        string Plan,
        string Diagnostic);

    private sealed record DurableResearchResolution(
        bool Succeeded,
        string Research,
        string Diagnostic);

    private static List<string> CopyGuidanceFiles(string workingDirectory, string contextDirectory)
    {
        var copied = new List<string>();
        foreach (var fileName in new[] { "AGENTS.md" })
        {
            var sourcePath = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Combine(contextDirectory, fileName);
            WriteText(targetPath, WorkerContextHelpers.TrimArtifactBlock(File.ReadAllText(sourcePath), GuidanceFileMaxChars));
            copied.Add(fileName);
        }

        return copied;
    }

    internal static string BuildManifest(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string> guidanceFiles,
        IReadOnlyList<string>? preflightFindings,
        string? contextDirectory = null)
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
            "- diff-summary.md: compact git status, changed files, and diff stat for the workspace.",
            "- prior-task-summaries.md: compact prior task summaries with changed files, behavior changes, verification commands/results, risks, and model fit.",
            "- prior-task-evidence.md: prior completed task verification evidence with a larger file budget than inline prompts.",
            "- context-package.json: task-scoped package metadata and fallback guidance for this dispatch."
        };

        if (!string.IsNullOrWhiteSpace(contextDirectory) &&
            File.Exists(Path.Combine(contextDirectory, "research-notes.md")))
        {
            lines.Add("- research-notes.md: complete validated Researcher artifact; pinned and never summarized.");
        }

        if (!string.IsNullOrWhiteSpace(contextDirectory) &&
            File.Exists(Path.Combine(contextDirectory, "planner-plan.md")))
        {
            lines.Add("- planner-plan.md: complete validated Planner artifact; pinned and never summarized.");
        }

        if (string.IsNullOrWhiteSpace(contextDirectory) ||
            File.Exists(Path.Combine(contextDirectory, "source-survey.md")))
        {
            lines.Add("- source-survey.md: compact repository source inventory, directory counts, task-term file matches, public API symbols, likely tests, call-site hints, and ownership hints.");
        }

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
            "diff-summary.md",
            "prior-task-summaries.md",
            "prior-task-evidence.md",
            "manifest.md",
            "context-package.json"
        };

        foreach (var optionalArtifact in new[] { "research-notes.md", "planner-plan.md", "source-survey.md" })
        {
            if (File.Exists(Path.Combine(contextDirectory, optionalArtifact)))
            {
                artifactNames.Add(optionalArtifact);
            }
        }

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
            "research-notes.md" => "Complete validated Researcher artifact retained outside generic context trimming.",
            "planner-plan.md" => "Complete validated Planner artifact retained outside generic context trimming.",
            "source-survey.md" => "Compact repository source inventory, task-term matches, symbols, tests, call-site hints, and ownership hints.",
            "diff-summary.md" => "Compact git status, changed files, and diff stat.",
            "prior-task-summaries.md" => "Compact prior task outcomes and verification summaries.",
            "prior-task-evidence.md" => "Larger prior verification evidence for targeted inspection.",
            "manifest.md" => "Human-readable artifact descriptions and role priorities.",
            "context-package.json" => "Task-scoped context package metadata and missing-artifact fallback guidance.",
            "subscription-preflight.md" => "Subscription dispatch preflight findings.",
            "AGENTS.md" => "Repository-local agent instructions.",
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
            "research-notes.md" or "planner-plan.md" => "materialized byte-complete from the latest validated upstream durable receipt",
            "source-survey.md" => "generated from source files, objective, task text, and verification plan in the working directory at dispatch preparation",
            "diff-summary.md" => "generated from git status and diff commands in the working directory at dispatch preparation",
            "context-package.json" => "generated with the current task package at dispatch preparation",
            "subscription-preflight.md" => "generated from subscription preflight immediately before dispatch preparation",
            "AGENTS.md" => "copied from working directory at dispatch preparation",
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
            "research-notes.md" => ["Planner", "Developer", "Tester", "Reviewer"],
            "planner-plan.md" => ["Developer", "Tester", "Reviewer"],
            "AGENTS.md" => ["Planner", "Researcher", "Developer", "Tester", "Reviewer"],
            _ => ["Planner", "Researcher", "Developer", "Tester", "Reviewer"]
        };
    }

    private static string TrimDigestText(string value)
    {
        return WorkerContextHelpers.TrimArtifactBlock(value, DigestTextMaxChars).ReplaceLineEndings(" ");
    }

    private static string TrimDigestEvidence(string value)
    {
        return WorkerContextHelpers.TrimArtifactBlock(value, DigestEvidenceMaxChars).ReplaceLineEndings(" ");
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
                "- 2. research-notes.md: consume the complete validated Researcher artifact; do not repeat a broad source survey.",
                "- 3. context-budget.md: use retrieval handles before asking for other large evidence in prompts.",
                "- 4. objective.md: preserve the goal, scope, and acceptance path.",
                "- 5. digest.md: identify current role focus, blockers, and prior outcomes."
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
                "- 2. planner-plan.md and research-notes.md: use the complete validated upstream handoffs as implementation authority.",
                "- 3. context-budget.md: prefer artifact handles over copying other large evidence into prompts.",
                "- 4. workflow-brokers.md: use deterministic broker actions for test selection, diff summary, and acceptance evidence.",
                "- 5. selected-skills.md: read only the relevant selected skills before editing or testing.",
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

    private static readonly JsonSerializerOptions CriteriaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static void WriteAcceptanceCriteriaIfNonEmpty(string objective, string workingDirectory)
    {
        var criteria = AcceptanceCriteriaParser.Parse(objective);
        if (criteria.Count == 0)
            return;

        var dir = Path.Combine(workingDirectory, ".orchestrator");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "goal-acceptance-criteria.json");
        File.WriteAllText(path, JsonSerializer.Serialize(criteria, CriteriaJsonOptions));
    }

    private static void WriteText(string path, string content)
    {
        File.WriteAllText(path, content);
    }

    private static void WriteOptionalArtifact(string path, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

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
