using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeveloperCriteriaSelfCheckDigestTests : WorkerDispatchTestSupport
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void DigestListsEveryEntryAndFullEvidenceFromLatestCompletedRound(AgentRole role)
    {
        var evidence = new string('x', 600) + "UNTRIMMED-TAIL";
        var entries = new[]
        {
            new { criterion_index = 0, status = "proven", evidence },
            new { criterion_index = 3, status = "not-owned", evidence = "Reviewer" },
            new { criterion_index = 5, status = "unmet", evidence = "Missing assertion" },
            new { criterion_index = 8, status = "proven", evidence = "Tests.Last asserts final outcome\nwithout trimming" }
        };
        var section = Section(WriteDigest(JsonSerializer.Serialize(entries), role));
        Xunit.Assert.Equal(entries.Select(e =>
            $"- criterion_index={e.criterion_index} status={e.status} evidence={e.evidence.Replace('\n', ' ')}"), section);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void LatestMissingRoundDoesNotFallBackToEarlierReport(AgentRole role)
    {
        Xunit.Assert.Equal(new[] { "- criteria_self_check field=missing in the latest completed Developer round." },
            Section(WriteDigest(null, role)));
    }

    [Xunit.Fact]
    public void EmptyAndMalformedReportsHaveOneLine()
    {
        Xunit.Assert.Equal(new[] { "- criteria_self_check field=present with no entries." },
            Section(WriteDigest("[]", AgentRole.Tester)));
        Xunit.Assert.Equal(new[] { "- criteria_self_check field=malformed in the latest completed Developer round: Invalid JSON array or entry shape." },
            Section(WriteDigest("[{", AgentRole.Tester)));
    }

    [Xunit.Fact]
    public void NoCompletedDeveloperHasMissingLineAndDeveloperHasNoSection()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Inspect digest.", [developer, tester]);
        var writer = new WorkerArtifactWriter();
        var directory = writer.Write(goal, tester, root);
        Xunit.Assert.Equal(new[] { "- criteria_self_check field=missing in the latest completed Developer round." },
            Section(File.ReadAllText(Path.Combine(directory, "digest.md"))));
        directory = writer.Write(goal, developer, root);
        Xunit.Assert.DoesNotContain("## Developer Criteria Self-Check", File.ReadAllText(Path.Combine(directory, "digest.md")), StringComparison.Ordinal);
    }

    private static string WriteDigest(string? latestField, AgentRole role)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        // Completion time, rather than list order, determines which candidate's report is shown.
        var latest = new TaskSpec(TaskId.New(), "Latest implementation.", AgentRole.Developer);
        var older = new TaskSpec(TaskId.New(), "Older implementation.", AgentRole.Developer);
        var pending = new TaskSpec(TaskId.New(), "Pending implementation.", AgentRole.Developer);
        var task = new TaskSpec(TaskId.New(), "Attest criteria.", role);
        var goal = kernel.CreateGoal("Inspect digest.", [older, task, latest, pending]);
        kernel.RecordTaskVerification(goal.Id, latest.Id, Verification(latestField, CompletedAt));
        kernel.RecordTaskVerification(goal.Id, older.Id, Verification(
            "[{\"criterion_index\":99,\"status\":\"proven\",\"evidence\":\"OLDER-REPORT\"}]", CompletedAt.AddMinutes(-1)));
        // A completed round on another goal must never enter this goal's digest.
        var foreign = new TaskSpec(TaskId.New(), "Foreign implementation.", AgentRole.Developer);
        var foreignGoal = kernel.CreateGoal("Other goal.", [foreign]);
        kernel.RecordTaskVerification(foreignGoal.Id, foreign.Id, Verification(
            "[{\"criterion_index\":100,\"status\":\"proven\",\"evidence\":\"FOREIGN-REPORT\"}]", CompletedAt.AddMinutes(1)));
        Xunit.Assert.Equal(WorkTaskStatus.Completed, latest.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, older.Status);
        Xunit.Assert.NotEqual(WorkTaskStatus.Completed, pending.Status);
        var directory = new WorkerArtifactWriter().Write(goal, task, root);
        var digest = File.ReadAllText(Path.Combine(directory, "digest.md"));
        Xunit.Assert.True(digest.IndexOf("## Prior Completed Outcomes", StringComparison.Ordinal) <
            digest.IndexOf("## Developer Criteria Self-Check", StringComparison.Ordinal));
        return digest;
    }

    private static TaskVerificationRecord Verification(string? field, DateTimeOffset completedAt) =>
        new("fixture", "C:\\repo", 0,
            "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nassigned_scope_complete: true\n" +
            (field is null ? string.Empty : $"criteria_self_check: {field}\n") + "END_WORKER_RESULT",
            string.Empty, completedAt);

    private static string[] Section(string digest)
    {
        var lines = digest.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.IndexOf(lines, "## Developer Criteria Self-Check");
        Xunit.Assert.True(start >= 0, "Digest must contain Developer Criteria Self-Check.");
        return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal))
            .Where(l => l.Length > 0).ToArray();
    }
}
