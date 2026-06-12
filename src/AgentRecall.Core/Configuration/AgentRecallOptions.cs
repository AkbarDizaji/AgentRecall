namespace AgentRecall.Core.Configuration;

/// <summary>
/// Strongly-typed configuration for AgentRecall. Bound from configuration
/// sources (JSON file, environment variables) by the infrastructure layer.
/// </summary>
public sealed class AgentRecallOptions
{
    /// <summary>The configuration section name these options bind from.</summary>
    public const string SectionName = "AgentRecall";

    /// <summary>
    /// Directory where AgentRecall stores local data. Defaults to a folder
    /// under the user's home directory. No storage is created in Phase 1.
    /// </summary>
    public string DataDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentrecall");

    /// <summary>Minimum log level to emit. Defaults to "Information".</summary>
    public string LogLevel { get; set; } = "Information";
}
