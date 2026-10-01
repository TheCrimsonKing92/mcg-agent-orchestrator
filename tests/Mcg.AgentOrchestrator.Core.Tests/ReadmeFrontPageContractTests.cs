using System.Text.RegularExpressions;

// Parallel-safe: these contracts only read files from the candidate repository.
public sealed class ReadmeFrontPageContractTests
{
    [Xunit.Fact]
    public void TitleParagraphCreditsWorkerAgentsAndLinksToHistory()
    {
        var lines = ReadLines("README.md");
        var titleIndex = Xunit.Assert.Single(
            Enumerable.Range(0, lines.Length).Where(index =>
                lines[index].StartsWith("# ", StringComparison.Ordinal)));
        var paragraphLines = lines.Skip(titleIndex + 1)
            .SkipWhile(string.IsNullOrWhiteSpace)
            .TakeWhile(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Xunit.Assert.NotEmpty(paragraphLines);
        Xunit.Assert.Contains("own worker agents", string.Join('\n', paragraphLines), StringComparison.Ordinal);
        Xunit.Assert.Contains("#reading-the-history", MarkdownLinkTargets(paragraphLines));
    }

    [Xunit.Fact]
    public void ReadingTheHistoryDescribesSingleGoalAndMergeTrainLandings()
    {
        var lines = ReadLines("README.md");
        var start = Array.IndexOf(lines, "## Reading the history");
        Xunit.Assert.True(start >= 0, "README.md is missing the Reading the history heading.");
        var end = Array.FindIndex(lines, start + 1, line =>
            line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal));
        var section = string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
        Xunit.Assert.Contains("Integrate goal/", section, StringComparison.Ordinal);
        Xunit.Assert.Contains("merge train", section, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void HeadingsAndArchitectureDiagramMatchFrontPage()
    {
        var lines = ReadLines("README.md");
        Xunit.Assert.Equal(
            new[]
            {
                "Architecture", "How it works", "Design decisions", "Reading the history",
                "Status and limits", "Running it", "Projects"
            },
            lines.Where(line => line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => line[3..]));

        var diagramIndex = Xunit.Assert.Single(
            Enumerable.Range(0, lines.Length).Where(index =>
                Regex.IsMatch(lines[index], @"^ {0,3}`{3,}mermaid\s*$")));
        Xunit.Assert.InRange(diagramIndex,
            Array.IndexOf(lines, "## Architecture") + 1,
            Array.IndexOf(lines, "## How it works") - 1);
        var closingFence = Array.FindIndex(lines, diagramIndex + 1,
            line => Regex.IsMatch(line, @"^ {0,3}`{3,}\s*$"));
        Xunit.Assert.InRange(closingFence, diagramIndex + 1,
            Array.IndexOf(lines, "## How it works") - 1);
    }

    [Xunit.Theory]
    [Xunit.InlineData("README.md")]
    [Xunit.InlineData("docs/cli-reference.md")]
    public void RelativeMarkdownLinksResolveFromTheirDocument(string documentPath)
    {
        var root = VerifiedRepositoryRoot.Find();
        var path = Path.Combine(root, documentPath);
        var targets = MarkdownLinkTargets(ReadLines(documentPath)).ToArray();
        Xunit.Assert.NotEmpty(targets);

        foreach (var target in targets)
        {
            if (target.StartsWith('#') || Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                continue;

            var relativePath = Uri.UnescapeDataString(target.Split('#')[0]);
            Xunit.Assert.False(string.IsNullOrWhiteSpace(relativePath),
                $"{documentPath} has an empty relative link: {target}");
            Xunit.Assert.False(Path.IsPathRooted(relativePath),
                $"{documentPath} has a rooted link: {target}");
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relativePath));
            Xunit.Assert.True(File.Exists(resolved) || Directory.Exists(resolved),
                $"{documentPath} link '{target}' does not resolve: {resolved}");
        }
    }

    [Xunit.Fact]
    public void CliReferencePreservesOriginalHeadingTitles()
    {
        // Literal heading lines from README.md:43-529 at 1a3d6b214.
        string[] originalHeadings =
        [
            "## Verify",
            "## Windows Setup",
            "## Autonomous execution",
            "## Core Loop (manual verbs — fallback; prefer `conduct --loop`)",
            "## `next` / `next --full`",
            "## `goal`",
            "## `accept`",
            "## `stop`",
            "## `config`",
            "## Interactive Console",
            "## Full Verb Surface",
            "## Dispatch Internals",
            "## Agent Configuration",
            "## Model Providers",
            "### Ollama (Local Models)",
            "### Smoke Testing",
            "## Setup Doctor",
            "## Semantic Acceptance Judges / Model Functions",
            "## Monitoring",
            "## Autonomous Conductor",
            "### Autonomous execution",
            "### Lifecycle states",
            "### Autonomy policy presets",
            "### Integration-branch landing",
            "### Operator pager (Discord)",
            "## Next Slices"
        ];
        var referenceLines = ReadLines("docs/cli-reference.md");
        foreach (var heading in originalHeadings)
            Xunit.Assert.Contains(heading, referenceLines);

        var readmeLines = ReadLines("README.md");
        Xunit.Assert.DoesNotContain("# FUNDAMENTALS", readmeLines);
        Xunit.Assert.DoesNotContain("# ADVANCED", readmeLines);
    }

    [Xunit.Fact]
    public void DesignDecisionsLinkTheirEvidenceAndCaseStudy()
    {
        var root = VerifiedRepositoryRoot.Find();
        var section = ReadSection("README.md", "## Design decisions", "## ");
        var subsectionIndices = Enumerable.Range(0, section.Length)
            .Where(index => section[index].StartsWith("### ", StringComparison.Ordinal)).ToArray();
        Xunit.Assert.InRange(subsectionIndices.Length, 3, 5);

        for (var index = 0; index < subsectionIndices.Length; index++)
        {
            var end = index + 1 < subsectionIndices.Length ? subsectionIndices[index + 1] : section.Length;
            var links = MarkdownLinkTargets(section[subsectionIndices[index]..end]);
            Xunit.Assert.Contains(links, target =>
                target.StartsWith("docs/", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(root, target.Split('#')[0])));
        }

        Xunit.Assert.Contains("docs/case-studies/cohort-attribution-partitions.md",
            MarkdownLinkTargets(section));
    }

    [Xunit.Fact]
    public void InventoryCountsMatchDiscoveredProjects()
    {
        var root = VerifiedRepositoryRoot.Find();
        var section = ReadSection("README.md", "## Projects", "## ");
        var markerIndex = Array.IndexOf(section, "<!-- current-project-inventory:begin -->");
        Xunit.Assert.True(markerIndex >= 0, "README project inventory marker is missing.");
        var introduction = string.Join('\n', section[..markerIndex]);

        foreach (var (folder, description) in new[]
                 { ("src", "production projects"), ("tests", "test/support projects") })
        {
            var count = Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories)
                .Count(path => !path.Replace('\\', '/').Split('/').Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)));
            Xunit.Assert.True(count > 0, $"No projects discovered under {folder}.");
            Xunit.Assert.Contains($"`{folder}/**/*.csproj` ({count} {description})", introduction,
                StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void GoalReferenceNamesScoutDefaultAndExplicitFiveRoleOption()
    {
        var section = string.Join('\n', ReadSection("docs/cli-reference.md", "## `goal`", "## "));
        Xunit.Assert.Contains("automatic default is the Scout pipeline", section, StringComparison.Ordinal);
        Xunit.Assert.Contains("(Planner → Developer → Tester → Reviewer)", section, StringComparison.Ordinal);
        Xunit.Assert.Contains("`--pipeline five-role` to request five roles", section, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OperatorDiaryIsArchivedWithItsOriginalFirstLine()
    {
        var root = VerifiedRepositoryRoot.Find();
        Xunit.Assert.False(File.Exists(Path.Combine(root, "HANDOFF.md")));
        // First line of HANDOFF.md at 1a3d6b214.
        Xunit.Assert.Equal("# Orchestrator Handoff", ReadLines("docs/history/handoff-2026-09.md")[0]);
    }

    [Xunit.Fact]
    public void PublicationReservesRightsWithoutAddingALicense()
    {
        var section = string.Join('\n', ReadSection("README.md", "## Status and limits", "## "));
        Xunit.Assert.Contains("all rights reserved", section, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Empty(Directory.EnumerateFiles(VerifiedRepositoryRoot.Find())
            .Where(path => Path.GetFileName(path).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)));
    }

    private const string LandedStateStatus = @"**Current status (2026-10-01):** Remediated by later goals, with residual backlog items still open; the original record below is unchanged. Goal `81f85740` (landed as `cd5b6d28e` on 2026-07-27) made the conductor tick the only writer of goal state: recovery commands such as `retry`, `progress` and `verify-manual` now submit typed intents that the tick applies; see the [state model](../state-model.md) and the [operator runbook](../operator-runbook.md). Goal `38ae793c` (landed as `8d70e552f` on 2026-08-13) made branch integration conductor-owned, so workers no longer rebase; see Branch integration ownership in the [role capability matrix](../role-capability-matrix.md). Backlog items `b940ad5c`, `79ab3324` and `bb6f496a` remain open.";

    private const string DormantRedsStatus = @"**Current status (2026-10-01):** Remediated by later goals, with residual backlog items still open; the original record below is unchanged. The two red tests were fixed by goal `e42c2c9b` (landed as `087191daf`), as the Fix section records. Goal `4bc184e2` (landed as `70269c3a2` on 2026-07-23) moved acceptance gates out of the tick into background attempts, closing backlog item `fdb75163`, so one goal's gate no longer holds the tick. Goal `6251e612` (landed as `42db8d9b4` on 2026-07-30) added the clean-test baseline from backlog item `cca13692`, and goal `5d57fe95` (landed as `d910eaed3` on 2026-09-17) requires executed evidence before a candidate failure is called inherited from `main`; see [baseline attribution](../baseline-attribution-replay.md). Backlog items `76d770ac` and `4d63661a` remain open.";

    private const string SlotStarvationStatus = @"**Current status (2026-10-01):** Remediated by later goals, with residual backlog items still open; the original record below is unchanged. Goal `cee1d2da` (landed as `73482780a` on 2026-07-15) retries a slot lock that has no identifiable holder and keeps a lock-blocked gate scheduled on later ticks. Goal `5f4bb7de` (landed as `5da60a7d6` on 2026-07-15) makes the gate wait for its own test child to exit before the next build, and goal `6e08860f` (landed as `2f747b72f` on 2026-07-15) followed up on the same gate lock path. Goal `4bc184e2` (landed as `70269c3a2` on 2026-07-23) moved gates out of the tick, and goal `7f488dfa` (landed as `b3da492a0` on 2026-09-29) gives acceptance gate lanes shard permits ahead of focused evidence lanes; see [acceptance resource isolation](../acceptance-gate-resource-isolation.md). Backlog items `c31f7ca5` and `88e2fd9e` remain open, and goal `a5340f2b` named below was cancelled.";

    private const string CaseStudyCorrection = @"The first partition, c4abdb65 alone, finished at 04:40:08Z after 2,807 seconds, about 2.8 times the combined gate. The two runs used the same acceptance runner and verifier but tested different candidate trees: the combined gate tested the cohort's `main` revision merged with both goals, and the partition tested that `main` revision merged with c4abdb65 alone.";

    [Xunit.Theory]
    [Xunit.InlineData("docs/incidents/2026-07-15-landed-state-races.md",
        "2026-07-15 Landed-State Races and Shared-Git Collisions", LandedStateStatus,
        "Operator recoveries only (branch reset to accepted tip; cherry-pick completion; status-tool repairs). No structural fix landed yet.", true)]
    [Xunit.InlineData("docs/incidents/2026-07-15-dormant-reds-gate-monopoly.md",
        "2026-07-15 Dormant Red Tests Monopolize the Gate and Throttle the Board", DormantRedsStatus,
        "because gates run inline in the tick (`fdb75163`, unfixed)", false)]
    [Xunit.InlineData("docs/incidents/2026-07-14-acceptance-gate-slot-starvation.md",
        "2026-07-14 Acceptance Gate Slot Starvation", SlotStarvationStatus,
        "No completed fix is recorded here.", false)]
    public void IncidentRecordsOpenWithDatedStatusLineAndKeepOriginalText(
        string documentPath, string title, string expectedStatus, string preservedText, bool wholeLine)
    {
        var lines = ReadLines(documentPath);
        Xunit.Assert.Equal("# " + title, lines[0]);
        Xunit.Assert.Equal("", lines[1]);
        Xunit.Assert.Equal(expectedStatus, lines.Skip(1).First(line => !string.IsNullOrWhiteSpace(line)));
        Xunit.Assert.Equal(expectedStatus, lines[2]);
        Xunit.Assert.Equal("", lines[3]);

        var originalBody = lines.Skip(4).ToArray();
        if (wholeLine)
            Xunit.Assert.Contains(preservedText, originalBody);
        else
            Xunit.Assert.Contains(preservedText, string.Join('\n', originalBody), StringComparison.Ordinal);

        var path = Path.Combine(VerifiedRepositoryRoot.Find(), documentPath);
        var targets = MarkdownLinkTargets(new[] { lines[2] }).ToArray();
        Xunit.Assert.NotEmpty(targets);
        foreach (var target in targets)
        {
            if (target.StartsWith('#') || Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                continue;

            var relativePath = Uri.UnescapeDataString(target.Split('#')[0]);
            Xunit.Assert.False(string.IsNullOrWhiteSpace(relativePath),
                $"{documentPath} has an empty relative link: {target}");
            Xunit.Assert.False(Path.IsPathRooted(relativePath),
                $"{documentPath} has a rooted link: {target}");
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relativePath));
            Xunit.Assert.True(File.Exists(resolved),
                $"{documentPath} status link '{target}' does not resolve: {resolved}");
        }
    }

    [Xunit.Fact]
    public void CaseStudyDistinguishesTheCandidateTreesItCompared()
    {
        var document = string.Join('\n', ReadLines("docs/case-studies/cohort-attribution-partitions.md"));
        Xunit.Assert.DoesNotContain("on the same code", document, StringComparison.Ordinal);
        Xunit.Assert.Contains(CaseStudyCorrection, document, StringComparison.Ordinal);
        Xunit.Assert.Contains("after 2,807 seconds, about 2.8 times the combined gate",
            document, StringComparison.Ordinal);
        Xunit.Assert.Contains("| Total | 1,011 s | 2,807 s |", document, StringComparison.Ordinal);
        Xunit.Assert.Contains("These are different goals, so the comparison is not a controlled experiment.",
            document, StringComparison.Ordinal);
    }

    private static string[] ReadLines(string relativePath)
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), relativePath);
        Xunit.Assert.True(File.Exists(path), $"Document is missing: {relativePath}");
        return File.ReadAllText(path).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static string[] ReadSection(string relativePath, string heading, string nextHeadingPrefix)
    {
        var lines = ReadLines(relativePath);
        var start = Array.IndexOf(lines, heading);
        Xunit.Assert.True(start >= 0, $"{relativePath} is missing heading '{heading}'.");
        var end = Array.FindIndex(lines, start + 1,
            line => line.StartsWith(nextHeadingPrefix, StringComparison.Ordinal));
        return lines[(start + 1)..(end < 0 ? lines.Length : end)];
    }

    private static IEnumerable<string> MarkdownLinkTargets(IEnumerable<string> lines)
    {
        string? openFence = null;
        foreach (var line in lines)
        {
            var fence = Regex.Match(line, @"^ {0,3}(?<fence>`{3,}|~{3,})");
            if (fence.Success)
            {
                var delimiter = fence.Groups["fence"].Value;
                if (openFence is null)
                    openFence = delimiter;
                else if (delimiter[0] == openFence[0] && delimiter.Length >= openFence.Length &&
                         string.IsNullOrWhiteSpace(line[fence.Length..]))
                    openFence = null;
                continue;
            }

            if (openFence is not null)
                continue;

            foreach (Match match in Regex.Matches(line,
                         @"\[[^\]]*\]\(\s*(?:<(?<target>[^>]+)>|(?<target>[^\s)]+))(?:\s+""[^""]*"")?\s*\)"))
                yield return match.Groups["target"].Value;

            var definition = Regex.Match(line,
                @"^ {0,3}\[[^\]]+\]:\s*(?:<(?<target>[^>]+)>|(?<target>\S+))");
            if (definition.Success)
                yield return definition.Groups["target"].Value;
        }
    }
}
