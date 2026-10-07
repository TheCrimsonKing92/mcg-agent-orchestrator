using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PowerShellClixmlStandardError
{
    // Comments, CDATA and quoted attributes must not end a document prematurely.
    private const string NonElements = "<!--[\\s\\S]*?-->|<!\\[CDATA\\[[\\s\\S]*?\\]\\]>|<\\?[\\s\\S]*?\\?>|";
    private static readonly Regex DocumentTokens = new(
        NonElements +
        "<(?<closing>/)?(?<name>(?:[\\w.-]+:)?Objs)(?=[\\s/>])(?:[^>\"']|\"[^\"]*\"|'[^']*')*>");
    private static readonly Regex DocumentStart = new(NonElements + "(?<document><(?:[\\w.-]+:)?Objs(?=[\\s/>]|$))");
    private static readonly Regex Escapes = new("_x([0-9A-Fa-f]{4})_");

    internal static string Readable(string standardError)
    {
        var headerStart = 0;
        var bodyStart = 0;
        while (true)
        {
            var newline = standardError.IndexOf('\n', headerStart);
            var lineEnd = newline < 0 ? standardError.Length : newline;
            if (lineEnd > headerStart && standardError[lineEnd - 1] == '\r') lineEnd--;
            if (standardError.AsSpan(headerStart, lineEnd - headerStart).SequenceEqual("#< CLIXML"))
            {
                bodyStart = newline < 0 ? standardError.Length : newline + 1;
                break;
            }
            if (newline < 0) return standardError;
            headerStart = newline + 1;
        }

        // A declaration belongs to the following document; never strip its root and leave a DTD behind.
        if (standardError.IndexOf("<!DOCTYPE", bodyStart, StringComparison.Ordinal) >= 0) return standardError;

        var readable = new StringBuilder();
        var position = bodyStart;
        try
        {
            while (true)
            {
                var start = DocumentStart.Match(standardError, position);
                while (start.Success && !start.Groups["document"].Success) start = start.NextMatch();
                if (!start.Success) break;
                var end = DocumentEnd(standardError, start.Index);
                if (end < 0) return standardError;
                readable.Append(standardError, position, start.Index - position);
                using var input = new StringReader(standardError[start.Index..end]);
                using var reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                var document = XDocument.Load(reader);
                var errors = document.Descendants()
                    .Where(element => element.Name.LocalName == "S" && (string?)element.Attribute("S") == "Error")
                    .Select(element => Escapes.Replace(element.Value, match =>
                        ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())
                        .TrimEnd('\r', '\n'));
                readable.Append(string.Join("\n", errors));
                position = end;
            }
        }
        catch (XmlException)
        {
            return standardError;
        }
        readable.Append(standardError, position, standardError.Length - position);
        return standardError[..headerStart] + readable.ToString().TrimEnd('\r', '\n');
    }

    private static int DocumentEnd(string text, int start)
    {
        var depth = 0;
        foreach (Match token in DocumentTokens.Matches(text, start))
        {
            if (!token.Groups["name"].Success) continue;
            if (depth == 0 && token.Index != start) return -1;
            if (token.Groups["closing"].Success) depth--;
            else if (!token.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return token.Index + token.Length;
        }
        return -1;
    }
}
