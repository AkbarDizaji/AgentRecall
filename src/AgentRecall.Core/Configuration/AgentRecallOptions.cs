namespace AgentRecall.Core.Configuration;

/// <summary>
/// Strongly-typed configuration for AgentRecall. Bound from configuration
/// sources (JSON file, environment variables) by the infrastructure layer.
/// </summary>
public sealed class AgentRecallOptions
{
    /// <summary>The configuration section name these options bind from.</summary>
    public const string SectionName = "AgentRecall";

    /// <summary>Default SQLite database file name within the data directory.</summary>
    public const string DefaultDatabaseFileName = "agentrecall.db";

    /// <summary>
    /// Directory where AgentRecall stores local data. Defaults to a folder
    /// under the user's home directory.
    /// </summary>
    public string DataDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentrecall");

    /// <summary>
    /// SQLite database file name, resolved relative to <see cref="DataDirectory"/>.
    /// </summary>
    public string DatabaseFileName { get; set; } = DefaultDatabaseFileName;

    /// <summary>Minimum log level to emit. Defaults to "Information".</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>The absolute path to the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
}
