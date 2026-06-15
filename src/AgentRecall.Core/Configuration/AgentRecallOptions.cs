using AgentRecall.Core.Hooks;

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

    /// <summary>
    /// When true (the default), capturing feedback produces an <c>Active</c> rule
    /// straight away. Set it to false to keep captured rules <c>Pending</c> until
    /// they are explicitly approved.
    /// </summary>
    public bool AutoApproveFeedback { get; set; } = true;

    /// <summary>The absolute path to the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>
    /// Whether the UserPromptSubmit hook injects context. When false, the hook is a
    /// no-op even if it's wired into Claude Code's settings.
    /// </summary>
    public bool HookEnabled { get; set; } = true;

    /// <summary>
    /// Keywords that mark a prompt as software-development work worth injecting
    /// context for. Single words match whole-word; multi-word entries match as phrases.
    /// </summary>
    public string[] HookKeywords { get; set; } = PromptGate.DefaultKeywords;

    /// <summary>Maximum rules the hook injects (keeps the block small).</summary>
    public int HookMaxRules { get; set; } = 5;

    /// <summary>Whether the hook may inject Pending (unapproved) rules.</summary>
    public bool HookIncludePending { get; set; }
}
