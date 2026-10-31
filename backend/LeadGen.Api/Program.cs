using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LeadGen.Api.Data;
using LeadGen.Api.Domain;
using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;
using LeadGen.Core.Scheduling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")).UseSnakeCaseNamingConvention());

builder.Services.AddHttpClient<IAiClient, GeminiClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton(new GeminiOptions(
    builder.Configuration["Gemini:ApiKey"],
    builder.Configuration["Gemini:Model"] ?? "gemini-2.5-flash-lite"));
builder.Services.AddSingleton(new XOptions(builder.Configuration["X:BearerToken"]));
builder.Services.AddHttpClient<ILeadSourceConnector, XConnector>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(RunOptions.Default);
builder.Services.AddScoped<IPipelineStore, EfPipelineStore>();
builder.Services.AddScoped<PipelineRunner>();
builder.Services.AddSingleton(SchedulerOptions.Default);
builder.Services.AddScoped<IScheduleStore, EfScheduleStore>();
builder.Services.AddScoped<ProfileRunCoordinator>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddHostedService<LeadGen.Api.SchedulerWorker>();

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
        ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration["Cors:Origin"] ?? "http://localhost:3000")
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Seed a single owner account from config (first run only).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var email = app.Configuration["Seed:Email"];
    var pwd = app.Configuration["Seed:Password"];
    if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(pwd) && !await db.Users.AnyAsync())
    {
        var u = new User { Name = "Owner", Email = email };
        u.PasswordHash = new PasswordHasher<User>().HashPassword(u, pwd);
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }
}

Guid CurrentUser(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

static bool ValidRule(ExclusionDto d) =>
    !string.IsNullOrWhiteSpace(d.Pattern) && d.Pattern.Trim().Length <= 100 && d.MatchType is "phrase" or "keyword";

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- auth ----
app.MapPost("/api/auth/login", async (LoginDto dto, AppDb db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
    if (user is null ||
        new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, dto.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();

    var token = new JwtSecurityToken(
        claims: new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name) },
        expires: DateTime.UtcNow.AddHours(12),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), name = user.Name });
});

var api = app.MapGroup("/api").RequireAuthorization();

// ---- services ----
api.MapGet("/services", async (ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    return await db.Services.Where(s => s.UserId == uid).ToListAsync();
});
api.MapPost("/services", async (ServiceDto dto, ClaimsPrincipal p, AppDb db) =>
{
    var s = new Service { UserId = CurrentUser(p), Name = dto.Name, Category = dto.Category };
    db.Services.Add(s);
    await db.SaveChangesAsync();
    return Results.Created($"/api/services/{s.Id}", s);
});
api.MapDelete("/services/{id:guid}", async (Guid id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    var s = await db.Services.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (s is null) return Results.NotFound();
    db.Services.Remove(s);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---- search profiles ----
api.MapGet("/search-profiles", async (ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    return await db.SearchProfiles.Include(x => x.Keywords).Include(x => x.Exclusions)
        .Where(x => x.UserId == uid).ToListAsync();
});

api.MapPost("/search-profiles", async (ProfileDto dto, ClaimsPrincipal p, AppDb db) =>
{
    var profile = new SearchProfile
    {
        UserId = CurrentUser(p), Name = dto.Name, Countries = dto.Countries, Cities = dto.Cities,
        Languages = dto.Languages, MinScore = dto.MinScore,
        Keywords = dto.Keywords.Select(k => new SearchKeyword { Keyword = k }).ToList()
    };
    db.SearchProfiles.Add(profile);
    await db.SaveChangesAsync();
    return Results.Created($"/api/search-profiles/{profile.Id}", profile);
});

// Exclusion rules are managed through their own endpoints below, so saving a profile never touches them.
api.MapPut("/search-profiles/{id:guid}", async (Guid id, ProfileDto dto, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    var exists = await db.SearchProfiles.AsNoTracking().AnyAsync(x => x.Id == id && x.UserId == uid);
    if (!exists) return Results.NotFound();

    // Direct SQL update: bypasses EF change tracking, so a scheduler tick rewriting
    // search_profiles between load and save can never raise a concurrency exception.
    await db.Database.ExecuteSqlRawAsync(
        @"UPDATE search_profiles
          SET name = {0}, countries = {1}, cities = {2}, languages = {3}, min_score = {4}
          WHERE id = {5}",
        dto.Name, dto.Countries, dto.Cities, dto.Languages, dto.MinScore, id);

    // Replace keywords wholesale.
    await db.SearchKeywords.Where(k => k.ProfileId == id).ExecuteDeleteAsync();
    foreach (var k in dto.Keywords)
        db.SearchKeywords.Add(new SearchKeyword { Id = Guid.NewGuid(), ProfileId = id, Keyword = k, MatchType = "phrase" });
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id, name = dto.Name, countries = dto.Countries, cities = dto.Cities,
        languages = dto.Languages, min_score = dto.MinScore, keywords = dto.Keywords
    });
});

