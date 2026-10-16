using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each class fixture owns a unique repository, local git config and cleanup;
// facts only read its completed history. No collection or shared git configuration is needed.
public sealed class GateFailureLandingAttributorTests : IClassFixture<GateFailureLandingAttributorTests.RepositoryFixture>
{
    private readonly RepositoryFixture _repository;
    public GateFailureLandingAttributorTests(RepositoryFixture repository) => _repository = repository;

    [Fact]
    public void AddedLandingWinsOverReferencedChangedTypeInMergedCandidate()
    {
        var attribution = Attribute(Probe("RebindReplayProbe", RepositoryFixture.RebindPath, "Driver"));
        Assert.Equal(GateFailureLandingAttributor.Origin.Interaction, attribution.Origin);
        Assert.Contains("RebindReplayProbe: added by goal 6a4ec362", attribution.Line);
        Assert.Contains(_repository.LandingSha[..9], attribution.Line);
        Assert.Contains(_repository.AnchorSha[..8], attribution.Line);
        Assert.Contains("likely interaction", attribution.Line);
        Assert.Contains("This candidate changed referenced type(s) Driver.", attribution.Line);
    }

    [Fact]
    public void ChangedTestSourceWinsWithoutReadingHistory()
    {
        var probe = Probe("OwnProbe", "tests/OwnProbe.cs", "Driver");
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, _) => throw new InvalidOperationException("must not read git"));
        var reading = Reading(probe) with { ChangedPaths = [".\\TESTS\\OwnProbe.cs"] };
        var attribution = Assert.Single(attributor.Attribute(GoalWithAnchor(), null, reading, [Check(probe)]));
        Assert.Equal(GateFailureLandingAttributor.Origin.Own, attribution.Origin);
        Assert.Contains("test source tests/OwnProbe.cs changed in this candidate's diff", attribution.Line);
    }

    [Fact]
    public void ExistingTestReferencingChangedTypeIsOwnFailure()
    {
        var attribution = Attribute(Probe("ExistingProbe", RepositoryFixture.ExistingPath, "Driver"));
        Assert.Equal(GateFailureLandingAttributor.Origin.Own, attribution.Origin);
        Assert.Contains("references candidate-changed type(s) Driver", attribution.Line);
        Assert.Contains("likely this candidate's own failure", attribution.Line);
        Assert.Contains(_repository.AnchorSha[..8], attribution.Line);
    }

    [Fact]
    public void LandingReachableFromAnchorDoesNotImplicateEarlierGoal()
    {
        var attribution = Attribute(Probe("OldProbe", RepositoryFixture.OldPath));
        Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains("unattributed", attribution.Line);
        Assert.DoesNotContain("interaction", attribution.Line);
        Assert.DoesNotContain("bbbbbbbb", attribution.Line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoAnchorSkipsGitAndUsesChangedTypeOnlyWhenPresent(bool referencesType)
    {
        var calls = new List<IReadOnlyList<string>>();
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, args) =>
        {
            calls.Add(args);
            return new(128, "", "must not be called");
        });
        var probe = Probe("RebindReplayProbe", RepositoryFixture.RebindPath, referencesType ? ["Driver"] : []);
        var attribution = Assert.Single(attributor.Attribute(GoalWithTasks(), _repository.MainSha, Reading(probe), [Check(probe)]));
        Assert.Empty(calls);
        Assert.Equal(referencesType ? GateFailureLandingAttributor.Origin.Own : GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains(referencesType ? "since the last pass (no anchor)" :
            "no passing Tester or Reviewer verification records a candidate sha, so main history was not read; unattributed.", attribution.Line);
    }

    [Fact]
    public void NonRepositoryIsUnreadableEvenWithReferencedChangedType()
    {
        // This sibling directory is owned by this fixture and is outside its git repository.
        var probe = Probe("RebindReplayProbe", RepositoryFixture.RebindPath, "Driver");
        var attributor = new GateFailureLandingAttributor(_repository.NonRepositoryRoot);
        var attribution = Assert.Single(attributor.Attribute(GoalWithAnchor(), _repository.MainSha, Reading(probe), [Check(probe)]));
        Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains("main history unreadable (exit 128", attribution.Line);
    }

    [Fact]
    public void MissingMainHeadIsUnreadableWithKnownAnchor()
    {
        var probe = Probe("ExistingProbe", RepositoryFixture.ExistingPath, "Driver");
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, _) => throw new Exception("git must be skipped"));
        var attribution = Assert.Single(attributor.Attribute(GoalWithAnchor(), null, Reading(probe), [Check(probe)]));
        Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains("main history unreadable (main head unavailable)", attribution.Line);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("not-started")]
    [InlineData("throw")]
    public void FirstGitFailureStopsHistoryReadsButChangedSourceStillWins(string failure)
    {
        var calls = 0;
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, _) =>
        {
            calls++;
            return failure switch
            {
                "timeout" => new(0, "", "drain failed\nextra", DrainTimedOut: true),
                "not-started" => new(0, "", "not started", ProcessStarted: false),
                "throw" => throw new IOException("broken runner\nextra"),
                _ => new(128, "", "bad revision\nextra")
            };
        });
        var probes = new[] { Probe("A", "tests/A.cs", "Driver"), Probe("B", "tests/B.cs", "Driver"), Probe("C", "src/Driver.cs") };
        var attributions = attributor.Attribute(GoalWithAnchor(), _repository.MainSha, Reading(probes), probes.Select(Check).ToArray());
        Assert.Equal(1, calls);
        Assert.Equal(3, attributions.Count);
        Assert.All(attributions.Take(2), item =>
        {
            Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, item.Origin);
            Assert.Contains("main history unreadable", item.Line);
            Assert.DoesNotContain("extra", item.Line);
        });
        Assert.Equal(attributions[0].Line[2..], attributions[1].Line[2..]);
        Assert.Equal(GateFailureLandingAttributor.Origin.Own, attributions[2].Origin);
    }

    [Fact]
    public void AddingQueryFailureDoesNotEmitInteraction()
    {
        var calls = 0;
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, args) =>
        {
            calls++;
            return args.Contains("--diff-filter=A") ? new(128, "", "failed adding query") :
                new(0, $"{_repository.LandingSha}\u001f6a4ec3623e3f41bfbb0ac369c49d66fc\n", "");
        });
        var probe = Probe("RebindReplayProbe", RepositoryFixture.RebindPath, "Driver");
        var attribution = Assert.Single(attributor.Attribute(GoalWithAnchor(), _repository.MainSha, Reading(probe), [Check(probe)]));
        Assert.Equal(2, calls);
        Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains("main history unreadable", attribution.Line);
    }

    [Fact]
    public void NewestForeignCommitNamesModifiedLandingAndCountsOtherGoals()
    {
        var goal = GoalWithAnchor();
        var calls = new List<IReadOnlyList<string>>();
        var newest = new string('a', 40);
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, args) =>
        {
            calls.Add(args);
            return new(0, args.Contains("--diff-filter=A") ? "" :
                $"{new string('c', 40)}\u001f{Guid.Parse(goal.Id.Value):D}\n{newest}\u001f6A4EC362-3E3F-41BF-BB0A-C369C49D66FC\n{new string('d', 40)}\u001f\n{new string('e', 40)}\u001fbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n", "");
        });
        var probe = Probe("RebindReplayProbe", RepositoryFixture.RebindPath, "Zebra", "Driver");
        var attribution = Assert.Single(attributor.Attribute(goal, _repository.MainSha, Reading(probe), [Check(probe)]));
        Assert.Equal(GateFailureLandingAttributor.Origin.Interaction, attribution.Origin);
        Assert.Contains("modified by goal 6a4ec362 (commit aaaaaaaaa)", attribution.Line);
        Assert.Contains("(+2 other landing goals) This candidate changed referenced type(s) Driver, Zebra.", attribution.Line);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, args => Assert.Equal("log", args[0]));
        Assert.Contains($"{_repository.AnchorSha}..{_repository.MainSha}", calls[0]);
        Assert.Equal(RepositoryFixture.RebindPath, calls[0][^1]);
    }

    [Fact]
    public void SelfGoalHistoryDoesNotImplicateLandingOrRunAddingQuery()
    {
        var goal = GoalWithAnchor();
        var calls = 0;
        var attributor = new GateFailureLandingAttributor(_repository.Root, (_, _) =>
        {
            calls++;
            return new(0, $"{_repository.LandingSha}\u001f{goal.Id.Value}\n", "");
        });
        var probe = Probe("ExistingProbe", RepositoryFixture.ExistingPath, "Driver");
        var attribution = Assert.Single(attributor.Attribute(goal, _repository.MainSha, Reading(probe), [Check(probe)]));
        Assert.Equal(1, calls);
        Assert.Equal(GateFailureLandingAttributor.Origin.Own, attribution.Origin);
    }

    [Fact]
    public void ActionableClassesAreGroupedOrderedBoundedAndEvidenceUsesFirstIdentity()
    {
        var probes = Enumerable.Range(0, 12).Select(i => Probe($"Class{i:00}", $"tests/Class{i:00}.cs")).ToList();
        probes.Add(Probe("Excluded", "tests/Excluded.cs"));
        probes.Add(Probe("Unresolved", "" ) with { ResolvedSourcePaths = [] });
        probes.Add(probes[0] with { TestIdentity = "Ns.Class00.ACase(value: \"dot.in.arg\")" });
        var checks = probes.Where(probe => !probe.TestIdentity.Contains("Excluded", StringComparison.Ordinal)).Select(Check).ToArray();
        var evidenceIdentity = probes[^1].TestIdentity;
        checks[0] = checks[0] with { FailingTestAttributions = [new(evidenceIdentity, AcceptanceTestFailureOrigin.Introduced, "recorded baseline evidence.  ")] };
        var attributions = new GateFailureLandingAttributor(_repository.Root).Attribute(GoalWithTasks(), null, Reading(probes.ToArray()), checks);
        Assert.Equal(11, attributions.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"Class{i:00}"), attributions.Take(10).Select(item => item.ClassName));
        Assert.EndsWith("Merge-base arm: recorded baseline evidence.", attributions[0].Line);
        Assert.Equal("2 more failing classes not attributed.", attributions[^1].Line);
        Assert.Equal("", attributions[^1].ClassName);
    }

    [Theory]
    [InlineData("Ns.Outer+Inner.Case(x: \"a.b\")", "Outer+Inner")]
    [InlineData("BareIdentity", "BareIdentity")]
    public void ClassNamesKeepNestedTypesAndArgumentDotsDoNotAffectGrouping(string identity, string expected)
    {
        var probe = Probe("Placeholder", RepositoryFixture.ExistingPath) with { TestIdentity = identity };
        Assert.Equal(expected, Attribute(probe).ClassName);
    }

    [Fact]
    public void MergeBaseEvidenceIsQuotedWithoutReinterpretingOriginAndLineIsCapped()
    {
        var probe = Probe("OldProbe", RepositoryFixture.OldPath);
        var check = Check(probe) with { FailingTestAttributions = [new(probe.TestIdentity, AcceptanceTestFailureOrigin.Introduced, new string('x', 500))] };
        var attribution = Assert.Single(new GateFailureLandingAttributor(_repository.Root).Attribute(GoalWithAnchor(), _repository.MainSha, Reading(probe), [check]));
        Assert.Equal(GateFailureLandingAttributor.Origin.Unattributed, attribution.Origin);
        Assert.Contains("Merge-base arm: xxx", attribution.Line);
        Assert.Equal(400, attribution.Line.Length);
        Assert.EndsWith("...", attribution.Line);
    }

    [Fact]
    public void LastPassingShaSelectsNewestCompletedTesterOrReviewerOnly()
    {
        var older = VerificationTask(AgentRole.Tester, "older", 1);
        var newest = VerificationTask(AgentRole.Reviewer, " newest ", 2);
        var distractors = new[]
        {
            VerificationTask(AgentRole.Tester, "failed", 3, exitCode: 1),
            VerificationTask(AgentRole.Developer, "developer", 4),
            VerificationTask(AgentRole.Reviewer, "  ", 5),
            VerificationTask(AgentRole.Tester, "pending", 6) with { Status = WorkTaskStatus.Running }
        };
        Assert.Equal("newest", GateFailureLandingAttributor.SelectLastPassingCandidateSha(GoalWithTasks([older, newest, .. distractors])));
        Assert.Equal("newest", GateFailureLandingAttributor.SelectLastPassingCandidateSha(GoalWithTasks(newest, older)));
        Assert.Equal("first", GateFailureLandingAttributor.SelectLastPassingCandidateSha(GoalWithTasks(
            VerificationTask(AgentRole.Tester, "first", 2), newest)));
        Assert.Null(GateFailureLandingAttributor.SelectLastPassingCandidateSha(GoalWithTasks(distractors)));
    }

    [Fact]
    public void GateOmitsHeaderWhenReadingOrAttributorOrResolvedClassesAreAbsent()
    {
        var gate = new ApparatusRedGate(Path.Combine(_repository.NonRepositoryRoot, Guid.NewGuid() + ".jsonl"),
            landingAttributor: new GateFailureLandingAttributor(_repository.Root));
        Assert.Empty(gate.FormatFailingClassAttribution(GoalWithAnchor(), null, null, []));
        Assert.Empty(gate.FormatFailingClassAttribution(GoalWithAnchor(), null, Reading(), []));
        var withoutAttributor = new ApparatusRedGate(Path.Combine(_repository.NonRepositoryRoot, Guid.NewGuid() + ".jsonl"));
        var probe = Probe("RebindReplayProbe", RepositoryFixture.RebindPath);
        Assert.Empty(withoutAttributor.FormatFailingClassAttribution(GoalWithAnchor(), _repository.MainSha, Reading(probe), [Check(probe)]));
    }

    private GateFailureLandingAttributor.ClassAttribution Attribute(ApparatusRedFailingTest probe) =>
        Assert.Single(new GateFailureLandingAttributor(_repository.Root).Attribute(GoalWithAnchor(), _repository.MainSha, Reading(probe), [Check(probe)]));

    private Goal GoalWithAnchor() => GoalWithTasks(VerificationTask(AgentRole.Tester, _repository.AnchorSha, 1));

    private static Goal GoalWithTasks(params TaskSnapshot[] tasks)
    {
        var id = GoalId.New();
        if (tasks.Length == 0)
            tasks = [VerificationTask(AgentRole.Developer, "", 0) with { Status = WorkTaskStatus.Pending, LastVerification = null }];
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(id.Value, "Attribute gate failures", GoalStatus.Active, tasks, [])], [])).GetGoal(id);
    }

    private static TaskSnapshot VerificationTask(AgentRole role, string sha, int minute, int exitCode = 0) =>
        new(TaskId.New().Value, "Verification", role, WorkTaskStatus.Completed, null, null,
            new TaskVerificationSnapshot("verify", "C:\\tmp", exitCode, "ok", "", DateTimeOffset.Parse("2026-10-15T00:00:00Z").AddMinutes(minute), ReviewedCommit: sha),
            null, null, null);

    private static ApparatusRedFailingTest Probe(string name, string path, params string[] types) =>
        new("focused tests", $"Ns.{name}.Case", null, [path], false, false, ReferencedChangedTypes: types);
    private static ApparatusRedGateReading Reading(params ApparatusRedFailingTest[] probes) => new(["src/Driver.cs"], probes, true, ["focused tests"]);
    private static AcceptanceCheckResult Check(ApparatusRedFailingTest probe) => new("focused tests", false, 1, "assertion failed", FailingTestIdentities: [probe.TestIdentity]);

    public sealed class RepositoryFixture : IDisposable
    {
        internal const string RebindPath = "tests/Rebind/RebindReplayProbe.cs";
        internal const string ExistingPath = "tests/ExistingProbe.cs";
        internal const string OldPath = "tests/OldProbe.cs";
        internal string Root { get; }
        internal string NonRepositoryRoot { get; }
        internal string AnchorSha { get; }
        internal string LandingSha { get; }
        internal string MainSha { get; }
        internal string MergeSha { get; }

        public RepositoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-gate-attribution-" + Guid.NewGuid().ToString("N"));
            NonRepositoryRoot = Root + "-nonrepo";
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(NonRepositoryRoot);
            Git("init", "-q", "-b", "main");
            Root = Git("rev-parse", "--show-toplevel");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "Gate Attribution Tests");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.hooksPath", ".no-hooks");
            Write("src/Driver.cs", "public class Driver { public int Value => 0; }");
            Write(ExistingPath, "public class ExistingProbe { Driver driver = new(); }");
            Write(OldPath, "public class OldProbe { }");
            Git("add", ".");
            Git("commit", "-q", "-m", "B0", "-m", "Goal: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Git("checkout", "-q", "-b", "goal");
            Write("src/Driver.cs", "public class Driver { public int Value => 1; }");
            Git("add", ".");
            Git("commit", "-q", "-m", "C1");
            AnchorSha = Git("rev-parse", "HEAD");
            Git("checkout", "-q", "main");
            Write(RebindPath, "namespace Ns; public class RebindReplayProbe { Driver driver = new(); public void Case() { } }");
            Git("add", ".");
            Git("commit", "-q", "-m", "L1", "-m", "Goal: 6a4ec3623e3f41bfbb0ac369c49d66fc");
            LandingSha = Git("rev-parse", "HEAD");
            Write("unrelated.txt", "M1");
            Git("add", ".");
            Git("commit", "-q", "-m", "M1");
            MainSha = Git("rev-parse", "HEAD");
            Git("checkout", "-q", "goal");
            Git("merge", "-q", "--no-edit", "main");
            MergeSha = Git("rev-parse", "HEAD");
        }

        private void Write(string path, string contents)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }

        private string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded && !result.DrainTimedOut && result.ProcessStarted,
                $"git {string.Join(' ', args)} failed: exit={result.ExitCode}; stderr={result.Error}");
            return result.Output.Trim();
        }

        public void Dispose()
        {
            foreach (var path in new[] { Root, NonRepositoryRoot })
            {
                try { Directory.Delete(path, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
