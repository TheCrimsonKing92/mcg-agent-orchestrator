using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using System.Text.Json.Nodes;
using Xunit;

// Parallel-safe: each test owns its source copies, registry, workspace and output writer.
public sealed class ProjectCompareManifestCliTests
{
    private const string Usage = "Usage: project compare-manifest [name] [--root <path>] --manifest <path>";
    private const string AppTest = "tests/AnswerKey.App.Tests/AnswerKey.App.Tests.csproj";
    private const string CoreTest = "tests/AnswerKey.Core.Tests/AnswerKey.Core.Tests.csproj";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compare_RunnerMatchOrDifference_ReturnsStatusWithoutWriting(bool mismatch)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var manifestPath = Path.Combine(source.Root, "hand-acceptance-manifest.json");
        if (mismatch)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["checks"]![0]!["runner"] = "mtp";
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        var registry = Registry(home);
        var sourceBefore = Snapshot(source.Root);
        var homeBefore = Snapshot(home.Root);
        using var output = new StringWriter();

        var exit = ProjectCliCommand.Execute(
            ["project", "compare-manifest", "--root", source.Root, "--manifest", manifestPath],
            registry, home.Root, null, output);

        Assert.Equal(mismatch ? 1 : 0, exit);
        var lines = Lines(output);
        Assert.Equal(2, lines.Length);
        Assert.Equal($"Match {AppTest} learned=vstest manifest=vstest", lines[0]);
        Assert.Equal(mismatch
            ? $"RunnerDiffers {CoreTest} learned=vstest manifest=mtp (The learned and manifest runners differ.)"
            : $"Match {CoreTest} learned=vstest manifest=vstest", lines[1]);
        AssertUnchanged(source.Root, sourceBefore);
        AssertUnchanged(home.Root, homeBefore);
        AssertNoModel(registry.DataRootDirectory);
        AssertNoModel(home.Root);
        AssertNoModel(source.Root);
    }

    [Fact]
    public void Compare_NoTestUnitsOrDotnetChecks_ReturnsZeroAndNoOutput()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        // Keep the answer-key manifest but remove solution membership and all test projects.
        File.Delete(Path.Combine(source.Root, "AnswerKey.sln"));
        Directory.Delete(Path.Combine(source.Root, "tests"), recursive: true);
        var manifestPath = Path.Combine(source.Root, "hand-acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        foreach (var check in manifest["checks"]!.AsArray()) check!["type"] = "command";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var registry = Registry(home);
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(
            ["project", "compare-manifest", "--root", source.Root, "--manifest", manifestPath],
            registry, home.Root, null, output));
        Assert.Empty(output.ToString());
        AssertNoModel(registry.DataRootDirectory);
    }

    [Theory]
    [InlineData("no-manifest")]
    [InlineData("missing-file")]
    [InlineData("directory")]
    [InlineData("malformed-json")]
    [InlineData("invalid-shape")]
    [InlineData("unknown-option")]
    [InlineData("missing-manifest-value")]
    [InlineData("missing-root-value")]
    [InlineData("option-as-value")]
    [InlineData("empty-manifest-value")]
    [InlineData("duplicate-manifest")]
    [InlineData("duplicate-root")]
    public void Compare_InvalidArgumentsOrManifest_ThrowsWithOwnUsage(string scenario)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var manifestPath = Path.Combine(source.Root, "hand-acceptance-manifest.json");
        if (scenario == "malformed-json") File.WriteAllText(manifestPath, "{");
        if (scenario == "invalid-shape") File.WriteAllText(manifestPath, "{\"checks\":{}}");
        var registry = Registry(home);
        string[] tail = scenario switch
        {
            "no-manifest" => [],
            "missing-file" => ["--manifest", Path.Combine(source.Root, "missing.json")],
            "directory" => ["--manifest", source.Root],
            "unknown-option" => ["--manifest", manifestPath, "--unknown"],
            "missing-manifest-value" => ["--manifest"],
            "missing-root-value" => ["--manifest", manifestPath, "--root"],
            "option-as-value" => ["--manifest", "--root"],
            "empty-manifest-value" => ["--manifest="],
            "duplicate-manifest" => ["--manifest", manifestPath, "--manifest=" + manifestPath],
            "duplicate-root" => ["--manifest", manifestPath, "--root=" + source.Root],
            _ => ["--manifest", manifestPath]
        };
        var sourceBefore = Snapshot(source.Root);
        var homeBefore = Snapshot(home.Root);
        using var output = new StringWriter();

        var exception = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "compare-manifest", "--root", source.Root, .. tail], registry, home.Root, null, output));

        Assert.Contains(Usage, exception.Message);
        Assert.Empty(output.ToString());
        AssertUnchanged(source.Root, sourceBefore);
        AssertUnchanged(home.Root, homeBefore);
        AssertNoModel(registry.DataRootDirectory);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("selected")]
    [InlineData("active-override")]
    [InlineData("default")]
    [InlineData("root-override")]
    public void Compare_ProjectSelection_UsesDiscoverRootPrecedence(string selection)
    {
        using var home = new ProjectOnboardingFixture("answer-key-repository");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var registry = Registry(home);
        registry.CreateProject("sample", source.Root);
        registry.SelectProject(selection == "active-override" ? "default" : "sample");
        // Make the selected root differ from the default, so a wrong precedence cannot pass.
        var sourceManifest = Path.Combine(source.Root, "hand-acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(sourceManifest))!;
        manifest["checks"]![0]!["runner"] = "mtp";
        File.WriteAllText(sourceManifest, manifest.ToJsonString());
        var project = Path.Combine(source.Root, CoreTest);
        var text = File.ReadAllText(project).Replace("<IsTestProject>true</IsTestProject>",
            "<IsTestProject>true</IsTestProject><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>");
        // The MTP declarations are intentionally unambiguous, following the existing solution fixture.
        text = text.Replace("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />", "");
        text = text.Replace("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />",
            "<PackageReference Include=\"xunit.v3.mtp-v2\" Version=\"3.2.2\" />");
        text = text.Replace("<PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.4\" />", "");
        File.WriteAllText(project, text);
        var useDefault = selection is "default" or "root-override";
        var manifestPath = useDefault ? Path.Combine(home.Root, "hand-acceptance-manifest.json") : sourceManifest;
        string[] options = selection switch
        {
            "named" => ["sample"],
            "default" => ["default"],
            "root-override" => ["sample", "--root=" + home.Root],
            _ => []
        };
        var sourceBefore = Snapshot(source.Root);
        var homeBefore = Snapshot(home.Root);
        using var output = new StringWriter();
        Assert.Equal(0, ProjectCliCommand.Execute(
            ["project", "compare-manifest", .. options, "--manifest=" + manifestPath], registry, home.Root,
            selection == "active-override" ? "sample" : null, output));
        Assert.Equal(new[]
        {
            $"Match {AppTest} learned=vstest manifest=vstest",
            $"Match {CoreTest} learned={(useDefault ? "vstest" : "mtp")} manifest={(useDefault ? "vstest" : "mtp")}"
        }, Lines(output));
        AssertUnchanged(source.Root, sourceBefore);
        AssertUnchanged(home.Root, homeBefore);
        AssertNoModel(registry.DataRootDirectory);
    }

    private static OrchestratorProjectRegistry Registry(ProjectOnboardingFixture home) =>
        new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static void AssertNoModel(string root)
    {
        if (Directory.Exists(root))
            Assert.Empty(Directory.GetFiles(root, "project-model.json", SearchOption.AllDirectories));
    }
}
