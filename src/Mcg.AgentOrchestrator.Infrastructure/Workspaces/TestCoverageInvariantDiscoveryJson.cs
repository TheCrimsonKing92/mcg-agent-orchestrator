using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TestCoverageInvariantDiscoveryJson
{
    internal static string EscapeRawControlCharacters(string text)
    {
        StringBuilder? escapedText = null;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inString && character < 0x20)
            {
                escapedText ??= new StringBuilder(text.Length).Append(text, 0, index);
                escapedText.Append(character switch
                {
                    '\t' => "\\t",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => "\\u00" + ((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
                });
                escaped = false;
                continue;
            }

            escapedText?.Append(character);
            if (escaped)
            {
                escaped = false;
            }
            else if (inString && character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                inString = !inString;
            }
        }

        return escapedText?.ToString() ?? text;
    }
}
