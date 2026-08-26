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
        var template = _template.Value;
        _hooks.BeforeTemplateValidation?.Invoke(template.Path);
        var templateBefore = ValidateRepository(
            template.Path,
            ValidationStage.Template,
            template.Path,
            stagingPath: null,
            finalPath: null,
            templateIdentity: template.Identity);
        EnsureSameIdentity(
            ValidationCheck.TemplateIdentityChanged,
            template.Identity,
            templateBefore,
            template.Path,
            stagingPath: null,
            finalPath: null);

        var stagingPath = CanonicalPath(_directoryAllocator());
        var parent = Path.GetDirectoryName(stagingPath)
            ?? throw new InvalidOperationException($"Staging path has no parent: {stagingPath}");
        var finalPath = CanonicalPath(Path.Combine(
            parent,
            $"{Path.GetFileName(stagingPath)}-published-{Guid.NewGuid():N}"));
        var published = false;
        RepositoryIdentity? stagingIdentity = null;
        RepositoryIdentity? finalIdentity = null;

        try
        {
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
                    finalIdentity);
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
                    finalIdentity);
            }

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
                    ex);
            }

            var templateAfter = ValidateRepository(
                template.Path,
                ValidationStage.TemplateAfterCopy,
                template.Path,
                stagingPath,
                finalPath,
                templateBefore);
            EnsureSameIdentity(
                ValidationCheck.TemplateIdentityChangedAfterCopy,
                templateBefore,
                templateAfter,
                template.Path,
                stagingPath,
                finalPath);

            stagingIdentity = ValidateRepository(
                stagingPath,
                ValidationStage.Staging,
                template.Path,
                stagingPath,
                finalPath,
                templateAfter);
            EnsureMatchingHead(
                ValidationCheck.StagingHeadMatchesTemplate,
                templateAfter,
                stagingIdentity,
                template.Path,
                stagingPath,
                finalPath);

            try
            {
                _fileSystem.MoveDirectory(stagingPath, finalPath);
                published = true;
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
                    ex);
            }

            finalIdentity = ValidateRepository(
                finalPath,
                ValidationStage.Published,
                template.Path,
                stagingPath,
                finalPath,
                templateAfter,
                stagingIdentity);
            EnsureMatchingHead(
                ValidationCheck.PublishedHeadMatchesTemplate,
                templateAfter,
                finalIdentity,
                template.Path,
                stagingPath,
                finalPath,
                stagingIdentity);

            return new CreationResult(templateAfter, finalIdentity);
        }
        catch (SeededRepositoryFailureException)
        {
            CleanupAttempt(stagingPath, finalPath, published);
            throw;
        }
        catch (Exception ex)
        {
            var diagnostic = Failure(
                published ? ValidationCheck.PublishedValidation : ValidationCheck.StagingValidation,
                template.Path,
                stagingPath,
                finalPath,
                _fileSystem.ObserveRepository(published ? finalPath : stagingPath),
                GitProbeResult.NotRun("seeded repository creation", ex.Message),
                templateBefore,
                stagingIdentity,
                finalIdentity,
                ex);
            CleanupAttempt(stagingPath, finalPath, published);
            throw diagnostic;
        }
    }

    internal static void RunFixtureGit(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        DateTimeOffset commitTime)
    {
        var runner = new HermeticGitRunner();
        var isCommit = arguments.Any(argument =>
            string.Equals(argument, "commit", StringComparison.Ordinal));
        var previousHead = isCommit ? TryReadHead(runner, workingDirectory) : null;
        GitProbeResult? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            last = runner.Run(
                workingDirectory,
                arguments,
                isCommit ? CommitEnvironment(commitTime) : null);
            if (last.Succeeded ||
                (isCommit && HasNewCommittedCleanHead(runner, workingDirectory, previousHead)))
            {
                return;
            }

            if (attempt < 2 &&
                string.IsNullOrWhiteSpace(last.StandardOutput) &&
                string.IsNullOrWhiteSpace(last.StandardError))
            {
                continue;
            }

            break;
        }

        throw new InvalidOperationException(
            $"Fixture Git command failed after typed observation: {last}");
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
        var path = CanonicalPath(_directoryAllocator());
        try
        {
            RunTemplateCommand(path, ["init", "-b", "main"], ValidationCheck.TemplateInit);
            RunTemplateCommand(path, ["config", "user.email", "tests@example.com"], ValidationCheck.TemplateUserEmail);
            RunTemplateCommand(path, ["config", "user.name", "Dispatch Tests"], ValidationCheck.TemplateUserName);
            _seedTemplate(path);
            RunTemplateCommand(path, ["add", "-A"], ValidationCheck.TemplateAdd);
            RunTemplateCommand(
                path,
                ["commit", "--allow-empty", "-m", "Seed"],
                ValidationCheck.TemplateCommit,
                SeedCommitTime);
            var identity = ValidateRepository(
                path,
                ValidationStage.Template,
                path,
                stagingPath: null,
                finalPath: null);
            return new TemplateState(path, identity);
        }
        catch (SeededRepositoryFailureException)
        {
            TryDeleteOwnedPath(path);
            throw;
        }
        catch (Exception ex)
        {
            var failure = Failure(
                ValidationCheck.TemplateConstruction,
                path,
                stagingPath: null,
                finalPath: null,
                _fileSystem.ObserveRepository(path),
                GitProbeResult.NotRun("template construction", ex.Message),
                innerException: ex);
            TryDeleteOwnedPath(path);
            throw failure;
        }
    }

    private void RunTemplateCommand(
        string path,
        IReadOnlyList<string> arguments,
        ValidationCheck check,
        DateTimeOffset? commitTime = null)
    {
        var isCommit = commitTime.HasValue;
        var previousHead = isCommit ? TryReadHead(_gitRunner, path) : null;
        GitProbeResult? result = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            result = _gitRunner.Run(
                path,
                arguments,
                commitTime.HasValue ? CommitEnvironment(commitTime.Value) : null);
            if (result.Succeeded ||
                (isCommit && HasNewCommittedCleanHead(_gitRunner, path, previousHead)))
            {
                return;
            }

            if (attempt < 2 &&
                string.IsNullOrWhiteSpace(result.StandardOutput) &&
                string.IsNullOrWhiteSpace(result.StandardError))
            {
                continue;
            }

            break;
        }

        throw Failure(
            check,
            path,
            stagingPath: null,
            finalPath: null,
            _fileSystem.ObserveRepository(path),
            result ?? GitProbeResult.NotRun($"git {string.Join(' ', arguments)}", "No Git attempt was recorded."));
    }

    private RepositoryIdentity ValidateRepository(
        string repositoryPath,
        ValidationStage stage,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        RepositoryIdentity? templateIdentity = null,
        RepositoryIdentity? stagingIdentity = null)
    {
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
            stagingIdentity);
        var head = SingleOutput(headResult);
        if (head.Length != 40 || head.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Failure(
                Check(stage, "HeadCommit"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                headResult,
                templateIdentity,
                stagingIdentity);
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
            stagingIdentity);
        if (!string.Equals(SingleOutput(inside), "true", StringComparison.Ordinal))
        {
            throw Failure(
                Check(stage, "InsideWorkTree"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                inside,
                templateIdentity,
                stagingIdentity);
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
            stagingIdentity);
        var topLevel = CanonicalPath(SingleOutput(topLevelResult));
        if (!string.Equals(topLevel, path, PathComparison))
        {
            throw Failure(
                Check(stage, "TopLevel"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                topLevelResult,
                templateIdentity,
                stagingIdentity);
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
            stagingIdentity);
        var rawGitDirectory = SingleOutput(gitDirectoryResult);
        var gitDirectory = CanonicalPath(Path.IsPathRooted(rawGitDirectory)
            ? rawGitDirectory
            : Path.Combine(path, rawGitDirectory));
        var expectedGitDirectory = CanonicalPath(Path.Combine(path, ".git"));
        if (!string.Equals(gitDirectory, expectedGitDirectory, PathComparison))
        {
            throw Failure(
                Check(stage, "GitDirectory"),
                sourceTemplatePath,
                stagingPath,
                finalPath,
                observation,
                gitDirectoryResult,
                templateIdentity,
                stagingIdentity);
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
            stagingIdentity);
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
                stagingIdentity);
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
        RepositoryIdentity? stagingIdentity)
    {
        var result = _gitRunner.Run(path, arguments);
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
                stagingIdentity);
        }

        return result;
    }

    private void EnsureSameIdentity(
        ValidationCheck check,
        RepositoryIdentity expected,
        RepositoryIdentity actual,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath)
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
            actual);
    }

    private void EnsureMatchingHead(
        ValidationCheck check,
        RepositoryIdentity template,
        RepositoryIdentity candidate,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
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
            finalPath is null ? null : candidate);
    }

    private static string SingleOutput(GitProbeResult result)
    {
        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 1 ? lines[0] : string.Empty;
    }

    private static string? TryReadHead(IGitRunner runner, string path)
    {
        var result = runner.Run(path, ["rev-parse", "--verify", "HEAD^{commit}"]);
        return result.Succeeded ? SingleOutput(result) : null;
    }

    private static bool HasNewCommittedCleanHead(IGitRunner runner, string path, string? previousHead)
    {
        var head = runner.Run(path, ["rev-parse", "--verify", "HEAD^{commit}"]);
        var currentHead = head.Succeeded ? SingleOutput(head) : null;
        if (string.IsNullOrWhiteSpace(currentHead) ||
            string.Equals(currentHead, previousHead, StringComparison.Ordinal))
        {
            return false;
        }

        var status = runner.Run(path, ["status", "--porcelain=v1", "--untracked-files=all"]);
        return status.Succeeded && string.IsNullOrWhiteSpace(status.StandardOutput);
    }

    private static IReadOnlyDictionary<string, string> CommitEnvironment(DateTimeOffset commitTime) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_DATE"] = commitTime.ToString("O"),
            ["GIT_COMMITTER_DATE"] = commitTime.ToString("O")
        };

    private void CleanupAttempt(string stagingPath, string finalPath, bool published)
    {
        TryDeleteOwnedPath(stagingPath);
        if (published)
        {
            TryDeleteOwnedPath(finalPath);
        }
    }

    private void TryDeleteOwnedPath(string path)
    {
        try
        {
            if (_fileSystem.DirectoryExists(path))
            {
                _fileSystem.DeleteDirectory(path);
            }
        }
        catch
        {
            // Preserve the original typed failure. Cleanup is bounded to factory-owned paths.
        }
    }

    private static ValidationCheck Check(ValidationStage stage, string suffix) =>
        Enum.Parse<ValidationCheck>($"{stage}{suffix}", ignoreCase: false);

    private static string CanonicalPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static SeededRepositoryFailureException Failure(
        ValidationCheck check,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FileSystemObservation observation,
        GitProbeResult gitResult,
        RepositoryIdentity? templateIdentity = null,
        RepositoryIdentity? stagingIdentity = null,
        RepositoryIdentity? finalIdentity = null,
        Exception? innerException = null) =>
        new(
            new FailureDiagnostic(
                check,
                sourceTemplatePath,
                stagingPath,
                finalPath,
                templateIdentity,
                stagingIdentity,
                finalIdentity,
                observation,
                gitResult),
            innerException);

    private sealed record TemplateState(string Path, RepositoryIdentity Identity);

    internal sealed record CreationResult(
        RepositoryIdentity TemplateIdentity,
        RepositoryIdentity PublishedIdentity);

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
        string EnvironmentContract = "not-run")
    {
        internal bool Succeeded =>
            ProcessStarted && ExitCode == 0 && !DrainTimedOut && !TimedOut && !DrainFailed;

        internal static GitProbeResult NotRun(string command, string reason) =>
            new(command, false, null, string.Empty, reason, false, false);

        internal static GitProbeResult NotStarted(string command, string reason) =>
            new(command, false, null, string.Empty, reason, false, false);

        public override string ToString() =>
            $"command={Command}; processStarted={ProcessStarted}; exitCode={ExitCode?.ToString() ?? "null"}; " +
            $"stdout={StandardOutput.Trim()}; stderr={StandardError.Trim()}; " +
            $"drainTimedOut={DrainTimedOut}; timedOut={TimedOut}; drainFailed={DrainFailed}; " +
            $"environment={EnvironmentContract}";
    }

    internal sealed record FileSystemObservation(
        bool RepositoryDirectoryExists,
        bool GitMetadataDirectoryExists,
        bool GitMetadataFileExists,
        bool HeadFileExists)
    {
        public override string ToString() =>
            $"repositoryDirectory={RepositoryDirectoryExists}; gitDirectory={GitMetadataDirectoryExists}; " +
            $"gitFile={GitMetadataFileExists}; headFile={HeadFileExists}";
    }

    internal sealed record FailureDiagnostic(
        ValidationCheck Check,
        string SourceTemplatePath,
        string? StagingPath,
        string? FinalPath,
        RepositoryIdentity? TemplateIdentity,
        RepositoryIdentity? StagingIdentity,
        RepositoryIdentity? FinalIdentity,
        FileSystemObservation FileSystem,
        GitProbeResult Git)
    {
        public override string ToString() =>
            $"check={Check}; sourceTemplate={SourceTemplatePath}; staging={StagingPath ?? "null"}; " +
            $"final={FinalPath ?? "null"}; templateIdentity={TemplateIdentity?.ToString() ?? "null"}; " +
            $"stagingIdentity={StagingIdentity?.ToString() ?? "null"}; " +
            $"finalIdentity={FinalIdentity?.ToString() ?? "null"}; filesystem=({FileSystem}); git=({Git})";
    }

    internal sealed class SeededRepositoryFailureException : InvalidOperationException
    {
        internal SeededRepositoryFailureException(FailureDiagnostic diagnostic, Exception? innerException = null)
            : base($"Seeded dispatch repository validation failed: {diagnostic}", innerException)
        {
            Diagnostic = diagnostic;
        }

        internal FailureDiagnostic Diagnostic { get; }
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

        public FileSystemObservation ObserveRepository(string path) =>
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
