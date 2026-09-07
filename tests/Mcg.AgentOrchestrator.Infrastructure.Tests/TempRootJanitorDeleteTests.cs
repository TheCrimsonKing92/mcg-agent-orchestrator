using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TempRootJanitorDeleteTests
{
    [Fact]
    public void HeldChildFailureRetainsMessageHResultAndRemainingPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"janitor-held-child-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "repository", ".git", "held.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "held");

        TempRootDeleteOutcome outcome;
        using (File.Open(child, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = AssemblyTempRedirect.DeleteOwnedTree(root, _ => { });
        }

        try
        {
            Assert.Equal(TempRootDeleteStatus.Failed, outcome.Status);
            Assert.Equal(nameof(IOException), outcome.ExceptionType);
            Assert.False(string.IsNullOrWhiteSpace(outcome.ExceptionMessage));
            Assert.NotNull(outcome.ExceptionHResult);
            Assert.Equal(child, outcome.FailurePath, ignoreCase: true);
            Assert.Equal(6, outcome.DeleteAttempts);

            var diagnostic = AssemblyTempRedirect.FormatCleanupDiagnostic(
                AssemblyTempRootCleanupOwner.AssemblyFixture,
                outcome,
                elapsedMilliseconds: 1550);
            Assert.Contains("delete_attempts=6", diagnostic, StringComparison.Ordinal);
            Assert.Contains("exception_hresult=0x", diagnostic, StringComparison.Ordinal);
            Assert.Contains($"failure_path=\"{child}\"", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exception_message=\"", diagnostic, StringComparison.Ordinal);

            var exception = Assert.Throws<InvalidOperationException>(
                () => AssemblyTempRootCleanupFixture.EnsureSuccessful(outcome));
            Assert.Contains(outcome.ExceptionMessage!, exception.Message, StringComparison.Ordinal);
            Assert.Contains($"0x{unchecked((uint)outcome.ExceptionHResult!.Value):X8}", exception.Message, StringComparison.Ordinal);
            Assert.Contains(child, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(root);
        }
    }

    [Fact]
    public void TransientHeldChildIsDeletedAfterTheHandleReleases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"janitor-transient-child-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "repository", "transient.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "held briefly");
        using var held = File.Open(child, FileMode.Open, FileAccess.Read, FileShare.None);
        var released = false;

        var outcome = AssemblyTempRedirect.DeleteOwnedTree(root, _ =>
        {
            held.Dispose();
            released = true;
        });

        Assert.True(released, "The retry branch did not invoke the controlled handle-release seam.");
        Assert.Equal(TempRootDeleteStatus.Deleted, outcome.Status);
        Assert.Equal(2, outcome.DeleteAttempts);
        Assert.False(Directory.Exists(root));
    }
}
