using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceFailureCauseReceiptV1(
    int ContractVersion,
    string Kind,
    string Owner,
    string ProbeClassification,
    bool ProcessStarted,
    int? ExitCode,
    long StandardOutputByteCount,
    long StandardErrorByteCount,
    bool DrainTimedOut,
    bool TimedOut,
    bool DrainFailed,
    string RepositoryHeadState,
    string Check,
    string FixtureAttemptId,
    int ProbeOrdinal);

internal static class AcceptanceFailureCauseReceiptCodec
{
    internal const string Prefix = "MCG_ACCEPTANCE_CAUSE_V1:";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Regex Marker = new(
        Regex.Escape(Prefix) + "(?<payload>[A-Za-z0-9+/=]+)",
        RegexOptions.CultureInvariant);

    internal static string Format(AcceptanceFailureCauseReceiptV1 receipt) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, Options)));

    internal static bool TryParse(string message, out AcceptanceFailureCauseReceiptV1 receipt)
    {
        receipt = null!;
        var matches = Marker.Matches(message ?? string.Empty);
        if (matches.Count != 1)
        {
            return false;
        }

        try
        {
            receipt = JsonSerializer.Deserialize<AcceptanceFailureCauseReceiptV1>(
                Convert.FromBase64String(matches[0].Groups["payload"].Value),
                Options)!;
            return IsEnvironmentalApparatus(receipt);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            receipt = null!;
            return false;
        }
    }

    private static bool IsEnvironmentalApparatus(AcceptanceFailureCauseReceiptV1? receipt)
    {
        if (receipt is null ||
            receipt.ContractVersion != 1 ||
            !receipt.Kind.Equals("seeded-dispatch-repository-git-probe", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.Check) ||
            string.IsNullOrWhiteSpace(receipt.FixtureAttemptId) ||
            receipt.FixtureAttemptId.Equals("not-assigned", StringComparison.Ordinal) ||
            receipt.ProbeOrdinal <= 0)
        {
            return false;
        }

        return receipt.Owner switch
        {
            "ProcessOutputApparatus" => IsProcessOutputApparatus(receipt),
            "FixturePublication" => IsFixturePublicationApparatus(receipt),
            _ => false
        };
    }

    private static bool IsProcessOutputApparatus(AcceptanceFailureCauseReceiptV1 receipt)
    {
        if (receipt.RepositoryHeadState is not (
            "ValidLooseReference" or "ValidPackedReference" or "ValidDetachedHead"))
        {
            return false;
        }

        return IsProcessFaultClassification(receipt);
    }

    private static bool IsFixturePublicationApparatus(AcceptanceFailureCauseReceiptV1 receipt)
    {
        if (receipt.RepositoryHeadState is not (
            "RepositoryMissing" or "GitMetadataMissing" or "HeadMissing" or "HeadInvalid" or
            "ReferenceMissing" or "ReferenceInvalid"))
        {
            return false;
        }

        return receipt.ProbeClassification switch
        {
            "NotRun" => !receipt.ProcessStarted && receipt.ExitCode is null &&
                receipt.StandardOutputByteCount == 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "NonZeroExit" => receipt.ProcessStarted && receipt.ExitCode is not null and not 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "InvalidRequiredOutput" => receipt.ProcessStarted && receipt.ExitCode == 0 &&
                receipt.StandardOutputByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            _ => IsProcessFaultClassification(receipt)
        };
    }

    private static bool IsProcessFaultClassification(AcceptanceFailureCauseReceiptV1 receipt) =>
        receipt.ProbeClassification switch
        {
            "EmptyRequiredOutput" => receipt.ProcessStarted && receipt.ExitCode == 0 &&
                receipt.StandardOutputByteCount == 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "LaunchFailure" => !receipt.ProcessStarted && receipt.ExitCode is null &&
                receipt.StandardOutputByteCount == 0 &&
                receipt.StandardErrorByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "ProcessTimeout" => receipt.ProcessStarted && receipt.TimedOut,
            "DrainTimeout" => receipt.ProcessStarted && !receipt.TimedOut && receipt.DrainTimedOut,
            "DrainFailure" => receipt.ProcessStarted && !receipt.TimedOut &&
                !receipt.DrainTimedOut && receipt.DrainFailed,
            "ProcessObservationFailure" => receipt.ProcessStarted &&
                receipt.StandardOutputByteCount == 0 &&
                receipt.StandardErrorByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            _ => false
        };
}

