using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalAcceptanceEvidenceBundle(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    string? WorktreePath,
    bool Passed,
    bool VerificationSkipped,
    IReadOnlyList<string> ChangedFiles,
    string DiffStat,
    RepositoryChangeSummary ChangeSummary,
    RepositoryTestImpactPlan TestImpactPlan,
    VerificationPolicy VerificationPolicy,
    IReadOnlyList<GoalAcceptancePolicyCheckEvidence> PolicyChecks,
    GoalAcceptanceBuildEnvironmentEvidence BuildEnvironment,
    IReadOnlyList<AcceptanceCheckResult> AcceptanceChecks,
    IReadOnlyList<GoalAcceptanceTaskEvidence> TaskEvidence,
    IReadOnlyList<GoalAcceptanceEvidenceBlocker> Blockers,
    IReadOnlyList<string> NextCommands);

internal sealed record GoalAcceptanceBuildEnvironmentEvidence(
    string LeaseId,
    string RootPath,
    string LeaseMetadataPath,
    bool RootExists,
    bool LeaseMetadataExists);

internal sealed record GoalAcceptancePolicyCheckEvidence(
    string Name,
    string Kind,
    bool Required,
    string State,
    string CommandLine,
    string Reason);

internal sealed record GoalAcceptanceTaskEvidence(
    int TaskNumber,
    AgentRole Role,
    WorkTaskStatus Status,
    string VerificationCommand,
    int VerificationExitCode,
    bool HasWorkerResultContract,
    string? ModelFitNote);

internal sealed record GoalAcceptanceEvidenceBlocker(
    string Kind,
    string Message,
    string SuggestedCommand);

