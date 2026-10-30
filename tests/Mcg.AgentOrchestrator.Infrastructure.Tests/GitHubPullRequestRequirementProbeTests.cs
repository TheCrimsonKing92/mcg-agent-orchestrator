using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test uses its own scripted runner; no git, gh or network is executed.
public sealed class GitHubPullRequestRequirementProbeTests
{
    [Theory]
    [InlineData("https://github.com/octo/sample.git")]
    [InlineData("git@github.com:octo/sample.git")]
    [InlineData("ssh://git@github.com/octo/sample.git")]
    public void ClassicProtection_RequiresPullRequestsWithoutQueryingRules(string remote)
    {
        var runner = new ScriptedRunner(remote);
        runner.Api("repos/octo/sample/branches/release%2Fnext/protection",
            Ok("""{"required_pull_request_reviews":{"required_approving_review_count":1}}"""));

        var result = new GitHubPullRequestRequirementProbe(runner).Probe(runner.Root, "release/next");

        Assert.Equal(PullRequestRequirementKind.Required, result.Kind);
        Assert.Contains("branch protection", result.Reason);
        runner.AssertCompleted();
    }

    [Fact]
    public void ApplicableRuleset_WithPullRequestRule_RequiresPullRequests()
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, Unprotected);
        runner.Api(Rules, Ok("""[{"type":"required_status_checks"},{"type":"pull_request","ruleset_id":42}]"""));

        var result = Probe(runner);

        Assert.Equal(PullRequestRequirementKind.Required, result.Kind);
        Assert.Contains("ruleset", result.Reason);
        runner.AssertCompleted();
    }

    [Theory]
    [InlineData("unprotected")]
    [InlineData("empty-protection")]
    [InlineData("null-reviews")]
    public void NeitherProtectionNorRulesRequirePullRequests_ReturnsNotRequired(string protection)
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, protection switch
        {
            "unprotected" => Unprotected,
            "empty-protection" => Ok("{}"),
            _ => Ok("""{"required_pull_request_reviews":null}""")
        });
        runner.Api(Rules, Ok("[]"));

        var result = Probe(runner);

        Assert.Equal(PullRequestRequirementKind.NotRequired, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        runner.AssertCompleted();
    }

    [Theory]
    [InlineData("missing", "gh is not installed")]
    [InlineData("unauthorized", "gh is not logged in")]
    [InlineData("login-exit", "gh is not logged in")]
    [InlineData("timeout", "gh api timed out")]
    [InlineData("server-error", "500")]
    [InlineData("malformed-json", "unreadable gh api response")]
    [InlineData("wrong-shape", "unreadable gh api response")]
    public void UnavailableProtection_ReturnsUnknownWithActionableReason(string scenario, string expectedReason)
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, scenario switch
        {
            "missing" => new(false, -1, "", "file not found"),
            "unauthorized" => Error(401, "Bad credentials"),
            "login-exit" => new(true, 4, "", "authentication required"),
            "timeout" => new(true, -1, "", "", TimedOut: true),
            "server-error" => Error(500, "Internal Server Error"),
            "malformed-json" => Ok("not json"),
            _ => Ok("[]")
        });

        var result = Probe(runner);

        Assert.Equal(PullRequestRequirementKind.Unknown, result.Kind);
        Assert.Contains(expectedReason, result.Reason);
        runner.AssertCompleted();
    }

    [Theory]
    [InlineData("https://gitlab.com/octo/sample.git")]
    [InlineData("git@gitlab.com:octo/sample.git")]
    [InlineData("https://github.com.example.org/octo/sample.git")]
    public void NonGitHubRemote_ReturnsUnknownWithoutCallingGh(string remote)
    {
        var runner = new ScriptedRunner(remote);

        var result = Probe(runner);

        Assert.Equal(PullRequestRequirementKind.Unknown, result.Kind);
        Assert.Contains("GitHub", result.Reason);
        runner.AssertCompleted();
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    [InlineData(404, true)]
    public void UnreadableProtection_UsesPositiveRulesButNeverInfersNotRequired(int status, bool requiredRule)
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, Error(status, status == 403 ? "Forbidden" : "Not Found"));
        runner.Api(Rules, Ok(requiredRule ? """[{"type":"pull_request"}]""" : "[]"));

        var result = Probe(runner);

        Assert.Equal(requiredRule ? PullRequestRequirementKind.Required : PullRequestRequirementKind.Unknown, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        if (!requiredRule) Assert.Contains(status.ToString(), result.Reason);
        runner.AssertCompleted();
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("malformed-json")]
    [InlineData("wrong-shape")]
    [InlineData("malformed-rule")]
    public void RulesFailure_IsUnknownRatherThanNotRequired(string scenario)
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, Unprotected);
        runner.Api(Rules, scenario switch
        {
            "failure" => Error(500, "Internal Server Error"),
            "malformed-json" => Ok("not json"),
            "wrong-shape" => Ok("{}"),
            _ => Ok("[{}]")
        });

        var result = Probe(runner);

        Assert.Equal(PullRequestRequirementKind.Unknown, result.Kind);
        Assert.Contains(scenario == "failure" ? "500" : "unreadable", result.Reason);
        runner.AssertCompleted();
    }

    [Fact]
    public void FullRulesPage_ReadsNextPageBeforeDeciding()
    {
        var runner = new ScriptedRunner();
        runner.Api(Protection, Unprotected);
        runner.Api(Rules, Ok(JsonSerializer.Serialize(Enumerable.Repeat(new { type = "required_status_checks" }, 100))));
        runner.Api(Rules + "&page=2", Ok("""[{"type":"pull_request"}]"""));

        Assert.Equal(PullRequestRequirementKind.Required, Probe(runner).Kind);

        runner.AssertCompleted();
    }

    private const string Protection = "repos/octo/sample/branches/main/protection";
    private const string Rules = "repos/octo/sample/rules/branches/main?per_page=100";
    private static ForgeProcessResult Unprotected => Error(404, "Branch not protected");
    private static ForgeProcessResult Ok(string json) => new(true, 0, json, "");
    private static ForgeProcessResult Error(int status, string message) =>
        new(true, 1, JsonSerializer.Serialize(new { message }), $"gh: {message} (HTTP {status})");
    private static PullRequestRequirement Probe(ScriptedRunner runner) =>
        new GitHubPullRequestRequirementProbe(runner).Probe(runner.Root, "main");

    private sealed class ScriptedRunner : IForgeProcessRunner
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forge-probe", Guid.NewGuid().ToString("N")));
        private readonly List<(string Executable, string[] Arguments, ForgeProcessResult Result)> expected = [];
        private readonly List<(string Executable, string[] Arguments, string Root, Dictionary<string, string> Environment)> calls = [];

        public ScriptedRunner(string remote = "https://github.com/octo/sample.git") =>
            expected.Add(("git", ["remote", "get-url", "origin"], Ok(remote + Environment.NewLine)));

        public void Api(string endpoint, ForgeProcessResult result) => expected.Add(("gh",
            ["api", "-H", "Accept: application/vnd.github+json", "--hostname", "github.com", endpoint], result));

        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            calls.Add((fileName, arguments.ToArray(), workingDirectory, environment.ToDictionary(pair => pair.Key, pair => pair.Value)));
            if (calls.Count > expected.Count) throw new InvalidOperationException("Unexpected forge process invocation.");
            return expected[calls.Count - 1].Result;
        }

        public void AssertCompleted()
        {
            // Check outside Probe's catch boundary so an unexpected invocation cannot masquerade as Unknown.
            Assert.Equal(expected.Count, calls.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].Executable, calls[index].Executable);
                Assert.Equal(expected[index].Arguments, calls[index].Arguments);
                Assert.Equal(Root, calls[index].Root);
                if (calls[index].Executable == "git")
                {
                    var setting = Assert.Single(calls[index].Environment);
                    Assert.Equal("GIT_CEILING_DIRECTORIES", setting.Key);
                    Assert.Equal(Directory.GetParent(Root)!.FullName, setting.Value);
                }
                else Assert.Empty(calls[index].Environment);
            }
        }
    }
}
