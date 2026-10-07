using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool RunAcceptanceWorkspaceMerge(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    var owner = $"acceptance:{Environment.ProcessId}:{Guid.NewGuid():N}";
    var lease = new ReconcileSweepRemediationStore(context.Workspace.SqliteStatePath)
        .TryAcquireAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30));
    if (lease is null)
    {
        Console.WriteLine($"BLOCKER step=acceptance-claim goal={goal.Id.Value[..8]} reason=already-running action=\"Wait for the active acceptance operation to finish.\"");
        return false;
    }

    using (lease)
    {
        return RunAcceptanceWorkspaceMergeCore(context, skipVerify);
    }
}

internal static bool RunAcceptanceWorkspaceMergeCore(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (TryReconcileLandedCleanedAcceptance(context, goal, "acceptance retry", out var reconciledDetail))
    {
        Console.WriteLine(reconciledDetail);
        context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=passed detail=already-landed-cleaned");
        return true;
    }

    if (TryNormalizePrematureCompletedGoalForAcceptance(context, goal, out var normalizedGoal, out var normalizedDetail))
    {
        Console.WriteLine(normalizedDetail);
        goal = normalizedGoal;
        context.CurrentGoal = normalizedGoal;
    }

    if (goal.Status == GoalStatus.AcceptanceFailed)
    {
        var failedWorktreePath = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
        var failedBranchHead = TryResolveGitHead(context, failedWorktreePath);
        var failedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
        if (ClearSupersededAcceptanceFailureForCandidate(
            context,
            goal,
            failedBranchHead,
            failedMainHead))
        {
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            Console.WriteLine(
                $"Acceptance history: superseded failure is historical for candidate " +
                $"{FormatAcceptanceCandidate(failedBranchHead, failedMainHead)}.");
        }
    }

    if (goal.Status != GoalStatus.Verified)
    {
        return false;
    }

    var engineHealth = PostLandingCanaryFactory.CreateCircuit(context.Workspace).Read();
    var engineDecision = AcceptanceEngineAcceptanceGate.Decide(
        engineHealth.Health,
        AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
    if (!engineDecision.Allowed)
    {
        Console.WriteLine(
            $"BLOCKER step=acceptance-engine reason=\"{engineDecision.Reason}\" " +
            $"landing={engineHealth.LandingSha ?? "unknown"} failure={engineHealth.FailureReason ?? "pending"} " +
            "action=\"Repair the acceptance engine, then run acceptance-engine clear <note>.\"");
        return false;
    }

    var worktreePath = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
    AcceptanceVerificationResult? verification = null;
    IReadOnlyList<string> landingChangedFiles = [];
    string? testedWorktreeHead = null;
    DateTimeOffset? acceptanceAttemptStartedAt = null;
    AcceptanceMergeGuardSnapshot? expectedMergeGuard = null;
    var testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
    if (worktreePath is not null)
    {
        var rebaseCheckStarted = System.Diagnostics.Stopwatch.StartNew();
        var needsRebase = context.Worktrees.NeedsRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
        rebaseCheckStarted.Stop();
        context.PhaseTimings.Record(
            "workspace-rebase-check",
            rebaseCheckStarted.Elapsed,
            ("goal", goal.Id.Value[..8]),
            ("needed", needsRebase));

        if (needsRebase)
        {
            var rebaseStarted = System.Diagnostics.Stopwatch.StartNew();
            var rebase = context.Worktrees.TryRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
            rebaseStarted.Stop();
            context.PhaseTimings.Record(
                "workspace-rebase",
                rebaseStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("status", rebase.Status),
                ("updated", rebase.UpdatedBranch));
            Console.WriteLine($"Workspace rebase: {FormatWorkspaceRebase(rebase)}");

            if (!rebase.UpdatedBranch)
            {
                var failedChecks = new[] { $"workspace rebase: {rebase.Status.ToString().ToLowerInvariant()}" };
                testedWorktreeHead = TryResolveGitHead(context, worktreePath);
                testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
                context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
                GoalOperationJournal.AcceptanceFailed(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", failedChecks)}.");
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
                AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=rebase checks={FormatConductEventChecks(failedChecks)}");
                Console.WriteLine("Acceptance evidence: blocked; merge blocked");
                return false;
            }
        }
        else
        {
            context.PhaseTimings.Record(
                "workspace-rebase",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "already-up-to-date"));
        }

        testedWorktreeHead = context.Worktrees.ResolveHead(worktreePath);
        testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
        if (ClearSupersededAcceptanceFailureForCandidate(context, goal, testedWorktreeHead, testedMainHead))
        {
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            Console.WriteLine($"Acceptance history: superseded failure is historical for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}.");
        }

        var proposedMergeGuard = AcceptanceMergeGuard.Capture(context.Kernel, goal.Id);
        var preflightGuard = context.PrepareAcceptanceMergeGuard(new AcceptanceMergeGuardPreflightRequest(
            goal.Id,
            proposedMergeGuard,
            testedWorktreeHead,
            testedMainHead));
        expectedMergeGuard = preflightGuard.CurrentGuard;
        if (preflightGuard.GuardAbort is { } preflightMismatch)
        {
            var preflightAbort = new AcceptanceMergeCommitResult(
                false,
                AcceptanceMergeGuard.BuildAbortMessage(goal.Id, preflightMismatch, passingGateReceiptRecorded: false),
                preflightMismatch);
            RecordAcceptanceGuardAbort(
                context,
                goal,
                preflightAbort,
                testedWorktreeHead,
                testedMainHead,
                acceptanceAttemptStartedAt,
                verification: null,
                stage: "preflight-state-guard");
            return false;
        }

        var changedFiles = context.Worktrees.GetChangedFiles(worktreePath);
        landingChangedFiles = changedFiles;
        acceptanceAttemptStartedAt = DateTimeOffset.UtcNow;
        if (skipVerify)
        {
            context.PhaseTimings.Record(
                "verification-suite",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "--skip-verify"));
            Console.WriteLine("Verification: skipped (--skip-verify)");
        }
        else
        {
            var sourceSizePreflight = SourceSizeRatchetPreflight.Evaluate(worktreePath);
            if (sourceSizePreflight.HasBlockingViolation)
            {
                IReadOnlyList<string> failedChecks =
                [
                    SourceSizeRatchetPreflight.CheckName,
                    .. SourceSizeRatchetPreflight.BlockingViolationMessages(sourceSizePreflight)
                ];
                context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
                GoalOperationJournal.AcceptanceFailed(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {sourceSizePreflight.Message}.");
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
                AppendConductEvent(
                    context,
                    "acceptance",
                    goal.Id,
                    $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=source-size-preflight checks={FormatConductEventChecks(failedChecks)}");
                Console.WriteLine($"Acceptance verification: {sourceSizePreflight.Message}");
                Console.WriteLine("Acceptance evidence: blocked; merge blocked");
                return false;
            }

            var verificationStarted = System.Diagnostics.Stopwatch.StartNew();
            int? stableSlotIndex = null;
            DotnetBuildEnvironmentLease? stableSlotLease = null;
            try
            {
                var onSlotWait = (DotnetBuildStableSlotWait wait) =>
                    Console.WriteLine($"waiting for build-{wait.SlotIndex} permit held by pid {wait.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
                stableSlotLease = context.StableSlotSelector is { } stableSlotSelector
                    ? stableSlotSelector(context.StableSlotAcquisitionTimeout, onSlotWait)
                    : SelectGoalBuildPermit(
                        goal.Id,
                        context.StableSlotAcquisitionTimeout,
                        onSlotWait,
                        context.CleanupHooks.BuildStorageRoot,
                        context.Workspace.ExecutionDirectory);
                if (context.CleanupHooks.BuildStorageRoot is { } configuredRoot &&
                    (!configuredRoot.ContainsPath(stableSlotLease.Environment.RootPath) ||
                     !configuredRoot.ContainsPath(stableSlotLease.Environment.ArtifactsPath) ||
                     !configuredRoot.ContainsPath(stableSlotLease.Environment.ExecutionLockPath)))
                {
                    stableSlotLease.Dispose();
                    stableSlotLease = null;
                    throw new InvalidOperationException("Acceptance build lease is outside the configured build storage namespace.");
                }
                stableSlotIndex = stableSlotLease.Environment.BuildPermitIndex ??
                    ParseStableSlotIndex(stableSlotLease.Environment.SlotOwnerToken)
                    ?? throw new IOException($"Build permit did not identify its pool index: {stableSlotLease.Environment.SlotOwnerToken}");
            }
            catch (DotnetBuildSlotsBusyException ex)
            {
                stableSlotLease?.Dispose();
                Console.WriteLine($"SLOTS_BUSY goal={goal.Id.Value[..8]} wantedBy={ex.SlotsBusy.WantedBy} busySlots={FormatBusySlots(ex.SlotsBusy.BusySlots)}");
                Console.WriteLine("Acceptance verification: stable dotnet build slots busy; goal remains ready and will retry on a later conduct tick.");
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "slot-unavailable",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:slot-unavailable for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}.",
                    acceptanceAttemptStartedAt);
                return false;
            }
            catch (IOException ex)
            {
                stableSlotLease?.Dispose();
                Console.WriteLine($"BLOCKER step=verification reason=build-slot-timeout detail=\"{EscapeBlockerDetail(ex.Message)}\" action=\"Wait for a stable dotnet build slot to clear, then rerun acceptance.\"");
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "slot-unavailable",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:slot-unavailable for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {ex.Message}",
                    acceptanceAttemptStartedAt);
                throw new InvalidOperationException("acceptance blocked waiting for a stable dotnet build slot.", ex);
            }

            using (stableSlotLease)
            {
                var enteredVerifying = context.Kernel.BeginGoalAcceptanceVerification(
                    goal.Id,
                    "Acceptance entered Verifying while the terminal gate runs.");
                try
                {
                    var executionOptions = new AcceptanceRunExecutionOptions(
                        ProgressSink: progress => AppendConductEvent(
                            context,
                            "gate-progress",
                            goal.Id,
                            FormatGateProgressConductEvent(progress)),
                        RunId: Environment.GetEnvironmentVariable(
                            AcceptanceAttemptArtifactCustody.AttemptIdVariable),
                        ResultsPrefix: Environment.GetEnvironmentVariable(
                            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable),
                        LivenessCheckHint: Environment.GetEnvironmentVariable(
                            AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable));
                    var executionOwner = AcceptanceExecutionOwners.CreateAttempt(
                        worktreePath,
                        goal.Id,
                        stableSlotIndex,
                        CancellationToken.None,
                        executionOptions);
                    verification = AcceptanceExecutionOwnerLifetime.Run(
                        executionOwner,
                        () => context.AcceptanceVerifier.RunOwnedAsync(
                            worktreePath,
                            goal.Id,
                            changedFiles,
                            stableSlotIndex,
                            stableSlotLease,
                            executionOwner).GetAwaiter().GetResult());
                }
                catch (AcceptanceInfrastructureDeferredException ex)
                {
                    var deferredLine =
                        $"ACCEPTANCE_INFRASTRUCTURE_DEFERRED goal={goal.Id.Value[..8]} " +
                        $"reason={ex.ReasonCode} detail=\"{EscapeBlockerDetail(ex.Message)}\"";
                    Console.WriteLine(deferredLine);
                    AppendConductEvent(context, "infrastructure-deferral", goal.Id, deferredLine);
                    Console.WriteLine("Acceptance verification: trusted baseline infrastructure unavailable; goal remains ready and will retry on a later conduct tick.");
                    GoalOperationJournal.AcceptanceBlocked(
                        context.Workspace.ExecutionDirectory,
                        goal,
                        "acceptance",
                        $"INFRASTRUCTURE_DEFERRED:{ex.ReasonCode}",
                        testedWorktreeHead,
                        testedMainHead,
                        $"Acceptance blocked:INFRASTRUCTURE_DEFERRED:{ex.ReasonCode} for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {ex.Message}",
                        acceptanceAttemptStartedAt);
                    return false;
                }
                catch (BuildLockBlockedException ex)
                {
                    var blockedLine = $"BUILD_LOCK_BLOCKED goal={goal.Id.Value[..8]} {FormatBuildLockBlocked(ex.Attribution)}";
                    Console.WriteLine(blockedLine);
                    AppendConductEvent(context, "lock-blocker", goal.Id, blockedLine);
                    Console.WriteLine("Acceptance verification: build artifact lock blocked progress; goal remains ready and will retry on a later conduct tick.");
                    GoalOperationJournal.AcceptanceBlocked(
                        context.Workspace.ExecutionDirectory,
                        goal,
                        "acceptance",
                        "BUILD_LOCK_BLOCKED",
                        testedWorktreeHead,
                        testedMainHead,
                        $"Acceptance blocked:BUILD_LOCK_BLOCKED for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {FormatBuildLockBlocked(ex.Attribution)}",
                        acceptanceAttemptStartedAt);
                    return false;
                }
                finally
                {
                    if (enteredVerifying && context.Kernel.GetGoal(goal.Id).Status == GoalStatus.Verifying)
                    {
                        context.Kernel.ReconcileGoalAcceptanceVerified(
                            goal.Id,
                            "Acceptance terminal gate finished; returned to Verified for deterministic landing.");
                    }
                }
            }
            verificationStarted.Stop();
            context.PhaseTimings.Record(
                "verification-suite",
                verificationStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("slot", stableSlotIndex.HasValue ? $"slot-{stableSlotIndex.Value}" : null),
                ("passed", verification.Passed),
                ("exit", verification.ExitCode),
                ("checks", verification.Checks?.Count ?? 0));
            foreach (var check in verification.Checks ?? [])
            {
                context.PhaseTimings.Record(
                    "verification-check",
                    TimeSpan.FromMilliseconds(check.DurationMilliseconds ?? 0),
                    ("goal", goal.Id.Value[..8]),
                    ("name", check.Name),
                    ("passed", check.Passed),
                    ("exit", check.ExitCode),
                    ("advisory", check.Advisory));
                Console.WriteLine($"Verification check: {(check.Passed ? "passed" : "failed")} - {check.Name}" +
                    (check.ExitCode is null ? "" : $" (exit {check.ExitCode})") +
                    (string.IsNullOrWhiteSpace(check.ArtifactsPath) ? "" : $" artifacts={check.ArtifactsPath}"));
            }
        }
    }
    else
    {
        context.PhaseTimings.Record(
            "workspace-rebase",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
        context.PhaseTimings.Record(
            "verification-suite",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
        expectedMergeGuard = AcceptanceMergeGuard.Capture(context.Kernel, goal.Id);
    }

    if (verification is { Passed: true })
    {
        context.Kernel.ClearAcceptanceFailure(goal.Id);
        GoalOperationJournal.AcceptanceGatePassed(
            context.Workspace.ExecutionDirectory,
            goal,
            "acceptance",
            testedWorktreeHead,
            testedMainHead,
            $"Acceptance gate passed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}; merge pending.",
            acceptanceAttemptStartedAt,
            GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
    }
    else if (skipVerify)
    {
        context.Kernel.ClearAcceptanceFailure(goal.Id);
    }

    var evidence = context.Worktrees.BuildAcceptanceEvidence(
        context.Kernel,
        goal,
        worktreePath,
        verification,
        skipVerify,
        context.Workspace.ExecutionDirectory);
    ConsoleViews.PrintAcceptanceEvidenceBundle(evidence);

    if (!evidence.Passed)
    {
        if (verification is { Passed: false })
        {
            Console.WriteLine($"Verification: failed (exit {verification.ExitCode}); merge blocked");
            Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
            var timeoutBlocker = TryBuildVerificationTimeoutBlocker(verification);
            if (timeoutBlocker is not null)
            {
                Console.WriteLine(timeoutBlocker);
            }

            if (!string.IsNullOrWhiteSpace(verification.OutputTail))
            {
                Console.WriteLine(verification.OutputTail);
            }

            if (timeoutBlocker is not null)
            {
                var timedOutChecks = verification.Checks?
                    .Where(IsBlockingTimeoutCheck)
                    .Select(c => c.Name)
                    .ToList() ?? ["acceptance-check-timeout"];
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "timeout",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:timeout for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", timedOutChecks)}.",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, timedOutChecks);
                AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=blocked type=timeout checks={FormatConductEventChecks(timedOutChecks)}");
                return false;
            }
        }
        else
        {
            Console.WriteLine("Acceptance evidence: blocked; merge blocked");
        }

        var failedChecks = verification?.Checks?
            .Where(c => !c.Passed && !c.Advisory)
            .Select(c => c.Name)
            .ToList();
        if (failedChecks is null or { Count: 0 })
        {
            failedChecks = ["acceptance evidence blocked"];
        }
        context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
        GoalOperationJournal.AcceptanceFailed(
            context.Workspace.ExecutionDirectory,
            goal,
            "acceptance",
            testedWorktreeHead,
            testedMainHead,
            $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", failedChecks)}.",
            acceptanceAttemptStartedAt,
            GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed checks={FormatConductEventChecks(failedChecks)}");
        return false;
    }

    if (verification is not null)
    {
        Console.WriteLine($"Verification: passed (exit {verification.ExitCode})");
        Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
    }

    var hostStop = context.StopAcceptanceHosts(new AcceptanceHostStopRequest(
        goal.Id,
        worktreePath,
        TimeSpan.FromSeconds(30)));
    Console.WriteLine(hostStop.Message);
    if (!hostStop.Succeeded)
    {
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["stop-host"], testedWorktreeHead, testedMainHead);
        GoalOperationJournal.AcceptanceFailed(
            context.Workspace.ExecutionDirectory,
            goal,
            "acceptance",
            testedWorktreeHead,
            testedMainHead,
            $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: stop-host.",
            acceptanceAttemptStartedAt,
            GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["stop-host"]);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed checks=stop-host");
        return false;
    }

    var preMergeObligations = context.Kernel.GetGoal(goal.Id)
        .GetOutstandingCriterionEvidenceObligations(testedWorktreeHead);
    if (preMergeObligations.Count > 0)
    {
        Console.WriteLine(
            "Acceptance evidence: merge blocked by outstanding criterion evidence: " +
            string.Join(", ", preMergeObligations.Select(item => $"{item.DisplayLabel}:{item.Owner}:{item.State}")));
        return false;
    }

    var mergeStarted = System.Diagnostics.Stopwatch.StartNew();
    var mergeCommit = context.FinalizeAcceptanceMerge(new AcceptanceMergeCommitRequest(
            goal.Id,
            expectedMergeGuard ?? AcceptanceMergeGuard.Capture(context.Kernel, goal.Id),
            testedWorktreeHead,
            PassingGateReceiptRecorded: verification is { Passed: true },
            Merge: () =>
            {
                var pendingRollback = GoalRollbackPlanner.CapturePendingAcceptance(context.Workspace.ExecutionDirectory, goal.Id);
                var merge = context.Worktrees.TryFastForwardMerge(
                    context.Workspace.ExecutionDirectory,
                    goal.Id,
                    () => PostLandingCanaryFactory.BuildMutationBlockReason(context.Workspace));

                if (merge is null)
                {
                    return new AcceptanceMergeCommitResult(true, null);
                }

                if (merge.FastForwarded && pendingRollback is not null)
                {
                    GoalRollbackPlanner.RecordAcceptance(context.Workspace.ExecutionDirectory, pendingRollback);
                }

                if (merge.FastForwarded && merge.ChangedFiles is not null)
                {
                    landingChangedFiles = merge.ChangedFiles;
                }

                return new AcceptanceMergeCommitResult(merge.FastForwarded, FormatWorkspaceMerge(merge));
            },
            "Acceptance completed goal after merge; cleanup deferred to conductor sweep."));
    mergeStarted.Stop();
    context.PhaseTimings.Record(
        "workspace-merge",
        mergeStarted.Elapsed,
        ("goal", goal.Id.Value[..8]),
        ("fastForwarded", mergeCommit.FastForwarded),
        ("guardAborted", mergeCommit.GuardAborted),
        ("message", mergeCommit.Message));

    if (mergeCommit.GuardAborted)
    {
        RecordAcceptanceGuardAbort(
            context,
            goal,
            mergeCommit,
            testedWorktreeHead,
            testedMainHead,
            acceptanceAttemptStartedAt,
            verification,
            stage: "state-guard");
        return false;
    }

    if (mergeCommit.Message is not null)
    {
        Console.WriteLine($"Workspace merge: {mergeCommit.Message}");
        if (mergeCommit.FastForwarded)
        {
            RecordAcceptanceCompleted(context, goal, testedWorktreeHead, testedMainHead, acceptanceAttemptStartedAt, verification);
            RunPostLandingCanary(context, goal, landingChangedFiles);
        }
        else
        {
            var failedChecks = new[] { "merge" };
            context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
            GoalOperationJournal.AcceptanceFailed(
                context.Workspace.ExecutionDirectory,
                goal,
                "acceptance",
                testedWorktreeHead,
                testedMainHead,
                $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: merge.",
                acceptanceAttemptStartedAt,
                GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
            context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
            AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=merge checks=merge");
            Console.WriteLine($"BLOCKER step=merge reason={mergeCommit.Message} action=\"Resolve conflicts on {context.Worktrees.BranchName(goal.Id)}, rerun verification, then rerun acceptance.\"");
        }
        return mergeCommit.FastForwarded;
    }

    RecordAcceptanceCompleted(context, goal, testedWorktreeHead, testedMainHead, acceptanceAttemptStartedAt, verification);
    RunPostLandingCanary(context, goal, landingChangedFiles);
    return true;
}

private static void RunPostLandingCanary(
    CliExecutionContext context,
    Goal goal,
    IReadOnlyList<string> changedFiles)
{
    var landingSha = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
    PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(
        context.Workspace,
        new ConductorLandingReceipt(goal.Id.Value, changedFiles, landingSha),
        Console.WriteLine);
}

private static DotnetBuildEnvironmentLease SelectGoalBuildPermit(
    GoalId goalId,
    TimeSpan? timeout,
    Action<DotnetBuildStableSlotWait>? onWait,
    DotnetBuildStorageRoot? storageRoot,
    string repositoryRoot)
{
    var environment = DotnetBuildEnvironmentManager.CreateAttempt(
        goalId, "acceptance", storageRoot: storageRoot, repositoryRoot: repositoryRoot);
    if (environment.BuildPermitIndex is { } permitIndex &&
        !DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(permitIndex, storageRoot))
    {
        onWait?.Invoke(new DotnetBuildStableSlotWait(
            permitIndex,
            DotnetBuildEnvironmentManager.GetStableSlotExecutionLeaseOwner(permitIndex, storageRoot)));
    }

    return DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, timeout) switch
    {
        DotnetBuildLeaseAcquisition.Acquired acquired => acquired.Lease,
        DotnetBuildLeaseAcquisition.SlotsBusy busy => throw new DotnetBuildSlotsBusyException(busy),
        DotnetBuildLeaseAcquisition.BuildLockBlocked blocked => throw new BuildLockBlockedException(blocked.Attribution),
        _ => throw new InvalidOperationException("Unknown dotnet build lease acquisition result.")
    };
}

private static string FormatBusySlots(IReadOnlyList<DotnetBuildStableSlotWait> busySlots) =>
    string.Join(
        "|",
        busySlots.Select(slot =>
            $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));

private static int? ParseStableSlotIndex(string slotOwnerToken)
{
    return slotOwnerToken.StartsWith("build-", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(slotOwnerToken[6..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var slotIndex)
            ? slotIndex
            : null;
}

private static bool TryNormalizePrematureCompletedGoalForAcceptance(
    CliExecutionContext context,
    Goal goal,
    out Goal normalizedGoal,
    out string detail)
{
    normalizedGoal = goal;
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status != GoalStatus.Completed)
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is {goal.Status}.";
        return false;
    }

    if (!GoalWorktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is not in a git worktree.";
        return false;
    }

    var gitFacts = GoalGitFactIndex.Build(context.Workspace.ExecutionDirectory).BuildGoalBranchFacts(goal);
    var hasBranchArtifact = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null ||
        gitFacts.HasGoalBranch;
    if (!hasBranchArtifact || gitFacts.BranchAlreadyLanded)
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} has no unmerged goal branch.";
        return false;
    }

    if (!context.Kernel.NormalizePrematureCompletedGoalForAcceptance(
            goal.Id,
            "acceptance: normalized raw Completed goal with unmerged branch back to Verified before merge."))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} does not have passed task verification gates.";
        return false;
    }

    normalizedGoal = context.Kernel.GetGoal(goal.Id);
    detail = $"Acceptance repair: normalized raw Completed goal {goalPrefix} to Verified so acceptance can merge {context.Worktrees.BranchName(goal.Id)}.";
    return true;
}

