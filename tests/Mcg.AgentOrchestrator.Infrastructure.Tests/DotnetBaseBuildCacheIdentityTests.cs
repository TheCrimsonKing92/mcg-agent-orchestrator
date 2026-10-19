using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected evaluation runner, explicit environment and independently owned files.
public sealed class DotnetBaseBuildCacheIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cache-identity", Guid.NewGuid().ToString("N"));
    private int _queries;

    [Fact]
    public async Task EvaluationQuery_OmitsNewestImportMarker()
    {
        Seed();
        var commands = new List<string[]>();
        Assert.NotNull(await Resolve(commands: commands));
        Assert.Equal("-getProperty:Configuration,TargetFramework,TargetFrameworks,RuntimeIdentifier,NETCoreSdkVersion,MSBuildSDKsPath,ProjectAssetsFile",
            Assert.Single(commands.SelectMany(args => args).Where(arg => arg.StartsWith("-getProperty:", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Components_CarryDigestsNotEnvironmentValues()
    {
        Seed();
        var baseline = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve());
        var changed = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve(variable: "value-must-not-appear"));
        Assert.DoesNotContain("value-must-not-appear", changed.Components);
        Assert.Equal("environment", DotnetBaseBuildCacheMissDetail.DifferingComponents(baseline.Components, changed.Components));
        var components = changed.Components.Split(';');
        Assert.Equal(components.Order(StringComparer.Ordinal), components);
        Assert.All(components, component => Assert.Matches("^[^=]+=[0-9a-f]{16}$", component));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("project")]
    [InlineData("analyzer")]
    [InlineData("options")]
    [InlineData("sdk")]
    [InlineData("pack")]
    [InlineData("package")]
    [InlineData("import")]
    [InlineData("environment")]
    [InlineData("configuration")]
    public async Task ChangedBuildInput_SelectsADifferentIdentity(string change)
    {
        Seed();
        var before = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve());
        var after = change switch
        {
            "environment" => await Resolve(variable: "changed"),
            "configuration" => await Resolve(configuration: "Release"),
            _ => await ChangeFile(change)
        };
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.True(_queries >= 2);
    }

    [Fact]
    public async Task SameInputsInNewOutputSlot_KeepIdentityDespiteItemAccessTimes()
    {
        Seed();
        var before = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve());
        var after = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve(slot: "second"));
        Assert.Equal(before, after);
        Assert.Equal(2, _queries);
    }

    [Fact]
    public async Task OptionalGlobalAnalyzerConfig_AbsenceIsStableAndCreationOrDeletionInvalidates()
    {
        Seed();
        var absent = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve());
        Assert.Equal(absent, await Resolve(slot: "second"));
        var path = Path.Combine(_root, ".globalconfig");
        File.WriteAllText(path, "is_global = true\ndotnet_diagnostic.CS1030.severity = error");
        var present = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve());
        Assert.NotEqual(absent, present);
        File.Delete(path);
        Assert.Equal(absent, await Resolve());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("malformed")]
    [InlineData("missing-source")]
    [InlineData("missing-analyzer")]
    [InlineData("missing-reference")]
    [InlineData("multi-target")]
    [InlineData("timed-out")]
    [InlineData("capture-limited")]
    [InlineData("incomplete-items")]
    public async Task UnresolvedInputs_DisableReuse(string failure)
    {
        Seed();
        Assert.Null(await Resolve(failure: failure));
    }

    [Theory]
    [InlineData("--no-restore")]
    [InlineData("--arch")]
    public void UnsupportedCommandShape_DisablesReuse(string argument) =>
        Assert.Null(DotnetBaseBuildCacheIdentityResolver.EvaluationArguments(["dotnet", "build", "fixture.csproj", argument], []));

    private Task<DotnetBaseBuildCacheIdentity?> ChangeFile(string change)
    {
        File.AppendAllText(Input(change), "changed");
        return Resolve();
    }

    private void Seed()
    {
        foreach (var kind in new[] { "source", "project", "analyzer", "options", "sdk", "pack", "package", "import" })
        {
            var path = Input(kind);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, kind);
        }
    }

    private string Input(string kind) => Path.Combine(_root, kind switch
    {
        "source" => "src/Fixture/source.cs", "project" => "src/Fixture/Fixture.csproj",
        "analyzer" => "packages/example/1.0/analyzer.dll", "options" => "src/Fixture/.editorconfig",
        "sdk" => "dotnet/sdk/10.0/compiler.dll", "pack" => "dotnet/packs/framework/ref.dll",
        "package" => "packages/example/1.0/tasks/bin/task.dll", _ => "Directory.Build.targets"
    });

    private Task<DotnetBaseBuildCacheIdentity?> Resolve(string configuration = "Debug", string variable = "fixed",
        string slot = "first", string? failure = null, List<string[]>? commands = null)
    {
        var owned = Path.Combine(_root, "owned", slot);
        var artifacts = Path.Combine(owned, "artifacts");
        var assetsPath = Path.Combine(artifacts, "obj/Fixture/project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
        File.WriteAllText(assetsPath, JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [Path.Combine(_root, "packages")] = new { } },
            libraries = new Dictionary<string, object> { ["example/1.0"] = new { type = "package", path = "example/1.0" } }
        }));
        var environment = new DotnetBuildEnvironment("fixture", owned, artifacts, Path.Combine(owned, "lock"),
            ["--artifacts-path", artifacts], "fixture");
        return DotnetBaseBuildCacheIdentityResolver.ResolveAsync(_root, ["src/Fixture/Fixture.csproj"],
            ["dotnet", "build", "Fixture.csproj", "-c", configuration], environment, args =>
            {
                commands?.Add(args);
                if (failure == "failed") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "failed"));
                if (failure == "timed-out") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "", TimedOut: true));
                if (failure == "capture-limited") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "", CaptureLimited: true));
                var preprocess = args.SingleOrDefault(arg => arg.StartsWith("-preprocess:", StringComparison.Ordinal));
                if (preprocess is not null) File.WriteAllText(preprocess[12..], File.ReadAllText(Input("import")));
                if (!args.Any(arg => arg.StartsWith("-getProperty:", StringComparison.Ordinal)))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                _queries++;
                var items = args.Single(arg => arg.StartsWith("-getItem:", StringComparison.Ordinal))[9..].Split(',')
                    .ToDictionary(name => name, _ => (object)Array.Empty<object>());
                items["Compile"] = new[] { new { FullPath = failure == "missing-source" ? "missing" : Input("source"), AccessedTime = _queries.ToString() } };
                items["Analyzer"] = new[] { new { FullPath = failure == "missing-analyzer" ? "missing" : Input("analyzer") } };
                items["GlobalAnalyzerConfigFiles"] = new[] { new { FullPath = Path.Combine(_root, ".globalconfig") } };
                if (failure == "missing-reference") items["Reference"] = new[] { new { FullPath = "missing" } };
                if (failure == "incomplete-items") items.Remove("AdditionalFiles");
                var snapshot = new
                {
                    Properties = new { Configuration = configuration, TargetFramework = "net10.0",
                        TargetFrameworks = failure == "multi-target" ? "net9.0;net10.0" : "", RuntimeIdentifier = "",
                        NETCoreSdkVersion = "10.0", MSBuildSDKsPath = Path.Combine(_root, "dotnet/sdk/10.0/Sdks"),
                        ProjectAssetsFile = assetsPath, MSBuildAllProjects = Input("import") },
                    Items = items
                };
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                    failure == "malformed" ? "{" : JsonSerializer.Serialize(snapshot)));
            }, new Dictionary<string, string> { ["build-variable"] = variable });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
