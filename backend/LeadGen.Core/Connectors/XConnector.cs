using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LeadGen.Core.Connectors;

public record XOptions(string? BearerToken);

/// <summary>
/// X (Twitter) recent search through the official API v2. Needs a paid API tier with search access.
/// Without a token it reports NotConfigured instead of failing silently.
/// </summary>
public class XConnector(HttpClient http, XOptions options) : ILeadSourceConnector
{
    public string SourceName => "x";

    public async Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BearerToken))
            return SourceFetchResult.Fail(FetchErrorKind.NotConfigured, "X bearer token is not configured");
        if (request.Keywords.Count == 0)
            return SourceFetchResult.Fail(FetchErrorKind.NotConfigured, "No search phrases configured");

        // Recent search limits query length, so keep the OR-group short.
        var terms = request.Keywords.Select(k => k.Contains(' ') ? $"\"{k}\"" : k).Take(15);
        var query = $"({string.Join(" OR ", terms)}) -is:retweet -is:reply";
        var max = Math.Clamp(request.MaxResults, 10, 100);
        var url = "https://api.twitter.com/2/tweets/search/recent" +
                  $"?query={Uri.EscapeDataString(query)}&max_results={max}&tweet.fields=created_at,lang";
        if (!string.IsNullOrEmpty(request.SinceCursor)) url += $"&since_id={Uri.EscapeDataString(request.SinceCursor)}";

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (HttpRequestException ex) { return SourceFetchResult.Fail(FetchErrorKind.Transient, $"X unreachable: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return SourceFetchResult.Fail(FetchErrorKind.Transient, "X request timed out"); }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                DateTimeOffset? reset = null;
                if (resp.Headers.TryGetValues("x-rate-limit-reset", out var v) && long.TryParse(v.FirstOrDefault(), out var epoch))
                    reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
                return SourceFetchResult.Fail(FetchErrorKind.RateLimited, "X rate limit reached", reset);
            }
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return SourceFetchResult.Fail(FetchErrorKind.Auth,
                    $"X rejected the token ({(int)resp.StatusCode}); check the API tier and permissions");
            if ((int)resp.StatusCode >= 500)
                return SourceFetchResult.Fail(FetchErrorKind.Transient, $"X API error {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                return SourceFetchResult.Fail(FetchErrorKind.Transient, $"X API returned {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var posts = new List<SourcePost>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var t in data.EnumerateArray())
                {
                    var id = t.GetProperty("id").GetString()!;
                    var created = DateTimeOffset.Parse(t.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture);
                    posts.Add(new SourcePost(id, t.GetProperty("text").GetString() ?? "", $"https://x.com/i/web/status/{id}", created));
                }
            }
            string? newest = null;
            if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("newest_id", out var n))
                newest = n.GetString();
            return new SourceFetchResult(posts, NextCursor: newest);
        }
    }
}