private static void RecordAcceptanceCompleted(
    CliExecutionContext context,
    Goal goal,
    string? branchHeadSha,
    string? mainHeadSha,
    DateTimeOffset? acceptanceAttemptStartedAt,
    AcceptanceVerificationResult? verification)
{
    GoalOperationJournal.AcceptancePassed(
        context.Workspace.ExecutionDirectory,
        goal,
        "acceptance",
        branchHeadSha,
        mainHeadSha,
        $"Acceptance passed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} and merge completed.",
        acceptanceAttemptStartedAt,
        GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
    context.Kernel.ClearAcceptanceFailure(goal.Id);
    if (goal.Status == GoalStatus.Verified)
    {
        context.Kernel.CompleteGoal(goal.Id, "Acceptance completed goal after merge; cleanup deferred to conductor sweep.");
    }

    context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
    AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=passed");
}

private static void AppendConductEvent(CliExecutionContext context, string eventKind, GoalId goalId, string detail)
{
    try
    {
        new ConductEventLogWriter(context.Workspace.ConductEventsLogPath).Append(eventKind, goalId.Value[..8], detail);
    }
    catch
    {
        // Shared operator event streaming is advisory; command output and lifecycle events remain authoritative.
    }
}

private static string FormatGateProgressConductEvent(AcceptanceGateProgress progress) =>
    $"PHASE_PROGRESS goal={progress.GoalId?[..Math.Min(8, progress.GoalId.Length)] ?? "unknown"} phase={progress.Phase} " +
    $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
    $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
    $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}";

