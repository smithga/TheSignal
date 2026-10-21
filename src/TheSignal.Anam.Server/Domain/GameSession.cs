namespace TheSignal.Anam.Server.Domain;

public enum GameSessionStatus
{
    Created,
    Running,
    Paused,
    Ended
}

public enum SceneExecutionStatus
{
    Pending,
    Active,
    Completed,
    Failed
}

public sealed record SceneExecutionState(
    string SceneId,
    SceneExecutionStatus Status = SceneExecutionStatus.Pending,
    TimeSpan? StartedAt = null,
    TimeSpan? CompletedAt = null,
    string? Error = null);

public enum TranscriptSpeaker
{
    Player,
    Actor,
    Director,
    System
}

public sealed record TranscriptEntry(
    string Id,
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset RecordedAt,
    string? ActorId = null,
    string? PlayerId = null,
    string? UtteranceId = null);

public sealed class GameSession
{
    public required string Id { get; init; }

    public GameSessionStatus Status { get; set; } = GameSessionStatus.Created;

    // Logical game time is independent from wall-clock time so rehearsal and
    // automated tests can pause or advance it deterministically.
    public TimeSpan ElapsedTime { get; set; }

    public bool IsClockRunning { get; set; }

    public List<RoleAssignment> RoleAssignments { get; } = [];

    public Dictionary<string, SceneExecutionState> Scenes { get; } =
        new(StringComparer.Ordinal);

    public List<TranscriptEntry> Transcript { get; } = [];

    public Queue<ActorUtterance> PendingUtterances { get; } = [];

    public string? CurrentSceneId { get; set; }

    public string? CurrentContext { get; set; }

    public bool IsResolvingPlayerInput { get; set; }

    public string? Decision { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
