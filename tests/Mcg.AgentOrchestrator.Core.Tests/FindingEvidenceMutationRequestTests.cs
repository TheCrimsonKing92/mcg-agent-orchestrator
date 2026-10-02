using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class FindingEvidenceMutationRequestTests
{
    private static readonly FindingEvidenceMutation Mutation = new("src/A.cs", "return true;", "return false;");

    [Xunit.Fact]
    public void Parse_Mutation_RoundTrips()
    {
        var finding = Parse(MutationOptions(Mutation));
        Assert.Equal(Mutation, finding.EvidenceRequest!.Mutation);
        var json = JsonSerializer.Serialize(new[] { finding });
        Assert.Contains("\"mutation\":{\"path\":\"src/A.cs\",\"old_text\":\"return true;\",\"new_text\":\"return false;\"}", json, StringComparison.Ordinal);
        Assert.True(ReviewFindingConvergence.TryParseJson(json, "[]", out var replay, out var diagnostic), diagnostic);
        Assert.Equal(Mutation, Assert.Single(replay.Findings).EvidenceRequest!.Mutation);
    }

    [Xunit.Theory]
    [Xunit.InlineData(",\"revert_paths\":[]", "mutation and revert_paths are mutually exclusive")]
    [Xunit.InlineData(",\"revert_paths\":[\"src/A.cs\"]", "mutation and revert_paths are mutually exclusive")]
    [Xunit.InlineData("no-control", "mutation requires negative_control 'revert-src'")]
    public void Parse_InvalidCombination_RejectsBeforeExecution(string option, string expected)
    {
        var options = option == "no-control" ? ",\"mutation\":" + JsonSerializer.Serialize(Mutation) : MutationOptions(Mutation) + option;
        Assert.False(ReviewFindingConvergence.TryParseJson(Findings(options), "[]", out _, out var diagnostic));
        Assert.Contains(expected, diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("1")]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("\"src/A.cs\"")]
    public void Parse_NonObjectMutation_Rejects(string value)
    {
        Assert.False(ReviewFindingConvergence.TryParseJson(Findings(",\"negative_control\":\"revert-src\",\"mutation\":" + value), "[]", out _, out _));
    }

    [Xunit.Fact]
    public void Identity_MutationFields_DistinguishRequestsWithoutChangingLegacy()
    {
        var request = Parse(",\"negative_control\":\"revert-src\",\"mutation\":null").EvidenceRequest!;
        Assert.Null(request.Mutation);
        Assert.Equal("Core.Tests:ProbeTests|negative_control=revert-src", FindingEvidenceExecutionClassifier.BuildRequestIdentity(request));
        Assert.DoesNotContain("mutation", JsonSerializer.Serialize(request), StringComparison.Ordinal);
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with { Mutation = Mutation });
        Assert.Equal(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with { Mutation = Mutation with { } }));
        foreach (var changed in new[]
        {
            Mutation with { Path = "src/B.cs" }, Mutation with { OldText = "return 1;" }, Mutation with { NewText = "return 0;" },
            Mutation with { OldText = "return true;\n" }, Mutation with { NewText = "" }
        })
            Assert.NotEqual(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with { Mutation = changed }));
        Assert.NotEqual(identity, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request));
        Assert.Contains("|mutation=[", identity, StringComparison.Ordinal);
        Assert.NotEqual(FindingEvidenceMutation.IdentityJson(new("src/A.cs", "a,b", "c")),
            FindingEvidenceMutation.IdentityJson(new("src/A.cs", "a", "b,c")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Merge_ChangedMutation_DropsPriorOutcome(bool equivalent)
    {
        var finding = Parse(MutationOptions(Mutation));
        var outcome = new FindingEvidenceOutcome(true, "receipt");
        var next = finding with { EvidenceRequest = finding.EvidenceRequest! with
        { Mutation = equivalent ? Mutation with { } : Mutation with { NewText = "return 0;" } } };
        var merged = Assert.Single(ReviewFindingConvergence.ApplyRound([finding with { EvidenceOutcome = outcome }], new ReviewFindingRound([next], [])));
        if (equivalent) Assert.Equal(outcome, merged.EvidenceOutcome);
        else Assert.Null(merged.EvidenceOutcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Classify_PriorReceipt_CoversOnlyMatchingMutation(bool equivalent)
    {
        var finding = Parse(MutationOptions(Mutation));
        var request = finding.EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request with
        { Mutation = equivalent ? Mutation with { } : Mutation with { NewText = "return 0;" } }, true, true, "green",
            RequestDispositions: [new FindingEvidenceRequestDisposition(finding.StableId,
                FindingEvidenceExecutionClassifier.BuildRequestIdentity(request), "executed-standalone")],
            NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.Demonstrated);
        var task = new TaskSpec(TaskId.New(), "Verify mutation", AgentRole.Tester);
        task.RecordVerification(new TaskVerificationRecord("focused evidence", "worktree", 0, "ok", "",
            DateTimeOffset.UtcNow, FindingEvidenceReceipts: [receipt]));
        Assert.Equal(equivalent ? FindingEvidenceExecutionState.ExecutedOnCandidate : FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, finding, "sha"));
    }

    [Xunit.Fact]
    public void Receipt_Mutation_RecordsPathAndTwelveCharacterHashes()
    {
        var request = Parse(MutationOptions(Mutation)).EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "sha", request, true, true, "green");
        Assert.Equal("src/A.cs", receipt.MutationPath);
        Assert.Equal("ba7816bf8f01", FindingEvidenceMutation.ShortHash("abc"));
        Assert.Equal(FindingEvidenceMutation.ShortHash(Mutation.OldText), receipt.MutationOldTextHash);
        Assert.Equal(FindingEvidenceMutation.ShortHash(Mutation.NewText), receipt.MutationNewTextHash);
        Assert.Matches("^[0-9a-f]{12}$", receipt.MutationOldTextHash!);
        Assert.Matches("^[0-9a-f]{12}$", receipt.MutationNewTextHash!);
        var json = JsonSerializer.Serialize(receipt);
        Assert.Contains("\"MutationPath\":\"src/A.cs\"", json, StringComparison.Ordinal);
        var replay = JsonSerializer.Deserialize<FindingEvidenceReceipt>(json)!;
        Assert.Equal(receipt.MutationOldTextHash, replay.MutationOldTextHash);
        Assert.Equal(receipt.MutationNewTextHash, replay.MutationNewTextHash);
        var legacy = JsonSerializer.Serialize(receipt with { Request = request with { Mutation = null } });
        Assert.DoesNotContain("Mutation", legacy, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Parse_DeletionAndMultilineText_PreservesExactText()
    {
        var mutation = Mutation with { OldText = "first\r\nsecond\n", NewText = "" };
        Assert.Equal(mutation, Parse(MutationOptions(mutation)).EvidenceRequest!.Mutation);
    }

    [Xunit.Theory]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationUnderTests, "mutation-under-tests")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationOutsideSrc, "mutation-outside-src")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal, "mutation-not-changed")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationEmptyOldText, "mutation-old-text-empty")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationUnchangedText, "mutation-old-text-equals-new-text")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationOldTextNotFound, "mutation-old-text-missing")]
    [Xunit.InlineData(FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous, "mutation-old-text-ambiguous")]
    public void Receipt_MutationRejection_RoundTripsTypedReason(FindingEvidenceRevertPathsRejection reason, string wire)
    {
        var receipt = new FindingEvidenceReceipt("receipt", "sha", Parse(MutationOptions(Mutation)).EvidenceRequest!, true, true, wire,
            NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.Inconclusive, RevertPathsRejection: reason);
        var json = JsonSerializer.Serialize(receipt);
        Assert.Contains(wire, json, StringComparison.Ordinal);
        Assert.Equal(reason, JsonSerializer.Deserialize<FindingEvidenceReceipt>(json)!.RevertPathsRejection);
    }

    private static string MutationOptions(FindingEvidenceMutation mutation) =>
        ",\"negative_control\":\"revert-src\",\"mutation\":" + JsonSerializer.Serialize(mutation);

    private static ReviewFinding Parse(string option)
    {
        Assert.True(ReviewFindingConvergence.TryParseJson(Findings(option), "[]", out var round, out var diagnostic), diagnostic);
        return Assert.Single(round.Findings);
    }

    private static string Findings(string option) =>
        "[{\"stable_id\":\"control\",\"state\":\"open\",\"location\":{\"file\":\"src/A.cs\",\"region\":\"A.Run\"}," +
        "\"description\":\"Prove test sensitivity\",\"evidence_request\":{\"selections\":[{\"test_project\":\"Core.Tests\",\"test_class\":\"ProbeTests\"}]" + option + "}}]";
}
