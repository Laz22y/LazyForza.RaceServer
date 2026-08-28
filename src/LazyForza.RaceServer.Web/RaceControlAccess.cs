namespace LazyForza.RaceServer.Web;

public enum RaceControlRole
{
    SuperAdmin,
    Administrator,
    Steward
}

public enum RaceControlPermission
{
    View,
    ManageRace,
    Adjudicate,
    ManageControlAccounts
}

public sealed record RaceControlPrincipal(Guid Id, string Name, RaceControlRole Role);

public sealed record RaceControlAccountSummary(
    Guid Id,
    string Name,
    RaceControlRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RaceControlAccountCreateRequest(
    string Name,
    RaceControlRole Role,
    string Password);

public sealed record RaceControlAccountUpdateRequest(
    string Name,
    RaceControlRole Role,
    string? Password);

public static class RaceControlAccess
{
    public static bool Allows(RaceControlRole role, RaceControlPermission permission) => role switch
    {
        RaceControlRole.SuperAdmin => true,
        RaceControlRole.Administrator => permission != RaceControlPermission.ManageControlAccounts,
        RaceControlRole.Steward => permission is RaceControlPermission.View or RaceControlPermission.Adjudicate,
        _ => false
    };

    public static RaceControlPermission RequiredPermission(string method, string path)
    {
        if (path.StartsWith("/api/admin/control-accounts", StringComparison.OrdinalIgnoreCase))
            return RaceControlPermission.ManageControlAccounts;
        if (HttpMethods.IsGet(method)) return RaceControlPermission.View;
        if (path.Equals("/api/admin/penalty", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/penalty/update", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/investigation", StringComparison.OrdinalIgnoreCase))
            return RaceControlPermission.Adjudicate;
        return RaceControlPermission.ManageRace;
    }
}
