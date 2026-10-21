namespace TheSignal.Anam.Server.Domain;

public enum DeliveryChannel
{
    SpokenAvatar,
    TextMessage,
    Overlay
}

public sealed record PerformanceDirection(
    string? Emotion = null,
    string? Pace = null,
    string? Intensity = null,
    string? GestureHint = null);

// The provider-neutral boundary between game/dialog logic and actor rendering.
// Renderer-specific configuration must not be added to this record.
public sealed record ActorUtterance(
    string Id,
    string ActorId,
    string Text,
    DeliveryChannel Channel,
    PerformanceDirection? Direction = null,
    string? SceneId = null,
    string? DialogLineId = null);
