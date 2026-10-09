using System.Text;

// Parallel-safe: reads immutable source and creates isolated fixture directories.
public sealed class IntegrationBranchLiteralConventionTests
{
    private sealed record Allowance(string File, string Literal, string Reason, string? Marker = null);
    private sealed record Hit(string File, int Line, string Literal, string SourceLine)
    {
        public override string ToString() => $"{File}:{Line}: integration branch literal \"{Literal}\"";
    }

    private const string SliceTwo = "Slice two: worker/mirror/lifecycle branch argument; remove when threaded.";
    private static readonly Allowance[] AllowList =
    [
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/TrunkBranchName.cs", "main",
            "Single definition of the default integration branch.", "public const string Default ="),
        new("Mcg.AgentOrchestrator.App/Console/OwnerActivityNarrator.cs", "main",
            "Classifies user-facing activity text; does not construct git arguments.", "Contains("),
        new("Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs", "main", SliceTwo),
        new("Mcg.AgentOrchestrator.Execution/Workers/WorkerGitContext.cs", "main", SliceTwo),
        new("Mcg.AgentOrchestrator.Execution/Workers/WorkerGitContext.cs", "main...HEAD", SliceTwo),
        new("Mcg.AgentOrchestrator.Execution/Workers/WorkerGitContext.cs", "main^{commit}", SliceTwo),
        new("Mcg.AgentOrchestrator.Execution/Workers/WorkerProfileDispatcher.cs", "main", SliceTwo),
        new("Mcg.AgentOrchestrator.Infrastructure/GoalLifecycleEventWriter.cs", "main", SliceTwo),
        new("Mcg.AgentOrchestrator.App/Orchestration/RemoteGitMirror.cs", "main", SliceTwo)
    ];

    [Fact]
    public void SourceHasNoUnconfiguredIntegrationBranchLiterals()
    {
        var root = Path.Combine(VerifiedRepositoryRoot.Find(), "src");
        var hits = Scan(root);
        Assert.Contains(hits, hit => hit.File == AllowList[0].File && hit.Literal == "main");
        Assert.All(AllowList, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
        var violations = Violations(hits);
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations.Select(hit => hit.ToString())));
    }

    [Theory]
    [InlineData("\"main\"", "main")]
    [InlineData("\"main^{commit}\"", "main^{commit}")]
    [InlineData("\"main..HEAD\"", "main..HEAD")]
    [InlineData("\"main...HEAD\"", "main...HEAD")]
    [InlineData("@\"main\"", "main")]
    [InlineData("$\"main...{candidate}\"", "main...")]
    [InlineData("\"\\u006dain\"", "main")]
    public void FixtureReintroductionReportsFileAndLine(string argument, string literal)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.cs"), "// fixture\nGitCli.Run(root, \"rev-parse\", " + argument + ");\n");
            var hit = Assert.Single(Scan(root));
            Assert.Equal("Fixture.cs", hit.File);
            Assert.Equal(2, hit.Line);
            Assert.Equal(literal, hit.Literal);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void ScannerIgnoresCommentsNonmatchingTextAndGeneratedTrees()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.cs"), """
                // GitCli.Run(root, "main");
                /* "main^{commit}" */
                var a = "maintenance";
                var b = "main must be repaired";
                var c = "escaped quote: \"main\"";
                var quote = '"';
                """);
            foreach (var directory in new[] { "bin", "obj", ".scratch", ".orchestrator-prototype" })
            {
                Directory.CreateDirectory(Path.Combine(root, directory));
                File.WriteAllText(Path.Combine(root, directory, "Generated.cs"), "GitCli.Run(root, \"main\");");
            }
            Assert.Empty(Scan(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static IReadOnlyList<Hit> Scan(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or "TestResults"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        var hits = new List<Hit>();
        foreach (var file in files)
        {
            hits.AddRange(ScanSource(Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
        }
        return hits;
    }

    private static Hit[] Violations(IEnumerable<Hit> hits) => hits.Where(hit => !AllowList.Any(entry =>
        entry.File == hit.File && entry.Literal == hit.Literal && (entry.Marker is null ||
            hit.SourceLine.Contains(entry.Marker, StringComparison.Ordinal)))).ToArray();

    private static IReadOnlyList<Hit> ScanSource(string relativePath, string source)
    {
        var hits = new List<Hit>();
        source = source.ReplaceLineEndings("\n");
        var lines = source.Split('\n');
        var line = 1;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n') { line++; continue; }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i + 1 < source.Length && source[i + 1] != '\n') i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                { if (source[i++] == '\n') line++; }
                i++;
                continue;
            }
            if (source[i] == '\'')
            {
                while (++i < source.Length && source[i] != '\'')
                    if (source[i] == '\\') i++;
                continue;
            }
            if (source[i] != '"') continue;
            var startLine = line;
            var verbatim = i > 0 && source[i - 1] == '@' || i > 1 && source[i - 2] == '@' && source[i - 1] == '$';
            var interpolated = i > 0 && source[i - 1] == '$' || i > 1 && source[i - 2] == '$' && source[i - 1] == '@';
            var quoteCount = 1;
            while (i + quoteCount < source.Length && source[i + quoteCount] == '"') quoteCount++;
            var raw = quoteCount >= 3;
            var value = new StringBuilder();
            var part = new StringBuilder();
            var parts = new List<string>();
            var holeDepth = 0;
            var prefixComplete = false;
            i += raw ? quoteCount : 1;
            for (; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '\n') line++;
                if (holeDepth > 0)
                {
                    if (c == '{') holeDepth++;
                    else if (c == '}') holeDepth--;
                    continue;
                }
                if (raw && source.AsSpan(i).StartsWith(new string('"', quoteCount), StringComparison.Ordinal))
                { i += quoteCount - 1; break; }
                if (!raw && c == '"')
                {
                    if (verbatim && i + 1 < source.Length && source[i + 1] == '"') { i++; c = '"'; }
                    else break;
                }
                else if (!raw && !verbatim && c == '\\' && i + 1 < source.Length)
                {
                    c = source[++i];
                    if (c is 'u' or 'U' or 'x')
                    {
                        var maximum = c == 'U' ? 8 : 4;
                        var digits = 0;
                        while (digits < maximum && i + 1 + digits < source.Length && Uri.IsHexDigit(source[i + 1 + digits])) digits++;
                        if (digits > 0)
                        {
                            var code = Convert.ToInt32(source.Substring(i + 1, digits), 16);
                            if (!prefixComplete) value.Append(char.ConvertFromUtf32(code));
                            part.Append(char.ConvertFromUtf32(code));
                            i += digits;
                            continue;
                        }
                    }
                    c = c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => c };
                }
                else if (interpolated && c == '{')
                {
                    if (i + 1 < source.Length && source[i + 1] == '{') i++;
                    else
                    {
                        prefixComplete = true;
                        parts.Add(part.ToString());
                        part.Clear();
                        holeDepth = 1;
                        continue;
                    }
                }
                else if (interpolated && c == '}' && i + 1 < source.Length && source[i + 1] == '}') i++;
                if (!prefixComplete) value.Append(c);
                part.Append(c);
            }
            var literal = value.ToString();
            if (literal == "main" || literal.StartsWith("main^", StringComparison.Ordinal) || literal.StartsWith("main..", StringComparison.Ordinal))
                hits.Add(new(relativePath, startLine, literal, lines[startLine - 1]));
            parts.Add(part.ToString());
            var qualified = QualifiedForm(parts);
            if (qualified is not null) hits.Add(new(relativePath, startLine, qualified, lines[startLine - 1]));
        }
        return hits;
    }

    private static string? QualifiedForm(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        foreach (var form in new[] { "refs/remotes/origin/main", "refs/heads/main", "origin/main" })
        {
            for (var start = 0; start < part.Length;)
            {
                var index = part.IndexOf(form, start, StringComparison.Ordinal);
                if (index < 0) break;
                var end = index + form.Length;
                if ((index == 0 || !IsRefCharacter(part[index - 1])) &&
                    (end == part.Length || !IsRefCharacter(part[end]) && part[end] != '/')) return form;
                start = end;
            }
        }
        return null;
    }

    private static bool IsRefCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '-';

    [Theory]
    [InlineData("\"refs/heads/main\"", "refs/heads/main")]
    [InlineData("\"refs/remotes/origin/main\"", "refs/remotes/origin/main")]
    [InlineData("\"origin/main\"", "origin/main")]
    [InlineData("$\"{sha}..refs/heads/main\"", "refs/heads/main")]
    [InlineData("@\"refs/heads/main\"", "refs/heads/main")]
    [InlineData("\"refs/heads/main^{commit}\"", "refs/heads/main")]
    public void QualifiedFixtureReintroductionFailsConvention(string argument, string literal)
    {
        var source = "// fixture\nGitCli.Run(root, \"merge-base\", " + argument + ", \"HEAD\");\n";
        var hit = Assert.Single(Violations(ScanSource("Fixture.cs", source)));
        Assert.Equal("Fixture.cs", hit.File);
        Assert.Equal(2, hit.Line);
        Assert.Equal(literal, hit.Literal);
    }

    [Fact]
    public void ScannerIgnoresQualifiedLookalikesAndConfiguredInterpolation()
    {
        Assert.Empty(ScanSource("Fixture.cs", """
            var a = "refs/heads/mainline";
            var b = "origin/maintenance";
            // "refs/heads/main"
            var c = $"refs/heads/{branch}";
            var d = $"refs/remotes/origin/{landedBranch}";
            """));
    }
}
