using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FactConfidence>))]
public enum FactConfidence
{
    [JsonStringEnumMemberName("high")]
    High,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("low")]
    Low
}
