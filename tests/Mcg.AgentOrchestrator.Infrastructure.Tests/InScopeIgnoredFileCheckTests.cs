using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fact owns its temporary repository and injected callbacks.
public sealed class InScopeIgnoredFileCheckTests
{
    internal const string NewFile = "cmd/schemadrift/delimiter_flag_test.go";
    internal const string Objective = """
        Reject invalid delimiters.

        ## Scope
        - `src/Feature.cs`
        - `cmd/schemadrift/main.go`
        - `cmd/schemadrift/delimiter_flag_test.go`, new
        Out of scope: `cmd/schemadrift/notes_test.go`
        """;
    private static readonly DateTimeOffset DispatchTime = DateTimeOffset.Parse("2026-06-02T12:00:00Z");

    [Xunit.Fact]
    public void Evaluate_RecentIgnoredScopeFile_ReportsRuleWithoutStaging()
    {
        using var repo = new Repository();
        repo.Write(NewFile);
        Assert.Contains(NewFile, repo.Git("ls-files", "--others", "--ignored", "--exclude-standard"));
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(repo.Root, NewFile)) >= DispatchTime.UtcDateTime);

        var result = repo.Evaluate();

        Assert.True(result.FailsRound);
        Assert.Contains(NewFile + " (.gitignore:1:schemadrift)", result.Diagnostic);
        Assert.Contains("@@MCG_ORCHESTRATOR_DIAGNOSTIC_CODE@@ in-scope-file-ignored", result.Diagnostic);
        Assert.Empty(repo.Git("diff", "--cached", "--name-only"));
        Assert.Empty(repo.Git("ls-files", "--", NewFile));
    }

    [Xunit.Fact]
    public void Evaluate_OutOfScopeAndUnnamedFiles_ReturnsEmpty()
    {
        using var repo = new Repository();
        repo.Write("cmd/schemadrift/notes_test.go");
        repo.Write("cmd/schemadrift/scratch.go");
        Assert.Contains("notes_test.go", repo.Git("ls-files", "--others", "--ignored", "--exclude-standard"));
        AssertEmpty(repo.Evaluate());
    }

    [Xunit.Fact]
    public void Evaluate_TrackedScopeFileModified_ReturnsEmpty()
    {
        using var repo = new Repository();
        repo.Write("cmd/schemadrift/main.go");
        Assert.Contains("cmd/schemadrift/main.go", repo.Git("diff", "--name-only"));
        AssertEmpty(repo.Evaluate());
    }

    [Xunit.Fact]
    public void Evaluate_IgnoredScopeFilePredatesDispatch_ReturnsEmpty()
    {
        using var repo = new Repository();
        repo.Write(NewFile);
        File.SetLastWriteTimeUtc(Path.Combine(repo.Root, NewFile), DispatchTime.AddMinutes(-1).UtcDateTime);
        Assert.Contains(NewFile, repo.Git("ls-files", "--others", "--ignored", "--exclude-standard"));
        AssertEmpty(repo.Evaluate());
    }

    [Xunit.Theory]
    [Xunit.InlineData("cmd/*/*_test.go")]
    [Xunit.InlineData("No file paths declared.")]
    [Xunit.InlineData("../cmd/test.go /cmd/test.go C:/cmd/test.go cmd/../test.go")]
    [Xunit.InlineData(".orchestrator-context/goal/test.cs src/obj/Generated.cs")]
    [Xunit.InlineData("cmd/folder/ cmd/.hidden cmd/noextension")]
    public void Evaluate_NoLiteralScopeFiles_DoesNotStartGit(string scope)
    {
        using var repo = new Repository();
        repo.Write(NewFile);
        var calls = 0;
        var result = InScopeIgnoredFileCheck.Evaluate(repo.Root, [scope], DispatchTime,
            (_, _) => { calls++; return new(128, "", "unexpected git"); }, _ => DispatchTime);

        AssertEmpty(result);
        Assert.Equal(0, calls);
    }

    [Xunit.Fact]
    public void Evaluate_DirectoryWithExtension_DoesNotStartGit()
    {
        using var repo = new Repository();
        Directory.CreateDirectory(Path.Combine(repo.Root, "cmd", "folder.cs"));
        AssertEmpty(InScopeIgnoredFileCheck.Evaluate(repo.Root, ["cmd/folder.cs"], DispatchTime,
            (_, _) => throw new InvalidOperationException("Directory started git"), _ => DispatchTime));
    }

    [Xunit.Fact]
    public void Evaluate_MissingWorktree_DoesNotStartGit()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));
        AssertEmpty(InScopeIgnoredFileCheck.Evaluate(missing, [NewFile], DispatchTime,
            (_, _) => throw new InvalidOperationException("Missing worktree started git"), _ => DispatchTime));
    }

    [Xunit.Theory]
    [Xunit.InlineData("ls-files", 128, true, false)]
    [Xunit.InlineData("ls-files", 1, false, false)]
    [Xunit.InlineData("ls-files", -1, true, false)]
    [Xunit.InlineData("ls-files", 0, true, true)]
    [Xunit.InlineData("check-ignore", 128, true, false)]
    [Xunit.InlineData("check-ignore", 1, false, false)]
    [Xunit.InlineData("check-ignore", -1, true, false)]
    [Xunit.InlineData("check-ignore", 0, true, true)]
    public void Evaluate_GitUnavailable_FailsOpen(
        string operation, int exitCode, bool started, bool drainTimedOut)
    {
        using var repo = new Repository();
        var calls = new List<string>();
        var result = InScopeIgnoredFileCheck.Evaluate(repo.Root, [NewFile], DispatchTime,
            (_, args) =>
            {
                calls.Add(args[0]);
                return args[0] == operation ? new(exitCode, "", "fatal", drainTimedOut, started)
                    : new(0, NewFile + '\0', "");
            }, _ => DispatchTime);

        Assert.False(result.FailsRound);
        Assert.Equal($"In-scope ignored-file check unavailable: operation={operation}; exit_code={exitCode}.", result.Diagnostic);
        Assert.DoesNotContain(DispatchFailureDiagnosticMarker.InScopeFileIgnored, result.Diagnostic);
        Assert.Equal(operation == "ls-files" ? ["ls-files"] : new[] { "ls-files", "check-ignore" }, calls);
    }

    [Xunit.Fact]
    public void Evaluate_SectionsAndQuotedPaths_UsesLiteralIndependentScopes()
    {
        using var repo = new Repository();
        const string quoted = "cmd/my tests/file.go";
        var calls = new List<string[]>();
        var result = InScopeIgnoredFileCheck.Evaluate(repo.Root,
            ["cmd/outside.go\n## sCoPe\n`" + quoted + "`\n## Next\ncmd/after.go", NewFile], DispatchTime,
            (_, args) =>
            {
                calls.Add(args);
                return args[0] == "ls-files" ? new(0, quoted + '\0' + NewFile + '\0' + "cmd/outside.go\0", "")
                    : new(0, $".gitignore\0{1}\0schemadrift\0{NewFile}\0", "");
            }, _ => DispatchTime.ToOffset(TimeSpan.FromHours(3)));

        Assert.True(result.FailsRound);
        Assert.Equal(new[] { "ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--",
            ":(literal)" + quoted, ":(literal)" + NewFile }, calls[0]);
        Assert.Equal(new[] { "check-ignore", "-v", "-z", "--", quoted, NewFile }, calls[1]);
        Assert.Contains(quoted + " (rule-unavailable)", result.Diagnostic);
        Assert.DoesNotContain("outside.go", result.Diagnostic);
        Assert.DoesNotContain("after.go", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Evaluate_ManyFiles_OrdersDeduplicatesAndBoundsDiagnostic()
    {
        using var repo = new Repository();
        var paths = Enumerable.Range(0, 22).Select(index => $"cmd/file{index:D2}.go").ToArray();
        var result = InScopeIgnoredFileCheck.Evaluate(repo.Root,
            [string.Join(' ', paths.Reverse().Concat(paths))], DispatchTime,
            (_, args) => args[0] == "ls-files" ? new(0, string.Join('\0', paths.Concat(paths)) + '\0', "")
                : new(0, $"rules\nfile\0{2}\0pattern\rtext\0{paths[0]}\0", ""), _ => DispatchTime);

        Assert.True(result.FailsRound);
        Assert.Contains("cmd/file00.go (rules\\nfile:2:pattern\\rtext); cmd/file01.go", result.Diagnostic);
        Assert.Contains("cmd/file19.go (rule-unavailable); +2 more.", result.Diagnostic);
        Assert.DoesNotContain("cmd/file20.go", result.Diagnostic);
        Assert.Equal(2, result.Diagnostic.ReplaceLineEndings("\n").Split('\n').Length);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Evaluate_TimestampUnavailable_SkipsFile(bool throws)
    {
        using var repo = new Repository();
        AssertEmpty(InScopeIgnoredFileCheck.Evaluate(repo.Root, [NewFile], DispatchTime,
            (_, args) => args[0] == "ls-files" ? new(0, NewFile + '\0', "")
                : throw new InvalidOperationException("No qualifying file for check-ignore"),
            _ => throws ? throw new IOException("File disappeared") : null));
    }

    private static void AssertEmpty(InScopeIgnoredFileCheckResult result)
    {
        Assert.False(result.FailsRound);
        Assert.Empty(result.Diagnostic);
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal Repository()
        {
            Git("init");
            Git("config", "user.email", "ignored-file-tests@example.com");
            Git("config", "user.name", "Ignored File Tests");
            Write("cmd/schemadrift/main.go");
            File.WriteAllText(Path.Combine(Root, "cmd/schemadrift/main.go"), "package main");
            Git("add", "--", "cmd/schemadrift/main.go");
            Git("commit", "-m", "Track main before ignoring directory");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), "schemadrift\n");
            Git("add", "--", ".gitignore");
            Git("commit", "-m", "Ignore schemadrift");
        }

        internal void Write(string path)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "// updated " + path);
        }

        internal InScopeIgnoredFileCheckResult Evaluate() => InScopeIgnoredFileCheck.Evaluate(
            Root, [Objective], DispatchTime);

        internal string Git(params string[] args)
        {
            var timestamp = DispatchTime.AddMinutes(-5).ToString("O");
            var result = InfrastructureTestSupport.RunGitProbe(Root, args, new Dictionary<string, string>
            { ["GIT_AUTHOR_DATE"] = timestamp, ["GIT_COMMITTER_DATE"] = timestamp });
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
