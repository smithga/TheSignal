namespace TheSignal.Anam.Server.Options;

public class AnamOptions
{
    public const string SectionName = "Anam";

    public string ApiKey { get; set; } = string.Empty;

    // If PersonaId is set, it takes priority (a full persona you built in Anam Lab).
    public string PersonaId { get; set; } = string.Empty;

    // Otherwise an ephemeral persona is built from these fields.
    public string AvatarId { get; set; } = string.Empty;
    public string AvatarModel { get; set; } = "cara-4";
    public string VoiceId { get; set; } = string.Empty;
    public string LlmId { get; set; } = string.Empty;
}
