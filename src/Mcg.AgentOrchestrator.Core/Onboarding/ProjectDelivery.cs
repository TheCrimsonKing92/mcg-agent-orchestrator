using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class ProjectDelivery
{
    public const string Direct = "direct";
    public const string PullRequest = "pull-request";
    public const string ModeKey = "delivery";
    public const string DeciderKey = "delivery-decider";

    public static string ValidateMode(string value) => value.ToLowerInvariant() switch
    {
        Direct => Direct,
        PullRequest => PullRequest,
        _ => throw new ArgumentException("delivery requires direct or pull-request with no surrounding whitespace.")
    };

    public static string ValidateDecider(string value)
    {
        if (value.Length is < 1 or > 39 ||
            !Regex.IsMatch(value, @"\A[a-zA-Z0-9]+(?:-[a-zA-Z0-9]+)*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("delivery-decider requires a code-host login: 1-39 letters or digits, with single internal hyphens.");
        return value;
    }
}
