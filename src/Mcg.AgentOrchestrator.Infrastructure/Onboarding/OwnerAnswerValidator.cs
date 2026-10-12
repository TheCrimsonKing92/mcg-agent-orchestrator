using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The explicit answer rules supported by this onboarding slice.</summary>
public static class OwnerAnswerValidator
{
    public const string DefaultTimeoutKey = "engine/timeouts/defaultMinutes";
    public const string BuildServerShutdownKey = "engine/timeouts/buildServerShutdownMinutes";
    public const string DiscoveryKey = "engine/timeouts/discoveryMinutes";
    public const string DotnetSdkKey = "environment/dotnet-sdk";
    public const int MaxMinutes = 1440;

    private static readonly Regex SdkVersion = new(
        @"\A[0-9]+(\.[0-9]+){1,3}(-[A-Za-z0-9]+(\.[A-Za-z0-9]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Validate(string factKey, string value)
    {
        value ??= "";
        if (factKey is DefaultTimeoutKey or BuildServerShutdownKey or DiscoveryKey ||
            (factKey.StartsWith("commands/", StringComparison.Ordinal) &&
             factKey.EndsWith("/timeoutMinutes", StringComparison.Ordinal) &&
             factKey.Length > "commands/".Length + "/timeoutMinutes".Length))
        {
            if (value.Length == 0 || value.Any(character => character is < '0' or > '9') ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
                minutes is < 1 or > MaxMinutes)
                throw new ArgumentException($"{factKey} requires a positive integer from 1 to {MaxMinutes} with no sign, decimal point or whitespace.");
            return minutes.ToString(CultureInfo.InvariantCulture);
        }

        if (factKey == DotnetSdkKey)
        {
            if (!SdkVersion.IsMatch(value))
                throw new ArgumentException($"{factKey} requires two to four dot-separated numeric version parts with an optional hyphen suffix of letters, digits and dots; wildcards, ranges and latest are refused.");
            return value;
        }

        if (factKey.StartsWith("commands/", StringComparison.Ordinal) &&
            factKey.EndsWith("/test", StringComparison.Ordinal) &&
            factKey.Length > "commands/".Length + "/test".Length)
        {
            if (value.Equals("VSTest", StringComparison.OrdinalIgnoreCase)) return "VSTest";
            if (value.Equals("MTP", StringComparison.OrdinalIgnoreCase)) return "MTP";
            throw new ArgumentException($"{factKey} requires VSTest or MTP with no surrounding whitespace.");
        }

        throw new ArgumentException($"{factKey} has no answer rule in this slice. Known keys: {DefaultTimeoutKey}, {BuildServerShutdownKey}, {DiscoveryKey}, {DotnetSdkKey}, commands/<unit>/test, commands/<unit>/timeoutMinutes.");
    }
}
