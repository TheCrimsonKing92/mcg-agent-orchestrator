using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: serializers and dispatch records are local to each test.
public sealed class ProviderKindLegacyNameJsonTests
{
    [Theory]
    [InlineData("\"OpenAICodexSpark\"")]
    [InlineData("\"openaicodexspark\"")]
    [InlineData("\"OpenAICodexLuna\"")]
    [InlineData("3")]
    public void DispatchRecords_LegacyOrCurrentKind_ReadLuna(string kindJson)
    {
        var options = Options();
        var json = DispatchJson(kindJson);

        Assert.Equal(ProviderKind.OpenAICodexLuna,
            JsonSerializer.Deserialize<TaskDispatchSnapshot>(json, options)!.WorkerProviderKind);
        Assert.Equal(ProviderKind.OpenAICodexLuna,
            JsonSerializer.Deserialize<TaskDispatchRecord>(json, options)!.WorkerProviderKind);
        Assert.Equal(3, (int)ProviderKind.OpenAICodexLuna);
    }

    [Fact]
    public void DispatchRecords_LunaKind_WriteOnlyCurrentName()
    {
        var options = Options();
        var snapshot = JsonSerializer.Deserialize<TaskDispatchSnapshot>(DispatchJson("3"), options)!;
        var record = JsonSerializer.Deserialize<TaskDispatchRecord>(DispatchJson("3"), options)!;

        foreach (var json in new[]
        {
            JsonSerializer.Serialize(snapshot, options), JsonSerializer.Serialize(record, options)
        })
        {
            Assert.Contains("\"WorkerProviderKind\":\"OpenAICodexLuna\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("spark", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("\"OpenAICodexNope\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void DispatchRecords_InvalidKind_ThrowJsonException(string kindJson)
    {
        var options = Options();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TaskDispatchSnapshot>(DispatchJson(kindJson), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TaskDispatchRecord>(DispatchJson(kindJson), options));
    }

    [Fact]
    public void DispatchRecords_OtherKindsAndIntegers_MatchPlatformConverter()
    {
        var options = Options();
        var values = Enum.GetValues<ProviderKind>().Concat([(ProviderKind)123, (ProviderKind)(-1)]);
        foreach (var value in values)
        {
            var kindJson = JsonSerializer.Serialize(value, options);
            var snapshot = JsonSerializer.Deserialize<TaskDispatchSnapshot>(DispatchJson(kindJson), options)!;
            var record = JsonSerializer.Deserialize<TaskDispatchRecord>(DispatchJson(kindJson), options)!;
            Assert.Equal(value, snapshot.WorkerProviderKind);
            Assert.Equal(value, record.WorkerProviderKind);
            using var written = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, options));
            Assert.Equal(kindJson, written.RootElement.GetProperty("WorkerProviderKind").GetRawText());
        }
    }

    private static JsonSerializerOptions Options() => new() { Converters = { new JsonStringEnumConverter() } };

    private static string DispatchJson(string kindJson) => $$"""
        {"WorkerName":"codex-luna","Command":"codex exec","WorkingDirectory":"C:\\repo","DispatchedAt":"2026-10-06T12:00:00Z","WorkerProviderKind":{{kindJson}}}
        """;
}
