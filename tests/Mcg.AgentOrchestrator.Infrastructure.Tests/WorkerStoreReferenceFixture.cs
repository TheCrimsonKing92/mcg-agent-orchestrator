using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

internal sealed class WorkerStoreReferenceFixture : IDisposable
{
    internal const string EventGoalId = "0d8fe45d519133850d8fe45d51913385";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "store-reference-tests", Guid.NewGuid().ToString("N"));
    internal string StoreRoot => Path.Combine(Root, ".orchestrator");
    internal string WorkingDirectory => Path.Combine(Root, "repo");
    internal IClock Clock { get; } = new FixedClock();

    internal void Advance() => ((FixedClock)Clock).UtcNow += TimeSpan.FromMinutes(1);

    internal WorkerStoreReferenceFixture()
    {
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(WorkingDirectory);
    }

    internal string WriteSource(string relativePath, string content)
    {
        var path = Path.Combine(StoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CreateGoal(string objective)
    {
        var kernel = new AgentOrchestratorKernel(Clock);
        var task = new TaskSpec(TaskId.New(), "Implement scoped behavior.", AgentRole.Developer);
        return (kernel, kernel.CreateGoal(objective, [task]), task);
    }

    internal async Task<AgentOrchestratorKernel> Reload(AgentOrchestratorKernel kernel)
    {
        var path = Path.Combine(StoreRoot, "state.db");
        StateDbMigrations.EnsureUpToDate(path);
        await new SqliteOrchestratorStateRepository(path).SaveAsync(kernel);
        var loaded = await new SqliteOrchestratorStateRepository(path).LoadAsync();
        Assert.True(File.Exists(path));
        return AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), Clock);
    }

    internal string Context(Goal goal) => Path.Combine(WorkingDirectory, ".orchestrator-context", goal.Id.Value);

    internal WorkerProfileDispatchResult Prepare(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(Root, "prompts"), WorkingDirectory, Clock.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            allowPendingRecordedDispatchRefresh: true,
            citedPriorEvidenceResolver: CitedPriorEvidenceResolver.ForWorkspace(Path.Combine(StoreRoot, "state.db"), StoreRoot));

    internal HumanInputRequest Answer(AgentOrchestratorKernel kernel, Goal goal, TaskId? taskId,
        string name, HumanWaitKind kind = HumanWaitKind.SpecClarification)
    {
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, taskId, $"Evidence for {name}?",
            kind: kind, isDismissible: true).Request;
        kernel.SubmitHumanInput(request.Id, Reference(name));
        return request;
    }

    internal static string Reference(string name, string? source = null) =>
        $"store-ref: {name} = operator-evidence:operator-evidence/{source ?? name}.md";

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal void AssertDelivered(WorkerProfileDispatchResult prepared, Goal goal, string name,
        string source, string excerpt)
    {
        var context = Context(goal);
        var relative = $"store-refs/{name}.md";
        var path = Path.Combine(context, relative);
        Assert.True(File.Exists(path), $"Missing delivered artifact {relative}.");
        var content = File.ReadAllText(path);
        Assert.Equal(excerpt, Excerpt(content));
        Assert.Contains($"Source sha256: {Hash(source)}", content);
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "artifact-registry.json")));
        var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("path").GetString() == relative);
        Assert.True(entry.GetProperty("hashVerified").GetBoolean());
        Assert.Equal(Hash(path), entry.GetProperty("sha256").GetString());
        Assert.Contains($"- {relative}:", File.ReadAllText(Path.Combine(context, "manifest.md")));
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "context-package.json")));
        Assert.Contains(package.RootElement.GetProperty("storeReferences").EnumerateArray(), item => item.GetString() == relative);
        var section = Assert.Single(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == $"context/{relative}");
        Assert.Equal(Hash(path), section.ContentHash);
        Assert.Equal(ContextDeliveryMode.InlineFull, section.DeliveryMode);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains($"identity=context/{relative}", prompt);
        Assert.Equal(content, PromptArtifact(prepared, $"context/{relative}"));
        using var manifest = JsonDocument.Parse(PromptArtifact(prepared, "context/manifest.v1.json"));
        var manifestEntry = Assert.Single(manifest.RootElement.GetProperty("Artifacts").EnumerateArray(),
            item => item.GetProperty("LogicalIdentity").GetString() == $"context/{relative}");
        Assert.Equal(Hash(path), manifestEntry.GetProperty("Sha256").GetString());
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(context, "packages", prepared.Task.Id.Value, relative)));
    }

    internal void AssertAbsent(WorkerProfileDispatchResult prepared, Goal goal, string name)
    {
        var relative = $"store-refs/{name}.md";
        Assert.False(File.Exists(Path.Combine(Context(goal), relative)));
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context(goal), "artifact-registry.json")));
        Assert.DoesNotContain(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("path").GetString() == relative);
        Assert.DoesNotContain($"- {relative}:", File.ReadAllText(Path.Combine(Context(goal), "manifest.md")));
        Assert.DoesNotContain(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == $"context/{relative}");
        Assert.DoesNotContain($"identity=context/{relative}", File.ReadAllText(prepared.PromptPath));
    }

    internal string[] Diagnostics(Goal goal) => File.ReadAllLines(Path.Combine(Context(goal), "manifest.md"))
        .Where(line => line.StartsWith("STORE_REF_UNRESOLVED", StringComparison.Ordinal)).ToArray();

    internal static string PromptArtifact(WorkerProfileDispatchResult prepared, string identity)
    {
        var lines = File.ReadAllLines(prepared.PromptPath);
        var index = Array.FindIndex(lines, line => line.StartsWith($"INLINE FULL: identity={identity};", StringComparison.Ordinal));
        Assert.True(index >= 0, $"No final prompt artifact for {identity}.");
        return JsonSerializer.Deserialize<string>(lines.Skip(index + 1).First(line => !string.IsNullOrWhiteSpace(line)))!;
    }

    internal string Capture(Goal goal)
    {
        var source = Context(goal);
        var destination = Path.Combine(Root, "captures", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    internal static string[] Inventory(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(directory, path)}:{Hash(path)}")
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal string WriteContext(string objective, bool includeStore = true)
    {
        var (_, goal, task) = CreateGoal(objective);
        return new WorkerArtifactWriter().Write(goal, task, WorkingDirectory,
            orchestratorStoreRoot: includeStore ? StoreRoot : null, clock: Clock);
    }

    internal StoreReferenceResolution Resolve(string line)
    {
        var reference = Xunit.Assert.Single(WorkerStoreReferenceResolver.Parse([line]));
        return WorkerStoreReferenceResolver.Resolve(reference, StoreRoot, Clock, out _);
    }

    internal static string Excerpt(string content)
    {
        var marker = WorkerStoreReferenceResolver.UntrustedBanner + Environment.NewLine + Environment.NewLine;
        var index = content.IndexOf(marker, StringComparison.Ordinal);
        Xunit.Assert.True(index >= 0, "Missing untrusted-data banner and excerpt separator.");
        return content[(index + marker.Length)..];
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 4, 2, 0, 0, TimeSpan.Zero);
    }
}
