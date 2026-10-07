// Parallel-safe: these contracts only read files from the candidate repository.
public sealed class ReadmeDistributedVerificationContractTests
{
    [Xunit.Fact]
    public void Subsection_InHowItWorks_ContainsApprovedParagraphsInOrder()
    {
        var lines = ReadLines();
        var howItWorks = Array.IndexOf(lines, "## How it works");
        var designDecisions = Array.IndexOf(lines, "## Design decisions");
        var subsection = Xunit.Assert.Single(Enumerable.Range(0, lines.Length)
            .Where(index => lines[index] == "### Distributed verification"));
        Xunit.Assert.True(howItWorks >= 0, "README.md is missing How it works.");
        Xunit.Assert.True(designDecisions > howItWorks, "Design decisions must follow How it works.");
        Xunit.Assert.InRange(subsection, howItWorks + 1, designDecisions - 1);
        var section = ReadSection(lines, "### Distributed verification");
        Xunit.Assert.Equal(designDecisions - subsection - 1, section.Length);
        var text = "\n" + string.Join('\n', section) + "\n";
        string[] paragraphs =
        [
            "Acceptance gates are the throughput limit. They ran one at a time on the operator's workstation, which is also a daily-use machine. To relieve it, the gate can run allowlisted test lanes on remote Windows executors over SSH.",
            "For each lane, the host pushes the candidate commit to the executor's repository, queues the job, and polls its status and heartbeat until the TRX results are ready to fetch. A remote result is admitted only when the executor reports the commit, tree, main revision, test-filter hash, and acceptance-manifest identity the gate requested. A discrepancy, a lapsed heartbeat, or a transport failure returns the lane to local execution. [RemoteLaneCoordinator](src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCoordinator.cs) decides admission, and [SshRemoteLaneExecutor](src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SshRemoteLaneExecutor.cs) owns the transport.",
            "The first remote runs, on 2026-10-07, passed 396 and 400 tests with every binding field matched, and both goals landed."
        ];
        var previousEnd = 0;
        foreach (var paragraph in paragraphs)
        {
            Xunit.Assert.Contains("\n\n" + paragraph + "\n\n", text, StringComparison.Ordinal);
            var index = text.IndexOf(paragraph, StringComparison.Ordinal);
            Xunit.Assert.True(index >= previousEnd, "Approved paragraphs must appear in order.");
            previousEnd = index + paragraph.Length;
        }
    }

    [Xunit.Fact]
    public void Architecture_MermaidBlock_ContainsRemoteExecutorBranch()
    {
        var section = ReadSection(ReadLines(), "## Architecture");
        var opening = Array.IndexOf(section, "```mermaid");
        Xunit.Assert.True(opening >= 0, "Architecture is missing its mermaid fence.");
        var closing = Array.IndexOf(section, "```", opening + 1);
        Xunit.Assert.True(closing > opening, "Architecture is missing its closing mermaid fence.");
        Xunit.Assert.Contains("    Gate -->|allowlisted lanes over SSH| Executors[Remote executors]",
            section[(opening + 1)..closing]);
    }

    [Xunit.Fact]
    public void StatusAndLimits_ContainsDistributedVerificationLink()
    {
        var section = string.Join('\n', ReadSection(ReadLines(), "## Status and limits"));
        Xunit.Assert.Contains(
            "[Distributed verification](#distributed-verification)", section, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ExecutorHealthRecord_JsonFence_ContainsAcceptedOutcome()
    {
        var section = ReadSection(ReadLines(), "### Distributed verification");
        var introduction = Array.IndexOf(section,
            "An abridged executor-health record from the first accepted run:");
        Xunit.Assert.True(introduction >= 0, "The abridged executor-health introduction is missing.");
        var opening = Array.FindIndex(section, introduction + 1, line => !string.IsNullOrWhiteSpace(line));
        Xunit.Assert.True(opening >= 0, "The abridged executor-health record is missing.");
        Xunit.Assert.Equal("```json", section[opening]);
        var closing = Array.IndexOf(section, "```", opening + 1);
        Xunit.Assert.True(closing > opening, "The executor-health record is missing its closing fence.");
        var record = string.Join('\n', section[(opening + 1)..closing]);
        Xunit.Assert.Contains("\"outcome\":\"accepted\"", record, StringComparison.Ordinal);
    }

    private static string[] ReadLines()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "README.md");
        Xunit.Assert.True(File.Exists(path), "Document is missing: README.md");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static string[] ReadSection(string[] lines, string heading)
    {
        var start = Array.IndexOf(lines, heading);
        Xunit.Assert.True(start >= 0, $"README.md is missing heading '{heading}'.");
        var end = Array.FindIndex(lines, start + 1, line =>
            line.StartsWith("## ", StringComparison.Ordinal) ||
            line.StartsWith("### ", StringComparison.Ordinal));
        return lines[(start + 1)..(end < 0 ? lines.Length : end)];
    }
}
