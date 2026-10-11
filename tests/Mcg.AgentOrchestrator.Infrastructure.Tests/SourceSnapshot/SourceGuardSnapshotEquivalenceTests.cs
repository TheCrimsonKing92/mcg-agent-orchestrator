using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: both scan arms read the same unique fixture tree.
public sealed class SourceGuardSnapshotEquivalenceTests
{
    [Theory]
    [InlineData("PowerShell")]
    [InlineData("RedirectedStream")]
    [InlineData("CallerPath")]
    [InlineData("DirectGit")]
    [InlineData("ParallelState")]
    [InlineData("ProcessStartInfo")]
    [InlineData("TempRoot")]
    [InlineData("Dashboard")]
    [InlineData("LegacyName")]
    [InlineData("OwnedGroup")]
    [InlineData("BuildShutdown")]
    [InlineData("WorkspaceBuildTokens")]
    [InlineData("WorkspaceConsumerTokens")]
    [InlineData("WorkspaceGoalTokens")]
    public void MigratedScanFindingsMatchDirectDiskIncludingViolationsAndExclusions(string guard)
    {
        using var fixture = ViolatingFixture();
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root);
        var directPaths = DirectFiles(guard, fixture).ToArray();
        var cachedPaths = SnapshotFiles(guard, fixture).ToArray();
        Assert.Equal(directPaths.Order(StringComparer.Ordinal), cachedPaths.Order(StringComparer.Ordinal));
        var direct = Findings(guard, fixture, directPaths, File.ReadAllText,
            path => File.ReadAllLines(path), path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));
        var cached = Findings(guard, fixture, cachedPaths, snapshot.GetText, snapshot.GetLines, snapshot.GetSyntaxTree);
        Assert.NotEmpty(direct);
        Assert.Equal(direct.Order(StringComparer.Ordinal), cached.Order(StringComparer.Ordinal));
        if (guard == "ProcessStartInfo")
            Assert.Contains(cached, finding => finding.Contains("obj", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclarationIndexAndBoundaryFindingsMatchDirectDisk()
    {
        using var fixture = ViolatingFixture();
        var snapshot = ExecutionWorkspacesReferenceGuardTests.BuildDeclarationIndex(fixture.Root);
        var direct = DirectDeclarationIndex(fixture.Root);
        Assert.Equal(direct.Keys.Order(StringComparer.Ordinal), snapshot.Keys.Order(StringComparer.Ordinal));
        foreach (var name in direct.Keys) Assert.Equal(direct[name], snapshot[name]);
        var pairs = new[] { ("Fixture.Execution", "Fixture.Outside"), ("Fixture.Execution.Inner", "Fixture.Outside") };
        var expected = ExecutionWorkspacesReferenceGuardTests.EvaluateBoundary(direct, pairs);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ExecutionWorkspacesReferenceGuardTests.EvaluateBoundary(snapshot, pairs));
    }

    private static SourceSnapshotFixture ViolatingFixture()
    {
        var fixture = new SourceSnapshotFixture();
        var launch = "class C { void M() { var p = new " + "ProcessStartInfo(\"pwsh\"); " +
            "var g = new Process" + "StartInfo(\"git\"); var a = p.StandardOutput.ReadToEnd(); " +
            "var b = p.StandardError.ReadToEnd(); WorkerProcessJobs." + "ClearRegistryForTests(); } " +
            "string Root([System.Runtime.CompilerServices.CallerFilePath] string path = \"\") => path; }";
        var production = "namespace Fixture; class Execution { class Inner {} void M() { " +
            "var temp = Path.GetTempPath(); group.Kill(); info.ArgumentList.Add(\"build-server\"); " +
            "GoalWorktrees.Open(); GoalAcceptanceVerifier.Run(); DotnetBuildEnvironmentManager.Run(); } " +
            "string value = \"hosted-dashboard Spark DotnetBuildEnvironment LocalProcessVerifier\"; }";
        foreach (var segment in new[] { "", "bin/", "obj/", "artifacts/", ".scratch/", ".orchestrator-prototype/",
                     ".orchestrator-worktrees/", "TestResults/", "playwright-report/", "Case/Bin/" })
        {
            fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + segment + "Violation.cs", launch);
            fixture.Write("tests/Mcg.AgentOrchestrator.Core.Tests/" + segment + "Violation.cs", launch);
            fixture.Write("src/Mcg.AgentOrchestrator.Execution/Processes/" + segment + "Violation.cs", production);
            fixture.Write("src/Mcg.AgentOrchestrator.Execution/Workers/" + segment + "Violation.cs", production.Replace("Execution", "Worker", StringComparison.Ordinal));
            fixture.Write("src/Mcg.AgentOrchestrator.Infrastructure/" + segment + "Outside.cs", "namespace Fixture; class Outside {} delegate void Handler();");
        }
        foreach (var name in new[] { "PowerShellLaunchSourceGuardTests", "ParallelSharedStateSourceGuardTests",
                     "ProcessStartInfoSourceGuardTests", "InfrastructureTestSupport", "VerifiedRepositoryRoot" })
            fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + name + ".cs", launch);
        fixture.Write("tests/Mcg.AgentOrchestrator.Core.Tests/VerifiedRepositoryRoot.cs", launch);
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliVerifiedRepositoryRoot.cs", launch);
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/obj/OnlyWindowViolation.cs",
            "class P { void M() { var p = new " + "ProcessStartInfo(\"tool\"); } }");
        fixture.Write("src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs", production);
        fixture.Write("src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs",
            "namespace Fixture; class Jobs {\ninternal static bool ReadAccountingAndDispose() {\ngroup.Kill();\nreturn true; }\n" +
            "internal static bool HasRegisteredJob() => true;\nvoid Unapproved() { group.Kill(); }\n}");
        return fixture;
    }

    private static IEnumerable<string> SnapshotFiles(string guard, SourceSnapshotFixture fixture) => guard switch
    {
        "PowerShell" => PowerShellLaunchSourceGuardTests.SourceFiles(fixture.TestProject),
        "RedirectedStream" => RedirectedStreamDrainSourceGuardTests.SourceFiles(fixture.TestProject),
        "CallerPath" => CallerFilePathRootSourceGuardTests.SourceFiles(fixture.Root, fixture.TestProject)
            .Concat(CallerFilePathRootSourceGuardTests.SourceFiles(fixture.Root, fixture.CoreProject)),
        "DirectGit" => DirectGitLaunchSourceGuardTests.SourceFiles(fixture.TestProject),
        "ParallelState" => ParallelSharedStateSourceGuardTests.ReadSources(Path.Combine(fixture.Root, "tests"))
            .Select(source => Path.Combine(fixture.Root, "tests", source.Path)),
        "ProcessStartInfo" => ProcessStartInfoSourceGuardTests.SourceFiles(fixture.TestProject),
        "TempRoot" => OrchestratorTempRootSourceGuardTests.SourceFiles(fixture.Root),
        "Dashboard" => DashboardRemnantSourceGuardTests.SourceFiles(fixture.Root),
        "LegacyName" => LegacySparkNameSourceGuardTests.SourceFiles(fixture.Root),
        "OwnedGroup" => ExecutionOwnedGroupCloseSourceGuardTests.SourceFiles(fixture.Root),
        "BuildShutdown" => BuildServerShutdownSourceGuardTests.SourceFiles(fixture.Root),
        "WorkspaceBuildTokens" or "WorkspaceConsumerTokens" or "WorkspaceGoalTokens" => new[] { "Processes", "Workers" }
            .SelectMany(area => ExecutionWorkspacesReferenceGuardTests.AreaFiles(fixture.Root,
                Path.Combine(fixture.Root, "src", "Mcg.AgentOrchestrator.Execution", area))),
        _ => throw new ArgumentException(guard)
    };

    // Independent oracle: these are the pre-migration bases, path forms and exclusion sets.
    private static IEnumerable<string> DirectFiles(string guard, SourceSnapshotFixture fixture)
    {
        var root = fixture.Root;
        var execution = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Execution");
        var directories = guard switch
        {
            "CallerPath" => new[] { fixture.TestProject, fixture.CoreProject },
            "ParallelState" => new[] { Path.Combine(root, "tests") },
            "TempRoot" or "Dashboard" or "LegacyName" or "BuildShutdown" => new[] { Path.Combine(root, "src") },
            "OwnedGroup" => new[] { execution },
            "WorkspaceBuildTokens" or "WorkspaceConsumerTokens" or "WorkspaceGoalTokens" =>
                new[] { Path.Combine(execution, "Processes"), Path.Combine(execution, "Workers") },
            _ => new[] { fixture.TestProject }
        };
        var excluded = guard switch
        {
            "ProcessStartInfo" => Array.Empty<string>(),
            "ParallelState" or "TempRoot" => new[] { "bin", "obj" },
            "OwnedGroup" => new[] { "bin", "obj", "artifacts" },
            "CallerPath" => new[] { "bin", "obj", "artifacts", ".scratch", "TestResults" },
            "Dashboard" or "LegacyName" or "WorkspaceBuildTokens" or "WorkspaceConsumerTokens" or "WorkspaceGoalTokens" =>
                new[] { "bin", "obj", ".scratch", ".orchestrator-prototype", ".orchestrator-worktrees", "TestResults", "playwright-report" },
            _ => new[] { "bin", "obj", ".scratch", ".orchestrator-prototype" }
        };
        foreach (var path in directories.SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)))
        {
            var filterPath = guard switch
            {
                "PowerShell" or "RedirectedStream" => Path.GetRelativePath(fixture.TestProject, path),
                "CallerPath" or "Dashboard" or "LegacyName" or "WorkspaceBuildTokens" or "WorkspaceConsumerTokens" or "WorkspaceGoalTokens" =>
                    Path.GetRelativePath(root, path),
                "OwnedGroup" => Path.GetRelativePath(execution, path),
                _ => path
            };
            if (filterPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(excluded.Contains)) continue;
            var name = Path.GetFileName(path);
            if (guard == "PowerShell" && Path.GetRelativePath(fixture.TestProject, path).Replace('\\', '/') == "PowerShellLaunchSourceGuardTests.cs" ||
                guard == "DirectGit" && name == "InfrastructureTestSupport.cs" ||
                guard == "ParallelState" && name == "ParallelSharedStateSourceGuardTests.cs" ||
                guard == "ProcessStartInfo" && name == "ProcessStartInfoSourceGuardTests.cs") continue;
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (guard == "CallerPath" && new[]
                {
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/VerifiedRepositoryRoot.cs",
                    "tests/Mcg.AgentOrchestrator.Core.Tests/VerifiedRepositoryRoot.cs",
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliVerifiedRepositoryRoot.cs"
                }.Contains(relative)) continue;
            yield return path;
        }
    }

    private static string[] Findings(string guard, SourceSnapshotFixture fixture, IEnumerable<string> paths,
        Func<string, string> text, Func<string, IReadOnlyList<string>> lines, Func<string, SyntaxTree> tree)
    {
        var root = fixture.Root;
        if (guard == "ParallelState")
            return ParallelSharedStateSourceGuardTests.FindOffenders(paths.Select(path =>
                (Path.GetRelativePath(Path.Combine(root, "tests"), path), text(path)))).ToArray();
        var findings = new List<string>();
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            switch (guard)
            {
                case "PowerShell":
                    findings.AddRange(PowerShellLaunchSourceGuardTests.FindLaunches(tree(path)).Select(f => $"{relative}:{f.Line}:{f.Selector}"));
                    break;
                case "RedirectedStream":
                    findings.AddRange(RedirectedStreamDrainSourceGuardTests.FindOffenders(tree(path)).Select(f => $"{relative}::{f.Method}: {f.Reason}"));
                    break;
                case "CallerPath":
                    findings.AddRange(CallerFilePathRootSourceGuardTests.CallerPathMethods(tree(path))
                        .Where(method => !method.ToFullString().Contains("VerifiedRepositoryRoot.", StringComparison.Ordinal))
                        .Select(method => $"{relative}: {method.Identifier.ValueText} bypasses VerifiedRepositoryRoot"));
                    break;
                case "DirectGit": findings.AddRange(DirectGitLaunchSourceGuardTests.FindDirectLaunches(path, text(path))); break;
                case "ProcessStartInfo":
                    findings.AddRange(ProcessStartInfoSourceGuardTests.FindOffenders(Path.GetRelativePath(fixture.TestProject, path), lines(path))); break;
                case "BuildShutdown":
                    if (BuildServerShutdownSourceGuardTests.IsShutdown(text(path))) findings.Add(relative);
                    break;
                case "TempRoot":
                    foreach (var (line, index) in lines(path).Select((line, index) => (line.Trim(), index)))
                    {
                        var srcPath = Path.GetRelativePath(Path.Combine(root, "src"), path).Replace('\\', '/');
                        if (line.Contains("Path.GetTempFileName(", StringComparison.Ordinal) || line.Contains("Directory.CreateTempSubdirectory(", StringComparison.Ordinal))
                            findings.Add($"{srcPath}:{index + 1}: implicit temp root construction");
                        if (line.Contains("Path.GetTempPath()", StringComparison.Ordinal) &&
                            srcPath != "Mcg.AgentOrchestrator.Execution/Processes/OrchestratorTempRoot.cs" &&
                            !OrchestratorTempRootSourceGuardTests.IsAllowed(srcPath, line)) findings.Add($"{srcPath}:{index + 1}: {line}");
                    }
                    break;
                case "OwnedGroup":
                    if (path.EndsWith("OwnedProcessGroup.cs", StringComparison.OrdinalIgnoreCase)) break;
                    string[] closeTokens = ["workerGroup?.Dispose()", "workerGroup?.Kill()", "processGroup?.Dispose()", "processGroup?.Kill()", "group.Dispose()", "group.Kill()"];
                    var sourceLines = lines(path).ToArray();
                    var isJobs = path.EndsWith(Path.Combine("Processes", "WorkerProcessJobs.cs"), StringComparison.OrdinalIgnoreCase);
                    var start = Array.FindIndex(sourceLines, line => line.Contains("internal static bool ReadAccountingAndDispose(", StringComparison.Ordinal));
                    var end = Array.FindIndex(sourceLines, line => line.Contains("internal static bool HasRegisteredJob", StringComparison.Ordinal));
                    if (isJobs) Assert.True(start >= 0 && end > start, "Missing accounting helper boundaries");
                    foreach (var (line, index) in sourceLines.Select((line, index) => (line, index)))
                    {
                        if (isJobs && index >= start && index < end) continue;
                        if (closeTokens.Any(token => line.Contains(token, StringComparison.Ordinal)) && !line.Contains("ReadAccountingAndDispose", StringComparison.Ordinal))
                            findings.Add($"{Path.GetRelativePath(root, path)}:{index + 1}:{line.Trim()}");
                    }
                    break;
                case "Dashboard":
                    string[] forbidden = ["src/Mcg.AgentOrchestrator.App/Dashboard/", "src/Mcg.AgentOrchestrator.Dashboard/", "excluded-dashboard", ".dashboard-url", "/api/source-survey", "/api/system/", "/api/goals/", "/api/task/", "/api/monitor", "hosted-dashboard", "Invoke-DashboardBuildTestCycle", "Run-DashboardBrowserScript", "dashboard source survey", "dashboard APIs"];
                    findings.AddRange(forbidden.Where(token => text(path).Contains(token, StringComparison.OrdinalIgnoreCase))
                        .Select(token => $"Removed literal '{token}' in {Path.GetRelativePath(root, path)}"));
                    break;
                case "LegacyName":
                    if (relative == "src/Mcg.AgentOrchestrator.Core/Domain/LunaLaneNames.cs") break;
                    findings.AddRange(new[] { "spark", "Spark", "SPARK" }.Where(token => text(path).Contains(token, StringComparison.Ordinal))
                        .Select(token => $"Retired spelling '{token}' in {relative}"));
                    break;
                case "WorkspaceBuildTokens":
                    if (text(path).Contains("DotnetBuildEnvironmentManager.", StringComparison.Ordinal) || text(path).Contains("GoalAcceptanceVerifier.", StringComparison.Ordinal)) findings.Add(relative);
                    break;
                case "WorkspaceConsumerTokens":
                    findings.AddRange(new[] { "DotnetBuildEnvironment", "GoalAcceptanceVerifier.", "LocalProcessVerifier" }
                        .Where(token => text(path).Contains(token, StringComparison.Ordinal)).Select(token => $"{relative}: {token}"));
                    break;
                case "WorkspaceGoalTokens":
                    if (text(path).Contains("GoalWorktrees.", StringComparison.Ordinal)) findings.Add(Path.GetRelativePath(root, path));
                    break;
                default: throw new ArgumentException(guard);
            }
        }
        return findings.ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> DirectDeclarationIndex(string root)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in new[] { "Mcg.AgentOrchestrator.Infrastructure", "Mcg.AgentOrchestrator.Execution" }
                     .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path.Split('/').Any(segment => segment is "bin" or "obj" or "artifacts")) continue;
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var declaration in syntax.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                var parts = declaration.AncestorsAndSelf().Reverse().Select(node => node switch
                {
                    BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                    DelegateDeclarationSyntax type => type.Identifier.ValueText,
                    _ => null
                }).Where(part => part is not null);
                var name = string.Join(".", parts);
                if (!index.TryGetValue(name, out var files)) index[name] = files = new HashSet<string>(StringComparer.Ordinal);
                files.Add(path);
            }
        }
        return index.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}
