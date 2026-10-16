using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Simulation;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class BoardSimulationFixture : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "mcg-board-simulation", Guid.NewGuid().ToString("N"));
    internal static DateTimeOffset Start => new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    internal static DateTimeOffset At(string time) => Start + TimeSpan.ParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture);
    internal static string Id(char label) => new(label switch { 'G' => '7', 'H' => '8', 'I' => '9', _ => char.ToLowerInvariant(label) }, 32);
    internal static RecordedGateMember Member(char label) => new(Id(label), new string('1', 40), [$"src/{label}.cs"], [$"resource:{label}"]);
    internal static RecordedGateRun Gate(GateRunKind kind, string members, string begin, string end, GateRunOutcome outcome) =>
        new(kind, members.Select(Member).ToArray(), At(begin), At(end), outcome);
    internal static RecordedReadinessEpoch Epoch(char label, string ready, string? leave = null)
    {
        var m = Member(label);
        return new(m.GoalId, At(ready), leave is null ? null : At(leave), m.CandidateRevision, m.LandingPaths, m.ResourceKeys);
    }

    internal static BoardHistoryTrace F1() => new(Start, Start.AddDays(1),
        [Gate(GateRunKind.Solo, "F", "00:20", "01:10", GateRunOutcome.Failed),
         Gate(GateRunKind.Train, "ABC", "01:10", "01:30", GateRunOutcome.Passed),
         Gate(GateRunKind.Cohort, "DE", "03:10", "03:30", GateRunOutcome.Failed),
         Gate(GateRunKind.Solo, "E", "03:30", "03:50", GateRunOutcome.Passed)],
        [Epoch('F', "00:20", "01:15"), Epoch('A', "01:00"), Epoch('B', "01:05"),
         Epoch('C', "01:10"), Epoch('D', "03:10", "03:35"), Epoch('E', "03:10")],
        [new(Id('A'), At("01:32")), new(Id('B'), At("01:32")), new(Id('C'), At("01:32")), new(Id('E'), At("03:52"))], []);

    internal static BoardHistoryTrace Drops() => new(Start, Start.AddDays(1),
        [Gate(GateRunKind.Train, "GHI", "05:00", "05:20", GateRunOutcome.Failed),
         Gate(GateRunKind.Train, "GH", "05:20", "05:40", GateRunOutcome.Failed),
         Gate(GateRunKind.Solo, "G", "05:40", "06:00", GateRunOutcome.Failed),
         Gate(GateRunKind.Solo, "H", "06:00", "06:20", GateRunOutcome.Passed),
         Gate(GateRunKind.Solo, "I", "06:20", "06:40", GateRunOutcome.Passed)],
        [Epoch('G', "05:00"), Epoch('H', "05:00"), Epoch('I', "05:00")],
        [new(Id('H'), At("06:22")), new(Id('I'), At("06:42"))], []);

    internal BoardSimulationFixture() => Directory.CreateDirectory(DirectoryPath);

    internal void WriteF1()
    {
        var trace = F1();
        WriteTrain(trace.GateRuns[1]);
        WriteCohort(trace.GateRuns[2]);
        WriteAttempt(trace.GateRuns[0], "f");
        WriteAttempt(trace.GateRuns[3], "e");
        foreach (var epoch in trace.Epochs)
        {
            WriteEvent(epoch.GoalId, "TaskDispatched", epoch.ReadyAt.AddMinutes(-1));
            WriteEvent(epoch.GoalId, "TaskVerified", epoch.ReadyAt);
            if (epoch.LeaveAt is { } leave) WriteEvent(epoch.GoalId, "TaskDispatched", leave);
        }
        foreach (var landing in trace.Landings)
        {
            WriteEvent(landing.GoalId, "GoalLanded", landing.LandedAt);
            WriteEvent(landing.GoalId, "GoalLanded", landing.LandedAt.AddSeconds(5), "main");
        }
    }

    internal void WriteTrain(RecordedGateRun gate)
    {
        var bindings = gate.Members.Select(m => new MergeTrainMemberBinding(new GoalId(m.GoalId),
            m.CandidateRevision, m.CandidateRevision, m.LandingPaths, m.ResourceKeys,
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", m.CandidateRevision)).ToArray();
        var identity = MergeTrainIdentity.Create(bindings, new string('a', 40), new string('b', 40), "manifest-v1");
        var trx = Path.Combine(DirectoryPath, "passed.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Fixture" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        new MergeTrainAcceptanceStore(Path.Combine(DirectoryPath, "merge-train-acceptance.db"))
            .SaveGateReceipt(new("train-fixture", identity, MergeTrainGateOutcome.Passed, gate.EndAt,
                (long)(gate.EndAt - gate.BeginAt).TotalMilliseconds, [], 0, [trx], ValidForLanding: true));
    }

    internal void WriteCohort(RecordedGateRun gate)
    {
        var bindings = gate.Members.Select(m => new AcceptanceCohortMemberBinding(new GoalId(m.GoalId),
            m.CandidateRevision, m.CandidateRevision, m.LandingPaths, m.ResourceKeys,
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")).ToArray();
        var identity = AcceptanceCohortIdentity.Create(bindings, new string('a', 40), new string('b', 40), "manifest-v1");
        new CohortAcceptanceStore(Path.Combine(DirectoryPath, "cohort-acceptance.db"))
            .SaveGateReceipt(new("cohort-fixture", identity, AcceptanceCohortGateOutcome.Failed, gate.EndAt,
                (long)(gate.EndAt - gate.BeginAt).TotalMilliseconds, ["failed"], 1, []));
    }

    internal string WriteAttempt(RecordedGateRun gate, string name, bool heartbeatOnly = false)
    {
        var member = gate.Members.Single();
        var directory = Path.Combine(DirectoryPath, "acceptance-gate-attempts", member.GoalId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".attempt.json");
        var attempt = new ConductorParallelAcceptanceAttempt(name, member.GoalId, member.GoalId[..8], 0,
            member.CandidateRevision, new string('a', 40), gate.BeginAt, gate.EndAt, 1,
            gate.Outcome == GateRunOutcome.Passed ? ConductorParallelAcceptanceAttemptOutcome.Passed : ConductorParallelAcceptanceAttemptOutcome.Failed,
            "", "", "", "", "", path, ScopePaths: member.LandingPaths, CompletedAt: heartbeatOnly ? null : gate.EndAt);
        File.WriteAllText(path, JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    internal string WriteEvent(string goalId, string kind, DateTimeOffset at, string branch = "integration")
    {
        var directory = Path.Combine(DirectoryPath, "goal-events");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, goalId + ".jsonl");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var line = kind == "GoalLanded"
            ? JsonSerializer.Serialize(new { cursor = 3, timestamp = at, goalId, eventType = kind,
                integrationBranch = branch, goalBranch = "goal/" + goalId[..8] }, options)
            : JsonSerializer.Serialize(new { cursor = 1, timestamp = at, goalId, eventType = kind,
                progressKind = kind == "TaskDispatched" ? "TaskDispatchRecorded" : "TaskVerificationRecorded",
                message = kind == "TaskDispatched" ? "Dispatched." : "Dispatch execution passed (0).", occurredAt = at, taskId = "t1" }, options);
        File.AppendAllText(path, line + Environment.NewLine);
        File.SetLastWriteTimeUtc(path, Start.AddHours(23).UtcDateTime);
        return path;
    }

    internal string[] Snapshot() => Directory.GetFiles(DirectoryPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
