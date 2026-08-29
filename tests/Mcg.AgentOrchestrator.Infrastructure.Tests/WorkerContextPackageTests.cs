using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextPackageTests
{
    private const string InfrastructureTestProjectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
    private const string RealProcessShardProbeProjectName = "Mcg.AgentOrchestrator.RealProcessShardProbe";

    private static readonly AgentRole[] AllRoles =
    [
        AgentRole.Researcher,
        AgentRole.Planner,
        AgentRole.Developer,
        AgentRole.Tester,
        AgentRole.Reviewer
    ];

    [Xunit.Theory]
    [Xunit.InlineData("preparation")]
    [Xunit.InlineData("rendering")]
    public void UndefinedDeliveryModeIsRejectedBeforePreparationOrRendering(string operation)
    {
        var bytes = Encoding.UTF8.GetBytes("complete authoritative bytes");
        Action action = () =>
        {
            var artifact = WorkerContextArtifact.Create(
                new LogicalArtifactIdentity("instructions/invalid-mode.md"),
                ContextArtifactKind.OperatorInstructions,
                bytes,
                [AgentRole.Developer],
                (ContextDeliveryMode)42,
                ContextContractVersion.V1);

            if (operation == "preparation")
            {
                _ = new WorkerContextPackageBuilder().Prepare(
                    AgentRole.Developer,
                    Path.GetTempPath(),
                    [artifact]);
            }
            else
            {
                _ = WorkerContextPackageBuilder.RenderArtifact(artifact);
            }
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("deliveryMode", error.ParamName);
    }

    [Xunit.Theory]
    [Xunit.InlineData("final newline\n")]
    [Xunit.InlineData("non-ASCII café 漢字 e\u0301\0delimiter\r\n")]
    [Xunit.InlineData("embedded\0delimiter\n")]
    public void InlineFullRecoversExactUtf8Bytes(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var artifact = Artifact("instructions/task.md", bytes, ContextDeliveryMode.InlineFull);
        var package = new WorkerContextPackageBuilder().Prepare(AgentRole.Developer, Path.GetTempPath(), [artifact]);
        var rendered = WorkerContextPackageBuilder.Render(package);
        var encoded = rendered.Split(Environment.NewLine).Last();

        var recovered = WorkerContextPackageBuilder.RecoverInlineBytes(encoded, "utf8-json");

        Assert.Equal(bytes, recovered);
        Assert.Equal(artifact.ContentHash, WorkerContextArtifact.Hash(recovered));
        Assert.Equal(1, Count(rendered, "identity=instructions/task.md"));
        Assert.DoesNotContain("MANDATORY READ: identity=instructions/task.md", rendered, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void StructuralCoverageUnicodeIdentityReconcilesAndMissingControlStaysRed()
    {
        const string unicodeIdentity =
            "WorkerContextPackageTests.InlineFullRecoversExactUtf8Bytes(content: \"non-ASCII café 漢字 e\u0301\\0delimiter\\r\\n\")";
        const string genuinelyUnexecuted =
            "WorkerContextPackageTests.InlineFullUsesBase64ForInvalidUtf8WithoutChangingBytes";
        var trx = WriteCoverageTrx(unicodeIdentity);
        try
        {
            var covered = TestCoverageInvariant.Evaluate(
                new HashSet<string>([unicodeIdentity], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("focused", true, [trx])]);

            Assert.True(covered.Passed, covered.Summary);
            Assert.Empty(covered.MissingTests);

            var filteredOut = TestCoverageInvariant.Evaluate(
                new HashSet<string>([unicodeIdentity, genuinelyUnexecuted], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("focused", true, [trx])]);

            Assert.False(filteredOut.Passed);
            Assert.Equal([genuinelyUnexecuted], filteredOut.MissingTests);
            Assert.Contains("executed=1, recorded=1, missing=1", filteredOut.Summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Xunit.Fact]
    public void StructuralCoverageMismatchKeepsBothIdentityForms()
    {
        const string discovered =
            "WorkerContextPackageTests.InlineFullRecoversExactUtf8Bytes(content: \"non-ASCII café ?? e'\\0delimiter\\r\\n\")";
        const string executed =
            "WorkerContextPackageTests.InlineFullRecoversExactUtf8Bytes(content: \"non-ASCII café 漢字 e\u0301\\0delimiter\\r\\n\")";
        const string closerButUnrelatedClass =
            "W0rkerContextPackageTests.InlineFullRecoversExactUtf8Bytes(content: \"non-ASCII café ?? e'\\0delimiter\\r\\n\")";
        var trx = WriteCoverageTrx(executed);
        var unrelatedTrx = WriteCoverageTrx(closerButUnrelatedClass);
        try
        {
            var result = TestCoverageInvariant.Evaluate(
                new HashSet<string>([discovered], StringComparer.OrdinalIgnoreCase),
                [new TestPartitionCoverage("focused", true, [trx, unrelatedTrx])]);

            Assert.False(result.Passed);
            var mismatch = Assert.Single(result.IdentityMismatches!);
            Assert.Equal(discovered, mismatch.Discovered);
            Assert.Equal(executed, mismatch.Executed);
        }
        finally
        {
            File.Delete(trx);
            File.Delete(unrelatedTrx);
        }
    }

    [Xunit.Fact]
    public void DiscoveryCaptureForcesUtf8BeforeMtpStarts()
    {
        var startInfo = BuildUtf8DiscoveryProcessStartInfo(
            ["dotnet", "tests.dll", "--list-tests", "json"],
            Path.GetTempPath(),
            @"\\.\pipe\utf8-discovery-out",
            @"\\.\pipe\utf8-discovery-err");

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("(chcp 65001 > nul &&", startInfo.Arguments, StringComparison.Ordinal);
            Assert.Contains(") > ", startInfo.Arguments, StringComparison.Ordinal);
            Assert.Contains("2> ", startInfo.Arguments, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("chcp", startInfo.ArgumentList.Last(), StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows.", SkipUnless = nameof(IsWindows))]
    public async Task Utf8DiscoveryCapturePreservesUnicodeBytesThroughOwnedNamedPipes()
    {
        const string expectedIdentity =
            "Utf8DiscoveryProbeTests.PreservesParameterizedUnicodeIdentity(content: \"non-ASCII café 漢字 e\u0301\\0delimiter\\r\\n\")";
        var dotnetHost = ResolveDotnetHostPath();
        var probeAssembly = ResolveRealProcessShardProbeAssembly();
        Xunit.Assert.Equal(
            $"{RealProcessShardProbeProjectName}.dll",
            Path.GetFileName(probeAssembly),
            ignoreCase: true);
        Xunit.Assert.DoesNotContain(
            InfrastructureTestProjectName,
            Path.GetFileName(probeAssembly),
            StringComparison.OrdinalIgnoreCase);
        var stdoutPipeName = $"mcg-utf8-discovery-{Guid.NewGuid():N}-out";
        var stderrPipeName = $"mcg-utf8-discovery-{Guid.NewGuid():N}-err";
        await using var stdoutPipe = new System.IO.Pipes.NamedPipeServerStream(
            stdoutPipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await using var stderrPipe = new System.IO.Pipes.NamedPipeServerStream(
            stderrPipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        var stdoutConnection = stdoutPipe.WaitForConnectionAsync();
        var stderrConnection = stderrPipe.WaitForConnectionAsync();
        var stdoutDrain = ConnectAndReadAsync(stdoutPipe, stdoutConnection);
        var stderrDrain = ConnectAndReadAsync(stderrPipe, stderrConnection);
        var startInfo = BuildUtf8DiscoveryProcessStartInfo(
            [
                dotnetHost,
                probeAssembly,
                "--no-ansi",
                "--progress",
                "off",
                "--list-tests",
                "json",
                "--filter-class",
                "*Utf8DiscoveryProbeTests*"
            ],
            Path.GetDirectoryName(probeAssembly)!,
            $@"\\.\pipe\{stdoutPipeName}",
            $@"\\.\pipe\{stderrPipeName}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start UTF-8 discovery capture process.");
        try
        {
            await Task.WhenAll(stdoutConnection, stderrConnection)
                .WaitAsync(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            var captures = await Task.WhenAll(stdoutDrain, stderrDrain)
                .WaitAsync(TimeSpan.FromSeconds(30));

            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var output = strictUtf8.GetString(captures[0]);
            var error = strictUtf8.GetString(captures[1]);
            Xunit.Assert.True(process.ExitCode == 0, $"Discovery exited {process.ExitCode}: {error}");
            var discovery = TestCoverageInvariant.ParseDiscovery(output, bareTestList: true);
            Xunit.Assert.Contains(expectedIdentity, discovery.Tests);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        static async Task<byte[]> ConnectAndReadAsync(Stream pipe, Task connection)
        {
            await connection;
            using var capture = new MemoryStream();
            await pipe.CopyToAsync(capture);
            return capture.ToArray();
        }
    }

    [Xunit.Fact]
    public void RealProcessShardProbeAssemblyResolutionRejectsMissingArtifact()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"mcg-missing-probe-{Guid.NewGuid():N}",
            $"{RealProcessShardProbeProjectName}.dll");

        var error = Xunit.Assert.Throws<FileNotFoundException>(
            () => ResolveRealProcessShardProbeAssembly([missingPath]));

        Xunit.Assert.Contains("Missing prebuilt MTP probe assembly", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(missingPath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void RealProcessShardProbeAssemblyResolutionRejectsAmbiguousArtifacts()
    {
        var root = CreateTempDirectory();
        var first = Path.Combine(root, "first", $"{RealProcessShardProbeProjectName}.dll");
        var second = Path.Combine(root, "second", $"{RealProcessShardProbeProjectName}.dll");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(second, string.Empty);

            var error = Xunit.Assert.Throws<InvalidOperationException>(
                () => ResolveRealProcessShardProbeAssembly([first, second]));

            Xunit.Assert.Contains("Ambiguous prebuilt MTP probe assemblies", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains(first, error.Message, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Contains(second, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void RealProcessShardProbeAssemblyCandidatesPreferIsolatedSiblingOverRepositoryFallback()
    {
        var root = CreateTempDirectory();
        try
        {
            var infrastructureOutput = Path.Combine(
                root,
                "artifacts",
                "bin",
                InfrastructureTestProjectName,
                "debug");

            var candidate = Xunit.Assert.Single(
                BuildRealProcessShardProbeAssemblyCandidates(infrastructureOutput, root));

            Xunit.Assert.Equal(
                Path.Combine(
                    root,
                    "artifacts",
                    "bin",
                    RealProcessShardProbeProjectName,
                    "debug",
                    $"{RealProcessShardProbeProjectName}.dll"),
                candidate,
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    private static ProcessStartInfo BuildUtf8DiscoveryProcessStartInfo(
        string[] command,
        string workingDirectory,
        string stdoutPipePath,
        string stderrPipePath) =>
        GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(
            command,
            workingDirectory,
            stdoutPipePath,
            stderrPipePath,
            forceUtf8ConsoleOutput: true);

    private static string ResolveRealProcessShardProbeAssembly(IReadOnlyList<string>? candidates = null)
    {
        var candidatePaths = (candidates ?? BuildRealProcessShardProbeAssemblyCandidates())
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existingPaths = candidatePaths.Where(File.Exists).ToArray();
        return existingPaths.Length switch
        {
            1 => existingPaths[0],
            0 => throw new FileNotFoundException(
                $"Missing prebuilt MTP probe assembly. Checked: {string.Join(", ", candidatePaths)}"),
            _ => throw new InvalidOperationException(
                $"Ambiguous prebuilt MTP probe assemblies: {string.Join(", ", existingPaths)}")
        };
    }

    private static IReadOnlyList<string> BuildRealProcessShardProbeAssemblyCandidates(
        string? baseDirectory = null,
        string? repositoryRoot = null)
    {
        var infrastructureOutput = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory);
        var configurationDirectory = infrastructureOutput.Name.Equals("net10.0", StringComparison.OrdinalIgnoreCase)
            ? infrastructureOutput.Parent
                ?? throw new InvalidOperationException("Infrastructure test output has no configuration directory.")
            : infrastructureOutput;
        var configuration = configurationDirectory.Name;
        var assemblyName = $"{RealProcessShardProbeProjectName}.dll";
        var isolatedProjectDirectory = configurationDirectory.Parent;
        if (isolatedProjectDirectory?.Name.Equals(InfrastructureTestProjectName, StringComparison.OrdinalIgnoreCase) == true &&
            isolatedProjectDirectory.Parent?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true)
        {
            // The sibling probe belongs to this isolated test build; a repository output may be stale.
            return
            [
                Path.Combine(
                    isolatedProjectDirectory.Parent.FullName,
                    RealProcessShardProbeProjectName,
                    configuration,
                    assemblyName)
            ];
        }

        return
        [
            Path.Combine(
                repositoryRoot ?? InfrastructureTestSupport.FindRepositoryRoot(),
                "tests",
                InfrastructureTestProjectName,
                "Fixtures",
                "RealProcessShardProbe",
                "bin",
                configuration,
                "net10.0",
                assemblyName)
        ];
    }

    [Xunit.Fact]
    public void InlineFullUsesBase64ForInvalidUtf8WithoutChangingBytes()
    {
        byte[] bytes = [0xff, 0x00, 0xc3, 0x28, 0x0a];
        var package = new WorkerContextPackageBuilder().Prepare(
            AgentRole.Tester,
            Path.GetTempPath(),
            [Artifact("evidence/binary.bin", bytes, ContextDeliveryMode.InlineFull)]);
        var rendered = WorkerContextPackageBuilder.Render(package);

        var recovered = WorkerContextPackageBuilder.RecoverInlineBytes(rendered.Split(Environment.NewLine).Last(), "base64");

        Assert.Equal(bytes, recovered);
        Assert.Contains("encoding=base64", rendered, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MandatoryFileIsVerifiedAndNamedWithIdentityAndHash()
    {
        var root = CreateTempDirectory();
        try
        {
            byte[][] cases =
            [
                [],
                Encoding.UTF8.GetBytes("final newline\n"),
                Encoding.UTF8.GetBytes("non-ASCII café 漢字 e\u0301\r\n"),
                [0x00, 0xff, 0xc3, 0x28, 0x0a]
            ];
            for (var index = 0; index < cases.Length; index++)
            {
                var bytes = cases[index];
                var relativePath = $"planner-plan-{index}.bin";
                File.WriteAllBytes(Path.Combine(root, relativePath), bytes);
                var identity = $"plans/planner-plan-{index}.bin";
                var artifact = Artifact(identity, bytes, ContextDeliveryMode.MandatoryFile, relativePath);

                var package = new WorkerContextPackageBuilder().Prepare(AgentRole.Developer, root, [artifact]);
                var rendered = WorkerContextPackageBuilder.Render(package);
                var recovered = File.ReadAllBytes(Path.Combine(root, relativePath));

                Assert.Equal(ContextDeliveryMode.MandatoryFile, Assert.Single(package.Artifacts).DeliveryMode);
                Assert.Contains($"MANDATORY READ: identity={identity}", rendered, StringComparison.Ordinal);
                Assert.Contains($"sha256={artifact.ContentHash}", rendered, StringComparison.Ordinal);
                Assert.Equal(bytes, recovered);
                Assert.Equal(artifact.ContentHash, WorkerContextArtifact.Hash(recovered));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InvalidMandatoryFileFallsBackToCompleteInlineBytesAndReidentifiesPackage()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("authoritative fallback\n");
            var mandatory = Artifact("research/research-notes.md", bytes, ContextDeliveryMode.MandatoryFile, "missing.md");
            var builder = new WorkerContextPackageBuilder();

            var package = builder.Prepare(AgentRole.Reviewer, root, [mandatory]);
            var inline = Artifact("research/research-notes.md", bytes, ContextDeliveryMode.InlineFull);
            var directlyInline = builder.Prepare(AgentRole.Reviewer, root, [inline]);

            var effective = Assert.Single(package.Artifacts);
            Assert.Equal(ContextDeliveryMode.InlineFull, effective.DeliveryMode);
            Assert.Equal("missing", effective.FallbackReason);
            Assert.Equal(directlyInline.SemanticPackageId, package.SemanticPackageId);
            Assert.DoesNotContain("MANDATORY READ", WorkerContextPackageBuilder.Render(package), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InvalidMandatoryFileWithoutAuthoritativeBytesFailsClosed()
    {
        var hash = WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("unavailable"));
        var pointer = WorkerContextArtifact.Create(
            new LogicalArtifactIdentity("prior/task-evidence.md"),
            ContextArtifactKind.PriorTaskEvidence,
            null,
            AllRoles,
            ContextDeliveryMode.MandatoryFile,
            ContextContractVersion.V1,
            "missing.md",
            hash);

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            new WorkerContextPackageBuilder().Prepare(AgentRole.Reviewer, Path.GetTempPath(), [pointer]));

        Assert.Equal("missing", error.Reason);
    }

    [Xunit.Fact]
    public void SemanticIdentityIsOrderRootAndTimestampIndependentAndChangesForEveryTupleField()
    {
        var builder = new WorkerContextPackageBuilder();
        var first = Artifact("a/context.md", Encoding.UTF8.GetBytes("same"), ContextDeliveryMode.InlineFull);
        var second = Artifact("b/context.md", Encoding.UTF8.GetBytes("other"), ContextDeliveryMode.InlineFull);
        var package = builder.Prepare(AgentRole.Developer, Path.Combine(Path.GetTempPath(), "root-a"), [first, second]);
        var reordered = builder.Prepare(AgentRole.Developer, Path.Combine(Path.GetTempPath(), "root-b"), [second, first]);

        Assert.Equal(package.SemanticPackageId, reordered.SemanticPackageId);
        Assert.StartsWith("ctxpkg-v1-sha256:", package.SemanticPackageId, StringComparison.Ordinal);
        Assert.NotEqual(package.SemanticPackageId, builder.Prepare(AgentRole.Developer, Path.GetTempPath(), [Artifact("a/context.md", Encoding.UTF8.GetBytes("changed"), ContextDeliveryMode.InlineFull), second]).SemanticPackageId);
        Assert.NotEqual(package.SemanticPackageId, builder.Prepare(AgentRole.Developer, Path.GetTempPath(), [Artifact("renamed/context.md", Encoding.UTF8.GetBytes("same"), ContextDeliveryMode.InlineFull), second]).SemanticPackageId);
        Assert.NotEqual(package.SemanticPackageId, builder.Prepare(AgentRole.Developer, Path.GetTempPath(), [WorkerContextArtifact.Create(first.Identity, first.Kind, first.AuthoritativeBytes, [AgentRole.Developer], first.DeliveryMode, first.ContractVersion), second]).SemanticPackageId);
        Assert.NotEqual(
            package.SemanticPackageId,
            WorkerContextPackageBuilder.ComputeSemanticPackageId(
            [
                WorkerContextArtifact.Create(
                    first.Identity,
                    ContextArtifactKind.RegisteredContext,
                    first.AuthoritativeBytes,
                    first.RoleVisibility,
                    ContextDeliveryMode.MandatoryFile,
                    first.ContractVersion,
                    "context.md"),
                second
            ]));
        Assert.NotEqual(
            package.SemanticPackageId,
            WorkerContextPackageBuilder.ComputeSemanticPackageId(
            [
                WorkerContextArtifact.Create(
                    first.Identity,
                    first.Kind,
                    first.AuthoritativeBytes,
                    first.RoleVisibility,
                    ContextDeliveryMode.InlineFull,
                    new ContextContractVersion(2)),
                second
            ]));
    }

    [Xunit.Fact(DisplayName = "BuildContextPackage_reviewer_materializes_complete_large_changed_and_conflict_scopes")]
    public void BuildContextPackageReviewerMaterializesCompleteLargeChangedAndConflictScopes()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            WriteEmptyRegistry(contextDirectory);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Review every changed path.", AgentRole.Reviewer);
            var goal = kernel.CreateGoal("Preserve complete reviewer scope", [task]);
            var changedPaths = Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles + 1)
                .Select(index => $"src/changed-{index:D3}-✓.cs")
                .ToArray();
            var conflictPaths = Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles + 2)
                .Select(index => $"src/conflict-{index:D3}-漢.cs")
                .ToArray();
            var inlinePreview = string.Join(Environment.NewLine, changedPaths.Take(WorkerGitContext.ReviewerChangedFilePromptMaxFiles));
            var brief = BriefFor(
                goal,
                task,
                $"## Reviewer Changed-File Scope{Environment.NewLine}{inlinePreview}{Environment.NewLine}- Omitted 1 additional changed file.");

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                task,
                root,
                contextDirectory,
                brief,
                reviewerScopeChangedFiles: changedPaths,
                reviewerScopeMergeBase: new string('a', 40),
                reviewerScopeTotalChangedFileCount: changedPaths.Length,
                reviewerMergeTreeClean: false,
                reviewerMergeTreeConflictPaths: conflictPaths,
                reviewerMergeTreeTotalConflictPathCount: conflictPaths.Length);

            var changedArtifact = Assert.Single(package.Artifacts.Where(artifact =>
                artifact.Identity.Value == "reviewer/changed-files.v1.json"));
            var conflictArtifact = Assert.Single(package.Artifacts.Where(artifact =>
                artifact.Identity.Value == "reviewer/merge-conflict-paths.v1.json"));
            Assert.Equal(ContextDeliveryMode.MandatoryFile, changedArtifact.DeliveryMode);
            Assert.Equal(ContextDeliveryMode.MandatoryFile, conflictArtifact.DeliveryMode);
            var changedBytes = Recover(root, changedArtifact);
            var conflictBytes = Recover(root, conflictArtifact);
            Assert.Equal(changedArtifact.ContentHash, WorkerContextArtifact.Hash(changedBytes));
            Assert.Equal(conflictArtifact.ContentHash, WorkerContextArtifact.Hash(conflictBytes));
            using var changedDocument = System.Text.Json.JsonDocument.Parse(changedBytes);
            using var conflictDocument = System.Text.Json.JsonDocument.Parse(conflictBytes);
            var recoveredChangedPaths = changedDocument.RootElement.GetProperty("Paths")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray();
            var recoveredConflictPaths = conflictDocument.RootElement.GetProperty("Paths")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray();
            Assert.Equal(changedPaths, recoveredChangedPaths);
            Assert.Equal(conflictPaths, recoveredConflictPaths);

            var rendered = WorkerContextPackageBuilder.Render(package);
            Assert.Contains(
                $"MANDATORY READ: identity={changedArtifact.Identity.Value}; path={changedArtifact.MandatoryRelativePath}; sha256={changedArtifact.ContentHash}",
                rendered,
                StringComparison.Ordinal);
            Assert.Contains(
                $"MANDATORY READ: identity={conflictArtifact.Identity.Value}; path={conflictArtifact.MandatoryRelativePath}; sha256={conflictArtifact.ContentHash}",
                rendered,
                StringComparison.Ordinal);
            Assert.DoesNotContain(changedPaths[0], rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(changedPaths[^1], rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(conflictPaths[0], rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(conflictPaths[^1], rendered, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "BuildContextPackage_reviewer_rejects_capped_scope_without_complete_mandatory_bytes")]
    public void BuildContextPackageReviewerRejectsCappedScopeWithoutCompleteMandatoryBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            WriteEmptyRegistry(contextDirectory);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Review every changed path.", AgentRole.Reviewer);
            var goal = kernel.CreateGoal("Reject incomplete reviewer scope", [task]);
            var cappedPreview = Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles)
                .Select(index => $"src/changed-{index:D3}.cs")
                .ToArray();

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                WorkerProfileDispatcher.BuildContextPackage(
                    goal,
                    task,
                    root,
                    contextDirectory,
                    BriefFor(goal, task, "review complete scope"),
                    reviewerScopeChangedFiles: cappedPreview,
                    reviewerScopeMergeBase: new string('a', 40),
                    reviewerScopeTotalChangedFileCount: cappedPreview.Length + 1,
                    reviewerMergeTreeClean: true,
                    reviewerMergeTreeConflictPaths: [],
                    reviewerMergeTreeTotalConflictPathCount: 0));

            Assert.Equal("reviewer-scope-incomplete", error.Reason);
            Assert.Equal("reviewer/changed-files.v1.json", error.Identity.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ReviewFindingHistoryKeepsOneCanonicalEntryPerStableId()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            WriteEmptyRegistry(contextDirectory);
            var firstTask = new TaskSpec(TaskId.New(), "First review", AgentRole.Reviewer);
            var secondTask = new TaskSpec(TaskId.New(), "Second review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Deduplicate only exact review snapshots", [firstTask, secondTask]);

            static TaskVerificationRecord Verification(string fingerprint) => new(
                "review",
                ".",
                0,
                "review complete",
                string.Empty,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                MergedReviewFindings:
                [
                    new ReviewFinding(
                        "stable-finding",
                        ReviewFindingState.Open,
                        new ReviewFindingLocation("src/One.cs", "One.Run"),
                        "finding description")
                ],
                FindingEvidenceReceipts:
                [
                    new FindingEvidenceReceipt(
                        $"receipt-{fingerprint}",
                        "candidate-sha",
                        new FindingEvidenceRequest(
                            [new FindingEvidenceSelection("tests/Tests.csproj", "Tests.One")]),
                        true,
                        true,
                        "receipt summary",
                        FindingRoundFingerprint: fingerprint)
                ],
                FullStandardOutput: "review complete",
                FullStandardError: string.Empty);

            kernel.RecordTaskVerification(goal.Id, firstTask.Id, Verification("round-one"));
            kernel.RecordTaskVerification(goal.Id, firstTask.Id, Verification("round-one"));
            kernel.RecordTaskVerification(goal.Id, firstTask.Id, Verification("round-two"));
            kernel.RecordTaskVerification(goal.Id, secondTask.Id, Verification("round-one"));

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                secondTask,
                root,
                contextDirectory,
                BriefFor(goal, secondTask, "inspect review history"));

            var historyArtifact = Assert.Single(package.Artifacts.Where(candidate =>
                candidate.Identity.Value == "goal/review-finding-history.json"));
            using var document = System.Text.Json.JsonDocument.Parse(Recover(root, historyArtifact));
            var entries = document.RootElement.GetProperty("findings").EnumerateArray().ToArray();

            Assert.Single(entries);
            Assert.Equal("stable-finding", entries[0].GetProperty("stable_id").GetString());
            Assert.Equal(4, document.RootElement.GetProperty("rounds").GetArrayLength());
            var receiptReferences = document.RootElement.GetProperty("receipt_bodies").EnumerateArray().ToArray();
            Assert.Equal(2, receiptReferences.Length);
            Assert.All(receiptReferences, reference =>
            {
                var identity = reference.GetProperty("logical_identity").GetString()!;
                var body = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == identity);
                Assert.Equal(reference.GetProperty("sha256").GetString(), body.ContentHash);
                _ = Recover(root, body);
            });
            var receipt = WorkerContextPackageBuilder.CreateReceipt(package);
            Assert.Equal(2, receipt.UniqueReviewFindingRoundCount);
            Assert.Equal(2, receipt.DuplicateReviewFindingRoundCount);
            Assert.Equal(2, receipt.UniqueFindingEvidenceReceiptCount);
            Assert.Equal(2, receipt.DuplicateFindingEvidenceReceiptCount);
            Assert.True(receipt.RenderedPromptBytes > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ExhaustiveSemanticSourceInventoryRecoversEveryAuthoritativeSourceAndHash()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            Directory.CreateDirectory(contextDirectory);
            var registryArtifacts = WorkerProfileDispatcher.DeliverableRegistryArtifactPaths
                .Select((path, index) => new
                {
                    Path = path,
                    Marker = $"registry-source-{index:D2}-{path}-✓",
                    Bytes = Encoding.UTF8.GetBytes($"registry-source-{index:D2}-{path}-✓\r\nfinal newline\n")
                })
                .ToArray();
            foreach (var artifact in registryArtifacts)
            {
                var artifactPath = Path.Combine(
                    contextDirectory,
                    artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
                File.WriteAllBytes(artifactPath, artifact.Bytes);
            }
            File.WriteAllText(
                Path.Combine(contextDirectory, "artifact-registry.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    artifacts = registryArtifacts.Select(artifact => new
                    {
                        path = artifact.Path,
                        exists = true,
                        sha256 = WorkerContextArtifact.Hash(artifact.Bytes),
                        roleVisibility = new[] { "Developer" },
                        hashVerified = true
                    }).ToArray()
                }));
            var kernel = new AgentOrchestratorKernel();
            var priorTask = new TaskSpec(TaskId.New(), "Research upstream behavior", AgentRole.Researcher);
            var task = new TaskSpec(
                TaskId.New(),
                "Implement",
                AgentRole.Developer,
                "required-instruction-source-✓");
            var goal = kernel.CreateGoal("Preserve exact semantic sources", [priorTask, task]);
            const string criterionOne = "acceptance-criterion-source-one";
            const string criterionTwo = "acceptance-criterion-source-two";
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "behavioral-contract-source-✓",
                [criterionOne, criterionTwo],
                VerificationClass.TestVerifiable,
                [new RefinedSpecDecision("decision-question-source", "decision-choice-source", "decision-rationale-source")],
                [new RefinedSpecOpenQuestion("open-question-id", "open-question-source", "contract", "Open")])
            {
                OperatorOwnedAcceptanceCriteria = ["operator-owned-criterion-source"]
            });
            const string causalEvent = "causal-event-source-✓";
            kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Running, causalEvent);
            kernel.ReportTaskProgress(
                goal.Id,
                priorTask.Id,
                WorkTaskStatus.Running,
                "CRITERIA CORRECTION: supersedes=obsolete-criterion-source; correction=effective-correction-source");
            kernel.RecordAcceptanceFailure(
                goal.Id,
                ["acceptance-failure-source"],
                branchHeadSha: "branch-head-source",
                mainHeadSha: "main-head-source");
            kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["criterion-retry-feedback-source"]);
            var evidenceRequest = new FindingEvidenceRequest(
                [new FindingEvidenceSelection("evidence-project-source", "evidence-class-source")]);
            var findings = new[]
            {
                new ReviewFinding(
                    "finding-source-one",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/One.cs", "One.Run"),
                    "finding-description-source-one",
                    EvidenceRequest: evidenceRequest,
                    EvidenceOutcome: new FindingEvidenceOutcome(
                        true,
                        "evidence-outcome-receipt-source",
                        Detail: "evidence-outcome-detail-source",
                        ResultReason: FindingEvidenceOutcomeReason.ValidEvidence)),
                new ReviewFinding(
                    "finding-source-two",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Two.cs", "Two.Run"),
                    "finding-description-source-two")
            };
            const string priorResult = "prior-task-result-source-✓\r\n";
            kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
                "research",
                root,
                0,
                priorResult,
                string.Empty,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                MergedReviewFindings: findings,
                FindingEvidenceReceipts:
                [
                    new FindingEvidenceReceipt(
                        "evidence-receipt-source",
                        "candidate-sha-source",
                        evidenceRequest,
                        true,
                        true,
                        "evidence-receipt-summary-source")
                ],
                FullStandardOutput: priorResult,
                FullStandardError: string.Empty));
            const string currentVerificationOutput = "current-verification-output-source-✓\n";
            const string currentVerificationError = "current-verification-error-source-漢字\r\n";
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify-current",
                root,
                1,
                currentVerificationOutput,
                currentVerificationError,
                DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
                FullStandardOutput: currentVerificationOutput,
                FullStandardError: currentVerificationError));
            File.WriteAllText(
                Path.Combine(root, ".orchestrator-handoff.md"),
                string.Join("\r\n",
                    "# Prior Task Handoff",
                    string.Empty,
                    "## Planner: compatibility-source",
                    string.Empty,
                    "### Verification Output",
                    "legacy-handoff-source-✓",
                    string.Empty,
                    "---",
                    string.Empty));
            var brief = BriefFor(
                goal,
                task,
                "required-brief-instruction-source-✓",
                "header-residual-instruction-source-✓");
            var observations = new List<WorkerProfileDispatcher.SemanticSourceObservation>();

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                task,
                root,
                contextDirectory,
                brief,
                observedSources: observations);

            var findingHistory = Assert.Single(package.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            using var findingHistoryDocument = System.Text.Json.JsonDocument.Parse(Recover(root, findingHistory));
            var recoveredReceiptBodies = findingHistoryDocument.RootElement.GetProperty("receipt_bodies")
                .EnumerateArray()
                .Select(reference =>
                {
                    var identity = reference.GetProperty("logical_identity").GetString()!;
                    var artifact = Assert.Single(package.Artifacts, candidate => candidate.Identity.Value == identity);
                    Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
                    var bytes = Recover(root, artifact);
                    Assert.Equal(reference.GetProperty("sha256").GetString(), WorkerContextArtifact.Hash(bytes));
                    return bytes;
                })
                .ToArray();
            Assert.Contains(recoveredReceiptBodies,
                bytes => Encoding.UTF8.GetString(bytes).Contains("evidence-receipt-summary-source", StringComparison.Ordinal));

            var expectedSources = new List<ExpectedSemanticSource>
            {
                new("goal objective", "goal/objective.md", "Preserve exact semantic sources"),
                new("task description", "task/description.md", "Implement"),
                new("task metadata", "task/metadata.json", task.Id.Value),
                new("behavioral contract", "goal/refined-spec.json", "behavioral-contract-source-✓"),
                new ExpectedSemanticSource("acceptance criterion one", "goal/refined-spec.json", criterionOne),
                new ExpectedSemanticSource("acceptance criterion two", "goal/refined-spec.json", criterionTwo),
                new("decision", "goal/refined-spec.json", "decision-choice-source"),
                new("open question", "goal/refined-spec.json", "open-question-source"),
                new("operator-owned criterion", "goal/refined-spec.json", "operator-owned-criterion-source"),
                new("effective correction", "goal/effective-acceptance-criteria-corrections.json", "effective-correction-source"),
                new("acceptance failure", "goal/latest-acceptance-failure.json", "acceptance-failure-source"),
                new ExpectedSemanticSource("finding one", "goal/review-finding-history.json", "finding-source-one"),
                new ExpectedSemanticSource("finding two", "goal/review-finding-history.json", "finding-source-two"),
                new ExpectedSemanticSource("evidence request", "goal/review-finding-history.json", "evidence-class-source"),
                new ExpectedSemanticSource("evidence outcome", "goal/review-finding-history.json", "evidence-outcome-detail-source"),
                new ExpectedSemanticSource("causal event", "goal/timeline.json", causalEvent),
                new ExpectedSemanticSource("prior-task result", $"prior/{priorTask.Id.Value}/verification-output", priorResult),
                new ExpectedSemanticSource("required instruction", "task/verification-plan.md", "required-instruction-source-✓"),
                new("criterion retry feedback", "task/criterion-retry-feedback.json", "criterion-retry-feedback-source"),
                new("current verification output", "task/last-verification/stdout", currentVerificationOutput),
                new("current verification error", "task/last-verification/stderr", currentVerificationError),
                new("legacy handoff", "legacy-handoff/v0/1", "legacy-handoff-source-✓"),
                new("header residual", "brief/header-residual.md", "header-residual-instruction-source-✓"),
                new ExpectedSemanticSource("brief instruction", "brief/current.md", "required-brief-instruction-source-✓")
            };
            expectedSources.AddRange(registryArtifacts.Select(artifact => new ExpectedSemanticSource(
                $"registered artifact {artifact.Path}",
                $"context/{artifact.Path}",
                artifact.Marker)));

            Assert.All(expectedSources, expected =>
            {
                var artifact = Assert.Single(package.Artifacts.Where(candidate =>
                    candidate.Identity.Value == expected.LogicalIdentity));
                var recovered = Recover(root, artifact);
                Assert.Equal(artifact.ContentHash, WorkerContextArtifact.Hash(recovered));
                AssertRecoveredContainsSemanticValue(recovered, expected.Marker);
            });

            var dispatchRoot = Path.Combine(root, "dispatch-scenario");
            var dispatchContext = Path.Combine(dispatchRoot, ".orchestrator-context", "goal");
            WriteEmptyRegistry(dispatchContext);
            var dispatchKernel = new AgentOrchestratorKernel();
            var dispatchTask = new TaskSpec(TaskId.New(), "Dispatch source", AgentRole.Developer);
            var dispatchGoal = dispatchKernel.CreateGoal("Dispatch semantic source", [dispatchTask]);
            var developer = AgentCatalog.Default().GetRequired(AgentRole.Developer);
            dispatchKernel.ActivateGoal(dispatchGoal.Id, [developer]);
            dispatchKernel.RecordTaskDispatch(
                dispatchGoal.Id,
                dispatchTask.Id,
                new TaskDispatchRecord(
                    "dispatch-worker-source",
                    "dispatch-command-source",
                    dispatchRoot,
                    DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
                    ProviderName: "OpenAI",
                    ModelName: AgentCatalog.OpenAiSolSubscriptionModelAlias));
            var dispatchObservations = new List<WorkerProfileDispatcher.SemanticSourceObservation>();
            var dispatchPackage = WorkerProfileDispatcher.BuildContextPackage(
                dispatchGoal,
                dispatchTask,
                dispatchRoot,
                dispatchContext,
                BriefFor(dispatchGoal, dispatchTask, "dispatch-instruction-source"),
                observedSources: dispatchObservations);
            var dispatchArtifact = Assert.Single(dispatchPackage.Artifacts,
                candidate => candidate.Identity.Value == "task/last-dispatch.json");
            var dispatchJson = Encoding.UTF8.GetString(Recover(dispatchRoot, dispatchArtifact));
            using var dispatchDocument = System.Text.Json.JsonDocument.Parse(dispatchJson);
            var dispatchRootElement = dispatchDocument.RootElement;
            Assert.Equal("dispatch-worker-source", dispatchRootElement.GetProperty("WorkerName").GetString());
            Assert.Equal("dispatch-command-source", dispatchRootElement.GetProperty("Command").GetString());
            Assert.Equal(dispatchRoot, dispatchRootElement.GetProperty("WorkingDirectory").GetString());
            Assert.Equal(
                DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
                dispatchRootElement.GetProperty("DispatchedAt").GetDateTimeOffset());

            var executionRoot = Path.Combine(root, "execution-scenario");
            var executionContext = Path.Combine(executionRoot, ".orchestrator-context", "goal");
            WriteEmptyRegistry(executionContext);
            var executionKernel = new AgentOrchestratorKernel();
            var executionTask = new TaskSpec(TaskId.New(), "Execution source", AgentRole.Developer);
            var executionGoal = executionKernel.CreateGoal("Execution semantic source", [executionTask]);
            executionKernel.ActivateGoal(executionGoal.Id, [developer]);
            var executionProvider = new FakeSmokeProvider(
                "last-model-output-source-✓",
                providerName: "OpenAI");
            await new AgentTaskRunner(
                executionKernel,
                [developer],
                new InMemoryModelProviderRegistry([executionProvider]))
                .RunAsync(executionGoal.Id, executionTask.Id);
            var executionObservations = new List<WorkerProfileDispatcher.SemanticSourceObservation>();
            var executionPackage = WorkerProfileDispatcher.BuildContextPackage(
                executionGoal,
                executionTask,
                executionRoot,
                executionContext,
                BriefFor(executionGoal, executionTask, "execution-instruction-source"),
                observedSources: executionObservations);
            var executionArtifact = Assert.Single(executionPackage.Artifacts.Where(candidate =>
                candidate.Identity.Value == "task/last-model-output.txt"));
            Assert.Equal(
                "last-model-output-source-✓",
                Encoding.UTF8.GetString(Recover(executionRoot, executionArtifact)));

            var reviewerRoot = Path.Combine(root, "reviewer-scope-scenario");
            var reviewerContext = Path.Combine(reviewerRoot, ".orchestrator-context", "goal");
            WriteEmptyRegistry(reviewerContext);
            var reviewerKernel = new AgentOrchestratorKernel();
            var reviewerTask = new TaskSpec(TaskId.New(), "Reviewer scope source", AgentRole.Reviewer);
            var reviewerGoal = reviewerKernel.CreateGoal("Reviewer scope semantic sources", [reviewerTask]);
            var reviewerPaths = Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles + 1)
                .Select(index => $"src/reviewer-{index:D3}.cs")
                .ToArray();
            var reviewerConflictPaths = Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles + 1)
                .Select(index => $"src/conflict-{index:D3}.cs")
                .ToArray();
            var reviewerObservations = new List<WorkerProfileDispatcher.SemanticSourceObservation>();
            var reviewerPackage = WorkerProfileDispatcher.BuildContextPackage(
                reviewerGoal,
                reviewerTask,
                reviewerRoot,
                reviewerContext,
                BriefFor(reviewerGoal, reviewerTask, "reviewer-scope-instruction-source"),
                observedSources: reviewerObservations,
                reviewerScopeChangedFiles: reviewerPaths,
                reviewerScopeMergeBase: new string('b', 40),
                reviewerScopeTotalChangedFileCount: reviewerPaths.Length,
                reviewerMergeTreeClean: false,
                reviewerMergeTreeConflictPaths: reviewerConflictPaths,
                reviewerMergeTreeTotalConflictPathCount: reviewerConflictPaths.Length);

            var scenarios = new[]
            {
                new ObservedPackage(root, package, observations),
                new ObservedPackage(dispatchRoot, dispatchPackage, dispatchObservations),
                new ObservedPackage(executionRoot, executionPackage, executionObservations),
                new ObservedPackage(reviewerRoot, reviewerPackage, reviewerObservations)
            };
            var observedSourceKinds = scenarios
                .SelectMany(scenario => scenario.Observations)
                .Select(observation => observation.Source)
                .Distinct()
                .OrderBy(source => source)
                .ToArray();
            Assert.Equal(
                Enum.GetValues<WorkerProfileDispatcher.WorkerContextSemanticSource>(),
                observedSourceKinds);
            Assert.All(scenarios, scenario => Assert.All(scenario.Observations, observation =>
            {
                var artifact = Assert.Single(scenario.Package.Artifacts.Where(candidate =>
                    candidate.Identity.Value == observation.LogicalIdentity));
                var recovered = Recover(scenario.WorkingDirectory, artifact);
                Assert.Equal(artifact.ContentHash, WorkerContextArtifact.Hash(recovered));
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void RetractedClarificationIsRemovedStructurallyWithoutMutatingUnrelatedArtifacts()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            Directory.CreateDirectory(contextDirectory);
            var kernel = new AgentOrchestratorKernel();
            var priorTask = new TaskSpec(TaskId.New(), "Research", AgentRole.Researcher);
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Preserve active clarification only", [priorTask, task]);
            const string activeText = "active-clarification-source-✓";
            const string retractedText = "retracted “clarification” \\ source-must-disappear-漢字";
            const string priorUnrelatedText = "prior-authoritative-content-must-survive-✓";
            const string registryUnrelatedText = "registered-authoritative-content-must-survive-漢字";
            var registryBytes = Encoding.UTF8.GetBytes(
                $"{registryUnrelatedText}\n{retractedText}\nregistered-tail\n");
            File.WriteAllBytes(Path.Combine(contextDirectory, "prior-task-evidence.md"), registryBytes);
            File.WriteAllText(
                Path.Combine(contextDirectory, "artifact-registry.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    artifacts = new[]
                    {
                        new
                        {
                            path = "prior-task-evidence.md",
                            exists = true,
                            sha256 = WorkerContextArtifact.Hash(registryBytes),
                            roleVisibility = new[] { "Developer" },
                            hashVerified = true
                        }
                    }
                }));
            var active = new HumanInputAnswerRecord(
                "active-answer",
                activeText,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                BriefVersion: 1);
            var retracted = new HumanInputAnswerRecord(
                "retracted-answer",
                retractedText,
                DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
                SupersededByAnswerId: active.Id,
                BriefVersion: 1);
            var baseSpec = new RefinedSpec(
                "contract",
                ["criterion"],
                VerificationClass.TestVerifiable,
                [],
                []);
            kernel.SetGoalRefinedSpec(goal.Id, baseSpec with
            {
                ClarificationAnswerHistory = [retracted, active]
            });
            var priorOutput = $"{priorUnrelatedText}\r\n{retractedText}\r\nprior-tail\r\n";
            kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
                "research",
                root,
                0,
                priorOutput,
                string.Empty,
                DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
                FullStandardOutput: priorOutput,
                FullStandardError: string.Empty));

            var withRetractedHistory = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                task,
                root,
                contextDirectory,
                BriefFor(goal, task, "preserve canonical clarifications"));
            var refinedSpecArtifact = Assert.Single(withRetractedHistory.Artifacts.Where(candidate =>
                candidate.Identity.Value == "goal/refined-spec.json"));
            var recovered = Recover(root, refinedSpecArtifact);
            using var refinedSpecJson = System.Text.Json.JsonDocument.Parse(recovered);
            var clarificationHistory = refinedSpecJson.RootElement.GetProperty("ClarificationAnswerHistory");

            var authoritativeAnswer = Assert.Single(clarificationHistory.EnumerateArray());
            Assert.Equal(activeText, authoritativeAnswer.GetProperty("Text").GetString());
            Assert.DoesNotContain(retractedText, Encoding.UTF8.GetString(recovered), StringComparison.Ordinal);
            Assert.DoesNotContain(
                System.Text.Json.JsonSerializer.Serialize(retractedText)[1..^1],
                Encoding.UTF8.GetString(recovered),
                StringComparison.Ordinal);
            var priorArtifact = Assert.Single(withRetractedHistory.Artifacts.Where(candidate =>
                candidate.Identity.Value == $"prior/{priorTask.Id.Value}/verification-output"));
            var registryArtifact = Assert.Single(withRetractedHistory.Artifacts.Where(candidate =>
                candidate.Identity.Value == "context/prior-task-evidence.md"));
            var filteredPrior = HumanInputRetractionPolicy.Apply(priorOutput, [], [retracted, active]);
            var filteredRegistry = HumanInputRetractionPolicy.Apply(
                Encoding.UTF8.GetString(registryBytes),
                [],
                [retracted, active]);
            Assert.Equal(Encoding.UTF8.GetBytes(filteredPrior), Recover(root, priorArtifact));
            Assert.Equal(Encoding.UTF8.GetBytes(filteredRegistry), Recover(root, registryArtifact));
            Assert.Contains(priorUnrelatedText, filteredPrior, StringComparison.Ordinal);
            Assert.Contains(registryUnrelatedText, filteredRegistry, StringComparison.Ordinal);
            Assert.DoesNotContain(retractedText, filteredPrior, StringComparison.Ordinal);
            Assert.DoesNotContain(retractedText, filteredRegistry, StringComparison.Ordinal);

            kernel.SetGoalRefinedSpec(goal.Id, baseSpec with
            {
                ClarificationAnswerHistory = [active]
            });
            var activeOnly = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                task,
                root,
                contextDirectory,
                BriefFor(goal, task, "preserve canonical clarifications"));
            var activeOnlyRefinedSpecArtifact = Assert.Single(activeOnly.Artifacts.Where(candidate =>
                candidate.Identity.Value == "goal/refined-spec.json"));

            Assert.Equal(activeOnlyRefinedSpecArtifact.ContentHash, refinedSpecArtifact.ContentHash);
            Assert.Equal(
                WorkerContextPackageBuilder.ComputeSemanticPackageId([activeOnlyRefinedSpecArtifact]),
                WorkerContextPackageBuilder.ComputeSemanticPackageId([refinedSpecArtifact]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, true, "registry-artifact-missing")]
    [Xunit.InlineData(true, false, "registry-artifact-hash-unverified")]
    public void RequiredRegistryArtifactInvalidStateFailsLoudly(
        bool exists,
        bool hashVerified,
        string expectedReason)
    {
        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            WorkerProfileDispatcher.RequireRegistryArtifactReady("research-notes.md", exists, hashVerified));

        Assert.Equal(expectedReason, error.Reason);
    }

    [Xunit.Fact]
    public void UnknownRegistryArtifactDefaultsToCompleteInlineDelivery()
    {
        var root = CreateTempDirectory();
        try
        {
            var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
            Directory.CreateDirectory(contextDirectory);
            var bytes = Encoding.UTF8.GetBytes("new artifact kind ✓\n");
            File.WriteAllBytes(Path.Combine(contextDirectory, "future-context.md"), bytes);
            File.WriteAllText(
                Path.Combine(contextDirectory, "artifact-registry.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    artifacts = new[]
                    {
                        new
                        {
                            path = "future-context.md",
                            exists = true,
                            sha256 = WorkerContextArtifact.Hash(bytes),
                            roleVisibility = new[] { "Developer" },
                            hashVerified = true
                        }
                    }
                }));
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Deliver future context", [task]);

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                task,
                root,
                contextDirectory,
                BriefFor(goal, task, "use all context"));

            var artifact = Assert.Single(package.Artifacts.Where(candidate =>
                candidate.Identity.Value == "context/future-context.md"));
            Assert.Equal(ContextDeliveryMode.InlineFull, artifact.DeliveryMode);
            Assert.Equal(bytes, artifact.AuthoritativeBytes);
            Assert.Null(artifact.MandatoryRelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FinalizedManifestUsesTheSameEffectivePackageAfterInlineFallback()
    {
        var bytes = Encoding.UTF8.GetBytes("complete fallback bytes");
        var source = Artifact(
            "context/research-notes.md",
            bytes,
            ContextDeliveryMode.MandatoryFile,
            "missing/research-notes.md");
        var builder = new WorkerContextPackageBuilder();
        var prepared = builder.Prepare(AgentRole.Developer, CreateTempDirectory(), [source]);
        var effective = Assert.Single(prepared.Artifacts);
        Assert.Equal(ContextDeliveryMode.InlineFull, effective.DeliveryMode);
        var inventory = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { identity = effective.Identity.Value, mode = effective.DeliveryMode.ToString() }
        });
        var manifest = WorkerContextArtifact.Create(
            new LogicalArtifactIdentity("context/manifest.v1.json"),
            ContextArtifactKind.ContextManifest,
            inventory,
            [AgentRole.Developer],
            ContextDeliveryMode.InlineFull,
            ContextContractVersion.V1);

        var finalPackage = builder.AppendFinalizedInlineArtifact(prepared, manifest);

        Assert.Contains(finalPackage.Artifacts, artifact =>
            artifact.Identity == source.Identity && artifact.DeliveryMode == ContextDeliveryMode.InlineFull);
        Assert.Contains("\"mode\":\"InlineFull\"", Encoding.UTF8.GetString(manifest.AuthoritativeBytes!), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void HeaderResidualRetainsAllInstructionsWhileRemovingTypedAndOperationalProjections()
    {
        var brief = string.Join("\n",
        [
            "# Agent Task Brief",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "bounded retry projection",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->",
            "Goal: full goal projection",
            "continued goal projection",
            "Goal id: goal-id",
            "Goal status: Active",
            "Decision context: do not attempt to reach dashboard APIs or orchestrator state.",
            "A newly introduced mandatory instruction must survive by default.",
            "Task: full task projection",
            "continued task projection",
            "Task role: Developer",
            "Task status: Assigned",
            "Task id: task-id",
            "Working directory, use absolute paths: C:\\machine-a\\repo",
            "Context files: read C:\\machine-a\\repo\\context\\digest.md first.",
            "Current target context:",
            "- Branch: goal/test",
            "- HEAD commit: abc123",
            "## Instructions",
            "worker instruction"
        ]);

        var residual = WorkerProfileDispatcher.ExtractCanonicalHeaderResidual(brief);

        Assert.Contains("Decision context: do not attempt", residual, StringComparison.Ordinal);
        Assert.Contains("newly introduced mandatory instruction", residual, StringComparison.Ordinal);
        Assert.Contains("Current target context:", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("bounded retry projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("goal projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("task projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("machine-a", residual, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void HeaderResidualRestoresReservedLiteralsBeforeCanonicalProjectionRemoval()
    {
        var literalBoundary = WorkerContextProjectionBoundary.Start(
            new LogicalArtifactIdentity("context/research-notes.md"));
        var brief = string.Join("\n",
        [
            "# Agent Task Brief",
            WorkerContextProjectionBoundary.EscapeReservedLiteral($"Goal: full goal projection {literalBoundary}"),
            "Goal id: goal-id",
            "Goal status: Active",
            WorkerContextProjectionBoundary.EscapeReservedLiteral($"Operator instruction preserves {literalBoundary}"),
            WorkerContextProjectionBoundary.EscapeReservedLiteral($"Task: full task projection {literalBoundary}"),
            "Task role: Developer",
            "Task status: Assigned",
            "Task id: task-id",
            "## Instructions",
            "worker instruction"
        ]);

        var residual = WorkerProfileDispatcher.ExtractCanonicalHeaderResidual(brief);

        Assert.DoesNotContain(WorkerContextProjectionBoundary.LiteralPrefix, residual, StringComparison.Ordinal);
        Assert.DoesNotContain("goal projection", residual, StringComparison.Ordinal);
        Assert.DoesNotContain("task projection", residual, StringComparison.Ordinal);
        Assert.Contains($"Operator instruction preserves {literalBoundary}", residual, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TypedBriefRetractsBoundaryShapedStaleAnswerBeforeEscaping()
    {
        var literalBoundary = WorkerContextProjectionBoundary.Start(
            new LogicalArtifactIdentity("context/collision.md"));
        var staleAnswer = $"stale-choice {literalBoundary}";
        var task = new TaskSpec(TaskId.New(), "Implement the corrected choice.", AgentRole.Developer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal($"Earlier choice: {staleAnswer}", [task]);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which choice should govern?");
        kernel.SubmitHumanInput(request.Id, staleAnswer);
        kernel.SupersedeHumanInput(
            goal.Id,
            request.Id,
            "corrected-choice",
            HumanInputAnswerOrigin.Operator);

        var brief = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            emitTypedSourceBoundaries: true).Content;

        Assert.Contains("corrected-choice", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-choice", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerContextProjectionBoundary.LiteralPrefix, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SerializeSemanticTimeline_CanonicalAuthoritativeBytesBindIdentityWithoutRootsOrTimestamps()
    {
        var rootA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "machine-a", "repo"));
        var rootB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "machine-b", "repo"));
        var goalId = GoalId.New();
        var occurredAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var recordedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z");
        var satisfiedAt = DateTimeOffset.Parse("2026-01-01T00:02:00Z");
        var first = new ProgressEvent(
            goalId,
            null,
            ProgressKind.TaskNote,
            $"Read {rootA}\\evidence.md",
            occurredAt,
            OperatorGates:
            [
                new OperatorGateRecord(
                    "deliverable",
                    "source",
                    recordedAt,
                    satisfiedAt,
                    $"Verified {rootA}\\receipt.md")
            ]);
        var second = first with
        {
            Message = $"Read {rootB}\\evidence.md",
            OccurredAt = occurredAt.AddDays(1),
            OperatorGates =
            [
                (first.OperatorGates!.Single() with
                {
                    RecordedAt = recordedAt.AddDays(1),
                    SatisfiedAt = satisfiedAt.AddDays(1),
                    SatisfactionEvidence = $"Verified {rootB}\\receipt.md"
                })
            ]
        };
        var meaningfullyChanged = second with { Message = "Read different evidence" };
        var laterEvent = second with { Kind = ProgressKind.TaskFailed, Message = "Causal successor" };

        var bytesA = WorkerProfileDispatcher.SerializeSemanticTimeline(
            [first], rootA, Path.Combine(rootA, ".orchestrator-context", goalId.Value));
        var bytesWithTimestampAndRootChanges = WorkerProfileDispatcher.SerializeSemanticTimeline(
            [second], rootB, Path.Combine(rootB, ".orchestrator-context", goalId.Value));
        var meaningfulBytes = WorkerProfileDispatcher.SerializeSemanticTimeline(
            [meaningfullyChanged], rootB, Path.Combine(rootB, ".orchestrator-context", goalId.Value));
        var bytesInCausalOrder = WorkerProfileDispatcher.SerializeSemanticTimeline(
            [second, laterEvent], rootB, Path.Combine(rootB, ".orchestrator-context", goalId.Value));
        var bytesInReverseOrder = WorkerProfileDispatcher.SerializeSemanticTimeline(
            [laterEvent, second], rootB, Path.Combine(rootB, ".orchestrator-context", goalId.Value));
        var builder = new WorkerContextPackageBuilder();
        var packageA = builder.Prepare(
            AgentRole.Developer,
            rootA,
            [Artifact(
                "timeline/causal-events.json",
                bytesA,
                ContextDeliveryMode.InlineFull)]);
        var packageWithTimestampAndRootChanges = builder.Prepare(
            AgentRole.Developer,
            rootB,
            [Artifact(
                "timeline/causal-events.json",
                bytesWithTimestampAndRootChanges,
                ContextDeliveryMode.InlineFull)]);
        var packageWithMeaningfulChange = builder.Prepare(
            AgentRole.Developer,
            rootB,
            [Artifact(
                "timeline/causal-events.json",
                meaningfulBytes,
                ContextDeliveryMode.InlineFull)]);
        var packageInCausalOrder = builder.Prepare(
            AgentRole.Developer,
            rootB,
            [Artifact(
                "timeline/causal-events.json",
                bytesInCausalOrder,
                ContextDeliveryMode.InlineFull)]);
        var packageInReverseOrder = builder.Prepare(
            AgentRole.Developer,
            rootB,
            [Artifact(
                "timeline/causal-events.json",
                bytesInReverseOrder,
                ContextDeliveryMode.InlineFull)]);

        Assert.Equal(bytesA, bytesWithTimestampAndRootChanges);
        Assert.Equal(WorkerContextArtifact.Hash(bytesA), Assert.Single(packageA.Artifacts).ContentHash);
        Assert.Equal(WorkerContextArtifact.Hash(bytesWithTimestampAndRootChanges), Assert.Single(packageWithTimestampAndRootChanges.Artifacts).ContentHash);
        Assert.Equal(packageA.SemanticPackageId, packageWithTimestampAndRootChanges.SemanticPackageId);
        Assert.NotEqual(packageA.SemanticPackageId, packageWithMeaningfulChange.SemanticPackageId);
        Assert.NotEqual(packageInCausalOrder.SemanticPackageId, packageInReverseOrder.SemanticPackageId);
        using var document = System.Text.Json.JsonDocument.Parse(bytesA);
        var serializedEvent = document.RootElement[0];
        var serializedGate = serializedEvent.GetProperty("OperatorGates")[0];
        Assert.Equal("Read ${workspace-root}/evidence.md", serializedEvent.GetProperty("Message").GetString());
        Assert.Equal("Verified ${workspace-root}/receipt.md", serializedGate.GetProperty("SatisfactionEvidence").GetString());
        Assert.False(serializedEvent.TryGetProperty("OccurredAt", out _));
        Assert.False(serializedGate.TryGetProperty("RecordedAt", out _));
        Assert.False(serializedGate.TryGetProperty("SatisfiedAt", out _));
        var json = Encoding.UTF8.GetString(bytesA);
        Assert.DoesNotContain(rootA.Replace("\\", "/", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void SerializeSemanticTimeline_OmitsOperationalGateTimestamps()
    {
        var root = Path.GetTempPath();
        var goalId = GoalId.New();
        var gate = new OperatorGateRecord(
            "deliverable",
            "source",
            DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
        var progressEvent = new ProgressEvent(
            goalId,
            null,
            ProgressKind.TaskNote,
            "awaiting operator gate",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            OperatorGates: [gate]);

        var bytes = WorkerProfileDispatcher.SerializeSemanticTimeline([progressEvent], root, root);

        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var serializedGate = document.RootElement[0].GetProperty("OperatorGates")[0];
        Assert.False(serializedGate.TryGetProperty("RecordedAt", out _));
        Assert.False(serializedGate.TryGetProperty("SatisfiedAt", out _));
    }

    [Xunit.Fact]
    public void FinalizedManifestBindsAuthoritativeTimelineHashIntoSemanticIdentity()
    {
        var builder = new WorkerContextPackageBuilder();
        var timelineA = Artifact(
            "goal/timeline.json",
            Encoding.UTF8.GetBytes("authoritative timeline at root A and time A"),
            ContextDeliveryMode.InlineFull);
        var timelineB = Artifact(
            "goal/timeline.json",
            Encoding.UTF8.GetBytes("authoritative timeline at root B and time B"),
            ContextDeliveryMode.InlineFull);
        var packageA = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
            builder,
            builder.Prepare(AgentRole.Developer, Path.GetTempPath(), [timelineA]));
        var packageB = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
            builder,
            builder.Prepare(AgentRole.Developer, Path.GetTempPath(), [timelineB]));
        var manifestA = Assert.Single(packageA.Artifacts.Where(artifact => artifact.Identity.Value == "context/manifest.v1.json"));
        var manifestB = Assert.Single(packageB.Artifacts.Where(artifact => artifact.Identity.Value == "context/manifest.v1.json"));

        Assert.NotEqual(timelineA.ContentHash, timelineB.ContentHash);
        Assert.NotEqual(manifestA.ContentHash, manifestB.ContentHash);
        Assert.NotEqual(packageA.SemanticPackageId, packageB.SemanticPackageId);
        Assert.Contains(timelineA.ContentHash, Encoding.UTF8.GetString(manifestA.AuthoritativeBytes!), StringComparison.Ordinal);
        Assert.Contains(timelineB.ContentHash, Encoding.UTF8.GetString(manifestB.AuthoritativeBytes!), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReceiptUsesTypedUnknownUsageAndPreservesSectionTruth()
    {
        var artifact = Artifact("criteria/1.md", Encoding.UTF8.GetBytes("criterion ✓"), ContextDeliveryMode.InlineFull);
        var package = new WorkerContextPackageBuilder().Prepare(AgentRole.Tester, Path.GetTempPath(), [artifact]);

        var receipt = WorkerContextPackageBuilder.CreateReceipt(package);

        var section = Assert.Single(receipt.Sections);
        Assert.Equal(artifact.ContentHash, section.ContentHash);
        Assert.Equal(ContextDeliveryMode.InlineFull, section.DeliveryMode);
        Assert.True(section.CharacterCount > 0);
        Assert.Equal(WorkerContextPackageBuilder.RenderArtifact(artifact).Length, section.CharacterCount);
        Assert.Equal(ProviderUsageState.Unknown, receipt.InputTokens.State);
        Assert.Null(receipt.InputTokens.Value);
        Assert.Equal("not-yet-reported", receipt.CachedInputTokens.UnknownReason);
    }

    [Xunit.Fact]
    public void RehydrateAndAppendInlineArtifactPreservesCompletePackageAndRebindsIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var builder = new WorkerContextPackageBuilder();
            var originalBytes = Encoding.UTF8.GetBytes("original context ✓\n");
            var original = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
                builder,
                builder.Prepare(
                    AgentRole.Developer,
                    root,
                    [Artifact("brief/original.md", originalBytes, ContextDeliveryMode.InlineFull)]));
            var rendered = WorkerContextPackageBuilder.Render(original);
            var receipt = WorkerContextPackageBuilder.CreateReceipt(original);
            var guidance = Encoding.UTF8.GetBytes("fresh steering guidance\n");

            var rehydrated = builder.RehydrateAndAppendInlineArtifact(
                AgentRole.Developer,
                root,
                rendered,
                receipt,
                new LogicalArtifactIdentity("steering/progressive-review-guidance.md"),
                guidance);

            Assert.NotEqual(original.SemanticPackageId, rehydrated.SemanticPackageId);
            Assert.Equal(originalBytes, Assert.Single(rehydrated.Artifacts.Where(artifact =>
                artifact.Identity.Value == "brief/original.md")).AuthoritativeBytes);
            Assert.Equal(guidance, Assert.Single(rehydrated.Artifacts.Where(artifact =>
                artifact.Identity.Value == "steering/progressive-review-guidance.md")).AuthoritativeBytes);
            Assert.Single(rehydrated.Artifacts.Where(artifact =>
                artifact.Identity.Value == "context/manifest.v1.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void AuthoritativeBytes_ReturnedArrayCannotMutateArtifact()
    {
        var original = Encoding.UTF8.GetBytes("immutable\n");
        var artifact = Artifact("instructions/immutable.md", original, ContextDeliveryMode.InlineFull);
        var exposed = artifact.AuthoritativeBytes!;

        exposed[0] = (byte)'X';

        Assert.Equal(original, artifact.AuthoritativeBytes);
        Assert.Equal(WorkerContextArtifact.Hash(original), artifact.ContentHash);
    }

    [Xunit.Fact]
    public void DispatchHostRejectsVisibilityMismatchBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("visible only to planner");
            File.WriteAllBytes(Path.Combine(root, "context.md"), bytes);
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "worker", root, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), null, false, false,
                MandatoryContextFiles:
                [
                    new MandatoryContextFileDescriptor(
                        "context/required.md", "context.md", WorkerContextArtifact.Hash(bytes), 1,
                        AgentRole.Developer, [AgentRole.Planner])
                ]);

            var exception = Record.Exception(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });
            var error = Assert.IsType<InvalidOperationException>(exception);

            Assert.Contains("not visible", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MandatoryReadAuthorityProbeIsInjectedIntoWorkerCommandBeforeProviderLaunch()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("authority-bound bytes");
            File.WriteAllBytes(Path.Combine(root, "context.md"), bytes);
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            startInfo.ArgumentList.Add("Write-Output 'provider-launch-marker'");
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md",
                "context.md",
                WorkerContextArtifact.Hash(bytes),
                1,
                AgentRole.Developer,
                [AgentRole.Developer]));

            DispatchProcessHost.PrependMandatoryContextAuthorityPreflight(startInfo, parameters);

            var command = Assert.Single(startInfo.ArgumentList);
            var probe = command.IndexOf("$mcgContextManifestPath", StringComparison.Ordinal);
            var provider = command.IndexOf("provider-launch-marker", StringComparison.Ordinal);
            Assert.True(probe >= 0, command);
            Assert.True(provider > probe, command);
            Assert.Contains("exit 86", command, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void LegacyV1PointerResolvesCompleteHashBoundEvidenceAndRejectsCorruption()
    {
        var bytes = Encoding.UTF8.GetBytes("complete prior evidence\n");
        var identity = new LogicalArtifactIdentity("prior/task-1/evidence.md");
        var representation = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes);
        var resolver = new LegacyHandoffCompatibilityResolver(key => key == identity.Value ? bytes : null);

        Assert.Equal(bytes, resolver.Resolve(representation));

        var corrupt = new LegacyHandoffCompatibilityResolver(_ => Encoding.UTF8.GetBytes("corrupt"));
        Assert.Throws<WorkerContextPreparationException>(() => corrupt.Resolve(representation));
    }

    [Xunit.Fact]
    public void DispatchHostRejectsHashMismatchedMandatoryContextBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "context.md"), "actual");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "worker",
                root,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                false,
                false,
                MandatoryContextFiles:
                [
                    new MandatoryContextFileDescriptor(
                        "context/required.md",
                        "context.md",
                        WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("expected")),
                        1,
                        AgentRole.Developer,
                        [AgentRole.Developer])
                ]);

            var exception = Record.Exception(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });
            var error = Assert.IsType<InvalidOperationException>(exception);

            Assert.Contains("hash mismatch", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DispatchHostRejectsUnsupportedVersionBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("versioned");
            File.WriteAllBytes(Path.Combine(root, "context.md"), bytes);
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md", "context.md", WorkerContextArtifact.Hash(bytes), 2,
                AgentRole.Developer, [AgentRole.Developer]));

            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });

            Assert.Contains("unsupported contract version", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DispatchHostRejectsInvalidPathBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        try
        {
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md", "../outside.md", new string('0', 64), 1,
                AgentRole.Developer, [AgentRole.Developer]));

            Assert.Throws<ArgumentException>(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DispatchHostRejectsReparseTraversalBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var junctionPath = Path.Combine(root, "linked");
        try
        {
            CreateDirectoryJunction(junctionPath, outside);
            var bytes = Encoding.UTF8.GetBytes("outside bytes");
            File.WriteAllBytes(Path.Combine(outside, "context.md"), bytes);
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md", "linked/context.md", WorkerContextArtifact.Hash(bytes), 1,
                AgentRole.Developer, [AgentRole.Developer]));

            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DispatchHostRejectsUnreadableMandatoryFileBeforeWorkerStart()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "context.md");
            var bytes = Encoding.UTF8.GetBytes("locked bytes");
            File.WriteAllBytes(path, bytes);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md", "context.md", WorkerContextArtifact.Hash(bytes), 1,
                AgentRole.Developer, [AgentRole.Developer]));

            Assert.Throws<IOException>(() =>
            {
                using var lease = DispatchProcessHost.AcquireMandatoryContextFileLeases(parameters);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DispatchHostIntegratedRunDoesNotSpawnWorkerForInvalidMandatoryContext()
    {
        var root = CreateTempDirectory();
        try
        {
            var marker = Path.Combine(root, "worker-started.txt");
            var parameters = HostParameters(root, new MandatoryContextFileDescriptor(
                "context/required.md", "missing.md", new string('0', 64), 1,
                AgentRole.Developer, [AgentRole.Developer])) with
            {
                Command = $"Set-Content -LiteralPath '{marker.Replace("'", "''", StringComparison.Ordinal)}' started"
            };
            var parametersPath = Path.Combine(root, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var exitCode = DispatchProcessHost.Run(parametersPath);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DispatchProcessHost.DispatchRunParameters HostParameters(
        string root,
        MandatoryContextFileDescriptor descriptor) => new(
            "Write-Output worker",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            null,
            false,
            false,
            MandatoryContextFiles: [descriptor]);

    private static WorkerContextArtifact Artifact(
        string identity,
        byte[] bytes,
        ContextDeliveryMode mode,
        string? path = null) => WorkerContextArtifact.Create(
            new LogicalArtifactIdentity(identity),
            mode == ContextDeliveryMode.MandatoryFile ? ContextArtifactKind.RegisteredContext : ContextArtifactKind.OperatorInstructions,
            bytes,
            AllRoles,
            mode,
            ContextContractVersion.V1,
            path);

    private static byte[] Recover(string workingDirectory, WorkerContextArtifact artifact) =>
        artifact.DeliveryMode == ContextDeliveryMode.InlineFull
            ? artifact.AuthoritativeBytes!
            : File.ReadAllBytes(Path.Combine(
                workingDirectory,
                artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", junctionPath, targetPath }
        }) ?? throw new InvalidOperationException("Could not start the directory-junction fixture process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        var output = outputTask.Result;
        var error = errorTask.Result;
        Assert.True(
            process.ExitCode == 0,
            $"Directory-junction fixture failed with exit {process.ExitCode}. stdout: {output} stderr: {error}");
        Assert.True((File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0);
    }

    private static TaskBrief BriefFor(
        Goal goal,
        TaskSpec task,
        string instruction,
        string? headerResidual = null) => new(
        goal.Id,
        task.Id,
        task.RequiredRole,
        task.Description,
        $"""
        # Agent Task Brief
        Goal: {goal.Objective}
        Goal id: {goal.Id.Value}
        Task: {task.Description}
        Task role: {task.RequiredRole}
        {headerResidual}
        ## Instructions
        {instruction}
        """);

    private sealed record ExpectedSemanticSource(string Name, string LogicalIdentity, string Marker);
    private sealed record ObservedPackage(
        string WorkingDirectory,
        WorkerContextPackage Package,
        IReadOnlyList<WorkerProfileDispatcher.SemanticSourceObservation> Observations);

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private static string WriteCoverageTrx(string identity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.trx");
        var escaped = System.Security.SecurityElement.Escape(identity);
        File.WriteAllText(
            path,
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{escaped}\"><TestMethod className=\"WorkerContextPackageTests\" name=\"InlineFullRecoversExactUtf8Bytes\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{escaped}\" outcome=\"Passed\" /></Results></TestRun>");
        return path;
    }

    private static void AssertRecoveredContainsSemanticValue(byte[] recovered, string expected)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(recovered);
            Assert.Contains(expected, EnumerateJsonStringValues(document.RootElement));
        }
        catch (System.Text.Json.JsonException)
        {
            Assert.Contains(expected, Encoding.UTF8.GetString(recovered), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> EnumerateJsonStringValues(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            yield return element.GetString()!;
            yield break;
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var value in EnumerateJsonStringValues(item))
                {
                    yield return value;
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var value in EnumerateJsonStringValues(property.Value))
                {
                    yield return value;
                }
            }
        }
    }

    private static void WriteEmptyRegistry(string contextDirectory)
    {
        Directory.CreateDirectory(contextDirectory);
        File.WriteAllText(
            Path.Combine(contextDirectory, "artifact-registry.json"),
            System.Text.Json.JsonSerializer.Serialize(new { artifacts = Array.Empty<object>() }));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-context-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
