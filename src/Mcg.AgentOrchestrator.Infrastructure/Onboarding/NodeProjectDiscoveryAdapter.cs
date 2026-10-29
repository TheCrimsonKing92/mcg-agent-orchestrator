using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.ProjectFactHelpers;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Learns root Node declarations without invoking tools or inspecting installed packages.</summary>
public sealed class NodeProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");

        var questions = new List<ProjectOwnerQuestion>();
        var packageSource = new FactSource("package.json", 1);
        (string Path, string Manager)[] lockfiles =
            [("package-lock.json", "npm"), ("pnpm-lock.yaml", "pnpm"), ("yarn.lock", "yarn")];
        var present = lockfiles.Where(file => Exists(file.Path)).ToArray();
        var managerSource = present.Length == 1 ? new FactSource(present[0].Path, 1) : packageSource;
        var manager = present.Length == 1 ? High(present[0].Manager, managerSource)
            : Fact(present.Length == 0 ? "undetermined" : "ambiguous", FactConfidence.Low, managerSource,
                NodeVerificationChecks.PackageManagerQuestion,
                present.Length == 0 ? "Which package manager is intended? No root package-lock.json, pnpm-lock.yaml or yarn.lock exists."
                    : $"Which lockfile is authoritative? Found {string.Join(", ", present.Select(file => file.Path))}.", questions);
        if (present.Length == 1 && manager.Value != "npm")
            Ask(NodeVerificationChecks.PackageManagerQuestion,
                $"Verification manifest emission for {manager.Value} is unsupported. Confirm how this project should install dependencies.",
                managerSource, questions);

        var framework = ReadFramework(root, questions);
        var configSource = new FactSource("tsconfig.json", 1);
        var config = High(Exists("tsconfig.json") ? "tsconfig.json" : NodeVerificationChecks.AbsentTypeScriptConfig, configSource);
        Ask(NodeVerificationChecks.RegistryQuestion,
            "A fresh worktree or gate attempt has no node_modules, so the install check needs registry network access. " +
            "Can both worker and gate environments reach the configured npm registry?", managerSource, questions);
        var hazards = new[]
        {
            new SharedStateHazard(".", NodeVerificationChecks.RegistryHazard, High("npm-registry", managerSource))
        };

        var learned = IntegrationBranchReader.Read(root);
        if (learned.Branch is null)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey, $"What is the integration branch? {learned.Reason}",
                new FactSource(".git", 1), questions);
        else if (learned.Branch.Confidence != FactConfidence.High)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey,
                $"Confirm that '{learned.Branch.Value}' is the integration branch learned from the checked-out branch hint.",
                learned.Branch.Source, questions);

        return new(ProjectModel.CurrentSchemaVersion, ".", [], [], [],
            questions.OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), [],
            [new(NodeVerificationChecks.PackageManagerFact, manager), new(NodeVerificationChecks.FrameworkFact, framework),
                new(NodeVerificationChecks.TypeScriptConfigFact, config)], [], hazards, learned.Branch, ProjectStack.Node);

        bool Exists(string path) => IsSafePath(root, Path.Combine(root, path)) && File.Exists(Path.Combine(root, path));
    }

    private static ProjectFact<string> ReadFramework(string root, List<ProjectOwnerQuestion> questions)
    {
        var source = new FactSource("package.json", 1);
        try
        {
            var path = Path.Combine(root, "package.json");
            if (!IsSafePath(root, path))
                throw new IOException("The file is a reparse point.");
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The package declaration must be an object.");
            var hasDependencies = document.RootElement.TryGetProperty("devDependencies", out var dependencies);
            if (hasDependencies && dependencies.ValueKind != JsonValueKind.Object)
                throw new JsonException("devDependencies must be an object.");
            var vitest = hasDependencies && dependencies.TryGetProperty("vitest", out _);
            var jest = hasDependencies && dependencies.TryGetProperty("jest", out _);
            if (vitest != jest)
            {
                var framework = vitest ? "vitest" : "jest";
                source = new FactSource("package.json", DependencyLine(text, framework));
                if (jest)
                    Ask(NodeVerificationChecks.FrameworkQuestion,
                        "Jest is recognized but unsupported for manifest emission. Which supported verification framework should be used?",
                        source, questions);
                return High(framework, source);
            }
            return Fact(vitest ? "ambiguous" : "undetermined", FactConfidence.Low, source,
                NodeVerificationChecks.FrameworkQuestion,
                vitest ? "Both vitest and jest are declared in devDependencies. Which test framework is authoritative?"
                    : "Which test framework should be used? Neither vitest nor jest is declared in package.json devDependencies.", questions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            var operation = exception is JsonException ? "parse" : "read";
            Ask($"discovery/files/package.json/{operation}", $"Cannot {operation} package.json: {exception.Message}", source, questions);
            return Fact("undetermined", FactConfidence.Low, source, NodeVerificationChecks.FrameworkQuestion,
                "Which test framework should be used? package.json could not be interpreted.", questions);
        }
    }

    private static int DependencyLine(string text, string framework)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("devDependencies"))
                continue;
            reader.Read();
            while (reader.Read() && reader.CurrentDepth > 1)
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 2 && reader.ValueTextEquals(framework))
                    return 1 + bytes.AsSpan(0, (int)reader.TokenStartIndex).Count((byte)'\n');
        }
        return 1;
    }

    private static ProjectFact<string> High(string value, FactSource source) => new(value, source, FactConfidence.High);
}
