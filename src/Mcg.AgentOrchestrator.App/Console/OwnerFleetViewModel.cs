using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerFleetReachability { Unknown, Reachable, Unreachable }

internal sealed record OwnerFleetHostNode(bool ConductorRunning, int ExecutorCount,
    int UnreadableOutcomeLines, int UnreadableProbeLines,
    int NoExecutorFreeCount = 0, DateTimeOffset? NoExecutorFreeLastAt = null);

internal sealed record OwnerFleetExecutorNode(string Id, OwnerFleetReachability Reachability,
    IReadOnlyList<string> ProbeReasons, DateTimeOffset? ProbeAt, int? ConfiguredSlots,
    int PeakConcurrentAttempts, string? LastOutcome, DateTimeOffset? LastOutcomeAt,
    double? AcceptedShare, IReadOnlyList<RemoteExecutorLaneSeconds> Lanes, RemoteExecutorSlotsSyncRow? SlotsFile = null,
    bool? AdmissionEnabled = null, string? Owner = null);

internal sealed record OwnerFleetViewModel(OwnerFleetHostNode Host,
    IReadOnlyList<OwnerFleetExecutorNode> Executors, IReadOnlyList<OwnerFleetProviderHoldNode>? ProviderHolds = null);

internal sealed record OwnerFleetProviderHoldNode(string BindingKey, string Name, string Source,
    DateTimeOffset PlacedAt, DateTimeOffset? NextProbeAt, int? Attempt, string? ResetSource, string? LastProbe);

internal static class OwnerFleetViewModelBuilder
{
    internal static OwnerFleetViewModel Build(RemoteExecutorReport report, bool conductorRunning, TimeZoneInfo zone,
        IReadOnlyDictionary<string, RemoteExecutorSlotsSyncRow>? slotsSync = null,
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null, OwnerConsoleHostRowOwner? owners = null) =>
        new(new(conductorRunning, report.Executors.Count, report.UnreadableOutcomeLines, report.UnreadableProbeLines,
                report.NoExecutorFreeCount, report.NoExecutorFreeLastAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null),
            report.Executors.Select(row =>
            {
                var last = row.RecentAttempts.FirstOrDefault();
                return new OwnerFleetExecutorNode(row.ExecutorId, row.LastProbe is null ? OwnerFleetReachability.Unknown :
                    row.LastProbe.Reachable ? OwnerFleetReachability.Reachable : OwnerFleetReachability.Unreachable,
                    row.LastProbe?.Reasons.ToArray() ?? [],
                    row.LastProbe is { } probe ? TimeZoneInfo.ConvertTime(probe.ObservedAt, zone) : null,
                    row.ConfiguredSlots, row.PeakConcurrentAttempts, last?.Outcome,
                    last is null ? null : TimeZoneInfo.ConvertTime(last.ObservedAt, zone), row.AcceptedShare, row.Lanes.ToArray(),
                    slotsSync?.GetValueOrDefault(row.ExecutorId) is { } sync ?
                        sync with { ObservedAt = TimeZoneInfo.ConvertTime(sync.ObservedAt, zone) } : null, row.AdmissionEnabled,
                    owners?.ForAttempts(row.RecentAttempts.Select(attempt => attempt.GateAttemptId)));
            }).ToArray(), (providerHolds ?? []).Select(row => new OwnerFleetProviderHoldNode(
                row.Hold.BindingKey, OwnerProviderHoldNarration.DisplayName(row.Hold.BindingKey),
                $"{row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}",
                TimeZoneInfo.ConvertTime(row.Hold.PlacedAt, zone),
                row.Schedule is { } schedule ? TimeZoneInfo.ConvertTime(schedule.NextProbeAt, zone) : null,
                row.Schedule?.Attempt, row.Schedule?.ResetSource,
                row.Schedule?.LastProbeOutcome is { } outcome && outcome != "none" ? outcome : null)).ToArray());

    internal static OwnerFleetViewModel Load(OrchestratorWorkspace workspace, IConductorLiveness liveness, TimeProvider? clock = null,
        OwnerConsoleHostRowOwner? owners = null) =>
        Build(RemoteExecutorReportReader.Read(workspace, null, 10, (clock ?? TimeProvider.System).GetUtcNow().AddHours(-24)),
            liveness.IsRunning(), TimeZoneInfo.Local, RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(
                Path.Combine(Path.GetDirectoryName(RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory))!,
                    RemoteExecutorSlotsSyncLedger.FileName)),
            ProviderUsageHoldReader.ReadWorkspace(workspace, (clock ?? TimeProvider.System).GetUtcNow()), owners);
}
