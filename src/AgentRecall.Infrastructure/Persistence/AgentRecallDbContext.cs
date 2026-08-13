using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the local AgentRecall SQLite database.
/// </summary>
public sealed class AgentRecallDbContext : DbContext
{
    public AgentRecallDbContext(DbContextOptions<AgentRecallDbContext> options)
        : base(options)
    {
    }

    public DbSet<RecallRule> Rules => Set<RecallRule>();
    public DbSet<RecallEvent> Events => Set<RecallEvent>();
    public DbSet<RecallScope> Scopes => Set<RecallScope>();
    public DbSet<RetrievalRecord> Retrievals => Set<RetrievalRecord>();
    public DbSet<RuleOutcome> Outcomes => Set<RuleOutcome>();
    public DbSet<LessonCandidate> LessonCandidates => Set<LessonCandidate>();
    public DbSet<RuleLifecycleRecommendation> Recommendations => Set<RuleLifecycleRecommendation>();
    public DbSet<TurnFinalization> TurnFinalizations => Set<TurnFinalization>();
    public DbSet<TurnJudgmentRequest> TurnJudgmentRequests => Set<TurnJudgmentRequest>();
    public DbSet<AgentRecallActivity> Activities => Set<AgentRecallActivity>();
    public DbSet<CareerImpactCandidate> CareerImpactCandidates => Set<CareerImpactCandidate>();
    public DbSet<DocOpportunityCandidate> DocOpportunityCandidates => Set<DocOpportunityCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecallRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.ScopeLevel).HasConversion<string>();
            // Stored as a string with a default so the additive schema reconciler
            // can backfill the column on databases created before categories existed.
            entity.Property(e => e.Category).HasConversion<string>().HasDefaultValue(RuleCategory.Unknown);
            // Outcome-aware capture metadata. Stored as a string with a default so the
            // additive schema reconciler can backfill the columns on older databases.
            entity.Property(e => e.CaptureReason).HasConversion<string>().HasDefaultValue(Core.Capture.CaptureReason.None);
            entity.Property(e => e.EvidenceSummary).HasDefaultValue(string.Empty);
            // Seed provenance. Stored as strings with defaults so the additive schema
            // reconciler can backfill the columns on databases created before seed packs.
            entity.Property(e => e.Source).HasConversion<string>().HasDefaultValue(RuleSource.Learned);
            entity.Property(e => e.SeedPack).HasDefaultValue(string.Empty);
            entity.Property(e => e.SeedRuleKey).HasDefaultValue(string.Empty);
            entity.Property(e => e.Trigger).IsRequired();
            entity.Property(e => e.Priority).HasDefaultValue(0);
            entity.Property(e => e.Deprecated).HasDefaultValue(false);
            // Universal-constraint delivery flag. Stored with a default so the additive schema
            // reconciler can backfill the column on databases created before always-apply rules.
            entity.Property(e => e.AlwaysApply).HasDefaultValue(false);
            // Chat/session correlation for the capture-approval gate. Stored with a default so
            // the additive schema reconciler can backfill the column on older databases.
            entity.Property(e => e.SessionId).HasDefaultValue(string.Empty);
            entity.HasIndex(e => new { e.ScopeLevel, e.ScopeValue });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Deprecated);
            entity.HasIndex(e => e.SessionId);
            // Non-unique: every non-seed rule shares the empty (pack, key) pair, so this
            // is a lookup index for idempotent seed installs, not a uniqueness constraint.
            entity.HasIndex(e => new { e.SeedPack, e.SeedRuleKey });
        });

        modelBuilder.Entity<RecallEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<RecallScope>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Level).HasConversion<string>();
            entity.HasIndex(e => new { e.Level, e.Value }).IsUnique();
        });

        modelBuilder.Entity<RetrievalRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RetrievalId).IsRequired();
            entity.HasIndex(e => e.RetrievalId).IsUnique();
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<RuleOutcome>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.RetrievalId);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<LessonCandidate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Category).HasConversion<string>().HasDefaultValue(RuleCategory.Unknown);
            entity.Property(e => e.CaptureReason).HasConversion<string>().HasDefaultValue(Core.Capture.CaptureReason.None);
            entity.Property(e => e.NormalizedKey).IsRequired();
            entity.HasIndex(e => e.NormalizedKey);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<RuleLifecycleRecommendation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RecommendationType).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Signature).IsRequired();
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.Signature);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TurnFinalization>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Stored with a default so the additive reconciler can backfill the column on
            // databases created before turn correlation existed.
            entity.Property(e => e.TurnId).HasDefaultValue(string.Empty);
            // Judge decision metadata; defaulted so the additive reconciler backfills these
            // columns on databases created before the semantic capture judge existed.
            entity.Property(e => e.DecisionSource).HasDefaultValue(string.Empty);
            entity.Property(e => e.JudgeDecision).HasDefaultValue(string.Empty);
            entity.Property(e => e.JudgeCaptureReason).HasDefaultValue(string.Empty);
            entity.Property(e => e.JudgeConfidence).HasDefaultValue(0d);
            entity.Property(e => e.SessionId).HasDefaultValue(string.Empty);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.RawHash);
            entity.HasIndex(e => e.Cwd);
            entity.HasIndex(e => e.TurnId);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<TurnJudgmentRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(JudgmentRequestStatus.Outstanding);
            entity.Property(e => e.ScopeLevel).HasConversion<string>().HasDefaultValue(ScopeLevel.Global);
            entity.Property(e => e.ResolvedDecision).HasDefaultValue(string.Empty);
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TurnId);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Cwd);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<AgentRecallActivity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActivityType).HasConversion<string>();
            entity.Property(e => e.NoticeLevel).HasConversion<string>();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ActivityType);
            entity.HasIndex(e => e.OperationHash);
            entity.HasIndex(e => e.TurnId);
        });

        modelBuilder.Entity<CareerImpactCandidate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(CareerImpactStatus.Open);
            entity.Property(e => e.TurnId).HasDefaultValue(string.Empty);
            entity.Property(e => e.OperationHash).HasDefaultValue(string.Empty);
            entity.Property(e => e.Source).HasDefaultValue("CareerImpactDetector");
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TurnId);
            entity.HasIndex(e => e.OperationHash);
        });

        modelBuilder.Entity<DocOpportunityCandidate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasDefaultValue(DocOpportunityStatus.Open);
            entity.Property(e => e.DocumentType).HasConversion<string>().HasDefaultValue(DocumentType.Incident);
            entity.Property(e => e.TurnId).HasDefaultValue(string.Empty);
            entity.Property(e => e.OperationHash).HasDefaultValue(string.Empty);
            entity.Property(e => e.Source).HasDefaultValue("HostSuppliedDocOpportunityJudge");
            entity.Property(e => e.WrittenPath).HasDefaultValue(string.Empty);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TurnId);
            entity.HasIndex(e => e.OperationHash);
        });
    }
}
