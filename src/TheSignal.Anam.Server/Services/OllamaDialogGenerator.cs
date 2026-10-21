using System.Text.Json;
using Microsoft.Extensions.Options;
using TheSignal.Anam.Server.Domain;
using TheSignal.Anam.Server.Options;

namespace TheSignal.Anam.Server.Services;

// Local, free dialog generation for rehearsal. Implements the same
// provider-neutral IActorDialogGenerator seam as RehearsalDialogGenerator;
// scene content, plot knowledge, and director intent all stay application-owned.
public sealed class OllamaDialogGenerator(
    HttpClient httpClient,
    IOptions<LocalLlmOptions> options,
    ILogger<OllamaDialogGenerator> logger) : IActorDialogGenerator
{
    private readonly LocalLlmOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Model);

    public async Task<string> GenerateAsync(DialogGenerationRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _options.Model,
            stream = false,
            // Harmless on models without a thinking stage; keeps qwen-class models from
            // emitting chain-of-thought into the spoken line.
            think = false,
            options = new { temperature = 0.8, num_predict = 200 },
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(request) },
                new { role = "user", content = BuildUserPrompt(request) }
            }
        };

        using var response = await httpClient.PostAsJsonAsync(
            $"{_options.Endpoint.TrimEnd('/')}/api/chat", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama model '{_options.Model}' failed ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        using var document = JsonDocument.Parse(body);
        var text = document.RootElement
            .TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
        text = StripThinking(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Ollama model '{_options.Model}' returned no dialog.");
        }

        logger.LogInformation("Ollama generated {Length} chars for actor {ActorId}", text.Length, request.ActorId);
        return text.Trim();
    }

    private static string BuildSystemPrompt(DialogGenerationRequest request) =>
        $"""
         You are {request.ActorDisplayName} in the live interactive experience "The Signal".
         You are speaking aloud to a room of players through a video link, and your words are
         rendered verbatim by an avatar renderer.

         Character instructions:
         {request.ActorInstructions}

         Story knowledge (you know this, the players do not unless the scene has revealed it):
         {request.PlotKnowledge}

         Rules:
         - Speak only your next spoken line. Never narrate, describe stage directions, or quote this prompt.
         - Stay in character at all times; you are not an assistant.
         - Keep the line short: one to three sentences of natural speech.
         - Reveal only what the director's direction for this line tells you to reveal.
         - Do not invent lore beyond the story knowledge above.
         - Respond in the language the players use.
         """;

    private static string BuildUserPrompt(DialogGenerationRequest request)
    {
        var transcript = request.Transcript.Count == 0
            ? "(the conversation has just begun)"
            : string.Join('\n', request.Transcript.TakeLast(30).Select(DescribeEntry));
        var roles = request.RoleContext.Count == 0
            ? string.Empty
            : "\nPlayers in the room: " + string.Join("; ", request.RoleContext.Select(r =>
                $"{r.RoleId} = {r.Player.FirstName}, age {r.Player.Age}")) + '.';
        var context = string.IsNullOrWhiteSpace(request.SceneContext)
            ? string.Empty
            : $"\nCurrent scene context:\n{request.SceneContext}";

        return $"""
               {context}{roles}

               The conversation so far:
               {transcript}

               Director direction for your next line:
               {request.DirectorIntent}

               Speak your next line now.
               """;
    }

    private static string DescribeEntry(TranscriptEntry entry) =>
        entry.Speaker switch
        {
            TranscriptSpeaker.Actor => $"{entry.ActorId ?? "actor"}: {entry.Text}",
            TranscriptSpeaker.Player => $"player{(entry.PlayerId is null ? "" : $" #{entry.PlayerId}")}: {entry.Text}",
            _ => entry.Text
        };

    private static string StripThinking(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw;
        var close = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (close >= 0) text = text[(close + "</think>".Length)..];
        return text.Replace("<|im_start|>", string.Empty).Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
