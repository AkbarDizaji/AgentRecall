namespace AgentRecall.Core.Domain;

/// <summary>
/// A known scope under which rules and events can be grouped — for example a
/// specific repository, language, or directory.
/// </summary>
public sealed class RecallScope
{
    public int Id { get; set; }

    public ScopeLevel Level { get; set; } = ScopeLevel.Global;

    /// <summary>The scope identifier (e.g. repo name, language, path).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional human-readable description of the scope.</summary>
    public string Description { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
