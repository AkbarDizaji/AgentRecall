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
            entity.Property(e => e.Trigger).IsRequired();
            entity.Property(e => e.Priority).HasDefaultValue(0);
            entity.Property(e => e.Deprecated).HasDefaultValue(false);
            entity.HasIndex(e => new { e.ScopeLevel, e.ScopeValue });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Deprecated);
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
    }
}
