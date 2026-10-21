namespace TheSignal.Anam.Server.Domain;

public enum DialogMode
{
    Say,
    Improvise
}

public record SceneTrigger(string Type, int? At, string? After, int Wait);

// Text is either the final script (Say) or an intent passed to dialog
// generation (Improvise). It is not renderer-specific.
public record DialogLine(string Actor, string Text, DialogMode Mode);

public record GameScene(
    string Id,
    string Title,
    string Actor,
    string Kind,
    SceneTrigger Trigger,
    PerformanceDirection? PerformanceDirection,
    string? Context,
    List<DialogLine> Dialog);
