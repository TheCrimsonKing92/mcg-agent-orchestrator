using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class WorkerProfileTests
{
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_default_contains_local_subscription_bridges")]
    public void WorkerProfileCatalogDefaultContainsLocalSubscriptionBridges()
{
    var catalog = WorkerProfileCatalog.Default();
    var profile = catalog.GetRequired("LOCAL-ECHO");
    var codex = catalog.GetRequired("codex-cli");
    var claude = catalog.GetRequired("claude-cli");

    Assert.Equal("local-echo", profile.Name);
    Assert.Contains(profile.CommandTemplate, text => text.Contains("{promptPath}", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("--skip-git-repo-check", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
    Assert.Contains(codex.CommandTemplate, text => text.Contains("Get-Content -Raw {promptPath}", StringComparison.Ordinal));
    Assert.Contains(claude.CommandTemplate, text => text.Contains("claude -p", StringComparison.Ordinal));
    Assert.Contains(claude.CommandTemplate, text => text.Contains("Get-Content -Raw {promptPath}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_upsert_replaces_existing_profile")]
    public void WorkerProfileCatalogUpsertReplacesExistingProfile()
{
    var catalog = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("local-echo", "Get-Content {promptPath}"));

    Assert.Equal(3, catalog.Profiles.Count);
    Assert.Equal("Get-Content {promptPath}", catalog.GetRequired("local-echo").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_merge_upserts_imported_profiles")]
    public void WorkerProfileCatalogMergeUpsertsImportedProfiles()
{
    var current = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("codex", "codex exec {promptPath}"));
    var imported = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex", "codex exec --full-auto {promptPath}"),
        new WorkerProfile("claude", "claude --file {promptPath}")
    ]);

    var merged = current.Merge(imported);

    Assert.Equal(5, merged.Profiles.Count);
    Assert.Equal("codex exec --full-auto {promptPath}", merged.GetRequired("codex").CommandTemplate);
    Assert.Equal("claude --file {promptPath}", merged.GetRequired("CLAUDE").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_roundtrips_profiles")]
    public void WorkerProfileStoreRoundtripsProfiles()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var catalog = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("agent", "agent-cli --file {promptPath}"));

    WorkerProfileStore.Save(path, catalog);
    var restored = WorkerProfileStore.Load(path);

    Assert.Equal("agent-cli --file {promptPath}", restored.GetRequired("agent").CommandTemplate);

    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = root;
        WorkerProfileStore.Save("workers-local.json", catalog);
        Assert.True(File.Exists(Path.Combine(root, "workers-local.json")));
    }
    finally
    {
        Environment.CurrentDirectory = previousDirectory;
    }
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_merges_saved_profiles_with_new_defaults")]
    public void WorkerProfileStoreLoadMergesSavedProfilesWithNewDefaults()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = new WorkerProfileCatalog(
    [
        new WorkerProfile("local-echo", "Get-Content {promptPath}"),
        new WorkerProfile("custom-agent", "custom-agent --prompt {promptPath}")
    ]);

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Equal("Get-Content {promptPath}", restored.GetRequired("local-echo").CommandTemplate);
    Assert.Equal("custom-agent --prompt {promptPath}", restored.GetRequired("custom-agent").CommandTemplate);
    Assert.Contains(restored.GetRequired("codex-cli").CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(restored.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(restored.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.Contains(restored.GetRequired("claude-cli").CommandTemplate, text => text.Contains("claude -p", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_required_rejects_missing_or_empty_files")]
    public void WorkerProfileStoreLoadRequiredRejectsMissingOrEmptyFiles()
{
    var root = CreateTempDirectory();
    var missing = Path.Combine(root, "missing.json");
    var empty = Path.Combine(root, "empty.json");
    File.WriteAllText(empty, "{\"profiles\":[]}");

    Assert.Throws<FileNotFoundException>(() => WorkerProfileStore.LoadRequired(missing));
    Assert.Throws<InvalidDataException>(() => WorkerProfileStore.LoadRequired(empty));
}
}