// ---- custom exclusion rules ----
api.MapGet("/search-profiles/{id:guid}/exclusions", async (Guid id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid)) return Results.NotFound();
    return Results.Ok(await db.ExclusionRules.Where(r => r.ProfileId == id).OrderBy(r => r.CreatedAt).ToListAsync());
});

api.MapPost("/search-profiles/{id:guid}/exclusions", async (Guid id, ExclusionDto dto, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid)) return Results.NotFound();
    if (!ValidRule(dto)) return Results.BadRequest(new { error = "pattern is required (max 100 chars); type must be phrase or keyword" });
    var rule = new ExclusionRule { ProfileId = id, Pattern = dto.Pattern.Trim(), MatchType = dto.MatchType, IsActive = dto.Active };
    db.ExclusionRules.Add(rule);
    db.AuditLogs.Add(new AuditLog { UserId = uid, Action = $"exclusion_add:{id}:{rule.Pattern}" });
    await db.SaveChangesAsync();
    return Results.Created($"/api/search-profiles/{id}/exclusions/{rule.Id}", rule);
});

// Edit, enable and disable all go through PUT.
api.MapPut("/search-profiles/{id:guid}/exclusions/{ruleId:guid}", async (Guid id, Guid ruleId, ExclusionDto dto, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid)) return Results.NotFound();
    var rule = await db.ExclusionRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProfileId == id);
    if (rule is null) return Results.NotFound();
    if (!ValidRule(dto)) return Results.BadRequest(new { error = "pattern is required (max 100 chars); type must be phrase or keyword" });
    rule.Pattern = dto.Pattern.Trim(); rule.MatchType = dto.MatchType; rule.IsActive = dto.Active;
    db.AuditLogs.Add(new AuditLog { UserId = uid, Action = $"exclusion_update:{ruleId}:{(dto.Active ? "on" : "off")}" });
    await db.SaveChangesAsync();
    return Results.Ok(rule);
});

api.MapDelete("/search-profiles/{id:guid}/exclusions/{ruleId:guid}", async (Guid id, Guid ruleId, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid)) return Results.NotFound();
    var rule = await db.ExclusionRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProfileId == id);
    if (rule is null) return Results.NotFound();
    db.ExclusionRules.Remove(rule);
    db.AuditLogs.Add(new AuditLog { UserId = uid, Action = $"exclusion_delete:{ruleId}" });
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Dry run: how would the analyzer treat this sample post with the profile's current active rules?
api.MapPost("/search-profiles/{id:guid}/exclusions/test", async (Guid id, TestDto dto, ClaimsPrincipal p, AppDb db, IAiClient ai, CancellationToken ct) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid, ct)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(dto.Content)) return Results.BadRequest(new { error = "content is required" });
    var rules = await db.ExclusionRules.Where(r => r.ProfileId == id && r.IsActive)
        .Select(r => new ExclusionRuleInput(r.Id.ToString(), r.Pattern, r.MatchType, true)).ToListAsync(ct);
    var (res, _) = await ai.AnalyzeAsync(dto.Content, rules, ct);
    return Results.Ok(new
    {
        res.IsLead, res.NeedsReview, res.ReviewReason, res.ExclusionReason, res.ExclusionSource, res.MatchedRule,
        res.CustomMatches, res.ServiceCategory
    });
});

