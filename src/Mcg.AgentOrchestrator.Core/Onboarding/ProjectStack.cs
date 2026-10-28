using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStack>))]
public enum ProjectStack
{
    [JsonStringEnumMemberName("dotnet")]
    Dotnet,
    [JsonStringEnumMemberName("go")]
    Go,
    [JsonStringEnumMemberName("node")]
    Node
}
