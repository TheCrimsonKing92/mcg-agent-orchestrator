// Parallel-safe: each test injects an explicit unique root, and every Git child receives a
// hermetic environment. No verdict observes a shared temp root, process list, clock, or schedule.
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerDispatchTestsSeededRepositoryFactoryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact]
    public void Create_MultipleCopies_ProducesIndependentCommittedRepositories()
    {
        using var scope = new FactoryScope(directoryAllocator: static (root, attempt) =>
        {
            var path = Path.Combine(root, attempt.ToString("D32"));
            Directory.CreateDirectory(path);
            return path;
        });

        var first = scope.Factory.Create();
        var second = scope.Factory.Create();

        Xunit.Assert.NotEqual(
            first.PublishedIdentity.RepositoryPath,
            second.PublishedIdentity.RepositoryPath);
        Xunit.Assert.NotEqual(
            first.PublishedIdentity.GitDirectoryPath,
            second.PublishedIdentity.GitDirectoryPath);
        Xunit.Assert.Equal(
            first.TemplateIdentity.HeadCommit,
            first.PublishedIdentity.HeadCommit);
        Xunit.Assert.Equal(
            second.TemplateIdentity.HeadCommit,
            second.PublishedIdentity.HeadCommit);
        Xunit.Assert.Equal(
            first.PublishedIdentity.RepositoryPath,
            first.PublishedIdentity.TopLevelPath);
        Xunit.Assert.Equal(
            second.PublishedIdentity.RepositoryPath,
            second.PublishedIdentity.TopLevelPath);
        Xunit.Assert.True(Directory.Exists(first.PublishedIdentity.GitDirectoryPath));
        Xunit.Assert.True(Directory.Exists(second.PublishedIdentity.GitDirectoryPath));
        Xunit.Assert.Equal(32, Path.GetFileName(first.PublishedIdentity.RepositoryPath).Length);
        Xunit.Assert.Equal(32, Path.GetFileName(second.PublishedIdentity.RepositoryPath).Length);
    }

    [Xunit.Fact]
    public void TempRootJanitor_LegacyAndGuardedPolicies_DiscriminateTemplateLoss()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"factory-janitor-{Guid.NewGuid():N}");
        var processId = 4246;
        var ownedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        var capturedOwner = AvailableProcess(processId);
        TempRootJanitorDeleteResult? legacyReceipt = null;
        string? redTemplate = null;
        try
        {
            using (var red = new FactoryScope(
                hooks: new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
                    BeforeTemplateValidation: template =>
                    {
                        redTemplate = template;
                        legacyReceipt = TempRootJanitor.ReapOwnedRoot(sharedRoot, processId);
                    }),
                root: ownedRoot))
            {
                var failure = Xunit.Assert.Throws<
                    WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                        () => red.Factory.Create());

                Xunit.Assert.Equal(
                    WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplatePath,
                    failure.Diagnostic.Check);
                Xunit.Assert.Equal(redTemplate, failure.Diagnostic.SourceTemplatePath);
                Xunit.Assert.False(failure.Diagnostic.FileSystem.RepositoryDirectoryExists);
                Xunit.Assert.False(failure.Diagnostic.FileSystem.GitMetadataDirectoryExists);
                Xunit.Assert.Equal("Repository directory is missing.", failure.Diagnostic.Git.StandardError);
                Xunit.Assert.Equal(TempRootJanitorDeleteStatus.Deleted, legacyReceipt?.Status);
            }

            var guardedReceipts = new List<TempRootJanitorReapResult>();
            var deleteCalls = 0;
            using (var green = new FactoryScope(
                hooks: new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
                    BeforeTemplateValidation: _ =>
                    {
                        guardedReceipts.Add(Xunit.Assert.Single(TempRootJanitor.ReapOwnedRoots(
                            [new TempRootJanitorOwnedRoot(
                                processId,
                                sharedRoot,
                                capturedOwner,
                                "seeded-factory-green")],
                            _ => Inspected(capturedOwner),
                            path =>
                            {
                                deleteCalls++;
                                return TempRootJanitor.DeleteTree(path);
                            })));
                    }),
                root: ownedRoot))
            {
                var first = green.Factory.Create();
                var second = green.Factory.Create();

                Xunit.Assert.Equal(2, guardedReceipts.Count);
                Xunit.Assert.All(
                    guardedReceipts,
                    receipt => Xunit.Assert.Equal(
                        TempRootJanitorReapDisposition.RetainedLiveOwner,
                        receipt.Disposition));
                Xunit.Assert.Equal(0, deleteCalls);
                Xunit.Assert.True(Directory.Exists(first.TemplateIdentity.GitDirectoryPath));
                Xunit.Assert.True(Directory.Exists(second.TemplateIdentity.GitDirectoryPath));
                Xunit.Assert.Equal(first.TemplateIdentity.HeadCommit, second.TemplateIdentity.HeadCommit);
            }
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(Timeout = 60_000)]
    public async Task TempRootJanitor_GuardedPolicy_PreservesTemplateForConcurrentCreates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"factory-janitor-concurrent-{Guid.NewGuid():N}");
        var processId = 4247;
        var ownedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        var capturedOwner = AvailableProcess(processId);
        using var rendezvous = new Barrier(participantCount: 2);
        var receipts = new List<TempRootJanitorReapResult>();
        var receiptLock = new object();
        var deleteCalls = 0;
        try
        {
            using var scope = new FactoryScope(
                hooks: new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
                    BeforeTemplateValidation: _ =>
                    {
                        Xunit.Assert.True(
                            rendezvous.SignalAndWait(TimeSpan.FromSeconds(30)),
                            "Concurrent factory hooks did not rendezvous.");
                        var receipt = Xunit.Assert.Single(TempRootJanitor.ReapOwnedRoots(
                            [new TempRootJanitorOwnedRoot(
                                processId,
                                sharedRoot,
                                capturedOwner,
                                "seeded-factory-concurrent")],
                            _ => Inspected(capturedOwner),
                            path =>
                            {
                                Interlocked.Increment(ref deleteCalls);
                                return TempRootJanitor.DeleteTree(path);
                            }));
                        lock (receiptLock)
                        {
                            receipts.Add(receipt);
                        }
                    }),
                root: ownedRoot);

            var created = await Task.WhenAll(
                Task.Run(scope.Factory.Create),
                Task.Run(scope.Factory.Create));

            Xunit.Assert.Equal(2, receipts.Count);
            Xunit.Assert.All(
                receipts,
                receipt => Xunit.Assert.Equal(
                    TempRootJanitorReapDisposition.RetainedLiveOwner,
                    receipt.Disposition));
            Xunit.Assert.Equal(0, deleteCalls);
            Xunit.Assert.All(
                created,
                result => Xunit.Assert.True(Directory.Exists(result.TemplateIdentity.GitDirectoryPath)));
            Xunit.Assert.Equal(created[0].TemplateIdentity.HeadCommit, created[1].TemplateIdentity.HeadCommit);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact]
    public void Create_RequiredStdoutSuccess_RetainsTypedReceipt()
    {
        using var scope = new FactoryScope();

        var created = scope.Factory.Create();

        var receipt = Xunit.Assert.Single(
            created.ProbeReceipts,
            candidate => candidate.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.PublishedHeadCommit);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.Success,
            receipt.Classification);
        Xunit.Assert.Equal(0, receipt.ExitCode);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(receipt.StandardOutput));
        Xunit.Assert.True(receipt.StandardOutputByteCount > 0);
        Xunit.Assert.Equal(0, receipt.StandardErrorByteCount);
        Xunit.Assert.Equal(created.FixtureAttemptId, receipt.FixtureAttemptId);
        if (OperatingSystem.IsWindows())
        {
            Xunit.Assert.Contains(
                "capture=owned-file-handles",
                receipt.EnvironmentContract,
                StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void Create_MissingTemplateMetadata_ReportsTemplateCheck()
    {
        string? invalidatedTemplate = null;
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            BeforeTemplateValidation: path =>
            {
                invalidatedTemplate = path;
                WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(Path.Combine(path, ".git"));
            }));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateMetadata,
            failure.Diagnostic.Check);
        Xunit.Assert.Equal(invalidatedTemplate, failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.GitMetadataDirectoryExists);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
    }

    [Xunit.Fact]
    public void Create_MetadataRemovedAfterCopy_RetainsPriorProbeReceipts()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (template, _) =>
                WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(Path.Combine(template, ".git"))));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateAfterCopyMetadata,
            failure.Diagnostic.Check);
        Xunit.Assert.NotEmpty(failure.Diagnostic.ProbeReceipts!);
        Xunit.Assert.Contains(
            failure.Diagnostic.ProbeReceipts!,
            receipt => receipt.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateStatus);
    }

    [Xunit.Fact]
    public void Create_CorruptCopiedHead_ReportsDestinationCheck()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) => File.Delete(Path.Combine(staging, ".git", "HEAD"))));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.True(
            failure.Diagnostic.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit,
            failure.Message);
        Xunit.Assert.NotNull(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.NotNull(failure.Diagnostic.StagingPath);
        Xunit.Assert.NotNull(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.HeadFileExists);
        Xunit.Assert.True(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.NotEqual(0, failure.Diagnostic.Git.ExitCode);
        Xunit.Assert.Contains("HEAD^{commit}", failure.Diagnostic.Git.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "repositorySelection=unset",
            failure.Diagnostic.Git.EnvironmentContract,
            StringComparison.Ordinal);
        Xunit.Assert.False(failure.Diagnostic.Git.DrainTimedOut);
        Xunit.Assert.False(failure.Diagnostic.Git.TimedOut);
        Xunit.Assert.False(failure.Diagnostic.Git.DrainFailed);
    }

    [Xunit.Fact]
    public void Create_TemplateIdentityMismatch_RetainsOrderedValidationReceipts()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            BeforeTemplateValidation: template =>
            {
                File.WriteAllText(Path.Combine(template, "identity-change.txt"), "changed");
                WorkerDispatchTestsSeededRepositoryFactory.RunFixtureGit(
                    template,
                    ["add", "-A"],
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
                WorkerDispatchTestsSeededRepositoryFactory.RunFixtureGit(
                    template,
                    ["commit", "-m", "Change identity"],
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
            }));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateIdentityChanged,
            failure.Diagnostic.Check);
        Xunit.Assert.NotEmpty(failure.Diagnostic.ProbeReceipts!);
        Xunit.Assert.Contains(
            failure.Diagnostic.ProbeReceipts!,
            receipt => receipt.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateHeadCommit);
        Xunit.Assert.Equal(
            Enumerable.Range(1, failure.Diagnostic.ProbeReceipts!.Count),
            failure.Diagnostic.ProbeReceipts!.Select(receipt => receipt.ProbeOrdinal));
    }

    [Xunit.Fact]
    public void Create_StagingHeadMismatch_RetainsOrderedValidationReceipts()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) =>
            {
                File.WriteAllText(Path.Combine(staging, "staging-change.txt"), "changed");
                WorkerDispatchTestsSeededRepositoryFactory.RunFixtureGit(
                    staging,
                    ["add", "-A"],
                    DateTimeOffset.Parse("2026-01-03T00:00:00Z"));
                WorkerDispatchTestsSeededRepositoryFactory.RunFixtureGit(
                    staging,
                    ["commit", "-m", "Change staging head"],
                    DateTimeOffset.Parse("2026-01-03T00:00:00Z"));
            }));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadMatchesTemplate,
            failure.Diagnostic.Check);
        Xunit.Assert.NotEmpty(failure.Diagnostic.ProbeReceipts!);
        Xunit.Assert.Contains(
            failure.Diagnostic.ProbeReceipts!,
            receipt => receipt.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit);
        Xunit.Assert.Equal(
            Enumerable.Range(1, failure.Diagnostic.ProbeReceipts!.Count),
            failure.Diagnostic.ProbeReceipts!.Select(receipt => receipt.ProbeOrdinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("drain-timeout")]
    [Xunit.InlineData("drain-failure")]
    [Xunit.InlineData("nonzero-stderr")]
    [Xunit.InlineData("malformed-output")]
    public void Create_GitProbeDecisionTable_ReportsTypedTemplateHeadCheck(string scenario)
    {
        var runner = new InterceptingGitRunner(result => scenario switch
        {
            "timeout" => result with { ExitCode = null, TimedOut = true },
            "drain-timeout" => result with { DrainTimedOut = true },
            "drain-failure" => result with { DrainFailed = true, StandardError = "controlled drain failure" },
            "nonzero-stderr" => result with
            {
                ExitCode = 7,
                StandardError = "controlled git stderr",
                StandardErrorByteCount = 21,
                Classification = WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit
            },
            "malformed-output" => result with { StandardOutput = "not-a-commit\n" },
            _ => throw new InvalidOperationException($"Unknown scenario: {scenario}")
        });
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.True(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains("HEAD^{commit}", failure.Diagnostic.Git.Command, StringComparison.Ordinal);
        switch (scenario)
        {
            case "timeout":
                Xunit.Assert.True(failure.Diagnostic.Git.TimedOut);
                Xunit.Assert.Null(failure.Diagnostic.Git.ExitCode);
                break;
            case "drain-timeout":
                Xunit.Assert.True(failure.Diagnostic.Git.DrainTimedOut);
                break;
            case "drain-failure":
                Xunit.Assert.True(failure.Diagnostic.Git.DrainFailed);
                Xunit.Assert.Contains("controlled drain failure", failure.Diagnostic.Git.StandardError);
                break;
            case "nonzero-stderr":
                Xunit.Assert.Equal(7, failure.Diagnostic.Git.ExitCode);
                Xunit.Assert.Equal(
                    WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit,
                    failure.Diagnostic.Git.Classification);
                Xunit.Assert.Equal("controlled git stderr", failure.Diagnostic.Git.StandardError);
                Xunit.Assert.Equal(21, failure.Diagnostic.Git.StandardErrorByteCount);
                break;
            case "malformed-output":
                Xunit.Assert.Equal(0, failure.Diagnostic.Git.ExitCode);
                Xunit.Assert.Equal("not-a-commit\n", failure.Diagnostic.Git.StandardOutput);
                break;
        }
    }

    [Xunit.Fact]
    public void Create_EmptyTopLevelOutput_RetainsTypedApparatusReceiptAndValidHeadBytes()
    {
        var runner = new InterceptingGitRunner(
            arguments => arguments.SequenceEqual(["rev-parse", "--show-toplevel"]),
            (_, _, result) => result with
            {
                StandardOutput = string.Empty,
                StandardOutputByteCount = 0
            });
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateTopLevel,
            failure.Diagnostic.Check);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.EmptyRequiredOutput,
            failure.Diagnostic.Git.Classification);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationFailureOwner.ProcessOutputApparatus,
            failure.Diagnostic.Owner);
        Xunit.Assert.True(failure.Diagnostic.FileSystem.HasValidHeadBytes, failure.Message);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.RepositoryHeadState.ValidLooseReference,
            failure.Diagnostic.FileSystem.HeadState);
        Xunit.Assert.Equal(0, failure.Diagnostic.Git.StandardOutputByteCount);
        Xunit.Assert.NotNull(failure.Diagnostic.Git.ChildProcessId);
        Xunit.Assert.NotNull(failure.Diagnostic.Git.ChildStartedAt);
        Xunit.Assert.NotEqual("not-assigned", failure.Diagnostic.Git.FixtureAttemptId);
        Xunit.Assert.True(failure.Diagnostic.Git.ProbeOrdinal > 0);
        Xunit.Assert.Contains(failure.Diagnostic.Git, failure.Diagnostic.ProbeReceipts!);
        Xunit.Assert.Contains(
            Mcg.AgentOrchestrator.Infrastructure.AcceptanceFailureCauseReceiptCodec.Prefix,
            failure.Message,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("The path is empty", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Create_EmptyTopLevelOutputWithMissingHead_RoutesFixturePublication()
    {
        var runner = new InterceptingGitRunner(
            arguments => arguments.SequenceEqual(["rev-parse", "--show-toplevel"]),
            (workingDirectory, _, result) =>
            {
                File.Delete(Path.Combine(workingDirectory, ".git", "HEAD"));
                return result with
                {
                    StandardOutput = string.Empty,
                    StandardOutputByteCount = 0
                };
            });
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationFailureOwner.FixturePublication,
            failure.Diagnostic.Owner);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.RepositoryHeadState.HeadMissing,
            failure.Diagnostic.FileSystem.HeadState);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.HasValidHeadBytes);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.EmptyRequiredOutput,
            failure.Diagnostic.Git.Classification);
    }

    [Xunit.Fact]
    public void Create_WhitespaceTopLevelOutput_IsInvalidRequiredOutputNotEmptyOutput()
    {
        var runner = new InterceptingGitRunner(
            arguments => arguments.SequenceEqual(["rev-parse", "--show-toplevel"]),
            (_, _, result) => result with
            {
                StandardOutput = " \r\n",
                StandardOutputByteCount = 3
            });
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.InvalidRequiredOutput,
            failure.Diagnostic.Git.Classification);
        Xunit.Assert.Equal(3, failure.Diagnostic.Git.StandardOutputByteCount);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationFailureOwner.Unknown,
            failure.Diagnostic.Owner);
        Xunit.Assert.DoesNotContain(
            Mcg.AgentOrchestrator.Infrastructure.AcceptanceFailureCauseReceiptCodec.Prefix,
            failure.Message,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Create_InvalidHeadBytes_RoutesFixturePublicationWithTypedState()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            BeforeTemplateValidation: template =>
                File.WriteAllText(Path.Combine(template, ".git", "HEAD"), "not-a-head\n")));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.RepositoryHeadState.HeadInvalid,
            failure.Diagnostic.FileSystem.HeadState);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationFailureOwner.FixturePublication,
            failure.Diagnostic.Owner);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit,
            failure.Diagnostic.Git.Classification);
    }

    [Xunit.Fact]
    public void Create_InvalidReferenceBytes_RoutesFixturePublicationWithTypedState()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            BeforeTemplateValidation: template =>
            {
                var head = File.ReadAllText(Path.Combine(template, ".git", "HEAD")).Trim();
                var reference = head["ref: ".Length..];
                File.WriteAllText(
                    Path.Combine(template, ".git", reference.Replace('/', Path.DirectorySeparatorChar)),
                    "not-an-object-id\n");
            }));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.RepositoryHeadState.ReferenceInvalid,
            failure.Diagnostic.FileSystem.HeadState);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationFailureOwner.FixturePublication,
            failure.Diagnostic.Owner);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit,
            failure.Diagnostic.Git.Classification);
        Xunit.Assert.Equal("not-an-object-id", failure.Diagnostic.FileSystem.ReferenceContent);
    }

    [Xunit.Fact]
    public void Create_ConcurrentSiblingFailureCleanup_PreservesAttemptOwnedRepository()
    {
        using var bothCopiesReady = new ManualResetEventSlim(false);
        var coordinated = 0;
        var copyCount = 0;
        var coordinationTimedOut = 0;
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) =>
            {
                if (Volatile.Read(ref coordinated) == 0)
                {
                    return;
                }

                var ordinal = Interlocked.Increment(ref copyCount);
                if (ordinal == 2)
                {
                    bothCopiesReady.Set();
                }

                if (!bothCopiesReady.Wait(TimeSpan.FromSeconds(20)))
                {
                    Volatile.Write(ref coordinationTimedOut, 1);
                    return;
                }

                if (ordinal == 1)
                {
                    File.Delete(Path.Combine(staging, ".git", "HEAD"));
                }
            }));
        _ = scope.Factory.Create();
        Volatile.Write(ref coordinated, 1);

        var first = Task.Factory.StartNew(
            () => CaptureCreate(scope.Factory),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var second = Task.Factory.StartNew(
            () => CaptureCreate(scope.Factory),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var outcomes = Task.WhenAll(first, second).GetAwaiter().GetResult();
        Xunit.Assert.Equal(0, Volatile.Read(ref coordinationTimedOut));
        Xunit.Assert.Equal(2, Volatile.Read(ref copyCount));
        var success = Xunit.Assert.Single(outcomes, outcome => outcome.Result is not null).Result!;
        var failure = Xunit.Assert.Single(outcomes, outcome => outcome.Failure is not null).Failure!;

        Xunit.Assert.NotEqual(failure.Diagnostic.FinalPath, success.PublishedIdentity.RepositoryPath);
        Xunit.Assert.DoesNotContain(
            failure.Diagnostic.Cleanup,
            cleanup => string.Equals(
                cleanup.Path,
                success.PublishedIdentity.RepositoryPath,
                StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.True(Directory.Exists(success.PublishedIdentity.GitDirectoryPath));
        var head = InfrastructureTestSupport.RunGitProbe(
            success.PublishedIdentity.RepositoryPath,
            ["rev-parse", "--verify", "HEAD^{commit}"]);
        Xunit.Assert.True(head.Succeeded, head.ToString());
        Xunit.Assert.Equal(40, head.StandardOutput.Trim().Length);
    }

    [Xunit.Fact]
    public void RunGitProbe_InjectedAmbientSelection_IsRemovedAndOptionalLocksAreDisabled()
    {
        using var scope = new FactoryScope();
        IReadOnlyDictionary<string, string?>? capturedEnvironment = null;
        var inheritedEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "controlled-path",
            ["GIT_DIR"] = "redirected-git-dir",
            ["GIT_WORK_TREE"] = "redirected-work-tree",
            ["GIT_INDEX_FILE"] = "redirected-index"
        };

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--porcelain=v1"],
            commandEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_DIR"] = "command-redirected-git-dir",
                ["GIT_OPTIONAL_LOCKS"] = "1"
            },
            startProcess: process =>
            {
                capturedEnvironment = process.StartInfo.Environment.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                return false;
            },
            inheritedEnvironment: inheritedEnvironment);

        Xunit.Assert.False(result.ProcessStarted);
        Xunit.Assert.NotNull(capturedEnvironment);
        Xunit.Assert.DoesNotContain("GIT_DIR", capturedEnvironment.Keys);
        Xunit.Assert.DoesNotContain("GIT_WORK_TREE", capturedEnvironment.Keys);
        Xunit.Assert.DoesNotContain("GIT_INDEX_FILE", capturedEnvironment.Keys);
        Xunit.Assert.Equal("0", capturedEnvironment["GIT_OPTIONAL_LOCKS"]);
    }

    [Xunit.Fact(Skip = "Requires Windows executable selection semantics.", SkipUnless = nameof(IsWindows))]
    public void RunGitProbe_CommandShimOnPath_SelectsNativeGitWithHardeningArguments()
    {
        using var scope = new FactoryScope();
        var shimDirectory = Path.Combine(scope.Root, "shim");
        var nativeDirectory = Path.Combine(scope.Root, "native");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllText(Path.Combine(shimDirectory, "git.cmd"), "@exit /b 0");
        var nativeGit = Path.Combine(nativeDirectory, "git.exe");
        File.WriteAllBytes(nativeGit, []);

        var inheritedEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, shimDirectory, nativeDirectory),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC"),
            ["TEMP"] = Environment.GetEnvironmentVariable("TEMP"),
            ["TMP"] = Environment.GetEnvironmentVariable("TMP")
        };

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["rev-parse", "--show-toplevel"],
            startProcess: _ => false,
            inheritedEnvironment: inheritedEnvironment);

        Xunit.Assert.Equal(nativeGit, result.Executable);
        Xunit.Assert.Equal(
            [
                "-c", "core.fsmonitor=false",
                "-c", "gc.auto=0",
                "-c", "maintenance.auto=false",
                "rev-parse", "--show-toplevel"
            ],
            result.Arguments);
        Xunit.Assert.DoesNotContain("git.cmd", result.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(Skip = "Requires Windows PowerShell.", SkipUnless = nameof(IsWindows))]
    public void RunGitProbe_RawStreamByteCounts_AreNotDecodedRoundTrips()
    {
        using var scope = new FactoryScope();
        var powershell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--short"],
            startProcess: process =>
            {
                process.StartInfo.FileName = powershell;
                process.StartInfo.ArgumentList.Clear();
                process.StartInfo.ArgumentList.Add("-NoProfile");
                process.StartInfo.ArgumentList.Add("-NonInteractive");
                process.StartInfo.ArgumentList.Add("-Command");
                process.StartInfo.ArgumentList.Add(
                    "$o=[Console]::OpenStandardOutput();$ob=[byte[]](239,187,191,65,255);" +
                    "$o.Write($ob,0,$ob.Length);$e=[Console]::OpenStandardError();" +
                    "$eb=[byte[]](66,255);$e.Write($eb,0,$eb.Length)");
                return process.Start();
            });

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Equal(5, result.StandardOutputByteCount);
        Xunit.Assert.Equal(2, result.StandardErrorByteCount);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.Success,
            result.Classification);
        Xunit.Assert.Equal(powershell, result.Executable);
    }

    [Xunit.Fact]
    public void Create_FailedValidation_CleansOnlyAttemptOwnedPaths()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterPublish: published => File.Delete(Path.Combine(published, ".git", "HEAD"))));
        var unrelated = Path.Combine(scope.Root, "unrelated-sibling");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.PublishedHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.HeadFileExists);
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.StagingPath!));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.FinalPath!));
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
        Xunit.Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
    }

    [Xunit.Fact]
    public void Create_CleanupFailure_IsRetainedInTypedDiagnostic()
    {
        string? publishedPath = null;
        var fileSystem = new TestFileSystem(
            path => string.Equals(path, publishedPath, StringComparison.Ordinal));
        using var scope = new FactoryScope(
            hooks: new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
                AfterPublish: published =>
                {
                    publishedPath = published;
                    File.Delete(Path.Combine(published, ".git", "HEAD"));
                }),
            fileSystem: fileSystem);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        var publishedCleanup = Xunit.Assert.Single(
            failure.Diagnostic.Cleanup,
            outcome => string.Equals(outcome.Path, failure.Diagnostic.FinalPath, StringComparison.Ordinal));
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.CleanupDisposition.Failed,
            publishedCleanup.Disposition);
        Xunit.Assert.Contains("controlled cleanup failure", publishedCleanup.Error, StringComparison.Ordinal);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.FinalPath));
        Xunit.Assert.Contains("cleanup=(", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Create_SuccessfulCopy_SurvivesSiblingFailureCleanup()
    {
        var copyCount = 0;
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) =>
            {
                if (Interlocked.Increment(ref copyCount) == 2)
                {
                    File.Delete(Path.Combine(staging, ".git", "HEAD"));
                }
            }));
        var successful = scope.Factory.Create();

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.True(Directory.Exists(successful.PublishedIdentity.RepositoryPath));
        Xunit.Assert.True(File.Exists(Path.Combine(successful.PublishedIdentity.GitDirectoryPath, "HEAD")));
        Xunit.Assert.True(File.Exists(Path.Combine(successful.TemplateIdentity.GitDirectoryPath, "HEAD")));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.StagingPath!));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.FinalPath!));
    }

    [Xunit.Fact]
    public void RunGitProbe_StartThrows_ReturnsTypedLaunchFailure()
    {
        using var scope = new FactoryScope();

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--short"],
            startProcess: _ => throw new System.ComponentModel.Win32Exception("controlled launch failure"));

        Xunit.Assert.False(result.ProcessStarted);
        Xunit.Assert.Null(result.ExitCode);
        Xunit.Assert.Equal(string.Empty, result.StandardOutput);
        Xunit.Assert.Contains("controlled launch failure", result.StandardError, StringComparison.Ordinal);
        Xunit.Assert.False(result.DrainTimedOut);
        Xunit.Assert.False(result.TimedOut);
        Xunit.Assert.False(result.DrainFailed);
        Xunit.Assert.EndsWith("git.exe", result.Executable, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Equal("status", result.Arguments![^2]);
        Xunit.Assert.Equal("--short", result.Arguments[^1]);
    }

    [Xunit.Fact]
    public void RunGitProbe_PostStartParentFault_IsNotAChildNonzeroExit()
    {
        using var scope = new FactoryScope();

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--short"],
            startProcess: _ => true);

        Xunit.Assert.True(result.ProcessStarted);
        Xunit.Assert.Null(result.ExitCode);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.ProcessObservationFailure,
            result.Classification);
    }

    [Xunit.Fact(Skip = "Requires Windows owned-file capture semantics.", SkipUnless = nameof(IsWindows))]
    public void RunGitProbe_OwnedCaptureReadFailure_RetainsCleanChildExit()
    {
        using var scope = new FactoryScope();
        var created = scope.Factory.Create();

        var result = InfrastructureTestSupport.RunGitProbe(
            created.PublishedIdentity.RepositoryPath,
            ["status", "--short"],
            beforeOwnedCaptureRead: (standardOutputPath, _) => File.Delete(standardOutputPath));

        Xunit.Assert.True(result.ProcessStarted);
        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.ProcessObservationFailure,
            result.Classification);
        Xunit.Assert.NotEmpty(result.StandardError);
    }

    [Xunit.Fact]
    public void Create_TemplateCommitBlankFailure_DoesNotRetryOrLoseReceipt()
    {
        var runner = new FirstTemplateCommitFailureGitRunner();
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.Equal(1, runner.CommitCalls);
        var receipts = failure.Diagnostic.ProbeReceipts!
            .Where(candidate => candidate.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateCommit)
            .ToArray();
        Xunit.Assert.Equal(3, receipts.Length);
        Xunit.Assert.Equal(
            Enumerable.Range(receipts[0].ProbeOrdinal, receipts.Length),
            receipts.Select(receipt => receipt.ProbeOrdinal));
        Xunit.Assert.Equal(
            2,
            receipts.Count(receipt => receipt.Arguments?.TakeLast(3).SequenceEqual(
                ["rev-parse", "--verify", "HEAD^{commit}"]) == true));
        var receipt = Xunit.Assert.Single(
            receipts,
            candidate => candidate.ExitCode == 128 &&
                candidate.Arguments?.Contains("commit", StringComparer.Ordinal) == true);
        Xunit.Assert.Equal(128, receipt.ExitCode);
        Xunit.Assert.Equal(0, receipt.StandardOutputByteCount);
        Xunit.Assert.Equal(0, receipt.StandardErrorByteCount);
    }

    [Xunit.Fact]
    public void Create_StagingAllocationThrows_ReportsTypedCheck()
    {
        using var scope = new FactoryScope(
            directoryAllocator: (root, attempt) => attempt == 2
                ? throw new IOException("controlled staging allocation failure")
                : CreateOwnedDirectory(root, attempt));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingPathAllocated,
            failure.Diagnostic.Check);
        Xunit.Assert.NotNull(failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.NotNull(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.Null(failure.Diagnostic.StagingPath);
        Xunit.Assert.Null(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains(
            "controlled staging allocation failure",
            failure.Diagnostic.Git.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
    }

    [Xunit.Fact]
    public void Create_TemplateAllocationThrows_ReportsTypedCheck()
    {
        using var scope = new FactoryScope(
            directoryAllocator: (_, _) => throw new IOException("controlled template allocation failure"));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplatePathAllocated,
            failure.Diagnostic.Check);
        Xunit.Assert.Null(failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.Null(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.Null(failure.Diagnostic.StagingPath);
        Xunit.Assert.Null(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains(
            "controlled template allocation failure",
            failure.Diagnostic.Git.StandardError,
            StringComparison.Ordinal);
    }

    private static CreateOutcome CaptureCreate(WorkerDispatchTestsSeededRepositoryFactory factory)
    {
        try
        {
            return new CreateOutcome(factory.Create(), null);
        }
        catch (WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException failure)
        {
            return new CreateOutcome(null, failure);
        }
    }

    private sealed record CreateOutcome(
        WorkerDispatchTestsSeededRepositoryFactory.CreationResult? Result,
        WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException? Failure);

    private sealed class InterceptingGitRunner : WorkerDispatchTestsSeededRepositoryFactory.IGitRunner
    {
        private readonly Func<IReadOnlyList<string>, bool> _shouldIntercept;
        private readonly Func<string, IReadOnlyList<string>,
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult,
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult> _transform;
        private int _intercepted;

        internal InterceptingGitRunner(
            Func<WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult,
                WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult> transform)
            : this(
                arguments => arguments.SequenceEqual(["rev-parse", "--verify", "HEAD^{commit}"]),
                (_, _, result) => transform(result))
        {
        }

        internal InterceptingGitRunner(
            Func<IReadOnlyList<string>, bool> shouldIntercept,
            Func<string, IReadOnlyList<string>,
                WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult,
                WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult> transform)
        {
            _shouldIntercept = shouldIntercept;
            _transform = transform;
        }

        public WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null)
        {
            var result = InfrastructureTestSupport.RunGitProbe(
                workingDirectory,
                arguments,
                commandEnvironment);
            if (result.Succeeded &&
                _shouldIntercept(arguments) &&
                Interlocked.CompareExchange(ref _intercepted, 1, 0) == 0)
            {
                return _transform(workingDirectory, arguments, result);
            }

            return result;
        }
    }

    private sealed class FirstTemplateCommitFailureGitRunner
        : WorkerDispatchTestsSeededRepositoryFactory.IGitRunner
    {
        private int _commitCalls;

        internal int CommitCalls => _commitCalls;

        public WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null)
        {
            if (arguments.Contains("commit", StringComparer.Ordinal) &&
                Interlocked.Increment(ref _commitCalls) == 1)
            {
                return new WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult(
                    $"git {string.Join(' ', arguments)}",
                    ProcessStarted: true,
                    ExitCode: 128,
                    StandardOutput: string.Empty,
                    StandardError: string.Empty,
                    DrainTimedOut: false,
                    TimedOut: false,
                    RepositoryDirectory: workingDirectory,
                    ChildProcessId: Environment.ProcessId,
                    ChildStartedAt: DateTimeOffset.UtcNow,
                    Arguments: arguments,
                    Classification:
                        WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.NonZeroExit);
            }

            return InfrastructureTestSupport.RunGitProbe(
                workingDirectory,
                arguments,
                commandEnvironment);
        }
    }

    private sealed class TestFileSystem(Func<string, bool> failDelete)
        : WorkerDispatchTestsSeededRepositoryFactory.IFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public WorkerDispatchTestsSeededRepositoryFactory.FileSystemObservation ObserveRepository(string path) =>
            new(
                Directory.Exists(path),
                Directory.Exists(Path.Combine(path, ".git")),
                File.Exists(Path.Combine(path, ".git")),
                File.Exists(Path.Combine(path, ".git", "HEAD")));

        public void CopyDirectoryContents(string source, string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        public void MoveDirectory(string source, string destination) =>
            Directory.Move(source, destination);

        public void DeleteDirectory(string path)
        {
            if (failDelete(path))
            {
                throw new IOException("controlled cleanup failure");
            }

            WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(path);
        }
    }

    private sealed class FactoryScope : IDisposable
    {
        private readonly Func<string, int, string>? _directoryAllocator;
        private int _nextDirectory;

        internal FactoryScope(
            WorkerDispatchTestsSeededRepositoryFactory.CreationHooks? hooks = null,
            Func<string, int, string>? directoryAllocator = null,
            WorkerDispatchTestsSeededRepositoryFactory.IFileSystem? fileSystem = null,
            WorkerDispatchTestsSeededRepositoryFactory.IGitRunner? gitRunner = null,
            string? root = null)
        {
            Root = root ?? CreateIsolatedFactoryRoot();
            Directory.CreateDirectory(Root);
            _directoryAllocator = directoryAllocator;
            Factory = new WorkerDispatchTestsSeededRepositoryFactory(
                AllocateDirectory,
                path => File.WriteAllText(Path.Combine(path, "seed.txt"), "seed"),
                fileSystem,
                gitRunner,
                hooks: hooks);
        }

        internal string Root { get; }

        internal WorkerDispatchTestsSeededRepositoryFactory Factory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(Root);
            }
        }

        private string AllocateDirectory()
        {
            var attempt = Interlocked.Increment(ref _nextDirectory);
            return _directoryAllocator?.Invoke(Root, attempt) ?? CreateOwnedDirectory(Root, attempt);
        }

        private static string CreateIsolatedFactoryRoot()
        {
            var processTempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var rootParent = AssemblyTempRedirect.TryParseProcessTempRootName(
                Path.GetFileName(processTempRoot),
                out _)
                    ? Path.GetDirectoryName(processTempRoot)
                        ?? throw new InvalidOperationException(
                            $"The process temp root has no parent: {processTempRoot}")
                    : processTempRoot;
            var root = Path.Combine(
                rootParent,
                $"factory-{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private static string CreateOwnedDirectory(string root, int attempt)
    {
        var path = Path.Combine(root, $"factory-owned-{attempt}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProcessInspectionRecord AvailableProcess(int processId) =>
        new(
            processId,
            ParentProcessId: 100,
            Name: "testhost",
            ExecutablePath: @"C:\host\testhost.exe",
            StartedAt: DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            CommandLine: "testhost seeded-factory-control",
            ProcessInspectionStatus.Available);

    private static WindowsNativeProcessInspection.ProcessInspectionResult Inspected(
        params ProcessInspectionRecord[] records) =>
        WindowsNativeProcessInspection.ProcessInspectionResult.Success(
            records.ToDictionary(record => record.ProcessId));
}
