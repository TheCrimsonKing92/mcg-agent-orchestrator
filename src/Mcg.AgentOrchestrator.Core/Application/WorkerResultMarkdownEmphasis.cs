namespace Mcg.AgentOrchestrator.Core;

public static class WorkerResultMarkdownEmphasis
{
    public static string Normalize(string trimmedLine)
    {
        var line = trimmedLine;
        var changed = line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal);
        if (changed)
        {
            line = line[2..].Trim();
        }

        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
        {
            return trimmedLine;
        }

        var label = line[..colonIndex].Trim();
        var value = line[(colonIndex + 1)..].Trim();
        string[] markers = ["**", "__", "*", "_"];
        foreach (var marker in markers)
        {
            if (!label.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var closes = label.EndsWith(marker, StringComparison.Ordinal) &&
                (marker.Length == 2 || !label.EndsWith(marker + marker, StringComparison.Ordinal));
            if (closes && label.Length > 2 * marker.Length &&
                label[marker.Length..^marker.Length].Trim().Length > 0)
            {
                label = label[marker.Length..^marker.Length].Trim();
                changed = true;
            }
            else if (marker.Length == 2 && !label.EndsWith('*') && !label.EndsWith('_'))
            {
                // The matching close can sit immediately after the field's colon.
                label = label[marker.Length..].Trim();
                if (value.StartsWith(marker, StringComparison.Ordinal))
                {
                    value = value[marker.Length..].Trim();
                }
                changed = true;
            }

            break;
        }

        foreach (var marker in markers)
        {
            if (!value.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (value.EndsWith(marker, StringComparison.Ordinal) &&
                (marker.Length == 2 || !value.EndsWith(marker + marker, StringComparison.Ordinal)) &&
                value.Length > 2 * marker.Length &&
                value[marker.Length..^marker.Length].Trim().Length > 0)
            {
                value = value[marker.Length..^marker.Length].Trim();
                changed = true;
            }

            break;
        }

        return changed ? label + (value.Length == 0 ? ":" : ": " + value) : trimmedLine;
    }
}
