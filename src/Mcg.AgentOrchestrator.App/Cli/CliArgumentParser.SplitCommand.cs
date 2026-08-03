namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static IReadOnlyList<string> SplitCommand(string line)
{
    var firstSpace = line.IndexOf(' ');
    if (firstSpace < 0)
    {
        return [line];
    }

    var command = line[..firstSpace];
    var remainder = line[(firstSpace + 1)..].Trim();

    if (command.Equals("progress", StringComparison.OrdinalIgnoreCase))
    {
        var (commandRemainder, metadataFlags) = ExtractOperatorIntentMetadataFlags(remainder);
        return [.. SplitTaskTargetCommandWithTextFileFlag(command, commandRemainder, 2), .. metadataFlags];
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase))
    {
        var (commandRemainder, metadataFlags) = ExtractOperatorIntentMetadataFlags(remainder);
        return [.. SplitRetryCommand(command, commandRemainder), .. metadataFlags];
    }

    if (command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        var (noteRemainder, gateFlags) = ExtractRepeatedValueFlag(remainder, "--gate-deliverable");
        return [.. SplitTaskTargetCommandWithTextFileFlag(command, noteRemainder, 1), .. gateFlags];
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("unpark-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitGoalDispositionCommand(command, remainder);
    }

    if (command.Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        const string limitReviewFlag = "--confirm-limit-review";
        var flagIndex = remainder.IndexOf(limitReviewFlag, StringComparison.OrdinalIgnoreCase);
        if (flagIndex >= 0)
        {
            var before = remainder[..flagIndex].Trim();
            var note = remainder[(flagIndex + limitReviewFlag.Length)..].Trim();
            var beforeParts = string.IsNullOrWhiteSpace(before)
                ? []
                : before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fileFlagIndex = IndexOfAnyFileFlag(note, "--text-file");
            if (fileFlagIndex >= 0)
            {
                var inlineNote = note[..fileFlagIndex].Trim();
                var flags = note[fileFlagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return string.IsNullOrWhiteSpace(inlineNote)
                    ? [command, .. beforeParts, limitReviewFlag, .. flags]
                    : [command, .. beforeParts, limitReviewFlag, inlineNote, .. flags];
            }

            return string.IsNullOrWhiteSpace(note)
                ? [command, .. beforeParts, limitReviewFlag]
                : [command, .. beforeParts, limitReviewFlag, note];
        }
    }

    if (command.Equals("verification-plan", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("ask-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, remainder];
    }

    if (command.Equals("gate-satisfied", StringComparison.OrdinalIgnoreCase))
    {
        return SplitGateSatisfiedCommand(command, remainder);
    }

    if (command.Equals("ask", StringComparison.OrdinalIgnoreCase) || command.Equals("answer", StringComparison.OrdinalIgnoreCase))
    {
        if (command.Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            return SplitTaskTargetCommand(command, remainder, 1);
        }

        var (answerRemainder, gateFlags) = ExtractRepeatedValueFlag(remainder, "--gate-deliverable");
        return [.. SplitTargetTextCommandWithFileFlags(command, answerRemainder, "--text-file"), .. gateFlags];
    }

    if (command.Equals("attention", StringComparison.OrdinalIgnoreCase))
    {
        if (remainder.StartsWith("answer ", StringComparison.OrdinalIgnoreCase) &&
            IndexOfAnyFileFlag(remainder, "--text-file") >= 0)
        {
            return SplitAttentionAnswerWithFileFlag(command, remainder);
        }

        var rest = remainder.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length >= 4 &&
            rest[0].Equals("answer", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeAttentionClarificationId(rest[2])
            ? [command, rest[0], rest[1], rest[2], rest[3]]
            : [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
    {
        var (commandRemainder, metadataFlags) = ExtractOperatorIntentMetadataFlags(remainder);
        return [.. SplitTaskTargetCommandWithTextFileFlag(command, commandRemainder, 2), .. metadataFlags];
    }

    if (command.Equals("dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 2);
    }

    if (command.Equals("worker-profile", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("worker-profile-export", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-import", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-check", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--body-file", "--text-file");
    }

    if (command.Equals("backlog-update", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetCommandWithValueFlags(command, remainder, "--title", "--description", "--priority", "--tags", "--status");
    }

    if (command.Equals("goal-amend", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetCommandWithValueFlags(command, remainder, "--waive", "--reason", "--reason-file", "--text-file", "--actor");
    }

    if (command.Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--reason-file", "--text-file");
    }

    if (command.Equals("backlog-annotate", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file");
    }

    if (command.Equals("goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
    }

    if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(
            command,
            remainder,
            "--text-file",
            "--as",
            "--confirm-goal-stop",
            "--confirm-goal-park",
            "--confirm-goal-abandon");
    }

    if (command.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-listen", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("simple-goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
    }

    if (command.Equals("lifecycle-simple-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("lifecycle-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("goal-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("backlog-intake", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("intent-template", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
    }

    if (command.Equals("add-task", StringComparison.OrdinalIgnoreCase))
    {
        return SplitAddTaskCommand(command, remainder);
    }

    if (command.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agent-add", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeAgentArgs([command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
    }

    if (command.Equals("tasks", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    return IsSimpleCommand(command)
        ? SplitSimpleCommandWithFlags(command, remainder)
        : [command, remainder];
}

private static IReadOnlyList<string> SplitAddTaskCommand(string command, string remainder)
{
    const string goalPrefixFlag = "--goal ";
    if (!remainder.StartsWith(goalPrefixFlag, StringComparison.OrdinalIgnoreCase))
    {
        return SplitAddTaskBeforeRoleFlag(
            SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file"));
    }

    var afterFlag = remainder[goalPrefixFlag.Length..].TrimStart();
    var prefixEnd = afterFlag.IndexOf(' ');
    if (prefixEnd < 0)
    {
        return [command, "--goal", afterFlag];
    }

    var goalPrefix = afterFlag[..prefixEnd];
    var roleAndText = SplitTargetTextCommandWithFileFlags(
        command,
        afterFlag[(prefixEnd + 1)..].TrimStart(),
        "--text-file");
    var parts = new List<string> { command, "--goal", goalPrefix };
    parts.AddRange(roleAndText.Skip(1));
    return SplitAddTaskBeforeRoleFlag(parts);
}

private static IReadOnlyList<string> SplitAddTaskBeforeRoleFlag(IReadOnlyList<string> source)
{
    var parts = source.ToList();
    var descriptionIndex = parts.Count > 1 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase)
        ? 4
        : 2;
    if (parts.Count == descriptionIndex + 1)
    {
        var beforeRoleIndex = IndexOfStandaloneFlag(parts[descriptionIndex], "--before-role");
        if (beforeRoleIndex >= 0)
        {
            var description = parts[descriptionIndex][..beforeRoleIndex].Trim();
            var beforeRole = parts[descriptionIndex][(beforeRoleIndex + "--before-role".Length)..].Trim();
            parts.RemoveAt(descriptionIndex);
            if (description.Length > 0)
            {
                parts.Add(description);
            }

            parts.Add("--before-role");
            if (beforeRole.Length > 0)
            {
                parts.Add(beforeRole);
            }
        }
    }

    return parts;
}

private static IReadOnlyList<string> SplitAttentionAnswerWithFileFlag(string command, string remainder)
{
    var rest = remainder.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
    if (rest.Length >= 4 && LooksLikeAttentionClarificationId(rest[2]))
    {
        var flagIndex = IndexOfAnyFileFlag(rest[3], "--text-file");
        if (flagIndex < 0)
        {
            return [command, rest[0], rest[1], rest[2], rest[3]];
        }

        var inlineText = rest[3][..flagIndex].Trim();
        var flags = rest[3][flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.IsNullOrWhiteSpace(inlineText)
            ? [command, rest[0], rest[1], rest[2], .. flags]
            : [command, rest[0], rest[1], rest[2], inlineText, .. flags];
    }

    var globalRemainder = remainder["answer ".Length..].Trim();
    return [command, "answer", .. SplitTargetTextCommandWithFileFlags("answer", globalRemainder, "--text-file").Skip(1)];
}

private static IReadOnlyList<string> SplitSimpleCommandWithFlags(string command, string remainder)
{
    var flagIndex = remainder.IndexOf(" --", StringComparison.Ordinal);
    if (flagIndex < 0 && remainder.StartsWith("--", StringComparison.Ordinal))
    {
        flagIndex = 0;
    }

    if (flagIndex < 0)
    {
        return [command, .. remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)];
    }

    var beforeFlags = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (string.IsNullOrWhiteSpace(beforeFlags))
    {
        return [command, .. flags];
    }

    return [command, .. beforeFlags.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries), .. flags];
}

private static IReadOnlyList<string> SplitTaskTargetCommand(string command, string remainder, int trailingArgumentCount)
{
    var maxParts = 2 + trailingArgumentCount;
    var rest = remainder.Split(' ', maxParts, StringSplitOptions.RemoveEmptyEntries);
    if (rest.Length == maxParts && LooksLikePositionalGoalTask(rest[0], rest[1]))
    {
        return [command, .. rest];
    }

    var goalFlagMaxParts = 3 + trailingArgumentCount;
    var goalFlagRest = remainder.Split(' ', goalFlagMaxParts, StringSplitOptions.RemoveEmptyEntries);
    if (goalFlagRest.Length == goalFlagMaxParts && goalFlagRest[0].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. goalFlagRest];
    }

    var legacyRest = remainder.Split(' ', 1 + trailingArgumentCount, StringSplitOptions.RemoveEmptyEntries);
    return [command, .. legacyRest];
}

private static IReadOnlyList<string> SplitTaskTargetCommandWithTextFileFlag(string command, string remainder, int trailingArgumentCount)
{
    var flagIndex = IndexOfAnyFileFlag(remainder, "--text-file");
    if (flagIndex < 0)
    {
        return SplitTaskTargetCommand(command, remainder, trailingArgumentCount);
    }

    var beforeFlag = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.IsNullOrWhiteSpace(beforeFlag)
        ? [command, .. flags]
        : [.. SplitTaskTargetCommand(command, beforeFlag, trailingArgumentCount), .. flags];
}

private static IReadOnlyList<string> SplitGateSatisfiedCommand(string command, string remainder)
{
    var parts = remainder.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 3)
    {
        return [command, .. parts];
    }

    var evidence = parts[2].Trim();
    if (evidence.StartsWith("--text-file ", StringComparison.OrdinalIgnoreCase))
    {
        return [command, parts[0], parts[1], .. evidence.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    return [command, parts[0], parts[1], evidence];
}

private static (string Remainder, IReadOnlyList<string> Flags) ExtractRepeatedValueFlag(
    string remainder,
    string flag)
{
    var pattern = $@"(?i)(?:^|\s){System.Text.RegularExpressions.Regex.Escape(flag)}\s+(?<value>[^\s]+)";
    var matches = System.Text.RegularExpressions.Regex.Matches(remainder, pattern);
    var flags = matches
        .SelectMany(match => new[] { flag, match.Groups["value"].Value })
        .ToArray();
    var stripped = System.Text.RegularExpressions.Regex.Replace(remainder, pattern, " ").Trim();
    return (stripped, flags);
}

private static IReadOnlyList<string> SplitRetryCommand(string command, string remainder)
{
    var parts = SplitTaskTargetCommandWithTextFileFlag(command, remainder, 1);
    var mechanicalIndex = parts
        .Select((part, index) => (part, index))
        .FirstOrDefault(item => item.part.Equals("--mechanical", StringComparison.OrdinalIgnoreCase));
    if (mechanicalIndex.part is not null)
    {
        return parts;
    }

    if (parts.Count < 3)
    {
        return parts;
    }

    var messageIndex = ResolveRetryMessageIndex(parts);
    if (messageIndex is null)
    {
        return parts;
    }

    var message = parts[messageIndex.Value];
    var flagIndex = IndexOfStandaloneFlag(message, "--mechanical");
    if (flagIndex < 0)
    {
        return parts;
    }

    var beforeFlag = message[..flagIndex].Trim();
    var afterFlag = message[(flagIndex + "--mechanical".Length)..].Trim();
    var normalized = parts.ToList();
    normalized[messageIndex.Value] = string.IsNullOrWhiteSpace(afterFlag)
        ? beforeFlag
        : string.Join(' ', [beforeFlag, afterFlag]).Trim();
    normalized.Add("--mechanical");
    return normalized.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
}

private static (string CommandRemainder, IReadOnlyList<string> MetadataFlags) ExtractOperatorIntentMetadataFlags(
    string remainder)
{
    const string idempotencyFlag = "--idempotency-key";
    var flagIndex = remainder.LastIndexOf($" {idempotencyFlag} ", StringComparison.OrdinalIgnoreCase);
    if (flagIndex < 0 && remainder.StartsWith($"{idempotencyFlag} ", StringComparison.OrdinalIgnoreCase))
    {
        flagIndex = 0;
    }

    if (flagIndex < 0)
    {
        return (remainder, []);
    }

    var commandRemainder = remainder[..flagIndex].Trim();
    var metadata = TokenizeQuotedArguments(remainder[flagIndex..].Trim());
    if (metadata.Count != 4 ||
        !metadata[0].Equals(idempotencyFlag, StringComparison.OrdinalIgnoreCase) ||
        !metadata[2].Equals("--operator-actor", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException(
            "Operator intent metadata must be '--idempotency-key <key> --operator-actor <actor>'.");
    }

    return (commandRemainder, metadata);
}

private static IReadOnlyList<string> TokenizeQuotedArguments(string value)
{
    var parts = new List<string>();
    var current = new System.Text.StringBuilder();
    var quoted = false;
    for (var index = 0; index < value.Length; index++)
    {
        var character = value[index];
        if (character == '\\' && index + 1 < value.Length && value[index + 1] == '"')
        {
            current.Append('"');
            index++;
            continue;
        }

        if (character == '"')
        {
            quoted = !quoted;
            continue;
        }

        if (char.IsWhiteSpace(character) && !quoted)
        {
            if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            continue;
        }

        current.Append(character);
    }

    if (quoted)
    {
        throw new ArgumentException("Operator intent metadata contains an unterminated quoted value.");
    }

    if (current.Length > 0)
    {
        parts.Add(current.ToString());
    }

    return parts;
}

private static int? ResolveRetryMessageIndex(IReadOnlyList<string> parts)
{
    if (parts.Count >= 5 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        return 4;
    }

    if (parts.Count >= 4 && LooksLikePositionalGoalTask(parts[1], parts[2]))
    {
        return 3;
    }

    return parts.Count >= 3 ? 2 : null;
}

private static bool LooksLikePositionalGoalTask(string first, string second)
{
    return !first.StartsWith("--", StringComparison.Ordinal) &&
        (!int.TryParse(first, out _) || first.Length >= 8) &&
        int.TryParse(second, out _);
}

private static IReadOnlyList<string> SplitTargetTextCommandWithFileFlags(string command, string remainder, params string[] fileFlags)
{
    var targetEnd = remainder.IndexOf(' ');
    if (targetEnd < 0)
    {
        return [command, remainder];
    }

    var target = remainder[..targetEnd];
    var rest = remainder[(targetEnd + 1)..].Trim();
    var flagIndex = IndexOfAnyFileFlag(rest, fileFlags);

    if (flagIndex < 0)
    {
        return [command, target, rest];
    }

    var inlineText = rest[..flagIndex].Trim();
    var flags = rest[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.IsNullOrWhiteSpace(inlineText)
        ? [command, target, .. flags]
        : [command, target, inlineText, .. flags];
}

private static IReadOnlyList<string> SplitGoalDispositionCommand(string command, string remainder)
{
    var parts = SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file");
    if (parts.Count != 3 ||
        !TryGetGoalDispositionConfirmationFlag(command, out var confirmationFlag))
    {
        return parts;
    }

    var text = parts[2];
    var confirmationIndex = IndexOfStandaloneFlag(text, confirmationFlag);
    if (confirmationIndex < 0)
    {
        return parts;
    }

    var reason = text[..confirmationIndex].Trim();
    return string.IsNullOrWhiteSpace(reason)
        ? [parts[0], parts[1], confirmationFlag]
        : [parts[0], parts[1], reason, confirmationFlag];
}

private static IReadOnlyList<string> SplitTargetCommandWithValueFlags(string command, string remainder, params string[] valueFlags)
{
    var firstSpace = remainder.IndexOf(' ');
    if (firstSpace < 0)
        return [command, remainder];

    var result = new List<string> { command, remainder[..firstSpace] };
    var tail = remainder[(firstSpace + 1)..].Trim();
    var tokens = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    for (var i = 0; i < tokens.Length; i++)
    {
        var token = tokens[i];
        if (!valueFlags.Any(flag => flag.Equals(token, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(token);
            continue;
        }

        result.Add(token);
        var value = new List<string>();
        for (i++; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith("--", StringComparison.Ordinal))
            {
                i--;
                break;
            }

            value.Add(tokens[i]);
        }

        if (value.Count > 0)
            result.Add(string.Join(' ', value));
    }

    return result;
}

private static bool TryGetGoalDispositionConfirmationFlag(string command, out string flag)
{
    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase))
    {
        flag = "--confirm-goal-abandon";
        return true;
    }

    if (command.Equals("park-goal", StringComparison.OrdinalIgnoreCase))
    {
        flag = "--confirm-goal-park";
        return true;
    }

    if (command.Equals("unpark-goal", StringComparison.OrdinalIgnoreCase))
    {
        flag = "--confirm-goal-unpark";
        return true;
    }

    if (command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase))
    {
        flag = "--confirm-goal-rollback";
        return true;
    }

    if (command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        flag = "--confirm-goal-stop";
        return true;
    }

    flag = "";
    return false;
}

private static int IndexOfStandaloneFlag(string text, string flag)
{
    var index = text.IndexOf($" {flag}", StringComparison.OrdinalIgnoreCase);
    if (index >= 0)
    {
        return index + 1;
    }

    return text.StartsWith(flag, StringComparison.OrdinalIgnoreCase) ? 0 : -1;
}

private static int IndexOfAnyFileFlag(string text, params string[] fileFlags)
{
    var best = -1;
    foreach (var fileFlag in fileFlags)
    {
        var index = text.IndexOf($" {fileFlag}", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            index++;
        }
        else if (text.StartsWith(fileFlag, StringComparison.OrdinalIgnoreCase))
        {
            index = 0;
        }

        if (index >= 0 && (best < 0 || index < best))
        {
            best = index;
        }
    }

    return best;
}

private static IReadOnlyList<string> SplitObjectiveCommandWithFlags(string command, string remainder)
{
    var fileFlagIndex = IndexOfAnyFileFlag(remainder, "--brief-file", "--text-file");
    if (fileFlagIndex >= 0)
    {
        var fileObjective = remainder[..fileFlagIndex].Trim();
        var beforeFileFlag = string.IsNullOrWhiteSpace(fileObjective)
            ? new[] { command }
            : [command, fileObjective];
        return [.. beforeFileFlag, .. remainder[fileFlagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    var flagIndex = remainder.IndexOf(" --", StringComparison.Ordinal);
    if (flagIndex < 0 && remainder.StartsWith("--", StringComparison.Ordinal))
    {
        flagIndex = 0;
    }

    if (flagIndex < 0)
    {
        return [command, remainder];
    }

    var objective = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.IsNullOrWhiteSpace(objective)
        ? [command, .. flags]
        : [command, objective, .. flags];
}
}
