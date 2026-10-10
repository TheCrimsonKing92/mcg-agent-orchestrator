using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each test owns its fixture copies, registry and output writer.
public sealed class ProjectCompareIntegrationBranchCliTests
{
    private const string Usage = "Usage: project compare-integration-branch [name] [--root <path>]";

    [Theory]
    [InlineData("Match", "trunk", 0)]
    [InlineData("BranchDiffers", "master", 1)]
    [InlineData("Unresolved", "main", 1)]
    public void Execute_Comparison_PrintsOneLineWithoutWriting(string kind, string branch, int expectedExit)
    {
        using var home = new ProjectOnboardingFixture("answer-key-repository");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        if (kind != "Unresolved") WriteRemote(source, "trunk");
        var registry = Registry(home);
        registry.CreateProject("sample", home.Root, branch);
        var sourceBefore = Snapshot(source.Root);
        var homeBefore = Snapshot(home.Root);
        using var output = new StringWriter();

        var exit = ProjectCliCommand.Execute(
            ["project", "compare-integration-branch", "--root", source.Root],
            registry, home.Root, "sample", output);

        Assert.Equal(expectedExit, exit);
        var line = Assert.Single(Lines(output));
        Assert.StartsWith(kind + " ", line);
        Assert.Contains("learned=" + (kind == "Unresolved" ? "-" : "trunk"), line);
        Assert.Contains("registry=" + branch, line);
        Assert.Contains("confidence=" + (kind == "Unresolved" ? "-" : "High"), line);
        if (kind == "Unresolved") Assert.Contains("integration-branch", line);
        AssertUnchanged(source.Root, sourceBefore);
        AssertUnchanged(home.Root, homeBefore);
        AssertNoModel(registry.DataRootDirectory);
        AssertNoModel(home.Root);
        AssertNoModel(source.Root);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("empty")]
    [InlineData("option-as-value")]
    public void Execute_InvalidOptions_ThrowsOwnUsageBeforeWriting(string scenario)
    {
        using var home = new ProjectOnboardingFixture("answer-key-repository");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var registry = Registry(home);
        string[] options = scenario switch
        {
            "unknown" => ["--unknown"],
            "missing" => ["--root"],
            "repeated" => ["--root", source.Root, "--root=" + source.Root],
            "empty" => ["--root="],
            _ => ["--root", "--root=" + source.Root]
        };
        var sourceBefore = Snapshot(source.Root);
        var homeBefore = Snapshot(home.Root);
        using var output = new StringWriter();

        var exception = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "compare-integration-branch", .. options], registry, home.Root, null, output));

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
    public void Execute_ProjectSelection_MirrorsSiblingRootPrecedence(string selection)
    {
        using var home = new ProjectOnboardingFixture("answer-key-repository");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        var registry = Registry(home);
        registry.CreateProject("sample", source.Root, "trunk");
        registry.SelectProject(selection == "active-override" ? "default" : "sample");
        var defaultBranch = new OrchestratorProject("default", home.Root).IntegrationBranch;
        WriteRemote(home, defaultBranch);
        WriteRemote(source, "trunk");
        var useDefault = selection == "default";
        if (selection == "root-override") WriteRemote(home, "trunk");
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
            ["project", "compare-integration-branch", .. options], registry, home.Root,
            selection == "active-override" ? "sample" : null, output));

        var line = Assert.Single(Lines(output));
        Assert.StartsWith("Match ", line);
        Assert.Contains("learned=" + (useDefault ? defaultBranch : "trunk"), line);
        AssertUnchanged(source.Root, sourceBefore);
        AssertUnchanged(home.Root, homeBefore);
    }

    private static void WriteRemote(ProjectOnboardingFixture source, string branch)
    {
        var path = Path.Combine(source.Root, "." + "git", "refs", "remotes", "origin", "HEAD");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ref: refs/remotes/origin/" + branch);
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
