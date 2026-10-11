using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private temp copies, registry and output files; no global state changes.
public sealed class ProjectEmitManifestRoundTripCliTests
{
    private const string AppTest = "tests/AnswerKey.App.Tests/AnswerKey.App.Tests.csproj";
    private const string CoreTest = "tests/AnswerKey.Core.Tests/AnswerKey.Core.Tests.csproj";

    [Fact]
    public void Emit_SettledAnswers_RoundTripsThroughComparersAndGateParsers()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        using var scratch = new ProjectOnboardingFixture("no-solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        var project = registry.CreateProject("sample", source.Root);
        Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);
        foreach (var (key, value) in new[] { ("defaultMinutes", "17"), ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
            Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
        var destination = Path.Combine(scratch.Root, "learned.json");
        var emitted = Execute(["project", "emit-manifest", "sample", "--out", destination]);
        Assert.Equal(0, emitted.Exit);
        Assert.Equal($"Emitted manifest: {destination} (2 checks)", Assert.Single(emitted.Lines));

        var checksComparison = Compare("compare-manifest", destination);
        Assert.Equal(0, checksComparison.Exit);
        Assert.Equal(2, checksComparison.Lines.Length);
        Assert.All(checksComparison.Lines, line => Assert.StartsWith("Match ", line));
        var engineComparison = Compare("compare-manifest-engine", destination);
        Assert.Equal(0, engineComparison.Exit);
        var comparisons = engineComparison.Lines.Where(line => !line.StartsWith("Owner answer:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(comparisons);
        Assert.All(comparisons, line => Assert.StartsWith("Match ", line));
        Assert.Equal(3, engineComparison.Lines.Count(line => line.StartsWith("Owner answer:", StringComparison.Ordinal)));
        Assert.DoesNotContain(engineComparison.Lines, line => line.StartsWith("OwnerQuestion engine/", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "DiffersFromHand", "DiffersFromGateDefault", "NotConsumed", "MissingFromLearned",
            "MissingFromManifest", "RunnerDiffers", "Unresolved", "Stale owner answer:" })
            Assert.DoesNotContain(checksComparison.Lines.Concat(engineComparison.Lines), line => line.StartsWith(forbidden, StringComparison.Ordinal));

        var json = File.ReadAllText(destination);
        var manifest = JsonSerializer.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.Version);
        Assert.Equal(new[] { AppTest, CoreTest }, manifest.Checks.Select(check => check.Project));
        Assert.Empty(manifest.ForbiddenChangedPathGlobs);
        var settings = AcceptanceGateEngineSettings.Parse(json);
        Assert.Equal(new[] { AppTest, CoreTest }, settings.DeclaredDotnetTestChecks.Select(check => check.Project));
        var model = ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(project.ResolveWorkspace().OrchestratorDirectory, "project-model.json")));
        Assert.Equal(LearnedEngineSettingsDeriver.Derive(model).MaxConcurrentShards, settings.MaxConcurrentShards);
        Assert.Equal(17, settings.Timeouts.DefaultMinutes);
        Assert.Equal(3, settings.Timeouts.BuildServerShutdownMinutes);
        Assert.Equal(4, settings.Timeouts.DiscoveryMinutes);

        // Executable negative controls: the existing comparers must catch changed facts.
        var changedRunner = JsonNode.Parse(json)!;
        changedRunner["checks"]![0]!["runner"] = "mtp";
        var runnerPath = Path.Combine(scratch.Root, "runner-control.json");
        File.WriteAllText(runnerPath, changedRunner.ToJsonString());
        var runnerComparison = Compare("compare-manifest", runnerPath);
        Assert.Equal(1, runnerComparison.Exit);
        Assert.Contains(runnerComparison.Lines, line => line.StartsWith($"RunnerDiffers {AppTest} ", StringComparison.Ordinal));
        var changedTimeout = JsonNode.Parse(json)!;
        changedTimeout["engine"]!["timeouts"]!["discoveryMinutes"] = 5;
        var timeoutPath = Path.Combine(scratch.Root, "timeout-control.json");
        File.WriteAllText(timeoutPath, changedTimeout.ToJsonString());
        var timeoutComparison = Compare("compare-manifest-engine", timeoutPath);
        Assert.Equal(1, timeoutComparison.Exit);
        Assert.Contains(timeoutComparison.Lines,
            line => line.StartsWith("DiffersFromHand engine.timeouts.discoveryMinutes ", StringComparison.Ordinal));
        Assert.Equal(json, File.ReadAllText(destination));

        (int Exit, string[] Lines) Compare(string verb, string path) =>
            Execute(["project", verb, "sample", "--manifest", path]);
        (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
