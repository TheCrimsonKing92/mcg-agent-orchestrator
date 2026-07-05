using System.Text.RegularExpressions;

public sealed class AgentHarnessDocsDriftTests
{
    private static readonly SharedSection[] SharedSections =
    [
        new("output-discipline", "## Output Discipline"),
        new("retry-and-loop-control", "## Retry and Loop Control"),
        new("repository-rules", "## Repository Rules"),
        new("architecture-and-design-discipline", "## Architecture & Design Discipline"),
        new("specification-discipline", "## Specification Discipline"),
        new("diagnosis-discipline", "## Diagnosis Discipline"),
        new("dashboard-dogfood-boundary", "## Dashboard / Dogfood Boundary"),
        new("operating-the-goal-loop", "## Operating the goal loop"),
        new("safety", "## Safety"),
        new("evidence", "## Evidence")
    ];

    [Xunit.Fact(DisplayName = "Harness_docs_counterpart_contracts_stay_in_sync")]
    public void HarnessDocsCounterpartContractsStayInSync()
    {
        var docs = ReadHarnessDocs();

        Validate(docs.Agents, docs.Claude);
    }

    [Xunit.Fact(DisplayName = "Harness_docs_drift_check_rejects_unpaired_contract_or_anchor_edits")]
    public void HarnessDocsDriftCheckRejectsUnpairedContractOrAnchorEdits()
    {
        var docs = ReadHarnessDocs();

        var contractDrift = docs.Claude.Replace(
            "Owns: Claude Code harness operating guidance.",
            "Owns: Edited Claude harness guidance.",
            StringComparison.Ordinal);
        var contractException = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(docs.Agents, contractDrift));
        Xunit.Assert.True(contractException.Message.Contains("CLAUDE.md contract Owns", StringComparison.Ordinal));

        var anchorDrift = docs.Claude.Replace(
            "- evidence",
            "- evidence\n- invented-shared-discipline",
            StringComparison.Ordinal);
        var anchorException = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(docs.Agents, anchorDrift));
        Xunit.Assert.True(anchorException.Message.Contains("Shared anchor lists must match", StringComparison.Ordinal));
    }

    private static void Validate(string agents, string claude)
    {
        RequireContains(agents, "docs/operator-runbook.md", "AGENTS.md must reference docs/operator-runbook.md.");
        RequireContains(claude, "docs/operator-runbook.md", "CLAUDE.md must reference docs/operator-runbook.md.");

        var agentsContract = ParseContract("AGENTS.md", agents);
        var claudeContract = ParseContract("CLAUDE.md", claude);

        RequireEqual(
            "shared repository discipline plus Codex harness operating guidance.",
            agentsContract.Owns,
            "AGENTS.md contract Owns changed.");
        RequireEqual(
            "CLAUDE.md owns Claude Code harness operating guidance and links back to AGENTS.md for shared discipline.",
            agentsContract.Counterpart,
            "AGENTS.md contract Counterpart changed.");
        RequireEqual(
            "Claude Code harness operating guidance.",
            claudeContract.Owns,
            "CLAUDE.md contract Owns changed.");
        RequireEqual(
            "AGENTS.md owns shared repository discipline plus Codex harness operating guidance.",
            claudeContract.Counterpart,
            "CLAUDE.md contract Counterpart changed.");
        RequireEqual(agentsContract.Rule, claudeContract.Rule, "Counterpart contract rules must match.");
        RequireContains(agentsContract.Rule, "update BOTH AGENTS.md and CLAUDE.md", "Contract rule must require updating both files.");
        RequireContains(agentsContract.Rule, "move the content to docs/operator-runbook.md or another shared home", "Contract rule must name the shared-home escape hatch.");

        var expectedAnchors = SharedSections.Select(section => section.Anchor).ToArray();
        RequireSequenceEqual(expectedAnchors, agentsContract.SharedAnchors, "AGENTS.md shared-anchor list changed.");
        RequireSequenceEqual(agentsContract.SharedAnchors, claudeContract.SharedAnchors, "Shared anchor lists must match.");

        foreach (var section in SharedSections)
        {
            var marker = $"<!-- shared-discipline:{section.Anchor} -->";
            RequireContains(agents, marker, $"AGENTS.md must carry shared marker {marker}.");
            RequireContains(agents, section.Heading, $"AGENTS.md must carry shared heading {section.Heading}.");
            RequireDoesNotContain(claude, marker, $"CLAUDE.md must not duplicate shared marker {marker}.");
            RequireDoesNotContain(claude, section.Heading, $"CLAUDE.md must not duplicate shared heading {section.Heading}.");
        }
    }

    private static HarnessDocs ReadHarnessDocs()
    {
        var root = FindRepositoryRoot();
        return new HarnessDocs(
            File.ReadAllText(Path.Combine(root, "AGENTS.md")),
            File.ReadAllText(Path.Combine(root, "CLAUDE.md")));
    }

    private static HarnessContract ParseContract(string path, string text)
    {
        var matches = Regex.Matches(
            text,
            @"<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->(?<body>.*?)<!-- HARNESS-COUNTERPART-CONTRACT:END -->",
            RegexOptions.Singleline);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"{path} must contain exactly one counterpart contract block; found {matches.Count}.");
        }

        var body = matches[0].Groups["body"].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var owns = ParseField(path, lines, "Owns:");
        var counterpart = ParseField(path, lines, "Counterpart:");
        var rule = ParseField(path, lines, "Rule:");
        var sharedAnchorsIndex = Array.FindIndex(lines, line => line.Equals("Shared anchors:", StringComparison.Ordinal));
        if (sharedAnchorsIndex < 0)
        {
            throw new InvalidOperationException($"{path} counterpart contract must include Shared anchors.");
        }

        var anchors = lines
            .Skip(sharedAnchorsIndex + 1)
            .TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToArray();
        if (anchors.Length == 0)
        {
            throw new InvalidOperationException($"{path} counterpart contract must list shared anchors.");
        }

        return new HarnessContract(owns, counterpart, rule, anchors);
    }

    private static string ParseField(string path, IReadOnlyList<string> lines, string field)
    {
        var line = lines.SingleOrDefault(line => line.StartsWith(field, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException($"{path} counterpart contract must include {field}");
        }

        return line[field.Length..].Trim();
    }

    private static string FindRepositoryRoot()
    {
        var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void RequireContains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireDoesNotContain(string value, string unexpected, string message)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private static void RequireSequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string message)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    private sealed record SharedSection(string Anchor, string Heading);

    private sealed record HarnessContract(
        string Owns,
        string Counterpart,
        string Rule,
        IReadOnlyList<string> SharedAnchors);

    private sealed record HarnessDocs(string Agents, string Claude);
}
