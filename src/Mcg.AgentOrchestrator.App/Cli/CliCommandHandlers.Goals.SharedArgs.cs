using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static string? GetOptionalArgument(IReadOnlyList<string> parts, params string[] flags)
{
    for (var i = 1; i < parts.Count; i++)
    {
        var part = parts[i];
        if (flags.Any(flag => part.Equals(flag, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (IsCliValueFlag(part))
        {
            i++;
            continue;
        }

        return part;
    }

    return null;
}

private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
{
    for (var i = 1; i < parts.Count; i++)
    {
        var inlinePrefix = flag + "=";
        if (parts[i].StartsWith(inlinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return parts[i][inlinePrefix.Length..];
        }

        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            return i + 1 < parts.Count ? parts[i + 1] : null;
        }
    }

    return null;
}

private static IReadOnlyList<string> GetFlagValues(IReadOnlyList<string> parts, string flag)
{
    var results = new List<string>();
    for (var i = 1; i < parts.Count - 1; i++)
    {
        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            results.Add(parts[i + 1]);
    }
    return results;
}

private static IReadOnlyList<CriterionDispositionRequest> ParseCriterionDispositions(IReadOnlyList<string> parts)
{
    var results = new List<CriterionDispositionRequest>();
    foreach (var (flag, fromFile) in new[]
             {
                 ("--disposition", false),
                 ("--disposition-file", true)
             })
    {
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{flag} requires <criterion-reference>=<{(fromFile ? "path" : "prose")}>.");
            }

            var value = parts[++index];
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
            {
                throw new ArgumentException($"{flag} requires <criterion-reference>=<{(fromFile ? "path" : "prose")}>.");
            }

            var criterionReference = value[..separator].Trim();
            var dispositionValue = value[(separator + 1)..].Trim();
            if (fromFile)
            {
                if (!File.Exists(dispositionValue))
                {
                    throw new InvalidOperationException($"--disposition-file not found: {dispositionValue}");
                }

                dispositionValue = File.ReadAllText(dispositionValue, System.Text.Encoding.UTF8);
            }

            results.Add(new CriterionDispositionRequest(criterionReference, dispositionValue));
        }
    }

    return results;
}

private static IReadOnlyList<string> GetOperatorGateDeliverableIds(IReadOnlyList<string> parts)
{
    for (var index = 0; index < parts.Count; index++)
    {
        if (!parts[index].Equals("--gate-deliverable", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("--gate-deliverable requires a non-empty deliverable id.");
        }
    }

    return GetFlagValues(parts, "--gate-deliverable")
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

private static List<string> RemoveFlagWithValue(IReadOnlyList<string> parts, string flag)
{
    var result = new List<string>(parts.Count);
    for (var i = 0; i < parts.Count; i++)
    {
        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            i++; // skip the flag value too
            continue;
        }

        result.Add(parts[i]);
    }

    return result;
}

private static AutonomyPolicy ResolveCliAutonomyPolicy(IReadOnlyList<string> parts)
{
    return AutonomyPolicy.Parse(GetFlagValue(parts, "--autonomy") ?? GetFlagValue(parts, "--autonomy-policy"));
}

private static TimeSpan? ResolveWatchStallWarningThreshold(IReadOnlyList<string> parts)
{
    if (GetFlagValue(parts, "--stall-warning-seconds") is { } secondsValue)
    {
        return TimeSpan.FromSeconds(ParsePositiveInteger(secondsValue, "--stall-warning-seconds"));
    }

    if (GetFlagValue(parts, "--stall-warning-minutes") is { } minutesValue)
    {
        return TimeSpan.FromMinutes(ParsePositiveInteger(minutesValue, "--stall-warning-minutes"));
    }

    return null;
}

private static int ResolveConductPollSeconds(IReadOnlyList<string> parts)
{
    if (GetFlagValue(parts, "--poll-seconds") is { } pollSeconds)
    {
        return ParsePositiveInteger(pollSeconds, "--poll-seconds");
    }

    if (HasCliConfirmation(parts, "--poll-seconds"))
    {
        throw new ArgumentException("--poll-seconds requires a positive integer value.");
    }

    if (GetFlagValue(parts, "--watch-interval") is { } watchInterval)
    {
        return ParsePositiveInteger(watchInterval, "--poll-seconds");
    }

    if (HasCliConfirmation(parts, "--watch-interval"))
    {
        throw new ArgumentException("--poll-seconds requires a positive integer value.");
    }

    return ConductorBatchLoop.DefaultWatchIntervalSeconds;
}

private static int ResolveUnscopedStallTickThreshold(IReadOnlyList<string> parts)
{
    if (GetFlagValue(parts, "--unscoped-stall-ticks") is { } ticks)
    {
        return ParsePositiveInteger(ticks, "--unscoped-stall-ticks");
    }

    if (HasCliConfirmation(parts, "--unscoped-stall-ticks"))
    {
        throw new ArgumentException("--unscoped-stall-ticks requires a positive integer value.");
    }

    return ConductorBatchLoop.DefaultUnscopedStallTickThreshold;
}

private static int ParsePositiveInteger(string value, string flag)
{
    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{flag} requires a positive integer value.");
    }

    return parsed;
}

private static void EnsurePolicyAllows(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation)
{
    if (!TryEnsurePolicyAllows(context, goal, policy, action, operation, out var error))
    {
        throw new InvalidOperationException(error);
    }
}

private static bool TryEnsurePolicyAllows(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation,
    out string error)
{
    var allowed = policy.Allows(action);
    AutonomyPolicyEvidence.Record(context.Kernel, goal, policy, action, operation, allowed);
    if (allowed)
    {
        error = string.Empty;
        return true;
    }

    try
    {
        policy.ThrowIfDisallowed(action, operation);
        error = string.Empty;
        return true;
    }
    catch (InvalidOperationException ex)
    {
        error = ex.Message;
        return false;
    }
}

private static void RecordPolicyAllowed(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation)
{
    AutonomyPolicyEvidence.Record(context.Kernel, goal, policy, action, operation, allowed: true);
}

private static string? GetFirstNonFlagArgument(IReadOnlyList<string> parts, int startIndex)
{
    for (var i = startIndex; i < parts.Count; i++)
    {
        var part = parts[i];
        if (IsCliValueFlag(part))
        {
            i++;
            continue;
        }

        if (!part.StartsWith("--", StringComparison.Ordinal))
        {
            return part;
        }
    }

    return null;
}

private static bool IsCliValueFlag(string part)
{
    return GoalRoleAgentFlags.ContainsKey(part) ||
        part.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--backlog-coverage", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--backlog-item", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--brief-file", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--request-key", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--pipeline", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--complex-model", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--confirm-limit-review", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription-model", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription-reasoning", StringComparison.OrdinalIgnoreCase);
}

private static string ResolveBriefObjective(IReadOnlyList<string> parts, string usage)
{
    return ResolveTextArgument(parts, inlineIndex: 1, usage, "--brief-file", "--text-file");
}

private static string ResolveTextArgument(IReadOnlyList<string> parts, int inlineIndex, string usage, params string[] fileFlags)
{
    var value = ResolveTextArgumentOrDefaultCore(
        parts,
        inlineIndex,
        defaultValue: null,
        allowStandardInput: false,
        context: null,
        fileFlags);
    if (value is null)
    {
        throw new ArgumentException($"Usage: {usage}");
    }

    return value;
}

private static string? ResolveTextArgumentOrDefault(IReadOnlyList<string> parts, int inlineIndex, string? defaultValue, params string[] fileFlags)
{
    return ResolveTextArgumentOrDefaultCore(
        parts,
        inlineIndex,
        defaultValue,
        allowStandardInput: false,
        context: null,
        fileFlags);
}

private static string ResolveTextArgumentAllowStandardInput(
    CliExecutionContext context,
    IReadOnlyList<string> parts,
    int inlineIndex,
    string usage,
    params string[] fileFlags)
{
    var value = ResolveTextArgumentOrDefaultCore(
        parts,
        inlineIndex,
        defaultValue: null,
        allowStandardInput: true,
        context,
        fileFlags);
    return value ?? throw new ArgumentException($"Usage: {usage}");
}

private static string? ResolveTextArgumentOrDefaultAllowStandardInput(
    CliExecutionContext context,
    IReadOnlyList<string> parts,
    int inlineIndex,
    string? defaultValue,
    params string[] fileFlags)
{
    return ResolveTextArgumentOrDefaultCore(
        parts,
        inlineIndex,
        defaultValue,
        allowStandardInput: true,
        context,
        fileFlags);
}

private static string? ResolveTextArgumentOrDefaultCore(
    IReadOnlyList<string> parts,
    int inlineIndex,
    string? defaultValue,
    bool allowStandardInput,
    CliExecutionContext? context,
    params string[] fileFlags)
{
    var presentFlags = fileFlags
        .Where(flag => parts.Any(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    var hasInlineText = parts.Count > inlineIndex && !parts[inlineIndex].StartsWith("--", StringComparison.Ordinal);
    if (presentFlags.Length == 0)
    {
        if (!hasInlineText)
        {
            return defaultValue;
        }

        var inlineParts = parts
            .Skip(inlineIndex)
            .TakeWhile(part => !part.StartsWith("--", StringComparison.Ordinal));
        return string.Join(' ', inlineParts);
    }

    if (hasInlineText)
    {
        throw new ArgumentException($"Provide either inline text or {presentFlags[0]} <path>, not both.");
    }

    if (presentFlags.Length > 1)
    {
        throw new ArgumentException($"Provide only one text file option: {string.Join(", ", presentFlags)}.");
    }

    var flag = presentFlags[0];
    var path = GetFlagValue(parts, flag)
        ?? throw new ArgumentException($"{flag} requires <path>.");
    if (allowStandardInput && path.Equals("-", StringComparison.Ordinal))
    {
        if (context is null || !context.IsStandardInputRedirected)
        {
            throw new InvalidOperationException("Standard input is not redirected; pipe content or provide a file.");
        }

        return context.StandardInput.ReadToEnd();
    }

    if (!File.Exists(path))
    {
        throw new InvalidOperationException($"{flag} not found: {path}");
    }

    return File.ReadAllText(path, System.Text.Encoding.UTF8);
}

private static string? ResolveFlagTextArgumentOrDefault(IReadOnlyList<string> parts, string valueFlag, params string[] fileFlags)
{
    var flagIndex = parts.ToList().FindIndex(part => part.Equals(valueFlag, StringComparison.OrdinalIgnoreCase));
    if (flagIndex < 0)
    {
        return null;
    }

    return ResolveTextArgumentOrDefault(parts, flagIndex + 1, defaultValue: null, fileFlags);
}

private static void EnsureCliConfirmation(IReadOnlyList<string> parts, string flag, string message)
{
    if (HasCliConfirmation(parts, flag))
    {
        return;
    }

    throw new InvalidOperationException(message);
}

private static bool HasCliConfirmation(IReadOnlyList<string> parts, string flag)
{
    return parts.Any(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
}
