using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A named record reference; parsing confers no authority to read a store.</summary>
public sealed record PlannerEvidenceStoreReference(string Name, string Kind, string Locator, string? Selector)
{
    public static bool TryParse(string evidenceKey, string value, out PlannerEvidenceStoreReference? reference)
    {
        reference = null;
        if (value.IndexOfAny(['\r', '\n']) >= 0) return false;
        var match = Regex.Match(value, @"\A(?<kind>[a-z-]+):(?<locator>[^#\s]+)(?:#(?<selector>.+))?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        reference = new(DeriveName(evidenceKey), match.Groups["kind"].Value, match.Groups["locator"].Value,
            match.Groups["selector"].Success ? match.Groups["selector"].Value.Trim() : null);
        return true;
    }

    public static string DeriveName(string evidenceKey)
    {
        var name = Regex.Replace(evidenceKey.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        name = name[..Math.Min(name.Length, 40)].TrimEnd('-');
        return name.Length == 0 ? "evidence" : name;
    }

    public string ToStoreRefLine() => $"store-ref: {Name} = {Kind}:{Locator}" +
        (Selector is null ? string.Empty : $"#{Selector}");
}
