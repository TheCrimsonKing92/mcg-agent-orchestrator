using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public sealed class DashboardRemovalSourceGuardTests
{
    private const string NamespacePattern = @"\b(?:using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?|namespace\s+)(?:Mcg\.AgentOrchestrator\.App\.Dashboard|Mcg\.AgentOrchestrator\.Dashboard)\b";
    private static readonly Regex FriendAssembly = new(
        "\\[\\s*assembly\\s*:\\s*(?:System\\.Runtime\\.CompilerServices\\.)?InternalsVisibleTo(?:Attribute)?\\s*\\(\\s*\"Mcg\\.AgentOrchestrator\\.Dashboard\\.Tests\"",
        RegexOptions.CultureInvariant);

    [Fact]
    public void RemovedDashboardConstructsAreAbsent()
    {
        var root = VerifiedRepositoryRoot.Find();
        foreach (var folder in new[]
        {
            "src/Mcg.AgentOrchestrator.Dashboard", "src/Mcg.AgentOrchestrator.App/Dashboard",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests"
        })
            Assert.False(Directory.Exists(Path.Combine(root, folder)), $"Removed folder exists: {folder}");

        foreach (var project in SourceFiles(root, "*.csproj"))
        {
            Assert.False(IsDashboardProject(Path.GetFileNameWithoutExtension(project)), project);
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
                Assert.False(IsDashboardProject(Path.GetFileNameWithoutExtension(
                    reference.Attribute("Include")!.Value.Replace('\\', '/'))), project);
        }

        var solution = File.ReadAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"));
        Assert.DoesNotMatch(@"(?m)^Project\([^\r\n]+ = ""Mcg\.AgentOrchestrator\.Dashboard(?:\.Tests)?""", solution);
        foreach (var source in SourceFiles(root, "*.cs"))
            Assert.False(HasRemovedConstruct(File.ReadAllText(source)), $"Removed C# construct in {source}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var manifestText = manifest.RootElement.GetRawText();
        Assert.DoesNotContain("Mcg.AgentOrchestrator.Dashboard.Tests", manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dashboard tests\"", manifestText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardDistinguishesDeclarationsFromSampleDataAndComments()
    {
        Assert.True(HasRemovedConstruct("using Mcg.AgentOrchestrator.App.Dashboard.Api;"));
        Assert.True(HasRemovedConstruct("global using D = Mcg.AgentOrchestrator.Dashboard;"));
        Assert.True(HasRemovedConstruct("namespace Mcg.AgentOrchestrator.Dashboard.Tests;"));
        Assert.True(HasRemovedConstruct("[assembly: InternalsVisibleTo(\"Mcg.AgentOrchestrator.Dashboard.Tests\")]"));
        Assert.False(HasRemovedConstruct("// using Mcg.AgentOrchestrator.App.Dashboard.Api;"));
        Assert.False(HasRemovedConstruct("/* namespace Mcg.AgentOrchestrator.Dashboard; */"));
        Assert.False(HasRemovedConstruct("var data = \"using Mcg.AgentOrchestrator.App.Dashboard.Api;\";"));
        Assert.False(HasRemovedConstruct("var data = @\"namespace Mcg.AgentOrchestrator.Dashboard;\";"));
        Assert.False(HasRemovedConstruct("var data = $\"{Render(\"using Mcg.AgentOrchestrator.App.Dashboard.Api;\")}\";"));
        Assert.False(HasRemovedConstruct("var data = \"\"\"\nusing Mcg.AgentOrchestrator.App.Dashboard.Api;\n\"\"\";"));
        Assert.False(HasRemovedConstruct("var data = \"[assembly: InternalsVisibleTo(\\\"Mcg.AgentOrchestrator.Dashboard.Tests\\\")]\";"));
        Assert.False(HasRemovedConstruct("using Mcg.AgentOrchestrator.App.Rendering;"));
    }

    private static bool HasRemovedConstruct(string source)
    {
        var code = MaskLiteralsAndComments(source);
        return Regex.IsMatch(code, NamespacePattern, RegexOptions.CultureInvariant) ||
            FriendAssembly.Matches(source).Any(match => code[match.Index] == '[');
    }

    private static string MaskLiteralsAndComments(string source)
    {
        var code = source.ToCharArray();
        for (var index = 0; index < source.Length; index++)
        {
            var start = index;
            if (source.AsSpan(index).StartsWith("//"))
            {
                index = source.IndexOf('\n', index);
                if (index < 0) index = source.Length;
            }
            else if (source.AsSpan(index).StartsWith("/*"))
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 2;
            }
            else if (source[index] is '"' or '\'')
            {
                index = SkipLiteral(source, index);
            }
            else continue;

            for (var masked = start; masked < Math.Min(index, source.Length); masked++)
                if (code[masked] is not '\r' and not '\n') code[masked] = ' ';
            index--;
        }
        return new string(code);
    }

    private static int SkipLiteral(string source, int index)
    {
        var quote = source[index];
        var width = 1;
        if (quote == '"')
            while (index + width < source.Length && source[index + width] == quote) width++;
        if (width >= 3)
        {
            var end = source.IndexOf(new string(quote, width), index + width, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + width;
        }

        var prefix = index > 1 ? source[(index - 2)..index] : source[..index];
        var verbatim = quote == '"' && (prefix.EndsWith('@') || prefix == "@$");
        var interpolated = quote == '"' && (prefix.EndsWith('$') || prefix == "$@");
        var expressionDepth = 0;
        index++;
        while (index < source.Length)
        {
            if (expressionDepth > 0)
            {
                if (source[index] is '"' or '\'') { index = SkipLiteral(source, index); continue; }
                if (source[index] == '{') expressionDepth++;
                if (source[index] == '}') expressionDepth--;
                index++;
                continue;
            }
            if (!verbatim && source[index] == '\\') { index += 2; continue; }
            if (interpolated && source[index] == '{')
            {
                if (index + 1 < source.Length && source[index + 1] == '{') { index += 2; continue; }
                expressionDepth++;
                index++;
                continue;
            }
            if (source[index++] != quote) continue;
            if (verbatim && index < source.Length && source[index] == quote) { index++; continue; }
            break;
        }
        return index;
    }

    private static bool IsDashboardProject(string name) => name is
        "Mcg.AgentOrchestrator.Dashboard" or "Mcg.AgentOrchestrator.Dashboard.Tests";

    private static IEnumerable<string> SourceFiles(string root, string pattern) =>
        new[] { "src", "tests" }.SelectMany(folder =>
            Directory.EnumerateFiles(Path.Combine(root, folder), pattern, SearchOption.AllDirectories))
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or "TestResults" or "playwright-report"));

}
