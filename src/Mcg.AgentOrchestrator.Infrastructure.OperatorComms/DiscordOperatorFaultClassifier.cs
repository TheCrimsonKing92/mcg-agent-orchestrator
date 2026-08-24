using System.Net;
using Discord.Net;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordOperatorAuthWarning(
    string OperationName,
    HttpStatusCode StatusCode,
    string Message);

public static class DiscordOperatorFaultClassifier
{
    private static int _disabledForProcess;

    internal static Action<DiscordOperatorAuthWarning> AuthWarningSink { get; set; } =
        warning => System.Diagnostics.Trace.TraceWarning(
            "discord_operator_auth_disabled operation={0} status={1} message={2}",
            warning.OperationName,
            (int)warning.StatusCode,
            warning.Message);

    public static bool IsAuthDisabledForProcess => Volatile.Read(ref _disabledForProcess) != 0;

    public static bool IsAuthError(Exception exception) =>
        TryGetHttpStatusCode(exception, out var statusCode) &&
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public static bool IsFatal(Exception exception) => false;

    public static bool IsTransient(Exception exception) =>
        TryGetHttpStatusCode(exception, out var statusCode) &&
        ((int)statusCode >= 500 || statusCode == HttpStatusCode.TooManyRequests);

    public static void DisableForProcess(string operationName, Exception exception)
    {
        if (!TryGetHttpStatusCode(exception, out var statusCode) || !IsAuthStatus(statusCode))
            return;

        if (Interlocked.Exchange(ref _disabledForProcess, 1) == 0)
        {
            AuthWarningSink(new DiscordOperatorAuthWarning(
                operationName,
                statusCode,
                exception.Message));
        }
    }

    internal static void ResetForTests()
    {
        Volatile.Write(ref _disabledForProcess, 0);
        AuthWarningSink = warning => System.Diagnostics.Trace.TraceWarning(
            "discord_operator_auth_disabled operation={0} status={1} message={2}",
            warning.OperationName,
            (int)warning.StatusCode,
            warning.Message);
    }

    private static bool IsAuthStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool TryGetHttpStatusCode(Exception exception, out HttpStatusCode statusCode)
    {
        switch (exception)
        {
            case HttpException httpException:
                statusCode = httpException.HttpCode;
                return true;
            case HttpRequestException { StatusCode: { } requestStatusCode }:
                statusCode = requestStatusCode;
                return true;
            default:
                statusCode = default;
                return false;
        }
    }
}