private static string FormatConductToken(string value) =>
    value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
        ? value
        : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

private static string FormatConductEventChecks(IEnumerable<string> checks) =>
    string.Join(",", checks.Select(check => check.Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_')));

private static string? TryResolveGitHead(CliExecutionContext context, string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    try
    {
        return context.Worktrees.ResolveHead(path);
    }
    catch
    {
        return null;
    }
}

private static string FormatAcceptanceCandidate(string? branchHeadSha, string? mainHeadSha) =>
    $"branch={FormatShortSha(branchHeadSha)} main={FormatShortSha(mainHeadSha)}";

private static string FormatShortSha(string? sha) =>
    string.IsNullOrWhiteSpace(sha)
        ? "unknown"
        : sha.Trim()[..Math.Min(12, sha.Trim().Length)];

private static bool ClearSupersededAcceptanceFailureForCandidate(
    CliExecutionContext context,
    Goal goal,
    string? branchHeadSha,
    string? mainHeadSha)
{
    if (goal.LatestAcceptanceFailure is not { } failure ||
        string.IsNullOrWhiteSpace(failure.BranchHeadSha) ||
        string.IsNullOrWhiteSpace(failure.MainHeadSha))
    {
        return false;
    }

    if (string.Equals(failure.BranchHeadSha, branchHeadSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(failure.MainHeadSha, mainHeadSha, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (goal.Status == GoalStatus.AcceptanceFailed)
    {
        return context.Kernel.RestoreVerifiedForSupersededAcceptanceFailure(
            goal.Id,
            branchHeadSha,
            mainHeadSha);
    }

    context.Kernel.ClearAcceptanceFailure(goal.Id);
    return true;
}

private static string? TryBuildVerificationTimeoutBlocker(AcceptanceVerificationResult verification)
{
    var timedOutCheck = verification.Checks?
        .FirstOrDefault(IsBlockingTimeoutCheck);

    if (timedOutCheck is null)
        return null;

    var artifactPath = !string.IsNullOrWhiteSpace(timedOutCheck.ArtifactsPath)
        ? timedOutCheck.ArtifactsPath
        : verification.ArtifactsPath;
    var artifactDetail = string.IsNullOrWhiteSpace(artifactPath) ? "none" : artifactPath;
    return $"BLOCKER step=verification reason=timeout check=\"{timedOutCheck.Name}\" artifacts={artifactDetail} action=\"Inspect verification command, artifact path, and last output above; rerun acceptance after clearing the blocker.\"";
}

private static bool IsBlockingTimeoutCheck(AcceptanceCheckResult check) =>
    !check.Advisory &&
    !check.Passed &&
    (check.Name.StartsWith("acceptance-check-timeout:", StringComparison.OrdinalIgnoreCase) ||
     check.ResultSummary?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true ||
     check.OutputTail?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true);

private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
{
    var holders = attribution.Holders.Count == 0
        ? "unknown"
        : string.Join(
            ",",
            attribution.Holders.Select(holder =>
                $"pid-{holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}:{holder.ProcessName ?? "unknown"}"));
    return $"path=\"{EscapeBlockerDetail(attribution.Path)}\" holders={holders}";
}

private static string EscapeBlockerDetail(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

private static void ReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    bool cleanupEvidenceRecorded = false)
{
    TryReconcileLandedCleanedAcceptance(context, goal, source, out _, cleanupEvidenceRecorded);
}

private static bool TryReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    if (!HasLandedCleanedTerminalEvidence(context, goal, out detail, cleanupEvidenceRecorded))
    {
        return false;
    }

    if (goal.Status == GoalStatus.Verified)
    {
        context.Kernel.CompleteGoal(goal.Id, $"Acceptance repaired after durable landing and cleanup evidence from {source}.");
    }

    context.Kernel.ClearAcceptanceFailure(goal.Id);
    detail = $"Acceptance repaired: goal {goal.Id.Value[..8]} is already landed and workspace cleanup is recorded ({source}).";
    return true;
}

private static bool HasLandedCleanedTerminalEvidence(
    CliExecutionContext context,
    Goal goal,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status is not (GoalStatus.Verified or GoalStatus.Completed))
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} is {goal.Status}, not Verified or Completed.";
        return false;
    }

    if (context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} still has a worktree.";
        return false;
    }

    var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var hasLandingEvidence = journal.LatestByOperation.Any(entry =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));
    if (!hasLandingEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed acceptance or conductor landing evidence.";
        return false;
    }

    var hasCleanupEvidence = cleanupEvidenceRecorded ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)));
    if (!hasCleanupEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed workspace cleanup evidence.";
        return false;
    }

    detail = $"goal {goalPrefix} has completed landing and cleanup evidence.";
    return true;
}