// Recent posts the analyzer rejected, with the recorded reason, so rules can be tuned.
api.MapGet("/analysis/excluded", async (AppDb db, int limit = 50) =>
{
    var rows = await (from a in db.AiAnalysisResults
                      join s in db.RawSignals on a.SignalId equals s.Id
                      where a.Intent == "excluded"
                      orderby a.AnalyzedAt descending
                      select new { s.Id, s.Content, s.Url, s.PublishedAt, a.AnalyzedAt, a.JsonResult })
        .Take(Math.Clamp(limit, 1, 200)).ToListAsync();

    return Results.Ok(rows.Select(r =>
    {
        using var doc = JsonDocument.Parse(r.JsonResult);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? matched = root.TryGetProperty("matched_rule", out var m) && m.ValueKind == JsonValueKind.Object &&
                          m.TryGetProperty("pattern", out var pt) ? pt.GetString() : null;
        return new { r.Id, r.Content, r.Url, r.PublishedAt, r.AnalyzedAt, reason = Str("exclusion_reason"),
                     source = Str("exclusion_source"), matchedPattern = matched };
    }));
});

api.MapPost("/search-profiles/{id:guid}/run", async (Guid id, ClaimsPrincipal p, ProfileRunCoordinator coordinator, AppDb db, CancellationToken ct) =>
{
    var uid = CurrentUser(p);
    if (!await db.SearchProfiles.AnyAsync(x => x.Id == id && x.UserId == uid, ct)) return Results.NotFound();
    var run = await coordinator.RunAsync(id, RunTrigger.Manual, ct);
    if (run.Outcome == CoordinatedOutcome.AlreadyRunning)
        return Results.Conflict(new { error = "This search is already running. Try again when it finishes." });
    db.AuditLogs.Add(new AuditLog { UserId = uid, Action = $"run_profile:{id}" });
    await db.SaveChangesAsync(ct);
    return Results.Ok(run.Sources);
});

// ---- schedule ----
object ScheduleView(SearchProfile x) => new
{
    mode = x.ScheduleMode, intervalMinutes = x.ScheduleIntervalMinutes,
    dailyTime = x.ScheduleDailyTime.ToString("HH:mm"), timeZone = x.ScheduleTimeZone, paused = x.SchedulePaused,
    nextRunAt = x.NextRunAt, lastRunAt = x.LastRunAt, lastRunStatus = x.LastRunStatus, lastRunError = x.LastRunError,
    running = x.RunningSince != null, retryAttempts = x.RetryAttempts
};

api.MapGet("/search-profiles/{id:guid}/schedule", async (Guid id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    var x = await db.SearchProfiles.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
    return x is null ? Results.NotFound() : Results.Ok(ScheduleView(x));
});

api.MapPut("/search-profiles/{id:guid}/schedule", async (Guid id, ScheduleIn body, ClaimsPrincipal p, AppDb db, TimeProvider clock) =>
{
    var uid = CurrentUser(p);
    var x = await db.SearchProfiles.FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
    if (x is null) return Results.NotFound();
    var mode = (body.Mode ?? "off").ToLowerInvariant() switch { "interval" => ScheduleMode.Interval, "daily" => ScheduleMode.Daily, "off" => ScheduleMode.Off, _ => (ScheduleMode?)null };
    if (mode is null) return Results.BadRequest(new { error = "mode must be off, interval or daily" });
    if (!TimeOnly.TryParse(body.DailyTime ?? "09:00", out var at)) return Results.BadRequest(new { error = "dailyTime must be HH:mm" });
    var settings = new ScheduleSettings(mode.Value, body.IntervalMinutes ?? 360, at, body.TimeZone ?? "UTC", false);
    if (settings.Mode != ScheduleMode.Off && ScheduleCalculator.Validate(settings) is { } err) return Results.BadRequest(new { error = err });
    var (saved, next, attempts) = ScheduleEditor.Save(settings, clock.GetUtcNow());
    ApplySchedule(x, saved, next, attempts);
    await db.SaveChangesAsync();
    return Results.Ok(ScheduleView(x));
});

