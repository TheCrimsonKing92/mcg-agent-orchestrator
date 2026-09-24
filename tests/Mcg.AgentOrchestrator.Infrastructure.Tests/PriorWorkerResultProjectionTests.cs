using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PriorWorkerResultProjectionTests
{
    private const string FixtureName = "8257cb24-ba064746-20260924003957.out.txt";

    [Fact]
    public void HistoricalDeveloperAuditKeepsAllFiftyFourEntries()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WorkerOutput", FixtureName));

        var projection = Project(output);

        for (var index = 1; index <= 54; index++)
            Assert.Contains($"audit_{index:00}:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("WORKER_RESULT:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Parsed, projection.Validation);
    }

    [Fact]
    public void MalformedHistoricalAuditRetainsTheCompleteLocatedBlock()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WorkerOutput", FixtureName));
        var malformed = output.Replace("skills: verification-before-completion, dotnet-windows-build-hygiene\n",
            string.Empty, StringComparison.Ordinal).Replace(
            "skills: verification-before-completion, dotnet-windows-build-hygiene\r\n",
            string.Empty, StringComparison.Ordinal);

        var projection = Project(malformed);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("missing field(s): skills", projection.Content, StringComparison.Ordinal);
        for (var index = 1; index <= 54; index++)
            Assert.Contains($"audit_{index:00}:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedProjectionBoundsProseOutsideTheLocatedBlock()
    {
        var output = new string('p', 8000) + "\nWORKER_RESULT:\n" +
            "files: none\ncommands: none\ntests: deferred - focused receipt needed\n" +
            "blockers: none\naudit_27: middle survives\nEND_WORKER_RESULT\n" +
            new string('s', 8000);

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("audit_27: middle survives", projection.Content, StringComparison.Ordinal);
        Assert.Contains("chars omitted", projection.Content, StringComparison.Ordinal);
        Assert.True(projection.Content.Length < 5000, $"projected chars={projection.Content.Length}");
    }

    [Fact]
    public void EndMarkerAtEndKeepsBlankLinesInsideTheCompleteBlock()
    {
        const string output = "WORKER_RESULT:\nfiles: none\n\naudit_27: middle survives\nEND_WORKER_RESULT";

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains(output, projection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingEchoedOpenerDoesNotHideTheLastCompleteBlock()
    {
        const string output = "WORKER_RESULT:\nfiles: none\naudit_27: middle survives\n" +
            "END_WORKER_RESULT\nThe worker later echoed\nWORKER_RESULT:\nfiles: none";

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("audit_27: middle survives", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
    }

    private static WorkerVerificationEvidence.ContextProjection Project(string output)
    {
        var task = new TaskSpec(TaskId.New(), "Prior Developer", AgentRole.Developer);
        var verification = new TaskVerificationRecord("test", @"C:\tmp", 0,
            output, string.Empty, DateTimeOffset.UtcNow, FullStandardOutput: output);
        return WorkerVerificationEvidence.ProjectStandardOutputForContextWithValidation(task, verification);
    }
}
