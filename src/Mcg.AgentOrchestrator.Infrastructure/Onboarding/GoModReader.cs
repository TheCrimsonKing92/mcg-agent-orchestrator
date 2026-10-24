namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads only top-level module, go and toolchain directives, without resolving dependencies.</summary>
internal static class GoModReader
{
    internal sealed record Directive(string Value, int Line);
    internal sealed record Module(Directive? Path, Directive? Go, Directive? Toolchain);

    internal static Module Read(string path)
    {
        Directive? module = null, go = null, toolchain = null;
        var depth = 0;
        foreach (var line in GoSourceFileReader.Tokenize(File.ReadAllText(path))
                     .Where(token => token.Kind != GoSourceFileReader.TokenKind.Comment).GroupBy(token => token.Line))
        {
            var tokens = line.ToArray();
            if (depth == 0 && tokens.Length >= 2 && tokens[0].Kind == GoSourceFileReader.TokenKind.Identifier &&
                tokens[1].Text != "(")
            {
                // Unquoted module paths and versions consist of several lexical tokens.
                var value = tokens[1].Kind == GoSourceFileReader.TokenKind.String ? tokens[1].Text
                    : string.Concat(tokens.Skip(1).Select(token => token.Text));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var directive = new Directive(value, tokens[0].Line);
                    switch (tokens[0].Text)
                    {
                        case "module": module ??= directive; break;
                        case "go": go ??= directive; break;
                        case "toolchain": toolchain ??= directive; break;
                    }
                }
            }
            foreach (var token in tokens.Where(token => token.Kind == GoSourceFileReader.TokenKind.Symbol))
            {
                if (token.Text == "(") depth++;
                else if (token.Text == ")") depth--;
            }
        }
        return new(module, go, toolchain);
    }
}
