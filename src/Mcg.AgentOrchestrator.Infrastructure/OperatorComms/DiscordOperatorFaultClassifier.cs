using System.Net;
using Discord.Net;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DiscordOperatorFaultClassifier
{
    public static bool IsFatal(Exception exception) =>
        TryGetHttpStatusCode(exception, out var statusCode) &&
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public static bool IsTransient(Exception exception) =>
        TryGetHttpStatusCode(exception, out var statusCode) &&
        ((int)statusCode >= 500 || statusCode == HttpStatusCode.TooManyRequests);

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
