using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace AgentRecall.Infrastructure.Configuration;

/// <summary>
/// Builds configuration from an optional JSON file and environment variables,
/// then binds it to <see cref="AgentRecallOptions"/>.
/// </summary>
public static class ConfigurationLoader
{
    /// <summary>Default config file name looked up in the base directory.</summary>
    public const string DefaultFileName = "agentrecall.json";

    /// <summary>Prefix for environment-variable overrides (e.g. AGENTRECALL__LogLevel).</summary>
    public const string EnvPrefix = "AGENTRECALL_";

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> from the given base path (defaults
    /// to the current directory) plus environment variables.
    /// </summary>
    public static IConfiguration BuildConfiguration(string? basePath = null)
    {
        basePath ??= Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(DefaultFileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: EnvPrefix)
            .Build();
    }

    /// <summary>
    /// Loads and binds <see cref="AgentRecallOptions"/>. Missing values fall back
    /// to the option defaults, so this always returns a usable instance.
    /// </summary>
    public static AgentRecallOptions Load(string? basePath = null) =>
        Bind(BuildConfiguration(basePath));

    /// <summary>
    /// Binds <see cref="AgentRecallOptions"/> from an existing configuration.
    /// </summary>
    public static AgentRecallOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AgentRecallOptions();
        configuration.GetSection(AgentRecallOptions.SectionName).Bind(options);

        // The notice levels are bound as raw strings and parsed defensively. Surface a
        // clear warning when a value is unrecognised; the option still falls back safely.
        WarnIfInvalidNoticeLevel(nameof(AgentRecallOptions.ActivityNoticeLevel), options.ActivityNoticeLevel);
        WarnIfInvalidNoticeLevel(nameof(AgentRecallOptions.HookNoticeLevel), options.HookNoticeLevel);

        if (!Core.Capture.InteractiveMemoryModes.IsValid(options.InteractiveMemoryMode))
        {
            Console.Error.WriteLine(
                $"[agentrecall] warning: invalid {AgentRecallOptions.SectionName}.{nameof(AgentRecallOptions.InteractiveMemoryMode)} " +
                $"'{options.InteractiveMemoryMode}'. Falling back to the default. Valid values: Auto, Ask, Silent.");
        }

        if (!Core.Activity.TurnSummaryLevels.IsValid(options.TurnSummaryLevel))
        {
            Console.Error.WriteLine(
                $"[agentrecall] warning: invalid {AgentRecallOptions.SectionName}.{nameof(AgentRecallOptions.TurnSummaryLevel)} " +
                $"'{options.TurnSummaryLevel}'. Falling back to the default. Valid values: Silent, Compact, Detailed.");
        }

        WarnIfInvalidEnum<Core.Domain.CareerImpactMode>(
            nameof(AgentRecallOptions.CareerImpactMode), options.CareerImpactMode, "Silent, SignificantOnly, Always");
        WarnIfInvalidEnum<Core.Domain.CareerImpactSummaryLevel>(
            nameof(AgentRecallOptions.CareerImpactSummaryLevel), options.CareerImpactSummaryLevel, "Compact, Detailed");

        return options;
    }

    private static void WarnIfInvalidEnum<TEnum>(string key, string? value, string validValues) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) ||
            (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[agentrecall] warning: invalid {AgentRecallOptions.SectionName}.{key} '{value}'. " +
            $"Falling back to the default. Valid values: {validValues}.");
    }

    private static void WarnIfInvalidNoticeLevel(string key, string? value)
    {
        if (!NoticeLevels.IsValid(value))
        {
            Console.Error.WriteLine(
                $"[agentrecall] warning: invalid {AgentRecallOptions.SectionName}.{key} '{value}'. " +
                $"Falling back to the default. Valid values: Silent, Normal, Verbose.");
        }
    }
}
