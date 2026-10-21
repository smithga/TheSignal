namespace TheSignal.Anam.Server.Domain;

public enum SceneEngineEventType
{
    SessionInitialized,
    GameStarted,
    GamePaused,
    TimeAdvanced,
    SceneEligible,
    SceneStarted,
    ContextChanged,
    PlayerInputReceived,
    UtteranceReady,
    NonSpokenDeliveryReady,
    VisualEffectRequested,
    SceneCompleted,
    SceneFailed
}

public sealed record SceneEngineEvent(
    SceneEngineEventType Type,
    string? SceneId = null,
    TimeSpan? ElapsedTime = null,
    string? Context = null,
    string? Effect = null,
    ActorUtterance? Utterance = null);

public sealed record SceneEngineResult(
    GameSession Session,
    IReadOnlyList<SceneEngineEvent> Events);
