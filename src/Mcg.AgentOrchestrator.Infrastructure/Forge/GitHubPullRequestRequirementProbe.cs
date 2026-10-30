using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads classic protection and active rules for the branch using the existing gh login.</summary>
public sealed class GitHubPullRequestRequirementProbe(IForgeProcessRunner runner) : IPullRequestRequirementProbe
{
    public PullRequestRequirement Probe(string repositoryRoot, string branch)
    {
        try
        {
            return ProbeCore(repositoryRoot, branch);
        }
        catch (JsonException)
        {
            return PullRequestRequirement.Unknown("unreadable gh api response");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PullRequestRequirement.Unknown($"forge probe failed: {exception.Message}");
        }
    }

    private PullRequestRequirement ProbeCore(string repositoryRoot, string branch)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var environment = new Dictionary<string, string>
        {
            ["GIT_CEILING_DIRECTORIES"] = Directory.GetParent(root)?.FullName ?? root
        };
        var origin = runner.Run("git", ["remote", "get-url", "origin"], root, environment, TimeSpan.FromSeconds(10));
        if (!origin.Started || origin.TimedOut || origin.ExitCode != 0 || string.IsNullOrWhiteSpace(origin.StandardOutput))
            return PullRequestRequirement.Unknown("no origin remote");
        if (!GitHubRemote.TryParse(origin.StandardOutput.Trim(), out var repository))
            return PullRequestRequirement.Unknown("origin remote is not a supported GitHub repository URL");

        var prefix = $"repos/{repository}";
        var escapedBranch = Uri.EscapeDataString(branch);
        var protection = Api($"{prefix}/branches/{escapedBranch}/protection", root);
        var unavailable = Unavailable(protection);
        if (unavailable is not null) return unavailable;
        var protectionReadable = protection.ExitCode == 0;
        if (protectionReadable)
        {
            using var json = JsonDocument.Parse(protection.StandardOutput);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return PullRequestRequirement.Unknown("unreadable gh api response");
            if (json.RootElement.TryGetProperty("required_pull_request_reviews", out var reviews) &&
                reviews.ValueKind == JsonValueKind.Object)
                return PullRequestRequirement.Required("branch protection requires pull request reviews");
            if (reviews.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                return PullRequestRequirement.Unknown("unreadable gh api response");
        }
        else
        {
            if (Status(protection) is not "403" and not "404")
                return PullRequestRequirement.Unknown($"gh api branch protection failed ({Status(protection)})");
            protectionReadable = Status(protection) == "404" &&
                (protection.StandardOutput + protection.StandardError).Contains("Branch not protected", StringComparison.OrdinalIgnoreCase);
        }

        // The branch-rules endpoint filters out disabled/evaluate rulesets and includes organization rules.
        for (var page = 1; page <= 10; page++)
        {
            var endpoint = $"{prefix}/rules/branches/{escapedBranch}?per_page=100" + (page == 1 ? "" : $"&page={page}");
            var rules = Api(endpoint, root);
            unavailable = Unavailable(rules);
            if (unavailable is not null) return unavailable;
            if (rules.ExitCode != 0)
                return PullRequestRequirement.Unknown($"gh api branch rules failed ({Status(rules)})");
            using var json = JsonDocument.Parse(rules.StandardOutput);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return PullRequestRequirement.Unknown("unreadable gh api response");
            foreach (var rule in json.RootElement.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                    return PullRequestRequirement.Unknown("unreadable gh api response");
                if (type.GetString() == "pull_request")
                    return PullRequestRequirement.Required("an active branch ruleset requires pull requests");
            }
            if (json.RootElement.GetArrayLength() < 100)
                return protectionReadable
                    ? PullRequestRequirement.NotRequired("branch protection and active rules have no pull request requirement")
                    : PullRequestRequirement.Unknown($"branch protection unreadable ({Status(protection)}); rules show no pull_request rule");
        }
        return PullRequestRequirement.Unknown("branch rules exceeded the probe page limit");
    }

    private ForgeProcessResult Api(string endpoint, string root) => runner.Run("gh",
        ["api", "-H", "Accept: application/vnd.github+json", "--hostname", "github.com", endpoint],
        root, new Dictionary<string, string>(), TimeSpan.FromSeconds(20));

    private static PullRequestRequirement? Unavailable(ForgeProcessResult result)
    {
        if (!result.Started) return PullRequestRequirement.Unknown("gh is not installed or not on PATH");
        if (result.TimedOut) return PullRequestRequirement.Unknown("gh api timed out");
        if (result.ExitCode == 4 || Status(result) == "401")
            return PullRequestRequirement.Unknown("gh is not logged in");
        return null;
    }

    private static string Status(ForgeProcessResult result)
    {
        var match = Regex.Match(result.StandardError + result.StandardOutput, @"HTTP\s+(\d{3})", RegexOptions.CultureInvariant);
        return match.Success ? $"{match.Groups[1].Value}" : $"exit {result.ExitCode}";
    }
}
