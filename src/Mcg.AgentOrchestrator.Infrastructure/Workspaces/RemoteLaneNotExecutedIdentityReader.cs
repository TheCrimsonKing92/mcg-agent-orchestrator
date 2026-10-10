using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneNotExecutedIdentities(IReadOnlyList<string> Identities, bool Unreadable);

internal static class RemoteLaneNotExecutedIdentityReader
{
    internal static RemoteLaneNotExecutedIdentities Read(IEnumerable<string>? trxPaths)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = trxPaths is null;
        foreach (var path in trxPaths ?? [])
        {
            try { identities.UnionWith(Read(XDocument.Load(path, LoadOptions.None))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                unreadable = true;
            }
        }
        return new(identities.Order(StringComparer.Ordinal).ToArray(), unreadable);
    }

    internal static IReadOnlyList<string> Read(XDocument document)
    {
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest" &&
                              !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult" &&
                              string.Equals(element.Attribute("outcome")?.Value, "NotExecuted", StringComparison.OrdinalIgnoreCase))
            .Select(result =>
            {
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
                return AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ??
                       result.Attribute("testId")?.Value?.Trim() ?? "unknown test";
            })
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
