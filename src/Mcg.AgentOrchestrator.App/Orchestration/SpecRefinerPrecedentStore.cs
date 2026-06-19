using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SpecRefinerPrecedent(
    string ForkKind,
    string Choice,
    string Rationale,
    DateTimeOffset RecordedAt);

internal sealed class SpecRefinerPrecedentStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SpecRefinerPrecedentStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<SpecRefinerPrecedent?> TryGetPrecedentAsync(
        string forkKind,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync(cancellationToken);
        return all.FirstOrDefault(p =>
            string.Equals(p.ForkKind, forkKind, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RecordPrecedentAsync(
        string forkKind,
        string choice,
        string rationale,
        CancellationToken cancellationToken = default)
    {
        var all = (await LoadAllAsync(cancellationToken)).ToList();
        all.RemoveAll(p => string.Equals(p.ForkKind, forkKind, StringComparison.OrdinalIgnoreCase));
        all.Add(new SpecRefinerPrecedent(forkKind, choice, rationale, DateTimeOffset.UtcNow));
        await SaveAllAsync(all, cancellationToken);
    }

    private async Task<IReadOnlyList<SpecRefinerPrecedent>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<List<SpecRefinerPrecedent>>(json, SerializerOptions) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task SaveAllAsync(
        IReadOnlyList<SpecRefinerPrecedent> precedents,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(precedents, SerializerOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }
}
