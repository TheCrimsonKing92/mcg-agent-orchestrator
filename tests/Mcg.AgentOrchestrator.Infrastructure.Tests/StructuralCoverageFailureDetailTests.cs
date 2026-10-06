using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure formatting, with a unique temporary directory for the TRX fixture.
public sealed class StructuralCoverageFailureDetailTests
{
    private const string Meaning = "meaning: each listed test exists and was discovered but no shard executed it; adding new tests cannot clear this";
    private const string ReceiptMeaning = "meaning: the candidate's discovered test count or main baseline generation does not reconcile; the receipts below give the counts";
    private const string CountReceipt = "cross-generation-count:candidate=10,minimum=12,main=12,deleted=0";

    [Xunit.Fact]
    public void Format_OnlyCountReceipt_ExplainsReconciliationWithoutTestClaim()
    {
        var coverage = Coverage(missing: [CountReceipt]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary,
            ReceiptMeaning, $"receipt: {CountReceipt}"
        }, lines);
        Assert.DoesNotContain(Meaning, lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("missing test: ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("omitted:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Format_TestAndStalenessReceipt_ExplainsBothInOrder()
    {
        const string receipt = "cross-generation-staleness:reason=main-generation-changed";
        var coverage = Coverage(missing: [receipt, "Fixture.Cases.Missing"]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary,
            Meaning, ReceiptMeaning, $"receipt: {receipt}",
            "missing test: Fixture.Cases.Missing"
        }, lines);
    }

    [Xunit.Fact]
    public void Format_TwelveReceipts_ListsFirstTenAndTwoOmitted()
    {
        var receipts = Enumerable.Range(1, 12).Reverse()
            .Select(i => $"cross-generation-staleness:reason=generation-{i:00}").ToArray();
        var coverage = Coverage(missing: receipts);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        var expected = new List<string>
        {
            "classification: structural-coverage-failed", coverage.Summary, ReceiptMeaning
        };
        expected.AddRange(receipts.Take(10).Select(receipt => $"receipt: {receipt}"));
        expected.Add("omitted: 2 more receipts not listed");
        Assert.Equal(expected, lines);
        Assert.Equal("omitted: 2 more receipts not listed",
            Assert.Single(lines.Where(line => line.StartsWith("omitted:", StringComparison.Ordinal))));
    }

    [Xunit.Fact]
    public void Format_CountShortfallResult_OmitsUnexecutedTestClaim()
    {
        // All candidate tests executed; only the comparison with main failed.
        var coverage = Coverage(missing: [CountReceipt]) with
        {
            ExecutedTests = Enumerable.Range(1, 10).Select(i => $"Fixture.Cases.Case{i:00}").ToArray(),
            IdentityMismatches = [],
            CountComparison = new TestCoverageCountComparison(10, 12, 12, 0, true)
        };

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary,
            ReceiptMeaning, $"receipt: {CountReceipt}"
        }, lines);
        Assert.DoesNotContain(Meaning, lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("missing test: ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Format_AllKinds_UsesIndependentCapsAndPreservesRepeatedEntries()
    {
        var mismatch = new TestCoverageIdentityMismatch("Fixture.Cases.Mismatch", null);
        var coverage = Coverage(
            missing: [mismatch.Discovered.ToUpperInvariant(), CountReceipt, CountReceipt,
                "Fixture.Cases.Missing", "Fixture.Cases.Missing"],
            empty: ["partition-one", "partition-two"],
            mismatches: [mismatch, new TestCoverageIdentityMismatch("Fixture.Cases.Other", null)]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 1);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary, Meaning, ReceiptMeaning,
            "empty partition: partition-one", "omitted: 1 more empty partitions not listed",
            "missing test: discovered=\"Fixture.Cases.Mismatch\"; executed=null",
            "omitted: 1 more identity mismatches not listed",
            $"receipt: {CountReceipt}", "omitted: 1 more receipts not listed",
            "missing test: Fixture.Cases.Missing", "omitted: 1 more missing tests not listed"
        }, lines);
    }

