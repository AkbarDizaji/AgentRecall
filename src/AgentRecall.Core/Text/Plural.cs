namespace AgentRecall.Core.Text;

/// <summary>
/// Agreement for the counted nouns in notices and CLI output ("1 rule", "2 rules").
/// </summary>
public static class Plural
{
    /// <summary>
    /// <paramref name="singular"/> when <paramref name="count"/> is exactly one, otherwise its
    /// regular plural. Only regular nouns are counted in this output, so there is no irregular
    /// form to look up.
    /// </summary>
    public static string Of(int count, string singular) => count == 1 ? singular : singular + "s";
}
