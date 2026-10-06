using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliAuthorDraftExtractionParityTests
{
    [Theory]
    [InlineData("draft")]
    [InlineData("stale")]
    [InlineData("unparseable")]
    [InlineData("failing-check")]
    public void Extraction_preserves_stdout_stderr_exit_and_receipt(string kind)
    {
        // Fixture uses a unique database and fake repository/model; no shared resources.
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown;
        if (kind == "failing-check") markdown = markdown.Replace("docs/role-capability-matrix.md:1", "missing/file.cs:1");
        var json = kind switch
        {
            "stale" => JsonSerializer.Serialize(new
            {
                kind = "stale", reason = "Capability exists.",
                evidenceReferences = new[] { "docs/role-capability-matrix.md:1" }
            }),
            "unparseable" => "not JSON",
            _ => JsonSerializer.Serialize(new { kind = "draft", markdown })
        };
        var seams = new AuthorBriefDraftSeams((_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, json, "fake stderr")), fixture.Repository);
        string[] args = ["author-draft", fixture.Item.Id[..8]];
        using var beforeOut = new StringWriter();
        using var beforeError = new StringWriter();
        var beforeCode = AuthorDraftExtractionBaseline.Run(args, fixture.Workspace, seams, beforeOut, beforeError);
        var beforeReceipt = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Drafts, "*.receipt.json")));
        foreach (var path in Directory.GetFiles(fixture.Drafts)) File.Delete(path);
        using var afterOut = new StringWriter();
        using var afterError = new StringWriter();
        var afterCode = CliAuthorDraftCommand.Run(args, fixture.Workspace, seams, afterOut, afterError);
        var afterReceipt = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Drafts, "*.receipt.json")));

        Assert.Equal(kind == "stale" ? 2 : kind == "draft" ? 0 : 1, beforeCode);
        Assert.Equal(beforeCode, afterCode);
        Assert.Equal(Normalize(beforeOut.ToString()), Normalize(afterOut.ToString()));
        Assert.Equal(beforeError.ToString(), afterError.ToString());
        Assert.Equal(beforeReceipt, kind == "unparseable" ? StripRawOutputFields(afterReceipt) : afterReceipt);
    }

    private static string Normalize(string text) =>
        Regex.Replace(text, "[0-9a-f]{8}-[0-9]{8}T[0-9]{9}-[0-9a-f]{32}", "<draft-stem>");

    private static string StripRawOutputFields(string receipt) =>
        Regex.Replace(receipt, "\\s*,\\s*\"rawOutputPath\"[^\\r\\n]*\\s*,\\s*\"rawOutputBytes\"\\s*:\\s*\\d+", "");
}
