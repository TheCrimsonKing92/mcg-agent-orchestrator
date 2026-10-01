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
        bool verificationSkipped,
        DotnetBuildStorageRoot? buildStorageRoot,
        string? executionDirectory = null)
    {
        var blockers = new List<GoalAcceptanceEvidenceBlocker>();
        var nextCommands = new List<string>();
        var acceptance = GoalAcceptanceStatusProjector.Build(kernel, goal, executionDirectory);
        var changedFiles = Array.Empty<string>();
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var diffStat = "not available";

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
            if (GitCli.IsWorktreeDirty(worktreePath))
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
            diffStat = GitCli.Run(worktreePath, "diff", "--stat", diffSpec).Output.Trim();
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

        var unbackedCompletedTaskCount = goal.Tasks
            .Count(task => task.Status == WorkTaskStatus.Completed && task.VerificationHistory.Count == 0);
        if (unbackedCompletedTaskCount > 0)
        {
            var completedTaskCount = goal.Tasks.Count(task => task.Status == WorkTaskStatus.Completed);
            AddBlocker(
                blockers,
                nextCommands,
                "provenance-check-failed",
                $"Provenance check failed: {unbackedCompletedTaskCount} of {completedTaskCount} completed task(s) have no verification receipt. Run 'provenance' to inspect.",
                "provenance");
        }

        if (goal.Status != GoalStatus.Verified)
        {
            AddBlocker(
                blockers,
                nextCommands,
                "goal-not-completed",
                $"Goal status is {goal.Status}; acceptance requires Verified.",
                $"monitor {goal.Id.Value[..8]}");
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

        var testImpactPlan = worktreePath is null
            ? RepositoryTestImpactPlanner.Plan(changeSummary)
            : RepositoryTestImpactPlanner.Plan(changeSummary, worktreePath);
        var verificationPolicy = VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer,
            goal.Objective,
            string.Join(Environment.NewLine, goal.Tasks.Select(task => task.Description)),
            string.Join(Environment.NewLine, goal.Tasks.Select(task => task.VerificationPlan)),
            changedFiles,
            testImpactPlan);
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
                        $"Acceptance verifier failed to auto-run required verification policy check '{missing.Name}'.",
                        $"rerun acceptance {goal.Id.Value[..8]} and inspect verifier output");
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
            testImpactPlan,
            verificationPolicy,
            policyChecks,
            BuildEnvironmentEvidence(goal.Id, buildStorageRoot),
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

    internal static string ResolvePolicyCheckState(
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
        acceptance ??= FindEquivalentAggregateTestCheck(check, acceptanceChecks);
        if (acceptance is null)
        {
            return "missing";
        }

        return acceptance.Passed ? "passed" : "failed";
    }

    private static AcceptanceCheckResult? FindEquivalentAggregateTestCheck(
        VerificationPolicyCheck check,
        IReadOnlyList<AcceptanceCheckResult> acceptanceChecks)
    {
        if (!check.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (check.CommandLine.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase))
        {
            string[] aggregateNames =
            [
                "core tests",
                "infrastructure tests",
                "provider environment tests",
                "cli tests"
            ];
            var aggregateChecks = aggregateNames
                .Select(name => acceptanceChecks.FirstOrDefault(result =>
                    !result.Advisory &&
                    result.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (aggregateChecks.Any(result => result is null))
            {
                return null;
            }

            var failed = aggregateChecks.FirstOrDefault(result => !result!.Passed);
            return failed ?? new AcceptanceCheckResult(
                check.Name,
                Passed: true,
                ExitCode: 0,
                OutputTail: null,
                ResultSummary: "covered by all project test checks");
        }

        var aggregateName = check.CommandLine.Contains(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            StringComparison.OrdinalIgnoreCase)
            ? "infrastructure tests"
            : check.CommandLine.Contains(
                "Mcg.AgentOrchestrator.Core.Tests.csproj",
                StringComparison.OrdinalIgnoreCase)
                ? "core tests"
                : check.CommandLine.Contains(
                    "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                    StringComparison.OrdinalIgnoreCase)
                    ? "provider environment tests"
                    : check.CommandLine.Contains(
                        "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj",
                        StringComparison.OrdinalIgnoreCase)
                        ? "cli tests"
                        : null;
        return aggregateName is null
            ? null
            : acceptanceChecks.FirstOrDefault(result =>
                !result.Advisory &&
                result.Name.Equals(aggregateName, StringComparison.OrdinalIgnoreCase));
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

    private static GoalAcceptanceBuildEnvironmentEvidence BuildEnvironmentEvidence(
        GoalId goalId, DotnetBuildStorageRoot? buildStorageRoot)
    {
        var root = DotnetBuildEnvironmentManager.GoalRoot(goalId, buildStorageRoot);
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
        ParseChangedFilesResult(
            GitCli.Run(workingDirectory, "diff", "--name-only", BuildDiffSpec(workingDirectory)));

    internal static string[] ParseChangedFilesResult(GitCli.GitResult result)
    {
        if (!result.ProcessStarted || result.ExitCode != 0 || result.DrainTimedOut)
        {
            var detail = BoundGitDiagnostic(
                string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
            throw new InvalidOperationException(
                "git changed-file discovery failed: " +
                $"process-started={result.ProcessStarted.ToString().ToLowerInvariant()}; " +
                $"exit-code={result.ExitCode}; " +
                $"drain-timed-out={result.DrainTimedOut.ToString().ToLowerInvariant()}; " +
                $"detail={detail}");
        }

        return SplitLines(result.Output.Trim());
    }

    private static string BoundGitDiagnostic(string value)
    {
        const int maxChars = 800;
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "none"
            : string.Join(' ', SplitLines(value));
        return normalized.Length <= maxChars
            ? normalized
            : normalized[..maxChars] + "...(truncated)";
    }

    // Bounded unified diff of the goal branch against its base, for feeding an advisory semantic
    // judge. Truncated so a large change cannot blow the judge's context/cost budget.
    public static string GetDiffExcerpt(string workingDirectory, int maxChars = 6000)
    {
        var diff = GitCli.Run(workingDirectory, "diff", BuildDiffSpec(workingDirectory)).Output.Trim();
        return diff.Length <= maxChars
            ? diff
            : diff[..maxChars] + $"{Environment.NewLine}...(diff truncated at {maxChars} chars)";
    }

    // Per-file diffs for the recursive judge: each changed file gets its own bounded diff so no
    // file's changes are hidden behind the whole-diff truncation boundary.
    public static IReadOnlyList<(string File, string Diff)> GetPerFileDiffs(
        string workingDirectory,
        int perFileMaxChars = 4000)
    {
        var diffSpec = BuildDiffSpec(workingDirectory);
        var result = new List<(string File, string Diff)>();
        foreach (var file in GetChangedFiles(workingDirectory))
        {
            var diff = GitCli.Run(workingDirectory, "diff", diffSpec, "--", file).Output.Trim();
            if (string.IsNullOrWhiteSpace(diff))
            {
                continue;
            }

            var truncated = diff.Length <= perFileMaxChars
                ? diff
                : diff[..perFileMaxChars] + $"{Environment.NewLine}...(per-file diff truncated at {perFileMaxChars} chars)";
            result.Add((file, truncated));
        }

        return result;
    }

    private static string BuildDiffSpec(string workingDirectory)
    {
        if (RevisionExists(workingDirectory, "main"))
        {
            return "main...HEAD";
        }

        if (RevisionExists(workingDirectory, "master"))
        {
            return "master...HEAD";
        }

        return RevisionExists(workingDirectory, "HEAD~1")
            ? "HEAD~1...HEAD"
            : "HEAD";
    }

    private static bool RevisionExists(string workingDirectory, string revision)
    {
        var result = GitCli.Run(workingDirectory, "rev-parse", "--verify", "--quiet", revision);
        if (result.ProcessStarted && result.ExitCode == 0 && !result.DrainTimedOut)
        {
            return true;
        }

        if (result.ProcessStarted &&
            result.ExitCode == 1 &&
            !result.DrainTimedOut &&
            string.IsNullOrWhiteSpace(result.Output) &&
            string.IsNullOrWhiteSpace(result.Error))
        {
            return false;
        }

        var detail = BoundGitDiagnostic(
            string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        throw new InvalidOperationException(
            "git base-ref discovery failed: " +
            $"ref={revision}; " +
            $"process-started={result.ProcessStarted.ToString().ToLowerInvariant()}; " +
            $"exit-code={result.ExitCode}; " +
            $"drain-timed-out={result.DrainTimedOut.ToString().ToLowerInvariant()}; " +
            $"detail={detail}");
    }
}
