using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: private source copy, registry, data root, tool directories and output files.
public sealed class ProjectToolHomeManifestCliTests
{
    [Fact]
    public void Execute_ToolHomeAnswers_EmitsSortedHomesAndComparesChanges()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        using var scratch = new ProjectOnboardingFixture("no-solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        registry.CreateProject("sample", source.Root);
        Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);
        foreach (var key in new[] { "defaultMinutes", "buildServerShutdownMinutes", "discoveryMinutes" })
            Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, "8"]).Exit);
        var first = Path.Combine(scratch.Root, "first");
        var second = Path.Combine(scratch.Root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Answer("RUSTUP_HOME", first);
        Answer("CARGO_HOME", first);
        Answer("RUSTUP_HOME", second); // Re-answering replaces the existing variable.
        var path = Path.Combine(scratch.Root, "manifest.json");

        Assert.Equal(0, Execute(["project", "emit-manifest", "sample", "--out", path]).Exit);

        Assert.True(File.Exists(path));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var homes = json["toolHomes"]!.AsArray();
        Assert.Equal(new[] { "CARGO_HOME", "RUSTUP_HOME" },
            homes.Select(entry => entry!["variable"]!.GetValue<string>()));
        Assert.Equal(new[] { first, second }, homes.Select(entry => entry!["directory"]!.GetValue<string>()));
        var matched = Compare();
        Assert.Equal(0, matched.Exit);
        Assert.Contains(matched.Lines, line => line.StartsWith("Match toolHome[RUSTUP_HOME] ", StringComparison.Ordinal));
        Assert.Contains(matched.Lines, line => line.StartsWith("Match toolHome[CARGO_HOME] ", StringComparison.Ordinal));

        // Ordering alone has no meaning; changing one directory must produce exit 1.
        var reordered = new JsonArray(homes.Reverse().Select(entry => entry!.DeepClone()).ToArray());
        json["toolHomes"] = reordered;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(0, Compare().Exit);
        reordered[0]!["directory"] = first;
        File.WriteAllText(path, json.ToJsonString());
        var changed = Compare();
        Assert.Equal(1, changed.Exit);
        var difference = Assert.Single(changed.Lines.Where(line => line.StartsWith("DiffersFromHand ", StringComparison.Ordinal)));
        Assert.Contains("toolHome[RUSTUP_HOME]", difference);
        Assert.Contains($"hand={first}", difference);
        Assert.Contains($"learned={second}", difference);

        void Answer(string variable, string directory) => Assert.Equal(0, Execute(
            ["project", "answer", "sample", "environment/tool-home/" + variable, directory]).Exit);
        (int Exit, string[] Lines) Compare() => Execute(["project", "compare-manifest", "sample", "--manifest", path]);
        (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
