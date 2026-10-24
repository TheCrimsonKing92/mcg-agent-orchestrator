using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns its fixture directory; discovery never executes a tool.
public sealed class GoProjectDiscoveryAdapterTests
{
    private static readonly string[] UnitIds =
        ["cmd/tool", "internal/core", "internal/native", "internal/util", "internal/winonly"];

    [Fact]
    public void Discover_Module_ReturnsExactUnitsWithDeclarationSources()
    {
        using var fixture = Fixture();
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(UnitIds, model.Units.Select(unit => unit.Id));
        Assert.Equal(UnitIds.Select(id => "example.com/fixture/" + id), model.Units.Select(unit => unit.Name.Value));
        Assert.Equal(new bool?[] { true, true, false, false, false }, model.Units.Select(unit => unit.IsTest.Value));
        foreach (var unit in model.Units)
        {
            Assert.Equal(unit.Id, unit.Location.Value);
            Assert.Equal(FactConfidence.High, unit.Name.Confidence);
            Assert.Equal(FactConfidence.High, unit.Location.Confidence);
            Assert.Equal(FactConfidence.High, unit.IsTest.Confidence);
            Assert.Contains("module", SourceLine(fixture, unit.Name.Source));
            Assert.Contains("package", SourceLine(fixture, unit.Location.Source));
            Assert.Contains("package", SourceLine(fixture, unit.IsTest.Source));
        }
        Assert.Equal(new FactSource("internal/util/util.go", 2), model.Units.Single(unit => unit.Id == "internal/util").Location.Source);
        Assert.Equal(new FactSource("internal/core/core_external_test.go", 1), model.Units.Single(unit => unit.Id == "internal/core").IsTest.Source);
        Assert.Equal(new FactSource("internal/winonly/helper_windows.go", 1), model.Units.Single(unit => unit.Id == "internal/winonly").Location.Source);
    }

    [Fact]
    public void Discover_Module_ResolvesOnlyInternalImportsWithoutSelfEdges()
    {
        using var fixture = Fixture();
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(new[] { ("cmd/tool", "internal/core"), ("internal/core", "internal/util") },
            model.Dependencies.Select(edge => (edge.FromUnit, edge.ToUnit)));
        Assert.All(model.Dependencies, edge =>
        {
            Assert.Equal(FactConfidence.High, edge.Confidence);
            Assert.Contains("example.com/fixture/" + edge.ToUnit, SourceLine(fixture, edge.Source));
        });
    }

    [Fact]
    public void Discover_Module_DerivesSafeBuildsAndOnlyDeclaredTestCommands()
    {
        using var fixture = Fixture();
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(UnitIds, model.Commands.Select(command => command.UnitId));
        Assert.Equal(new[]
        {
            "go build -o NUL ./cmd/tool", "go build ./internal/core", "go build ./internal/native",
            "go build ./internal/util", "go build ./internal/winonly"
        }, model.Commands.Select(command => command.BuildCommand.Value));
        Assert.Equal(new string?[] { "go test ./cmd/tool", "go test ./internal/core", null, null, null },
            model.Commands.Select(command => command.TestCommand?.Value));
        Assert.Equal(new[] { "cmd/tool", "internal/core" }, model.TestSetups.Select(setup => setup.UnitId));
        Assert.All(model.TestSetups, setup =>
        {
            Assert.Equal("go test", setup.Framework.Value);
            Assert.Equal("go", setup.Runner.Value);
            Assert.Equal(FactConfidence.High, setup.Framework.Confidence);
            Assert.Equal(FactConfidence.High, setup.Runner.Confidence);
            Assert.Contains("package", SourceLine(fixture, setup.Framework.Source));
            Assert.Contains("package", SourceLine(fixture, setup.Runner.Source));
        });
        Assert.All(model.Commands, command =>
        {
            Assert.Equal(FactConfidence.High, command.BuildCommand.Confidence);
            Assert.Contains("package", SourceLine(fixture, command.BuildCommand.Source));
            if (command.TestCommand is { } test)
            {
                Assert.Equal(FactConfidence.High, test.Confidence);
                Assert.Contains("package", SourceLine(fixture, test.Source));
            }
        });
    }

    [Fact]
    public void Discover_Module_ReportsFourHazardsAndIgnoresNegativeControls()
    {
        using var fixture = Fixture();
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(new[]
        {
            ("cmd/tool", "go-test-builds-binary", "cmd/tool", "cmd/tool/main_test.go", 9),
            ("internal/native", "go-cgo", "cgo", "internal/native/native.go", 4),
            ("internal/winonly", "go-build-constraint", "filename:_windows", "internal/winonly/helper_windows.go", 1),
            ("internal/winonly", "go-build-constraint", "windows", "internal/winonly/win.go", 1)
        }, model.Hazards.Select(hazard => (hazard.UnitId, hazard.Name, hazard.IsolationKey.Value,
            hazard.IsolationKey.Source.Path, hazard.IsolationKey.Source.Line!.Value)));
        Assert.All(model.Hazards, hazard =>
        {
            Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
            SourceLine(fixture, hazard.IsolationKey.Source);
        });
    }