    [Xunit.Fact]
    public void Format_OneMissingTest_ExplainsFailureWithoutOmissions()
    {
        var coverage = Coverage(missing: ["Fixture.Cases.OnlyMissing"]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed",
            coverage.Summary,
            Meaning,
            "missing test: Fixture.Cases.OnlyMissing"
        }, lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("omitted:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Format_TwentyFiveMissingTests_ListsFirstTenAndFifteenOmitted()
    {
        var missing = Enumerable.Range(1, 25).Reverse().Select(i => $"Fixture.Cases.Missing{i:00}").ToArray();
        var coverage = Coverage(missing: missing);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        Assert.Equal(missing.Take(10).Select(name => $"missing test: {name}"),
            lines.Where(line => line.StartsWith("missing test: ", StringComparison.Ordinal)));
        Assert.Equal("omitted: 15 more missing tests not listed",
            Assert.Single(lines.Where(line => line.StartsWith("omitted:", StringComparison.Ordinal))));
        Assert.Equal("omitted: 15 more missing tests not listed", lines[^1]);
    }

    [Xunit.Fact]
    public void Format_ThreeCutLists_ReportsOrderedEntriesAndExactTails()
    {
        // Descending names make sorting observable. The overlapping name differs only in case.
        var missing = Enumerable.Range(1, 25).Reverse().Select(i => $"Fixture.Cases.Missing{i:00}").ToArray();
        var partitions = Enumerable.Range(1, 13).Reverse().Select(i => $"partition-{i:00}").ToArray();
        var mismatches = Enumerable.Range(1, 12).Reverse()
            .Select(i => new TestCoverageIdentityMismatch($"Fixture.Cases.Mismatch{i:00}", $"Fixture.Cases.Other{i:00}"))
            .ToArray();
        var coverage = Coverage(
            missing: [mismatches[0].Discovered.ToUpperInvariant(), .. missing],
            empty: partitions,
            mismatches: mismatches);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        var expected = new List<string>
        {
            "classification: structural-coverage-failed",
            coverage.Summary,
            Meaning
        };
        expected.AddRange(partitions.Take(10).Select(name => $"empty partition: {name}"));
        expected.Add("omitted: 3 more empty partitions not listed");
        expected.AddRange(mismatches.Take(10).Select(mismatch =>
            $"missing test: discovered=\"{mismatch.Discovered}\"; executed=\"{mismatch.Executed}\""));
        expected.Add("omitted: 2 more identity mismatches not listed");
        expected.AddRange(missing.Take(10).Select(name => $"missing test: {name}"));
        expected.Add("omitted: 15 more missing tests not listed");
        Assert.Equal(expected, lines);
        Assert.DoesNotContain($"missing test: {mismatches[0].Discovered.ToUpperInvariant()}", lines);
        Assert.Equal(3, lines.Count(line => line.StartsWith("omitted:", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void Format_ExactlyAtCap_HasNoOmittedLine()
    {
        var coverage = Coverage(
            missing: ["Fixture.Cases.Missing"],
            empty: ["partition"],
            mismatches: [new TestCoverageIdentityMismatch("Fixture.Cases.Mismatch", null)]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 1);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary, Meaning,
            "empty partition: partition",
            "missing test: discovered=\"Fixture.Cases.Mismatch\"; executed=null",
            "missing test: Fixture.Cases.Missing"
        }, lines);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void Format_NonPositiveCap_ReportsEntireListsAsOmitted(int limit)
    {
        var coverage = Coverage(
            missing: ["Fixture.Cases.Missing"],
            empty: ["partition"],
            mismatches: [new TestCoverageIdentityMismatch("Fixture.Cases.Mismatch", null)]);

        var lines = StructuralCoverageFailureDetail.Format(coverage, limit);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary, Meaning,
            "omitted: 1 more empty partitions not listed",
            "omitted: 1 more identity mismatches not listed",
            "omitted: 1 more missing tests not listed"
        }, lines);
    }

    [Xunit.Fact]
    public void Format_OnlyEmptyPartitions_OmitsTestMeaning()
    {
        var coverage = Coverage(empty: ["partition"]);

        Assert.Equal(new[]
        {
            "classification: structural-coverage-failed", coverage.Summary,
            "empty partition: partition"
        }, StructuralCoverageFailureDetail.Format(coverage, 10));
    }

    [Xunit.Fact]
    public void Format_EvaluatedTrx_ListsTenUnexecutedTestsAndOneOmittedMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-coverage-detail-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var trx = Path.Combine(root, "coverage.trx");
            File.WriteAllText(trx, """
                <TestRun>
                  <TestDefinitions>
                    <UnitTest id="1" name="Case01">
                      <TestMethod className="Fixture.CoverageCases" name="Case01" />
                    </UnitTest>
                  </TestDefinitions>
                  <Results>
                    <UnitTestResult testId="1" testName="Case01" outcome="Passed" />
                  </Results>
                </TestRun>
                """);
            var discovered = Enumerable.Range(1, 12)
                .Select(i => $"Fixture.CoverageCases.Case{i:00}").ToArray();
            var coverage = TestCoverageInvariant.Evaluate(
                discovered, [new TestPartitionCoverage("partition", true, [trx])]);

            Assert.False(coverage.Passed);
            Assert.Equal(new[] { discovered[0] }, coverage.ExecutedTests);
            Assert.Equal(discovered.Skip(1), coverage.MissingTests);
            Assert.Empty(coverage.EmptyPartitions);
            var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

            var expected = new List<string>
            {
                "classification: structural-coverage-failed",
                "structural coverage failed: discovered=12, executed=1, recorded=1, missing=11, emptyPartitions=0",
                Meaning
            };
            expected.AddRange(discovered.Skip(1).Take(10).Select(name =>
                $"missing test: discovered=\"{name}\"; executed=null"));
            expected.Add("omitted: 1 more identity mismatches not listed");
            Assert.Equal(expected, lines);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TestCoverageInvariantResult Coverage(
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? empty = null,
        IReadOnlyList<TestCoverageIdentityMismatch>? mismatches = null) =>
        new(false, "coverage summary passed through verbatim", missing ?? [], empty ?? [],
            "structural-coverage-failed", [], mismatches);
}
