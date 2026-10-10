using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;

// Parallel-safe: read-only inspection of the explicitly resolved verification worktree.
public sealed class ExecutionTestsProjectIsolationTests
{
    private const string MainProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string ExecutionProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Execution/Mcg.AgentOrchestrator.Infrastructure.Execution.Tests.csproj";

    private static readonly string[] MovedClasses =
    [
        "WorkerSandboxGitBoundaryTests",
        "BacklogSimilaritySearchTests",
        "ProtectedProcessIdentityKillGuardTests",
        "ProtectedProcessIdentityRegistrationTests",
        "ProtectedProcessIdentityStartupTests",
        "TempRootJanitorLeakedArtifactReapTests",
        "ProcessObservationRolesTests",
        "WorkerBuildLogErrorReaderTests",
        "WorkerBuildLogErrorReaderTestsSlotDirectories",
        "ModelFunctionCatalogStoreTests",
        "PracticeRegistryStoreTests",
        "BackgroundDispatchRunnerProcessSnapshotTests",
        "BackgroundDispatchRunnerSkillUsageTests",
        "DispatchProcessIdentityEvidenceProbeFailureTests",
        "DispatchProcessRecoveryServiceAccessDeniedProbeTests",
        "DispatchProcessRecoveryServiceTests",
        "DispatchWorktreeCommitterMessageTests",
        "DispatchWorktreeCommitterTests",
        "ExtendedLengthPathTests",
        "LaunchLockSummaryFormatterTests",
        "OwnedRunRootReaperTests",
        "OwnedRunRootRegistryTests",
        "ReviewerChangedExistingTestClassifierTests",
        "SpawnRegistryOwnerTransferTests",
        "TempRootJanitorDeleteTests",
        "WindowsNativeProcessInspectionTests",
        "WorkerContextArtifactsVerificationTests",
        "WorkerContextCompatibilityTests",
        "WorkerGitContextTests",
        "WorkerResultParserBacktickWrappedBlockTests",
        "WorkerResultParserEvidenceTests",
        "WorkerSkillReadParserTests",
        "WorkerSourceSurveyInventoryTests",
        "WorkerDispatchCompletionClassifierTests"
    ];

    [Fact]
    public void ExecutionProject_ReferencesOnlyCoreAndExecution()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var projectPath = Path.Combine(root, ExecutionProject);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var references = XDocument.Load(projectPath).Descendants("ProjectReference")
            .Select(reference => Path.GetRelativePath(root,
                Path.GetFullPath(Path.Combine(projectDirectory,
                    reference.Attribute("Include")!.Value.Replace('\\', '/')))).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj"
        ], references);
    }

    [Fact]
    public void ExecutionCheck_RunsWholeProjectWithTrxReporting()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/acceptance-manifest.json")));
        var checks = manifest.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        var check = Assert.Single(checks.Where(candidate =>
            candidate.TryGetProperty("project", out var project) && project.GetString() == ExecutionProject));
        Assert.Equal("execution tests", check.GetProperty("name").GetString());
        Assert.Equal("dotnet-test", check.GetProperty("type").GetString());
        Assert.Equal("mtp", check.GetProperty("runner").GetString());
        Assert.Equal(["--verbosity", "minimal"],
            check.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToArray());

        var invocation = Assert.Single(manifest.RootElement.GetProperty("engine")
            .GetProperty("mtpInvocations").EnumerateArray()
            .Where(candidate => candidate.GetProperty("project").GetString() == ExecutionProject));
        var arguments = invocation.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()).ToArray();
        Assert.Contains("--report-trx", arguments);
        Assert.Contains("--report-trx-filename", arguments);
        Assert.Contains("{trxFileName}", arguments);
        Assert.DoesNotContain(arguments, argument => argument is not null &&
            argument.StartsWith("--filter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MovedClasses_AreCompiledOnlyByExecutionProject()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var mainPath = Path.Combine(root, MainProject);
        Assert.Contains("<Compile Remove=\"Execution\\**\\*.cs\" />", File.ReadAllText(mainPath),
            StringComparison.Ordinal);

        var mainSources = CompiledSources(mainPath);
        var executionSources = CompiledSources(Path.Combine(root, ExecutionProject));
        Assert.NotEmpty(mainSources);
        Assert.NotEmpty(executionSources);

        foreach (var className in MovedClasses)
        {
            var declaration = new Regex(@"\b(?:class|record|struct)\s+" + Regex.Escape(className) + @"\b");
            Assert.DoesNotContain(mainSources, source => declaration.IsMatch(source.Value));
            var owner = Assert.Single(executionSources.Where(source => declaration.IsMatch(source.Value)));
            Assert.Equal(1, declaration.Matches(owner.Value).Count);
            Assert.Equal(className + ".cs", Path.GetFileName(owner.Key));
        }
    }

    private static Dictionary<string, string> CompiledSources(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        var project = XDocument.Load(projectPath);
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Replace('\\', '/').Split('/')
                .Any(segment => segment is "bin" or "obj"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var remove in project.Descendants("Compile").Attributes("Remove"))
        {
            foreach (var pattern in remove.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                sources.RemoveWhere(path => MatchesGlob(Path.GetRelativePath(directory, path), pattern));
        }

        foreach (var include in project.Descendants("Compile").Attributes("Include"))
        {
            // This project's explicit includes are literal files; fail loudly if that contract changes.
            Assert.DoesNotContain('*', include.Value);
            Assert.DoesNotContain('?', include.Value);
            sources.Add(Path.GetFullPath(Path.Combine(directory, include.Value.Replace('\\', '/'))));
        }

        return sources.ToDictionary(path => path, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesGlob(string path, string pattern)
    {
        var expression = Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]");
        return Regex.IsMatch(path.Replace('\\', '/'), "^" + expression + "$", RegexOptions.IgnoreCase);
    }
}
