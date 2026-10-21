namespace TheSignal.Anam.Server.Options;

// Configuration for the free local rehearsal path: a localhost voice bridge
// (Piper TTS + whisper.cpp STT) and a local Ollama dialog model. Nothing here
// is paid, and every endpoint defaults to loopback — nothing leaves the machine.
public class LocalVoiceOptions
{
    public const string SectionName = "Local";

    // The tools/voice-bridge FastAPI service. Empty disables local voice mode.
    public string BridgeEndpoint { get; set; } = string.Empty;
}

public class LocalLlmOptions
{
    public const string SectionName = "Ollama";

    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    // Empty keeps the deterministic RehearsalDialogGenerator in place;
    // when set, improvise lines and player responses run through Ollama.
    public string Model { get; set; } = string.Empty;
}
