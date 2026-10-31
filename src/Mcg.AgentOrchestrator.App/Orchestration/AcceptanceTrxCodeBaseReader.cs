using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceTrxCodeBaseReader
{
    internal static IReadOnlyList<string> Read(IReadOnlyList<string> paths)
    {
        var assemblies = new List<string>();
        foreach (var path in paths)
        {
            var document = XDocument.Load(path);
            var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                .Where(element => element.Attribute("id") is not null)
                .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? "", out var definition);
                var value = definition?.Descendants().FirstOrDefault(element => element.Name.LocalName == "TestMethod")
                    ?.Attribute("codeBase")?.Value ?? "";
                assemblies.Add(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : value);
            }
        }
        return assemblies.Distinct(StringComparer.Ordinal).ToArray();
    }
}
