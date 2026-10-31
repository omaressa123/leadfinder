using System.Net;
using System.Text;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;
using LeadGen.Core.Scheduling;

namespace LeadGen.Core.Tests;

public class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

/// <summary>Stage 3: Reddit connector against realistic API responses (no network).</summary>
public static class Stage3Tests
{
    private const long Base = 1793000000;   // fixed created_utc
    private static string Child(string id, string title, string body, long created, string author = "someone", string? removed = null) =>
        "{\"kind\":\"t3\",\"data\":{\"id\":\"" + id + "\",\"name\":\"t3_" + id + "\",\"title\":" + J(title) +
        ",\"selftext\":" + J(body) + ",\"author\":\"" + author + "\",\"created_utc\":" + created + ".0,\"permalink\":\"/r/forhire/comments/" + id +
        "/post/\",\"removed_by_category\":" + (removed is null ? "null" : "\"" + removed + "\"") + "}}";
    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
    private static string Listing(params string[] children) => "{\"kind\":\"Listing\",\"data\":{\"children\":[" + string.Join(",", children) + "]}}";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private const string TokenJson = """{"access_token":"tok-1","token_type":"bearer","expires_in":86400,"scope":"*"}""";

    private static (RedditConnector, StubHandler, FakeClock) Make(Func<HttpRequestMessage, HttpResponseMessage> search, RedditOptions? o = null)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
        var h = new StubHandler(req => req.RequestUri!.Host == "www.reddit.com" ? Json(TokenJson) : search(req));
        var c = new RedditConnector(new HttpClient(h), o ?? new RedditOptions("id", "secret", "web:leadgen-test:1.0 (by /u/test)"),
            new RedditTokenCache(), clock);
        return (c, h, clock);
    }

    private static SourceSearchRequest Req(string? cursor = null) =>
        new(new[] { "need developer", "erp", "build \"app\"" }, new[] { "en" }, 50, cursor);

    public static async Task RunAll()
    {
        await T.Run("Stage 3: Reddit connector (official API)", Connector);
        await T.Run("Stage 3: Reddit errors, permissions and rate limits", Errors);
        await T.Run("Stage 3: Reddit through the same pipeline, analyzer, exclusions and scoring", Pipeline);
    }

    private static async Task Connector()
    {
        var (c, h, _) = Make(_ => Json(Listing(
            Child("a1", "Need a company to build our ERP system", "We are in Riyadh and need a quote.", Base + 30),
            Child("a2", "Old post", "body", Base),
            Child("a3", "Removed", "[removed]", Base + 20),
            Child("a4", "Deleted author", "body", Base + 21, "[deleted]"),
            Child("a5", "Mod removed", "body", Base + 22, "someone", "moderator"))));

        var r = await c.FetchAsync(Req(), default);
        T.Check("success, no error", r.Kind == FetchErrorKind.None && r.Error is null);
        T.Eq("removed/deleted posts are dropped", 2, r.Posts.Count);
        var p = r.Posts.First(x => x.ExternalId == "t3_a1");
        T.Eq("link points at the post", "https://www.reddit.com/r/forhire/comments/a1/post/", p.Url);
        T.Eq("publication date saved", DateTimeOffset.FromUnixTimeSeconds(Base + 30), p.PublishedAt);
        T.Check("content is title plus body", p.Content.StartsWith("Need a company to build our ERP system") && p.Content.Contains("quote"));
        T.Eq("cursor is the newest created_utc", (Base + 30).ToString(), r.NextCursor);

        var tokenReq = h.Requests.First(x => x.RequestUri!.Host == "www.reddit.com");
        T.Check("token request uses Basic auth with the app credentials", tokenReq.Headers.Authorization?.Scheme == "Basic");
        var search = h.Requests.Last();
        T.Eq("search goes to the official oauth host", "oauth.reddit.com", search.RequestUri!.Host);
        T.Eq("bearer token is sent", "tok-1", search.Headers.Authorization?.Parameter);
        T.Check("descriptive User-Agent is sent", search.Headers.UserAgent.ToString().Contains("leadgen-test"));
        var q = Uri.UnescapeDataString(search.RequestUri!.Query);
        T.Check("phrases are quoted and OR-ed", q.Contains("\"need developer\" OR erp OR \"build app\""), q);
        T.Check("sorted by newest", q.Contains("sort=new"));

        var r2 = await c.FetchAsync(Req((Base + 30).ToString()), default);
        T.Eq("second call with cursor drops posts already seen", 0, r2.Posts.Count);
        T.Eq("token is reused, not re-requested", 1, h.Requests.Count(x => x.RequestUri!.Host == "www.reddit.com"));

        var (c2, _, _) = Make(_ => Json(Listing()));
        var empty = await c2.FetchAsync(Req("123"), default);
        T.Check("empty result keeps the old cursor", empty.Posts.Count == 0 && empty.NextCursor == "123");
    }

    private static async Task Errors()
    {
        var none = new RedditConnector(new HttpClient(new StubHandler(_ => throw new Exception("must not be called"))),
            new RedditOptions(null, null));
        var nc = await none.FetchAsync(Req(), default);
        T.Check("no credentials: NotConfigured and no network call", nc.Kind == FetchErrorKind.NotConfigured);

        var badCreds = new RedditConnector(new HttpClient(new StubHandler(_ => Json("{}", HttpStatusCode.Unauthorized))),
            new RedditOptions("id", "wrong"), new RedditTokenCache());
        T.Eq("bad client secret: Auth (not retried)", FetchErrorKind.Auth, (await badCreds.FetchAsync(Req(), default)).Kind);

        var (forbidden, _, _) = Make(_ => Json("{}", HttpStatusCode.Forbidden));
        var f = await forbidden.FetchAsync(Req(), default);
        T.Check("403: Auth with a permissions message", f.Kind == FetchErrorKind.Auth && f.Error!.Contains("permission"));

        var (limited, _, clock) = Make(_ => Json("{}", HttpStatusCode.TooManyRequests, ("x-ratelimit-reset", "120")));
        var l = await limited.FetchAsync(Req(), default);
        T.Eq("429: RateLimited", FetchErrorKind.RateLimited, l.Kind);
        T.Eq("429: reset time taken from the header", clock.GetUtcNow().AddSeconds(120), l.RateLimitResetsAt);

        var (retryAfter, _, clock2) = Make(_ =>
        {
            var r = Json("{}", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        });
        T.Eq("429 with Retry-After is honoured", clock2.GetUtcNow().AddSeconds(30), (await retryAfter.FetchAsync(Req(), default)).RateLimitResetsAt);

        var (exhausted, _, clock3) = Make(_ => Json(Listing(Child("b1", "Need an ERP developer", "x", Base + 5)),
            HttpStatusCode.OK, ("x-ratelimit-remaining", "0.0"), ("x-ratelimit-reset", "45")));
        var ex = await exhausted.FetchAsync(Req(), default);
        T.Check("quota used up: posts kept and source parked until reset",
            ex.Kind == FetchErrorKind.None && ex.Posts.Count == 1 && ex.RateLimitResetsAt == clock3.GetUtcNow().AddSeconds(45));

        var (server, _, _) = Make(_ => Json("oops", HttpStatusCode.BadGateway));
        T.Eq("5xx: Transient (retried with backoff)", FetchErrorKind.Transient, (await server.FetchAsync(Req(), default)).Kind);

        var (garbage, _, _) = Make(_ => Json("<html>", HttpStatusCode.OK));
        T.Eq("unreadable body: Transient, no crash", FetchErrorKind.Transient, (await garbage.FetchAsync(Req(), default)).Kind);

        var down = new RedditConnector(new HttpClient(new StubHandler(_ => throw new HttpRequestException("dns"))),
            new RedditOptions("id", "secret"), new RedditTokenCache());
        T.Eq("network down: Transient", FetchErrorKind.Transient, (await down.FetchAsync(Req(), default)).Kind);

        // An expired token mid-life: refreshed once, then it works.
        int tokens = 0, searches = 0;
        var h = new StubHandler(req =>
        {
            if (req.RequestUri!.Host == "www.reddit.com") { tokens++; return Json("{\"access_token\":\"t" + tokens + "\",\"expires_in\":86400}"); }
            searches++;
            return req.Headers.Authorization!.Parameter == "t1" ? Json("{}", HttpStatusCode.Unauthorized) : Json(Listing());
        });
        var refreshing = new RedditConnector(new HttpClient(h), new RedditOptions("id", "secret"), new RedditTokenCache());
        var rr = await refreshing.FetchAsync(Req(), default);
        T.Check("401 on search: token refreshed once and the call succeeds", rr.Kind == FetchErrorKind.None && tokens == 2 && searches == 2);
    }

    private static async Task Pipeline()
    {
        // Real analyzer + real pipeline + Reddit connector fed with Reddit-shaped JSON.
        var rig0 = new Rig();
        var (reddit, _, clock) = Make(_ => Json(Listing(
            Child("p1", "Looking for a company to build our ERP system", "Based in Riyadh, budget is ready.", Base + 10),
            Child("p2", "Looking for a full-time .NET employee", "Join our team in Cairo", Base + 11),
            Child("p3", "Looking for programming courses", "Where can I learn?", Base + 12),
            Child("p4", "Need an ERP system, not a job", "Our shop in Alexandria needs software.", Base + 13))));
        var rig = Rig.Create(new ILeadSourceConnector[] { reddit });
        rig.Clock.Set(DateTimeOffset.FromUnixTimeSeconds(Base + 100));
        _ = clock; _ = rig0;

        var run = await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        var src = run.Sources.Single();
        T.Eq("reddit source finished", SourceRunStatus.Done, src.Status);
        T.Eq("all 4 posts analysed", 4, src.NewPosts);
        var leadIds = rig.Pipe.Leads.Select(l => l.ExternalId).OrderBy(x => x).ToList();
        T.Check("valid purchase requests become leads", leadIds.SequenceEqual(new[] { "t3_p1", "t3_p4" }), string.Join(",", leadIds));
        T.Eq("employee post and course post are excluded", 2, src.Excluded);
        T.Check("source and link saved with the signal",
            rig.Pipe.Signals[(rig.SourceId("reddit"), "t3_p1")].Url.StartsWith("https://www.reddit.com/r/forhire/"));

        await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("second run: same posts produce no extra leads", 2, rig.Pipe.Leads.Count);
        T.Eq("second run: nothing re-analysed", 4, rig.Ai.Calls);
    }
}
