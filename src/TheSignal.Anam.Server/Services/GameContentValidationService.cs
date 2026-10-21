using System.Text.RegularExpressions;

namespace TheSignal.Anam.Server.Services;

public sealed record ContentValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed partial class GameContentValidationService(
    SceneLoaderService sceneLoader,
    ActorLoaderService actorLoader,
    RoleAssignmentService roleService)
{
    private static readonly HashSet<string> TriggerTypes = ["timestamp", "after", "manual"];
    private static readonly HashSet<string> TemplateFields =
        ["firstName", "age", "physicalAppearance", "background", "career"];

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_]+)\s*\}\}")]
    private static partial Regex TemplatePattern();

    public async Task<ContentValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        var actors = await actorLoader.LoadAllAsync(cancellationToken);
        var roles = await roleService.LoadRolesAsync(cancellationToken);
        var errors = new List<string>();

        AddDuplicates(errors, scenes.Select(item => item.Id), "scene");
        AddDuplicates(errors, actors.Select(item => item.Id), "actor");
        AddDuplicates(errors, roles.Select(item => item.Id), "role");
        var sceneIds = scenes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var actorIds = actors.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var roleIds = roles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var scene in scenes)
        {
            if (!TriggerTypes.Contains(scene.Trigger.Type))
                errors.Add($"Scene '{scene.Id}' has unknown trigger type '{scene.Trigger.Type}'.");
            if (scene.Trigger.Type == "after" &&
                (scene.Trigger.After is null || !sceneIds.Contains(scene.Trigger.After)))
                errors.Add($"Scene '{scene.Id}' references missing prerequisite '{scene.Trigger.After}'.");

            foreach (var line in scene.Dialog)
            {
                if (!actorIds.Contains(line.Actor))
                    errors.Add($"Scene '{scene.Id}' references unknown actor '{line.Actor}'.");
                ValidateTemplates(errors, scene.Id, line.Text, roleIds);
            }
            ValidateTemplates(errors, scene.Id, scene.Context, roleIds);
        }
        return new ContentValidationResult(errors);
    }

    private static void AddDuplicates(List<string> errors, IEnumerable<string> values, string kind)
    {
        foreach (var item in values.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"Duplicate {kind} ID '{item.Key}'.");
    }

    private static void ValidateTemplates(List<string> errors, string sceneId, string? text, HashSet<string> roleIds)
    {
        if (text is null) return;
        foreach (Match match in TemplatePattern().Matches(text))
        {
            var role = match.Groups[1].Value;
            var field = match.Groups[2].Value;
            if (!roleIds.Contains(role)) errors.Add($"Scene '{sceneId}' references unknown role '{role}'.");
            if (!TemplateFields.Contains(field)) errors.Add($"Scene '{sceneId}' uses unknown role field '{field}'.");
        }
    }
}
