using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TheSignal.Anam.Server.Options;

namespace TheSignal.Anam.Server.Services;

public class AnamSessionClient(HttpClient httpClient, IOptions<AnamOptions> options, ILogger<AnamSessionClient> logger)
{
    private readonly AnamOptions _options = options.Value;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        (!string.IsNullOrWhiteSpace(_options.PersonaId) || !string.IsNullOrWhiteSpace(_options.AvatarId));

    public async Task<string> CreateSessionTokenAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Anam API key and either PersonaId or AvatarId must be configured.");
        }

        // A saved persona is used only to discover rendering configuration.
        // Game plot, character instructions, and dialog generation remain application-owned.
        var basePersona = string.IsNullOrWhiteSpace(_options.PersonaId)
            ? new PersonaComponents(_options.AvatarId, _options.AvatarModel, _options.VoiceId, _options.LlmId)
            : await GetPersonaComponentsAsync(_options.PersonaId, ct);

        var personaConfig = new
        {
            name = "The Signal",
            avatarId = basePersona.AvatarId,
            avatarModel = basePersona.AvatarModel,
            voiceId = string.IsNullOrWhiteSpace(basePersona.VoiceId) ? null : basePersona.VoiceId,
            llmId = string.IsNullOrWhiteSpace(basePersona.LlmId) ? null : basePersona.LlmId,
            systemPrompt = "Render only text supplied through talk(). Do not generate game dialog."
        };

        var requestBody = new { personaConfig };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anam.ai/v1/auth/session-token")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody, SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Anam session-token request failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Anam session-token request failed with status {response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("sessionToken").GetString() ?? string.Empty;
    }

    private async Task<PersonaComponents> GetPersonaComponentsAsync(string personaId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.anam.ai/v1/personas/{personaId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Anam get-persona request failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Anam get-persona request failed with status {response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var avatarId = root.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.Object
            ? avatar.GetProperty("id").GetString() ?? string.Empty
            : string.Empty;
        var avatarModel = root.TryGetProperty("avatarModel", out var model) && model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? _options.AvatarModel
            : _options.AvatarModel;
        var voiceId = root.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object
            ? voice.GetProperty("id").GetString() ?? string.Empty
            : string.Empty;
        var llmId = root.TryGetProperty("llmId", out var llm) && llm.ValueKind == JsonValueKind.String
            ? llm.GetString() ?? string.Empty
            : string.Empty;
        return new PersonaComponents(avatarId, avatarModel, voiceId, llmId);
    }

    private record PersonaComponents(string AvatarId, string AvatarModel, string VoiceId, string LlmId);
}
