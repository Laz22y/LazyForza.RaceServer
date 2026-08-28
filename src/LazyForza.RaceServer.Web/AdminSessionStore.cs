using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LazyForza.RaceServer.Web;

public sealed class AdminSessionStore(Func<string, RaceControlPrincipal?> authenticate)
{
    public const string CookieName = "lfz-race-admin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public RaceControlPrincipal? Authenticate(string password) => authenticate(password);

    public string Create(RaceControlPrincipal principal)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[token] = new Session(principal, DateTimeOffset.UtcNow + Lifetime);
        return token;
    }

    public bool TryGetPrincipal(string? token, out RaceControlPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var session)) return false;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(token, out _);
            return false;
        }
        principal = session.Principal;
        return true;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) sessions.TryRemove(token, out _);
    }

    public void RevokeAccount(Guid accountId)
    {
        foreach (var pair in sessions.Where(pair => pair.Value.Principal.Id == accountId))
            sessions.TryRemove(pair.Key, out _);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions.Where(pair => pair.Value.ExpiresAt <= now))
            sessions.TryRemove(pair.Key, out _);
    }

    private sealed record Session(RaceControlPrincipal Principal, DateTimeOffset ExpiresAt);
}
