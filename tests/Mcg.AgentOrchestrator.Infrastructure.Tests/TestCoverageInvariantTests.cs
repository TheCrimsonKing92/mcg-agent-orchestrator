using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestCoverageInvariantTests
{
    [Xunit.Fact(DisplayName = "TestCoverageInvariant_fails_when_a_discovered_test_was_filtered_out")]
    public void TestCoverageInvariantFailsWhenDiscoveredTestWasFilteredOut()
    {
        var trx = WriteTrx(("1", "ExampleTests.Runs", "Passed"));
        try
        {
            var discovered = TestCoverageInvariant.ParseDiscoveredTests(
                "The following Tests are available:\n  ExampleTests.Runs\n  ExampleTests.WasFilteredOut");

            var result = TestCoverageInvariant.Evaluate(
                discovered,
                [new TestPartitionCoverage("lane", true, [trx])]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains("ExampleTests.WasFilteredOut", result.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_fails_closed_for_zero_test_or_skipped_partition")]
    public void TestCoverageInvariantFailsClosedForZeroTestOrSkippedPartition()
    {
        var emptyTrx = WriteTrx();
        var skippedTrx = WriteTrx(("1", "ExampleTests.Skipped", "NotExecuted"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["ExampleTests.Skipped"], StringComparer.OrdinalIgnoreCase),
                [
                    new TestPartitionCoverage("empty", true, [emptyTrx]),
                    new TestPartitionCoverage("skipped", true, [skippedTrx])
                ]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Equal(["empty", "skipped"], result.EmptyPartitions);
        }
        finally
        {
            File.Delete(emptyTrx);
            File.Delete(skippedTrx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_rejects_cross_generation_drop_unless_test_file_was_deleted")]
    public void TestCoverageInvariantRejectsCrossGenerationDropUnlessTestFileWasDeleted()
    {
        var trx = WriteTrx(("1", "CurrentTests.Runs", "Passed"));
        try
        {
            var candidate = new HashSet<string>(["CurrentTests.Runs"], StringComparer.OrdinalIgnoreCase);
            var main = new HashSet<string>(
                ["CurrentTests.Runs", "RemovedTests.WasPresentOnMain"],
                StringComparer.OrdinalIgnoreCase);
            var partitions = new[] { new TestPartitionCoverage("lane", true, [trx]) };

            var rejected = TestCoverageInvariant.Evaluate(candidate, partitions, main, []);
            var allowed = TestCoverageInvariant.Evaluate(candidate, partitions, main, ["tests/RemovedTests.cs"]);

            Xunit.Assert.False(rejected.Passed);
            Xunit.Assert.Contains("main-only:RemovedTests.WasPresentOnMain", rejected.MissingTests);
            Xunit.Assert.True(allowed.Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    private static string WriteTrx(params (string Id, string Name, string Outcome)[] tests)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.trx");
        var definitions = string.Join(
            "",
            tests.Select(test =>
                $"<UnitTest id=\"{test.Id}\" name=\"{test.Name}\"><TestMethod className=\"{test.Name[..test.Name.LastIndexOf('.')]}\" name=\"{test.Name[(test.Name.LastIndexOf('.') + 1)..]}\" /></UnitTest>"));
        var results = string.Join(
            "",
            tests.Select(test =>
                $"<UnitTestResult testId=\"{test.Id}\" testName=\"{test.Name}\" outcome=\"{test.Outcome}\" />"));
        File.WriteAllText(
            path,
            $"<TestRun><TestDefinitions>{definitions}</TestDefinitions><Results>{results}</Results></TestRun>");
        return path;
    }
}
