using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    private sealed record LoadedLandingDenylist(LandingDenylist? Denylist, string Source);

    private static string InvalidDenylistSource(Exception exception) =>
        "invalid:" + exception.Message.Replace('\r', ' ').Replace('\n', ' ');

    private static LoadedLandingDenylist LoadLandingDenylist(string executionDirectory, string boundMainSha)
    {
        try
        {
            // Listing the immutable tree distinguishes absence from a failed git invocation.
            var listing = RunGit(executionDirectory, "ls-tree", "--name-only", boundMainSha, "--", "config/landing-denylist.json");
            if (listing.ExitCode != 0) throw new InvalidOperationException(listing.Error);
            if (string.IsNullOrWhiteSpace(listing.Output))
                return new(LandingDenylist.BuiltInDefault, LandingAdmissionReceipt.BuiltInDefaultSource);
            var content = RunGit(executionDirectory, "show", $"{boundMainSha}:config/landing-denylist.json");
            if (content.ExitCode != 0) throw new InvalidOperationException(content.Error);
            var source = $"main:{boundMainSha}";
            return new(LandingDenylist.Parse(content.Output, source), source);
        }
        catch (Exception exception) { return new(null, InvalidDenylistSource(exception)); }
    }

    private static LandingAdmissionReceipt BuildLandingAdmission(
        LoadedLandingDenylist loaded, string candidateSha, IEnumerable<string> changedPaths)
    {
        try
        {
            var matches = loaded.Denylist?.Match(changedPaths).Select(match => $"{match.RuleId}={match.Path}").ToArray() ?? [];
            return LandingAdmissionReceipt.Evaluated(candidateSha, loaded.Source, matches);
        }
        catch (Exception exception)
        {
            return LandingAdmissionReceipt.Evaluated(candidateSha, InvalidDenylistSource(exception), []);
        }
    }

    private static LandingAdmissionReceipt BuildLandingAdmission(
        string executionDirectory, string boundMainSha, string candidateSha, IEnumerable<string> changedPaths) =>
        BuildLandingAdmission(LoadLandingDenylist(executionDirectory, boundMainSha), candidateSha, changedPaths);
}
