using System.Net;
using System.Text;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;
using LeadGen.Core.Scheduling;

namespace LeadGen.Core.Tests;

/// <summary>
/// Stage 4: the whole pipeline, as the scheduler runs it: collection from X and Reddit (real connector classes
/// over stubbed HTTP) -> dedupe -> real Python analyzer with custom exclusions -> scoring -> Lead.
/// </summary>
public static class Stage4Tests
{
    private const long T0 = 1793000000;

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string XTweet(string id, string text, long at) =>
        "{\"id\":\"" + id + "\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) +
        ",\"created_at\":\"" + DateTimeOffset.FromUnixTimeSeconds(at).ToString("yyyy-MM-ddTHH:mm:ss.000Z") + "\"}";

    private static string RedditChild(string id, string title, string body, long at) =>
        "{\"kind\":\"t3\",\"data\":{\"id\":\"" + id + "\",\"name\":\"t3_" + id + "\",\"title\":" + System.Text.Json.JsonSerializer.Serialize(title) +
        ",\"selftext\":" + System.Text.Json.JsonSerializer.Serialize(body) + ",\"author\":\"u\",\"created_utc\":" + at +
        ".0,\"permalink\":\"/r/x/comments/" + id + "/p/\"}}";

    public static async Task RunAll()
    {
        await T.Run("Stage 4: full pipeline, two sources, scheduled, one lead per post", Full);
        await T.Run("Stage 4: a source failure does not stop the rest of the system", Failure);
    }

    private static ILeadSourceConnector X(Func<HttpResponseMessage> respond) =>
        new XConnector(new HttpClient(new StubHandler(_ => respond())), new XOptions("bearer"));

    private static ILeadSourceConnector Reddit(Func<HttpRequestMessage, HttpResponseMessage> search) =>
        new RedditConnector(new HttpClient(new StubHandler(r => r.RequestUri!.Host == "www.reddit.com"
            ? Json("{\"access_token\":\"t\",\"expires_in\":3600}") : search(r))), new RedditOptions("id", "secret"), new RedditTokenCache());

    private static async Task Full()
    {
        var ads = "Join our team! We are hiring a full-time developer in Cairo";
        var xData = "{\"data\":[" + string.Join(",",
            XTweet("1001", "Looking for a company to build our ERP system in Riyadh", T0 + 10),
            XTweet("1002", "Need a software company to develop an online store for our shop in Alexandria", T0 + 20),
            XTweet("1003", ads, T0 + 30),
            XTweet("1004", "Looking for a developer to build a mobile app for our clinic in Jeddah. Not interested in courses", T0 + 40)) +
            "],\"meta\":{\"newest_id\":\"1004\"}}";
        var redditData = "{\"data\":{\"children\":[" + string.Join(",",
            RedditChild("r1", "Need a company to build our ERP system", "Based in Dubai, budget ready", T0 + 15),
            RedditChild("r2", "Looking for programming courses", "where to learn", T0 + 25),
            // same text as X tweet 1001, different source: still one lead per post, per source
            RedditChild("r3", "Looking for a company to build our ERP system in Riyadh", "", T0 + 35)) + "]}}";

        var rig = Rig.Create(new[] { X(() => Json(xData)), Reddit(_ => Json(redditData)) });
        rig.Clock.Set(DateTimeOffset.FromUnixTimeSeconds(T0 + 100));
        // A custom rule that must exclude the hiring post only, not the "not interested in courses" purchase request.
        var profile = Fx.Profile(new[] { Fx.Rule("hiring"), Fx.Rule("courses") });
        rig.Pipe.Profile = profile;
        rig.Sched.Rows.Clear();
        rig.Pipe.Profiles[profile.Id] = profile;
        rig.AddSchedule(profile.Id, ScheduleSettings.EverySixHours());
        rig.Clock.Advance(TimeSpan.FromHours(6));

        var tick = await rig.Scheduler.TickAsync(default);
        var run = tick.Single().Run!;
        T.Check("scheduled run completed ok", run.Outcome == CoordinatedOutcome.Completed && run.Status == "ok", run.Status);
        T.Eq("both sources ran", 2, run.Sources.Count(s => s.Status == SourceRunStatus.Done));

        var xLeads = rig.Pipe.Leads.Where(l => l.ExternalId is "1001" or "1002" or "1003" or "1004").Select(l => l.ExternalId).OrderBy(x => x).ToList();
        T.Check("X: purchase requests became leads, hiring post did not",
            xLeads.Contains("1001") && xLeads.Contains("1002") && !xLeads.Contains("1003"), string.Join(",", xLeads));
        T.Check("X: 'not interested in courses' purchase request is not rejected by the 'courses' rule (context)",
            xLeads.Contains("1004"));
        var rLeads = rig.Pipe.Leads.Where(l => l.ExternalId.StartsWith("t3_")).Select(l => l.ExternalId).OrderBy(x => x).ToList();
        T.Check("Reddit: ERP requests became leads, courses post did not",
            rLeads.SequenceEqual(new[] { "t3_r1", "t3_r3" }), string.Join(",", rLeads));
        T.Eq("every lead belongs to exactly one signal", rig.Pipe.Leads.Count, rig.Pipe.Leads.Select(l => l.SignalId).Distinct().Count());
        T.Check("leads carry a score and priority", rig.Pipe.Leads.All(l => l.Score >= profile.MinScore && l.Priority.Length > 0));
        T.Check("leads from both sources are in the same lead list",
            rig.Pipe.Leads.Select(l => l.SourceId).Distinct().Count() == 2);
        var courses = rig.Pipe.Analyses.Where(a => a.ExternalId == "t3_r2").Single();
        T.Check("the exclusion reason is recorded for the rejected post",
            courses.Result.ExclusionSource is "custom" or "builtin" && !string.IsNullOrEmpty(courses.Result.ExclusionReason));
        var hiring = rig.Pipe.Analyses.Single(a => a.ExternalId == "1003");
        T.Eq("hiring post rejected by the custom rule", "custom", hiring.Result.ExclusionSource);

        int leadsBefore = rig.Pipe.Leads.Count, analysed = rig.Ai.Calls;
        rig.Clock.Advance(TimeSpan.FromHours(6));
        await rig.Scheduler.TickAsync(default);
        T.Eq("next scheduled run: no duplicate leads", leadsBefore, rig.Pipe.Leads.Count);
        T.Eq("next scheduled run: nothing analysed twice", analysed, rig.Ai.Calls);
        await rig.Coordinator.RunAsync(profile.Id, RunTrigger.Manual, default);
        T.Eq("manual run right after: still no duplicates", leadsBefore, rig.Pipe.Leads.Count);
    }

