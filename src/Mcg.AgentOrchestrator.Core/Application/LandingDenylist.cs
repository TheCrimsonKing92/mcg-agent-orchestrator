using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Observations only: this type has no authority to admit or hold a landing.
public sealed class LandingDenylist
{
    public static LandingDenylist BuiltInDefault { get; } = new([
        new("process-launch", "Starts, contains, stops or reaps worker and child processes.", ["src/*/Processes/WorkerProcessJobs*.cs", "src/*/Processes/OwnedProcessGroup.cs", "src/*/Processes/ProcessSpawnGuard.cs", "src/*/Processes/SpawnRegistry.cs", "src/*/Processes/*Reaper.cs", "src/*/Processes/ProcessTreeGuiSuppression*.cs", "src/*/Processes/DispatchProcessHost.cs", "src/*/Processes/WorkerProcessRunner.cs", "src/*/Processes/ChildConsoleLaunchPolicy.cs", "src/*/Processes/ProtectedProcessIdentity.cs", "src/*/Processes/GracefulDispatchDetacher.cs", "src/*/Processes/WorkerShell.cs"]),
        new("provider-login", "Reads or passes provider sign-in material to workers.", ["src/Mcg.AgentOrchestrator.Execution/Workers/ClaudeCredentialSource.cs"]),
        new("git-mutation", "Moves main, the integration branch or the remote mirror.", ["src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor*.cs", "src/Mcg.AgentOrchestrator.App/Orchestration/RemoteGitMirror.cs", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.GitOps.cs"]),
        new("worker-sandbox", "Sets worker sandbox policy and integrity levels.", ["src/**/*WorkerSandbox*.cs"]),
        new("landing-policy", "Decides what lands, who may approve it, and this denylist itself.", ["src/Mcg.AgentOrchestrator.Core/Application/LandingPolicy.cs", "src/Mcg.AgentOrchestrator.Core/Application/LandingDecision.cs", "src/Mcg.AgentOrchestrator.Core/Application/LandingDenylist*.cs", "src/Mcg.AgentOrchestrator.Core/Application/RepositoryOwnershipMap.cs", "src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier.OwnerProtectedConfiguration.cs", "src/Mcg.AgentOrchestrator.Core/Conductor/ConductorAutonomyPolicy.cs", "src/Mcg.AgentOrchestrator.Core/Collaboration/AcceptancePolicyChangeDecision.cs", "src/Mcg.AgentOrchestrator.App/Orchestration/OperatorIntentPolicyApproval.cs", "config/landing-denylist.json", "config/acceptance-manifest.json", "**/conductor-policy.json"]),
    ]);

    public sealed record Rule(string Id, string Reason, IReadOnlyList<string> Paths);
    public sealed record MatchResult(string RuleId, string Path);
    private readonly IReadOnlyList<Rule> _rules;
    private LandingDenylist(IReadOnlyList<Rule> rules) => _rules = rules;
    public IReadOnlyList<Rule> Rules => _rules;

    public static LandingDenylist Parse(string json, string source)
    {
        FormatException Invalid(string field) => new($"{source}: invalid landing denylist {field}.");
        void Properties(JsonElement obj, string field, params string[] allowed)
        {
            if (obj.ValueKind != JsonValueKind.Object) throw Invalid(field);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in obj.EnumerateObject())
                if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
                    throw Invalid($"{field}.{property.Name}");
        }
        string RequiredString(JsonElement obj, string property, string field)
        {
            if (!obj.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString())) throw Invalid($"{field}.{property}");
            return value.GetString()!;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Properties(root, "document", "version", "rules");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1)
                throw Invalid("version");
            if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() == 0)
                throw Invalid("rules");
            var parsed = new List<Rule>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules.EnumerateArray())
            {
                var field = rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("id", out var idValue) &&
                    idValue.ValueKind == JsonValueKind.String ? idValue.GetString()! : "id";
                Properties(rule, field, "id", "reason", "paths");
                var id = RequiredString(rule, "id", field);
                if (!Regex.IsMatch(id, "^[a-z]+(-[a-z]+)*$", RegexOptions.CultureInvariant) || !ids.Add(id)) throw Invalid(id);
                var reason = RequiredString(rule, "reason", id);
                if (!rule.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() == 0)
                    throw Invalid($"{id}.paths");
                var patterns = new List<string>();
                foreach (var pathValue in paths.EnumerateArray())
                {
                    if (pathValue.ValueKind != JsonValueKind.String) throw Invalid($"{id}.paths");
                    var path = pathValue.GetString()!;
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) ||
                        path.IndexOfAny(['\\', '?', '[', ']', '{', '}']) >= 0 ||
                        path.Split('/').Any(segment => segment.Length == 0 || (segment.Contains("**", StringComparison.Ordinal) && segment != "**")))
                        throw Invalid($"{id}.paths ({path})");
                    patterns.Add(path);
                }
                parsed.Add(new Rule(id, reason, patterns.ToArray()));
            }
            return new LandingDenylist(parsed.ToArray());
        }
        catch (JsonException exception) { throw new FormatException($"{source}: invalid landing denylist JSON: {exception.Message}", exception); }
        catch (InvalidOperationException exception) { throw new FormatException($"{source}: invalid landing denylist version or field type.", exception); }
    }

    public IReadOnlyList<MatchResult> Match(IEnumerable<string> paths)
    {
        var normalized = paths.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matches = new List<MatchResult>();
        foreach (var rule in _rules)
            foreach (var path in normalized)
                if (rule.Paths.Any(pattern => Matches(pattern.Split('/'), path.Split('/'))))
                    matches.Add(new MatchResult(rule.Id, path));
        return matches;
    }

    private static bool Matches(string[] pattern, string[] path)
    {
        var previous = new bool[path.Length + 1];
        previous[0] = true;
        foreach (var segment in pattern)
        {
            var current = new bool[path.Length + 1];
            if (segment == "**")
            {
                current[0] = previous[0];
                for (var s = 1; s <= path.Length; s++) current[s] = previous[s] || current[s - 1];
            }
            else
            {
                var matcher = new Regex("\\A" + Regex.Escape(segment).Replace("\\*", "[^/]*") + "\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                for (var s = 1; s <= path.Length; s++) current[s] = previous[s - 1] && matcher.IsMatch(path[s - 1]);
            }
            previous = current;
        }
        return previous[path.Length];
    }
}
