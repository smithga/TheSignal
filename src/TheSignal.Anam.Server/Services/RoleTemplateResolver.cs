using System.Text.RegularExpressions;
using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public sealed partial class RoleTemplateResolver
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_]+)\s*\}\}")]
    private static partial Regex TemplatePattern();

    public string Resolve(string text, IReadOnlyList<RoleAssignment> assignments)
    {
        var roles = assignments.ToDictionary(item => item.RoleId, StringComparer.Ordinal);
        return TemplatePattern().Replace(text, match =>
        {
            if (!roles.TryGetValue(match.Groups[1].Value, out var assignment))
                throw new InvalidOperationException($"Role '{match.Groups[1].Value}' has not been assigned.");

            return match.Groups[2].Value switch
            {
                "firstName" => assignment.Player.FirstName,
                "age" => assignment.Player.Age.ToString(),
                "physicalAppearance" => assignment.Player.PhysicalAppearance,
                "background" => assignment.Player.Background,
                "career" => assignment.Player.Career,
                _ => throw new InvalidOperationException($"Unknown role template field '{match.Groups[2].Value}'.")
            };
        });
    }
}
