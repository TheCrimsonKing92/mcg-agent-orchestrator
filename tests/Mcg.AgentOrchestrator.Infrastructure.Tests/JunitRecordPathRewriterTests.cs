using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;

// Parallel-safe: paths are interpreted under a unique root, without global state.
public sealed class JunitRecordPathRewriterTests
{
    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("opaque")]
    [InlineData("absent")]
    [InlineData("middle")]
    public void PathBearingPrefix_RebasesOnlySourcePaths(string kind)
    {
        using var fixture = new JunitCommandFixture("component");
        var file = kind == "absolute" ? Path.Combine(fixture.Root, "component", "src", "unit.case") : "src/unit.case";
        if (kind == "backslash") file = file.Replace('/', '\\');
        var id = kind switch { "opaque" => "Class.Name::case", "absent" => "opaque", "middle" => "opaque::" + file, _ => file.Replace('\\', '/') + "::case" };
        var record = new TestResult(id, "case", kind == "absent" ? null : file, ["suite"], TestResultStatus.Passed,
            7, "message", "trace", [], "seed", "output", "tool");
        var rewritten = JunitRecordPathRewriter.Rewrite(record, fixture.Root, Path.GetFullPath(fixture.ProjectRoot));
        Assert.Equal(kind == "absent" ? null : "component/src/unit.case", rewritten.File);
        Assert.Equal(kind is "opaque" or "absent" or "middle" ? id : "component/src/unit.case::case", rewritten.Id);
        Assert.Equal(record with { Id = rewritten.Id, File = rewritten.File }, rewritten);
    }

    [Theory]
    [InlineData("../outside.case")]
    [InlineData("absolute")]
    public void ExternalSource_RefusesWithOffendingPath(string path)
    {
        using var fixture = new JunitCommandFixture();
        var source = path == "absolute" ? Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside.case") : path;
        var record = new TestResult(source + "::case", "case", source, [], TestResultStatus.Passed, null, null, null, [], null, null, "tool");
        var error = Assert.Throws<IOException>(() => JunitRecordPathRewriter.Rewrite(record, fixture.Root, Path.GetFullPath(fixture.ProjectRoot)));
        Assert.Contains(source, error.Message);
    }
}
