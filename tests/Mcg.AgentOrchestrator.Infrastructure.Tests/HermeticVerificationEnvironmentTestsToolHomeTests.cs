using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTestsToolHomeTests
{
    [Fact]
    public void Configure_DeclaredHomes_AppliesOnlyThisProjectsDeclarations()
    {
        using var project = new ProjectOnboardingFixture("no-solution");
        var rustup = Path.Combine(project.Root, "toolchains");
        var cargo = Path.Combine(project.Root, "packages");
        Directory.CreateDirectory(rustup);
        Directory.CreateDirectory(cargo);
        DeclaredToolHome[] homes = [new("RUSTUP_HOME", rustup), new("CARGO_HOME", cargo)];
        var environment = InheritedEnvironment();

        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
            environment, project.Root, toolHomes: homes);

        Assert.Equal(rustup, environment["RUSTUP_HOME"]);
        Assert.Equal(cargo, environment["CARGO_HOME"]);
        var profile = Path.Combine(Path.GetTempPath(), "mcg-hvp");
        Assert.Equal(profile, environment["HOME"]);
        Assert.Equal(profile, environment["USERPROFILE"]);
        Assert.All(environment.Keys, name => Assert.True(
            AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(name, homes), name));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable("RUSTUP_HOME"));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable("CARGO_HOME"));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable("OTHER_TOOL_HOME", homes));

        // A second project inherits the same ambient values but declares neither home.
        var undeclared = InheritedEnvironment();
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(undeclared, project.Root);
        Assert.DoesNotContain("RUSTUP_HOME", undeclared.Keys);
        Assert.DoesNotContain("CARGO_HOME", undeclared.Keys);
        Assert.Equal(profile, undeclared["HOME"]);
        Assert.Equal(profile, undeclared["USERPROFILE"]);
        Assert.All(undeclared.Keys, name => Assert.True(
            AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(name), name));
    }

    private static Dictionary<string, string?> InheritedEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["RUSTUP_HOME"] = "inherited-toolchains", ["CARGO_HOME"] = "inherited-packages",
        ["HOME"] = "inherited-profile", ["USERPROFILE"] = "inherited-profile"
    };
}
