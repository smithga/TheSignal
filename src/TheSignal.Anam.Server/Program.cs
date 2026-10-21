using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TheSignal.Anam.Server.Domain;
using TheSignal.Anam.Server.Options;
using TheSignal.Anam.Server.Services;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    var envFile = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".env"));
    builder.Configuration.AddInMemoryCollection(LoadDevelopmentEnvFile(envFile));
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.Configure<AnamOptions>(builder.Configuration.GetSection(AnamOptions.SectionName));
builder.Services.Configure<LocalVoiceOptions>(builder.Configuration.GetSection(LocalVoiceOptions.SectionName));
builder.Services.Configure<LocalLlmOptions>(builder.Configuration.GetSection(LocalLlmOptions.SectionName));
builder.Services.AddSingleton<GamePlotKnowledgeService>();
builder.Services.AddSingleton<PlayerDirectoryService>();
builder.Services.AddSingleton<SceneLoaderService>();
builder.Services.AddSingleton<ActorLoaderService>();
builder.Services.AddSingleton<RoleAssignmentService>();
builder.Services.AddSingleton<RoleTemplateResolver>();
builder.Services.AddSingleton<GameContentValidationService>();
builder.Services.AddSingleton<DialogResolver>();
builder.Services.AddSingleton<RehearsalDialogGenerator>();
builder.Services.AddHttpClient<OllamaDialogGenerator>(client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddSingleton<OllamaDialogGenerator>();
// Free local model when Ollama:Model is configured; the deterministic rehearsal stub
// (no network) stays as the default so dev mode makes no external calls unless asked.
builder.Services.AddSingleton<IActorDialogGenerator>(services =>
    services.GetRequiredService<OllamaDialogGenerator>().IsConfigured
        ? services.GetRequiredService<OllamaDialogGenerator>()
        : services.GetRequiredService<RehearsalDialogGenerator>());
builder.Services.AddSingleton<SceneEngine>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IGameSessionStore, InMemoryGameSessionStore>();
builder.Services.AddHttpClient<AnamSessionClient>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (app.Environment.IsDevelopment())
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});

app.MapGet("/api/anam/configuration", (AnamSessionClient anamClient) =>
    Results.Ok(new { configured = anamClient.IsConfigured }));

app.MapGet("/api/local/configuration", (
    IOptions<LocalVoiceOptions> localVoice,
    IOptions<LocalLlmOptions> localLlm,
    OllamaDialogGenerator ollama) =>
{
    var bridgeEndpoint = localVoice.Value.BridgeEndpoint.Trim();
    return Results.Ok(new
    {
        configured = bridgeEndpoint.Length > 0,
        bridgeEndpoint,
        dialogModel = localLlm.Value.Model.Trim(),
        localDialogActive = ollama.IsConfigured
    });
});

