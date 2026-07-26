using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestCoverageInvariantTests
{
    [Xunit.Fact(DisplayName = "TestCoverageInvariant_attempt_receipt_copy_survives_originating_slot_clear")]
    public void TestCoverageInvariantAttemptReceiptCopySurvivesOriginatingSlotClear()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-receipts-{Guid.NewGuid():N}");
        var attemptPrefix = Path.Combine(root, "attempts", "attempt-123");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                root);
            var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(3);
            var sourcePath = Path.Combine(
                GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(environment),
                "lane.trx");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            WriteTrxAt(sourcePath, ("1", "ExampleTests.Runs", "Passed"));
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);

            var durablePaths = GoalAcceptanceVerifier.CopyCompletedTestReceiptsToAttemptFolder([sourcePath])!;
            Directory.Delete(environment.ArtifactsPath, recursive: true);
            var coverage = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["ExampleTests.Runs"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, durablePaths, AttemptId: "attempt-123")],
                currentAttemptId: "attempt-123");

            Assert.True(coverage.Passed, coverage.Summary);
            Assert.Empty(coverage.EmptyPartitions);
            Assert.Single(durablePaths);
            Assert.True(File.Exists(durablePaths[0]));
            Assert.StartsWith(
                attemptPrefix + ".receipts" + Path.DirectorySeparatorChar,
                durablePaths[0],
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_receipt_copy_failure_preserves_slot_receipt_path")]
    public void TestCoverageInvariantReceiptCopyFailurePreservesSlotReceiptPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-receipt-failure-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "slot", "lane.trx");
        var attemptPrefix = Path.Combine(root, "attempts", "attempt-123");
        var receiptDirectory = attemptPrefix + ".receipts";
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            WriteTrxAt(sourcePath, ("1", "ExampleTests.Runs", "Passed"));
            Directory.CreateDirectory(Path.GetDirectoryName(receiptDirectory)!);
            File.WriteAllText(receiptDirectory, "blocks directory creation");
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);

            var durablePaths = GoalAcceptanceVerifier.CopyCompletedTestReceiptsToAttemptFolder([sourcePath])!;

            Assert.Equal([sourcePath], durablePaths);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
            Xunit.Assert.Equal(
                AcceptanceFailureClassifications.StructuralCoverageFailed,
                result.FailureClassification);
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
            Xunit.Assert.Equal(
                AcceptanceFailureClassifications.StructuralCoverageFailed,
                result.FailureClassification);
        }
        finally
        {
            File.Delete(emptyTrx);
            File.Delete(skippedTrx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_accounts_skip_marked_tests_recorded_as_NotExecuted")]
    public void TestCoverageInvariantAccountsSkipMarkedTestsRecordedAsNotExecuted()
    {
        var trx = WriteTrx(
            ("1", "ExampleTests.Runs", "Passed"),
            ("2", "ExampleTests.SkippedByDesign", "NotExecuted"));
        try
        {
            var discovered = new HashSet<string>(
                ["ExampleTests.Runs", "ExampleTests.SkippedByDesign"],
                StringComparer.OrdinalIgnoreCase);
            var partitions = new[] { new TestPartitionCoverage("lane", true, [trx]) };

            var accounted = TestCoverageInvariant.Evaluate(discovered, partitions);

            Xunit.Assert.True(accounted.Passed);

            var withUnrecorded = new HashSet<string>(
                ["ExampleTests.Runs", "ExampleTests.SkippedByDesign", "ExampleTests.NeverRecorded"],
                StringComparer.OrdinalIgnoreCase);

            var rejected = TestCoverageInvariant.Evaluate(withUnrecorded, partitions);

            Xunit.Assert.False(rejected.Passed);
            Xunit.Assert.Contains("ExampleTests.NeverRecorded", rejected.MissingTests);
            Xunit.Assert.DoesNotContain("ExampleTests.SkippedByDesign", rejected.MissingTests);
        }
        finally
        {
            File.Delete(trx);
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
            Xunit.Assert.Contains(
                rejected.MissingTests,
                missing => missing.StartsWith("cross-generation-count:", StringComparison.Ordinal));
            Xunit.Assert.True(allowed.Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_classifies_empty_partition_as_environmental_only_with_positive_evidence")]
    public void TestCoverageInvariantClassifiesEmptyPartitionAsEnvironmentalOnlyWithPositiveEvidence()
    {
        var emptyTrx = WriteTrx();
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["ExampleTests.Runs"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("interfered", true, [emptyTrx], HasEnvironmentInterferenceEvidence: true)]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Equal(["interfered"], result.EmptyPartitions);
            Xunit.Assert.Equal(
                AcceptanceFailureClassifications.GateEnvironmentInterference,
                result.FailureClassification);
        }
        finally
        {
            File.Delete(emptyTrx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_scopes_receipts_to_attempt_and_latest_partition_run")]
    public void TestCoverageInvariantScopesReceiptsToAttemptAndLatestPartitionRun()
    {
        var attemptFolder = Path.Combine(Path.GetTempPath(), $"coverage-attempts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attemptFolder);
        var priorAttempt = WriteTrxAt(
            Path.Combine(attemptFolder, "attempt-prior.lane.trx"),
            ("1", "PriorAttemptTests.MustNotCount", "Passed"));
        var firstRun = WriteTrxAt(
            Path.Combine(attemptFolder, "attempt-current.lane.run-0.trx"),
            ("2", "FirstRunTests.MustBeReplaced", "Passed"));
        var latestRun = WriteTrxAt(
            Path.Combine(attemptFolder, "attempt-current.lane.run-1.trx"),
            ("3", "EligibleTests.One", "Passed"),
            ("4", "EligibleTests.Two", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(
                    ["EligibleTests.One", "EligibleTests.Two"],
                    StringComparer.OrdinalIgnoreCase),
                [
                    new TestPartitionCoverage(
                        "lane",
                        true,
                        [priorAttempt],
                        AttemptId: "attempt-prior",
                        RunOrdinal: 0),
                    new TestPartitionCoverage(
                        "lane",
                        true,
                        [firstRun],
                        AttemptId: "attempt-current",
                        RunOrdinal: 0),
                    new TestPartitionCoverage(
                        "lane",
                        true,
                        [latestRun],
                        AttemptId: "attempt-current",
                        RunOrdinal: 1)
                ],
                currentAttemptId: "attempt-current");

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Equal(["EligibleTests.One", "EligibleTests.Two"], result.ExecutedTests);
            Xunit.Assert.Empty(result.MissingTests);
            Xunit.Assert.Contains("discovered=2, executed=2", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(priorAttempt);
            File.Delete(firstRun);
            File.Delete(latestRun);
            Directory.Delete(attemptFolder);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_cross_generation_allows_identity_rename_when_count_is_preserved")]
    public void TestCoverageInvariantCrossGenerationAllowsIdentityRenameWhenCountIsPreserved()
    {
        var trx = WriteTrx(("1", "RenamedTests.NewName", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["RenamedTests.NewName"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, [trx])],
                new HashSet<string>(["OriginalTests.OldName"], StringComparer.OrdinalIgnoreCase),
                []);

            Xunit.Assert.True(result.Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_matches_bare_MTP_display_names_to_TRX_results")]
    public void TestCoverageInvariantMatchesBareMtpDisplayNamesToTrxResults()
    {
        var trx = WriteTrxWithMethodIdentity(
            ("1", "structural coverage uses the MTP display name", "Passed", "ExampleTests.Runs"));
        try
        {
            var discovered = TestCoverageInvariant.ParseDiscoveredTests(
                "structural coverage uses the MTP display name",
                bareTestList: true);

            var result = TestCoverageInvariant.Evaluate(
                discovered,
                [new TestPartitionCoverage("core tests", true, [trx])]);

            Xunit.Assert.True(result.Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_bare_MTP_discovery_ignores_summaries_and_diagnostics")]
    public void TestCoverageInvariantBareMtpDiscoveryIgnoresSummariesAndDiagnostics()
    {
        var discovered = TestCoverageInvariant.ParseDiscoveredTests(
            """
            Microsoft.Testing.Platform v1.8.0
            [xUnit.net 00:00:00.10] Discovering: Example.Tests
            structural coverage uses the MTP display name
            Skipping real-worker process guard: process command-line enumeration is unavailable on this platform.
            Test discovery summary: total: 1, failed: 0, succeeded: 1
            Test run summary: Tests found: 1
            total: 1
            succeeded: 1
            Duration: 00:00:00.42
            """,
            bareTestList: true);

        Xunit.Assert.Equal(["structural coverage uses the MTP display name"], discovered);
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_preserves_distinct_parameterized_cases")]
    public void TestCoverageInvariantPreservesDistinctParameterizedCases()
    {
        var trx = WriteTrx(("1", "TheoryTests.Runs(value: 1)", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(
                    ["TheoryTests.Runs(value: 1)", "TheoryTests.Runs(value: 2)"],
                    StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("theories", true, [trx])]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains("TheoryTests.Runs(value: 2)", result.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_does_not_count_failed_TRX_rows_as_completed")]
    public void TestCoverageInvariantDoesNotCountFailedTrxRowsAsCompleted()
    {
        var trx = WriteTrx(("1", "ExampleTests.Fails", "Failed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["ExampleTests.Fails"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("failure-neutralized-run", true, [trx])]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains("ExampleTests.Fails", result.MissingTests);
            Xunit.Assert.Contains("failure-neutralized-run", result.EmptyPartitions);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_allows_custom_display_name_for_deleted_test_file")]
    public void TestCoverageInvariantAllowsCustomDisplayNameForDeletedTestFile()
    {
        var trx = WriteTrx(("1", "CurrentTests.Runs", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["CurrentTests.Runs"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, [trx])],
                new HashSet<string>(
                    ["CurrentTests.Runs", "custom display name without class identity"],
                    StringComparer.OrdinalIgnoreCase),
                ["tests/Example.Tests/RemovedTests.cs"]);

            Xunit.Assert.True(result.Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_does_not_apply_generic_deleted_filename_to_unrelated_test")]
    public void TestCoverageInvariantDoesNotApplyGenericDeletedFilenameToUnrelatedTest()
    {
        var trx = WriteTrx(("1", "CurrentTests.Runs", "Passed"));
        try
        {
            var candidate = new HashSet<string>(["CurrentTests.Runs"], StringComparer.OrdinalIgnoreCase);
            var main = new HashSet<string>(
                ["CurrentTests.Runs", "UnrelatedTests.WasPresentOnMain"],
                StringComparer.OrdinalIgnoreCase);

            var result = TestCoverageInvariant.Evaluate(
                candidate,
                [new TestPartitionCoverage("lane", true, [trx])],
                main,
                ["tests/Tests.cs"]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains(
                result.MissingTests,
                missing => missing.StartsWith("cross-generation-count:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(trx);
        }
    }

    private static string WriteTrx(params (string Id, string Name, string Outcome)[] tests) =>
        WriteTrxWithMethodIdentityAt(
            Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.trx"),
            tests.Select(test => (test.Id, test.Name, test.Outcome, test.Name)).ToArray());

    private static string WriteTrxAt(
        string path,
        params (string Id, string Name, string Outcome)[] tests) =>
        WriteTrxWithMethodIdentityAt(
            path,
            tests.Select(test => (test.Id, test.Name, test.Outcome, test.Name)).ToArray());

    private static string WriteTrxWithMethodIdentity(
        params (string Id, string Name, string Outcome, string MethodIdentity)[] tests) =>
        WriteTrxWithMethodIdentityAt(
            Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.trx"),
            tests);

    private static string WriteTrxWithMethodIdentityAt(
        string path,
        params (string Id, string Name, string Outcome, string MethodIdentity)[] tests)
    {
        var definitions = string.Join(
            "",
            tests.Select(test =>
                $"<UnitTest id=\"{test.Id}\" name=\"{test.Name}\"><TestMethod className=\"{test.MethodIdentity[..test.MethodIdentity.LastIndexOf('.')]}\" name=\"{test.MethodIdentity[(test.MethodIdentity.LastIndexOf('.') + 1)..]}\" /></UnitTest>"));
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
