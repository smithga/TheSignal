using System.Collections.Concurrent;
using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public interface IGameSessionStore
{
    GameSession Create();
    bool TryGet(string sessionId, out GameSession? session);
    GameSession? Reset(string sessionId);
    bool End(string sessionId);
}

public sealed class InMemoryGameSessionStore(TimeProvider timeProvider) : IGameSessionStore
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions =
        new(StringComparer.Ordinal);

    public GameSession Create()
    {
        var now = timeProvider.GetUtcNow();
        var session = new GameSession
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

        if (!_sessions.TryAdd(session.Id, session))
        {
            // A GUID collision is vanishingly unlikely, but never return a
            // session that was not actually stored.
            return Create();
        }

        return session;
    }

    public bool TryGet(string sessionId, out GameSession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public GameSession? Reset(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var existing))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var reset = new GameSession
        {
            Id = existing.Id,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };

        return _sessions.TryUpdate(sessionId, reset, existing) ? reset : null;
    }

    public bool End(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        session.Status = GameSessionStatus.Ended;
        session.IsClockRunning = false;
        session.UpdatedAt = timeProvider.GetUtcNow();
        return true;
    }
}
