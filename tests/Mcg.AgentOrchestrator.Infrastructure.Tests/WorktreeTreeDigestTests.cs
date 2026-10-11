using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorktreeTreeDigestTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void NonRepositoryReportsGitStepAndReceiptDigestDetail()
    {
        var root = Path.Combine(Path.GetTempPath(), $"digest-non-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".git")));
            Directory.CreateDirectory(Path.Combine(root, "App"));
            File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "App", "Changed.cs"), "public class Changed { }");
            Xunit.Assert.False(WorktreeTreeDigest.TryCompute(root, out var digest, out var reason, out var failure));
            Xunit.Assert.Equal("", digest);
            Xunit.Assert.Equal("digest-unavailable", reason);
            Xunit.Assert.NotNull(failure);
            Xunit.Assert.Equal("rev-parse", failure.Step);
            Xunit.Assert.NotEqual(0, failure.ExitCode);
            Xunit.Assert.False(failure.TimedOut);
            Xunit.Assert.False(string.IsNullOrWhiteSpace(failure.StandardErrorFirstLine));
            Xunit.Assert.Contains("step=rev-parse", failure.Describe());

            var path = Path.Combine(root, WorkerBuildReceipt.FileName);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                treeDigest = "wbc1:git-tree:" + new string('a', 40),
                digestAlgorithm = WorktreeTreeDigest.Algorithm,
                buildOutcome = "success",
                generatedAtUtc = "2026-10-11T12:00:00Z",
                worktreeRoot = root,
                configuration = "Debug",
                projects = Array.Empty<string>()
            }));
            var verdict = WorkerBuildReceipt.Evaluate(path, root);
            Xunit.Assert.False(verdict.Matches);
            Xunit.Assert.Equal("digest-unavailable", verdict.Reason);
            Xunit.Assert.NotNull(verdict.Detail);
            Xunit.Assert.Contains("step=rev-parse", verdict.Detail);
            var resolution = OrchestratorBuildEvidenceCheck.Resolve(root, Mcg.AgentOrchestrator.Core.AgentRole.Developer,
                ["App/Changed.cs"], [], false, () => verdict, _ => new(true, 0, "PASS build: 0 errors"));
            Xunit.Assert.Contains("worker_build_receipt=digest-unavailable; digest_detail=" + verdict.Detail,
                resolution.Diagnostic);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void DigestChangesForEditsAddsAndDeletesWithoutTouchingRealIndex()
    {
        var root = CreateSeededDispatchRepository();
        var index = ReadGit(root, ["ls-files", "--stage"]);
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(root, out var initial, out var error), error);
        var path = Path.Combine(root, "new-file.txt");
        File.WriteAllText(path, "one");
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(root, out var added, out error), error);
        File.WriteAllText(path, "two");
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(root, out var edited, out error), error);
        File.Delete(path);
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(root, out var restored, out error), error);

        Xunit.Assert.NotEqual(initial, added);
        Xunit.Assert.NotEqual(added, edited);
        Xunit.Assert.Equal(initial, restored);
        Xunit.Assert.Equal(index, ReadGit(root, ["ls-files", "--stage"]));
    }
}
