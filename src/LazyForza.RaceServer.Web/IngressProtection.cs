using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LazyForza.RaceServer.Web;

public sealed record IngressOptions
{
    public int LoginFailureLimit { get; init; } = 5;
    public int SourceLoginFailureLimit { get; init; } = 120;
    public int LoginFailureWindowSeconds { get; init; } = 60;
    public int MaximumConcurrentLogins { get; init; } = 32;
    public int MaximumFailureBuckets { get; init; } = 4096;
    public int MaximumUnauthenticatedConnections { get; init; } = 64;
    public int MaximumUnauthenticatedPerSource { get; init; } = 32;
    public int LoginTimeoutSeconds { get; init; } = 12;
    public int MessagesPerSecond { get; init; } = 60;
    public int MessageBurst { get; init; } = 120;
    public int BytesPerSecond { get; init; } = 131072;
    public int ByteBurst { get; init; } = 262144;
    public string[] TrustedProxyAddresses { get; init; } = [];

    public IngressOptions Validate()
    {
        if (LoginFailureLimit < 1 || SourceLoginFailureLimit < LoginFailureLimit || LoginFailureWindowSeconds is < 1 or > 3600 ||
            MaximumConcurrentLogins is < 1 or > 1024 || MaximumFailureBuckets is < 2 or > 65536 ||
            MaximumUnauthenticatedConnections is < 1 or > 1024 || MaximumUnauthenticatedPerSource < 1 ||
            MaximumUnauthenticatedPerSource > MaximumUnauthenticatedConnections || LoginTimeoutSeconds is < 1 or > 120 ||
            MessagesPerSecond < 1 || MessageBurst < 1 || BytesPerSecond < 1 || ByteBurst < 1 ||
            TrustedProxyAddresses.Any(value => !IPAddress.TryParse(value, out _)))
            throw new InvalidOperationException("Invalid RaceServer:Ingress limits or trusted proxy addresses.");
        return this;
    }
}

/// <summary>Transport limits, independent of race state and authenticated player quotas.</summary>
public sealed class IngressProtection(IngressOptions options, Func<long>? clock = null)
{
    public IngressOptions Options { get; } = options.Validate();
    private readonly object sync = new();
    private readonly Dictionary<string, Bucket> failures = [];
    private readonly Dictionary<string, int> pending = [];
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private int concurrentLogins;
    private int unauthenticated;

    public string Source(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        var normalized = peer?.MapToIPv6();
        // Trust exactly one overwritten X-Forwarded-For value from an explicitly configured immediate peer.
        if (normalized is not null && Options.TrustedProxyAddresses.Any(value => IPAddress.Parse(value).MapToIPv6().Equals(normalized)) &&
            IPAddress.TryParse(context.Request.Headers["X-Forwarded-For"].ToString(), out var forwarded))
            peer = forwarded;
        return peer?.MapToIPv6().ToString() ?? "unknown";
    }

    public static string LoginIdentity(string? displayName, string? resumeToken) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(resumeToken)
            ? (displayName ?? "").Trim().ToUpperInvariant()[..Math.Min((displayName ?? "").Trim().Length, 20)]
            : resumeToken)));

    public IDisposable? TryAcquireConnection(string source)
    {
        lock (sync)
        {
            var count = pending.GetValueOrDefault(source);
            if (unauthenticated >= Options.MaximumUnauthenticatedConnections || count >= Options.MaximumUnauthenticatedPerSource) return null;
            unauthenticated++;
            pending[source] = count + 1;
            return new Release(() => { lock (sync) { unauthenticated--; if (--pending[source] == 0) pending.Remove(source); } });
        }
    }

    public LoginAttempt? TryLogin(string source, string channel, string identity, out int retryAfterSeconds)
    {
        lock (sync)
        {
            var time = now();
            foreach (var key in failures.Where(pair => pair.Value.Expires <= time).Select(pair => pair.Key).ToArray()) failures.Remove(key);
            var sourceKey = channel + ":" + source;
            var identityKey = sourceKey + ":" + identity;
            retryAfterSeconds = 1;
            if (concurrentLogins >= Options.MaximumConcurrentLogins) return null;
            foreach (var (key, limit) in new[] { (sourceKey, Options.SourceLoginFailureLimit), (identityKey, Options.LoginFailureLimit) })
                if (failures.TryGetValue(key, out var existing) && existing.Count >= limit)
                { retryAfterSeconds = Seconds(existing.Expires - time); return null; }
            var missing = new[] { sourceKey, identityKey }.Count(key => !failures.ContainsKey(key));
            if (failures.Count + missing > Options.MaximumFailureBuckets)
            { retryAfterSeconds = Seconds(failures.Values.Min(bucket => bucket.Expires) - time); return null; }
            var buckets = new[] { sourceKey, identityKey }.Select(key =>
            {
                if (!failures.TryGetValue(key, out var bucket)) failures[key] = bucket = new Bucket(time + Options.LoginFailureWindowSeconds * 1000L);
                bucket.Count++; // Reserve before authentication, so concurrent password checks cannot exceed the budget.
                return bucket;
            }).ToArray();
            concurrentLogins++;
            return new LoginAttempt(success => { lock (sync)
            {
                concurrentLogins--;
                if (success) for (var i = 0; i < buckets.Length; i++)
                {
                    var key = i == 0 ? sourceKey : identityKey;
                    if (--buckets[i].Count == 0 && failures.GetValueOrDefault(key) == buckets[i]) failures.Remove(key);
                }
            } });
        }
    }

    private static int Seconds(long milliseconds) => Math.Max(1, (int)Math.Ceiling(milliseconds / 1000d));
    private sealed class Bucket(long expires) { public long Expires { get; } = expires; public int Count { get; set; } }
    private sealed class Release(Action release) : IDisposable { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
    public sealed class LoginAttempt(Action<bool> complete) : IDisposable
    {
        private Action<bool>? action = complete;
        public void Complete(bool credentialsValid) => Interlocked.Exchange(ref action, null)?.Invoke(credentialsValid);
        public void Dispose() => Complete(false);
    }
}

public sealed class ConnectionMessageBudget(IngressOptions options, Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private double messages = options.MessageBurst;
    private double bytes = options.ByteBurst;
    private long? last;

    public bool TryConsume(int byteCount, bool newMessage, out int retryAfterSeconds)
    {
        var time = now();
        var elapsed = Math.Max(0, time - (last ?? time)) / 1000d;
        last = time;
        messages = Math.Min(options.MessageBurst, messages + elapsed * options.MessagesPerSecond);
        bytes = Math.Min(options.ByteBurst, bytes + elapsed * options.BytesPerSecond);
        var cost = newMessage ? 1 : 0;
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(Math.Max((cost - messages) / options.MessagesPerSecond, (byteCount - bytes) / options.BytesPerSecond)));
        if (messages < cost || bytes < byteCount) return false;
        messages -= cost;
        bytes -= byteCount;
        return true;
    }
}

public static class IngressHttp
{
    public static string RetryMessage(int seconds) => $"请求过于频繁，请在 {seconds} 秒后重试。";
    public static IResult TooManyRequests(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { code = "rateLimited", error = RetryMessage(seconds), retryAfterSeconds = seconds }, statusCode: 429);
    }
}