app.MapPost("/api/anam/session-token", async (AnamSessionClient anamClient, CancellationToken ct) =>
{
    if (!anamClient.IsConfigured)
    {
        return Results.Problem("Anam API key and either PersonaId or AvatarId are not configured on the server.", statusCode: StatusCodes.Status500InternalServerError);
    }

    try
    {
        var sessionToken = await anamClient.CreateSessionTokenAsync(ct);
        return Results.Ok(new { sessionToken });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/game/scenes", async (SceneLoaderService sceneLoader, CancellationToken ct) =>
{
    var scenes = await sceneLoader.LoadAllAsync(ct);
    return Results.Ok(scenes);
});

app.MapGet("/api/game/actors", async (ActorLoaderService actorLoader, CancellationToken ct) =>
{
    var actors = await actorLoader.LoadAllAsync(ct);
    return Results.Ok(actors);
});

app.MapGet("/api/game/validation", async (
    GameContentValidationService validation,
    CancellationToken ct) =>
{
    var result = await validation.ValidateAsync(ct);
    return result.IsValid ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/game/roles/assign", async (RoleAssignmentService roleService, CancellationToken ct) =>
{
    var assignments = await roleService.AssignAsync(null, ct);
    return Results.Ok(assignments);
});

app.MapGet("/api/game/roles", () =>
{
    return Results.Ok(Array.Empty<RoleAssignment>());
});

app.MapPost("/api/game/sessions", (IGameSessionStore sessions) =>
{
    var session = sessions.Create();
    return Results.Created($"/api/game/sessions/{session.Id}", session);
});

app.MapGet("/api/game/sessions/{sessionId}", (string sessionId, IGameSessionStore sessions) =>
{
    return sessions.TryGet(sessionId, out var session)
        ? Results.Ok(session)
        : Results.NotFound();
});

app.MapPost("/api/game/sessions/{sessionId}/reset", (string sessionId, IGameSessionStore sessions) =>
{
    var session = sessions.Reset(sessionId);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.MapDelete("/api/game/sessions/{sessionId}", (string sessionId, IGameSessionStore sessions) =>
{
    return sessions.End(sessionId) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/game/sessions/{sessionId}/roles/assign", async (
    string sessionId,
    int? seed,
    IGameSessionStore sessions,
    RoleAssignmentService roleService,
    CancellationToken ct) =>
{
    if (!sessions.TryGet(sessionId, out var session) || session is null)
    {
        return Results.NotFound();
    }

    try
    {
        var assignments = await roleService.AssignAsync(seed, ct);
        lock (session)
        {
            session.RoleAssignments.Clear();
            session.RoleAssignments.AddRange(assignments);
            session.UpdatedAt = TimeProvider.System.GetUtcNow();
        }
        return Results.Ok(assignments);
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/game/sessions/{sessionId}/initialize", async (
    string sessionId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.InitializeAsync(sessionId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/start", async (
    string sessionId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.StartAsync(sessionId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/pause", (
    string sessionId,
    SceneEngine engine) =>
{
    var result = engine.Pause(sessionId);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/advance", async (
    string sessionId,
    AdvanceTimeRequest request,
    SceneEngine engine,
    CancellationToken ct) =>
{
    if (request.Seconds < 0)
    {
        return Results.BadRequest(new { error = "Seconds must not be negative." });
    }

    var result = await engine.AdvanceTimeAsync(sessionId, TimeSpan.FromSeconds(request.Seconds), ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/{sceneId}/trigger", async (
    string sessionId,
    string sceneId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.TriggerSceneAsync(sessionId, sceneId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/{sceneId}/context", async (
    string sessionId,
    string sceneId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.SetSceneContextAsync(sessionId, sceneId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/{sceneId}/complete", async (
    string sessionId,
    string sceneId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.CompleteSceneAsync(sessionId, sceneId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/active/prepare", async (
    string sessionId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.PrepareActiveSceneAsync(sessionId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/{sceneId}/lines/{lineIndex:int}/trigger", async (
    string sessionId,
    string sceneId,
    int lineIndex,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.TriggerLineAsync(sessionId, sceneId, lineIndex, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/player-input", async (
    string sessionId,
    PlayerInputRequest request,
    SceneEngine engine,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
    {
        return Results.BadRequest("Player input must contain 1 to 2000 characters.");
    }

    try
    {
        var result = await engine.ReceivePlayerInputAsync(sessionId, request.Text.Trim(), request.PlayerId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapPost("/api/game/sessions/{sessionId}/utterances/{utteranceId}/complete", async (
    string sessionId,
    string utteranceId,
    SceneEngine engine,
    CancellationToken ct) =>
{
    var result = await engine.AcknowledgeUtteranceAsync(sessionId, utteranceId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/utterances/{utteranceId}/fail", (
    string sessionId,
    string utteranceId,
    SceneFailureRequest request,
    SceneEngine engine) =>
{
    var result = engine.FailUtterance(sessionId, utteranceId, request.Error);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/game/sessions/{sessionId}/scenes/active/fail", (
    string sessionId,
    SceneFailureRequest request,
    SceneEngine engine) =>
{
    var result = engine.FailActiveScene(sessionId, request.Error);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.Run();

static Dictionary<string, string?> LoadDevelopmentEnvFile(string path)
{
    string[] DevelopmentEnvPrefixes = ["ANAM__", "LOCAL__", "OLLAMA__"];
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return values;

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

        var separator = line.IndexOf('=');
        if (separator <= 0) continue;
        var key = line[..separator].Trim();
        if (!DevelopmentEnvPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

        // Real environment variables take precedence over the convenience file.
        if (Environment.GetEnvironmentVariable(key) is not null) continue;
        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
    }

    return values;
}

public sealed record AdvanceTimeRequest(double Seconds);
public sealed record PlayerInputRequest(string Text, string? PlayerId);
public sealed record SceneFailureRequest(string Error);
