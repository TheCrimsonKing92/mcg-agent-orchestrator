using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: read-only assembly and repository inspection.
public sealed class ConsoleViewsRetirementTests
{
    [Fact]
    public void AppAssembly_ConsoleViewsRetired_HasNoTypeWithThatSimpleName()
    {
        var assembly = typeof(GoalListTextView).Assembly;
        Assert.Equal("Mcg.AgentOrchestrator.App", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Name == "ConsoleViews");
    }

    [Theory]
    [InlineData("ConsoleViews.cs")]
    [InlineData("ConsoleViews.Configuration.cs")]
    public void CliSource_ConsoleViewsRetired_HasNoOriginalFile(string fileName)
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.App", "Cli", fileName);

        Assert.False(File.Exists(path), $"Retired source file still exists: {path}");
    }
}
