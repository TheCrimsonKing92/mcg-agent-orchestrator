using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its directories and environment dictionary; no process state is changed.
public sealed class DeclaredToolHomeEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "declared-tool-home-" + Guid.NewGuid().ToString("N"));

    public DeclaredToolHomeEnvironmentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Apply_DeclaredHomes_PreservesExactValuesAndOtherBindings()
    {
        var directory = Path.Combine(_root, "%literal%");
        Directory.CreateDirectory(directory);
        var declaredDirectory = Path.Combine(directory, ".");
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = "profile-root", ["BUILD_TOOL_HOME"] = "inherited"
        };

        DeclaredToolHomeEnvironment.Apply(environment, [new("BUILD_TOOL_HOME", declaredDirectory)]);

        Assert.Equal(declaredDirectory, environment["BUILD_TOOL_HOME"]);
        Assert.Equal("profile-root", environment["HOME"]);
        Assert.Equal(2, environment.Count);
    }

    [Fact]
    public void Apply_MissingLaterDirectory_RejectsBeforeChangingAnyBindings()
    {
        var missing = Path.Combine(_root, "missing");
        DeclaredToolHome[] homes = [new("BUILD_TOOL_HOME", _root), new("OTHER_TOOL_HOME", missing)];
        var environment = new Dictionary<string, string?> { ["BUILD_TOOL_HOME"] = "inherited" };

        var diagnostic = DeclaredToolHomeEnvironment.DescribeMissing(homes);
        var error = Assert.Throws<InvalidOperationException>(() => DeclaredToolHomeEnvironment.Apply(environment, homes));

        Assert.Equal(diagnostic, error.Message);
        Assert.Contains("OTHER_TOOL_HOME", error.Message);
        Assert.Contains(missing, error.Message);
        Assert.Equal("inherited", environment["BUILD_TOOL_HOME"]);
        Assert.Single(environment);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Declares_InvocationScopedHomes_DoesNotPermitOtherNames()
    {
        DeclaredToolHome[] homes = [new("BUILD_TOOL_HOME", _root)];

        Assert.True(DeclaredToolHomeEnvironment.Declares("build_tool_home", homes));
        Assert.False(DeclaredToolHomeEnvironment.Declares("OTHER_TOOL_HOME", homes));
        Assert.False(DeclaredToolHomeEnvironment.Declares("BUILD_TOOL_HOME", []));
        Assert.False(DeclaredToolHomeEnvironment.Declares("BUILD_TOOL_HOME", null));
        Assert.Null(DeclaredToolHomeEnvironment.DescribeMissing(homes));
        Assert.Null(DeclaredToolHomeEnvironment.DescribeMissing([]));
    }

    [Fact]
    public void Apply_NoDeclarations_KeepsOnlyExistingBindings()
    {
        var environment = new Dictionary<string, string?> { ["HOME"] = "profile-root" };

        DeclaredToolHomeEnvironment.Apply(environment, []);

        Assert.Single(environment);
        Assert.Equal("profile-root", environment["HOME"]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
