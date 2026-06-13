using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
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
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();

        // Feedback capture and rule extraction.
        services.AddSingleton<IRecallExtractor, RuleBasedRecallExtractor>();
        services.AddScoped<IFeedbackService, FeedbackService>();

        // Retrieval. NullEmbeddingProvider keeps search keyword-only.
        services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
        services.AddScoped<IRecallSearchService, KeywordRecallSearchService>();

        return services;
    }
}
