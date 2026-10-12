using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns a source copy, registry, data root and destination tree.
public sealed class ProjectEmitMtpInvocationCliTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";

    [Fact]
    public void Emit_MtpAnswer_RoundTripsAndRefusesChangedLayout()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        using var scratch = new ProjectOnboardingFixture("no-solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        var project = registry.CreateProject("sample", source.Root);
        var modelDirectory = project.ResolveWorkspace().OrchestratorDirectory;
        Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);
        foreach (var (key, value) in new[] { ("defaultMinutes", "17"),
            ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
            Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
        Assert.Equal(0, Execute(["project", "answer", "sample", "commands/" + Unit + "/test", "MTP"]).Exit);
        var answerPath = Path.Combine(modelDirectory, OwnerAnswerFile.FileName);
        var answers = File.ReadAllBytes(answerPath);
        var destination = Path.Combine(scratch.Root, "learned.json");
        var emitted = Execute(["project", "emit-manifest", "sample", "--out", destination]);
        Assert.Equal(0, emitted.Exit);
        Assert.Equal($"Emitted manifest: {destination} (1 checks)", Assert.Single(emitted.Lines));
        var bytes = File.ReadAllBytes(destination);
        var json = File.ReadAllText(destination);
        var manifest = JsonSerializer.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        var check = Assert.Single(manifest.Checks);
        Assert.Equal("dotnet-test", check.Type);
        Assert.Equal(Unit, check.Project);
        Assert.Equal("mtp", check.Runner);
        var settings = AcceptanceGateEngineSettings.Parse(json);
        Assert.Equal(Unit, Assert.Single(settings.MtpInvocations).Project);
        Assert.Same(settings.MtpInvocations[0], settings.ResolveMtpInvocation(Unit));
        var installed = Execute(["project", "install-manifest", "sample", "--from", destination]);
        Assert.Equal(0, installed.Exit);
        var live = Path.Combine(modelDirectory, "acceptance-manifest.json");
        Assert.Equal(bytes, File.ReadAllBytes(live));
        Assert.Equal(answers, File.ReadAllBytes(answerPath));

        // The same stored model/answers must refuse after the actual source layout changes.
        var projectFile = Path.GetFullPath(Path.Combine(source.Root, Unit));
        var xml = XDocument.Load(projectFile);
        xml.Root!.Element("PropertyGroup")!.Add(new XElement("TargetFrameworks", "net8.0;net9.0"));
        xml.Save(projectFile);
        var refusedDestination = Path.Combine(scratch.Root, "refused.json");
        var refused = Execute(["project", "emit-manifest", "sample", "--out", refusedDestination]);
        Assert.Equal(1, refused.Exit);
        AssertLayoutBlocker(refused.Lines);
        Assert.False(File.Exists(refusedDestination));
        Assert.Equal(answers, File.ReadAllBytes(answerPath));

        var refusedInstall = Execute(["project", "install-manifest", "sample", "--from", destination]);
        Assert.Equal(1, refusedInstall.Exit);
        AssertLayoutBlocker(refusedInstall.Lines);
        Assert.Equal(bytes, File.ReadAllBytes(live));
        Assert.Equal(answers, File.ReadAllBytes(answerPath));

        void AssertLayoutBlocker(string[] lines)
        {
            var blocker = Assert.Single(lines.Where(line => line.StartsWith("Blocked engine/mtpInvocations ", StringComparison.Ordinal)));
            Assert.Contains(Unit, blocker);
            Assert.Contains(projectFile, blocker);
            Assert.Contains("TargetFrameworks", blocker);
        }

        (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
