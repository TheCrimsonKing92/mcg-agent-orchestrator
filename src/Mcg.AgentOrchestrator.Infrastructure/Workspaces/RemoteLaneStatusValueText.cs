namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneStatusValueText
{
    internal static string? Sanitize(string? value)
    {
        if (value is null) return null;
        var characters = value[..Math.Min(value.Length, 80)].ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or ' ' or '-'))
                characters[index] = '?';
        }
        return new string(characters) + (value.Length > 80 ? "..." : "");
    }
}