api.MapPost("/search-profiles/{id:guid}/schedule/pause", async (Guid id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = CurrentUser(p);
    var x = await db.SearchProfiles.FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
    if (x is null) return Results.NotFound();
    var (s, next) = ScheduleEditor.Pause(EfScheduleStore.ToSettings(x));
    ApplySchedule(x, s, next, x.RetryAttempts);
    await db.SaveChangesAsync();
    return Results.Ok(ScheduleView(x));
});

api.MapPost("/search-profiles/{id:guid}/schedule/resume", async (Guid id, ClaimsPrincipal p, AppDb db, TimeProvider clock) =>
{
    var uid = CurrentUser(p);
    var x = await db.SearchProfiles.FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
    if (x is null) return Results.NotFound();
    var (s, next) = ScheduleEditor.Resume(EfScheduleStore.ToSettings(x), clock.GetUtcNow());
    ApplySchedule(x, s, next, 0);
    await db.SaveChangesAsync();
    return Results.Ok(ScheduleView(x));
});

static void ApplySchedule(SearchProfile x, ScheduleSettings s, DateTimeOffset? next, int attempts)
{
    x.ScheduleMode = EfScheduleStore.ModeName(s.Mode);
    x.ScheduleIntervalMinutes = s.IntervalMinutes;
    x.ScheduleDailyTime = s.DailyTime;
    x.ScheduleTimeZone = s.TimeZoneId;
    x.SchedulePaused = s.Paused;
    x.NextRunAt = next;
    x.RetryAttempts = attempts;
}

// ---- sources ----
api.MapGet("/sources", async (AppDb db) => await db.SourceConnections.ToListAsync());
api.MapPost("/sources/{id:guid}/toggle", async (Guid id, AppDb db) =>
{
    var s = await db.SourceConnections.FindAsync(id);
    if (s is null) return Results.NotFound();
    s.Status = s.Status == "enabled" ? "disabled" : "enabled";
    s.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(s);
});
api.MapPost("/sources/{id:guid}/test", async (Guid id, AppDb db, IEnumerable<ILeadSourceConnector> connectors, CancellationToken ct) =>
{
    var s = await db.SourceConnections.FindAsync(id);
    var c = connectors.FirstOrDefault(x => x.SourceName == s?.SourceType);
    if (s is null || c is null) return Results.NotFound();
    var r = await c.FetchAsync(new SourceSearchRequest(new[] { "test" }, new[] { "en" }, 10), ct);
    return Results.Ok(new { ok = r.Kind == FetchErrorKind.None, error = r.Error, sample = r.Posts.Count });
});
api.MapGet("/collection-jobs", async (AppDb db) =>
    await db.CollectionJobs.OrderByDescending(j => j.StartedAt).Take(50).ToListAsync());

// ---- leads ----
api.MapGet("/leads", async (AppDb db, string? status, string? priority, string? category, string? country,
    int? minScore, bool? needsReview, int page = 1, int pageSize = 25) =>
{
    var q = db.Leads.AsQueryable();
    if (!string.IsNullOrEmpty(status)) q = q.Where(l => l.Status == status);
    if (!string.IsNullOrEmpty(priority)) q = q.Where(l => l.Priority == priority);
    if (!string.IsNullOrEmpty(category)) q = q.Where(l => l.Category == category);
    if (!string.IsNullOrEmpty(country)) q = q.Where(l => l.Country == country);
    if (minScore is not null) q = q.Where(l => l.Score >= minScore);
    if (needsReview is not null) q = q.Where(l => l.NeedsReview == needsReview);

    var total = await q.CountAsync();
    var items = await (from l in q
                       join s in db.RawSignals on l.SignalId equals s.Id
                       orderby l.Score descending, s.PublishedAt descending
                       select new { l.Id, l.Score, l.Priority, l.Status, l.Category, l.Country, l.City, l.NeedsReview, l.ReviewReason,
                                    s.Content, s.Url, s.PublishedAt })
        .Skip((Math.Max(page, 1) - 1) * pageSize).Take(Math.Clamp(pageSize, 1, 100)).ToListAsync();
    return Results.Ok(new { total, items });
});

