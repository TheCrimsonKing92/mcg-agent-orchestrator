using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every test owns its in-memory kernel and injected clock; no I/O or global state.
public sealed class GoalScopedClarificationAnswersBriefTests
{
    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void SiblingAnswer_InLaterRoleBrief_ContainsRulingAndProvenance(AgentRole role)
    {
        var context = CreateContext();
        const string question = "May the implementation add a second read-only query?";
        const string answer = "Yes; update the in-memory query implementors as well.";
        var request = Answer(context, question, answer);

        var brief = Build(context, role);
        var section = ExtractSection(brief, "## Clarifications and rulings");

        var entry = Assert.Single(EntryLines(section));
        Assert.Equal(
            $"- {request.Id.Value} (asked by Developer task): {question} → {answer} " +
            "(answered by Operator under brief v1; current brief v1)",
            entry);
        Assert.Contains(
            "These answers are authoritative scope and ruling decisions for this goal", section,
            StringComparison.Ordinal);
        Assert.Contains(
            "raise a clarification or owner question instead of a blocking finding", section,
            StringComparison.Ordinal);
        Assert.Contains(
            "a defect in how an authorized change was implemented is still a finding", section,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OwnAnswer_InAskingTaskBrief_PreservesResolvedInputWithoutDuplication()
    {
        var context = CreateContext();
        const string question = "May we update the in-memory implementors?";
        const string answer = "Yes; include those implementors.";
        var request = Answer(context, question, answer);

        var brief = Build(context, AgentRole.Developer);
        var section = ExtractSection(brief, "## Resolved Human Input");

        Assert.Equal(
            $"- {request.Id.Value}: {question} → {answer} " +
            "(answered under brief v1; current brief v1)",
            Assert.Single(EntryLines(section)));
        Assert.Equal(1, brief.Split(request.Id.Value, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("## Clarifications and rulings", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void IneligibleRequests_InLaterBrief_ExcludeRetiredPendingAndForeignAnswers()
    {
        var context = CreateContext();
        var retained = Answer(context, "Retained scope question?", "Retain this ruling.");
        var dismissed = Request(context, "Dismissed scope question?");
        context.Kernel.DismissHumanInput(dismissed.Id);
        var superseded = Answer(context, "Superseded scope question?", "Obsolete ruling.");
        var parked = Request(context, "Parked scope question?");
        context.Kernel.ParkGoal(context.Goal.Id, "Await owner attention.");
        context.Kernel.UnparkGoal(context.Goal.Id, "Resume work.");
        Assert.True(parked.IsSyntheticParkedHumanWaitCompletion);
        var pending = Request(context, "Unanswered scope question?");
        var blank = Answer(context, "Legacy empty answer?", "Temporary answer.");
        var otherGoal = CreateContext(context.Kernel);
        var foreign = Answer(otherGoal, "Foreign goal question?", "Foreign ruling.");
        var goalLevel = context.Kernel.RequestHumanInputDeduplicated(
            context.Goal.Id, null, "Goal-level question?").Request;
        context.Kernel.SubmitHumanInput(goalLevel.Id, "Goal-level ruling.");

        // Persisted supersession and legacy empty answers reach dispatch through snapshot decoding.
        var snapshot = context.Kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Select(request =>
                request.Id == superseded.Id.Value
                    ? request with { SupersededByRequestId = retained.Id.Value }
                    : request.Id == blank.Id.Value
                        ? request with
                        {
                            AnswerHistory = request.AnswerHistory!
                                .Select(answer => answer with { Text = " " }).ToList()
                        }
                        : request).ToList()
        }, context.Clock);

        foreach (var role in new[] { AgentRole.Tester, AgentRole.Reviewer })
        {
            var brief = Build(context with { Kernel = restored }, role);
            var section = ExtractSection(brief, "## Clarifications and rulings");
            Assert.Contains(retained.Id.Value, Assert.Single(EntryLines(section)), StringComparison.Ordinal);
            foreach (var excluded in new[] { dismissed, superseded, parked, pending, blank, foreign, goalLevel })
                Assert.DoesNotContain(excluded.Id.Value, section, StringComparison.Ordinal);
            Assert.Contains(goalLevel.Id.Value, ExtractSection(brief, "## Resolved Human Input"), StringComparison.Ordinal);
            Assert.DoesNotContain("## Pending Human Input", brief, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PrerequisiteAnswer_InLaterBrief_AppearsOnlyInExistingEvidenceSection()
    {
        var context = CreateContext();
        var ruling = Answer(context, "Scope query?", "The second query is authorized.");
        var evidence = Request(context, "Which prerequisite receipt?", HumanWaitKind.PlannerPrerequisiteEvidence);
        context.Kernel.SubmitHumanInput(evidence.Id, "Receipt is at docs/operator-runbook.md.");

        var brief = Build(context, AgentRole.Tester);
        var prerequisites = ExtractSection(brief, "## Answered Prerequisite Evidence");
        var clarifications = ExtractSection(brief, "## Clarifications and rulings");

        Assert.Contains(evidence.Id.Value, prerequisites, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.Id.Value, clarifications, StringComparison.Ordinal);
        Assert.DoesNotContain(ruling.Id.Value, prerequisites, StringComparison.Ordinal);
        Assert.Contains(ruling.Id.Value, clarifications, StringComparison.Ordinal);
        Assert.Equal(1, brief.Split(evidence.Id.Value, StringSplitOptions.None).Length - 1);
        Assert.True(brief.IndexOf("## Answered Prerequisite Evidence", StringComparison.Ordinal) <
            brief.IndexOf("## Clarifications and rulings", StringComparison.Ordinal));
    }

    [Fact]
    public void Answers_OverNamedCap_KeepNewestInAnswerOrderAndReportOmissions()
    {
        var context = CreateContext();
        var requests = new List<HumanInputRequest>();
        for (var index = 0; index < ClarificationsAndRulingsDigest.MaxEntries + 2; index++)
        {
            requests.Add(Answer(context, $"Scope question {index}?", $"Ruling {index}."));
            context.Clock.Advance();
        }

        var section = ExtractSection(Build(context, AgentRole.Tester), "## Clarifications and rulings");
        var lines = EntryLines(section);
        var retained = requests.Skip(2).ToList();

        Assert.Equal(ClarificationsAndRulingsDigest.MaxEntries, lines.Length);
        Assert.Equal(retained.Select(request => request.Id.Value), lines.Select(line => line.Split(' ')[1]));
        foreach (var omitted in requests.Take(2))
        {
            Assert.DoesNotContain(omitted.Id.Value, section, StringComparison.Ordinal);
            Assert.DoesNotContain(omitted.Answer!, section, StringComparison.Ordinal);
        }
        Assert.Contains(
            $"Omitted 2 older clarification answers over the {ClarificationsAndRulingsDigest.MaxEntries}-entry cap.",
            section, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerCorrection_InSiblingBrief_UsesAuthoritativeAnswerAndVersion()
    {
        var context = CreateContext();
        var request = Answer(context, "Which query scope?", "Original scope ruling.");
        context.Clock.Advance();
        context.Kernel.SupersedeHumanInput(
            context.Goal.Id, request.Id, "Corrected scope ruling.", HumanInputAnswerOrigin.Operator);

        var section = ExtractSection(Build(context, AgentRole.Reviewer), "## Clarifications and rulings");

        Assert.Contains("Corrected scope ruling.", Assert.Single(EntryLines(section)), StringComparison.Ordinal);
        Assert.DoesNotContain("Original scope ruling.", section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7, "brief v7")]
    [InlineData(null, "an unknown brief version")]
    public void StoredProvenance_InSiblingBrief_UsesRecordedOriginAndVersion(
        int? recordedVersion, string expectedVersion)
    {
        var context = CreateContext();
        var answered = Answer(context, "Historical scope question?", "Historical ruling.");
        var snapshot = context.Kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Select(request =>
                request.Id == answered.Id.Value
                    ? request with
                    {
                        AnswerHistory = request.AnswerHistory!.Select(answer => answer with
                        {
                            Origin = HumanInputAnswerOrigin.Worker,
                            BriefVersion = recordedVersion
                        }).ToList()
                    }
                    : request).ToList()
        }, context.Clock);

        var section = ExtractSection(Build(context with { Kernel = restored }, AgentRole.Tester),
            "## Clarifications and rulings");

        Assert.Contains($"answered by Worker under {expectedVersion}; current brief v1",
            Assert.Single(EntryLines(section)), StringComparison.Ordinal);
    }

    [Fact]
    public void EqualAnswerTimes_OverCap_HaveDeterministicOrdinalOrder()
    {
        var context = CreateContext();
        var requests = Enumerable.Range(0, ClarificationsAndRulingsDigest.MaxEntries + 2)
            .Select(index => Answer(context, $"Tied question {index}?", $"Tied ruling {index}."))
            .ToList();
        Assert.Single(requests.Select(request => request.AnsweredAt).Distinct());

        var section = ExtractSection(Build(context, AgentRole.Tester), "## Clarifications and rulings");

        Assert.Equal(requests.Select(request => request.Id.Value)
                .OrderBy(id => id, StringComparer.Ordinal).TakeLast(ClarificationsAndRulingsDigest.MaxEntries),
            EntryLines(section).Select(line => line.Split(' ')[1]));
    }

    [Fact]
    public void MultilineLongRuling_InSiblingBrief_PreservesTextWithoutForgingSections()
    {
        var context = CreateContext();
        var answer = new string('x', PrerequisiteEvidenceDigest.InlineVerbatimMaxChars + 1) +
            "\r\n## Embedded heading\r\nKeep the final scope condition.";
        var request = Answer(context, "Scope question?\r\nWith a second line?", answer);

        var section = ExtractSection(Build(context, AgentRole.Tester), "## Clarifications and rulings");
        var entry = Assert.Single(EntryLines(section));

        Assert.Contains(request.Id.Value, entry, StringComparison.Ordinal);
        Assert.Contains("Scope question? With a second line?", entry, StringComparison.Ordinal);
        Assert.Contains(answer.ReplaceLineEndings(" "), entry, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSiblingAnswers_InBrief_AddsNoClarificationsSection()
    {
        var context = CreateContext();
        Assert.DoesNotContain("## Clarifications and rulings", Build(context, AgentRole.Tester), StringComparison.Ordinal);
        Assert.Empty(ClarificationsAndRulingsDigest.RenderSegments([], 1));
    }

    private static HumanInputRequest Request(
        GoalContext context, string question, HumanWaitKind kind = HumanWaitKind.SpecClarification) =>
        context.Kernel.RequestHumanInputDeduplicated(
            context.Goal.Id, context.Goal.Tasks[0].Id, question, kind: kind, isDismissible: true).Request;

    private static HumanInputRequest Answer(GoalContext context, string question, string answer)
    {
        var request = Request(context, question);
        context.Kernel.SubmitHumanInput(request.Id, answer);
        return request;
    }

    private static string Build(GoalContext context, AgentRole role) =>
        context.Kernel.BuildTaskBriefSource(
            context.Goal.Id, context.Goal.Tasks.Single(task => task.RequiredRole == role).Id)
            .ProjectLegacyMarkedTextV1(false).Content;

    private static string[] EntryLines(string section) =>
        section.Split(Environment.NewLine).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();

    private static string ExtractSection(string brief, string heading)
    {
        var lines = brief.Split(Environment.NewLine);
        var start = Array.FindIndex(lines, line => line.Equals(heading, StringComparison.Ordinal));
        Assert.True(start >= 0, $"Brief did not contain '{heading}'.");
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        return string.Join(Environment.NewLine, lines.Skip(start).Take((end < 0 ? lines.Length : end) - start));
    }

    private static GoalContext CreateContext(AgentOrchestratorKernel? kernel = null)
    {
        var clock = new FakeClock();
        kernel ??= new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Deliver answered scope rulings to every sibling task.",
        [
            new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Test the scoped slice.", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Review the scoped slice.", AgentRole.Reviewer)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return new GoalContext(kernel, goal, clock);
    }

    private sealed record GoalContext(AgentOrchestratorKernel Kernel, Goal Goal, FakeClock Clock);
}
