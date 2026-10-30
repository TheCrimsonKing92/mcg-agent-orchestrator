using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.ProjectFactHelpers;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Learns Lua ecosystem declarations and proposes checks without executing tooling.</summary>
public sealed class LuaProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    public const string FrameworkFact = "lua-test-framework";
    public const string TestCommandFact = "lua-test-command";
    public const string InstallCommandFact = "lua-install-command";
    public const string FrameworkQuestion = "environment/lua-test-framework";
    public const string InstallQuestion = "environment/lua-luarocks-install";
    public const string FrameworkQuestionText = "No busted specs were found. Use busted through crosswalk as the test framework?";
    public const string InstallCommand = "luarocks make --local";

    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var files = RepositoryFiles(root, excludedDirectoryNames);
        var questions = new List<ProjectOwnerQuestion>();
        var evidence = BustedEvidence(files);
        var source = new FactSource(evidence ?? ".", 1);
        var framework = evidence is not null ? new ProjectFact<string>("busted", source, FactConfidence.High)
            : Fact("undetermined", FactConfidence.Low, source, FrameworkQuestion, FrameworkQuestionText, questions);
        var needs = new List<EnvironmentNeed>
        {
            new(FrameworkFact, framework),
            new(TestCommandFact, new("crosswalk-test .", source, FactConfidence.High))
        };
        if (Rockspec(files) is { } rockspec)
        {
            var rockspecSource = new FactSource(rockspec, 1);
            needs.Add(new(InstallCommandFact, Fact(InstallCommand, FactConfidence.Low, rockspecSource, InstallQuestion,
                "Allow luarocks make --local to change the host's LuaRocks tree?", questions)));
        }

        var learned = IntegrationBranchReader.Read(root);
        if (learned.Branch is null)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey, $"What is the integration branch? {learned.Reason}",
                new FactSource(".git", 1), questions);
        else if (learned.Branch.Confidence != FactConfidence.High)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey,
                $"Confirm that '{learned.Branch.Value}' is the integration branch learned from the checked-out branch hint.",
                learned.Branch.Source, questions);

        return new(ProjectModel.CurrentSchemaVersion, ".", [], [], [],
            questions.OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), [], needs.AsReadOnly(),
            [], [], learned.Branch, ProjectStack.Lua);
    }

    public sealed record Check(string Name, string Type, string? Command, string? Project, IReadOnlyList<string> Arguments);

    public static (IReadOnlyList<Check> Checks, IReadOnlyList<ManifestBlocker> Blockers) For(
        ProjectModel model, AppliedOwnerAnswers applied)
    {
        var blockers = new List<ManifestBlocker>();
        var checks = new List<Check>();
        var framework = model.EnvironmentNeeds.FirstOrDefault(need => need.Name == FrameworkFact)?.Value;
        if (Answer(FrameworkQuestion) != "yes" &&
            framework is not { Value: "busted", Confidence: FactConfidence.High or FactConfidence.Medium })
            Block(FrameworkQuestion, FrameworkQuestionText);

        if (model.EnvironmentNeeds.Any(need => need.Name == InstallCommandFact))
        {
            switch (Answer(InstallQuestion))
            {
                case "yes":
                    checks.Add(new(InstallCommand, "command", "luarocks", null, ["make", "--local"]));
                    break;
                case "no":
                    break;
                default:
                    Block(InstallQuestion, "Allow luarocks make --local to change the host's LuaRocks tree?");
                    break;
            }
        }
        checks.Add(new("crosswalk-test .", "crosswalk-test", null, ".", []));
        return blockers.Count > 0 ? ([], blockers.AsReadOnly()) : (checks.AsReadOnly(), []);

        string? Answer(string key) => applied.AnsweredFacts.LastOrDefault(fact => fact.FactKey == key)?.Fact.Value;

        void Block(string key, string reason) => blockers.Add(new(key,
            model.OwnerQuestions.FirstOrDefault(question => question.FactKey == key)?.Question ?? $"{key}: {reason}"));
    }

    internal static string? BustedEvidence(IReadOnlyList<string> files) =>
        files.FirstOrDefault(file => file.Equals(".busted", StringComparison.OrdinalIgnoreCase)) ??
        files.FirstOrDefault(file => file.StartsWith("spec/", StringComparison.OrdinalIgnoreCase) &&
            file.EndsWith("_spec.lua", StringComparison.OrdinalIgnoreCase));

    internal static string? Rockspec(IReadOnlyList<string> files) =>
        files.FirstOrDefault(file => !file.Contains('/') && file.EndsWith(".rockspec", StringComparison.OrdinalIgnoreCase)) ??
        files.FirstOrDefault(file => file.StartsWith("rockspecs/", StringComparison.OrdinalIgnoreCase) &&
            file.Count(character => character == '/') == 1 && file.EndsWith(".rockspec", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> RepositoryFiles(string root, IReadOnlyCollection<string>? excludedDirectoryNames)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        var exclusions = new HashSet<string>(excludedDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(["bin", "obj", ".git", "node_modules", "vendor", "lua_modules", ".luarocks", "CVS"]);
        var files = new List<string>();
        Visit(root);
        EnsureRootPresent();
        return files.Order(StringComparer.Ordinal).ToArray();

        void EnsureRootPresent()
        {
            if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
                throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        void Visit(string directory)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (DirectoryNotFoundException) { EnsureRootPresent(); return; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    EnsureRootPresent();
                    continue;
                }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (!name.StartsWith('.') && !exclusions.Contains(name))
                        Visit(entry);
                }
                else
                    files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }
    }
}
