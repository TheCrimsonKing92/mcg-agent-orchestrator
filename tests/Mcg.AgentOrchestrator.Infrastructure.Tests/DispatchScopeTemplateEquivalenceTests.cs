using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: class-owned template, independent repositories, logs and in-memory kernels per arm.
public sealed class DispatchScopeTemplateEquivalenceTests(WorkerBuildScopeDispatchFixtureTemplate template)
    : IClassFixture<WorkerBuildScopeDispatchFixtureTemplate>
{
    [Fact]
    public void PreparedCopy_IndependentFreshConstruction_HasEquivalentDispatchState()
    {
        using var copy = template.Copy();
        using var fresh = new WorkerBuildScopeDispatchFixture(copy.GoalId, copy.PredecessorId, copy.SuccessorId);

        foreach (var worktree in new[] { false, true })
        {
            Assert.Equal(Git(fresh, worktree, "branch", "--show-current"), Git(copy, worktree, "branch", "--show-current"));
            Assert.Equal(Git(fresh, worktree, "rev-parse", "HEAD^{tree}"), Git(copy, worktree, "rev-parse", "HEAD^{tree}"));
        }
        var registration = NormalizePaths(Git(fresh, false, "worktree", "list", "--porcelain"), fresh);
        Assert.Contains("worktree $fixture/.orchestrator-worktrees/", registration);
        Assert.Equal(registration, NormalizePaths(Git(copy, false, "worktree", "list", "--porcelain"), copy));
        Assert.Equal(NormalizeDispatch(fresh), NormalizeDispatch(copy));
        Assert.Equal(NormalizePaths(JsonSerializer.Serialize(fresh.Predecessor.LastProcess), fresh),
            NormalizePaths(JsonSerializer.Serialize(copy.Predecessor.LastProcess), copy));
        Assert.Single(copy.Predecessor.DispatchHistory);
        Assert.Null(copy.Successor.LastDispatch);
        Assert.Equal(fresh.Predecessor.Status, copy.Predecessor.Status);
        Assert.True(fresh.Receipt().Matches);
        Assert.True(copy.Receipt().Matches);
        Assert.False(fresh.Resolve().FailsRound);
        Assert.False(copy.Resolve().FailsRound);
        Assert.Equal(0, fresh.Builds);
        Assert.Equal(0, copy.Builds);

        fresh.Prepare(fresh.Successor);
        copy.Prepare(copy.Successor);
        Assert.Equal(fresh.Successor.LastDispatch!.BaseCommit, copy.Successor.LastDispatch!.BaseCommit);
        Assert.True(fresh.Receipt().Matches);
        Assert.True(copy.Receipt().Matches);
    }

    [Fact]
    public void Copy_MutationAndDisposal_LeaveOtherCopiesAndTemplateUnchanged()
    {
        using var untouched = template.Copy();
        var templateBefore = FileDigests(template.TemplateMain);
        var copyBefore = FileDigests(untouched.Main);
        string removed;
        using (var mutated = template.Copy())
        {
            Assert.NotEqual(untouched.Main, mutated.Main);
            Assert.NotSame(untouched.Kernel, mutated.Kernel);
            removed = mutated.Main;
            File.WriteAllText(Path.Combine(mutated.Root, "Core", "Core.cs"), "corrupt source");
            File.WriteAllText(Path.Combine(mutated.Main, ".git", "refs", "heads", mutated.IntegrationBranch), new string('a', 40));
            File.Delete(WorkerBuildProjectScope.MetadataPath(mutated.Root));
            File.WriteAllText(mutated.ReceiptPath, "corrupt receipt");
            File.WriteAllText(mutated.Predecessor.LastProcess!.StandardOutputPath, "corrupt output");
            mutated.Kernel.RecordDispatchProviderSessionId(mutated.GoalId, mutated.PredecessorId, "copy-only-session");
        }
        Assert.False(Directory.Exists(removed));
        Assert.Equal(templateBefore, FileDigests(template.TemplateMain));
        Assert.Equal(copyBefore, FileDigests(untouched.Main));
        Assert.Null(untouched.Predecessor.LastDispatch!.ProviderSessionId);
        using var later = template.Copy();
        Assert.Null(later.Predecessor.LastDispatch!.ProviderSessionId);
        Assert.True(later.Receipt().Matches);
        Assert.True(untouched.Receipt().Matches);
    }

    private static string NormalizeDispatch(WorkerBuildScopeDispatchFixture fixture)
    {
        var dispatch = fixture.Predecessor.LastDispatch!;
        var identity = dispatch.WorkerBuildScope!;
        var retained = Convert.FromBase64String(identity.RetainedMetadataBase64!);
        Assert.Equal(retained, File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root)));
        Assert.Equal(identity.MetadataDigest, Convert.ToHexStringLower(SHA256.HashData(retained)));
        Assert.Equal(dispatch.DispatchedAt, identity.CapturedDispatchAt);
        var metadata = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(retained)!;
        // Only roots and the independently generated nonce differ. Keep every authority field.
        var normalized = metadata with
        {
            WorktreeRoot = NormalizePaths(metadata.WorktreeRoot, fixture),
            GitCommonDirectory = NormalizePaths(metadata.GitCommonDirectory, fixture),
            DispatchIdentity = "$nonce"
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized);
        return JsonSerializer.Serialize(dispatch with
        {
            WorkingDirectory = NormalizePaths(dispatch.WorkingDirectory, fixture),
            WorkerBuildScope = identity with
            {
                MetadataDigest = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                RetainedMetadataBase64 = Convert.ToBase64String(bytes)
            }
        });
    }

    private static string NormalizePaths(string value, WorkerBuildScopeDispatchFixture fixture) =>
        value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace('\\', '/')
            .Replace(fixture.Main.Replace('\\', '/'), "$fixture", StringComparison.OrdinalIgnoreCase);

    private static string Git(WorkerBuildScopeDispatchFixture fixture, bool worktree, params string[] arguments) =>
        WorkerBuildScopeDispatchFixture.Git(worktree ? fixture.Root : fixture.Main, arguments);

    private static string[] FileDigests(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))
        .Order(StringComparer.Ordinal).ToArray();
}
