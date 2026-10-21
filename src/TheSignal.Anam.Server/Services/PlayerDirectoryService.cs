using System.Text.Json;

namespace TheSignal.Anam.Server.Services;

public record PlayerProfile(
    string Id,
    string Name,
    int Age,
    string PhysicalAppearance,
    string Background,
    string Career,
    string Email,
    string Phone,
    string? Role = null);

// Loads rich player profiles (including PII) for server-side use only.
// Never expose this data directly over an unauthenticated HTTP endpoint.
public class PlayerDirectoryService(IWebHostEnvironment env, ILogger<PlayerDirectoryService> logger)
{
    private readonly string _path = GamePaths.Players(env);

    public async Task<IReadOnlyList<PlayerProfile>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            logger.LogWarning("Players file not found at {Path}; continuing with no players.", _path);
            return [];
        }

        var json = await File.ReadAllTextAsync(_path, ct);
        var players = JsonSerializer.Deserialize<List<PlayerProfile>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return players ?? [];
    }
}
