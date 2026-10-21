using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public record GameActor(
    string Id,
    string DisplayName,
    DeliveryChannel Channel,
    string CharacterInstructions,
    string? Description,
    string? VoiceNotes,
    string? Notes);

// Parses game/actors/*.md into structured actors. Same flat-frontmatter +
// ## Section convention as SceneLoaderService.
public class ActorLoaderService(IWebHostEnvironment env, ILogger<ActorLoaderService> logger)
{
    public async Task<List<GameActor>> LoadAllAsync(CancellationToken ct)
    {
        var dir = GamePaths.Actors(env);
        if (!Directory.Exists(dir))
        {
            logger.LogWarning("Actors folder not found at {Dir}; continuing with no actors.", dir);
            return [];
        }

        var actors = new List<GameActor>();
        foreach (var file in Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var actor = ParseActor(text, file);
            if (actor is not null)
            {
                actors.Add(actor);
            }
        }

        return actors;
    }

    private GameActor? ParseActor(string text, string filePath)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            logger.LogWarning("Actor file {File} is missing '---' frontmatter; skipping.", filePath);
            return null;
        }

        var frontmatter = new Dictionary<string, string>();
        var i = 1;
        for (; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx < 0) continue;
            var key = lines[i][..idx].Trim();
            var value = lines[i][(idx + 1)..].Trim();
            frontmatter[key] = value;
        }
        i++; // skip closing ---

        var body = string.Join('\n', lines.Skip(i));
        var sections = ParseSections(body);

        var id = frontmatter.GetValueOrDefault("id") ?? Path.GetFileNameWithoutExtension(filePath);
        var displayName = frontmatter.GetValueOrDefault("displayName") ?? id;
        var channel = ParseDeliveryChannel(frontmatter.GetValueOrDefault("channel"), filePath);

        sections.TryGetValue("description", out var description);
        sections.TryGetValue("voice notes", out var voiceNotes);
        sections.TryGetValue("notes", out var notes);

        return new GameActor(
            id,
            displayName,
            channel,
            body.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description,
            string.IsNullOrWhiteSpace(voiceNotes) ? null : voiceNotes,
            string.IsNullOrWhiteSpace(notes) ? null : notes);
    }

    private DeliveryChannel ParseDeliveryChannel(string? value, string filePath)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "spoken-avatar" => DeliveryChannel.SpokenAvatar,
            // Compatibility for actor files created before delivery channels
            // were made provider-neutral.
            "anam-avatar" => DeliveryChannel.SpokenAvatar,
            "text-message" => DeliveryChannel.TextMessage,
            "overlay" => DeliveryChannel.Overlay,
            _ => throw new InvalidDataException(
                $"Actor file '{filePath}' has unknown delivery channel '{value}'.")
        };
    }

    private static Dictionary<string, string> ParseSections(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');
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
            if (trimmed.StartsWith("## "))
            {
                Flush();
                currentHeader = trimmed[3..].Trim();
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
