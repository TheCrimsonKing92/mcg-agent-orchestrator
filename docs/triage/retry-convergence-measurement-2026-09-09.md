# Retry routing: before/after fixture, 2026-09-09

This comparison addresses goal `e990fc48` criterion 8: record before/after subscription dispatch count, evidence attempts, and tokens/elapsed time on a fixture or bounded dogfood run.

The fixture demonstrates restored progress. With the same unchanged candidate, green evidence, blocking Tester/Reviewer source findings and already-pending upstream retry, baseline `dc6935c14fcca59333bf9274dd61ad23e78bed8a` escalated on all ten ticks. Candidate `911c31e2b3aed6b12a1d455c0ea6ce0639ff226c` admitted one Developer and then held without admitting another worker. Both fresh and snapshot-restart cases had that result. Existing goal tests separately cover Developer completion on a new SHA followed by Tester admission.

## Measured results

The scenario makes **zero real subscription dispatches, zero provider calls and uses zero model tokens** in every case. Those three receipt fields are constants by construction, not observed live-provider counters. Admissions below are simulated callbacks. Evidence executions count calls to a simulated focused-evidence callback; no external test process runs inside that callback. The zero/zero comparison establishes no added evidence work, not reduced evidence execution. Do not infer paid token savings, provider performance, or full repository acceptance from these results.

| Revision | Restart at tick 6 | Simulated admissions | Roles | Evidence callbacks | Driver elapsed (ms) | Outcomes |
|---|---|---:|---|---:|---:|---|
| baseline | False | 0 | none | 0 | 14.2833 | 10 Escalated |
| baseline | True | 0 | none | 0 | 207.3106 | 10 Escalated |
| candidate | False | 1 | Developer | 0 | 40.9 | 1 Executed, 9 Held |
| candidate | True | 1 | Developer | 0 | 168.3731 | 1 Executed, 9 Held |

Elapsed time covers ten driver calls and snapshot restoration when selected. It excludes build/setup and is a single-run diagnostic, not a speed benchmark. Neither lower latency nor a percentage speedup is established. Both cases retain the seeded green receipt. The candidate's recorded `PreReviewEvidenceAttemptCount` is 1; the baseline has no such property and reports `null`. The callback counter provides the comparable execution measurement. Total timeline retry events count every role without decomposition and must not be interpreted as additional Developer admissions.

Accounting compatibility: these receipts describe the named revisions, before the later accounting change that excludes synthetic reuse and distinguishes otherwise identical receipts by recording time. Existing persisted totals retain their earlier counting rules; bounded history cannot reconstruct a complete corrected lifetime total. They must not be treated as exact execution counts or used to infer token savings across that change. Restoration retains the greater of the persisted total and the retained non-reuse receipt count. For legacy snapshots without a persisted total, that reconstructed floor can be lower than in earlier revisions. The measured callback counts above remain the comparable fixture evidence.

The simulated Developer never exits in this fixture. Its final state is Running with a Held driver outcome; task completion and end-to-end landing are not proved. This is an end-to-end cross-revision routing comparison, not attribution to one particular call site. Anthropic Opus 5 approved it for criterion 8's fixture option subject to this distinction between routing progress and completion.

## Reproduction and provenance

Build separate detached checkouts of the two revisions. Add the **identical** C# source below as `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsRetryMeasurement.cs` in each. Run the repository-managed test helper with the Infrastructure test project and filter `FullyQualifiedName~ConductorDriverTestsRetryMeasurement`. Follow `docs/operator-runbook.md` for managed test execution; do not use a native test apphost. This fixture intentionally measures outcomes rather than requiring the broken baseline to behave like the repaired candidate.

The compatibility adapter invokes each revision's production retry entry point: baseline `ConductorDriver.cs` calls `RetryTask`, candidate calls `RetryTaskAutomatically`, with the same argument meaning. Reflection only selects that existing entry point and observes an optional telemetry property. Initial state, ten-tick schedule, two capacity-unavailable ticks, evidence callbacks and restart are unchanged. Recorded process identity is read from the actual dispatch metadata. No production files were modified in either measurement checkout.

Each run writes two measurement JSON files under `%USERPROFILE%/AppData/LocalLow/Mcg.AgentOrchestrator/e990-measurement`, named by Core assembly SHA256 and restart flag. Preserve these with the managed-runner receipt and source/DLL/PDB binding. The execution receipts each record 2 passed, 0 skipped, exit 0 and confirmed process exit. Source/DLL/PDB binding verified the fixture, Driver and Core retry implementation. The application versions match the named revisions.

Evidence root for this execution: `.orchestrator/operator-evidence/rearchitecture-20260906/`:

- `e990-before-after-measurement.json`: both revisions, four complete observations and source identities.
- `e990-{baseline,candidate}-measurement-v3-binding.json`: revision, fixture checksum, assembly/source proofs and source status.
- `e990-{baseline,candidate}-measurement-v3-execution/`: runner receipt, TRX and copied measurement JSON.
- `e990-measurement-peer-review.json`: independent Anthropic review of the comparison and bounded conclusion.

Two earlier instrumentation failures were retained separately: mismatched dispatch/verification directory, then mismatched process/dispatch command. They are fixture setup errors, not product regressions. The final version below derives those fields from the dispatch record; both revisions were rebuilt and rebound after correction. Earlier failed receipts and baseline-only intermediate measurements are not the final comparison.

Fixture SHA256 for the executed source: `84DB2E60333F50E48FEBBA62D6155A746B1CCC45F0AED061B38374DA334E546B`.

## Fixture source

