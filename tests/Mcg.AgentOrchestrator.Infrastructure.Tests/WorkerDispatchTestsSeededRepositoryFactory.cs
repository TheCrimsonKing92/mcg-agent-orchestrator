using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerDispatchTestsSeededRepositoryFactory
{
    private static readonly DateTimeOffset SeedCommitTime =
        DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly Func<string> _directoryAllocator;
    private readonly Action<string> _seedTemplate;
    private readonly IFileSystem _fileSystem;
    private readonly IGitRunner _gitRunner;
    private readonly CreationHooks _hooks;
    private readonly Lazy<TemplateState> _template;

    internal WorkerDispatchTestsSeededRepositoryFactory(
        Func<string> directoryAllocator,
        Action<string> seedTemplate,
        IFileSystem? fileSystem = null,
        IGitRunner? gitRunner = null,
        CreationHooks? hooks = null)
    {
        _directoryAllocator = directoryAllocator;
        _seedTemplate = seedTemplate;
        _fileSystem = fileSystem ?? new PhysicalFileSystem();
        _gitRunner = gitRunner ?? new HermeticGitRunner();
        _hooks = hooks ?? new CreationHooks();
        _template = new Lazy<TemplateState>(
            CreateTemplate,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal CreationResult Create()
    {
        var attempt = FixtureAttempt.Create("create");
        var template = _template.Value;
        _hooks.BeforeTemplateValidation?.Invoke(template.Path);
        var templateBefore = ValidateRepository(
            template.Path,
            ValidationStage.Template,
            template.Path,
            stagingPath: null,
            finalPath: null,
            templateIdentity: template.Identity,
            attempt: attempt);
        EnsureSameIdentity(
            ValidationCheck.TemplateIdentityChanged,
            template.Identity,
            templateBefore,
            template.Path,
            stagingPath: null,
            finalPath: null,
            attempt);

        string? allocatedStagingPath = null;
        string? stagingPath = null;
        string? finalPath = null;
        var published = false;
        var untypedFailureCheck = ValidationCheck.StagingPathAllocated;
        RepositoryIdentity? stagingIdentity = null;
        RepositoryIdentity? finalIdentity = null;

        try
        {
            allocatedStagingPath = _directoryAllocator();
            stagingPath = CanonicalPath(allocatedStagingPath);
            var parent = Path.GetDirectoryName(stagingPath)
                ?? throw new InvalidOperationException($"Staging path has no parent: {stagingPath}");
            finalPath = CanonicalPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));

            if (!_fileSystem.DirectoryExists(stagingPath))
            {
                throw Failure(
                    ValidationCheck.StagingPathAllocated,
                    template.Path,
                    stagingPath,
                    finalPath,
                    _fileSystem.ObserveRepository(stagingPath),
                    GitProbeResult.NotRun("filesystem allocation", "The allocator did not create the staging directory."),
                    templateBefore,
                    stagingIdentity,
                    finalIdentity,
                    probeReceipts: attempt.Receipts);
            }

            if (_fileSystem.DirectoryExists(finalPath) || _fileSystem.FileExists(finalPath))
            {
                throw Failure(
                    ValidationCheck.FinalPathAvailable,
                    template.Path,
                    stagingPath,
                    finalPath,
                    _fileSystem.ObserveRepository(finalPath),
                    GitProbeResult.NotRun("filesystem publication", "The final path already exists."),
                    templateBefore,
                    stagingIdentity,
                    finalIdentity,
                    probeReceipts: attempt.Receipts);
            }

            untypedFailureCheck = ValidationCheck.StagingValidation;
            try
            {
                _fileSystem.CopyDirectoryContents(template.Path, stagingPath);
                _hooks.AfterCopy?.Invoke(template.Path, stagingPath);
            }
            catch (Exception ex)
            {
                throw Failure(
                    ValidationCheck.Copy,
                    template.Path,
                    stagingPath,
                    finalPath,
                    _fileSystem.ObserveRepository(stagingPath),
                    GitProbeResult.NotRun("filesystem copy", ex.Message),
                    templateBefore,
                    stagingIdentity,
                    finalIdentity,
                    ex,
                    attempt.Receipts);
            }

            var templateAfter = ValidateRepository(
                template.Path,
                ValidationStage.TemplateAfterCopy,
                template.Path,
                stagingPath,
                finalPath,
                templateBefore,
                attempt: attempt);
            EnsureSameIdentity(
                ValidationCheck.TemplateIdentityChangedAfterCopy,
                templateBefore,
                templateAfter,
                template.Path,
                stagingPath,
                finalPath,
                attempt);

            stagingIdentity = ValidateRepository(
                stagingPath,
                ValidationStage.Staging,
                template.Path,
                stagingPath,
                finalPath,
                templateAfter,
                attempt: attempt);
            EnsureMatchingHead(
                ValidationCheck.StagingHeadMatchesTemplate,
                templateAfter,
                stagingIdentity,
                template.Path,
                stagingPath,
                finalPath,
                attempt);

            try
            {
                _fileSystem.MoveDirectory(stagingPath, finalPath);
                published = true;
                untypedFailureCheck = ValidationCheck.PublishedValidation;
                _hooks.AfterPublish?.Invoke(finalPath);
            }
            catch (Exception ex)
            {
                throw Failure(
                    ValidationCheck.PublicationMove,
                    template.Path,
                    stagingPath,
                    finalPath,
                    _fileSystem.ObserveRepository(published ? finalPath : stagingPath),
                    GitProbeResult.NotRun("filesystem move", ex.Message),
                    templateAfter,
                    stagingIdentity,
                    finalIdentity,
                    ex,
                    attempt.Receipts);
            }

            finalIdentity = ValidateRepository(
                finalPath,
                ValidationStage.Published,
                template.Path,
                stagingPath,
                finalPath,
                templateAfter,
                stagingIdentity,
                attempt);
            EnsureMatchingHead(
                ValidationCheck.PublishedHeadMatchesTemplate,
                templateAfter,
                finalIdentity,
                template.Path,
                stagingPath,
                finalPath,
                attempt,
                stagingIdentity);

            return new CreationResult(
                templateAfter,
                finalIdentity,
                attempt.Id,
                template.ProbeReceipts.Concat(attempt.Receipts).ToArray());
        }
        catch (SeededRepositoryFailureException failure)
        {
            throw failure.WithCleanup(
                CleanupAttempt(stagingPath ?? allocatedStagingPath, finalPath, published));
        }
        catch (Exception ex)
        {
            var diagnostic = Failure(
                untypedFailureCheck,
                template.Path,
                stagingPath ?? allocatedStagingPath,
                finalPath,
                ObserveRepositorySafely(published ? finalPath : stagingPath ?? allocatedStagingPath),
                GitProbeResult.NotRun("seeded repository creation", ex.Message),
                templateBefore,
                stagingIdentity,
                finalIdentity,
                ex,
                attempt.Receipts);
            throw diagnostic.WithCleanup(
                CleanupAttempt(stagingPath ?? allocatedStagingPath, finalPath, published));
        }
    }

    internal static IReadOnlyList<GitProbeResult> RunFixtureGit(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        DateTimeOffset commitTime)
    {
        var runner = new HermeticGitRunner();
        var fixtureAttempt = FixtureAttempt.Create("fixture-git");
        var isCommit = arguments.Any(argument =>
            string.Equals(argument, "commit", StringComparison.Ordinal));
        var previousHead = isCommit
            ? TryReadHead(runner, workingDirectory, fixtureAttempt, ValidationCheck.FixtureGitCommand)
            : null;
        var result = fixtureAttempt.Record(
            runner.Run(
                workingDirectory,
                arguments,
                isCommit ? CommitEnvironment(commitTime) : null),
            workingDirectory,
            ValidationCheck.FixtureGitCommand);
        if (result.Succeeded ||
            (isCommit && HasNewCommittedCleanHead(
                runner,
                workingDirectory,
                previousHead,
                fixtureAttempt,
                ValidationCheck.FixtureGitCommand)))
        {
            return fixtureAttempt.Receipts.ToArray();
        }

        var observation = new PhysicalFileSystem().ObserveRepository(workingDirectory);
        throw Failure(
            ValidationCheck.FixtureGitCommand,
            workingDirectory,
            stagingPath: null,
            finalPath: null,
            observation,
            result,
            probeReceipts: fixtureAttempt.Receipts);
    }

    internal static void DeleteOwnedDirectory(string path)
    {
        var result = TempRootJanitor.DeleteTree(path);
        if (result.Status == TempRootJanitorDeleteStatus.Failed)
        {
            throw new IOException(
                $"Could not delete owned directory '{path}': {result.ExceptionType} at {result.FailurePath}");
        }
    }

    private TemplateState CreateTemplate()
    {
        var attempt = FixtureAttempt.Create("template");
        string? allocatedPath = null;
        string? path = null;
        var untypedFailureCheck = ValidationCheck.TemplatePathAllocated;
        try
        {
            allocatedPath = _directoryAllocator();
            path = CanonicalPath(allocatedPath);
            if (!_fileSystem.DirectoryExists(path))
            {
                throw Failure(
                    ValidationCheck.TemplatePathAllocated,
                    path,
                    stagingPath: null,
                    finalPath: null,
                    _fileSystem.ObserveRepository(path),
                    GitProbeResult.NotRun(
                        "filesystem allocation",
                        "The allocator did not create the template directory."),
                    probeReceipts: attempt.Receipts);
            }

            untypedFailureCheck = ValidationCheck.TemplateConstruction;
            RunTemplateCommand(path, ["init", "-b", "main"], ValidationCheck.TemplateInit, attempt);
            RunTemplateCommand(path, ["config", "user.email", "tests@example.com"], ValidationCheck.TemplateUserEmail, attempt);
            RunTemplateCommand(path, ["config", "user.name", "Dispatch Tests"], ValidationCheck.TemplateUserName, attempt);
            _seedTemplate(path);
            RunTemplateCommand(path, ["add", "-A"], ValidationCheck.TemplateAdd, attempt);
            RunTemplateCommand(
                path,
                ["commit", "--allow-empty", "-m", "Seed"],
                ValidationCheck.TemplateCommit,
                attempt,
                SeedCommitTime);
            var identity = ValidateRepository(
                path,
                ValidationStage.Template,
                path,
                stagingPath: null,
                finalPath: null,
                attempt: attempt);
            return new TemplateState(path, identity, attempt.Receipts.ToArray());
        }
        catch (SeededRepositoryFailureException failure)
        {
            throw failure.WithCleanup(CleanupOwnedPaths(path ?? allocatedPath));
        }
        catch (Exception ex)
        {
            var failure = Failure(
                untypedFailureCheck,
                path ?? allocatedPath,
                stagingPath: null,
                finalPath: null,
                ObserveRepositorySafely(path ?? allocatedPath),
                GitProbeResult.NotRun("template construction", ex.Message),
                innerException: ex,
                probeReceipts: attempt.Receipts);
            throw failure.WithCleanup(CleanupOwnedPaths(path ?? allocatedPath));
        }
    }

    private void RunTemplateCommand(
        string path,
        IReadOnlyList<string> arguments,
        ValidationCheck check,
        FixtureAttempt attempt,
        DateTimeOffset? commitTime = null)
    {
        var isCommit = commitTime.HasValue;
        var previousHead = isCommit ? TryReadHead(_gitRunner, path, attempt, check) : null;
        var result = attempt.Record(
            _gitRunner.Run(
                path,
                arguments,
                commitTime.HasValue ? CommitEnvironment(commitTime.Value) : null),
            path,
            check);
        if (result.Succeeded ||
            (isCommit && HasNewCommittedCleanHead(_gitRunner, path, previousHead, attempt, check)))
        {
            return;
        }

        throw Failure(
            check,
            path,
            stagingPath: null,
            finalPath: null,
            _fileSystem.ObserveRepository(path),
            result,
            probeReceipts: attempt.Receipts);
    }

    private RepositoryIdentity ValidateRepository(
        string repositoryPath,
        ValidationStage stage,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        RepositoryIdentity? templateIdentity = null,
        RepositoryIdentity? stagingIdentity = null,
        FixtureAttempt? attempt = null)
    {
        attempt ??= FixtureAttempt.Create("validation");
        var path = CanonicalPath(repositoryPath);
        var observation = _fileSystem.ObserveRepository(path);
        if (!observation.RepositoryDirectoryExists)
        {
            throw Failure(
                Check(stage, "Path"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                GitProbeResult.NotRun("filesystem repository path", "Repository directory is missing."),
                templateIdentity,
                stagingIdentity);
        }

        if (!observation.GitMetadataDirectoryExists)
        {
            throw Failure(
                Check(stage, "Metadata"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                GitProbeResult.NotRun("git rev-parse --git-dir", "Expected .git directory is missing."),
                templateIdentity,
                stagingIdentity);
        }

        var headResult = RequiredProbe(
            path,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            Check(stage, "HeadCommit"),
            sourceTemplatePath,
            stagingPath,
            finalPath,
            observation,
            templateIdentity,
            stagingIdentity,
            attempt);
        var head = SingleOutput(headResult);
        if (head.Length != 40 || head.Any(character => !Uri.IsHexDigit(character)))
        {
            headResult = attempt.Reclassify(headResult, GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, "HeadCommit"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(path),
                headResult,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var inside = RequiredProbe(
            path,
            ["rev-parse", "--is-inside-work-tree"],
            Check(stage, "InsideWorkTree"),
            sourceTemplatePath,
            stagingPath,
            finalPath,
            observation,
            templateIdentity,
            stagingIdentity,
            attempt);
        if (!string.Equals(SingleOutput(inside), "true", StringComparison.Ordinal))
        {
            inside = attempt.Reclassify(inside, GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, "InsideWorkTree"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(path),
                inside,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var topLevelResult = RequiredProbe(
            path,
            ["rev-parse", "--show-toplevel"],
            Check(stage, "TopLevel"),
            sourceTemplatePath,
            stagingPath,
            finalPath,
            observation,
            templateIdentity,
            stagingIdentity,
            attempt);
        var rawTopLevel = SingleOutput(topLevelResult);
        if (string.IsNullOrEmpty(rawTopLevel))
        {
            topLevelResult = attempt.Reclassify(topLevelResult, GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, "TopLevel"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(path),
                topLevelResult,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var topLevel = CanonicalPath(rawTopLevel);
        if (!string.Equals(topLevel, path, PathComparison))
        {
            topLevelResult = attempt.Reclassify(topLevelResult, GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, "TopLevel"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(path),
                topLevelResult,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var gitDirectoryResult = RequiredProbe(
            path,
            ["rev-parse", "--git-dir"],
            Check(stage, "GitDirectory"),
            sourceTemplatePath,
            stagingPath,
            finalPath,
            observation,
            templateIdentity,
            stagingIdentity,
            attempt);
        var rawGitDirectory = SingleOutput(gitDirectoryResult);
        var gitDirectory = CanonicalPath(Path.IsPathRooted(rawGitDirectory)
            ? rawGitDirectory
            : Path.Combine(path, rawGitDirectory));
        var expectedGitDirectory = CanonicalPath(Path.Combine(path, ".git"));
        if (!string.Equals(gitDirectory, expectedGitDirectory, PathComparison))
        {
            gitDirectoryResult = attempt.Reclassify(
                gitDirectoryResult,
                GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, "GitDirectory"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(path),
                gitDirectoryResult,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var statusResult = RequiredProbe(
            path,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            Check(stage, "Status"),
            sourceTemplatePath,
            stagingPath,
            finalPath,
            observation,
            templateIdentity,
            stagingIdentity,
            attempt,
            requiresOutput: false);
        if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            throw Failure(
                Check(stage, "Status"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                statusResult,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        return new RepositoryIdentity(path, gitDirectory, topLevel, head, statusResult.StandardOutput);
    }

    private GitProbeResult RequiredProbe(
        string path,
        IReadOnlyList<string> arguments,
        ValidationCheck check,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FileSystemObservation observation,
        RepositoryIdentity? templateIdentity,
        RepositoryIdentity? stagingIdentity,
        FixtureAttempt attempt,
        bool requiresOutput = true)
    {
        var result = _gitRunner.Run(path, arguments);
        if (result.Succeeded && requiresOutput && result.StandardOutputByteCount == 0)
        {
            result = result with { Classification = GitProbeClassification.EmptyRequiredOutput };
        }

        result = attempt.Record(result, path, check);
        if (!result.Succeeded)
        {
            throw Failure(
                check,
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                result,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        if (result.Classification == GitProbeClassification.EmptyRequiredOutput)
        {
            var failureObservation = _fileSystem.ObserveRepository(path);
            throw Failure(
                check,
                sourceTemplatePath,
                stagingPath,
                finalPath,
                failureObservation,
                result,
                templateIdentity,
                stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        return result;
    }

    private void EnsureSameIdentity(
        ValidationCheck check,
        RepositoryIdentity expected,
        RepositoryIdentity actual,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FixtureAttempt attempt)
    {
        if (expected == actual)
        {
            return;
        }

        throw Failure(
            check,
            sourceTemplatePath,
            stagingPath,
            finalPath,
            _fileSystem.ObserveRepository(actual.RepositoryPath),
            GitProbeResult.NotRun("identity comparison", $"expected={expected}; actual={actual}"),
            expected,
            actual,
            probeReceipts: attempt.Receipts);
    }

    private void EnsureMatchingHead(
        ValidationCheck check,
        RepositoryIdentity template,
        RepositoryIdentity candidate,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FixtureAttempt attempt,
        RepositoryIdentity? stagingIdentity = null)
    {
        if (string.Equals(template.HeadCommit, candidate.HeadCommit, StringComparison.Ordinal))
        {
            return;
        }

        throw Failure(
            check,
            sourceTemplatePath,
            stagingPath,
            finalPath,
            _fileSystem.ObserveRepository(candidate.RepositoryPath),
            GitProbeResult.NotRun(
                "HEAD identity comparison",
                $"templateHead={template.HeadCommit}; candidateHead={candidate.HeadCommit}"),
            template,
            stagingIdentity ?? candidate,
            finalPath is null ? null : candidate,
            probeReceipts: attempt.Receipts);
    }

    private static string SingleOutput(GitProbeResult result)
    {
        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 1 ? lines[0] : string.Empty;
    }

    private static string? TryReadHead(
        IGitRunner runner,
        string path,
        FixtureAttempt attempt,
        ValidationCheck check)
    {
        var result = attempt.Record(
            runner.Run(path, ["rev-parse", "--verify", "HEAD^{commit}"]),
            path,
            check);
        return result.Succeeded ? SingleOutput(result) : null;
    }

    private static bool HasNewCommittedCleanHead(
        IGitRunner runner,
        string path,
        string? previousHead,
        FixtureAttempt attempt,
        ValidationCheck check)
    {
        var head = attempt.Record(
            runner.Run(path, ["rev-parse", "--verify", "HEAD^{commit}"]),
            path,
            check);
        var currentHead = head.Succeeded ? SingleOutput(head) : null;
        if (string.IsNullOrWhiteSpace(currentHead) ||
            string.Equals(currentHead, previousHead, StringComparison.Ordinal))
        {
            return false;
        }

        var status = attempt.Record(
            runner.Run(path, ["status", "--porcelain=v1", "--untracked-files=all"]),
            path,
            check);
        return status.Succeeded && string.IsNullOrWhiteSpace(status.StandardOutput);
    }

    private static IReadOnlyDictionary<string, string> CommitEnvironment(DateTimeOffset commitTime) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_DATE"] = commitTime.ToString("O"),
            ["GIT_COMMITTER_DATE"] = commitTime.ToString("O")
        };

    private IReadOnlyList<CleanupOutcome> CleanupAttempt(
        string? stagingPath,
        string? finalPath,
        bool published)
    {
        return published
            ? CleanupOwnedPaths(stagingPath, finalPath)
            : CleanupOwnedPaths(stagingPath);
    }

    private IReadOnlyList<CleanupOutcome> CleanupOwnedPaths(params string?[] paths)
    {
        var outcomes = new List<CleanupOutcome>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            outcomes.Add(DeleteOwnedPath(path!));
        }

        return outcomes;
    }

    private CleanupOutcome DeleteOwnedPath(string path)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(path))
            {
                return new CleanupOutcome(path, CleanupDisposition.NotPresent, null);
            }

            _fileSystem.DeleteDirectory(path);
            return _fileSystem.DirectoryExists(path)
                ? new CleanupOutcome(path, CleanupDisposition.Failed, "Directory still exists after deletion returned.")
                : new CleanupOutcome(path, CleanupDisposition.Deleted, null);
        }
        catch (Exception ex)
        {
            return new CleanupOutcome(
                path,
                CleanupDisposition.Failed,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private FileSystemObservation ObserveRepositorySafely(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileSystemObservation(false, false, false, false);
        }

        try
        {
            return _fileSystem.ObserveRepository(path);
        }
        catch
        {
            return new FileSystemObservation(false, false, false, false);
        }
    }

    private static ValidationCheck Check(ValidationStage stage, string suffix) =>
        Enum.Parse<ValidationCheck>($"{stage}{suffix}", ignoreCase: false);

    private static string CanonicalPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static SeededRepositoryFailureException Failure(
        ValidationCheck check,
        string? sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FileSystemObservation observation,
        GitProbeResult gitResult,
        RepositoryIdentity? templateIdentity = null,
        RepositoryIdentity? stagingIdentity = null,
        RepositoryIdentity? finalIdentity = null,
        Exception? innerException = null,
        IReadOnlyList<GitProbeResult>? probeReceipts = null)
    {
        var receipts = probeReceipts?.ToArray() ??
            (gitResult.ProcessStarted ? [gitResult] : Array.Empty<GitProbeResult>());
        return new SeededRepositoryFailureException(
            new FailureDiagnostic(
                check,
                sourceTemplatePath,
                stagingPath,
                finalPath,
                templateIdentity,
                stagingIdentity,
                finalIdentity,
                observation,
                gitResult,
                Cleanup: [],
                Owner: DetermineOwner(observation, gitResult),
                ProbeReceipts: receipts),
            innerException);
    }

    private static ValidationFailureOwner DetermineOwner(
        FileSystemObservation observation,
        GitProbeResult result)
    {
        if (result.Classification is
            GitProbeClassification.LaunchFailure or
            GitProbeClassification.ProcessTimeout or
            GitProbeClassification.DrainTimeout or
            GitProbeClassification.DrainFailure or
            GitProbeClassification.ProcessObservationFailure)
        {
            return ValidationFailureOwner.ProcessOutputApparatus;
        }

        if (result.Classification == GitProbeClassification.EmptyRequiredOutput)
        {
            return observation.HasValidHeadBytes
                ? ValidationFailureOwner.ProcessOutputApparatus
                : observation.HeadState == RepositoryHeadState.HeadUnreadable
                    ? ValidationFailureOwner.Unknown
                    : ValidationFailureOwner.FixturePublication;
        }

        return observation.HeadState is
            RepositoryHeadState.RepositoryMissing or
            RepositoryHeadState.GitMetadataMissing or
            RepositoryHeadState.HeadMissing or
            RepositoryHeadState.HeadInvalid or
            RepositoryHeadState.ReferenceMissing or
            RepositoryHeadState.ReferenceInvalid
                ? ValidationFailureOwner.FixturePublication
                : ValidationFailureOwner.Unknown;
    }

    private sealed record TemplateState(
        string Path,
        RepositoryIdentity Identity,
        IReadOnlyList<GitProbeResult> ProbeReceipts);

    private sealed class FixtureAttempt(string id)
    {
        private readonly List<GitProbeResult> _receipts = [];

        internal string Id { get; } = id;

        internal IReadOnlyList<GitProbeResult> Receipts => _receipts;

        internal static FixtureAttempt Create(string prefix) =>
            new($"{prefix}-{Guid.NewGuid():N}");

        internal GitProbeResult Record(
            GitProbeResult result,
            string repositoryDirectory,
            ValidationCheck check)
        {
            var receipt = result with
            {
                RepositoryDirectory = CanonicalPath(repositoryDirectory),
                FixtureAttemptId = Id,
                ProbeOrdinal = _receipts.Count + 1,
                Check = check
            };
            _receipts.Add(receipt);
            return receipt;
        }

        internal GitProbeResult Reclassify(
            GitProbeResult result,
            GitProbeClassification classification)
        {
            var receipt = result with { Classification = classification };
            var index = _receipts.FindIndex(candidate => candidate.ProbeOrdinal == result.ProbeOrdinal);
            if (index >= 0)
            {
                _receipts[index] = receipt;
            }

            return receipt;
        }
    }

    internal sealed record CreationResult(
        RepositoryIdentity TemplateIdentity,
        RepositoryIdentity PublishedIdentity,
        string FixtureAttemptId,
        IReadOnlyList<GitProbeResult> ProbeReceipts);

    internal sealed record RepositoryIdentity(
        string RepositoryPath,
        string GitDirectoryPath,
        string TopLevelPath,
        string HeadCommit,
        string Status);

    internal sealed record GitProbeResult(
        string Command,
        bool ProcessStarted,
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        bool DrainTimedOut,
        bool TimedOut,
        bool DrainFailed = false,
        string EnvironmentContract = "not-run",
        string Executable = "git",
        IReadOnlyList<string>? Arguments = null,
        string? RepositoryDirectory = null,
        long StandardOutputByteCount = 0,
        long StandardErrorByteCount = 0,
        bool StandardOutputTruncated = false,
        bool StandardErrorTruncated = false,
        int? ChildProcessId = null,
        DateTimeOffset? ChildStartedAt = null,
        string FixtureAttemptId = "not-assigned",
        int ProbeOrdinal = 0,
        ValidationCheck? Check = null,
        GitProbeClassification Classification = GitProbeClassification.NotRun)
    {
        internal bool Succeeded =>
            ProcessStarted && ExitCode == 0 && !DrainTimedOut && !TimedOut && !DrainFailed;

        internal static GitProbeResult NotRun(string command, string reason) =>
            new(
                command,
                false,
                null,
                string.Empty,
                BoundDiagnostic(reason),
                false,
                false,
                StandardErrorByteCount: System.Text.Encoding.UTF8.GetByteCount(reason),
                StandardErrorTruncated: reason.Length > MaximumDiagnosticCharacters,
                Classification: GitProbeClassification.NotRun);

        internal static GitProbeResult NotStarted(string command, string reason) =>
            new(
                command,
                false,
                null,
                string.Empty,
                BoundDiagnostic(reason),
                false,
                false,
                StandardErrorByteCount: System.Text.Encoding.UTF8.GetByteCount(reason),
                StandardErrorTruncated: reason.Length > MaximumDiagnosticCharacters,
                Classification: GitProbeClassification.LaunchFailure);

        private const int MaximumDiagnosticCharacters = 4096;

        internal static string BoundDiagnostic(string value) =>
            value.Length <= MaximumDiagnosticCharacters ? value : value[..MaximumDiagnosticCharacters];

        internal string ChildIdentity => ChildProcessId is null
            ? "not-started"
            : $"pid={ChildProcessId}; startedAt={ChildStartedAt?.ToString("O") ?? "unknown"}";

        public override string ToString() =>
            $"executable={Executable}; arguments=[{string.Join(", ", Arguments ?? [])}]; command={Command}; " +
            $"repository={RepositoryDirectory ?? "null"}; fixtureAttempt={FixtureAttemptId}; " +
            $"probeOrdinal={ProbeOrdinal}; check={Check?.ToString() ?? "none"}; classification={Classification}; " +
            $"processStarted={ProcessStarted}; child=({ChildIdentity}); exitCode={ExitCode?.ToString() ?? "null"}; " +
            $"stdoutBytes={StandardOutputByteCount}; stdoutTruncated={StandardOutputTruncated}; stdout={StandardOutput.Trim()}; " +
            $"stderrBytes={StandardErrorByteCount}; stderrTruncated={StandardErrorTruncated}; stderr={StandardError.Trim()}; " +
            $"drainTimedOut={DrainTimedOut}; timedOut={TimedOut}; drainFailed={DrainFailed}; " +
            $"environment={EnvironmentContract}";
    }

    internal sealed record FileSystemObservation(
        bool RepositoryDirectoryExists,
        bool GitMetadataDirectoryExists,
        bool GitMetadataFileExists,
        bool HeadFileExists,
        RepositoryHeadState HeadState = RepositoryHeadState.NotObserved,
        long HeadByteCount = 0,
        string HeadContent = "",
        bool HeadContentTruncated = false,
        string? HeadReference = null,
        bool ReferenceFileExists = false,
        bool PackedReferenceExists = false,
        long ReferenceByteCount = 0,
        string ReferenceContent = "",
        bool ReferenceContentTruncated = false,
        string? ReadError = null)
    {
        internal bool HasValidHeadBytes => HeadState is
            RepositoryHeadState.ValidLooseReference or
            RepositoryHeadState.ValidPackedReference or
            RepositoryHeadState.ValidDetachedHead;

        public override string ToString() =>
            $"repositoryDirectory={RepositoryDirectoryExists}; gitDirectory={GitMetadataDirectoryExists}; " +
            $"gitFile={GitMetadataFileExists}; headFile={HeadFileExists}; headState={HeadState}; " +
            $"headBytes={HeadByteCount}; headTruncated={HeadContentTruncated}; head={HeadContent.Trim()}; " +
            $"headReference={HeadReference ?? "null"}; referenceFile={ReferenceFileExists}; " +
            $"packedReference={PackedReferenceExists}; referenceBytes={ReferenceByteCount}; " +
            $"referenceTruncated={ReferenceContentTruncated}; reference={ReferenceContent.Trim()}; " +
            $"readError={ReadError ?? "none"}";
    }

    internal sealed record FailureDiagnostic(
        ValidationCheck Check,
        string? SourceTemplatePath,
        string? StagingPath,
        string? FinalPath,
        RepositoryIdentity? TemplateIdentity,
        RepositoryIdentity? StagingIdentity,
        RepositoryIdentity? FinalIdentity,
        FileSystemObservation FileSystem,
        GitProbeResult Git,
        IReadOnlyList<CleanupOutcome> Cleanup,
        ValidationFailureOwner Owner = ValidationFailureOwner.Unknown,
        IReadOnlyList<GitProbeResult>? ProbeReceipts = null)
    {
        public override string ToString()
        {
            var causeReceipt = Owner == ValidationFailureOwner.ProcessOutputApparatus
                ? AcceptanceFailureCauseReceiptCodec.Format(new AcceptanceFailureCauseReceiptV1(
                    ContractVersion: 1,
                    Kind: "seeded-dispatch-repository-git-probe",
                    Owner: Owner.ToString(),
                    ProbeClassification: Git.Classification.ToString(),
                    ProcessStarted: Git.ProcessStarted,
                    ExitCode: Git.ExitCode,
                    StandardOutputByteCount: Git.StandardOutputByteCount,
                    StandardErrorByteCount: Git.StandardErrorByteCount,
                    DrainTimedOut: Git.DrainTimedOut,
                    TimedOut: Git.TimedOut,
                    DrainFailed: Git.DrainFailed,
                    RepositoryHeadState: FileSystem.HeadState.ToString(),
                    Check: Check.ToString(),
                    FixtureAttemptId: Git.FixtureAttemptId,
                    ProbeOrdinal: Git.ProbeOrdinal))
                : "none";
            return $"check={Check}; sourceTemplate={SourceTemplatePath ?? "null"}; staging={StagingPath ?? "null"}; " +
            $"final={FinalPath ?? "null"}; templateIdentity={TemplateIdentity?.ToString() ?? "null"}; " +
            $"stagingIdentity={StagingIdentity?.ToString() ?? "null"}; " +
            $"finalIdentity={FinalIdentity?.ToString() ?? "null"}; owner={Owner}; filesystem=({FileSystem}); git=({Git}); " +
            $"causeReceipt={causeReceipt}; probeReceipts=({string.Join(" || ", ProbeReceipts ?? [])}); " +
            $"cleanup=({string.Join(" | ", Cleanup)})";
        }
    }

    internal enum GitProbeClassification
    {
        NotRun,
        Success,
        EmptyRequiredOutput,
        NonZeroExit,
        LaunchFailure,
        ProcessTimeout,
        DrainTimeout,
        DrainFailure,
        ProcessObservationFailure,
        InvalidRequiredOutput
    }

    internal enum RepositoryHeadState
    {
        NotObserved,
        RepositoryMissing,
        GitMetadataMissing,
        HeadMissing,
        HeadUnreadable,
        HeadInvalid,
        ReferenceMissing,
        ReferenceInvalid,
        ValidLooseReference,
        ValidPackedReference,
        ValidDetachedHead
    }

    internal enum ValidationFailureOwner
    {
        Unknown,
        ProcessOutputApparatus,
        FixturePublication
    }

    internal sealed record CleanupOutcome(
        string Path,
        CleanupDisposition Disposition,
        string? Error);

    internal enum CleanupDisposition
    {
        NotPresent,
        Deleted,
        Failed
    }

    internal sealed class SeededRepositoryFailureException : InvalidOperationException
    {
        internal SeededRepositoryFailureException(FailureDiagnostic diagnostic, Exception? innerException = null)
            : base($"Seeded dispatch repository validation failed: {diagnostic}", innerException)
        {
            Diagnostic = diagnostic;
        }

        internal FailureDiagnostic Diagnostic { get; }

        internal SeededRepositoryFailureException WithCleanup(IReadOnlyList<CleanupOutcome> cleanup) =>
            new(Diagnostic with { Cleanup = cleanup }, InnerException);
    }

    internal sealed record CreationHooks(
        Action<string>? BeforeTemplateValidation = null,
        Action<string, string>? AfterCopy = null,
        Action<string>? AfterPublish = null);

    internal interface IGitRunner
    {
        GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null);
    }

    internal interface IFileSystem
    {
        bool DirectoryExists(string path);
        bool FileExists(string path);
        FileSystemObservation ObserveRepository(string path);
        void CopyDirectoryContents(string source, string destination);
        void MoveDirectory(string source, string destination);
        void DeleteDirectory(string path);
    }

    private sealed class HermeticGitRunner : IGitRunner
    {
        public GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null) =>
            InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments, commandEnvironment);
    }

    private sealed class PhysicalFileSystem : IFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public FileSystemObservation ObserveRepository(string path)
        {
            var repositoryExists = Directory.Exists(path);
            var gitPath = Path.Combine(path, ".git");
            var gitDirectoryExists = Directory.Exists(gitPath);
            var gitFileExists = File.Exists(gitPath);
            if (!repositoryExists)
            {
                return new FileSystemObservation(
                    false,
                    gitDirectoryExists,
                    gitFileExists,
                    false,
                    RepositoryHeadState.RepositoryMissing);
            }

            if (!gitDirectoryExists)
            {
                return new FileSystemObservation(
                    true,
                    false,
                    gitFileExists,
                    false,
                    RepositoryHeadState.GitMetadataMissing);
            }

            var headPath = Path.Combine(gitPath, "HEAD");
            if (!File.Exists(headPath))
            {
                return new FileSystemObservation(
                    true,
                    true,
                    gitFileExists,
                    false,
                    RepositoryHeadState.HeadMissing);
            }

            try
            {
                var headBytes = File.ReadAllBytes(headPath);
                var head = System.Text.Encoding.UTF8.GetString(headBytes).Trim();
                var boundedHead = GitProbeResult.BoundDiagnostic(head);
                if (!head.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    return new FileSystemObservation(
                        true,
                        true,
                        gitFileExists,
                        true,
                        IsObjectId(head)
                            ? RepositoryHeadState.ValidDetachedHead
                            : RepositoryHeadState.HeadInvalid,
                        headBytes.LongLength,
                        boundedHead,
                        boundedHead.Length != head.Length);
                }

                var reference = head[5..].Trim();
                if (!IsSafeReference(reference))
                {
                    return new FileSystemObservation(
                        true,
                        true,
                        gitFileExists,
                        true,
                        RepositoryHeadState.HeadInvalid,
                        headBytes.LongLength,
                        boundedHead,
                        boundedHead.Length != head.Length,
                        reference);
                }

                var referencePath = Path.Combine(gitPath, reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(referencePath))
                {
                    var referenceBytes = File.ReadAllBytes(referencePath);
                    var referenceContent = System.Text.Encoding.UTF8.GetString(referenceBytes).Trim();
                    var boundedReference = GitProbeResult.BoundDiagnostic(referenceContent);
                    return new FileSystemObservation(
                        true,
                        true,
                        gitFileExists,
                        true,
                        IsObjectId(referenceContent)
                            ? RepositoryHeadState.ValidLooseReference
                            : RepositoryHeadState.ReferenceInvalid,
                        headBytes.LongLength,
                        boundedHead,
                        boundedHead.Length != head.Length,
                        reference,
                        ReferenceFileExists: true,
                        ReferenceByteCount: referenceBytes.LongLength,
                        ReferenceContent: boundedReference,
                        ReferenceContentTruncated: boundedReference.Length != referenceContent.Length);
                }

                var packedRefsPath = Path.Combine(gitPath, "packed-refs");
                if (!File.Exists(packedRefsPath))
                {
                    return new FileSystemObservation(
                        true,
                        true,
                        gitFileExists,
                        true,
                        RepositoryHeadState.ReferenceMissing,
                        headBytes.LongLength,
                        boundedHead,
                        boundedHead.Length != head.Length,
                        reference);
                }

                var packedBytes = File.ReadAllBytes(packedRefsPath);
                var packedContent = System.Text.Encoding.UTF8.GetString(packedBytes);
                var packedValue = packedContent
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith('#') && !line.StartsWith('^'))
                    .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(parts => parts.Length == 2 &&
                        string.Equals(parts[1], reference, StringComparison.Ordinal));
                var packedObjectId = packedValue is { Length: 2 } ? packedValue[0] : string.Empty;
                var boundedPacked = GitProbeResult.BoundDiagnostic(packedObjectId);
                return new FileSystemObservation(
                    true,
                    true,
                    gitFileExists,
                    true,
                    IsObjectId(packedObjectId)
                        ? RepositoryHeadState.ValidPackedReference
                        : packedValue is { Length: 2 }
                            ? RepositoryHeadState.ReferenceInvalid
                            : RepositoryHeadState.ReferenceMissing,
                    headBytes.LongLength,
                    boundedHead,
                    boundedHead.Length != head.Length,
                    reference,
                    PackedReferenceExists: packedValue is { Length: 2 },
                    ReferenceByteCount: packedBytes.LongLength,
                    ReferenceContent: boundedPacked,
                    ReferenceContentTruncated: boundedPacked.Length != packedObjectId.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileSystemObservation(
                    true,
                    true,
                    gitFileExists,
                    true,
                    RepositoryHeadState.HeadUnreadable,
                    ReadError: $"{ex.GetType().Name}: {GitProbeResult.BoundDiagnostic(ex.Message)}");
            }
        }

        private static bool IsObjectId(string value) =>
            value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

        private static bool IsSafeReference(string value) =>
            value.StartsWith("refs/", StringComparison.Ordinal) &&
            !value.Contains("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(value);

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

        public void DeleteDirectory(string path) =>
            DeleteOwnedDirectory(path);
    }

    private enum ValidationStage
    {
        Template,
        TemplateAfterCopy,
        Staging,
        Published
    }

    internal enum ValidationCheck
    {
        TemplateConstruction,
        FixtureGitCommand,
        TemplatePathAllocated,
        TemplateInit,
        TemplateUserEmail,
        TemplateUserName,
        TemplateAdd,
        TemplateCommit,
        TemplatePath,
        TemplateMetadata,
        TemplateInsideWorkTree,
        TemplateTopLevel,
        TemplateGitDirectory,
        TemplateHeadCommit,
        TemplateStatus,
        TemplateIdentityChanged,
        TemplateAfterCopyPath,
        TemplateAfterCopyMetadata,
        TemplateAfterCopyInsideWorkTree,
        TemplateAfterCopyTopLevel,
        TemplateAfterCopyGitDirectory,
        TemplateAfterCopyHeadCommit,
        TemplateAfterCopyStatus,
        TemplateIdentityChangedAfterCopy,
        StagingPathAllocated,
        FinalPathAvailable,
        Copy,
        StagingPath,
        StagingMetadata,
        StagingInsideWorkTree,
        StagingTopLevel,
        StagingGitDirectory,
        StagingHeadCommit,
        StagingStatus,
        StagingHeadMatchesTemplate,
        PublicationMove,
        PublishedPath,
        PublishedMetadata,
        PublishedInsideWorkTree,
        PublishedTopLevel,
        PublishedGitDirectory,
        PublishedHeadCommit,
        PublishedStatus,
        PublishedHeadMatchesTemplate,
        StagingValidation,
        PublishedValidation
    }
}
