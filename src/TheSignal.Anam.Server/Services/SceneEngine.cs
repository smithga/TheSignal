using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public sealed class SceneEngine(
    IGameSessionStore sessions,
    SceneLoaderService sceneLoader,
    DialogResolver dialogResolver,
    RoleTemplateResolver templates,
    TimeProvider timeProvider)
{
    public async Task<SceneEngineResult?> ReceivePlayerInputAsync(
        string sessionId,
        string text,
        string? playerId,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        lock (session)
        {
            if (session.IsResolvingPlayerInput || session.CurrentSceneId is not null ||
                session.PendingUtterances.Count > 0)
                throw new InvalidOperationException("Player input is accepted only when no scene or actor performance is active.");

            session.IsResolvingPlayerInput = true;
            session.Transcript.Add(new TranscriptEntry(
                Guid.NewGuid().ToString("N"), TranscriptSpeaker.Player, text,
                timeProvider.GetUtcNow(), PlayerId: playerId));
            session.UpdatedAt = timeProvider.GetUtcNow();
        }

        try
        {
            var utterance = await dialogResolver.ResolvePlayerResponseAsync(session, cancellationToken);
            lock (session)
            {
                session.PendingUtterances.Enqueue(utterance);
                session.UpdatedAt = timeProvider.GetUtcNow();
                return Result(session, UtteranceEvent(session, utterance));
            }
        }
        finally
        {
            lock (session) session.IsResolvingPlayerInput = false;
        }
    }

    public async Task<SceneEngineResult?> SetSceneContextAsync(
        string sessionId, string sceneId, CancellationToken cancellationToken)
    {
        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        var scene = scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene is null) return null;
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        lock (session)
        {
            var context = scene.Context is null
                ? null
                : templates.Resolve(scene.Context, session.RoleAssignments);
            session.CurrentContext = context;
            session.UpdatedAt = timeProvider.GetUtcNow();
            return Result(session, new SceneEngineEvent(
                SceneEngineEventType.ContextChanged, scene.Id, session.ElapsedTime, context));
        }
    }

    public async Task<SceneEngineResult?> PrepareActiveSceneAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        string? sceneId;
        lock (session) sceneId = session.CurrentSceneId;
        var scene = scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene is null) return new SceneEngineResult(session, []);

        var utterances = new List<ActorUtterance>();
        for (var index = 0; index < scene.Dialog.Count; index++)
            utterances.Add(await dialogResolver.ResolveAsync(session, scene, scene.Dialog[index], index, cancellationToken));

        lock (session)
        {
            if (session.CurrentSceneId != scene.Id) return new SceneEngineResult(session, []);
            var events = new List<SceneEngineEvent>();
            foreach (var utterance in utterances)
            {
                session.PendingUtterances.Enqueue(utterance);
                events.Add(UtteranceEvent(session, utterance));
            }
            session.UpdatedAt = timeProvider.GetUtcNow();
            return new SceneEngineResult(session, events);
        }
    }

    public async Task<SceneEngineResult?> TriggerLineAsync(
        string sessionId, string sceneId, int lineIndex, CancellationToken cancellationToken)
    {
        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        var scene = scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene is null || lineIndex < 0 || lineIndex >= scene.Dialog.Count) return null;
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        var utterance = await dialogResolver.ResolveAsync(
            session, scene, scene.Dialog[lineIndex], lineIndex, cancellationToken);
        lock (session)
        {
            if (session.IsResolvingPlayerInput)
            {
                return new SceneEngineResult(session, []);
            }

            session.PendingUtterances.Enqueue(utterance);
            session.UpdatedAt = timeProvider.GetUtcNow();
            return Result(session, UtteranceEvent(session, utterance));
        }
    }

    public async Task<SceneEngineResult?> AcknowledgeUtteranceAsync(
        string sessionId, string utteranceId, CancellationToken cancellationToken)
    {
        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        lock (session)
        {
            if (!session.PendingUtterances.TryPeek(out var pending) || pending.Id != utteranceId)
                return new SceneEngineResult(session, []);
            session.PendingUtterances.Dequeue();
            session.Transcript.Add(new TranscriptEntry(
                Guid.NewGuid().ToString("N"), TranscriptSpeaker.Actor, pending.Text,
                timeProvider.GetUtcNow(), ActorId: pending.ActorId, UtteranceId: pending.Id));
            var events = new List<SceneEngineEvent>();
            if (session.PendingUtterances.Count == 0 && session.CurrentSceneId is not null &&
                session.Scenes.TryGetValue(session.CurrentSceneId, out var state))
            {
                var sceneId = session.CurrentSceneId;
                session.Scenes[sceneId] = state with
                { Status = SceneExecutionStatus.Completed, CompletedAt = session.ElapsedTime };
                session.CurrentSceneId = null;
                events.Add(new SceneEngineEvent(SceneEngineEventType.SceneCompleted, sceneId, session.ElapsedTime));
                StartNextEligibleScene(session, scenes, events);
            }
            session.UpdatedAt = timeProvider.GetUtcNow();
            return new SceneEngineResult(session, events);
        }
    }

    public SceneEngineResult? FailActiveScene(string sessionId, string error)
    {
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        lock (session)
        {
            if (session.CurrentSceneId is null ||
                !session.Scenes.TryGetValue(session.CurrentSceneId, out var state))
                return new SceneEngineResult(session, []);
            var sceneId = session.CurrentSceneId;
            session.Scenes[sceneId] = state with
            { Status = SceneExecutionStatus.Failed, Error = error };
            session.PendingUtterances.Clear();
            session.CurrentSceneId = null;
            session.UpdatedAt = timeProvider.GetUtcNow();
            return Result(session, new SceneEngineEvent(
                SceneEngineEventType.SceneFailed, sceneId, session.ElapsedTime));
        }
    }

    public SceneEngineResult? FailUtterance(string sessionId, string utteranceId, string error)
    {
        if (!sessions.TryGet(sessionId, out var session) || session is null) return null;
        lock (session)
        {
            if (!session.PendingUtterances.TryPeek(out var pending) || pending.Id != utteranceId)
                return new SceneEngineResult(session, []);

            session.PendingUtterances.Dequeue();
            var events = new List<SceneEngineEvent>();
            if (session.CurrentSceneId is not null &&
                session.Scenes.TryGetValue(session.CurrentSceneId, out var state))
            {
                var sceneId = session.CurrentSceneId;
                session.Scenes[sceneId] = state with
                { Status = SceneExecutionStatus.Failed, Error = error };
                session.PendingUtterances.Clear();
                session.CurrentSceneId = null;
                events.Add(new SceneEngineEvent(
                    SceneEngineEventType.SceneFailed, sceneId, session.ElapsedTime));
            }

            session.UpdatedAt = timeProvider.GetUtcNow();
            return new SceneEngineResult(session, events);
        }
    }

    public async Task<SceneEngineResult?> InitializeAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        var scenes = await sceneLoader.LoadAllAsync(cancellationToken);
        lock (session)
        {
            foreach (var scene in scenes)
            {
                session.Scenes.TryAdd(
                    scene.Id,
                    new SceneExecutionState(scene.Id));
            }

            session.UpdatedAt = timeProvider.GetUtcNow();
            return Result(
                session,
                new SceneEngineEvent(
                    SceneEngineEventType.SessionInitialized,
                    ElapsedTime: session.ElapsedTime));
        }
    }

    public async Task<SceneEngineResult?> StartAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var loadedScenes = await sceneLoader.LoadAllAsync(cancellationToken);
        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        lock (session)
        {
            EnsureSceneStates(session, loadedScenes);
            session.Status = GameSessionStatus.Running;
            session.IsClockRunning = true;
            session.UpdatedAt = timeProvider.GetUtcNow();

            var events = new List<SceneEngineEvent>
            {
                new(SceneEngineEventType.GameStarted, ElapsedTime: session.ElapsedTime)
            };
            StartNextEligibleScene(session, loadedScenes, events);
            return new SceneEngineResult(session, events);
        }
    }

    public SceneEngineResult? Pause(string sessionId)
    {
        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        lock (session)
        {
            session.Status = GameSessionStatus.Paused;
            session.IsClockRunning = false;
            session.UpdatedAt = timeProvider.GetUtcNow();
            return Result(
                session,
                new SceneEngineEvent(
                    SceneEngineEventType.GamePaused,
                    ElapsedTime: session.ElapsedTime));
        }
    }

    public async Task<SceneEngineResult?> AdvanceTimeAsync(
        string sessionId,
        TimeSpan amount,
        CancellationToken cancellationToken)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Time cannot advance by a negative amount.");
        }

        var loadedScenes = await sceneLoader.LoadAllAsync(cancellationToken);
        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        lock (session)
        {
            EnsureSceneStates(session, loadedScenes);
            var events = new List<SceneEngineEvent>();

            if (session.IsClockRunning)
            {
                session.ElapsedTime += amount;
                session.UpdatedAt = timeProvider.GetUtcNow();
                events.Add(new SceneEngineEvent(
                    SceneEngineEventType.TimeAdvanced,
                    ElapsedTime: session.ElapsedTime));
                StartNextEligibleScene(session, loadedScenes, events);
            }

            return new SceneEngineResult(session, events);
        }
    }

    public async Task<SceneEngineResult?> TriggerSceneAsync(
        string sessionId,
        string sceneId,
        CancellationToken cancellationToken)
    {
        var loadedScenes = await sceneLoader.LoadAllAsync(cancellationToken);
        var scene = loadedScenes.FirstOrDefault(candidate => candidate.Id == sceneId);
        if (scene is null)
        {
            return null;
        }

        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        lock (session)
        {
            EnsureSceneStates(session, loadedScenes);
            if (session.IsResolvingPlayerInput || session.CurrentSceneId is not null ||
                session.Scenes[scene.Id].Status != SceneExecutionStatus.Pending)
            {
                return new SceneEngineResult(session, []);
            }

            var events = new List<SceneEngineEvent>();
            StartScene(session, scene, events);
            return new SceneEngineResult(session, events);
        }
    }

    public async Task<SceneEngineResult?> CompleteSceneAsync(
        string sessionId,
        string sceneId,
        CancellationToken cancellationToken)
    {
        var loadedScenes = await sceneLoader.LoadAllAsync(cancellationToken);
        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            return null;
        }

        lock (session)
        {
            if (session.CurrentSceneId != sceneId ||
                !session.Scenes.TryGetValue(sceneId, out var state) ||
                state.Status != SceneExecutionStatus.Active)
            {
                return new SceneEngineResult(session, []);
            }

            session.Scenes[sceneId] = state with
            {
                Status = SceneExecutionStatus.Completed,
                CompletedAt = session.ElapsedTime
            };
            session.CurrentSceneId = null;
            session.UpdatedAt = timeProvider.GetUtcNow();

            var events = new List<SceneEngineEvent>
            {
                new(
                    SceneEngineEventType.SceneCompleted,
                    sceneId,
                    session.ElapsedTime)
            };
            StartNextEligibleScene(session, loadedScenes, events);
            return new SceneEngineResult(session, events);
        }
    }

    private static void EnsureSceneStates(GameSession session, IEnumerable<GameScene> scenes)
    {
        foreach (var scene in scenes)
        {
            session.Scenes.TryAdd(scene.Id, new SceneExecutionState(scene.Id));
        }
    }

    private void StartNextEligibleScene(
        GameSession session,
        IEnumerable<GameScene> scenes,
        List<SceneEngineEvent> events)
    {
        if (session.IsResolvingPlayerInput || session.CurrentSceneId is not null)
        {
            return;
        }

        var scene = scenes.FirstOrDefault(candidate => IsEligible(session, candidate));
        if (scene is not null)
        {
            events.Add(new SceneEngineEvent(
                SceneEngineEventType.SceneEligible,
                scene.Id,
                session.ElapsedTime));
            StartScene(session, scene, events);
        }
    }

    private static bool IsEligible(GameSession session, GameScene scene)
    {
        if (!session.Scenes.TryGetValue(scene.Id, out var state) ||
            state.Status != SceneExecutionStatus.Pending)
        {
            return false;
        }

        return scene.Trigger.Type switch
        {
            "timestamp" => session.ElapsedTime >= TimeSpan.FromSeconds(scene.Trigger.At ?? 0),
            "after" => IsAfterTriggerEligible(session, scene.Trigger),
            _ => false
        };
    }

    private static bool IsAfterTriggerEligible(GameSession session, SceneTrigger trigger)
    {
        if (trigger.After is null ||
            !session.Scenes.TryGetValue(trigger.After, out var prerequisite) ||
            prerequisite.CompletedAt is null)
        {
            return false;
        }

        return session.ElapsedTime >=
            prerequisite.CompletedAt.Value + TimeSpan.FromSeconds(trigger.Wait);
    }

    private void StartScene(
        GameSession session,
        GameScene scene,
        List<SceneEngineEvent> events)
    {
        var state = session.Scenes[scene.Id];
        session.Scenes[scene.Id] = state with
        {
            Status = SceneExecutionStatus.Active,
            StartedAt = session.ElapsedTime
        };
        session.CurrentSceneId = scene.Id;
        session.CurrentContext = scene.Context is null
            ? null
            : templates.Resolve(scene.Context, session.RoleAssignments);
        session.UpdatedAt = timeProvider.GetUtcNow();

        events.Add(new SceneEngineEvent(
            SceneEngineEventType.SceneStarted,
            scene.Id,
            session.ElapsedTime));

        if (!string.IsNullOrWhiteSpace(session.CurrentContext))
        {
            events.Add(new SceneEngineEvent(
                SceneEngineEventType.ContextChanged,
                scene.Id,
                session.ElapsedTime,
                session.CurrentContext));
        }

        if (scene.Kind == "signal-lost")
        {
            events.Add(new SceneEngineEvent(
                SceneEngineEventType.VisualEffectRequested,
                scene.Id,
                session.ElapsedTime,
                Effect: "signal-lost"));
        }
    }

    private static SceneEngineResult Result(GameSession session, SceneEngineEvent gameEvent) =>
        new(session, [gameEvent]);

    private static SceneEngineEvent UtteranceEvent(GameSession session, ActorUtterance utterance) =>
        new(
            utterance.Channel == DeliveryChannel.SpokenAvatar
                ? SceneEngineEventType.UtteranceReady
                : SceneEngineEventType.NonSpokenDeliveryReady,
            utterance.SceneId,
            session.ElapsedTime,
            Utterance: utterance);
}
