using TheSignal.Anam.Server.Domain;

namespace TheSignal.Anam.Server.Services;

public sealed record DialogGenerationRequest(
    string ActorId,
    string ActorDisplayName,
    string ActorInstructions,
    string PlotKnowledge,
    string DirectorIntent,
    string? SceneContext,
    IReadOnlyList<RoleAssignment> RoleContext,
    string PromptVersion,
    IReadOnlyList<TranscriptEntry> Transcript);

public interface IActorDialogGenerator
{
    Task<string> GenerateAsync(DialogGenerationRequest request, CancellationToken cancellationToken);
}

// Deterministic fallback for development. It makes the requested intent visible
// without calling an external model. A real generator can replace it through DI.
public sealed class RehearsalDialogGenerator : IActorDialogGenerator
{
    public Task<string> GenerateAsync(DialogGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"[IMPROVISE: {request.DirectorIntent}]");
    }
}

public sealed class DialogResolver(
    ActorLoaderService actorLoader,
    GamePlotKnowledgeService plotKnowledge,
    RoleTemplateResolver templates,
    IActorDialogGenerator generator)
{
    public async Task<ActorUtterance> ResolveAsync(
        GameSession session,
        GameScene scene,
        DialogLine line,
        int lineIndex,
        CancellationToken cancellationToken)
    {
        var actors = await actorLoader.LoadAllAsync(cancellationToken);
        var actor = actors.FirstOrDefault(candidate => candidate.Id == line.Actor)
            ?? throw new InvalidDataException($"Unknown actor '{line.Actor}' in scene '{scene.Id}'.");
        var resolved = templates.Resolve(line.Text, session.RoleAssignments);
        var knowledge = await plotKnowledge.LoadAsync(cancellationToken);
        var finalText = line.Mode == DialogMode.Say
            ? resolved
            : await generator.GenerateAsync(
                new DialogGenerationRequest(
                    line.Actor,
                    actor.DisplayName,
                    actor.CharacterInstructions,
                    knowledge,
                    resolved,
                    session.CurrentContext,
                    session.RoleAssignments,
                    "signal-dialog-v1",
                    session.Transcript),
                cancellationToken);

        return new ActorUtterance(
            Guid.NewGuid().ToString("N"),
            line.Actor,
            finalText,
            actor.Channel,
            scene.PerformanceDirection,
            scene.Id,
            $"{scene.Id}:{lineIndex}");
    }

    public async Task<ActorUtterance> ResolvePlayerResponseAsync(
        GameSession session,
        CancellationToken cancellationToken)
    {
        var actors = await actorLoader.LoadAllAsync(cancellationToken);
        var actor = actors.FirstOrDefault(candidate => candidate.Id == "evelyn")
            ?? throw new InvalidDataException("Player input cannot be answered because actor 'evelyn' is not loaded.");
        if (actor.Channel != DeliveryChannel.SpokenAvatar)
            throw new InvalidDataException("Player input responses require Evelyn's spoken-avatar delivery channel.");

        var knowledge = await plotKnowledge.LoadAsync(cancellationToken);
        var finalText = await generator.GenerateAsync(
            new DialogGenerationRequest(
                actor.Id,
                actor.DisplayName,
                actor.CharacterInstructions,
                knowledge,
                "Respond in character to the latest player message.",
                session.CurrentContext,
                session.RoleAssignments,
                "signal-dialog-v1",
                session.Transcript),
            cancellationToken);

        return new ActorUtterance(
            Guid.NewGuid().ToString("N"),
            actor.Id,
            finalText,
            actor.Channel,
            SceneId: session.CurrentSceneId,
            DialogLineId: "player-response");
    }
}
