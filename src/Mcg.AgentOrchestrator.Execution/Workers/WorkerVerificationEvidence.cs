using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerVerificationEvidence
{
    private const string LegacySnapshotUnavailableReason = "legacy-snapshot-authoritative-output-unavailable";
    private const int MalformedOutputExcerptMaxChars = 4000;
    private const int StructuredFieldMaxChars = 4000;
    private const int LocatedWorkerResultMaxChars = 20000;

    internal sealed record ContextOutput(
        string Content,
        bool IsAuthoritative,
        string? UnavailableReason);

    internal enum ContextProjectionValidation
    {
        Parsed,
        NonAuthoritative,
        Malformed
    }

    internal sealed record ContextProjection(
        string Content,
        ContextProjectionValidation Validation)
    {
        public string ValidationReceiptValue => Validation switch
        {
            ContextProjectionValidation.Parsed => "parsed",
            ContextProjectionValidation.NonAuthoritative => "non-authoritative",
            ContextProjectionValidation.Malformed => "malformed",
            _ => throw new ArgumentOutOfRangeException(nameof(Validation), Validation, null)
        };
    }

    public static string RequireAuthoritativeStandardOutput(
        TaskVerificationRecord verification,
        LogicalArtifactIdentity identity)
    {
        if (verification.AuthoritativeStandardOutput is { } authoritativeOutput)
        {
            return authoritativeOutput;
        }

        if (TryRecoverLegacySnapshotStandardOutput(verification, out var recoveredOutput))
        {
            return recoveredOutput;
        }

        throw new WorkerContextPreparationException(
            identity,
            verification.FullStandardOutputUnavailableReason ?? "authoritative-evidence-unavailable",
            "Complete stdout is unavailable; the bounded verification preview is not authoritative evidence.");
    }

    public static ContextOutput ResolveStandardOutputForContext(
        TaskVerificationRecord verification,
        LogicalArtifactIdentity identity)
    {
        if (verification.AuthoritativeStandardOutput is { } authoritativeOutput)
        {
            return new ContextOutput(authoritativeOutput, true, null);
        }

        if (TryRecoverLegacySnapshotStandardOutput(verification, out var recoveredOutput))
        {
            return new ContextOutput(recoveredOutput, true, null);
        }

        var unavailableReason = verification.FullStandardOutputUnavailableReason;
        if (unavailableReason is null)
        {
            // A post-contract record that claims no capture failure should have persisted authoritative
            // bytes. Keep that invariant loud; only explained capture failures may degrade to a preview.
            return new ContextOutput(
                RequireAuthoritativeStandardOutput(verification, identity),
                true,
                null);
        }

        var content = string.Join(
            Environment.NewLine,
            "[legacy verification context]",
            "authoritative: false",
            $"unavailable-reason: {unavailableReason}",
            "The complete historical stdout was never retained with an integrity digest. " +
            "The bounded preview below is context only and must not be treated as authoritative evidence.",
            string.Empty,
            verification.StandardOutput);
        return new ContextOutput(content, false, unavailableReason);
    }

    public static string ProjectStandardOutputForContext(
        TaskSpec task,
        TaskVerificationRecord verification,
        LogicalArtifactIdentity? identity = null)
        => ProjectStandardOutputForContextWithValidation(task, verification, identity).Content;

    public static string ProjectPriorTaskOutputForContext(TaskSpec task) =>
        ProjectPriorTaskOutputForContextWithValidation(task).Content;

    public static ContextProjection ProjectPriorTaskOutputForContextWithValidation(TaskSpec task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var latest = task.LastVerification ?? throw new ArgumentException(
            "Prior task has no verification output.", nameof(task));
        var latestProjection = ProjectStandardOutputForContextWithValidation(task, latest);
        if (!latest.Command.StartsWith("manual-verification ", StringComparison.Ordinal))
            return latestProjection;

        var operatorContent = latest.Command == "manual-verification failed"
            ? BoundHeadAndTail(latest.AuthoritativeStandardError ?? latest.StandardError,
                MalformedOutputExcerptMaxChars)
            : latestProjection.Content;
        var worker = task.VerificationHistory.LastOrDefault(record => record.WorkerResultPresent);
        if (worker is null)
            return new ContextProjection(operatorContent, latestProjection.Validation);

        var workerProjection = ProjectStandardOutputForContextWithValidation(task, worker);
        return new ContextProjection(string.Join(Environment.NewLine,
            "Worker-produced verification (last worker round):",
            workerProjection.Content,
            "Operator adjudication (latest verification):",
            operatorContent), workerProjection.Validation);
    }

    public static ContextProjection ProjectStandardOutputForContextWithValidation(
        TaskSpec task,
        TaskVerificationRecord verification,
        LogicalArtifactIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(verification);
        var artifactIdentity = identity ?? new LogicalArtifactIdentity($"prior/{task.Id.Value}/verification-output");
        var contextOutput = ResolveStandardOutputForContext(verification, artifactIdentity);
        var output = contextOutput.Content;
        var bytes = Encoding.UTF8.GetBytes(output);
        var sourceHandle = string.IsNullOrWhiteSpace(verification.StandardOutputPath)
            ? "host-captured-authoritative-output"
            : Path.GetFullPath(Path.IsPathRooted(verification.StandardOutputPath)
                ? verification.StandardOutputPath
                : Path.Combine(verification.WorkingDirectory, verification.StandardOutputPath));
        var receiptPrefix =
            $"Artifact receipt: purpose=prior-worker-output; stable_id={artifactIdentity.Value}; source_handle={sourceHandle}; chars={output.Length}; bytes={bytes.Length}; sha256={WorkerContextArtifact.Hash(bytes)}";
        if (!contextOutput.IsAuthoritative)
        {
            return new ContextProjection(
                $"{receiptPrefix}; validation=non-authoritative; problem_excerpt={contextOutput.UnavailableReason ?? "authoritative output unavailable"}" +
                    Environment.NewLine + BoundOutsideWorkerResult(output),
                ContextProjectionValidation.NonAuthoritative);
        }

        if (!WorkerResultParser.TryParseResult(output, out var parsed, out var diagnostic))
        {
            return new ContextProjection(
                $"{receiptPrefix}; validation=malformed; problem_excerpt={diagnostic}" +
                    Environment.NewLine + BoundOutsideWorkerResult(output),
                ContextProjectionValidation.Malformed);
        }

        var preferredOrder = new[]
        {
            "files", "commands", "tests", "commit", "blockers", "findings", "touched_anchors",
            "criteria_verdicts", "verdict", "citations", "model_fit", "skills", "confidence"
        };
        var orderedFields = preferredOrder
            .Where(parsed.Fields.ContainsKey)
            .Concat(parsed.Fields.Keys
                .Where(key => !preferredOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var oversizedFields = orderedFields
            .Where(key => parsed.Fields[key].Length > StructuredFieldMaxChars)
            .ToArray();
        var validationReceipt = oversizedFields.Length == 0
            ? "validation=parsed"
            : $"validation=malformed; problem_excerpt=structured fields exceed {StructuredFieldMaxChars} chars; oversized_fields={string.Join(',', oversizedFields)}";
        var validation = oversizedFields.Length == 0
            ? ContextProjectionValidation.Parsed
            : ContextProjectionValidation.Malformed;
        var lines = new List<string>
        {
            $"{receiptPrefix}; {validationReceipt}",
            "WORKER_RESULT:"
        };
        lines.AddRange(orderedFields.Select(key => ProjectStructuredField(key, parsed.Fields[key])));
        lines.Add("END_WORKER_RESULT");
        return new ContextProjection(string.Join(Environment.NewLine, lines), validation);
    }

    private static string ProjectStructuredField(string key, string value)
    {
        if (value.Length <= StructuredFieldMaxChars)
        {
            return $"{key}: {value}";
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return $"{key}: [oversized structured field omitted; chars={value.Length}; bytes={bytes.Length}; " +
            $"sha256={WorkerContextArtifact.Hash(bytes)}; complete source remains at source_handle]";
    }

    private static string BoundHeadAndTail(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        var headChars = maxChars / 2;
        var tailChars = maxChars - headChars;
        return trimmed[..headChars] +
            Environment.NewLine +
            $"...[{trimmed.Length - maxChars} chars omitted from malformed output; complete source remains at source_handle]..." +
            Environment.NewLine +
            trimmed[^tailChars..];
    }

    private static string BoundOutsideWorkerResult(string output)
    {
        var lines = output.Split('\n');
        var lineStart = 0;
        var openers = new List<(int Line, int Start)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (WorkerResultParser.IsOpener(lines[i].Trim()))
                openers.Add((i, lineStart));
            lineStart += lines[i].Length + 1;
        }
        if (openers.Count == 0)
            return BoundHeadAndTail(output, MalformedOutputExcerptMaxChars);

        var selected = openers.Count - 1;
        for (var i = selected; i >= 0; i--)
        {
            if (Array.FindIndex(lines, openers[i].Line + 1,
                    line => WorkerResultParser.IsEndMarker(line.Trim())) < 0)
                continue;
            selected = i;
            break;
        }
        var blockStart = openers[selected].Start;

        lineStart = 0;
        var blockEnd = output.Length;
        var fallbackEnd = output.Length;
        var insideBlock = false;
        var foundEndMarker = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lineStart == blockStart)
                insideBlock = true;
            else if (insideBlock && WorkerResultParser.IsEndMarker(lines[i].Trim()))
            {
                blockEnd = lineStart + lines[i].Length;
                foundEndMarker = true;
                break;
            }
            else if (insideBlock && fallbackEnd == output.Length &&
                     (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TrimStart().StartsWith('#')))
                fallbackEnd = lineStart;
            lineStart += lines[i].Length + 1;
        }
        if (!foundEndMarker)
            blockEnd = fallbackEnd;

        var block = output[blockStart..blockEnd];
        if (block.Length > LocatedWorkerResultMaxChars)
        {
            var bytes = Encoding.UTF8.GetBytes(block);
            block = $"[oversized WORKER_RESULT block omitted; chars={block.Length}; bytes={bytes.Length}; " +
                $"sha256={WorkerContextArtifact.Hash(bytes)}; complete source remains at source_handle]";
        }
        var before = BoundHeadAndTail(output[..blockStart], MalformedOutputExcerptMaxChars / 2);
        var after = BoundHeadAndTail(output[blockEnd..], MalformedOutputExcerptMaxChars / 2);
        return string.Join(Environment.NewLine, new[] { before, block, after }.Where(part => part.Length > 0));
    }

    public static bool TryRecoverLegacySnapshotStandardOutput(
        TaskVerificationRecord verification,
        out string output)
    {
        output = string.Empty;
        if (!string.Equals(
                verification.FullStandardOutputUnavailableReason,
                LegacySnapshotUnavailableReason,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            return false;
        }

        var recordedPath = verification.StandardOutputPath;
        var resolvedPath = Path.IsPathRooted(recordedPath)
            ? recordedPath
            : Path.Combine(verification.WorkingDirectory, recordedPath);
        try
        {
            var candidate = File.ReadAllText(resolvedPath);
            // A legacy snapshot recorded no digest. Its output file can become authoritative only when
            // the snapshot retained every character; matching a bounded head/tail preview cannot prove
            // that the omitted middle bytes are unchanged.
            if (candidate.Length > VerificationTextBounds.BoundThreshold ||
                !candidate.Equals(verification.StandardOutput, StringComparison.Ordinal))
            {
                return false;
            }

            output = candidate;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
