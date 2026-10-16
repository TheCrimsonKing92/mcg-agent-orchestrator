using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel safe: pure in-memory inputs, with explicit timestamps and no shared state.
public sealed class OperatorIntentCoverageReportTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset At = Since.AddDays(1);
    private static readonly DateTimeOffset Until = Since.AddDays(3);

    [Theory]
    [InlineData("criterion-evidence-map", "{\"requiredScope\":\"acceptance:full-gate\"}", "criterion-evidence-map scope=acceptance binding=first")]
    [InlineData("criterion-evidence-record", "{\"requiredScope\":\"operator:manual\",\"passed\":false}", "criterion-evidence-record scope=operator passed=false")]
    [InlineData("criterion-evidence-record", "{\"scope\":\"acceptance:full-gate\",\"passed\":true}", "criterion-evidence-record scope=acceptance passed=true")]
    [InlineData("retry", "{\"retryCause\":2}", "retry cause=NewTestFinding")]
    [InlineData("retry", "{\"retryCause\":\"NewTestFinding\"}", "retry cause=NewTestFinding")]
    [InlineData("retry", "{\"retryCause\":\"newtestfinding\"}", "retry cause=NewTestFinding")]
    [InlineData("retry", "{\"retryCause\":999}", "retry cause=999")]
    [InlineData("retry", "{\"retryCause\":\"future\"}", "retry cause=future")]
    [InlineData("adjudicate", "{\"shape\":\"route\",\"cause\":\"X\"}", "adjudicate shape=route cause=X")]
    [InlineData("verify-manual", "{\"request\":{\"passed\":true}}", "verify-manual passed=true")]
    [InlineData("answer", "{\"targetKind\":2}", "answer targetKind=HumanInput")]
    [InlineData("answer", "{\"targetKind\":\"HumanInput\"}", "answer targetKind=HumanInput")]
    [InlineData("lesson-record", "{\"rule\":\"rule\",\"situation\":\"case\"}", "lesson-record")]
    [InlineData("cancel-dispatch", "{}", "cancel-dispatch")]
    [InlineData("adjudicate", "{broken", "adjudicate shape=none cause=none")]
    [InlineData("verify-manual", "[]", "verify-manual passed=none")]
    [InlineData("retry", null, "retry cause=none")]
    public void ClassKeysFollowVerbPayloadContracts(string verb, string? payload, string expected)
    {
        var row = Assert.Single(Build([Intent("human", verb, payload)]).Intents);
        Assert.Equal(expected, row.ClassKey);
    }

    [Theory]
    [InlineData("old", "rebind")]
    [InlineData("current", "first")]
    public void BindingUsesHistoryBeforeTheWindowFromAnyActor(string priorSha, string binding)
    {
        var prior = Intent("prior", "criterion-evidence-map", Map(priorSha)) with
        { CreatedAt = Since.AddMinutes(-1), ActorKind = OperatorActorKind.Agent };
        var human = Intent("human", "criterion-evidence-map", Map("current"));
        var row = Assert.Single(Build([human, prior]).Intents);
        Assert.Equal($"criterion-evidence-map scope=acceptance binding={binding}", row.ClassKey);
    }

    [Theory]
    [InlineData("same-time")]
    [InlineData("future")]
    [InlineData("other-goal")]
    [InlineData("other-criterion")]
    [InlineData("other-scope")]
    [InlineData("empty-sha")]
    public void UnrelatedOrNonEarlierMappingsDoNotRebind(string control)
    {
        var prior = Intent("prior", "criterion-evidence-map", Map("old")) with
        { CreatedAt = Since.AddMinutes(-1), ActorKind = OperatorActorKind.Agent };
        prior = control switch
        {
            "same-time" => prior with { CreatedAt = At },
            "future" => prior with { CreatedAt = At.AddSeconds(1) },
            "other-goal" => prior with { GoalId = "other" },
            "other-criterion" => prior with { PayloadJson = Map("old", 2) },
            "other-scope" => prior with { PayloadJson = Map("old", scope: "operator:manual") },
            _ => prior with { PayloadJson = Map("") }
        };
        Assert.Equal("criterion-evidence-map scope=acceptance binding=first",
            Assert.Single(Build([prior, Intent("human", "criterion-evidence-map", Map("current"))]).Intents).ClassKey);
    }

    [Fact]
    public void DispositionsRespectPriorityAndCoverageUsesAgreedOverTotal()
    {
        var humans = new[] { "agreed", "disagreed", "silent", "uncovered" }
            .Select(id => Intent(id, "adjudicate", Adjudication("X")) with { TaskId = id }).ToArray();
        var matching = Typed(humans[0], Adjudication("X"));
        var differing = Typed(humans[1], Adjudication("Y"));
        // An earlier disagreeing observation and silent trigger must not outrank agreement.
        var earlierDisagreement = matching with { Id = "earlier", PayloadJson = Adjudication("Y") };
        var report = Build([.. humans, matching, differing, earlierDisagreement],
            [Trigger("agreed"), Trigger("disagreed"), Trigger("silent")]);
        Assert.Equal(new[] { "agreed", "disagreed", "silent", "uncovered" }, report.Intents.Select(i => i.Disposition));
        var verb = Assert.Single(report.Verbs);
        Assert.Equal((4, 1, 1, 1, 1), (verb.Total, verb.Agreed, verb.Disagreed, verb.Silent, verb.Uncovered));
        Assert.Equal((double)verb.Agreed / verb.Total, verb.Coverage);
        Assert.Equal(0.25, report.Coverage);
    }

    [Theory]
    [InlineData("future-intent")]
    [InlineData("other-task")]
    [InlineData("other-goal")]
    [InlineData("cli-agent")]
    [InlineData("future-trigger")]
    [InlineData("serviced-trigger")]
    public void UnrelatedOrFutureEvidenceLeavesIntentUncovered(string control)
    {
        var human = Intent("human", "adjudicate", Adjudication("X"));
        var typed = Typed(human, human.PayloadJson);
        typed = control switch
        {
            "future-intent" => typed with { CreatedAt = At.AddSeconds(1) },
            "other-task" => typed with { TaskId = "other" },
            "other-goal" => typed with { GoalId = "other" },
            _ => typed with { Channel = "cli" }
        };
        OperatorIntentCoverageTrigger[] triggers = control switch
        {
            "future-trigger" => [Trigger("task") with { FirstOccurrence = At.AddSeconds(1) }],
            "serviced-trigger" => [Trigger("task") with { IntentId = "produced" }],
            _ => []
        };
        Assert.Equal("uncovered", Assert.Single(Build([human, typed], triggers).Intents).Disposition);
    }

    [Fact]
    public void AuthorMatchesPayloadTargetAtTheSameInstantAndNullTaskCannotBeSilent()
    {
        var human = Intent("human", "answer", "{\"targetKind\":2,\"targetId\":\"question\"}") with { TaskId = null };
        var author = Typed(human, "{\"targetKind\":\"HumanInput\",\"targetId\":\"question\"}") with
        { Channel = "conductor-author", TaskId = null, CreatedAt = At };
        var row = Assert.Single(Build([human, author]).Intents);
        Assert.Equal("question", row.Target);
        Assert.Equal("agreed", row.Disposition);
        Assert.Equal("uncovered", Assert.Single(Build([human], [Trigger("question"), Trigger("")]).Intents).Disposition);
        Assert.Equal("task", Assert.Single(Build([human with { TaskId = "task" }]).Intents).Target);
        Assert.Equal("", Assert.Single(Build([human with { TaskId = "" }]).Intents).Target);
        Assert.Equal("", Assert.Single(Build([human with { PayloadJson = "{}" }]).Intents).Target);
    }

    [Fact]
    public void WindowIsHalfOpenAndNullActorCountsOnAnyChannel()
    {
        var atStart = Intent("start", "cancel-dispatch") with { CreatedAt = Since, ActorKind = null, Channel = "other" };
        var before = atStart with { Id = "before", CreatedAt = Since.AddTicks(-1) };
        var atEnd = atStart with { Id = "end", CreatedAt = Until };
        var agent = atStart with { Id = "agent", ActorKind = OperatorActorKind.Agent };
        var report = Build([before, atStart, atEnd, agent]);
        Assert.Equal("start", Assert.Single(report.Intents).Id);
        Assert.Equal(1, report.HumanIntents);
    }

    [Theory]
    [InlineData(30, false, true)]
    [InlineData(29, false, false)]
    [InlineData(30, true, false)]
    public void CandidateRequiresThresholdAndEveryMemberStructural(int count, bool text, bool expected)
    {
        var intents = Enumerable.Range(0, count).Select(i => Intent($"human-{i}", "retry",
            i == 0 && text ? "{\"retryCause\":2,\"message\":\"explain\"}" : "{\"retryCause\":2,\"message\":\"  \"}")).ToArray();
        var row = Assert.Single(Build(intents).Classes);
        Assert.Equal(!text, row.Structural);
        Assert.Equal(count, row.Uncovered);
        Assert.Equal(expected, row.AutomatableCandidate);
        Assert.Equal(!text, Assert.Single(Build(intents, minCases: 5).Classes).AutomatableCandidate);
    }

    [Theory]
    [InlineData("{\"request\":{\"note\":\"explain\"}}", false)]
    [InlineData("{\"rationale\":\"explain\"}", false)]
    [InlineData("{\"request\":{\"deeper\":{\"text\":\"ignored\"}}}", true)]
    [InlineData("{\"array\":[{\"message\":\"ignored\"}]}", true)]
    [InlineData("{\"Text\":\"case-sensitive\",\"detail\":\" \"}", true)]
    [InlineData("{broken", true)]
    public void StructuralInspectsOnlyNamedStringsAtTheSpecifiedDepth(string payload, bool expected)
    {
        Assert.Equal(expected, Assert.Single(Build([Intent("human", "cancel-dispatch", payload)]).Intents).Structural);
    }

    [Fact]
    public void ClassCountsIncludeRejectedAndDistinctGoalsWithDeterministicOrdering()
    {
        var report = Build([
            Intent("a", "lesson-record") with { Status = "rejected" },
            Intent("b", "lesson-record") with { GoalId = "other" },
            Intent("c", "cancel-dispatch"), Intent("d", "answer")]);
        Assert.Equal(new[] { "lesson-record", "answer targetKind=none", "cancel-dispatch" }, report.Classes.Select(c => c.ClassKey));
        var largest = report.Classes[0];
        Assert.Equal((2, 1, 2, 0d), (largest.Total, largest.Rejected, largest.DistinctGoals, largest.Coverage));
        Assert.Equal(new[] { "answer", "cancel-dispatch", "lesson-record" }, report.Verbs.Select(v => v.Verb));
    }

    private static OperatorIntentCoverageResult Build(OperatorIntentCoverageIntent[] intents,
        OperatorIntentCoverageTrigger[]? triggers = null, int minCases = 30) =>
        OperatorIntentCoverageReport.Build(intents, triggers ?? [], Since, Until, minCases);
    private static OperatorIntentCoverageIntent Intent(string id, string verb, string? payload = "{}") =>
        new(id, verb, "goal", "task", payload, "cli", OperatorActorKind.Human, "Applied", At);
    private static OperatorIntentCoverageIntent Typed(OperatorIntentCoverageIntent human, string? payload) =>
        human with { Id = "typed-" + human.Id, PayloadJson = payload, ActorKind = OperatorActorKind.Agent,
            Channel = "conductor-steward", CreatedAt = At.AddMinutes(-1) };
    private static OperatorIntentCoverageTrigger Trigger(string task) =>
        new("goal", task, "PlannerOutputContractRejected", At.AddMinutes(-1), null);
    private static string Adjudication(string cause) => $$"""{"shape":"route","cause":"{{cause}}"}""";
    private static string Map(string sha, int criterion = 1, string scope = "acceptance:full-gate") =>
        $$"""{"criterionIndex":{{criterion}},"requiredScope":"{{scope}}","candidateSha":"{{sha}}"}""";
}
