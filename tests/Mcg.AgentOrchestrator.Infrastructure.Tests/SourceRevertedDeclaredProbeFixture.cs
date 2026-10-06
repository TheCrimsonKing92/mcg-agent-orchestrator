using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git/worktree boundary; execution is injected by the contract tests.
internal sealed class SourceRevertedDeclaredProbeFixture : IDisposable
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Core.Tests/";
    internal const string Support = Project + "ProbeSupport.cs";
    internal const string Counter = Project + "ProbeSupportCounter.cs";
    internal const string Selected = Project + "SupportProbeTests.cs";
    internal const string Legacy = Project + "LegacyProbeTests.cs";
    internal const string Policy = "config/probe-policy.json";
    internal const string Unchanged = "config/unchanged.json";
    internal const string Guide = ".agents/skills/probe/guide.md";
    internal const string MainSupport = "public static class ProbeSupport { public static int Launches => 2; }";
    internal const string CandidateSupport = "public static class ProbeSupport { public static int Launches => 1; }";
    internal static readonly string[] Declared = [Support, Counter, Selected, Legacy, Policy, Unchanged, Guide];
    private readonly GoalId _goalId = GoalId.New();
    internal string Root { get; } = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("mcg-source-reverted-probes"), Guid.NewGuid().ToString("N"));
    internal string CandidateSha { get; }

    internal SourceRevertedDeclaredProbeFixture()
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
        Write(Support, MainSupport);
        Write(Legacy, "public sealed class LegacyProbeTests { [Xunit.Fact] public void Legacy() => Xunit.Assert.Equal(2, ProbeSupport.Launches); }");
        Write(Policy, """{ "launches": 2 }""");
        Write(Unchanged, "{}");
        Write(Guide, "Probe guide v1");
        Git("init", "-b", "main");
        Git("config", "core.autocrlf", "false");
        Git("config", "user.email", "declared-probe@example.invalid");
        Git("config", "user.name", "Declared Probe");
        Git("add", ".");
        Git("commit", "-m", "declared baseline");
        Git("checkout", "-b", "goal/declared-probe");
        Write(Support, CandidateSupport);
        Write(Counter, "public static class ProbeSupportCounter { public static int Count => 0; }");
        Write(Selected, "public sealed class SupportProbeTests { [Xunit.Fact] public void OneLaunch() => Xunit.Assert.Equal(1, ProbeSupport.Launches); }");
        Write(Legacy, "public sealed class LegacyProbeTests { [Xunit.Fact] public void Legacy() => Xunit.Assert.Equal(1, ProbeSupport.Launches); }");
        Write(Policy, """{ "launches": 1 }""");
        Write(Guide, "Probe guide v2");
        Git("add", ".");
        Git("commit", "-m", "declared candidate");
        CandidateSha = Git("rev-parse", "HEAD").Trim();
    }

    internal Task<FocusedEvidenceRunResult> RunAsync(GoalAcceptanceVerifier verifier, string selection,
        IReadOnlyList<string>? paths, IReadOnlyList<string>? declared, FindingEvidenceMutation? mutation = null) =>
        verifier.RunFocusedEvidenceAsync(Root, _goalId, "Core.Tests: " + selection,
            negativeControl: FindingEvidenceNegativeControl.RevertSrc, revertPaths: paths,
            mutation: mutation, declaredPaths: declared);

    internal string Read(string path) => File.ReadAllText(Path.Combine(Root, path));
    internal void Write(string path, string text)
    {
        var target = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text.ReplaceLineEndings("\n"));
    }
    internal string Git(params string[] arguments)
    {
        var result = GitCli.Run(Root, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output;
    }
    public void Dispose()
    {
        DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goalId);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
