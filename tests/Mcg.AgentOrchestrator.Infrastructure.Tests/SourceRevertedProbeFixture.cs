using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git/worktree boundary; build and test execution are injected by each probe.
internal sealed class SourceRevertedProbeFixture : IDisposable
{
    internal const string Feature = "src/Feature.cs";
    internal const string Other = "src/Other.cs";
    internal const string Added = "src/Added.cs";
    internal const string UnlistedAdded = "src/UnlistedAdded.cs";
    internal const string Deleted = "src/Deleted.cs";
    internal const string Unchanged = "src/Unchanged.cs";
    internal const string Test = "tests/Mcg.AgentOrchestrator.Core.Tests/AddedProbeTests.cs";
    internal const string Identity = "AddedProbeTests.AddedBehavior";
    internal const string BaselineFeature = "public static class Feature { public static bool Enabled => false; }";
    internal const string CandidateFeature = "public static class Feature { public static bool Enabled => true; }";
    internal const string CandidateOther = "public static class Other { public static bool AddedMember => true; }";
    private readonly GoalId _goalId = GoalId.New();
    internal string Root { get; } = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("mcg-source-reverted-probes"), Guid.NewGuid().ToString("N"));
    internal string CandidateSha { get; private set; } = "";
    internal string CandidateTest { get; } = "public sealed class AddedProbeTests { [Xunit.Fact] public void AddedBehavior() => Xunit.Assert.True(Feature.Enabled && Other.AddedMember); }";

    internal SourceRevertedProbeFixture()
    {
        Write("config/acceptance-manifest.json", """
            { "version": 1, "engine": {
              "slotCount": 1, "maxConcurrentShards": 1, "enforceStructuralCoverage": false,
              "mtpInvocations": [{
                "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
              }] }, "checks": [], "forbiddenChangedPathGlobs": [] }
            """);
        Write(".gitignore", ".orchestrator/\nbin/\nobj/\n");
        Write("Directory.Build.props", "<Project><PropertyGroup><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>");
        Write("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup><ItemGroup><Compile Include="../../src/**/*.cs" /><PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.3.2" /><PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" /></ItemGroup></Project>
            """);
        Write(Feature, BaselineFeature);
        Write(Other, "public static class Other { public static bool Enabled => true; }");
        Write(Unchanged, "public static class Unchanged { }");
        Write(Deleted, "public static class Deleted { }");
        Git("init", "-b", "main");
        Git("config", "user.email", "source-reverted@example.invalid");
        Git("config", "user.name", "Source Reverted Probe");
        Git("add", ".");
        Git("commit", "-m", "source baseline");
        Git("checkout", "-b", "goal/source-reverted");
        Write(Feature, CandidateFeature);
        Write(Other, CandidateOther);
        Write(Added, "public static class Added { }");
        Write(UnlistedAdded, "public static class UnlistedAdded { }");
        File.Delete(Path.Combine(Root, Deleted));
        Write(Test, CandidateTest);
        Git("add", ".");
        Git("commit", "-m", "source candidate");
        CandidateSha = Git("rev-parse", "HEAD").Trim();
    }

    internal Task<FocusedEvidenceRunResult> RunAsync(GoalAcceptanceVerifier verifier, IReadOnlyList<string>? paths) =>
        verifier.RunFocusedEvidenceAsync(Root, _goalId, "Core.Tests: AddedProbeTests",
            negativeControl: FindingEvidenceNegativeControl.RevertSrc, revertPaths: paths);

    internal string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    private void Write(string path, string content)
    {
        var target = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    internal string Git(params string[] arguments)
    {
        var result = GitCli.Run(Root, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output;
    }

    internal static void MakeTrxRed(string[] args)
    {
        var path = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
            args[Array.IndexOf(args, "--report-trx-filename") + 1]);
        var document = XDocument.Load(path);
        document.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
        var counters = document.Descendants("Counters").Single();
        counters.SetAttributeValue("failed", 1);
        counters.SetAttributeValue("passed", 0);
        document.Save(path);
    }

    public void Dispose()
    {
        DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goalId);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
