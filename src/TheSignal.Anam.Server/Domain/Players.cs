namespace TheSignal.Anam.Server.Domain;

// Safe subset of a player profile that can be used for game templating and
// returned to the browser. Contact details remain server-side.
public sealed record PublicPlayerProfile(
    string FirstName,
    int Age,
    string PhysicalAppearance,
    string Background,
    string Career);

public sealed record RoleAssignment(string RoleId, PublicPlayerProfile Player);
