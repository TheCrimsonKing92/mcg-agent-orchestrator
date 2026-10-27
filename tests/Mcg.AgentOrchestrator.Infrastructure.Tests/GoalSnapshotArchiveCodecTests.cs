using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the codec has no mutable external state.
public sealed class GoalSnapshotArchiveCodecTests
{
    [Fact]
    public void Encode_UnicodeJson_PreservesMarkerAndExactText()
    {
        const string json = "{\"text\":\"ASCII é 中 \U0001F600\"}";
        var encoded = GoalSnapshotArchiveCodec.Encode(json);
        Assert.Equal(new byte[] { 0x4D, 0x47, 0x42, 0x31 }, encoded[..4]);
        Assert.True(string.Equals(json, GoalSnapshotArchiveCodec.Decode(encoded), StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_LegacyTextOrNull_PreservesOriginalValue()
    {
        const string json = "{\"Id\":\"é中\U0001F600\"}";
        Assert.True(string.Equals(json, GoalSnapshotArchiveCodec.Decode(json), StringComparison.Ordinal));
        Assert.Null(GoalSnapshotArchiveCodec.Decode(null));
        Assert.Null(GoalSnapshotArchiveCodec.Decode(DBNull.Value));
    }

    [Fact]
    public void Decode_MalformedOrUnsupportedValue_ThrowsArchiveDiagnostic()
    {
        var encoded = GoalSnapshotArchiveCodec.Encode("{\"Id\":\"x\",\"body\":\"valid payload\"}");
        object[] invalid =
        [
            Encoding.UTF8.GetBytes("{\"Id\":\"x\"}"),
            encoded[..(4 + (encoded.Length - 4) / 2)],
            "MGB1"u8.ToArray().Concat(Enumerable.Repeat((byte)0xFF, 16)).ToArray(),
            42L,
            encoded.Concat(new byte[] { 0x00 }).ToArray(),
            "MGB1"u8.ToArray()
        ];
        foreach (var value in invalid)
            Assert.Contains("goal_snapshot_archive",
                Assert.Throws<InvalidDataException>(() => GoalSnapshotArchiveCodec.Decode(value)).Message);
    }

    [Fact]
    public void Decode_InvalidUtf8InValidBrotli_ThrowsArchiveDiagnostic()
    {
        using var output = new MemoryStream();
        output.Write("MGB1"u8);
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(new byte[] { 0xC3, 0x28 });
        Assert.Contains("goal_snapshot_archive",
            Assert.Throws<InvalidDataException>(() => GoalSnapshotArchiveCodec.Decode(output.ToArray())).Message);
    }

    [Fact]
    public void Encode_RepetitiveGoalSnapshot_UsesAtMostQuarterOfUtf8Bytes()
    {
        var id = GoalId.New().Value;
        var at = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new GoalSnapshot(id, "Archive codec fixture", GoalStatus.Completed, [],
            Enumerable.Range(0, 200).Select(index => new ProgressEventSnapshot(id, null,
                ProgressKind.GoalPolicyDecision, "Repeated terminal goal evidence é 中 \U0001F600", at.AddSeconds(index))).ToArray());
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(snapshot, options);
        var encoded = GoalSnapshotArchiveCodec.Encode(json);
        Assert.True(encoded.Length * 4 <= Encoding.UTF8.GetByteCount(json));
        Assert.Equal(json, GoalSnapshotArchiveCodec.Decode(encoded));
    }
}
