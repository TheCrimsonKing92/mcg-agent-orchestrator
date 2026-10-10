using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public static class ReviewerFindingsLineBlocker
{
    public static bool TryReadFindingsLine(string line, out string blockerText)
    {
        blockerText = string.Empty;
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator]
            .Replace("#", string.Empty)
            .Replace("*", string.Empty)
            .Replace("`", string.Empty)
            .Trim()
            .TrimStart('-', ' ')
            .Trim();
        if (!key.Equals("findings", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = line[(separator + 1)..].Trim();
        if (!value.StartsWith('['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var blockers = new List<string>();
            var index = 0;
            foreach (var finding in document.RootElement.EnumerateArray())
            {
                if (finding.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                string? state = null;
                string? severity = null;
                string? stableId = null;
                string? title = null;
                string? description = null;
                foreach (var property in finding.EnumerateObject())
                {
                    var text = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()?.Trim()
                        : null;
                    switch (property.Name.ToLowerInvariant())
                    {
                        case "state": state = text; break;
                        case "severity": severity = text; break;
                        case "stable_id": stableId = text; break;
                        case "title": title = text; break;
                        case "description": description = text; break;
                    }
                }

                // Only explicit resolved/advisory values can remove an item from blocker territory.
                if (!string.Equals(state, "resolved", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(severity, "advisory", StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add(!string.IsNullOrEmpty(stableId) ? stableId :
                        !string.IsNullOrEmpty(title) ? title :
                        !string.IsNullOrEmpty(description) ? description :
                        index.ToString(CultureInfo.InvariantCulture));
                }

                index++;
            }

            blockerText = string.Join("; ", blockers);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
