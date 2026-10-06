using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns a unique temporary directory and workers file.
public sealed class LunaLaneLegacyProfileFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(LunaLaneLegacyProfileFileTests), Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_LegacyOnlyFile_PreservesTemplateAndEmitsLuna(bool required)
    {
        var path = WriteProfiles([Profile("codex-spark", "custom")]);

        var catalog = required ? WorkerProfileStore.LoadRequired(path) : WorkerProfileStore.Load(path);
        var profile = catalog.GetRequired("codex-luna");

        Assert.Equal("codex-luna", profile.Name);
        Assert.Contains("marker=custom", profile.CommandTemplate, StringComparison.Ordinal);
        Assert.Same(profile, catalog.GetRequired("codex-spark"));
        Assert.All(catalog.Profiles, entry => Assert.DoesNotContain("spark", entry.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("codex-luna", SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(catalog).ProfileName);

        WorkerProfileStore.Save(path, catalog);
        var saved = File.ReadAllText(path);
        Assert.Contains("codex-luna", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("codex-spark", saved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Load_BothNames_CurrentEntryWinsInEitherOrder(bool required, bool legacyFirst)
    {
        var legacy = Profile("codex-spark", "legacy");
        var current = Profile("codex-luna", "current");
        var path = WriteProfiles(legacyFirst ? [legacy, current] : [current, legacy]);

        var catalog = required ? WorkerProfileStore.LoadRequired(path) : WorkerProfileStore.Load(path);

        var profile = catalog.GetRequired("codex-luna");
        Assert.Equal(current.CommandTemplate, profile.CommandTemplate);
        Assert.Single(catalog.Profiles.Where(entry => entry.Name == "codex-luna"));
        Assert.All(catalog.Profiles, entry => Assert.DoesNotContain("spark", entry.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static WorkerProfile Profile(string name, string marker) =>
        WorkerProfileCatalog.Default().GetRequired("codex-luna") with
        {
            Name = name,
            CommandTemplate = WorkerProfileCatalog.Default().GetRequired("codex-luna").CommandTemplate + " -c marker=" + marker
        };

    private string WriteProfiles(IReadOnlyList<WorkerProfile> profiles)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "workers.json");
        // Write the input directly: Save of a normalized catalog would conceal the legacy fixture.
        File.WriteAllText(path, JsonSerializer.Serialize(new WorkerProfileCatalog(profiles)));
        Assert.Contains("codex-spark", File.ReadAllText(path), StringComparison.Ordinal);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
