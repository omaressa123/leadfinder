namespace LeadGen.Api.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "owner";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Service
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

public class SearchProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string[] Countries { get; set; } = Array.Empty<string>();
    public string[] Cities { get; set; } = Array.Empty<string>();
    public string[] Languages { get; set; } = { "ar", "en" };
    public int MinScore { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Scheduling (see LeadGen.Core.Scheduling). Mode: off | interval | daily.
    public string ScheduleMode { get; set; } = "off";
    public int ScheduleIntervalMinutes { get; set; } = 360;
    public TimeOnly ScheduleDailyTime { get; set; } = new(9, 0);
    public string ScheduleTimeZone { get; set; } = "UTC";
    public bool SchedulePaused { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunError { get; set; }
    public int RetryAttempts { get; set; }
    public DateTimeOffset? RunningSince { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? RunToken { get; set; }

    public List<SearchKeyword> Keywords { get; set; } = new();
    public List<ExclusionRule> Exclusions { get; set; } = new();
}

public class SearchKeyword
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string Keyword { get; set; } = "";
    public string MatchType { get; set; } = "phrase";
}

/// <summary>A custom exclusion word/phrase. Sent to the analyzer with every post; see the analyzer for context handling.</summary>
public class ExclusionRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string Pattern { get; set; } = "";
    public string MatchType { get; set; } = "phrase";   // phrase | keyword
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceType { get; set; } = "";
    public string Status { get; set; } = "disabled";
    public string? LastError { get; set; }
    public DateTimeOffset? RateLimitedUntil { get; set; }
    public DateTimeOffset? LastFetchAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per profile + source: newest post seen, so the next run does not fetch old posts again.</summary>
public class CollectionCursor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public Guid SourceId { get; set; }
    public string Cursor { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CollectionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Guid? ProfileId { get; set; }
    public string Status { get; set; } = "queued";
    public int Fetched { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public class RawSignal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public string ExternalId { get; set; } = "";
    public string Content { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ContentHash { get; set; } = "";
}

public class AiAnalysisResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SignalId { get; set; }
    public string Intent { get; set; } = "";
    public string? Category { get; set; }
    public decimal Confidence { get; set; }
    public string JsonResult { get; set; } = "{}";
    public string ModelVersion { get; set; } = "";
    public string RulesVersion { get; set; } = "";
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Lead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SignalId { get; set; }
    public Guid? ServiceId { get; set; }
    public string? Category { get; set; }
    public int Score { get; set; }
    public string Priority { get; set; } = "low";
    public string Status { get; set; } = "new";
    public bool NeedsReview { get; set; }
    public string? ReviewReason { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class LeadScoreFactor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public string Factor { get; set; } = "";
    public int Score { get; set; }
    public int Weight { get; set; }
}

public class LeadActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public string ActivityType { get; set; } = "";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class LeadStatus
{
    public static readonly string[] All =
        { "new", "reviewed", "contacted", "responded", "negotiation", "converted", "rejected" };
}