api.MapGet("/leads/{id:guid}", async (Guid id, AppDb db) =>
{
    var lead = await db.Leads.FindAsync(id);
    if (lead is null) return Results.NotFound();
    var signal = await db.RawSignals.FindAsync(lead.SignalId);
    var analysis = await db.AiAnalysisResults.Where(a => a.SignalId == lead.SignalId)
        .OrderByDescending(a => a.AnalyzedAt).FirstOrDefaultAsync();
    var factors = await db.LeadScoreFactors.Where(f => f.LeadId == id).ToListAsync();
    var activities = await db.LeadActivities.Where(a => a.LeadId == id).OrderByDescending(a => a.CreatedAt).ToListAsync();
    return Results.Ok(new { lead, signal, analysis = analysis?.JsonResult, modelVersion = analysis?.ModelVersion, factors, activities });
});

// Status moves are recorded by the owner, never inferred by the model (SRS section 10).
api.MapPatch("/leads/{id:guid}/status", async (Guid id, StatusDto dto, ClaimsPrincipal p, AppDb db) =>
{
    if (!LeadStatus.All.Contains(dto.Status)) return Results.BadRequest(new { error = "invalid status" });
    var lead = await db.Leads.FindAsync(id);
    if (lead is null) return Results.NotFound();
    var old = lead.Status;
    lead.Status = dto.Status;
    lead.UpdatedAt = DateTimeOffset.UtcNow;
    if (dto.Status != "new") lead.NeedsReview = false;   // moving a flagged lead on means the owner has reviewed it
    db.LeadActivities.Add(new LeadActivity { LeadId = id, ActivityType = "status_change", Notes = $"{old} -> {dto.Status}" });
    db.AuditLogs.Add(new AuditLog { UserId = CurrentUser(p), Action = $"lead_status:{id}:{dto.Status}" });
    await db.SaveChangesAsync();
    return Results.Ok(lead);
});

api.MapPost("/leads/{id:guid}/activities", async (Guid id, ActivityDto dto, AppDb db) =>
{
    if (!await db.Leads.AnyAsync(l => l.Id == id)) return Results.NotFound();
    var a = new LeadActivity { LeadId = id, ActivityType = dto.ActivityType, Notes = dto.Notes };
    db.LeadActivities.Add(a);
    await db.SaveChangesAsync();
    return Results.Created($"/api/leads/{id}", a);
});

// ---- reports ----
api.MapGet("/reports/overview", async (AppDb db) => Results.Ok(new
{
    total = await db.Leads.CountAsync(),
    needsReview = await db.Leads.CountAsync(l => l.NeedsReview),
    byPriority = await db.Leads.GroupBy(l => l.Priority).Select(g => new { key = g.Key, count = g.Count() }).ToListAsync(),
    byStatus = await db.Leads.GroupBy(l => l.Status).Select(g => new { key = g.Key, count = g.Count() }).ToListAsync(),
    byCategory = await db.Leads.GroupBy(l => l.Category).Select(g => new { key = g.Key, count = g.Count() }).ToListAsync(),
    byCountry = await db.Leads.GroupBy(l => l.Country).Select(g => new { key = g.Key, count = g.Count() }).ToListAsync()
}));

app.Run();

record LoginDto(string Email, string Password);
record ServiceDto(string Name, string Category);
record ProfileDto(string Name, string[] Countries, string[] Cities, string[] Languages, int MinScore, string[] Keywords);
record StatusDto(string Status);
record ActivityDto(string ActivityType, string? Notes);
record ExclusionDto(string Pattern, string MatchType, bool Active);
record TestDto(string Content);

record ScheduleIn(string? Mode, int? IntervalMinutes, string? DailyTime, string? TimeZone);
