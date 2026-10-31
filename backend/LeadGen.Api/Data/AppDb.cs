using LeadGen.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeadGen.Api.Data;

// Table/column names are snake_case via EFCore.NamingConventions and match db/init.sql.
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<SearchProfile> SearchProfiles => Set<SearchProfile>();
    public DbSet<SearchKeyword> SearchKeywords => Set<SearchKeyword>();
    public DbSet<ExclusionRule> ExclusionRules => Set<ExclusionRule>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<CollectionCursor> CollectionCursors => Set<CollectionCursor>();
    public DbSet<CollectionJob> CollectionJobs => Set<CollectionJob>();
    public DbSet<RawSignal> RawSignals => Set<RawSignal>();
    public DbSet<AiAnalysisResult> AiAnalysisResults => Set<AiAnalysisResult>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadScoreFactor> LeadScoreFactors => Set<LeadScoreFactor>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<SearchProfile>().HasMany(p => p.Keywords).WithOne().HasForeignKey(k => k.ProfileId);
        b.Entity<SearchProfile>().HasMany(p => p.Exclusions).WithOne().HasForeignKey(k => k.ProfileId);
        b.Entity<RawSignal>().HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
        b.Entity<AiAnalysisResult>().Property(x => x.JsonResult).HasColumnType("jsonb");
        b.Entity<Lead>().HasIndex(x => x.SignalId).IsUnique();
        b.Entity<CollectionCursor>().HasIndex(x => new { x.ProfileId, x.SourceId }).IsUnique();
    }
}
