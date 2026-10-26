using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: read-only inspection of the verified worktree; controls use in-memory XML.
public sealed class OwnerConsoleTestsProjectIsolationTests
{
    private const string MainProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string NewProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsole/Mcg.AgentOrchestrator.Infrastructure.OwnerConsole.Tests.csproj";
    private static string Root => InfrastructureTestSupport.FindRepositoryRoot();

    [Fact]
    public void MovedDeclarations_CompileOnlyInNestedOwner()
    {
        var main = Path.Combine(Root, MainProject);
        Assert.Contains("<Compile Remove=\"OwnerConsole\\**\\*.cs\" />", File.ReadAllText(main), StringComparison.Ordinal);
        var mainSources = CompiledSources(main);
        var nestedSources = CompiledSources(Path.Combine(Root, NewProject));
        Assert.NotEmpty(mainSources);
        Assert.NotEmpty(nestedSources);
        var nestedDirectory = Path.GetDirectoryName(Path.Combine(Root, NewProject))! + Path.DirectorySeparatorChar;
        var nestedNames = nestedSources.Keys
            .Where(path => path.StartsWith(nestedDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var source in mainSources.Values.Where(text => nestedNames.Any(name =>
                     text.Contains(name!, StringComparison.Ordinal))))
        {
            var declarations = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .Where(node => node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .Select(node => node switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                    DelegateDeclarationSyntax type => type.Identifier.ValueText,
                    _ => null
                }).OfType<string>();
            Assert.Empty(declarations.Where(nestedNames.Contains));
        }
        foreach (var name in new[] { "OwnerConsoleHarness.cs", "OwnerConsoleTestClock.cs",
                     "OwnerConsoleControllerFactory.cs", "OwnerConsoleLandingBlockerViewTests.cs" })
            Assert.True(File.Exists(Path.Combine(Root,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests", name)), name);
    }

    [Fact]
    public void ProjectContract_HasExactReferencesAndRunnerProperties()
    {
        var projectPath = Path.Combine(Root, NewProject);
        var directory = Path.GetDirectoryName(projectPath)!;
        var document = XDocument.Load(projectPath);
        var references = document.Descendants("ProjectReference").Attributes("Include")
            .Select(include => Path.GetRelativePath(Root,
                Path.GetFullPath(Path.Combine(directory, include.Value.Replace('\\', '/')))).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { AcceptancePolicyShardPlanner.CoreProject, AcceptancePolicyShardPlanner.AppProject,
            AcceptancePolicyShardPlanner.InfrastructureProject, AcceptancePolicyShardPlanner.TestSupportProject }
            .Order(StringComparer.Ordinal), references);
        Assert.Equal("true", Assert.Single(document.Descendants("IsTestProject")).Value);
        Assert.Equal("Exe", Assert.Single(document.Descendants("OutputType")).Value);
        Assert.Equal("true", Assert.Single(document.Descendants("UseMicrosoftTestingPlatformRunner")).Value);
        Assert.NotEmpty(document.Descendants("Compile").Attributes("Include"));
        Assert.Empty(LinkViolations(document, directory));
    }

    [Fact]
    public void TestDeclaringLink_IsRejectedBySameValidator()
    {
        var projectPath = Path.Combine(Root, NewProject);
        var document = XDocument.Load(projectPath);
        document.Root!.Add(new XElement("ItemGroup", new XElement("Compile",
            new XAttribute("Include", "..\\WorkerProfileTests.cs"))));
        Assert.Equal(["..\\WorkerProfileTests.cs: declares test methods"],
            LinkViolations(document, Path.GetDirectoryName(projectPath)!));
    }

    [Fact]
    public void CheckedInManifest_DeclaresOneWholeProjectCheckAndInvocation()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "config/acceptance-manifest.json")));
        var check = Assert.Single(manifest.RootElement.GetProperty("checks").EnumerateArray()
            .Where(item => item.TryGetProperty("project", out var project) && project.GetString() == NewProject));
        Assert.Equal("owner console tests", check.GetProperty("name").GetString());
        Assert.Equal("dotnet-test", check.GetProperty("type").GetString());
        Assert.Equal("mtp", check.GetProperty("runner").GetString());
        Assert.Equal(["--verbosity", "minimal"], Strings(check.GetProperty("arguments")));
        var engine = manifest.RootElement.GetProperty("engine");
        var invocation = Assert.Single(engine.GetProperty("mtpInvocations").EnumerateArray()
            .Where(item => item.GetProperty("project").GetString() == NewProject));
        var arguments = Strings(invocation.GetProperty("arguments"));
        foreach (var required in new[] { "--report-trx", "--report-trx-filename", "{trxFileName}", "--minimum-expected-tests" })
            Assert.Contains(required, arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--filter", StringComparison.OrdinalIgnoreCase));
        foreach (var lane in engine.GetProperty("infrastructureTestLanes").EnumerateArray())
            Assert.DoesNotContain("OwnerConsole", lane.GetProperty("filter").GetString()!, StringComparison.Ordinal);
        var inventory = DeclaredTestProjectInventory.DeclaredProjects(AcceptanceGateEngineSettings.Load(Root));
        Assert.Contains(NewProject, inventory);
        Assert.Contains(AcceptancePolicyShardPlanner.StorageRetentionTestsProject, inventory);
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static IReadOnlyList<string> LinkViolations(XDocument document, string directory)
    {
        var violations = new List<string>();
        foreach (var include in document.Descendants("Compile").Attributes("Include"))
        {
            if (include.Value.IndexOfAny(['*', '?', '$']) >= 0)
            {
                violations.Add(include.Value + ": nonliteral path");
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(directory, include.Value.Replace('\\', '/')));
            if (!File.Exists(path))
                violations.Add(include.Value + ": missing file");
            else if (Regex.IsMatch(File.ReadAllText(path), @"\[\s*(?:global::)?(?:Xunit\.)?(?:Fact|Theory)(?:Attribute)?\b"))
                violations.Add(include.Value + ": declares test methods");
        }
        return violations;
    }

    private static Dictionary<string, string> CompiledSources(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        var document = XDocument.Load(projectPath);
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Replace('\\', '/').Split('/')
                .Any(segment => segment is "bin" or "obj"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var remove in document.Descendants("Compile").Attributes("Remove"))
        foreach (var pattern in remove.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var expression = Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*/", @"(?:.*/)?")
                .Replace(@"\*", @"[^/]*").Replace(@"\?", @"[^/]");
            sources.RemoveWhere(path => Regex.IsMatch(Path.GetRelativePath(directory, path).Replace('\\', '/'),
                "^" + expression + "$", RegexOptions.IgnoreCase));
        }
        foreach (var include in document.Descendants("Compile").Attributes("Include"))
        {
            Assert.DoesNotContain('*', include.Value);
            Assert.DoesNotContain('?', include.Value);
            sources.Add(Path.GetFullPath(Path.Combine(directory, include.Value.Replace('\\', '/'))));
        }
        return sources.ToDictionary(path => path, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
    }
}
