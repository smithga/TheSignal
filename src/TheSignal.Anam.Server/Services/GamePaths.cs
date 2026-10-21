namespace TheSignal.Anam.Server.Services;

// Resolves paths into the shared /game content folder at the repo root (sibling of /src).
public static class GamePaths
{
    public static string Root(IWebHostEnvironment env) =>
        Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "game"));

    public static string PlotOverview(IWebHostEnvironment env) =>
        Path.Combine(Root(env), "plot", "overview.md");

    public static string Actors(IWebHostEnvironment env) =>
        Path.Combine(Root(env), "actors");

    public static string Players(IWebHostEnvironment env) =>
        Path.Combine(Root(env), "players", "players.json");

    public static string Roles(IWebHostEnvironment env) =>
        Path.Combine(Root(env), "roles", "roles.json");
}
