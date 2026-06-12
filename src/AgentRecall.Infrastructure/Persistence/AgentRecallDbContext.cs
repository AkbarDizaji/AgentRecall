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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecallRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.ScopeLevel).HasConversion<string>();
            entity.Property(e => e.Trigger).IsRequired();
            entity.HasIndex(e => new { e.ScopeLevel, e.ScopeValue });
            entity.HasIndex(e => e.Status);
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
    }
}
