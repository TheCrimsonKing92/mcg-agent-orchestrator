using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.ProjectFactHelpers;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Learns an executable suite from crosswalk without knowing its stack.</summary>
public sealed class CrosswalkProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    private readonly CrosswalkStackReport _report;
    private readonly int _testCount;

    internal CrosswalkProjectDiscoveryAdapter(CrosswalkStackReport report, int testCount)
    {
        _report = report;
        _testCount = testCount;
    }

    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        var source = new FactSource(measurementReference: "discovery:crosswalk");
        EnvironmentNeed Fact(string name, string value) => new(name, new(value, source, FactConfidence.High));
        var needs = new[]
        {
            Fact("crosswalk-stack", _report.Stack),
            Fact("crosswalk-test-framework", _report.TestFramework ?? ""),
            Fact("crosswalk-evidence", _report.Evidence),
            Fact("crosswalk-test-count", _testCount.ToString(CultureInfo.InvariantCulture)),
            Fact("crosswalk-test-command", "crosswalk-test .")
        };
        var questions = new List<ProjectOwnerQuestion>();
        var learned = IntegrationBranchReader.Read(root);
        if (learned.Branch is null)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey, $"What is the integration branch? {learned.Reason}",
                new FactSource(".git", 1), questions);
        else if (learned.Branch.Confidence != FactConfidence.High)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey,
                $"Confirm that '{learned.Branch.Value}' is the integration branch learned from the checked-out branch hint.",
                learned.Branch.Source, questions);
        return new(ProjectModel.CurrentSchemaVersion, ".", [], [], [],
            questions.OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), [], needs, [], [],
            learned.Branch, ProjectStack.Crosswalk);
    }

    internal static GoalAcceptanceVerifier.AcceptanceManifestCheck VerificationCheck() =>
        new() { Name = "crosswalk-test .", Type = "crosswalk-test", Project = ".", Arguments = [] };
}
