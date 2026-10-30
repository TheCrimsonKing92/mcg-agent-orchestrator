using Mcg.AgentOrchestrator.Infrastructure;
using Grade = Mcg.AgentOrchestrator.Infrastructure.CrosswalkFocusedEvidence.SelectionGrade;

// Parallel-safe: grading and manifest parsing use only immutable, in-memory inputs.
public sealed class CrosswalkFocusedEvidenceTests
{
    internal const string File = "src/game/types/facade.test.ts";
    internal const string Manifest = """
        {"checks":[
          {"name":"install","type":"command","command":"npm","arguments":["ci"]},
          {"name":"crosswalk selection","type":"crosswalk-test","project":".","timeoutMinutes":3}
        ]}
        """;

    [Fact]
    public void Grade_CtrfSelections_IndependentlyReportsPassAbsentAndFailure()
    {
        const string failedFile = "src/game/types/broken.test.ts";
        var read = new NormalizedTestResultSource().ReadJson(CrosswalkAcceptanceFixture.Report(
            (File + "::first", "passed"), (File + "::second", "passed"),
            (failedFile + "::first", "passed"), (failedFile + "::second", "failed")));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        Assert.Equal(4, read.Records.Count);

        var grades = new[] { File, File + "::second", "src/absent.test.ts", failedFile }
            .Select(selector => CrosswalkFocusedEvidence.Grade(read.Records, selector)).ToArray();

        Assert.Equal([Grade.Satisfied, Grade.Satisfied, Grade.NotDemonstrated, Grade.Failed], grades);
    }

    [Theory]
    [InlineData("skipped", "NotDemonstrated")]
    [InlineData("failed", "Failed")]
    [InlineData("passed", "Satisfied")]
    public void Grade_SingleResult_RequiresExecutedPass(string status, string expected)
    {
        var read = new NormalizedTestResultSource().ReadJson(
            CrosswalkAcceptanceFixture.Report((File + "::case", status)));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        Assert.Single(read.Records);

        Assert.Equal(expected, CrosswalkFocusedEvidence.Grade(read.Records, File).ToString());
    }

    [Fact]
    public void Parse_MultipleTypedChecks_PreservesFirstDeclarationAndAnyAlias()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"checks":[
              {"name":"shell command","type":"command","command":"npx","arguments":["crosswalk"]},
              {"name":"first","type":"CROSSWALK-TEST","project":"client","arguments":["original"],"timeoutMinutes":7},
              {"name":"second","type":"crosswalk-test","project":"server"}
            ]}
            """);
        Assert.True(CrosswalkFocusedEvidence.Applies(settings));
        var check = settings.DeclaredCrosswalkTestCheck;
        Assert.NotNull(check);
        Assert.Equal("first", check.Name);
        Assert.Equal("client", check.Project);
        Assert.Equal(["original"], check.Arguments);
        Assert.Equal(7, check.TimeoutMinutes);
        foreach (var alias in new[] { ".", "client", "second", "arbitrary alias" })
        {
            Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(alias, settings, out var project));
            Assert.Equal("first", project);
        }
        Assert.False(CrosswalkFocusedEvidence.TryResolveProject(" ", settings, out _));
        Assert.Null(AcceptanceGateEngineSettings.Parse("""
            {"checks":[{"type":"command","command":"npx","arguments":["crosswalk"]}]}
            """).DeclaredCrosswalkTestCheck);
    }
}
