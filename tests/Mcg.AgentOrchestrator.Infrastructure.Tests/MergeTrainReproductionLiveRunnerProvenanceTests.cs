using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: partitions, build storage, leases and process receipts are fixture-owned.
public sealed class MergeTrainReproductionLiveRunnerProvenanceTests : AcceptanceCohortWorkflowTests
{
    private const string Project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
    private const string TestName = "Ns.ReproductionProbeTests.Passes";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveRunnerRecordsEachPartitionsBuildAndRejectsForeignTrxAssembly(bool foreignAssembly)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            var projectDirectory = Path.Combine(repo, "tests", Project);
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, Project + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "ReproductionProbeTests.cs"),
                "namespace Ns; public sealed class ReproductionProbeTests { [Xunit.Fact] public void Passes() { } }");
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config/acceptance-manifest.json"),
                AcceptanceManifestTestDefaults.WithEngine("""
                { "version": 1, "checks": [], "forbiddenChangedPathGlobs": [] }
                """));
            RunGit(repo, "add", "tests", "config");
            RunGit(repo, "commit", "-m", "Add reproduction probe");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var members = Enumerable.Range(1, 3).Select(index =>
            {
                var path = $"member-{index}.txt";
                // Goal branches use the first eight characters, so vary that prefix.
                var goalValue = index.ToString("x8").PadRight(32, '0');
                var candidate = CreateCandidate(repo, goalValue, path, "member " + index);
                return TrainBind(candidate.GoalId, candidate.Revision, path, "resource-" + index);
            }).ToArray();
            var process = new RecordingProcess(foreignAssembly, Path.Combine(repo, "foreign-output"));
            // Keep the real focused verifier: it must propagate the live runner's binding
            // into BOTH the build command and the MTP assembly invocation.
            var verifier = new GoalAcceptanceVerifier(new GoalAcceptanceVerifierTestOverrides
            {
                BuildStorageRootForTests = new DotnetBuildStorageRoot(Path.Combine(repo, ".build-storage"))
            }, process.Run);
            var cleanup = CreateIsolatedCleanupContext(repo);
            var leases = new List<DotnetBuildEnvironmentLease>();
            var runner = MergeTrainReproductionAttribution.CreateLiveRunner(
                OrchestratorWorkspace.ForDirectory(repo), verifier, cleanup.Hooks, (_, _) =>
                {
                    var root = Path.Combine(repo, ".reproduction-lease");
                    Directory.CreateDirectory(root);
                    var lockPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".lock");
                    var environment = new DotnetBuildEnvironment("probe", root, Path.Combine(root, "artifacts"),
                        lockPath, ["--artifacts-path", Path.Combine(root, "artifacts")], "probe", BuildPermitIndex: 0);
                    var lease = new DotnetBuildEnvironmentLease(environment,
                        new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
                    leases.Add(lease);
                    return lease;
                });

            var roots = new List<string>();
            foreach (var member in members)
            {
                var before = process.Builds.Count;
                var result = Assert.IsType<MergeTrainReproductionAttribution.RunResult>(
                    runner(member, main, [TestName], CancellationToken.None));
                var evidence = Assert.IsType<MergeTrainReproductionProvenance>(result.Provenance);
                Assert.Equal(before + 1, process.Builds.Count);
                var build = process.Builds[^1];
                Assert.Equal(main, evidence.MainRevision);
                Assert.Equal(member.CandidateRevision, evidence.MemberRevision);
                Assert.Equal(RunGitOutput(repo, "rev-parse", member.CandidateRevision + "^{tree}").Trim(), evidence.ExpectedTreeRevision);
                Assert.Equal(build.Tree, evidence.TreeRevision);
                Assert.Equal(evidence.ExpectedTreeRevision, evidence.TreeRevision);
                Assert.Equal(build.OutputRoot, evidence.PartitionBuildOutputRoot);
                Assert.Equal(new[] { build.OutputRoot }, evidence.BuildOutputRoots);
                Assert.Equal(new[] { process.Assemblies[^1] }, evidence.AssemblyPaths);
                if (!foreignAssembly) Assert.Equal(build.AssemblyPath, Assert.Single(evidence.AssemblyPaths));
                Assert.Equal(new[] { TestName }, result.ExecutedTests);
                Assert.Empty(result.Failures);
                Assert.Equal(foreignAssembly ? "assembly-outside-root" : null,
                    MergeTrainReproductionEvidenceCheck.InvalidReason(evidence, member, main));
                // Validation survives disposal of the fresh output directory.
                Assert.Equal(foreignAssembly ? "assembly-outside-root" : null, evidence.ValidationFailure);
                Assert.False(Directory.Exists(build.OutputRoot));
                roots.Add(build.OutputRoot);
            }
            Assert.Equal(3, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(3, process.Assemblies.Count);
            Assert.Equal(3, leases.Count);
            Assert.All(leases, lease => Assert.False(lease.IsExecutionLockHeld));
        }
        finally { DeleteDirectory(repo); }
    }

    private sealed class RecordingProcess(bool foreignAssembly, string foreignRoot)
    {
        internal List<(string OutputRoot, string Tree, string AssemblyPath)> Builds { get; } = [];
        internal List<string> Assemblies { get; } = [];

        internal Task<GoalAcceptanceVerifier.CommandResult> Run(string[] args, string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                var root = Argument(args, "--artifacts-path");
                var environment = new DotnetBuildEnvironment("process-probe", root, root, "unused", [], "probe");
                var invocation = AcceptanceGateEngineSettings.Load(workingDirectory)
                    .ResolveMtpInvocation($"tests/{Project}/{Project}.csproj");
                foreach (var path in invocation.ResolveRequiredBuildArtifacts(environment))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "modeled build output");
                }
                Builds.Add((root, RunGitOutput(workingDirectory, "rev-parse", "HEAD^{tree}").Trim(),
                    invocation.ResolveManagedAssemblyPath(environment)));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            }
            Assert.True(args.Length > 1 && args[0] == "dotnet" && args[1].EndsWith(Project + ".dll", StringComparison.Ordinal),
                "Only the selected managed test assembly may execute: " + string.Join(" ", args));
            var build = Builds[^1];
            Assert.StartsWith(build.OutputRoot + Path.DirectorySeparatorChar, args[1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(build.AssemblyPath, args[1]);
            Assert.True(File.Exists(args[1]), "The modeled build must have produced the assembly before test execution.");
            var assembly = foreignAssembly ? Path.Combine(foreignRoot, Project + ".dll") : args[1];
            if (foreignAssembly)
            {
                Directory.CreateDirectory(foreignRoot);
                File.WriteAllText(assembly, "unrelated build output");
            }
            Assemblies.Add(assembly);
            var trx = Path.Combine(Argument(args, "--results-directory"), Argument(args, "--report-trx-filename"));
            Directory.CreateDirectory(Path.GetDirectoryName(trx)!);
            new XDocument(new XElement("TestRun",
                new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "probe"),
                    new XAttribute("name", TestName), new XElement("TestMethod", new XAttribute("className", "Ns.ReproductionProbeTests"),
                        new XAttribute("name", "Passes"), new XAttribute("codeBase", new Uri(assembly).AbsoluteUri)))),
                new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "probe"),
                    new XAttribute("testName", TestName), new XAttribute("outcome", "Passed"))),
                new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                    new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1),
                    new XAttribute("failed", 0), new XAttribute("notExecuted", 0))))).Save(trx);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        }

        private static string Argument(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            Assert.True(index >= 0 && index + 1 < args.Length, "Missing command argument: " + name);
            return args[index + 1];
        }
    }
}
