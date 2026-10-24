using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads source declarations, retaining lines while excluding comments and string contents.</summary>
internal static class GoSourceFileReader
{
    internal enum TokenKind { Identifier, String, Rune, Symbol, Comment }
    internal sealed record Token(TokenKind Kind, string Text, int Line);
    internal sealed record Declaration(string Value, int Line);
    internal sealed record Import(string LocalName, string Path, int Line);
    internal sealed record SourceFile(Declaration? Package, IReadOnlyList<Import> Imports,
        IReadOnlyList<Declaration> Constraints, IReadOnlyList<int> BuildCalls);

    internal static SourceFile Read(string text, bool isTestFile)
    {
        var lexed = Tokenize(text);
        var tokens = lexed.Where(token => token.Kind != TokenKind.Comment).ToArray();
        Declaration? package = null;
        var imports = new List<Import>();
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (Is(tokens[i], "package") && tokens[i + 1].Kind == TokenKind.Identifier)
                package ??= new(tokens[i + 1].Text, tokens[i].Line);
            if (!Is(tokens[i], "import"))
                continue;
            var j = i + 1;
            var block = tokens[j].Text == "(";
            if (block) j++;
            do
            {
                if (j >= tokens.Length || tokens[j].Text == ")") break;
                if (tokens[j].Text == ";") { j++; continue; }
                string? alias = null;
                if (tokens[j].Kind == TokenKind.Identifier || tokens[j].Text == ".")
                    alias = tokens[j++].Text;
                if (j >= tokens.Length || tokens[j].Kind != TokenKind.String) break;
                var spec = tokens[j++];
                imports.Add(new(alias ?? spec.Text.Split('/')[^1], spec.Text, spec.Line));
            } while (block);
            i = j - 1;
        }

        var constraints = lexed.Where(token => token.Kind == TokenKind.Comment &&
                (package is null || token.Line < package.Line))
            .Select(token => token.Text.StartsWith("go:build ", StringComparison.Ordinal) ||
                             token.Text.StartsWith("go:build\t", StringComparison.Ordinal)
                ? new Declaration(token.Text[9..].Trim(), token.Line)
                : null).Where(value => value is not null).Cast<Declaration>().ToList();
        if (constraints.Count == 0)
            constraints.AddRange(lexed.Where(token => token.Kind == TokenKind.Comment &&
                    (package is null || token.Line < package.Line) &&
                    token.Text.TrimStart().StartsWith("+build ", StringComparison.Ordinal))
                .Select(token => new Declaration(token.Text.TrimStart()[7..].Trim(), token.Line)));

        var buildCalls = new List<int>();
        if (isTestFile)
        {
            var execNames = imports.Where(import => import.Path == "os/exec" && import.LocalName != "_")
                .Select(import => import.LocalName).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i + 3 < tokens.Length; i++)
            {
                if (tokens[i].Kind != TokenKind.Identifier || !execNames.Contains(tokens[i].Text) ||
                    tokens[i + 1].Text != "." || tokens[i + 2].Kind != TokenKind.Identifier ||
                    tokens[i + 2].Text is not ("Command" or "CommandContext") || tokens[i + 3].Text != "(")
                    continue;
                var arguments = ReadArguments(tokens, i + 4);
                var first = tokens[i + 2].Text == "CommandContext" ? 1 : 0;
                if (arguments.Count > first + 1 && IsLiteral(arguments[first], "go") &&
                    IsLiteral(arguments[first + 1], "build"))
                    buildCalls.Add(tokens[i].Line);
            }
        }
        return new(package, imports, constraints.Where(value => value.Value.Length > 0).ToArray(), buildCalls);
    }

    private static bool Is(Token token, string identifier) =>
        token.Kind == TokenKind.Identifier && token.Text == identifier;

    private static bool IsLiteral(List<Token> tokens, string value) =>
        tokens.Count == 1 && tokens[0].Kind == TokenKind.String && tokens[0].Text == value;

    private static List<List<Token>> ReadArguments(Token[] tokens, int start)
    {
        var result = new List<List<Token>>();
        var argument = new List<Token>();
        var depth = 0;
        for (var i = start; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Symbol)
            {
                if (token.Text == ")" && depth == 0) { result.Add(argument); break; }
                if (token.Text == "," && depth == 0) { result.Add(argument); argument = []; continue; }
                if (token.Text is "(" or "[" or "{") depth++;
                if (token.Text is ")" or "]" or "}") depth--;
            }
            argument.Add(token);
        }
        return result;
    }

    // Shared with go.mod: comments and quoted text obey the same lexical boundaries.
    internal static List<Token> Tokenize(string text)
    {
        var result = new List<Token>();
        var line = 1;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(ch) || ch == '\uFEFF') { i++; continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var start = i += 2;
                while (i < text.Length && text[i] != '\n') i++;
                result.Add(new(TokenKind.Comment, text[start..i].TrimEnd('\r'), line));
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { i += 2; break; }
                    if (text[i++] == '\n') line++;
                }
                continue;
            }
            var tokenLine = line;
            if (ch is '"' or '`' or '\'')
            {
                var quote = ch;
                i++;
                var value = new StringBuilder();
                while (i < text.Length && text[i] != quote)
                {
                    ch = text[i++];
                    if (ch == '\n') line++;
                    if (quote != '`' && ch == '\\' && i < text.Length)
                        value.Append(ReadEscape(text, ref i));
                    else if (quote != '`' || ch != '\r') value.Append(ch);
                }
                if (i < text.Length) i++;
                result.Add(new(quote == '\'' ? TokenKind.Rune : TokenKind.String, value.ToString(), tokenLine));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                result.Add(new(TokenKind.Identifier, text[start..i], tokenLine));
                continue;
            }
            result.Add(new(TokenKind.Symbol, ch.ToString(), line));
            i++;
        }
        return result;
    }

    private static string ReadEscape(string text, ref int i)
    {
        var ch = text[i++];
        var digits = ch switch { 'x' => 2, 'u' => 4, 'U' => 8, >= '0' and <= '7' => 2, _ => 0 };
        if (digits > 0 && i + digits <= text.Length)
        {
            var number = ch is >= '0' and <= '7' ? ch + text.Substring(i, digits) : text.Substring(i, digits);
            i += digits;
            if (ch is >= '0' and <= '7')
            {
                if (number.All(c => c is >= '0' and <= '7'))
                    return ((char)Convert.ToInt32(number, 8)).ToString();
            }
            else if (int.TryParse(number, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) &&
                     code is >= 0 and <= 0x10ffff && code is not (>= 0xd800 and <= 0xdfff))
                return char.ConvertFromUtf32(code);
            return "\uFFFD";
        }
        return ch switch { 'a' => "\a", 'b' => "\b", 'f' => "\f", 'n' => "\n", 'r' => "\r", 't' => "\t", 'v' => "\v", _ => ch.ToString() };
    }
}