internal static class AcceptanceFailureCauseAdjudicator
{
    internal static AcceptanceCheckResult Attach(AcceptanceCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (check.Passed)
        {
            return check with { FailureCauseEvidence = null };
        }

        var trxCause = ExtractTrxFailureCauseEvidence(check.TestResultPaths, check.Name);
        if (check.FailureCauseEvidence is not null && trxCause is not null &&
            (check.FailureCauseEvidence.Cause != trxCause.Cause ||
             !string.Equals(
                 check.FailureCauseEvidence.SourceClassification,
                 trxCause.SourceClassification,
                 StringComparison.Ordinal)))
        {
            return check with { FailureCauseEvidence = null };
        }

        if (string.IsNullOrWhiteSpace(check.FailureClassification) &&
            check.FailureCauseEvidence is null &&
            trxCause is not null)
        {
            check = check with
            {
                FailureClassification = trxCause.SourceClassification,
                FailureCauseEvidence = trxCause
            };
        }

        check = AcceptanceAssemblyCleanupEvidence.AttachCleanupCause(check);
        var classification = string.IsNullOrWhiteSpace(check.FailureClassification)
            ? null
            : check.FailureClassification.Trim();
        var classifiedCause = classification switch
        {
            AcceptanceFailureClassifications.GateEnvironmentInterference or
            AcceptanceFailureClassifications.FocusedSelectionApparatusFailure or
            AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable or
            AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus or
            AcceptanceFailureClassifications.AssemblyCleanupFailure or
            AcceptanceFailureClassifications.SeededRepositoryApparatus =>
                AcceptanceFailureCause.EnvironmentalApparatus,
            _ => (AcceptanceFailureCause?)null
        };

        var supplied = check.FailureCauseEvidence;
        if (supplied is not null &&
            (!Enum.IsDefined(supplied.Cause) ||
             supplied.Cause == AcceptanceFailureCause.NotClassified ||
             string.IsNullOrWhiteSpace(supplied.Evidence) ||
             supplied.CheckName is not null &&
             !supplied.CheckName.Equals(check.Name, StringComparison.Ordinal) ||
             supplied.SourceClassification is not null &&
             (classification is null ||
              !supplied.SourceClassification.Equals(classification, StringComparison.Ordinal))))
        {
            return check with { FailureCauseEvidence = null };
        }

        if (classifiedCause is null)
        {
            return check;
        }

        if (supplied is not null && supplied.Cause != classifiedCause)
        {
            return check with { FailureCauseEvidence = null };
        }

        var evidence = supplied?.Evidence.Trim() ??
            $"check={JsonSerializer.Serialize(check.Name)}; failureClassification={JsonSerializer.Serialize(classification)}";
        return check with
        {
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                classifiedCause.Value,
                evidence,
                check.Name,
                classification)
        };
    }

    private static AcceptanceFailureCauseEvidence? ExtractTrxFailureCauseEvidence(
        IEnumerable<string>? trxPaths,
        string checkName)
    {
        if (trxPaths is null)
        {
            return null;
        }

        var receipts = new List<AcceptanceFailureCauseReceiptV1>();
        var failedResultCount = 0;
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                var failedResults = XDocument.Load(trxPath, LoadOptions.None)
                    .Descendants()
                    .Where(element =>
                    {
                        if (!element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var outcome = element.Attribute("outcome")?.Value;
                        return !string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();
                foreach (var result in failedResults)
                {
                    failedResultCount++;
                    var message = result.Descendants()
                        .FirstOrDefault(element =>
                            element.Name.LocalName.Equals("Message", StringComparison.Ordinal) &&
                            element.Ancestors().Any(ancestor =>
                                ancestor.Name.LocalName.Equals("ErrorInfo", StringComparison.Ordinal)))
                        ?.Value;
                    if (message is null ||
                        !AcceptanceFailureCauseReceiptCodec.TryParse(message, out var receipt))
                    {
                        return null;
                    }

                    receipts.Add(receipt);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }

        if (failedResultCount == 0 || receipts.Count != failedResultCount)
        {
            return null;
        }

        var evidence = string.Join(
            " | ",
            receipts.Select(AcceptanceFailureCauseReceiptCodec.Format).Distinct(StringComparer.Ordinal));
        if (evidence.Length > 4096)
        {
            evidence = evidence[..4096];
        }

        var sourceClassification = receipts.All(receipt =>
            receipt.Owner.Equals("ProcessOutputApparatus", StringComparison.Ordinal))
                ? AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus
                : AcceptanceFailureClassifications.SeededRepositoryApparatus;
        return new AcceptanceFailureCauseEvidence(
            AcceptanceFailureCause.EnvironmentalApparatus,
            evidence,
            checkName,
            sourceClassification);
    }
}