private static void RecordAcceptanceGuardAbort(
    CliExecutionContext context,
    Goal goal,
    AcceptanceMergeCommitResult abort,
    string? testedWorktreeHead,
    string? testedMainHead,
    DateTimeOffset? acceptanceAttemptStartedAt,
    AcceptanceVerificationResult? verification,
    string stage)
{
    context.RegisterAcceptanceGuardAbort();
    var detail = abort.Message ?? "Acceptance merge aborted because a landing guard changed.";
    var field = abort.GuardAbort?.Field ?? "unknown";
    GoalOperationJournal.AcceptanceAborted(
        context.Workspace.ExecutionDirectory,
        goal,
        "acceptance",
        testedWorktreeHead,
        testedMainHead,
        $"Acceptance aborted:state-guard for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {detail}",
        acceptanceAttemptStartedAt,
        GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
    var abortEvent =
        $"ACCEPTANCE goal={goal.Id.Value[..8]} result=aborted stage={stage} field={FormatConductEventChecks([field])} checks={FormatConductEventChecks([detail])}";
    AppendConductEvent(
        context,
        "acceptance",
        goal.Id,
        abortEvent);
    Console.WriteLine(abortEvent);
    Console.WriteLine(
        $"BLOCKER step=acceptance-state-guard reason=aborted field=\"{EscapeBlockerDetail(field)}\" " +
        $"detail=\"{EscapeBlockerDetail(detail)}\" action=\"Quiesce conductor mutations for this goal before another landing attempt; do not use a bare retry while the goal is changing.\"");
    var projectionKernel = context.ReloadKernel([goal.Id.Value]);
    var projectionGoal = projectionKernel.Goals.FirstOrDefault(candidate => candidate.Id == goal.Id) ?? goal;
    ConsoleViews.PrintAcceptanceSummary(
        projectionGoal,
        GoalAcceptanceStatusProjector.Build(projectionKernel, projectionGoal, context.Workspace.ExecutionDirectory));
}

private static T RunGoalMarkLandedStep<T>(string stepName, Func<T> step)
{
    try
    {
        return step();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"goal-mark-landed {stepName} failed: {ex.Message}", ex);
    }
}

private static void PrintGoalMarkLandedSummary(
    bool hadWorktree,
    bool cleanupComplete)
{
    Console.WriteLine("Goal landed cleanup:");
    Console.WriteLine(hadWorktree
        ? cleanupComplete ? "cleanup: worktree removed" : "cleanup: worktree cleanup deferred"
        : "cleanup: worktree already absent");
    Console.WriteLine(cleanupComplete ? "cleanup: branch deleted" : "cleanup: branch cleanup deferred");
    Console.WriteLine(cleanupComplete ? "cleanup: app-host lock released" : "cleanup: app-host lock cleanup deferred");
    Console.WriteLine(cleanupComplete ? "cleanup: goal marked CleanedUp" : "cleanup: goal marked landed; cleanup-needed recorded");
}
}