    private static async Task Failure()
    {
        var redditData = "{\"data\":{\"children\":[" +
            RedditChild("r1", "Need a company to build our ERP system", "Based in Dubai, budget ready", T0 + 15) + "]}}";

        // X is rate limited, Reddit healthy.
        var rig = Rig.Create(new[]
        {
            X(() => { var r = Json("{}", HttpStatusCode.TooManyRequests); r.Headers.TryAddWithoutValidation("x-rate-limit-reset", "1793009999"); return r; }),
            Reddit(_ => Json(redditData))
        });
        rig.Clock.Set(DateTimeOffset.FromUnixTimeSeconds(T0 + 100));
        rig.Sched.Rows[rig.Profile.Id].NextRunAt = rig.Clock.GetUtcNow();
        var run = (await rig.Scheduler.TickAsync(default)).Single().Run!;
        T.Check("X failure is reported, Reddit still delivers its lead",
            run.Sources.Single(s => s.SourceType == "x").Status == SourceRunStatus.Failed &&
            run.Sources.Single(s => s.SourceType == "reddit").Leads == 1);
        T.Eq("the lead is on the dashboard", 1, rig.Pipe.Leads.Count);
        T.Eq("overall status partial", "partial", run.Status);

        // Reddit credentials rejected + X works: not retried forever, X still works.
        var rig2 = Rig.Create(new[]
        {
            X(() => Json("{\"data\":[" + XTweet("2001", "Looking for a company to build our ERP system in Riyadh", T0 + 10) + "],\"meta\":{\"newest_id\":\"2001\"}}")),
            Reddit(_ => Json("{}", HttpStatusCode.Forbidden))
        });
        rig2.Clock.Set(DateTimeOffset.FromUnixTimeSeconds(T0 + 100));
        var run2 = await rig2.Coordinator.RunAsync(rig2.Profile.Id, RunTrigger.Manual, default);
        T.Check("Reddit permission error reported, X lead still saved",
            run2.Sources.Single(s => s.SourceType == "reddit").Status == SourceRunStatus.Failed && rig2.Pipe.Leads.Count == 1);
    }
}
