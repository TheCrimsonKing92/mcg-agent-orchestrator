using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using static InfrastructureTestSupport;

// Parallel-safe: all registry, selection, project and data paths belong to this fixture.
public sealed class ProjectRegistryLookupDiagnosticsTests : IDisposable
{
    private readonly string directory = CreateTempDirectory();

    [Theory]
    [InlineData("get")]
    [InlineData("on")]
    [InlineData("off")]
    public void Lookup_NameInOtherRegistry_ReportsQueriedPathWithoutWriting(string surface)
    {
        var other = Registry("other registry");
        var queried = Registry("queried registry with spaces");
        var root = Directory.CreateDirectory(Path.Combine(directory, "private project root")).FullName;
        other.CreateProject("Shared", root);
        queried.CreateProject("unrelated-entry", root);
        queried.SelectProject("unrelated-entry");
        Assert.Equal("Shared", other.GetRequiredProject("Shared").Name);
        Assert.DoesNotContain(queried.ListProjects(), project => project.Name == "Shared");
        Assert.True(Path.IsPathFullyQualified(queried.RegistryPath));
        Assert.Contains(" ", queried.RegistryPath);
        var before = Snapshot();

        var error = Assert.Throws<ArgumentException>(() => Lookup(queried, surface, "  Shared  "));

        AssertDiagnostic(error.Message, queried);
        Assert.DoesNotContain(other.RegistryPath, error.Message);
        Assert.DoesNotContain("unrelated-entry", error.Message);
        Assert.DoesNotContain(root, error.Message);
        Assert.DoesNotContain(queried.DataRootDirectory, error.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal("unrelated-entry", queried.ReadSelectedProjectName());
    }

    [Theory]
    [InlineData("get", false)]
    [InlineData("get", true)]
    [InlineData("on", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("off", true)]
    public void Lookup_AbsentRegistry_DoesNotCreateFilesOrDirectories(
        string surface, bool existingDirectory)
    {
        var registry = Registry("absent registry with spaces");
        if (existingDirectory) Directory.CreateDirectory(registry.RegistryDirectory);
        Assert.False(File.Exists(registry.RegistryPath));
        var before = Snapshot();

        var error = Assert.Throws<ArgumentException>(() => Lookup(registry, surface, " Shared "));

        AssertDiagnostic(error.Message, registry);
        Assert.Equal(before, Snapshot());
        Assert.Null(registry.ReadSelectedProjectName());
        Assert.False(Directory.Exists(registry.DataRootDirectory));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("on")]
    [InlineData("off")]
    public void Lookup_MalformedRegistry_PreservesJsonFailureAndBytes(string surface)
    {
        var registry = Registry("malformed registry");
        Directory.CreateDirectory(registry.RegistryDirectory);
        File.WriteAllText(registry.RegistryPath, "{ malformed JSON");
        var before = Snapshot();

        var error = Assert.Throws<JsonException>(() => Lookup(registry, surface, "Shared"));

        Assert.DoesNotContain("Unknown project", error.Message);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("get")]
    [InlineData("on")]
    [InlineData("off")]
    public void Lookup_UnreadableRegistry_PreservesIoFailureAndBytes(string surface)
    {
        var registry = Registry("unreadable registry");
        Directory.CreateDirectory(registry.RegistryDirectory);
        File.WriteAllText(registry.RegistryPath, "{\"Projects\":[]}");
        var before = Snapshot();
        using (var obstruction = new FileStream(registry.RegistryPath, FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(File.Exists(registry.RegistryPath));
            var error = Assert.Throws<IOException>(() => Lookup(registry, surface, "Shared"));
            Assert.DoesNotContain("Unknown project", error.Message);
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Resolve_ExplicitSelectedAndDefaultProjects_RemainReadOnly()
    {
        var registry = Registry("successful registry");
        var root = Directory.CreateDirectory(Path.Combine(directory, "project root")).FullName;
        registry.CreateProject("Shared", root, "trunk");
        registry.SelectProject("Shared");
        var before = Snapshot();

        Assert.Equal("trunk", registry.GetRequiredProject(" Shared ").IntegrationBranch);
        Assert.Equal(root, registry.ResolveActiveProject(root, "shared").RootDirectory);
        Assert.Equal("Shared", registry.ResolveActiveProject(root, null).Name);
        Assert.Equal("default", registry.ResolveActiveProject(root, "default").Name);
        var absent = Registry("implicit default registry");
        Assert.Equal(root, absent.ResolveActiveProject(root, null).RootDirectory);

        Assert.Equal(before, Snapshot());
    }

    private OrchestratorProjectRegistry Registry(string name) =>
        new(Path.Combine(directory, name), Path.Combine(directory, "private data root"));

    private static void Lookup(OrchestratorProjectRegistry registry, string surface, string name)
    {
        if (surface == "get") _ = registry.GetRequiredProject(name);
        else registry.SetUseLearnedIntegrationBranch(name, surface == "on");
    }

    private static void AssertDiagnostic(string message, OrchestratorProjectRegistry registry)
    {
        Assert.Contains("Unknown project 'Shared'", message);
        Assert.DoesNotContain("  Shared  ", message);
        Assert.Contains($"Registry: '{registry.RegistryPath}'", message);
        Assert.Contains("Check registry/project selection before creating an entry", message);
        Assert.Contains("project create Shared --root <path>", message);
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Directory.Exists(path) ? path + "/" :
            path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
