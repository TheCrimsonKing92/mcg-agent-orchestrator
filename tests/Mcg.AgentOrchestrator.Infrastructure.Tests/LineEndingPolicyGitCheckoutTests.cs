using System.Text;

// Parallel-safe: each invocation owns its repository and both linked worktrees.
public sealed class LineEndingPolicyGitCheckoutTests
{
    [Fact]
    public void Policy_AutocrlfTrue_PreservesOldCheckoutAndControlsNewCheckout()
    {
        var policyLines = File.ReadAllLines(Path.Combine(VerifiedRepositoryRoot.Find(), ".gitattributes")).Take(3);
        var temporaryRoot = Directory.CreateTempSubdirectory("mcg-line-ending-policy-").FullName;
        var repository = Path.Combine(temporaryRoot, "repository");
        var existing = Path.Combine(temporaryRoot, "existing");
        var fresh = Path.Combine(temporaryRoot, "fresh");
        Directory.CreateDirectory(repository);
        try
        {
            Git(repository, "init", "--initial-branch=main");
            Git(repository, "config", "core.autocrlf", "true");
            Git(repository, "config", "user.name", "Line ending policy test");
            Git(repository, "config", "user.email", "line-endings@example.invalid");
            Assert.Equal("true", Git(repository, "config", "--get", "core.autocrlf").Trim());
            File.WriteAllBytes(Path.Combine(repository, "sample.cs"), Encoding.UTF8.GetBytes("// first\r\n// second\r\n"));
            File.WriteAllBytes(Path.Combine(repository, "sample.cmd"), Encoding.UTF8.GetBytes("@echo off\r\necho policy\r\n"));
            File.WriteAllBytes(Path.Combine(repository, "sample.bat"), Encoding.UTF8.GetBytes("@echo off\r\necho policy\r\n"));
            Git(repository, "add", ".");
            Git(repository, "commit", "-m", "Seed normalized text");
            var baseline = Git(repository, "rev-parse", "HEAD").Trim();
            Git(repository, "worktree", "add", "--detach", existing, baseline);
            AssertCrlf(Path.Combine(existing, "sample.cs"));
            AssertCrlf(Path.Combine(existing, "sample.cmd"));
            Assert.Empty(Git(existing, "status", "--porcelain"));

            File.WriteAllText(Path.Combine(repository, ".gitattributes"),
                string.Join('\n', policyLines) + "\n");
            Git(repository, "add", ".gitattributes");
            Git(repository, "commit", "-m", "Declare line ending policy");
            var policy = Git(repository, "rev-parse", "HEAD").Trim();
            Git(repository, "worktree", "add", "--detach", fresh, policy);
            var source = File.ReadAllBytes(Path.Combine(fresh, "sample.cs"));
            Assert.DoesNotContain((byte)'\r', source);
            Assert.Equal(2, source.Count(value => value == (byte)'\n'));
            AssertCrlf(Path.Combine(fresh, "sample.cmd"));
            AssertCrlf(Path.Combine(fresh, "sample.bat"));

            Git(existing, "checkout", "--detach", policy);
            AssertCrlf(Path.Combine(existing, "sample.cs"));
            Assert.Empty(Git(existing, "status", "--porcelain"));
            Git(repository, "add", "--renormalize", ".");
            Assert.Empty(Git(repository, "diff", "--cached", "--name-only"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void AssertCrlf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(2, bytes.Count(value => value == (byte)'\n'));
        Assert.Equal(2, bytes.Count(value => value == (byte)'\r'));
        for (var index = 0; index < bytes.Length; index++)
            if (bytes[index] == (byte)'\n')
                Assert.True(index > 0 && bytes[index - 1] == (byte)'\r', $"Bare LF in '{path}' at byte {index}");
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} at '{root}': {result}");
        Assert.False(result.StandardOutputTruncated, $"Truncated git output at '{root}': {result}");
        return result.StandardOutput;
    }
}
