using System.Text.RegularExpressions;

public sealed class DispatcherProviderProbeIsolationTests
{
    [Xunit.Fact]
    public void DispatcherCallsInjectBothProviderProbes()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var tests = Path.Combine(root, "tests");
        Assert.True(Directory.Exists(tests), $"Dispatcher probe convention cannot find {tests}.");
        var failures = SourceFiles(tests)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => FindViolations(File.ReadAllText(path), Path.GetRelativePath(root, path)))
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    [Xunit.Fact]
    public void MissingInjectionFailsAndNamesRealProbe()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            "WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, goal);"));
        Assert.Contains("ClaudeCliAuthProbe.ForOneDispatchPreflight", failure.Message);
        Assert.Contains("missing injection: claudeAuthProbe, commandExists", failure.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("PrepareSubscriptionTask")]
    [Xunit.InlineData("PreflightSubscriptionTask")]
    [Xunit.InlineData("PrepareSubscriptionReadyBatch")]
    public void EveryInjectableEntryPointRejectsMissingProbe(string entryPoint)
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            $"WorkerProfileDispatcher.{entryPoint}(kernel, commandExists: fake);"));
        Assert.Contains("missing injection: claudeAuthProbe.", failure.Message);
        Assert.DoesNotContain("missing injection: claudeAuthProbe, commandExists", failure.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("null!")]
    [Xunit.InlineData("(null)")]
    [Xunit.InlineData("default")]
    [Xunit.InlineData("default(Func<ClaudeCliAuthState>)")]
    public void NullInjectionFails(string value)
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            $"WorkerProfileDispatcher.PrepareSubscriptionTask(claudeAuthProbe: {value}, commandExists: fake);"));
        Assert.Contains("missing injection: claudeAuthProbe.", failure.Message);
    }

    [Xunit.Fact]
    public void FullyInjectedCallPasses()
        => VerifySource("WorkerProfileDispatcher.PrepareSubscriptionTask(claudeAuthProbe: signedIn, commandExists: present);");

    [Xunit.Fact]
    public void MissingCommandCheckFails()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            "WorkerProfileDispatcher.PreflightSubscriptionTask(claudeAuthProbe: fake);"));
        Assert.Contains("missing injection: commandExists.", failure.Message);
    }

    [Xunit.Fact]
    public void OnlyTopLevelArgumentsCountAsInjection()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            "WorkerProfileDispatcher.PrepareSubscriptionTask(Build(claudeAuthProbe: fake, commandExists: fake));"));
        Assert.Contains("missing injection: claudeAuthProbe, commandExists", failure.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("// dispatcher-real-provider-probe-opt-in: intentional integration coverage\n", true)]
    [Xunit.InlineData("// dispatcher-real-provider-probe-opt-in:   \n", false)]
    [Xunit.InlineData("// dispatcher-real-provider-probe-opt-in: intentional integration coverage\n\n", false)]
    [Xunit.InlineData("/*\n// dispatcher-real-provider-probe-opt-in: hidden inside block comment\n*/\n", false)]
    public void OptInRequiresReasonOnImmediatelyPrecedingLine(string prefix, bool allowed)
    {
        var source = prefix + "WorkerProfileDispatcher.PrepareSubscriptionTask(kernel);";
        if (allowed)
            VerifySource(source);
        else
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(source));
    }

    [Xunit.Fact]
    public void ReadyTasksCallNamesInjectableReplacement()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(
            "WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(kernel, commandExists: fake);"));
        Assert.Contains("ClaudeCliAuthProbe.ForOneDispatchPreflight", failure.Message);
        Assert.Contains("claudeAuthProbe cannot be injected", failure.Message);
        Assert.Contains("PrepareSubscriptionReadyBatch", failure.Message);
    }

    [Xunit.Fact]
    public void NestedArgumentsAndLiteralFormsKeepCallBoundaries()
        => VerifySource(""""
            WorkerProfileDispatcher.PrepareSubscriptionTask(
                Build(new[] { Nested(")", @"quotes "" )"), '(', $"value {Nested("inside")}", """raw )""") }),
                claudeAuthProbe: () => Create(Nested()),
                commandExists: name => { return Present(name); });
            WorkerProfileDispatcher.PreflightSubscriptionTask(
                claudeAuthProbe: fake, commandExists: fake);
            """");

    [Xunit.Fact]
    public void StringsAndCommentsDoNotDeclareDispatcherCalls()
        => VerifySource(""""
            // WorkerProfileDispatcher.PrepareSubscriptionTask(kernel);
            /* WorkerProfileDispatcher.PreflightSubscriptionTask(kernel); */
            var text = "WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(kernel)";
            var verbatim = @"WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(kernel)";
            var raw = """WorkerProfileDispatcher.PrepareSubscriptionTask(kernel)""";
            """");

    [Xunit.Fact]
    public void InterpolatedExpressionsCannotHideUninjectedCalls()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(""""
            var text = $"result {WorkerProfileDispatcher.PrepareSubscriptionTask(kernel)}";
            var raw = $$"""result {{WorkerProfileDispatcher.PreflightSubscriptionTask(kernel)}}""";
            """"));
        Assert.Contains("PrepareSubscriptionTask", failure.Message);
        Assert.Contains("PreflightSubscriptionTask", failure.Message);
    }

    [Xunit.Fact]
    public void LiteralTextCannotMasqueradeAsInjectedArguments()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource("""
            WorkerProfileDispatcher.PrepareSubscriptionTask("claudeAuthProbe: fake, commandExists: fake");
            """));
        Assert.Contains("missing injection: claudeAuthProbe, commandExists", failure.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("using static Mcg.AgentOrchestrator.Infrastructure.WorkerProfileDispatcher;")]
    [Xunit.InlineData("using Dispatcher = Mcg.AgentOrchestrator.Infrastructure.WorkerProfileDispatcher;")]
    public void DispatcherImportsCannotBypassConvention(string directive)
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => VerifySource(directive));
        Assert.Contains("ClaudeCliAuthProbe.ForOneDispatchPreflight", failure.Message);
        Assert.Contains("claudeAuthProbe and commandExists", failure.Message);
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs"))
            yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".scratch" or "TestResults" or "playwright-report")
                continue;
            foreach (var path in SourceFiles(child))
                yield return path;
        }
    }

    // This is a source convention, not an interception of production execution. Keeping the
    // check in test code leaves the dispatcher's real defaults untouched.
    private static void VerifySource(string source)
    {
        var failures = FindViolations(source, "fixture.cs");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<string> FindViolations(string source, string path)
    {
        var lexer = new SourceLexer(source);
        var tokens = lexer.Read();
        var failures = new List<string>();
        for (var i = 0; i + 3 < tokens.Count; i++)
        {
            if (tokens[i].Text == "using" && (tokens[i + 1].Text == "static" || tokens[i + 2].Text == "="))
            {
                var directive = tokens.Skip(i).TakeWhile(token => token.Text != ";");
                if (directive.Any(token => token.Text == "WorkerProfileDispatcher"))
                    failures.Add($"{path}: Dispatcher aliases/static imports can hide ClaudeCliAuthProbe.ForOneDispatchPreflight; use explicit WorkerProfileDispatcher calls with claudeAuthProbe and commandExists injections.");
            }
            if (tokens[i].Text != "WorkerProfileDispatcher" || tokens[i + 1].Text != "."
                || tokens[i + 2].Text is not ("PrepareSubscriptionTask" or "PreflightSubscriptionTask"
                    or "PrepareSubscriptionReadyBatch" or "PrepareSubscriptionReadyTasks")
                || tokens[i + 3].Text != "(")
                continue;

            var offset = tokens[i].Offset;
            var line = source.AsSpan(0, offset).Count('\n') + 1;
            if (HasOptIn(source, offset, lexer.LineComments))
                continue;
            var entryPoint = tokens[i + 2].Text;
            var injected = new HashSet<string>(StringComparer.Ordinal);
            var depth = 0;
            var argumentStart = i + 4;
            var closed = false;
            for (var j = argumentStart; j < tokens.Count; j++)
            {
                var text = tokens[j].Text;
                if (depth == 0 && text is "," or ")")
                {
                    var argument = tokens.GetRange(argumentStart, j - argumentStart);
                    if (argument.Count > 2 && argument[1].Text == ":")
                    {
                        var value = argument.Skip(2).FirstOrDefault(token => token.Text != "(");
                        if (value is not null && value.Text is not ("null" or "default"))
                            injected.Add(argument[0].Text);
                    }
                    argumentStart = j + 1;
                    if (text == ")")
                    {
                        closed = true;
                        break;
                    }
                }
                else if (text is "(" or "[" or "{")
                    depth++;
                else if (text is ")" or "]" or "}")
                    depth--;
            }

            var missing = new[] { "claudeAuthProbe", "commandExists" }.Where(name => !injected.Contains(name));
            var message = entryPoint == "PrepareSubscriptionReadyTasks"
                ? "claudeAuthProbe cannot be injected here; use PrepareSubscriptionReadyBatch(...).Dispatches and inject claudeAuthProbe and commandExists."
                : !closed ? "unterminated invocation; cannot verify claudeAuthProbe and commandExists injections."
                : string.Join(", ", missing) is { Length: > 0 } names ? $"missing injection: {names}." : null;
            if (message is not null)
                failures.Add($"{path}:{line}: WorkerProfileDispatcher.{entryPoint} can reach ClaudeCliAuthProbe.ForOneDispatchPreflight or the real command check; {message}");
        }
        return failures;
    }

    private static bool HasOptIn(string source, int offset, HashSet<int> lineComments)
    {
        var lineStart = source.LastIndexOf('\n', offset) + 1;
        if (lineStart == 0)
            return false;
        var previousStart = lineStart < 2 ? 0 : source.LastIndexOf('\n', lineStart - 2) + 1;
        var previous = source[previousStart..(lineStart - 1)];
        var marker = Regex.Match(previous, @"^\s*(// dispatcher-real-provider-probe-opt-in:)\s*\S.*$");
        return marker.Success && lineComments.Contains(previousStart + marker.Groups[1].Index);
    }

    private sealed record Token(string Text, int Offset);

    // Tokenize only code: commas and parentheses in literal text must not delimit arguments.
    // Interpolation expressions are code and are scanned recursively, including nested strings.
    private sealed class SourceLexer(string source)
    {
        private readonly List<Token> _tokens = [];
        private int _position;
        public HashSet<int> LineComments { get; } = [];

        public List<Token> Read()
        {
            ReadCode();
            return _tokens;
        }

        private void ReadCode(int closingBraces = 0)
        {
            var braces = 0;
            while (_position < source.Length)
            {
                var c = source[_position];
                if (closingBraces > 0 && braces == 0 && c == '}' && RunLength('}') >= closingBraces)
                    return;
                if (char.IsWhiteSpace(c)) { _position++; continue; }
                if (StartsWith("//"))
                {
                    LineComments.Add(_position);
                    var end = source.IndexOf('\n', _position);
                    _position = end < 0 ? source.Length : end;
                    continue;
                }
                if (StartsWith("/*"))
                {
                    var end = source.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end < 0 ? source.Length : end + 2;
                    continue;
                }
                if (TryReadString())
                    continue;
                if (c == '\'')
                {
                    _tokens.Add(new Token("<literal>", _position++));
                    while (_position < source.Length)
                    {
                        var next = source[_position++];
                        if (next == '\\') _position = Math.Min(source.Length, _position + 1);
                        else if (next == '\'') break;
                    }
                    continue;
                }
                if (char.IsLetter(c) || c is '_' or '@')
                {
                    var start = _position++;
                    while (_position < source.Length && (char.IsLetterOrDigit(source[_position]) || source[_position] == '_'))
                        _position++;
                    _tokens.Add(new Token(source[start.._position].TrimStart('@'), start));
                    continue;
                }
                if (c == '{') braces++;
                else if (c == '}') braces--;
                _tokens.Add(new Token(c.ToString(), _position++));
            }
        }

        private bool TryReadString()
        {
            var start = _position;
            var cursor = start;
            var dollars = 0;
            var verbatim = false;
            while (cursor < source.Length && source[cursor] is '$' or '@')
            {
                if (source[cursor++] == '$') dollars++;
                else verbatim = true;
            }
            if (cursor >= source.Length || source[cursor] != '"')
                return false;
            _position = cursor;
            var quotes = RunLength('"');
            var raw = quotes >= 3;
            _position += raw ? quotes : 1;
            _tokens.Add(new Token("<literal>", start));
            while (_position < source.Length)
            {
                if (source[_position] == '"')
                {
                    if (raw)
                    {
                        if (RunLength('"') >= quotes) { _position += quotes; break; }
                    }
                    else if (verbatim && StartsWith("\"\"")) { _position += 2; continue; }
                    else { _position++; break; }
                }
                if (!raw && !verbatim && source[_position] == '\\')
                {
                    _position = Math.Min(source.Length, _position + 2);
                    continue;
                }
                if (dollars > 0 && source[_position] == '{')
                {
                    var count = RunLength('{');
                    var needed = raw ? dollars : 1;
                    if (!raw && count >= 2) { _position += 2; continue; }
                    if (count >= needed)
                    {
                        _position += count;
                        _tokens.Add(new Token("{", _position - needed));
                        ReadCode(needed);
                        _tokens.Add(new Token("}", _position));
                        _position = Math.Min(source.Length, _position + needed);
                        continue;
                    }
                }
                _position++;
            }
            return true;
        }

        private bool StartsWith(string text) => source.AsSpan(_position).StartsWith(text, StringComparison.Ordinal);

        private int RunLength(char character)
        {
            var end = _position;
            while (end < source.Length && source[end] == character) end++;
            return end - _position;
        }
    }
}
