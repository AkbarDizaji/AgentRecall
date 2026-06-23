using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Compression;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Extraction;
using AgentRecall.Core.Memory;
using AgentRecall.Core.Mining;
using AgentRecall.Core.Outcomes;
using AgentRecall.Core.Policy;
using AgentRecall.Core.Reporting;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.Embeddings;
using AgentRecall.Infrastructure.Persistence;
using AgentRecall.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AgentRecall SQLite context, repositories, and database
    /// initializer. Assumes <see cref="AgentRecallOptions"/> is already registered.
    /// </summary>
    public static IServiceCollection AddAgentRecallPersistence(this IServiceCollection services)
    {
        services.AddDbContext<AgentRecallDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<AgentRecallOptions>();
            builder.UseSqlite($"Data Source={options.DatabasePath}");
        });

        services.AddScoped<IRecallRuleRepository, RecallRuleRepository>();
        services.AddScoped<IRecallEventRepository, RecallEventRepository>();
        services.AddScoped<IRecallScopeRepository, RecallScopeRepository>();
        services.AddScoped<IRetrievalRecordRepository, RetrievalRecordRepository>();
        services.AddScoped<IRuleOutcomeRepository, RuleOutcomeRepository>();
        services.AddScoped<ILessonCandidateRepository, LessonCandidateRepository>();
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();

        // Feedback capture and rule extraction.
        services.AddSingleton<IRecallRuleQualityValidator, RecallRuleQualityValidator>();
        services.AddSingleton<IRecallExtractor, RuleBasedRecallExtractor>();
        // "Lessons, not facts": screen candidates before storing them as rules.
        services.AddSingleton<IMemoryWorthinessClassifier, MemoryWorthinessClassifier>();
        services.AddScoped<IFeedbackService, FeedbackService>();

        // Retrieval. NullEmbeddingProvider keeps search keyword-only.
        services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
        services.AddScoped<IRecallSearchService, KeywordRecallSearchService>();

        // Lifecycle, versioning, and failure ingestion.
        services.AddScoped<IRuleLifecycleService, RuleLifecycleService>();
        services.AddScoped<ILogImportService, LogImportService>();
        services.AddScoped<IPullRequestImportService, PullRequestImportService>();

        // Conflict resolution across matching rules.
        services.AddScoped<IPolicyEngine, PolicyEngine>();

        // Smart context injection: relevance-ranked rule retrieval for a task.
        services.AddSingleton<IConceptExpander, DomainConceptExpander>();
        services.AddScoped<IContextInjectionService, ContextInjectionService>();

        // Memory compression: dedupe and distil rules into canonical guidance.
        services.AddSingleton<ICanonicalRuleGenerator, DeterministicCanonicalRuleGenerator>();
        services.AddScoped<IMemoryCompressionService, MemoryCompressionService>();

        // Proactive memory helpers.
        services.AddSingleton<IFeedbackCandidateAnalyzer, FeedbackCandidateAnalyzer>();

        // Conflict detection and explainable, deterministic resolution.
        services.AddSingleton<IRuleConflictDetector, RuleConflictDetector>();
        services.AddSingleton<IRuleResolutionService, RuleResolutionService>();

        // Outcome-based learning: move rule confidence on real evidence.
        services.AddScoped<IOutcomeTrackingService, OutcomeTrackingService>();

        // Lesson mining: propose new lesson candidates from repeated historical signals.
        services.AddScoped<ILessonMiningService, LessonMiningService>();

        // Learning reports: local-only analytics over rules and the event ledger.
        services.AddScoped<ILearningReportService, LearningReportService>();

        return services;
    }
}
