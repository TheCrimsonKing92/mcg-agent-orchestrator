using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerEventFields
{
    internal static string? Field(OwnerConductEvent item, string name)
    {
        var match = Regex.Match(item.Detail, @"(?:^|\s)" + Regex.Escape(name) + @"=(.*?)(?=\s+[\w-]+=|$)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    internal static string Head(OwnerConductEvent item) => item.Detail.Split(' ', 2)[0];
    internal static string Prefix(string? id) => id is null ? "" : id[..Math.Min(8, id.Length)];
    internal static string[] Members(OwnerConductEvent item) => (Field(item, "members") ?? "").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries);
    internal static bool Positive(OwnerConductEvent item, string name) =>
        Regex.Matches(item.Detail, @"(?:^|\s)" + name + @"=").Count == 1 && int.TryParse(Field(item, name), out var count) && count > 0;
}
