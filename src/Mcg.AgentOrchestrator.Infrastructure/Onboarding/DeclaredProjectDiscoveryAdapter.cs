using Mcg.AgentOrchestrator.Core;
using System.Text.Json.Nodes;
using static Mcg.AgentOrchestrator.Infrastructure.ProjectFactHelpers;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Asks the owner for whole-project commands when no stack marker is recognized.</summary>
public sealed class DeclaredProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    public const string BuildQuestion = "commands/declared/build";
    public const string TestQuestion = "commands/declared/test";
    public const string ReportsQuestion = "commands/declared/reports";
    public const string BuildCheckName = "declared-build";
    public const string TestCheckName = "declared-test";

    private readonly IReadOnlyList<EnvironmentNeed> _facts;

    public DeclaredProjectDiscoveryAdapter(IReadOnlyList<EnvironmentNeed>? facts = null) => _facts = facts?.ToArray() ?? [];

    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        var questions = new List<ProjectOwnerQuestion>();
        var source = new FactSource(measurementReference: "discovery:declared:owner-commands");
        Ask(BuildQuestion, "What command builds the project? Answer none when no build is needed.", source, questions);
        Ask(TestQuestion, "What command runs the whole test suite and writes JUnit XML?", source, questions);
        Ask(ReportsQuestion, "Which comma-separated relative globs select the JUnit reports?", source, questions);
        var learned = IntegrationBranchReader.Read(root);
        if (learned.Branch is null)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey, $"What is the integration branch? {learned.Reason}",
                new FactSource(".git", 1), questions);
        else if (learned.Branch.Confidence != FactConfidence.High)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey,
                $"Confirm that '{learned.Branch.Value}' is the integration branch learned from the checked-out branch hint.",
                learned.Branch.Source, questions);
        return new(ProjectModel.CurrentSchemaVersion, ".", [], [], [],
            questions.OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), [], _facts, [], [],
            learned.Branch, ProjectStack.Declared);
    }

    internal static (IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> Checks, IReadOnlyList<ManifestBlocker> Blockers) For(
        ProjectModel model, AppliedOwnerAnswers applied)
    {
        var keys = new[] { BuildQuestion, TestQuestion, ReportsQuestion };
        var blockers = keys.Where(key => Answer(key) is null).Select(key => new ManifestBlocker(key,
            model.OwnerQuestions.FirstOrDefault(question => question.FactKey == key)?.Question ?? $"Owner answer required: {key}")).ToArray();
        if (blockers.Length > 0) return ([], blockers);
        var checks = new List<GoalAcceptanceVerifier.AcceptanceManifestCheck>();
        var build = DeclaredCommandLine.Parse(Answer(BuildQuestion)!);
        if (Answer(BuildQuestion) != "none")
            checks.Add(new() { Name = BuildCheckName, Type = "command", Command = build[0], Arguments = build[1..] });
        var test = DeclaredCommandLine.Parse(Answer(TestQuestion)!);
        checks.Add(new() { Name = TestCheckName, Type = "junit-command", Command = test[0], Arguments = test[1..],
            Project = ".", Reports = DeclaredCommandLine.ParseReports(Answer(ReportsQuestion)!) });
        return (checks, []);

        string? Answer(string key) => applied.AnsweredFacts.LastOrDefault(fact => fact.FactKey == key)?.Fact.Value;
    }

    internal static JsonObject Emit(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
    {
        var json = new JsonObject { ["name"] = check.Name, ["type"] = check.Type, ["command"] = check.Command,
            ["arguments"] = new JsonArray(check.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()) };
        if (check.Type == "junit-command")
        {
            json["project"] = check.Project;
            json["reports"] = new JsonArray(check.Reports.Select(report => (JsonNode?)JsonValue.Create(report)).ToArray());
        }
        return json;
    }
}
