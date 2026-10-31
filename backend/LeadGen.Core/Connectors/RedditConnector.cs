using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LeadGen.Core.Connectors;

/// <param name="UserAgent">Reddit requires a descriptive, unique User-Agent, e.g. "web:leadgen:1.0 (by /u/yourname)".</param>
public record RedditOptions(string? ClientId, string? ClientSecret, string UserAgent = "web:leadgen:1.0");

/// <summary>Shared across connector instances so the OAuth token is reused until it expires.</summary>
public class RedditTokenCache
{
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal string? Token;
    internal DateTimeOffset ExpiresAt;
    public void Clear() { Token = null; }
}

/// <summary>
/// Reddit search through the official API (OAuth2 "application only" flow, read access). Uses only
/// permitted endpoints, sends a proper User-Agent, and honours the x-ratelimit-* headers and 429 responses.
/// Reddit search has no "since" parameter, so the cursor is the newest created_utc seen and older posts are dropped.
/// </summary>
public class RedditConnector(HttpClient http, RedditOptions options, RedditTokenCache? cache = null, TimeProvider? clock = null)
    : ILeadSourceConnector
{
    private readonly RedditTokenCache _cache = cache ?? new RedditTokenCache();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string SourceName => "reddit";

    public async Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
            return SourceFetchResult.Fail(FetchErrorKind.NotConfigured, "Reddit client id/secret are not configured");
        if (request.Keywords.Count == 0)
            return SourceFetchResult.Fail(FetchErrorKind.NotConfigured, "No search phrases configured");

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (token, tokenFailure) = await GetTokenAsync(ct);
                if (token is null) return tokenFailure!;

                var result = await SearchAsync(request, token, ct);
                // An expired/revoked token: drop it and try once more with a fresh one.
                if (result.Kind == FetchErrorKind.Auth && result.Error == "unauthorized" && attempt == 0)
                { _cache.Clear(); continue; }
                return result.Error == "unauthorized"
                    ? SourceFetchResult.Fail(FetchErrorKind.Auth, "Reddit rejected the access token (401)") : result;
            }
            return SourceFetchResult.Fail(FetchErrorKind.Auth, "Reddit rejected the access token (401)");
        }
        catch (HttpRequestException ex) { return SourceFetchResult.Fail(FetchErrorKind.Transient, $"Reddit unreachable: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return SourceFetchResult.Fail(FetchErrorKind.Transient, "Reddit request timed out"); }
        catch (JsonException) { return SourceFetchResult.Fail(FetchErrorKind.Transient, "Reddit returned an unreadable response"); }
    }

    private async Task<(string? Token, SourceFetchResult? Failure)> GetTokenAsync(CancellationToken ct)
    {
        await _cache.Gate.WaitAsync(ct);
        try
        {
            if (_cache.Token is not null && _cache.ExpiresAt > _clock.GetUtcNow().AddSeconds(60)) return (_cache.Token, null);

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token")
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") })
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}")));
            msg.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

            using var resp = await http.SendAsync(msg, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return (null, SourceFetchResult.Fail(FetchErrorKind.RateLimited, "Reddit rate limit reached", ResetFrom(resp)));
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, SourceFetchResult.Fail(FetchErrorKind.Auth,
                    $"Reddit rejected the client id/secret ({(int)resp.StatusCode}); check the app credentials"));
            if (!resp.IsSuccessStatusCode)
                return (null, SourceFetchResult.Fail(FetchErrorKind.Transient, $"Reddit token endpoint returned {(int)resp.StatusCode}"));

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("access_token", out var t) || t.GetString() is not { Length: > 0 } token)
                return (null, SourceFetchResult.Fail(FetchErrorKind.Auth,
                    "Reddit did not return an access token; check the app type and credentials"));
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _cache.Token = token;
            _cache.ExpiresAt = _clock.GetUtcNow().AddSeconds(seconds);
            return (token, null);
        }
        finally { _cache.Gate.Release(); }
    }

    private async Task<SourceFetchResult> SearchAsync(SourceSearchRequest request, string token, CancellationToken ct)
    {
        // Search syntax: quoted phrases joined with OR. Keep under Reddit's 512 character query limit.
        var terms = new List<string>();
        var length = 0;
        foreach (var k in request.Keywords.Select(k => k.Trim().Replace("\"", "")).Where(k => k.Length > 0))
        {
            var term = k.Contains(' ') ? $"\"{k}\"" : k;
            if (length + term.Length + 4 > 480) break;
            terms.Add(term); length += term.Length + 4;
        }
        var query = string.Join(" OR ", terms);
        var limit = Math.Clamp(request.MaxResults, 10, 100);
        var url = $"https://oauth.reddit.com/search?q={Uri.EscapeDataString(query)}&sort=new&type=link&limit={limit}" +
                  $"&t={(string.IsNullOrEmpty(request.SinceCursor) ? "week" : "month")}&raw_json=1&restrict_sr=false";

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

        using var resp = await http.SendAsync(msg, ct);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            return SourceFetchResult.Fail(FetchErrorKind.RateLimited, "Reddit rate limit reached", ResetFrom(resp));
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return SourceFetchResult.Fail(FetchErrorKind.Auth, "unauthorized");
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return SourceFetchResult.Fail(FetchErrorKind.Auth, "Reddit denied access (403); the app may lack read permission");
        if ((int)resp.StatusCode >= 500)
            return SourceFetchResult.Fail(FetchErrorKind.Transient, $"Reddit API error {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode)
            return SourceFetchResult.Fail(FetchErrorKind.Transient, $"Reddit API returned {(int)resp.StatusCode}");

        // Remaining quota is exhausted: park the source until the window resets, but keep the posts we did get.
        DateTimeOffset? parkUntil = null;
        if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) &&
            double.TryParse(rem.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left) && left < 1)
            parkUntil = ResetFrom(resp);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        long.TryParse(request.SinceCursor, out var since);
        var posts = new List<SourcePost>();
        long newest = since;

        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                if (!child.TryGetProperty("data", out var d)) continue;
                var id = Str(d, "name") ?? (Str(d, "id") is { } bare ? $"t3_{bare}" : null);
                if (id is null) continue;
                var created = d.TryGetProperty("created_utc", out var c) && c.TryGetDouble(out var cu) ? (long)cu : 0;
                if (created == 0) continue;
                if (created > newest) newest = created;
                if (created <= since) continue;   // already seen in an earlier run

                var author = Str(d, "author");
                var body = Str(d, "selftext") ?? "";
                if (author == "[deleted]" || body is "[removed]" or "[deleted]") continue;
                if (d.TryGetProperty("removed_by_category", out var rb) && rb.ValueKind == JsonValueKind.String) continue;

                var text = $"{Str(d, "title")}\n{body}".Trim();
                var link = Str(d, "permalink") is { } pl ? "https://www.reddit.com" + pl : Str(d, "url") ?? "";
                posts.Add(new SourcePost(id, text, link, DateTimeOffset.FromUnixTimeSeconds(created)));
            }
        }

        var cursor = newest > 0 ? newest.ToString(CultureInfo.InvariantCulture) : request.SinceCursor;
        return new SourceFetchResult(posts, NextCursor: cursor, RateLimitResetsAt: parkUntil);
    }

    private DateTimeOffset ResetFrom(HttpResponseMessage resp)
    {
        double seconds = 60;
        if (resp.Headers.TryGetValues("x-ratelimit-reset", out var v) &&
            double.TryParse(v.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) seconds = r;
        else if (resp.Headers.RetryAfter?.Delta is { } d) seconds = d.TotalSeconds;
        return _clock.GetUtcNow().AddSeconds(Math.Max(1, seconds));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
