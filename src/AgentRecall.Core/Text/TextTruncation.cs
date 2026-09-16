namespace AgentRecall.Core.Text;

/// <summary>
/// Shortens text for a one-line label — a rule summary in the activity log, a row in a
/// CLI table, a conflict notice. The ellipsis is part of the budget, so the result is
/// never wider than the cap the caller asked for.
/// </summary>
public static class TextTruncation
{
    /// <summary>
    /// <paramref name="value"/> capped at <paramref name="max"/> characters, ending in an
    /// ellipsis when it had to be cut. A null value and a non-positive cap both give an
    /// empty string, so a label built from an unset field never throws mid-render.
    /// </summary>
    public static string Ellipsize(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
