using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class ConductorStewardAdjudicationEvidenceNotesTests
{
    private const string Diagnosis = "Correct the Planner citations";
    private const string Instruction = "Use exact tracked paths";

    [Fact]
    public void Planner_rejection_note_moves_to_diagnosis_and_route_payload()
    {
        using var temp = new TempDirectory();
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var trigger = Trigger(goal);
        const string rule = "planner-rule=planner-output-contract-rejected";
        const string candidate = "candidate-sha=0123456789abcdef0123456789abcdef01234567";
        const string note = "rejection: target citation '*.cs' does not exist";

        var result = Parse(trigger, [rule, candidate, note]);
        var resolver = new AdjudicationEvidenceResolver();

        Assert.Equal(new[] { rule, candidate }, result.EvidenceReferences);
        Assert.Equal(Diagnosis + "\nSteward notes:\n" + note, result.Text);
        Assert.Null(ConductorStewardRoutePolicy.RejectionReason(trigger, result, goal, temp.Path, resolver));
        var feedback = ConductorStewardRetryTemplate.Compose(trigger, result);
        Assert.NotNull(feedback);
        var payload = new AdjudicateOperatorIntentPayload("route", feedback!,
            result.EvidenceReferences!, 0, temp.Path);
        Assert.Equal(new[] { rule, candidate }, payload.EvidenceReferences);
        Assert.Contains("Steward diagnosis:" + Environment.NewLine + result.Text + Environment.NewLine,
            payload.Text);
        Assert.All(payload.EvidenceReferences, reference =>
            Assert.True(resolver.TryResolve(reference, goal, payload, out _)));
    }

    [Theory]
    [InlineData("trx:missing/path.trx")]
    [InlineData("rejection:no-space-after-colon")]
    [InlineData("rejection:")]
    public void Unresolved_references_still_reject_the_route(string reference)
    {
        using var temp = new TempDirectory();
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var trigger = Trigger(goal);
        var result = Parse(trigger, ["bundle: this is a note", reference]);

        Assert.Equal(new[] { reference }, result.EvidenceReferences);
        Assert.Equal("evidence-reference-unresolved", ConductorStewardRoutePolicy.RejectionReason(
            trigger, result, goal, temp.Path, new AdjudicationEvidenceResolver()));
    }

    [Fact]
    public void Existing_operator_evidence_file_is_resolved_as_evidence()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "operator.txt"), "Verified citations");
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var trigger = Trigger(goal);
        const string reference = "operator-evidence:operator.txt";
        var result = Parse(trigger, [reference]);
        var resolver = new AdjudicationEvidenceResolver();
        var payload = new AdjudicateOperatorIntentPayload("route", result.Text,
            result.EvidenceReferences!, 0, temp.Path);

        Assert.Equal(new[] { reference }, result.EvidenceReferences);
        Assert.Equal(Diagnosis, result.Text);
        Assert.True(resolver.TryResolve(reference, goal, payload, out var entry));
        Assert.Equal(reference, entry.ReceiptId);
        Assert.Null(ConductorStewardRoutePolicy.RejectionReason(trigger, result, goal, temp.Path, resolver));
    }

    [Theory]
    [InlineData("trx: missing.trx")]
    [InlineData("TRX:\tmissing.trx")]
    [InlineData("operator-evidence: missing.txt")]
    [InlineData("OPERATOR-EVIDENCE: missing.txt")]
    [InlineData("focused-evidence: missing-pointer")]
    [InlineData("FOCUSED-EVIDENCE: missing-pointer")]
    [InlineData("acceptance-attempt: missing-attempt")]
    [InlineData("ACCEPTANCE-ATTEMPT: missing-attempt")]
    public void Known_kinds_with_whitespace_remain_subject_to_resolution(string reference)
    {
        using var temp = new TempDirectory();
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var trigger = Trigger(goal);
        var result = Parse(trigger, [reference]);

        Assert.Equal(new[] { reference }, result.EvidenceReferences);
        Assert.Equal(Diagnosis, result.Text);
        Assert.Equal("evidence-reference-unresolved", ConductorStewardRoutePolicy.RejectionReason(
            trigger, result, goal, temp.Path, new AdjudicationEvidenceResolver()));
    }

    [Theory]
    [InlineData("a: text")]
    [InlineData("bundle-2: text")]
    [InlineData("123: text")]
    [InlineData("rejection:\ttext")]
    [InlineData("rejection:\ntext")]
    public void Unknown_ASCII_word_and_whitespace_become_verbatim_notes(string note)
    {
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var result = Parse(Trigger(goal), [note]);

        Assert.Empty(result.EvidenceReferences!);
        Assert.Equal(Diagnosis + "\nSteward notes:\n" + note, result.Text);
    }

    [Theory]
    [InlineData("planner-rule=planner-output-contract-rejected")]
    [InlineData("bare-receipt")]
    [InlineData("two words: text")]
    [InlineData("note.name: text")]
    [InlineData("note/name: text")]
    [InlineData("note_name: text")]
    [InlineData("réjection: text")]
    [InlineData(": text")]
    [InlineData(" note: text")]
    public void Other_reference_forms_and_diagnosis_stay_unchanged(string reference)
    {
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var result = Parse(Trigger(goal), [reference]);

        Assert.Equal(new[] { reference }, result.EvidenceReferences);
        Assert.Equal(Diagnosis, result.Text);
    }

    [Theory]
    [InlineData("route", "text")]
    [InlineData("close", "text")]
    [InlineData("ask-owner", "question")]
    [InlineData("no-action", "reason")]
    [InlineData("reopen-regate", "text")]
    [InlineData("verify-manual", "text")]
    public void Notes_preserve_order_under_one_header_for_every_model_kind(string kind, string textField)
    {
        var fields = new Dictionary<string, object>
        {
            ["kind"] = kind,
            [textField] = Diagnosis,
            ["evidenceReferences"] = new[] { "bundle: first note", "receipt=1", "rejection: second note" }
        };

        var result = ConductorStewardAdjudicationParser.Parse(JsonSerializer.Serialize(fields));

        Assert.Equal(kind, result.Kind);
        Assert.Equal(new[] { "receipt=1" }, result.EvidenceReferences);
        Assert.Equal(Diagnosis + "\nSteward notes:\nbundle: first note\nrejection: second note", result.Text);
    }

    private static ConductorStewardAdjudication Parse(ConductorStewardTrigger trigger, string[] references) =>
        ConductorStewardAdjudicationParser.Parse(JsonSerializer.Serialize(new
        {
            kind = "route", targetTaskId = trigger.TaskId, cause = "ContractClarification",
            reversibility = "reversible", text = Diagnosis, instruction = Instruction,
            evidenceReferences = references
        }));

    private static ConductorStewardTrigger Trigger(Goal goal) => new(
        goal.Id.Value, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Planner).Id.Value,
        "0123456789abcdef0123456789abcdef01234567", ConductorStewardTriggerKind.PlannerOutputContractRejected,
        new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero), "Planner contract rejected", "", [], ["Fix citations"]);

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcg-steward-evidence-notes", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