```csharp
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsRetryMeasurement
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MeasureUnchangedPendingRetry(bool restart)
    {
        const string candidate = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var predecessor in goal.Tasks.TakeWhile(task => task.Id != developer.Id))
            PassVerification(kernel, goal, predecessor);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidate);
        kernel.RetryTask(goal.Id, developer.Id, "The operator supplied an upstream source correction.",
            retryCause: RetryCause.NewSourceFinding);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value, 1, candidate, ["Infrastructure.Tests: ConductorDriverTests"],
            PreReviewEvidenceDisposition.Green, 1, 0,
            [new PreReviewEvidenceCheckReceipt("ConductorDriverTests", "Infrastructure.Tests: ConductorDriverTests", true, 0)],
            [], "fixture green evidence", "fixture://green", DateTimeOffset.UtcNow));
        RecordFinding(kernel, goal, tester);
        FailReviewerNeedsWork(kernel, goal, reviewer, "reviewer source finding",
            findings: [EvidenceFindingWithRequest("The unchanged candidate still has a blocking source defect.",
                id: "combined-reviewer-correctness", category: FindingCategory.Correctness)]);

        var evidenceExecutions = 0;
        var evidenceElapsed = TimeSpan.Zero;
        var starts = new List<string>();
        var retryCalls = 0;
        var tick = 0;
        var outcomes = new List<object>();
        // Mirror each revision's production ConductorDriver retry callback. The old
        // revision has no automatic-specific entry point; argument meaning is unchanged.
        var retry = typeof(AgentOrchestratorKernel).GetMethod("RetryTaskAutomatically") ??
            typeof(AgentOrchestratorKernel).GetMethod("RetryTask")!;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidate),
            runFocusedEvidence: (_, request) =>
            {
                var timer = Stopwatch.StartNew();
                evidenceExecutions++;
                var result = DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidate);
                evidenceElapsed += timer.Elapsed;
                return result;
            },
            dispatchAndStart: current =>
            {
                if (tick < 3) return DispatchStartOutcome.EmptyBatch("Fixture provider capacity unavailable for two ticks.");
                var batch = GoalManagementCommandService.BuildReadyTaskParallelPlan(current, DefaultAgents()).Batches.FirstOrDefault();
                foreach (var task in current.Tasks.Where(task => batch?.IntentIds.Contains(task.Id.Value) == true))
                {
                    starts.Add(task.RequiredRole.ToString());
                    DispatchTask(kernel, current, task);
                    kernel.RecordTaskProcessStarted(current.Id, task.Id, new TaskProcessRecord(
                        12345, task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory, @"C:\tmp\stdout", @"C:\tmp\stderr", @"C:\tmp\exit",
                        DateTimeOffset.UtcNow, null, null));
                }
                return DispatchStartOutcome.Started();
            },
            retryTaskWithCause: (goalId, taskId, message, kind, cause) =>
            {
                retryCalls++;
                var arguments = retry.GetParameters().Select(parameter => parameter.Name switch
                {
                    "goalId" => (object)goalId, "taskId" => taskId, "message" => message,
                    "retryRoundKind" => kind, "retryCause" => cause,
                    _ when parameter.HasDefaultValue => parameter.DefaultValue,
                    _ => throw new InvalidOperationException("Unmapped retry parameter: " + parameter.Name)
                }).ToArray();
                return (TaskSpec)retry.Invoke(kernel, arguments)!;
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message),
            recordFindingEvidenceRequest: (goalId, taskId, message) => kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, id, result, receipt) => kernel.RecordFindingEvidenceOutcome(goalId, taskId, id, result, receipt));
        var elapsed = Stopwatch.StartNew();
        for (tick = 1; tick <= 10; tick++)
        {
            if (restart && tick == 6)
            {
                kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
                goal = kernel.GetGoal(goal.Id);
            }
            var outcome = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            outcomes.Add(new { tick, type = outcome.Outcome.GetType().Name, detail = outcome.Outcome.ToString() });
        }
        elapsed.Stop();
        var coreSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Goal).Assembly.Location)));
        var appSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ConductorDriver).Assembly.Location)));
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Mcg.AgentOrchestrator", "e990-measurement");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, coreSha + "-" + restart + ".json"), JsonSerializer.Serialize(new
        {
            coreSha, appSha, restart, retryAdapter = retry.Name, ticks = 10,
            subscriptionDispatchCount = 0, simulatedDispatchAdmissions = starts.Count, simulatedRoles = starts,
            modelCalls = 0, modelTokens = 0, evidenceExecutions, simulatedEvidenceElapsedMilliseconds = evidenceElapsed.TotalMilliseconds,
            driverElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, retryCalls,
            recordedEvidenceAttempts = typeof(TaskSpec).GetProperty("PreReviewEvidenceAttemptCount") is { } counter
                ? (int?)goal.Tasks.Sum(task => (int)counter.GetValue(task)!) : null,
            retryEvents = goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried), outcomes,
            scope = "Identical deterministic driver fixture; process starts and evidence callbacks are simulated. No subscriptions or external tests execute. Time excludes build and setup; no live model-token saving is claimed."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(10, outcomes.Count);
    }

    private static void RecordFinding(AgentOrchestratorKernel kernel, Goal goal, TaskSpec tester)
    {
        DispatchTask(kernel, goal, tester, "test");
        var finding = new ReviewFinding("T-FINDING", ReviewFindingState.Open,
            new ReviewFindingLocation("src/Test.cs", "Test.Run"), "The candidate requires a finding-specific response.",
            FindingSeverity.Blocking, FindingCategory.Correctness,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]));
        var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
            "tests: deferred - acceptance owns focused execution", "commit: none", "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}", "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - deterministic control", "skills: none", "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(tester.LastDispatch!.Command, tester.LastDispatch.WorkingDirectory, 0,
            output, "", DateTimeOffset.UtcNow, StandardOutputPath: @"C:\tmp\tester.out.log", WorkerResultPresent: true));
    }
}
```
