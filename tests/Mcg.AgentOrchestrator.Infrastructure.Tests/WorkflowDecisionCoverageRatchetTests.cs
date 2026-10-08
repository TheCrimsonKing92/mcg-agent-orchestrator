using System.Text.RegularExpressions;
using Xunit.Sdk;

public sealed class WorkflowDecisionCoverageRatchetTests
{
    // Read-only inventory of 5f55ee9fa. Keys are file/member pairs, not line numbers;
    // any undecided sibling keeps its member here until the last site is decided.
    private static readonly string[] AllowList =
    [
        "AcceptanceCriterionEvidence.RefusedCarry.cs : DescribeRefusedCarryHold : Refused carry hold is returned without a policy decision.",
        "AcceptanceCriterionEvidence.cs : RecordAndCreateHold : Criterion evidence diagnostic becomes an undecided hold.",
        "ConductorAcceptanceCohorts.cs : CohortGateFaulted : Cohort fault creates held member results without decisions.",
        "ConductorBatchLoop.ParallelAcceptanceCompletion.cs : CompleteParallelAcceptanceRun : Completion recovery holds lack typed attribution.",
        "ConductorBatchLoop.ParallelAcceptanceCompletion.cs : ParallelAcceptanceHeld : Parallel acceptance hold factory sets only ownership.",
        "ConductorBatchLoop.cs : EscalateParallelAcceptanceSafely : Exception fallback escalation has no policy decision.",
        "ConductorDriver.AcceptanceCarryForward.cs : CompleteLandingAfterRacingLandingCarryForward : Racing landing carry-forward hold lacks a decision.",
        "ConductorDriver.AcceptanceCohortExecution.cs : CohortHeld : Cohort hold member results have no decisions.",
        "ConductorDriver.AcceptanceCohortExecution.cs : CohortInFlight : In-flight cohort hold has no decision.",
        "ConductorDriver.DeferredNoChangeEvidence.cs : TryRunDeferredNoChangeEvidence : Deferred evidence escalation and pending hold lack attribution.",
        "ConductorDriver.FindingEvidenceDeveloperHandoff.cs : TryRouteDeliveredFindingEvidenceToDeveloper : Developer evidence handoff hold has no decision.",
        "ConductorDriver.FindingEvidenceRequests.cs : FocusedEvidencePendingHeld : Target-typed pending evidence hold sets ownership only.",
        "ConductorDriver.GitAndLeaseHelpers.cs : ReplacementEvidenceMutationHeld : Replacement mutation hold factory leaves attribution to callers.",
        "ConductorDriver.LandingCompletion.cs : Escalate : Escalation factory leaves attribution to callers.",
        "ConductorDriver.MergeTrains.cs : RunMergeTrain : Failed merge train creates an undecided hold.",
        "ConductorDriver.ParallelAcceptance.cs : EscalateParallelLandingAcceptance : Parallel landing escalation delegates without attaching a decision.",
        "ConductorDriver.ParallelAcceptance.cs : ReplayParallelLandingEarlyOutcome : Early outcome replay delegates to undecided Escalate.",
        "ConductorDriver.PreDispatchIntegration.cs : TryIntegrateMainBeforeReadOnlyDispatch : Integration refusal escalations have no decisions.",
        "ConductorDriver.PreReviewEvidence.cs : TryRunPreReviewEvidenceStage : Evidence failure escalation branches have no decisions.",
        "ConductorDriver.PreReviewEvidenceRouting.cs : TryStopRepeatedPreReviewMappingRetry : Mapping retry hold and escalation have no decisions.",
        "ConductorDriver.PreReviewEvidenceTimeout.cs : TryHoldPreReviewEvidenceTimeout : Evidence timeout hold and escalation have no decisions.",
        "ConductorDriver.PreReviewRepeatedFailure.cs : TryHoldRepeatedPreReviewFailure : Repeated evidence failure escalates without attribution.",
        "ConductorDriver.PreTesterDeferredEvidence.cs : TryEscalatePreTesterRedLoop : Repeated pre-tester red evidence escalates without attribution.",
        "ConductorDriver.PreTesterDeferredEvidence.cs : TryRunPreTesterDeferredEvidence : Pending focused evidence helper returns an undecided hold.",
        "ConductorDriver.TimedOutSelectionRerun.cs : EscalateUnchangedTimedOutRound : Unchanged timeout round escalates without a decision.",
        "ConductorDriver.TimedOutSelectionRerun.cs : ExecuteTimedOutSelectionRerun : Timeout rerun holds and escalation lack attribution.",
        "ConductorDriver.TimedOutSelectionRerun.cs : RetryTesterAfterTimedOutRerun : Tester retry authority change creates an undecided hold.",
        "ConductorDriver.TimedOutSelectionRerun.cs : TryResumeTimedOutSelectionRerun : Stale timeout resumption creates an undecided hold.",
        "ConductorDriver.cs : AdvanceOnce : Awaiting verification and unknown lifecycle outcomes lack decisions.",
        "ConductorDriver.cs : ExecuteDispatchAndStart : Failed dispatch recovery escalates without a decision.",
        "ConductorParallelAcceptanceAttempts.RefusedCarryRelaunch.cs : TryHoldRefusedCarryRelaunch : Refused carry helper result is held without a decision.",
        "ConductorParallelAcceptanceAttempts.cs : FromArtifact : Rehydrated held and escalated outcomes do not restore decisions."
    ];