internal static class GoalAcceptanceEvidenceBundleBuilder
{
    public static GoalAcceptanceEvidenceBundle Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string? worktreePath,
        AcceptanceVerificationResult? verification,
        bool verificationSkipped)
    {
        var blockers = new List<GoalAcceptanceEvidenceBlocker>();
        var nextCommands = new List<string>();
        var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
        var changedFiles = Array.Empty<string>();
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var diffStat = "not available";
        var dirtyOutput = string.Empty;

        if (worktreePath is null)
        {
            AddBlocker(
                blockers,
                nextCommands,
                "workspace-missing",
                "Goal has no isolated workspace to inspect before acceptance.",
                $"workspace create {goal.Id.Value[..8]}");
        }
        else
        {
            dirtyOutput = RunGit(worktreePath, "status", "--porcelain");
            if (!string.IsNullOrWhiteSpace(dirtyOutput))
            {
                AddBlocker(
                    blockers,
                    nextCommands,
                    "dirty-worktree",
                    "Worktree has uncommitted changes; commit, revert, or park them before merge.",
                    $"goal-recovery {goal.Id.Value[..8]}");
            }

            var diffSpec = BuildDiffSpec(worktreePath);
            changedFiles = GetChangedFiles(worktreePath);
            changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
            diffStat = RunGit(worktreePath, "diff", "--stat", diffSpec);
            if (string.IsNullOrWhiteSpace(diffStat))
            {
                diffStat = changedFiles.Length == 0 ? "no branch diff" : "diff stat unavailable";
            }

            var generated = changeSummary.Files
                .Where(file => file.IsGeneratedArtifact)
                .Select(file => file.Path)
                .ToArray();
            if (generated.Length > 0)
            {
                AddBlocker(
                    blockers,
                    nextCommands,
                    "generated-artifacts",
                    $"Generated or transient paths are changed: {string.Join(", ", generated)}.",
                    "remove generated artifacts, then rerun acceptance");
            }
        }

        if (goal.Status != GoalStatus.Completed)
        {
            AddBlocker(
                blockers,
                nextCommands,
                "goal-not-completed",
                $"Goal status is {goal.Status}; acceptance requires Completed.",
                $"monitor {goal.Id.Value[..8]}");
        }
        else
        {
            var provenance = ProvenanceReport.Build([goal], string.Empty);
            if (provenance.UnbackedGoalCount > 0)
            {
                var record = provenance.Goals[0];
                AddBlocker(
                    blockers,
                    nextCommands,
                    "provenance-check-failed",
                    $"Provenance check failed: {record.UnbackedTaskCount} of {record.CompletedTaskCount} completed task(s) have no verification receipt. Run 'provenance' to inspect.",
                    "provenance");
            }
        }

        foreach (var summaryBlocker in acceptance.Blockers)
        {
            AddBlocker(
                blockers,
                nextCommands,
                summaryBlocker.Kind.ToString(),
                summaryBlocker.Message,
                summaryBlocker.SuggestedAction);
        }

        var taskEvidence = goal.Tasks
            .Select((task, index) => BuildTaskEvidence(task, index + 1))
            .ToArray();

        var verificationPolicy = VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer,
            goal.Objective,
            string.Join(Environment.NewLine, goal.Tasks.Select(task => task.Description)),
            string.Join(Environment.NewLine, goal.Tasks.Select(task => task.VerificationPlan)),
            changedFiles);
        var policyChecks = BuildPolicyCheckEvidence(verificationPolicy, verification);

        if (!verificationSkipped)
        {
            if (verification is null)
            {
                AddBlocker(
                    blockers,
                    nextCommands,
                    "acceptance-verification-missing",
                    "Acceptance verifier did not produce a result.",
                    $"acceptance {goal.Id.Value[..8]}");
            }
            else
            {
                if (!verification.Passed)
                {
                    AddBlocker(
                        blockers,
                        nextCommands,
                        "acceptance-verification-failed",
                        $"Acceptance verification failed with exit {verification.ExitCode}.",
                        $"acceptance {goal.Id.Value[..8]}");
                }

                if ((verification.Checks?.Count ?? 0) == 0)
                {
                    AddBlocker(
                        blockers,
                        nextCommands,
                        "acceptance-checks-missing",
                        "Acceptance verification produced no check records.",
                        "add an acceptance manifest check or rerun acceptance with the default verifier");
                }

                foreach (var missing in policyChecks.Where(check =>
                    check.Required &&
                    check.State.Equals("missing", StringComparison.OrdinalIgnoreCase) &&
                    !IsManualPolicyKind(check.Kind)))
                {
                    AddBlocker(
                        blockers,
                        nextCommands,
                        "acceptance-policy-check-missing",
                        $"Required verification policy check '{missing.Name}' was not run by acceptance.",
                        "add an acceptance manifest check or rerun acceptance with generated policy checks");
                }
            }
        }

        return new GoalAcceptanceEvidenceBundle(
            goal.Id,
            goal.Objective,
            goal.Status,
            worktreePath,
            blockers.Count == 0,
            verificationSkipped,
            changedFiles,
            diffStat.Trim(),
            changeSummary,
            RepositoryTestImpactPlanner.Plan(changeSummary),
            verificationPolicy,
            policyChecks,
            BuildEnvironmentEvidence(goal.Id),
            verification?.Checks?.ToArray() ?? [],
            taskEvidence,
            blockers,
            nextCommands.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static GoalAcceptancePolicyCheckEvidence[] BuildPolicyCheckEvidence(
        VerificationPolicy policy,
        AcceptanceVerificationResult? verification)
    {
        var acceptanceChecks = verification?.Checks ?? [];
        return policy.Checks.Select(check =>
        {
            var state = ResolvePolicyCheckState(check, acceptanceChecks);
            return new GoalAcceptancePolicyCheckEvidence(
                check.Name,
                check.Kind,
                check.Required,
                state,
                check.CommandLine,
                check.Reason);
        }).ToArray();
    }

    private static string ResolvePolicyCheckState(
        VerificationPolicyCheck check,
        IReadOnlyList<AcceptanceCheckResult> acceptanceChecks)
    {
        if (check.Kind.Equals("no-op", StringComparison.OrdinalIgnoreCase))
        {
            return "no-op";
        }

        if (IsManualPolicyKind(check.Kind))
        {
            return "manual";
        }

        var acceptance = acceptanceChecks.FirstOrDefault(result =>
            result.Name.Equals(check.Name, StringComparison.OrdinalIgnoreCase));
        if (acceptance is null)
        {
            return "missing";
        }

        return acceptance.Passed ? "passed" : "failed";
    }

    private static bool IsManualPolicyKind(string kind) =>
        kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase);

    private static GoalAcceptanceTaskEvidence BuildTaskEvidence(TaskSpec task, int taskNumber)
    {
        var verification = task.LastVerification;
        return new GoalAcceptanceTaskEvidence(
            taskNumber,
            task.RequiredRole,
            task.Status,
            verification?.Command ?? "(missing)",
            verification?.ExitCode ?? -1,
            verification is not null && HasWorkerResultContract(verification),
            verification?.ModelFitNote);
    }

    private static GoalAcceptanceBuildEnvironmentEvidence BuildEnvironmentEvidence(GoalId goalId)
    {
        var root = DotnetBuildEnvironmentManager.GoalRoot(goalId);
        var metadata = Path.Combine(root, "lease", "lease.json");
        return new GoalAcceptanceBuildEnvironmentEvidence(
            $"goal-{goalId.Value[..8].ToLowerInvariant()}",
            root,
            metadata,
            Directory.Exists(root),
            File.Exists(metadata));
    }

    private static bool HasWorkerResultContract(TaskVerificationRecord verification)
    {
        var text = $"{verification.StandardOutput}\n{verification.StandardError}";
        return text.Contains("WORKER_RESULT:", StringComparison.Ordinal) &&
            text.Contains("END_WORKER_RESULT", StringComparison.Ordinal);
    }

    private static void AddBlocker(
        List<GoalAcceptanceEvidenceBlocker> blockers,
        List<string> nextCommands,
        string kind,
        string message,
        string suggestedCommand)
    {
        blockers.Add(new GoalAcceptanceEvidenceBlocker(kind, message, suggestedCommand));
        if (!string.IsNullOrWhiteSpace(suggestedCommand))
        {
            nextCommands.Add(suggestedCommand);
        }
    }

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string[] GetChangedFiles(string workingDirectory) =>
        SplitLines(RunGit(workingDirectory, "diff", "--name-only", BuildDiffSpec(workingDirectory)));

    // Bounded unified diff of the goal branch against its base, for feeding an advisory semantic
    // judge. Truncated so a large change cannot blow the judge's context/cost budget.
    public static string GetDiffExcerpt(string workingDirectory, int maxChars = 6000)
    {
        var diff = RunGit(workingDirectory, "diff", BuildDiffSpec(workingDirectory));
        return diff.Length <= maxChars
            ? diff
            : diff[..maxChars] + $"{Environment.NewLine}...(diff truncated at {maxChars} chars)";
    }

    private static string BuildDiffSpec(string workingDirectory)
    {
        if (GitSucceeds(workingDirectory, "rev-parse", "--verify", "main"))
        {
            return "main...HEAD";
        }

        if (GitSucceeds(workingDirectory, "rev-parse", "--verify", "master"))
        {
            return "master...HEAD";
        }

        return GitSucceeds(workingDirectory, "rev-parse", "--verify", "HEAD~1")
            ? "HEAD~1...HEAD"
            : "HEAD";
    }

    private static bool GitSucceeds(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
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
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        return process.WaitForExit(10000) && process.ExitCode == 0;
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
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
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);

        return process.ExitCode == 0
            ? output.Trim()
            : error.Trim();
    }
}
