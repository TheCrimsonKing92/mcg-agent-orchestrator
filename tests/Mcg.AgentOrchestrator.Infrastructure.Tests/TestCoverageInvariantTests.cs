using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestCoverageInvariantTests
{
    [Xunit.Fact(DisplayName = "TestCoverageInvariant_attempt_receipt_copy_survives_originating_slot_clear")]
    public void TestCoverageInvariantAttemptReceiptCopySurvivesOriginatingSlotClear()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-receipts-{Guid.NewGuid():N}");
        var attemptPrefix = Path.Combine(root, "attempts", "attempt-123");
        var environment = new DotnetBuildEnvironment(
            "run-slot-3",
            Path.Combine(root, "slots", "slot-3"),
            Path.Combine(root, "slots", "slot-3", "artifacts"),
            Path.Combine(root, "slots", "slot-3", "lease.lock"),
            [],
            "slot-3");
        try
        {
            var sourcePath = Path.Combine(
                GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(environment),
                "lane.trx");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            WriteTrxAt(sourcePath, ("1", "ExampleTests.Runs", "Passed"));
            var durablePaths = GoalAcceptanceVerifier.CopyCompletedTestReceiptsToAttemptFolder(
                [sourcePath],
                attemptPrefix)!;
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
                Path.GetDirectoryName(attemptPrefix)! + Path.DirectorySeparatorChar,
                durablePaths[0],
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_receipt_copy_failure_fails_lane_completion")]
    public void TestCoverageInvariantReceiptCopyFailureFailsLaneCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-receipt-failure-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "slot", "lane.trx");
        var attemptPrefix = Path.Combine(root, "attempts", "attempt-123");
        var receiptDirectory = Path.GetDirectoryName(attemptPrefix)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            WriteTrxAt(sourcePath, ("1", "ExampleTests.Runs", "Passed"));
            Directory.CreateDirectory(Path.GetDirectoryName(receiptDirectory)!);
            File.WriteAllText(receiptDirectory, "blocks directory creation");
            var exception = Assert.Throws<IOException>(
                () => GoalAcceptanceVerifier.CopyCompletedTestReceiptsToAttemptFolder(
                    [sourcePath],
                    attemptPrefix));

            Assert.True(File.Exists(sourcePath));
            Assert.Contains(sourcePath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(receiptDirectory, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_matches_console_downgraded_dash_to_trx_unicode_dash")]
    public void TestCoverageInvariantMatchesConsoleDowngradedDashToTrxUnicodeDash()
    {
        foreach (var unicodeDash in "\u2010\u2011\u2012\u2013\u2014\u2015")
        {
            const string discoveredIdentity =
                "ExampleTests.Theory(expectedEvidence: failed - no TRX produced)";
            var recordedIdentity =
                $"ExampleTests.Theory(expectedEvidence: failed {unicodeDash} no TRX produced)";
            var trx = WriteTrx(("1", recordedIdentity, "Passed"));
            try
            {
                var result = TestCoverageInvariant.Evaluate(
                    new HashSet<string>([discoveredIdentity], StringComparer.OrdinalIgnoreCase),
                    [new TestPartitionCoverage("lane", true, [trx])]);

                Xunit.Assert.True(result.Passed, result.Summary);
                Xunit.Assert.Empty(result.MissingTests);
            }
            finally
            {
                File.Delete(trx);
            }
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_allows_cross_generation_count_when_test_files_move_to_another_project")]
    public void TestCoverageInvariantAllowsCrossGenerationCountWhenTestFilesMoveToAnotherProject()
    {
        const string gatedProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        const string extractedProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj";
        var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
            "R100\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentTests.cs" +
                "\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/ProviderEnvironmentTests.cs\n" +
            "R097\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentIsolationTests.cs" +
                "\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/ProviderEnvironmentIsolationTests.cs",
            gatedProject,
            _ => extractedProject);
        var candidate = Enumerable.Range(0, 3439)
            .Select(index => $"current coverage case {index:D4}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = Enumerable.Range(0, 6)
            .Select(index => (
                Name: $"provider environment case {index:D2}",
                SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentTests.cs"))
            .Concat(Enumerable.Range(0, 7)
                .Select(index => (
                    Name: $"provider isolation case {index:D2}",
                    SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentIsolationTests.cs")))
            .ToArray();
        var mainDiscovery = ParseMtpDiscovery(
            candidate
                .Select(name => (
                    Name: name,
                    SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CurrentCoverageTests.cs"))
                .Concat(moved)
                .ToArray());
        var trx = WriteTrxWithMethodIdentity(candidate
            .Select((name, index) => (
                index.ToString(),
                name,
                "Passed",
                $"CurrentCoverageTests.Case{index:D4}"))
            .ToArray());
        try
        {
            var partitions = new[] { new TestPartitionCoverage("lane", true, [trx]) };

            var receipt = TestCoverageInvariant.Evaluate(
                candidate,
                partitions,
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);
            var oneShort = TestCoverageInvariant.Evaluate(
                candidate.Take(3438).ToHashSet(StringComparer.OrdinalIgnoreCase),
                partitions,
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);

            Xunit.Assert.Equal(
                [
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentTests.cs",
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironmentIsolationTests.cs"
                ],
                deleted);
            Xunit.Assert.True(receipt.Passed);
            Xunit.Assert.False(oneShort.Passed);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=3438,minimum=3439,main=3452,deleted=13",
                oneShort.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_preserves_cli_move_across_parent_and_extracted_project")]
    public void TestCoverageInvariantPreservesCliMoveAcrossParentAndExtractedProject()
    {
        const string parentProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
            "R100\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs" +
                "\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliArgumentNormalizationTests.cs\n" +
            "R100\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.AddTaskCommands.cs" +
                "\ttests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliCommandTests.AddTaskCommands.cs",
            parentProject,
            _ => cliProject);
        var retained = Enumerable.Range(0, 20)
            .Select(index => $"retained case {index:D2}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = Enumerable.Range(0, 15)
            .Select(index => (
                Name: $"argument normalization case {index:D2}",
                SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs"))
            .Concat(Enumerable.Range(0, 4)
                .Select(index => (
                    Name: $"add-task command case {index:D2}",
                    SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.AddTaskCommands.cs")))
            .ToArray();
        var mainDiscovery = ParseMtpDiscovery(
            retained
                .Select(name => (
                    Name: name,
                    SourceFile: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RetainedTests.cs"))
                .Concat(moved)
                .ToArray());
        var trx = WriteTrxWithMethodIdentity(retained
            .Select((name, index) => (
                index.ToString(),
                name,
                "Passed",
                $"RetainedTests.Case{index:D2}"))
            .ToArray());
        try
        {
            var partitions = new[] { new TestPartitionCoverage("parent", true, [trx]) };

            var receipt = TestCoverageInvariant.Evaluate(
                retained,
                partitions,
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);
            var oneShort = TestCoverageInvariant.Evaluate(
                retained.Take(19).ToHashSet(StringComparer.OrdinalIgnoreCase),
                partitions,
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);

            Xunit.Assert.Equal(19, moved.Length);
            Xunit.Assert.Equal(
                [
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs",
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.AddTaskCommands.cs"
                ],
                deleted);
            Xunit.Assert.True(receipt.Passed);
            Xunit.Assert.False(oneShort.Passed);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=19,minimum=20,main=39,deleted=19",
                oneShort.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_credits_single_test_file_once_from_discovery_metadata")]
    public void TestCoverageInvariantCountsDeletedTestFile()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var candidate = new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase);
            var mainDiscovery = ParseMtpDiscovery(
                ("current test", "tests/Example.Tests/CurrentTests.cs"),
                ("removed test", "tests/Example.Tests/RemovedTests.cs"));
            var partitions = new[] { new TestPartitionCoverage("lane", true, [trx]) };
            var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
                "D\ttests/Example.Tests/RemovedTests.cs",
                "tests/Example.Tests/Example.Tests.csproj",
                _ => throw new InvalidOperationException("Deletion rows do not resolve a destination project."));

            var allowed = TestCoverageInvariant.Evaluate(
                candidate,
                partitions,
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);

            Xunit.Assert.Equal(["tests/Example.Tests/RemovedTests.cs"], deleted);
            Xunit.Assert.True(allowed.Passed);
            Xunit.Assert.Empty(allowed.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_refuses_unmatched_MTP_location_credit")]
    public void TestCoverageInvariantRefusesUnmatchedMtpLocationCredit()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var mainDiscovery = ParseMtpDiscoveryWithoutLocations("current test", "removed test");
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, [trx])],
                mainDiscovery.Tests,
                ["tests/Example.Tests/RemovedTests.cs"],
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=2,main=2,deleted=0",
                result.MissingTests);
            Xunit.Assert.Contains(
                "cross-generation-attribution:source=mtp-json-location,file=tests/Example.Tests/RemovedTests.cs,status=unattributed,fallback=none,credit=0",
                result.Summary);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_rejects_tests_dropped_from_a_retained_file")]
    public void TestCoverageInvariantRejectsTestsDroppedFromRetainedFile()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var candidate = new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase);
            var mainDiscovery = ParseMtpDiscovery(
                ("current test", "tests/Example.Tests/RetainedTests.cs"),
                ("vanished test", "tests/Example.Tests/RetainedTests.cs"));
            var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
                "M\ttests/RetainedTests.cs",
                "tests/Example.Tests/Example.Tests.csproj",
                _ => throw new InvalidOperationException("Modified rows do not resolve a destination project."));

            var rejected = TestCoverageInvariant.Evaluate(
                candidate,
                [new TestPartitionCoverage("lane", true, [trx])],
                mainDiscovery.Tests,
                deleted,
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest);

            Xunit.Assert.Empty(deleted);
            Xunit.Assert.False(rejected.Passed);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=2,main=2,deleted=0",
                rejected.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact]
    public void Evaluate_DeclaredRemovalsAcrossFiles_CreditsEveryCase()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var candidate = new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase);
            var mainDiscovery = ParseMtpDiscovery(
                ("current test", "tests/Example.Tests/FirstRetainedTests.cs"),
                ("Example.Tests.FirstRetainedTests.FirstRemoval", "tests/Example.Tests/FirstRetainedTests.cs"),
                ("Example.Tests.SecondRetainedTests.SecondRemoval", "tests/Example.Tests/SecondRetainedTests.cs"),
                ("Example.Tests.SecondRetainedTests.ThirdRemoval", "tests/Example.Tests/SecondRetainedTests.cs"));

            var result = TestCoverageInvariant.Evaluate(
                candidate,
                [new TestPartitionCoverage("lane", true, [trx])],
                mainDiscovery.Tests,
                [],
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest,
                sanctionedRemovedTests:
                [
                    "Example.Tests.FirstRetainedTests.FirstRemoval",
                    "Example.Tests.SecondRetainedTests.SecondRemoval",
                    "Example.Tests.SecondRetainedTests.ThirdRemoval"
                ]);

            Xunit.Assert.True(result.Passed, result.Summary);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=1,main=4,deleted=3",
                result.Summary,
                StringComparison.Ordinal);
            Xunit.Assert.Contains(
                "source=declared-test-removal,identity=Example.Tests.FirstRetainedTests.FirstRemoval,status=corroborated,credit=1",
                result.Summary,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact]
    public void Evaluate_DeclaredMethodIdentity_CreditsDisplayNameAndEveryTheoryExpansion()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var candidate = new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase);
            var mainDiscovery = ParseMtpDiscovery(
                ("current test", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_enabled_pushes_main_goal_branch_and_tags_to_bare_remote", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_success_does_not_leave_interrupted_enqueue_operation", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_disabled_or_unconfigured_does_not_push(writeDisabledConfig: False)", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_disabled_or_unconfigured_does_not_push(writeDisabledConfig: True)", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Terminal_sweep_does_not_run_remote_mirror_push_inline", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_unreachable_remote_defers_and_later_retry_succeeds", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_retry_uses_landed_branch_tip_after_cleanup_deletes_goal_branch", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_two_remotes_pushes_reachable_and_defers_unreachable", "tests/Example.Tests/LandingExecutorTests.cs"),
                ("Remote_mirror_processing_preserves_later_enqueued_mirror_debt", "tests/Example.Tests/LandingExecutorTests.cs"));

            var result = TestCoverageInvariant.Evaluate(
                candidate,
                [new TestPartitionCoverage("lane", true, [trx])],
                mainDiscovery.Tests,
                [],
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest,
                sanctionedRemovedTests:
                [
                    "Example.Tests.LandingExecutorTests.RemoteMirrorEnabledPushesMainGoalBranchAndTagsToBareRemote",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorSuccessDoesNotLeaveInterruptedEnqueueOperation",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorDisabledOrUnconfiguredDoesNotPush",
                    "Example.Tests.LandingExecutorTests.TerminalSweepDoesNotRunRemoteMirrorPushInline",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorUnreachableRemoteDefersAndLaterRetrySucceeds",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorRetryUsesLandedBranchTipAfterCleanupDeletesGoalBranch",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable",
                    "Example.Tests.LandingExecutorTests.RemoteMirrorProcessingPreservesLaterEnqueuedMirrorDebt"
                ]);

            Xunit.Assert.True(result.Passed, result.Summary);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=1,main=10,deleted=9",
                result.Summary,
                StringComparison.Ordinal);
            Xunit.Assert.Contains(
                "identity=Example.Tests.LandingExecutorTests.RemoteMirrorDisabledOrUnconfiguredDoesNotPush,status=corroborated,credit=2",
                result.Summary,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact]
    public void Evaluate_DeclaredRemoval_DoesNotDoubleCreditLegacyFallback()
    {
        var trx = WriteTrxWithMethodIdentity(("1", "current test", "Passed", "CurrentTests.Runs"));
        try
        {
            var mainDiscovery = ParseMtpDiscoveryWithoutLocations(
                "current test",
                "Example.Tests.RemovedTests.RemovedCase");
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["current test"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, [trx])],
                mainDiscovery.Tests,
                ["tests/Example.Tests/RemovedTests.cs"],
                mainDiscoveredTestSourceFiles: mainDiscovery.SourceFilesByTest,
                sanctionedRemovedTests: ["Example.Tests.RemovedTests.RemovedCase"]);

            Xunit.Assert.True(result.Passed, result.Summary);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=1,main=2,deleted=1",
                result.Summary,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_does_not_reduce_minimum_for_intra_project_rename")]
    public void TestCoverageInvariantDoesNotReduceMinimumForIntraProjectRename()
    {
        const string project = "tests/Example.Tests/Example.Tests.csproj";
        var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
            "R100\ttests/Example.Tests/OldTests.cs\ttests/Example.Tests/RenamedTests.cs",
            project,
            _ => project);
        var trx = WriteTrx(("1", "CurrentTests.Runs", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>(["CurrentTests.Runs"], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("lane", true, [trx])],
                new HashSet<string>(
                    ["CurrentTests.Runs", "OldTests.WasPresentOnMain"],
                    StringComparer.OrdinalIgnoreCase),
                deleted);

            Xunit.Assert.Empty(deleted);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=2,main=2,deleted=0",
                result.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_ignores_non_test_renames_and_test_copies")]
    public void GoalAcceptanceVerifierIgnoresNonTestRenamesAndTestCopies()
    {
        var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
            "R100\tdocs/old.md\tdocs/new.md\n" +
            "C100\ttests/Example.Tests/SourceTests.cs\ttests/Example.Tests/CopiedTests.cs",
            "tests/Example.Tests/Example.Tests.csproj",
            _ => "tests/Other.Tests/Other.Tests.csproj");

        Xunit.Assert.Empty(deleted);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_resolves_nearest_owning_project_within_worktree")]
    public void GoalAcceptanceVerifierResolvesNearestOwningProjectWithinWorktree()
    {
        var outer = Path.Combine(Path.GetTempPath(), $"owning-project-{Guid.NewGuid():N}");
        var worktree = Path.Combine(outer, "worktree");
        var parentDirectory = Path.Combine(worktree, "tests", "Parent.Tests");
        var nestedDirectory = Path.Combine(parentDirectory, "Nested");
        var sameDirectory = Path.Combine(worktree, "tests", "Same.Tests");
        var unownedDirectory = Path.Combine(worktree, "tests", "Unowned");
        try
        {
            Directory.CreateDirectory(nestedDirectory);
            Directory.CreateDirectory(sameDirectory);
            Directory.CreateDirectory(unownedDirectory);
            File.WriteAllText(Path.Combine(outer, "Ancestor.Tests.csproj"), "");
            File.WriteAllText(Path.Combine(parentDirectory, "Parent.Tests.csproj"), "");
            File.WriteAllText(Path.Combine(nestedDirectory, "Nested.Tests.csproj"), "");
            File.WriteAllText(Path.Combine(sameDirectory, "Same.Tests.csproj"), "");

            var rootWithTrailingSeparator = worktree + Path.DirectorySeparatorChar;
            var nestedOwner = GoalAcceptanceVerifier.ResolveOwningProjectForTests(
                rootWithTrailingSeparator,
                "tests/Parent.Tests/Nested/CaseTests.cs");
            var sameDirectoryOwner = GoalAcceptanceVerifier.ResolveOwningProjectForTests(
                rootWithTrailingSeparator,
                "tests/Same.Tests/CaseTests.cs");
            var noOwner = GoalAcceptanceVerifier.ResolveOwningProjectForTests(
                rootWithTrailingSeparator,
                "tests/Unowned/CaseTests.cs");

            Xunit.Assert.Equal("tests/Parent.Tests/Nested/Nested.Tests.csproj", nestedOwner);
            Xunit.Assert.Equal("tests/Same.Tests/Same.Tests.csproj", sameDirectoryOwner);
            Xunit.Assert.Null(noOwner);
            Xunit.Assert.Null(GoalAcceptanceVerifier.ResolveOwningProjectForTests(worktree, " "));
        }
        finally
        {
            if (Directory.Exists(outer))
            {
                Directory.Delete(outer, recursive: true);
            }
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_matches_truncated_theory_names_with_middle_dot_ellipsis")]
    public void TestCoverageInvariantMatchesTruncatedTheoryNamesWithMiddleDotEllipsis()
    {
        const string testName = "TheoryTests.Runs(value: abc···)";
        var trx = WriteTrx(("1", testName, "Passed"));
        try
        {
            var discovered = TestCoverageInvariant.ParseDiscoveredTests(testName, bareTestList: true);

            var result = TestCoverageInvariant.Evaluate(
                discovered,
                [new TestPartitionCoverage("theories", true, [trx])]);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Empty(result.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_does_not_canonicalize_replacement_character_names")]
    public void TestCoverageInvariantDoesNotCanonicalizeReplacementCharacterNames()
    {
        const string trxName = "TheoryTests.Runs(value: abc···)";
        const string corruptedDiscoveryName = "TheoryTests.Runs(value: abc���)";
        var trx = WriteTrx(("1", trxName, "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>([corruptedDiscoveryName], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("theories", true, [trx])]);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains(corruptedDiscoveryName, result.MissingTests);
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_counts_display_name_with_diagnostic_prefix")]
    public void TestCoverageInvariantCountsDisplayNameWithDiagnosticPrefix()
    {
        const string testName = "ErrorHandlingWhenDispatchExits";
        var discovered = TestCoverageInvariant.ParseDiscoveredTests(
            $"Microsoft.Testing.Platform v1.8.0{Environment.NewLine}{testName}",
            bareTestList: true);
        var trx = WriteTrx(("1", "UnrelatedTests.Runs", "Passed"));
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                discovered,
                [new TestPartitionCoverage("lane", true, [trx])]);

            Xunit.Assert.Equal([testName], discovered);
            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains(testName, result.MissingTests);
        }
        finally
        {
            File.Delete(trx);
        }
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_counts_truncated_parameterized_cases_exactly")]
    public void TestCoverageInvariantCountsTruncatedParameterizedCasesExactly()
    {
        var uniqueNames = Enumerable.Range(0, 42)
            .Select(index => $"TheoryTests.Runs(value: {index:D2}···)")
            .ToArray();
        var discoveredNames = uniqueNames
            .Concat([uniqueNames[0], uniqueNames[1]])
            .ToArray();
        var discovery = ParseMtpDiscoveryWithoutLocations(discoveredNames);
        var trxRows = discoveredNames
            .Select((name, index) => ($"test-{index:D2}", name, "Passed"))
            .ToArray();
        var completeTrx = WriteTrx(trxRows);
        var oneShortTrx = WriteTrx(trxRows[..^1]);
        try
        {
            var complete = TestCoverageInvariant.Evaluate(
                discovery.Tests,
                [new TestPartitionCoverage("theories", true, [completeTrx])]);
            var oneShort = TestCoverageInvariant.Evaluate(
                discovery.Tests,
                [new TestPartitionCoverage("theories", true, [oneShortTrx])]);

            Xunit.Assert.Equal(44, discovery.Tests.Count);
            Xunit.Assert.True(complete.Passed);
            Xunit.Assert.Contains("discovered=44, executed=44", complete.Summary);
            Xunit.Assert.False(oneShort.Passed);
            Xunit.Assert.Single(oneShort.MissingTests);
            Xunit.Assert.Equal(uniqueNames[1], oneShort.MissingTests[0]);
        }
        finally
        {
            File.Delete(completeTrx);
            File.Delete(oneShortTrx);
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

    [Xunit.Fact(DisplayName = "TestCoverageInvariant_refuses_deletion_credit_for_unmatched_filename")]
    public void TestCoverageInvariantRefusesDeletionCreditForUnmatchedFilename()
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

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Contains(
                "cross-generation-count:candidate=1,minimum=2,main=2,deleted=0",
                result.MissingTests);
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

    private static TestDiscoverySnapshot ParseMtpDiscovery(
        params (string Name, string SourceFile)[] tests)
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"main-discovery-{Guid.NewGuid():N}");
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tests = tests.Select((test, index) => new
            {
                uid = $"test-{index:D4}",
                displayName = test.Name,
                type = new
                {
                    assemblyFullName = "Example.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                    typeName = Path.GetFileNameWithoutExtension(test.SourceFile),
                    methodName = $"Case{index:D4}",
                    methodArity = 0,
                    returnTypeFullName = "System.Void",
                    parameterTypeFullNames = Array.Empty<string>()
                },
                location = new
                {
                    file = Path.Combine(
                        repositoryRoot,
                        test.SourceFile.Replace('/', Path.DirectorySeparatorChar)),
                    lineStart = index + 1,
                    lineEnd = index + 1
                }
            })
        });

        return TestCoverageInvariant.ParseDiscovery(
            json,
            bareTestList: true,
            repositoryRoot: repositoryRoot);
    }

    private static TestDiscoverySnapshot ParseMtpDiscoveryWithoutLocations(params string[] tests)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tests = tests.Select((test, index) => new
            {
                uid = $"test-{index:D4}",
                displayName = test
            })
        });

        return TestCoverageInvariant.ParseDiscovery(
            json,
            bareTestList: true,
            repositoryRoot: Path.GetTempPath());
    }

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
