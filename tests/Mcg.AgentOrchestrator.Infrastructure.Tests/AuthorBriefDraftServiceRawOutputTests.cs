using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its GUID root and uses fake repository/process seams.
public sealed class AuthorBriefDraftServiceRawOutputTests
{
    [Fact]
    public void Unparseable_output_is_saved_beside_receipt_with_path_and_byte_count()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        const string output = "not JSON";

        Assert.Equal(1, Run(fixture, output).ExitCode);

        var receiptPath = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.receipt.json"));
        var rawPath = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.raw.txt"));
        Assert.Equal(receiptPath.Replace(".receipt.json", ".raw.txt"), rawPath);
        Assert.Equal(output, File.ReadAllText(rawPath));
        using var receipt = fixture.Receipt();
        Assert.Equal("unparseable", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal(rawPath, receipt.RootElement.GetProperty("rawOutputPath").GetString());
        Assert.Equal(new FileInfo(rawPath).Length,
            receipt.RootElement.GetProperty("rawOutputBytes").GetInt64());
    }

    [Fact]
    public void Oversized_output_keeps_only_the_final_65536_bytes()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var output = new string('a', 4464) + new string('b', 65536);

        Assert.Equal(1, Run(fixture, output).ExitCode);

        var rawPath = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.raw.txt"));
        Assert.Equal(65536L, new FileInfo(rawPath).Length);
        Assert.Equal(output[^65536..], File.ReadAllText(rawPath));
        using var receipt = fixture.Receipt();
        Assert.Equal(65536, receipt.RootElement.GetProperty("rawOutputBytes").GetInt32());
    }

    [Fact]
    public void Utf8_tail_preserves_exact_bytes_even_when_it_splits_a_character()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var output = new string('\u20ac', 23334);
        var bytes = Encoding.UTF8.GetBytes(output);

        Assert.Equal(1, Run(fixture, output).ExitCode);

        var rawPath = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.raw.txt"));
        Assert.Equal(bytes[^65536..], File.ReadAllBytes(rawPath));
        using var receipt = fixture.Receipt();
        Assert.Equal(65536, receipt.RootElement.GetProperty("rawOutputBytes").GetInt32());
    }

    [Fact]
    public void Empty_output_writes_a_zero_byte_raw_file()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();

        Assert.Equal(1, Run(fixture, "").ExitCode);

        var rawPath = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.raw.txt"));
        Assert.Equal(0L, new FileInfo(rawPath).Length);
        using var receipt = fixture.Receipt();
        Assert.Equal(0, receipt.RootElement.GetProperty("rawOutputBytes").GetInt32());
    }

    [Theory]
    [InlineData("draft", 0)]
    [InlineData("stale", 2)]
    [InlineData("failed", 1)]
    public void Other_round_outcomes_omit_raw_files_and_receipt_fields(string kind, int expectedCode)
    {
        using var unparseable = new CliAuthorDraftCommandTests.Fixture();
        Assert.Equal(1, Run(unparseable, "not JSON").ExitCode);
        Assert.Single(Directory.GetFiles(unparseable.Drafts, "*.raw.txt"));
        using var unparseableReceipt = unparseable.Receipt();
        Assert.True(unparseableReceipt.RootElement.TryGetProperty("rawOutputPath", out _));
        Assert.True(unparseableReceipt.RootElement.TryGetProperty("rawOutputBytes", out _));

        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var output = kind switch
        {
            "draft" => JsonSerializer.Serialize(new
            {
                kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
            }),
            "stale" => JsonSerializer.Serialize(new
            {
                kind = "stale", reason = "Capability exists.",
                evidenceReferences = new[] { "docs/role-capability-matrix.md:1" }
            }),
            _ => "not JSON"
        };

        Assert.Equal(expectedCode, Run(fixture, output, kind == "failed" ? 17 : 0).ExitCode);

        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.raw.txt"));
        using var receipt = fixture.Receipt();
        Assert.Equal(kind, receipt.RootElement.GetProperty("kind").GetString());
        Assert.False(receipt.RootElement.TryGetProperty("rawOutputPath", out _));
        Assert.False(receipt.RootElement.TryGetProperty("rawOutputBytes", out _));
    }

    private static AuthorBriefDraftOutcome Run(CliAuthorDraftCommandTests.Fixture fixture,
        string output, int exitCode = 0) => AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
        new((_, _) => Task.FromResult(new WorkerProcessRunResult(exitCode, output, "fake stderr")),
            fixture.Repository), fixture.Output, fixture.Error);
}
