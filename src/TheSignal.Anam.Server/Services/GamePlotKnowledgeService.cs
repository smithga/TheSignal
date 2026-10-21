namespace TheSignal.Anam.Server.Services;

// Reads the game plot/lore file fresh on each call so edits show up without restarting the server.
public class GamePlotKnowledgeService(IWebHostEnvironment env, ILogger<GamePlotKnowledgeService> logger)
{
    private readonly string _path = GamePaths.PlotOverview(env);

    public async Task<string> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            logger.LogWarning("Game plot knowledge file not found at {Path}; continuing without it.", _path);
            return string.Empty;
        }

        return await File.ReadAllTextAsync(_path, ct);
    }
}