    [Fact]
    public void Discover_Module_RecordsToolchainNestedModuleAndDeterministicJson()
    {
        using var fixture = Fixture();
        var adapter = new GoProjectDiscoveryAdapter();
        var model = adapter.Discover(fixture.Root);

        var need = Assert.Single(model.EnvironmentNeeds);
        Assert.Equal("go-toolchain", need.Name);
        Assert.Equal("1.21.5", need.Value.Value);
        Assert.Equal(FactConfidence.High, need.Value.Confidence);
        Assert.Equal(new FactSource("go.mod", 3), need.Value.Source);
        Assert.Contains("go 1.21.5", SourceLine(fixture, need.Value.Source));
        Assert.Equal("units/tools/module", Assert.Single(model.OwnerQuestions).FactKey);
        Assert.Equal(ProjectStack.Go, model.Stack);
        Assert.Equal(".", model.RepositoryRoot);
        Assert.Empty(model.Measurements);
        Assert.Equal(ProjectModelJson.Serialize(model), ProjectModelJson.Serialize(adapter.Discover(fixture.Root)));
    }

    [Fact]
    public void Discover_CommentsAndContextCalls_UsesOnlyExecutableBuildLiterals()
    {
        using var fixture = Fixture();
        Write(fixture, "cmd/tool/main_test.go", """
            /* //go:build ignored
            import "C"
            exec.Command("go", "build")
            */
            package main
            import runner "os/exec"
            import "context"
            var text = `runner.Command("go", "build")`
            var other = "runner.Command(\"go\", \"build\")"
            // runner.Command("go", "build")
            func test() {
                runner.CommandContext(
                    context.WithValue(context.Background(), ')', ','),
                    "go", /* build tool */ "build", ".")
                runner.Command("go", "build", ".")
                runner.Command("go", "build" + "suffix", ".")
                runner.Command("echo", "go", "build")
                runner.Command("go", "vet")
            }
            """);

        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        var hazard = Assert.Single(model.Hazards.Where(hazard => hazard.UnitId == "cmd/tool"));
        Assert.Equal("go-test-builds-binary", hazard.Name);
        Assert.Equal(12, hazard.IsolationKey.Source.Line);
        Assert.Contains("runner.CommandContext", SourceLine(fixture, hazard.IsolationKey.Source));
        Assert.DoesNotContain(model.Hazards, hazard => hazard.IsolationKey.Value == "ignored");
        Assert.Equal(4, model.Hazards.Count);
    }

    [Fact]
    public void Discover_ExcludedImports_PreservesLowEdgesAndQuestions()
    {
        using var fixture = Fixture();
        Write(fixture, "internal/util/util.go", """
            package util
            import "example.com/fixture/tools"
            import "example.com/fixture/internal/core/testdata"
            import "example.com/fixture/internal/native"
            import "example.com/fixture/vendor/dep"
            import "example.com/fixture/_ignored"
            import "example.com/fixture/.hidden"
            """);
        Write(fixture, "vendor/dep/dep.go", "package dep");
        Write(fixture, ".hidden/hidden.go", "package hidden");
        Write(fixture, "onlytests/only_test.go", "package onlytests");
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root, ["native"]);

        Assert.Equal(UnitIds.Where(id => id != "internal/native"), model.Units.Select(unit => unit.Id));
        var edges = model.Dependencies.Where(edge => edge.FromUnit == "internal/util").ToArray();
        Assert.Equal(new[] { ".hidden", "_ignored", "internal/core/testdata", "internal/native", "tools", "vendor/dep" },
            edges.Select(edge => edge.ToUnit));
        Assert.All(edges, edge =>
        {
            Assert.Equal(FactConfidence.Low, edge.Confidence);
            Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"dependencies/internal/util/{edge.ToUnit}");
            Assert.Contains("example.com/fixture/" + edge.ToUnit, SourceLine(fixture, edge.Source));
        });
    }

    [Fact]
    public void Discover_RootMainAndDirectives_RecordsPinAndCombinedFilenameConstraint()
    {
        using var fixture = Fixture();
        Write(fixture, "go.mod", """
            // module fake.example/comment
            module "example.com/fixture" // canonical module
            require (
                example.net/module v1.0.0
            )
            go 1.23.0
            toolchain go1.23.4
            """);
        Write(fixture, "main_linux_amd64.go", "// +build linux,amd64\n\npackage main\n");
        Write(fixture, "root_test.go", "package main\n");
        var model = new GoProjectDiscoveryAdapter().Discover(fixture.Root);

        var unit = model.Units[0];
        Assert.Equal(".", unit.Id);
        Assert.Equal("example.com/fixture", unit.Name.Value);
        Assert.Equal(new FactSource("go.mod", 2), unit.Name.Source);
        Assert.Equal("go build -o NUL .", model.Commands[0].BuildCommand.Value);
        Assert.Equal("go test .", model.Commands[0].TestCommand!.Value);
        Assert.Equal(new[] { ("go-toolchain", "1.23.0", 6), ("go-toolchain-pin", "go1.23.4", 7) },
            model.EnvironmentNeeds.Select(need => (need.Name, need.Value.Value, need.Value.Source.Line!.Value)));
        Assert.Equal(new[] { "filename:_linux_amd64", "linux,amd64" },
            model.Hazards.Where(hazard => hazard.UnitId == ".").Select(hazard => hazard.IsolationKey.Value));
    }

    private static ProjectOnboardingFixture Fixture()
    {
        var fixture = new ProjectOnboardingFixture("go-module");
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        return fixture;
    }

    private static string SourceLine(ProjectOnboardingFixture fixture, FactSource source)
    {
        Assert.NotNull(source.Path);
        Assert.NotNull(source.Line);
        Assert.True(source.Line > 0);
        Assert.False(Path.IsPathRooted(source.Path));
        Assert.DoesNotContain('\\', source.Path);
        return File.ReadAllLines(Path.Combine(fixture.Root, source.Path))[source.Line.Value - 1];
    }

    private static void Write(ProjectOnboardingFixture fixture, string path, string text)
    {
        var destination = Path.Combine(fixture.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, text);
    }
}
