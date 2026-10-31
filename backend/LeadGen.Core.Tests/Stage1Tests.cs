using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;

namespace LeadGen.Core.Tests;

/// <summary>Stage 1: custom exclusion rules reach the analyzer and shape the pipeline (real Python service).</summary>
public static class Stage1Tests
{
    private static readonly ExclusionRuleInput[] Words =
    {
        Fx.Rule("full-time"), Fx.Rule("employee"), Fx.Rule("job"), Fx.Rule("courses"),
    };

    public static async Task RunAll()
    {
        await T.Run("Stage 1: analyzer contract and the four work-order examples", Examples);
        await T.Run("Stage 1: conflict goes to review, disabled rule is ignored, Arabic", ConflictAndMore);
        await T.Run("Stage 1: pipeline applies rules, records reasons, one lead per post", Pipeline);
    }

    private static async Task Examples()
    {
        var ai = Fx.RealAi();
        var (ok1, _) = await ai.AnalyzeAsync("Looking for a .NET developer for my ERP project", Words, default);
        T.Check("valid request is accepted", ok1.IsLead && !ok1.NeedsReview);

        var (r2, _) = await ai.AnalyzeAsync("Looking for a full-time .NET employee", Words, default);
        T.Check("full-time employee is rejected by a custom rule", !r2.IsLead && r2.ExclusionReason == "custom_rule" && r2.ExclusionSource == "custom");
        T.Check("matched rule is recorded", r2.MatchedRule is { Pattern: "full-time" or "employee" });

        var (r3, _) = await ai.AnalyzeAsync("Need an ERP system, not a job", Words, default);
        T.Check("negated word does not reject a real request", r3.IsLead && !r3.NeedsReview);
        T.Check("the ignored match is recorded as negated context",
            r3.CustomMatches is { Count: 1 } m && m[0].Negated && m[0].Outcome == "ignored_negated_context");

        var (r4, _) = await ai.AnalyzeAsync("Looking for programming courses", Words, default);
        T.Check("courses is rejected", !r4.IsLead && r4.ExclusionReason == "custom_rule");
    }

    private static async Task ConflictAndMore()
    {
        var ai = Fx.RealAi();
        var (c, _) = await ai.AnalyzeAsync("Looking for a company to build our ERP system with support after launch",
            new[] { Fx.Rule("support") }, default);
        T.Check("excluded word inside a clear purchase request is kept for review", c.IsLead && c.NeedsReview);
        T.Check("review reason names the word", c.ReviewReason?.Contains("support") == true);

        var (d, _) = await ai.AnalyzeAsync("Looking for programming courses", new[] { Fx.Rule("courses", active: false) }, default);
        T.Check("disabled rule does not apply (built-in rule still decides)",
            !d.IsLead && d.ExclusionSource == "builtin" && d.MatchedRule is null && d.CustomRulesApplied == 0);

        var (a, _) = await ai.AnalyzeAsync("عايزين شركة تعمل لنا نظام ERP للشركة، مش وظيفة", new[] { Fx.Rule("وظيفة") }, default);
        T.Check("Arabic negation keeps the lead", a.IsLead && !a.NeedsReview);

        var (p, _) = await ai.AnalyzeAsync("مطلوب موظف دوام كامل للعمل بالرياض", new[] { Fx.Rule("دوام كامل", "phrase") }, default);
        T.Check("Arabic phrase rule rejects", !p.IsLead && p.ExclusionSource == "custom");

        var (none, _) = await ai.AnalyzeAsync("We need a software company to build our ERP system for our factory in Riyadh",
            Array.Empty<ExclusionRuleInput>(), default);
        T.Check("no rules: existing behaviour unchanged", none.IsLead && none.ServiceCategory == "ERP_DEVELOPMENT" && none.Geography.Country == "Saudi Arabia");
    }

    private static async Task Pipeline()
    {
        var store = new InMemoryStore();
        var src = new SourceState(Guid.NewGuid(), "x", true, null, null);
        store.Sources.Add(src);
        var profile = Fx.Profile(new[] { Fx.Rule("full-time"), Fx.Rule("employee"), Fx.Rule("job"), Fx.Rule("courses"), Fx.Rule("support") });

        var posts = new List<SourcePost>
        {
            Fx.Post("1", "Looking for a .NET developer for my ERP project"),
            Fx.Post("2", "Looking for a full-time .NET employee"),
            Fx.Post("3", "Need an ERP system, not a job"),
            Fx.Post("4", "Looking for programming courses"),
            Fx.Post("5", "Looking for a company to build our ERP system with support after launch"),
            Fx.Post("6", "looking for a   .NET developer for my ERP project"),   // same text as #1, new id: duplicate
        };
        var connector = new FakeConnector("x", _ => new SourceFetchResult(posts, NextCursor: "6"));
        var runner = new PipelineRunner(store, Fx.RealAi(), new[] { connector }, options: new RunOptions(TimeSpan.Zero, TimeSpan.FromMinutes(15)));

        var res = (await runner.RunProfileAsync(profile, default)).Single();
        T.Eq("run status", SourceRunStatus.Done, res.Status);
        T.Eq("posts saved (duplicate text skipped)", 5, res.NewPosts);
        T.Eq("leads created", 3, res.Leads);
        T.Eq("excluded posts", 2, res.Excluded);
        T.Eq("leads flagged for review", 1, res.NeedsReview);

        var byId = store.Analyses.ToDictionary(a => a.ExternalId);
        T.Check("#2 reason recorded", byId["2"].Result.ExclusionReason == "custom_rule" && byId["2"].Result.MatchedRule != null);
        T.Check("#4 reason recorded", byId["4"].Result.ExclusionReason == "custom_rule");
        T.Check("raw analysis JSON stored for every post", store.Analyses.All(a => a.Raw.Contains("exclusion_reason")));
        T.Check("#3 (negated 'job') became a lead", store.Leads.Any(l => l.ExternalId == "3" && !l.NeedsReview));
        T.Check("#5 lead carries the review flag", store.Leads.Any(l => l.ExternalId == "5" && l.NeedsReview));
        T.Eq("cursor saved", "6", store.Cursors.Values.Single());

        var again = (await runner.RunProfileAsync(profile, default)).Single();
        T.Eq("second run: nothing new processed", 0, again.NewPosts);
        T.Eq("second run: still exactly 3 leads", 3, store.Leads.Count);
        T.Eq("every lead belongs to a unique post", store.Leads.Count, store.Leads.Select(l => l.SignalId).Distinct().Count());
        T.Eq("cursor passed to the connector on the next run", "6", connector.LastRequest?.SinceCursor);
    }
}
