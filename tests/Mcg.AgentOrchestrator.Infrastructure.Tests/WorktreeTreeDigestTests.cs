using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorktreeTreeDigestTests : WorkerDispatchTestSupport
{
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
