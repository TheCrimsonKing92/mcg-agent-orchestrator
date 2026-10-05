using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Synthetic decision-table inputs. These do not substitute for the real-derived criterion-1 fixture.
internal static class FailureClusterTestData
{
    internal static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-21T00:00:00Z");
    internal static readonly DateTimeOffset Until = Since.AddDays(14);
    internal const string Green = "finding evidence-on-demand: role=Reviewer; task=aaaaaaaa; Focused evidence completed and its receipt was attached to the requesting finding.";
    internal static FailureClusterInputs Create() => new(
        [
            new("TaskRetried", Green, "11111111", "aaaaaaaa", Since.AddHours(1)),
            new("TaskRetried", Green.Replace("aaaaaaaa", "bbbbbbbb"), "22222222", "bbbbbbbb", Since.AddHours(2)),
            new("TaskRetried", Green.Replace("aaaaaaaa", "cccccccc"), "33333333", "cccccccc", Since.AddHours(3)),
            new("GoalEscalated", "escalated at WorkspaceReady — Goal branch goal/44444444 has uncommitted changes; conductor integration cannot start from a dirty worktree.", "44444444", null, Since.AddHours(4)),
            new("TaskFailed", "Dispatch failed: rule=unknown-failure; exit code 1: claude -p ...", "55555555", "dddddddd", Since.AddHours(5))
        ],
        [
            new(Since.AddHours(6), "gate-progress", "66666666", "PHASE_PROGRESS goal=66666666 phase=gate-phase-breakdown elapsed_ms=300000 target=scope=verifier-run;outcome=InfrastructureFailure"),
            new(Since.AddHours(6).AddSeconds(1), "acceptance-cohort", "66666666", "ACCEPTANCE_COHORT_EXIT tick=9 goal=66666666 outcome=InfrastructureFailure reason=structural-coverage-permit-unavailable")
        ],
        [new("44444444", null, Since.AddHours(4.5)), new("55555555", "dddddddd", Since.AddHours(5.5))]);
}
