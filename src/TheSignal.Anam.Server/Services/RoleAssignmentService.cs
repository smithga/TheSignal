using System.Text.Json;
using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public record GameRole(string Id, string Description);

// Assigns players to roles: fixed roles (e.g. "host") come from a player's own
// `role` field; remaining roles are randomly and uniquely distributed among
// the remaining players. The caller stores the result in its GameSession.
public class RoleAssignmentService(PlayerDirectoryService playerDirectory, IWebHostEnvironment env, ILogger<RoleAssignmentService> logger)
{
    private readonly string _rolesPath = GamePaths.Roles(env);
    public async Task<List<GameRole>> LoadRolesAsync(CancellationToken ct)
    {
        if (!File.Exists(_rolesPath))
        {
            logger.LogWarning("Roles file not found at {Path}; continuing with no assignable roles.", _rolesPath);
            return [];
        }

        var json = await File.ReadAllTextAsync(_rolesPath, ct);
        var roles = JsonSerializer.Deserialize<List<GameRole>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return roles ?? [];
    }

    public async Task<List<RoleAssignment>> AssignAsync(int? seed, CancellationToken ct)
    {
        var players = await playerDirectory.LoadAsync(ct);
        var roles = await LoadRolesAsync(ct);

        var assignments = new List<RoleAssignment>();

        var knownRoleIds = roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);
        var fixedPlayers = players.Where(p => !string.IsNullOrWhiteSpace(p.Role)).ToList();
        foreach (var player in fixedPlayers)
        {
            if (!knownRoleIds.Contains(player.Role!))
            {
                throw new InvalidDataException($"Player '{player.Id}' references unknown role '{player.Role}'.");
            }
            assignments.Add(new RoleAssignment(player.Role!, ToPublicProfile(player)));
        }

        var random = seed is null ? Random.Shared : new Random(seed.Value);
        var remainingPlayers = players.Except(fixedPlayers).OrderBy(_ => random.Next()).ToList();
        var remainingRoles = roles.Where(r => assignments.TrueForAll(a => a.RoleId != r.Id)).ToList();

        foreach (var (role, player) in remainingRoles.Zip(remainingPlayers))
        {
            assignments.Add(new RoleAssignment(role.Id, ToPublicProfile(player)));
        }

        return assignments;
    }

    private static PublicPlayerProfile ToPublicProfile(PlayerProfile player)
    {
        var firstName = player.Name.Split(' ', 2)[0];
        return new PublicPlayerProfile(firstName, player.Age, player.PhysicalAppearance, player.Background, player.Career);
    }
}
