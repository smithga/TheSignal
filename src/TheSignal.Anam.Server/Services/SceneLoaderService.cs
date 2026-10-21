using System.Text.RegularExpressions;
using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

// Parses game/scenes/*.md into structured scenes. Each file has flat YAML-ish
// frontmatter (id/title/actor/kind/trigger/at/after/wait), followed by either
// legacy ## Direction/## Context/## Dialog sections or rich # sections. In the
// rich format, every non-Dialog section is assembled into runtime context.
// Dialog bullets use "actorId: line text" to say who speaks; bullets without a
// recognized "actorId:" prefix fall back to the scene's default actor. Add
// "(improvise)" after the actor id -- e.g. "evelyn (improvise): ..." -- to mark
// text as direction/thoughts to riff on instead of exact script.
public class SceneLoaderService(IWebHostEnvironment env, ILogger<SceneLoaderService> logger)
{
    private static readonly Regex RoleTemplatePattern = new(
        @"\{\{\s*role:([a-zA-Z0-9_-]+)(?:\.([a-zA-Z0-9_]+))?\s*\}\}",
        RegexOptions.Compiled);

    public async Task<List<GameScene>> LoadAllAsync(CancellationToken ct)
    {
        var dir = Path.Combine(GamePaths.Root(env), "scenes");
        if (!Directory.Exists(dir))
        {
            logger.LogWarning("Scenes folder not found at {Dir}; continuing with no scenes.", dir);
            return [];
        }

        var scenes = new List<GameScene>();
        foreach (var file in Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var scene = ParseScene(text, file);
            if (scene is not null)
            {
                scenes.Add(scene);
            }
        }

        return scenes;
    }

    private GameScene? ParseScene(string text, string filePath)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            logger.LogWarning("Scene file {File} is missing '---' frontmatter; skipping.", filePath);
            return null;
        }

        var frontmatter = new Dictionary<string, string>();
        var i = 1;
        for (; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx < 0) continue;
            frontmatter[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }
        i++; // skip closing ---

        var body = string.Join('\n', lines.Skip(i));
        var sections = ParseSections(body, out var usesTopLevelSections);

        var id = frontmatter.GetValueOrDefault("id") ?? Path.GetFileNameWithoutExtension(filePath);
        var title = frontmatter.GetValueOrDefault("title") ?? id;
        var actor = frontmatter.GetValueOrDefault("actor") ?? "";
        var kind = frontmatter.GetValueOrDefault("kind") ?? "scene";
        var triggerType = frontmatter.GetValueOrDefault("trigger") ?? "manual";
        int? at = frontmatter.TryGetValue("at", out var atStr) && int.TryParse(atStr, out var atVal) ? atVal : null;
        var after = frontmatter.GetValueOrDefault("after");
        var wait = frontmatter.TryGetValue("wait", out var waitStr) && int.TryParse(waitStr, out var waitVal) ? waitVal : 0;

        var trigger = new SceneTrigger(triggerType, at, string.IsNullOrWhiteSpace(after) ? null : after, wait);
        sections.TryGetValue("direction", out var direction);
        var context = usesTopLevelSections
            ? BuildContext(sections)
            : sections.GetValueOrDefault("context");

        var dialog = new List<DialogLine>();
        if (sections.TryGetValue("dialog", out var dialogRaw))
        {
            dialog = dialogRaw.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- "))
                .Select(line => ParseDialogLine(line[2..].Trim(), actor))
                .ToList();
        }

        // Rich Direction is prose for model context, not an Anam Director Note
        // tag. The latter only accepts concise expression cues such as "concerned".
        var performanceDirection = usesTopLevelSections || string.IsNullOrWhiteSpace(direction)
            ? null
            : new PerformanceDirection(Emotion: direction);
        return new GameScene(id, title, actor, kind, trigger,
            performanceDirection,
            string.IsNullOrWhiteSpace(context) ? null : context,
            dialog);
    }

    private static DialogLine ParseDialogLine(string bullet, string defaultActor)
    {
        var idx = bullet.IndexOf(": ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var tag = bullet[..idx].Trim();
            var mode = DialogMode.Say;
            if (tag.EndsWith("(improvise)", StringComparison.OrdinalIgnoreCase))
            {
                mode = DialogMode.Improvise;
                tag = tag[..^"(improvise)".Length].Trim();
            }
            else if (tag.Contains('(') || tag.Contains(')'))
            {
                throw new InvalidDataException($"Unknown dialog mode in '{bullet}'.");
            }

            if (tag.Length > 0 && tag.All(c => char.IsLower(c) || char.IsDigit(c) || c is '-' or '_'))
            {
                return new DialogLine(tag, NormalizeRoleTemplates(bullet[(idx + 2)..].Trim()), mode);
            }
        }

        return new DialogLine(defaultActor, NormalizeRoleTemplates(bullet), DialogMode.Say);
    }

    private static string? BuildContext(Dictionary<string, string> sections)
    {
        var context = string.Join("\n\n", sections
            .Where(section => !section.Key.Equals("dialog", StringComparison.OrdinalIgnoreCase))
            .Where(section => !string.IsNullOrWhiteSpace(section.Value))
            .Select(section => $"# {section.Key}\n\n{NormalizeRoleTemplates(section.Value)}"));

        return string.IsNullOrWhiteSpace(context) ? null : context;
    }

    // Rich scenes may use {{role:host}} or {{role:host.firstName}}. Normalize
    // both forms to the client template contract, defaulting bare roles to names.
    private static string NormalizeRoleTemplates(string text) => RoleTemplatePattern.Replace(text, match =>
    {
        var roleId = match.Groups[1].Value;
        var field = match.Groups[2].Success ? match.Groups[2].Value : "firstName";
        return $"{{{{{roleId}.{field}}}}}";
    });

    private static Dictionary<string, string> ParseSections(string body, out bool usesTopLevelSections)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');
        usesTopLevelSections = lines.Any(line => line.TrimStart().StartsWith("# ", StringComparison.Ordinal));
        var headerPrefix = usesTopLevelSections ? "# " : "## ";
        string? currentHeader = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (currentHeader is not null)
            {
                result[currentHeader] = string.Join('\n', buffer).Trim();
            }
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(headerPrefix, StringComparison.Ordinal))
            {
                Flush();
                currentHeader = trimmed[headerPrefix.Length..].Trim();
            }
            else
            {
                buffer.Add(line);
            }
        }
        Flush();

        return result;
    }
}
