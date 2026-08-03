using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LazyForza.RaceServer.Web;

public sealed class AdminSessionStore(Func<string, bool> passwordMatches)
{
    public const string CookieName = "lfz-race-admin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);

    public bool PasswordMatches(string password) =>
        passwordMatches(password);

    public string Create()
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[token] = DateTimeOffset.UtcNow + Lifetime;
        return token;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var expiresAt)) return false;
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) sessions.TryRemove(token, out _);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions.Where(pair => pair.Value <= now))
            sessions.TryRemove(pair.Key, out _);
    }

}