    private static readonly Regex MemberDeclaration = new(
        @"(?m)^[ \t]*(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|new|unsafe|partial)\s+)+(?<signature>[^;{}=]*?)\b(?<name>\w+)\s*(?:<[^;{}()]+>)?\s*(?<open>\()",
        RegexOptions.CultureInvariant);
    private static readonly Regex DecisionMarker = new(@"\bDecision\s*=(?!=)|\.\s*ToRecord\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex CallsAndConstructions = new(
        @"\bnew\s+ConductorAdvanceOutcome\s*\.\s*(?:Held|Escalated)\s*\(|\b(?<call>\w+)\s*\(",
        RegexOptions.CultureInvariant);

    [Xunit.Fact]
    public void EveryUndecidedSite_IsAllowListedByFileAndMember() => AssertCoverage(Scan(ReadSources()), AllowList);

    [Xunit.Fact]
    public void EveryAllowListEntry_StillNamesAnUndecidedSite() => AssertNoStaleEntries(Scan(ReadSources()), AllowList);

    [Xunit.Fact]
    public void AllowList_HasUniqueSortedKeysAndNonemptyReasons()
    {
        var keys = AllowList.Select(EntryKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(AllowList.Order(StringComparer.Ordinal), AllowList);
        foreach (var entry in AllowList)
        {
            var parts = entry.Split(" : ", 3, StringSplitOptions.None);
            Assert.True(parts.Length == 3 && parts.All(part => !string.IsNullOrWhiteSpace(part)), $"Invalid allow-list entry: {entry}");
        }
    }

    [Xunit.Fact]
    public void AddedHeldInUnlistedMethod_FailsCoverageAndNamesMethod()
    {
        const string source = "class Fixture {\nprivate object Existing() { return null; }\n}";
        var added = source.Replace("private object Existing()", "private object Unlisted() { return new ConductorAdvanceOutcome.Held(state, \"x\"); }\nprivate object Existing()", StringComparison.Ordinal);
        var failure = Assert.Throws<XunitException>(() => AssertCoverage(Scan("Fixture.cs", added), []));
        Assert.Contains("Fixture.cs : Unlisted", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RemovingListedMethod_FailsStaleEntryAndNamesEntry()
    {
        const string entry = "Fixture.cs : Listed : Existing undecided hold.";
        const string method = "private object Listed() { return new ConductorAdvanceOutcome.Held(state, \"x\"); }";
        var source = $"class Fixture {{\n{method}\n}}";
        AssertNoStaleEntries(Scan("Fixture.cs", source), [entry]);
        var failure = Assert.Throws<XunitException>(() => AssertNoStaleEntries(Scan("Fixture.cs", source.Replace(method, "", StringComparison.Ordinal)), [entry]));
        Assert.Contains(entry, failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DecidedSibling_DoesNotHideUndecidedTernaryOrSwitchArm()
    {
        const string source = """
            class Fixture
            {
                private object Ternary() => flag
                    ? new ConductorAdvanceOutcome.Held(state, "x") { Decision = policy.ToRecord() }
                    : new ConductorAdvanceOutcome.Held(state, "y");
                private object Switch() => state switch
                {
                    First => new ConductorAdvanceOutcome.Held(state, "x") { Decision = policy.ToRecord() },
                    _ => new ConductorAdvanceOutcome.Escalated(state, "y")
                };
            }
            """;
        Assert.Equal(new[] { "Fixture.cs : Switch", "Fixture.cs : Ternary" }, Scan("Fixture.cs", source));
    }

    [Xunit.Fact]
    public void ConsumingStatement_AttachesDecisionOnlyToItsOwnResult()
    {
        const string source = """
            partial class ConductorDriver
            {
                private object Mixed()
                {
                    var first = Escalate(state, "x");
                    var second = Escalate(state, "y");
                    return first with { Outcome = first.Outcome with { Decision = policy.ToRecord() } };
                }
                private object Covered()
                {
                    var result = flag ? Escalate(state, "x") : new ConductorAdvanceOutcome.Escalated(state, "y");
                    return result with { Outcome = result.Outcome with { Decision = policy.ToRecord() } };
                }
                private object TupleSibling()
                {
                    var first = Escalate(state, "x");
                    var second = Escalate(state, "y");
                    return (first, second with { Decision = policy.ToRecord() });
                }
                private object Reassigned()
                {
                    var result = Escalate(state, "x");
                    result = AlreadyDecided();
                    return result with { Decision = policy.ToRecord() };
                }
                private object CoveredSwitch()
                {
                    var result = state switch
                    {
                        First => new ConductorAdvanceOutcome.Held(state, "x") { Decision = policy.ToRecord() },
                        _ => new ConductorAdvanceOutcome.Held(state, "y")
                    };
                    return result with { Decision = policy.ToRecord() };
                }
                private object ReturnedConsumer() =>
                    MakeResult(new ConductorAdvanceOutcome.Held(state, "x")) with { Decision = policy.ToRecord() };
            }
            """;
        Assert.Equal(new[] { "Fixture.cs : Mixed", "Fixture.cs : Reassigned", "Fixture.cs : TupleSibling" }, Scan("Fixture.cs", source));
    }

    [Xunit.Fact]
    public void Helpers_AreDiscoveredAcrossFilesAndAttachingConsumerIsRecognized()
    {
        const string helpers = """
            class Helpers
            {
                internal static ConductorAdvanceOutcome.Held Pending() => new(state, "x");
                internal static ConductorAdvanceOutcome.Held Attach(ConductorAdvanceOutcome.Held hold) => hold with { Decision = policy.ToRecord() };
            }
            """;
        const string callers = """
            class Caller
            {
                private object Missing() => Helpers.Pending();
                private object Covered()
                {
                    var hold = Helpers.Pending();
                    return Helpers.Attach(hold);
                }
                private object Inline() => Helpers.Attach(Helpers.Pending());
            }
            """;
        Assert.Equal(new[] { "Caller.cs : Missing", "Helpers.cs : Pending" }, Scan([("Helpers.cs", helpers), ("Caller.cs", callers)]));
    }

    [Xunit.Fact]
    public void LineStartingConstructionWithInitializer_IsNotAMemberDeclaration()
    {
        const string source = """
            class Fixture
            {
                private static object HeldFactory() =>
                    new(
                        new ConductorAdvanceOutcome.Held(state, "x") { Owner = owner });
                private static object EscalatedFactory() =>
                    new ConductorAdvanceOutcome.Escalated(state, "x") { Owner = owner };
            }
            """;
        Assert.Equal(new[] { "Fixture.cs : EscalatedFactory", "Fixture.cs : HeldFactory" }, Scan("Fixture.cs", source));
    }

    [Xunit.Fact]
    public void LiteralsCommentsAndLocalFunctions_DoNotCorruptMemberAttribution()
    {
        const string source = """"
            class Fixture
            {
                private object Outer()
                {
                    var text = $"literal {string.Join("}", values)} new ConductorAdvanceOutcome.Held(state, \"x\")";
                    var verbatim = @"braces { } and ""quotes""";
                    var raw = """ new ConductorAdvanceOutcome.Escalated(state, "x") { } """;
                    var character = '}';
                    // new ConductorAdvanceOutcome.Held(state, "x")
                    /* } new ConductorAdvanceOutcome.Escalated(state, "x") */
                    object Local() => new ConductorAdvanceOutcome.Held(state, "x");
                    return Local();
                }
                private object Next() => new ConductorAdvanceOutcome.Held(state, "x") { Decision = policy.ToRecord() };
            }
            """";
        Assert.Equal(new[] { "Fixture.cs : Outer" }, Scan("Fixture.cs", source));
    }

    [Xunit.Fact]
    public void UnrelatedEscalateAndDeclaration_AreNotDriverCalls()
    {
        const string source = """
            class Other
            {
                private void Escalate() { }
                private object Run() { Escalate(); return new LandingDecision.Escalate("x"); }
            }
            partial class ConductorDriver
            {
                private object Qualified() => this.Escalate(state, "x");
                private object OtherOutcome() => new LandingDecision.Escalate("x");
            }
            """;
        // Keep the unrelated class in a separate file, as in the real author host.
        var split = source.IndexOf("partial class ConductorDriver", StringComparison.Ordinal);
        Assert.Empty(Scan("Other.cs", source[..split]));
        Assert.Equal(new[] { "Driver.cs : Qualified" }, Scan("Driver.cs", source[split..]));
    }

    [Xunit.Fact]
    public void Enumeration_RequiresSourcesAndKnownFiles()
    {
        var sources = ReadSources();
        Assert.NotEmpty(sources);
        Assert.ThrowsAny<XunitException>(() => AssertSourceFiles([]));
        var failure = Assert.ThrowsAny<XunitException>(() => AssertSourceFiles(["ConductorDriver.cs"]));
        Assert.Contains("ConductorBatchLoop.cs", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MalformedSource_FailsLoudly() =>
        Assert.Throws<XunitException>(() => Scan("Broken.cs", "class Broken { private object Run() { return new ConductorAdvanceOutcome.Held(state, \"x\");"));

    internal static string[] Scan(string fileName, string text) => Scan([(fileName, text)]);

    internal static string[] Scan(IEnumerable<(string FileName, string Text)> sources)
    {
        var files = sources.Select(source => Parse(source.FileName, source.Text)).ToArray();
        var helpers = files.SelectMany(file => file.Members.Where(member => ReturnsOutcome(member.Signature)))
            .GroupBy(member => member.Name, StringComparer.Ordinal)
            // A mixed overload set is conservatively undecided.
            .ToDictionary(group => group.Key, group => group.All(member => DecisionMarker.IsMatch(member.Body)), StringComparer.Ordinal);
        var attaching = helpers.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var driver = Regex.IsMatch(file.Code, @"\bpartial\s+class\s+ConductorDriver\b");
            foreach (Match site in CallsAndConstructions.Matches(file.Code))
            {
                var open = file.Code.IndexOf('(', site.Index, site.Length);
                if (file.Members.Any(member => member.ParameterOpen == open)) continue;
                var member = file.Members.Where(candidate => candidate.Start <= site.Index && candidate.End > site.Index)
                    .OrderBy(candidate => candidate.Start).FirstOrDefault();
                var call = site.Groups["call"].Value;
                var direct = call.Length == 0;
                var escalation = driver && call == "Escalate" &&
                    !Regex.IsMatch(file.Code[..site.Index], @"\bnew\s+(?:\w+\s*\.\s*)*$");
                var helper = helpers.TryGetValue(call, out var decided) && !decided;
                var targetTyped = call == "new" && member is not null && Regex.IsMatch(member.Signature, @"\bConductorAdvanceOutcome\s*\.\s*(?:Held|Escalated)\??\s*$");
                if (!direct && !escalation && !helper && !targetTyped) continue;
                if (member is null) throw new XunitException($"Cannot attribute outcome site in {file.Name} at offset {site.Index} to a member.");
                var end = ExpressionEnd(file.Code, file.Pairs, open);
                if (!IsDecided(file, member, site.Index, end, attaching)) found.Add($"{file.Name} : {member.Name}");
            }
        }
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    internal static void AssertCoverage(IEnumerable<string> found, IEnumerable<string> entries)
    {
        var missing = found.Except(entries.Select(EntryKey), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0) throw new XunitException("Unlisted undecided sites:\n" + string.Join('\n', missing));
    }

    internal static void AssertNoStaleEntries(IEnumerable<string> found, IEnumerable<string> entries)
    {
        var keys = found.ToHashSet(StringComparer.Ordinal);
        var stale = entries.Where(entry => !keys.Contains(EntryKey(entry))).Order(StringComparer.Ordinal).ToArray();
        if (stale.Length != 0) throw new XunitException("Stale allow-list entries:\n" + string.Join('\n', stale));
    }

    private static string EntryKey(string entry) => string.Join(" : ", entry.Split(" : ", 3, StringSplitOptions.None).Take(2));

    private static (string FileName, string Text)[] ReadSources()
    {
        var root = VerifiedRepositoryRoot.Find();
        Assert.True(Directory.Exists(Path.Combine(root, "src")) && Directory.Exists(Path.Combine(root, "tests")), $"Invalid source root: {root}");
        var folder = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Orchestration");
        Assert.True(Directory.Exists(folder), $"Missing orchestration folder: {folder}");
        var paths = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        AssertSourceFiles(paths.Select(Path.GetFileName).Select(name => name!));
        return paths.Select(path => (Path.GetRelativePath(folder, path).Replace('\\', '/'), File.ReadAllText(path))).ToArray();
    }

    private static void AssertSourceFiles(IEnumerable<string> names)
    {
        var files = names.ToHashSet(StringComparer.Ordinal);
        Assert.True(files.Count > 0, "Orchestration source scan found zero .cs files.");
        foreach (var required in new[] { "ConductorDriver.cs", "ConductorBatchLoop.cs", "ConductorDriver.LandingCompletion.cs" })
            Assert.True(files.Contains(required), $"Orchestration source scan omitted {required}.");
    }

    private static bool ReturnsOutcome(string signature) => Regex.IsMatch(signature, @"\bConductorAdvanceOutcome(?:\s*\.\s*(?:Held|Escalated))?\??\s*$");

    private static bool IsDecided(Source file, Member member, int start, int end, HashSet<string> attaching)
    {
        if (DecisionMarker.IsMatch(file.Code[start..end])) return true;
        // A decision-attaching wrapper consumes this expression directly.
        foreach (var pair in file.Pairs.Where(pair => file.Code[pair.Key] == '(' && pair.Key < start && pair.Value >= end))
        {
            var name = Regex.Match(file.Code[..pair.Key], @"(?<name>\w+)\s*$").Groups["name"].Value;
            if (attaching.Contains(name)) return true;
            var consumedEnd = ExpressionEnd(file.Code, file.Pairs, pair.Key);
            if (DecisionMarker.IsMatch(file.Code[(pair.Value + 1)..consumedEnd])) return true;
        }
        var statementStart = file.Code.LastIndexOf(';', start - 1, start - member.Start) + 1;
        statementStart = Math.Max(statementStart, member.Start);
        var assignment = Regex.Matches(file.Code[statementStart..start], @"\b(?<name>\w+)\s*=(?!=|>)").Cast<Match>()
            .LastOrDefault(match => !file.Pairs.Any(pair => pair.Key < statementStart + match.Index && pair.Value > statementStart + match.Index && pair.Value < start));
        if (assignment is null) return false;
        var variable = assignment.Groups["name"].Value;
        var statementEnd = file.Code.IndexOf(';', end, member.End - end);
        if (statementEnd < 0) return false;
        var cursor = statementEnd + 1;
        foreach (var statement in file.Code[cursor..member.End].Split(';'))
        {
            if (Regex.IsMatch(statement, $@"\b{Regex.Escape(variable)}\b"))
            {
                foreach (Match with in Regex.Matches(statement, @"\bwith\s*\{"))
                {
                    var receiverEnd = cursor + with.Index;
                    var receiverStart = ReceiverStart(file, receiverEnd);
                    var initializer = file.Code.IndexOf('{', receiverEnd);
                    if (Regex.IsMatch(file.Code[receiverStart..receiverEnd], $@"\b{Regex.Escape(variable)}\b") &&
                        DecisionMarker.IsMatch(file.Code[initializer..(file.Pairs[initializer] + 1)])) return true;
                }
                foreach (var helper in attaching)
                    foreach (Match call in Regex.Matches(statement, $@"\b{Regex.Escape(helper)}\s*\("))
                    {
                        var open = cursor + call.Index + call.Length - 1;
                        if (Regex.IsMatch(file.Code[open..(file.Pairs[open] + 1)], $@"\b{Regex.Escape(variable)}\b")) return true;
                    }
                // Reassignment ends the result's lifetime; a later unrelated decision is not evidence.
                if (Regex.IsMatch(statement, $@"\b{Regex.Escape(variable)}\s*=(?!=|>)")) break;
            }
            cursor += statement.Length + 1;
        }
        return false;
    }

    private static int ReceiverStart(Source file, int end)
    {
        var cursor = end - 1;
        while (cursor >= 0 && char.IsWhiteSpace(file.Code[cursor])) cursor--;
        while (cursor >= 0)
        {
            if (file.Code[cursor] is ')' or ']')
            {
                cursor = file.Pairs.Single(pair => pair.Value == cursor).Key - 1;
                continue;
            }
            if (char.IsLetterOrDigit(file.Code[cursor]) || file.Code[cursor] is '_' or '.') { cursor--; continue; }
            break;
        }
        return cursor + 1;
    }

    private static int ExpressionEnd(string code, Dictionary<int, int> pairs, int open)
    {
        var end = pairs[open] + 1;
        var next = SkipWhitespace(code, end);
        if (next + 4 <= code.Length && code.AsSpan(next, 4).SequenceEqual("with")) next = SkipWhitespace(code, next + 4);
        return next < code.Length && code[next] == '{' ? pairs[next] + 1 : end;
    }

    private static Source Parse(string name, string text)
    {
        var code = MaskNonCode(text);
        var pairs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] is '(' or '{' or '[') stack.Push(i);
            if (code[i] is not (')' or '}' or ']')) continue;
            if (stack.Count == 0) throw new XunitException($"Unbalanced source in {name} at offset {i}.");
            var open = stack.Pop();
            if ((code[open], code[i]) is not (('(', ')') or ('{', '}') or ('[', ']')))
                throw new XunitException($"Mismatched source delimiters in {name} at offset {i}.");
            pairs.Add(open, i);
        }
        if (stack.Count > 0) throw new XunitException($"Unbalanced source in {name}.");
        var members = new List<Member>();
        foreach (Match match in MemberDeclaration.Matches(code))
        {
            // A qualified construction at the start of a line can match the
            // declaration regex, but a method's return type cannot end in a dot.
            if (match.Groups["signature"].Value.TrimEnd().EndsWith(".", StringComparison.Ordinal)) continue;
            var open = match.Groups["open"].Index;
            var bodyStart = SkipWhitespace(code, pairs[open] + 1);
            int end;
            if (bodyStart < code.Length && code[bodyStart] == '{') end = pairs[bodyStart] + 1;
            else if (bodyStart + 2 <= code.Length && code.AsSpan(bodyStart, 2).SequenceEqual("=>"))
            {
                end = bodyStart + 2;
                while (end < code.Length && code[end] != ';')
                {
                    if (pairs.TryGetValue(end, out var close)) end = close;
                    end++;
                }
                end++;
            }
            else continue;
            members.Add(new(match.Groups["name"].Value, match.Groups["signature"].Value, open, bodyStart, end, code[bodyStart..end]));
        }
        return new(name, code, pairs, members);
    }

    private static int SkipWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        return start;
    }

    private static string MaskNonCode(string text)
    {
        var masked = text.ToCharArray();
        for (var i = 0; i < text.Length;)
        {
            var start = i;
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i);
                if (i < 0) i = text.Length;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new XunitException("Unterminated source comment.");
                i = close + 2;
            }
            else if (text[i] is '"' or '\'') i = LiteralEnd(text, i);
            else { i++; continue; }
            for (var j = start; j < i; j++) if (masked[j] is not ('\r' or '\n')) masked[j] = ' ';
        }
        return new string(masked);
    }

    private static int LiteralEnd(string text, int start)
    {
        var quote = text[start];
        var quotes = 1;
        while (quote == '"' && start + quotes < text.Length && text[start + quotes] == '"') quotes++;
        // A verbatim string can start with doubled quotes; it is not a raw literal.
        var prefix = start;
        while (prefix > 0 && text[prefix - 1] is '@' or '$') prefix--;
        var verbatim = text[prefix..start].Contains('@');
        var interpolated = text[prefix..start].Contains('$');
        if (quotes >= 3 && !verbatim)
        {
            var close = text.IndexOf(new string('"', quotes), start + quotes, StringComparison.Ordinal);
            if (close < 0) throw new XunitException("Unterminated raw source string.");
            return close + quotes;
        }
        for (var i = start + 1; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\') { i++; continue; }
            if (interpolated && text[i] == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
                var depth = 1;
                while (++i < text.Length && depth > 0)
                {
                    if (text[i] is '"' or '\'') { i = LiteralEnd(text, i) - 1; continue; }
                    if (text[i] == '{') depth++;
                    if (text[i] == '}') depth--;
                }
                i--;
                continue;
            }
            if (text[i] != quote) continue;
            if (verbatim && i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i + 1;
        }
        throw new XunitException("Unterminated source literal.");
    }

    private sealed record Source(string Name, string Code, Dictionary<int, int> Pairs, List<Member> Members);
    private sealed record Member(string Name, string Signature, int ParameterOpen, int Start, int End, string Body);
}
