using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OwnerProtectedGateDiagnostic
{
    private const int MaxDetailLength = 512;

    internal static AcceptanceCheckResult? Find(AcceptanceVerificationResult result) =>
        result.Checks?.FirstOrDefault(check => !check.Passed && !check.Advisory &&
            (check.Name.Trim().Equals("owner-protected configuration", StringComparison.OrdinalIgnoreCase) ||
             check.Name.Trim().Equals("acceptance manifest trusted dimensions", StringComparison.OrdinalIgnoreCase)));

    internal static void Write(TextWriter writer, string gateIdentity, AcceptanceCheckResult check) =>
        writer.WriteLine($"ACCEPTANCE_OWNER_PROTECTED_FAILURE gate={SingleLine(gateIdentity)} " +
            $"check={SingleLine(check.Name)} detail={SingleLine(check.OutputTail)}");

    private static string SingleLine(string? value)
    {
        var text = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in value ?? string.Empty)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = text.Length > 0;
                continue;
            }
            if (pendingSpace)
                text.Append(' ');
            pendingSpace = false;
            text.Append(character);
            if (text.Length > MaxDetailLength)
                return text.ToString(0, MaxDetailLength - 3) + "...";
        }
        return text.ToString();
    }
}
