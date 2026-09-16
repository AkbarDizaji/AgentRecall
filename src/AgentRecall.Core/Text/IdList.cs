using System.Globalization;

namespace AgentRecall.Core.Text;

/// <summary>
/// Reads and writes the comma-separated id lists used to carry rule, candidate and
/// recommendation ids through the single-column storage on activities, retrievals and
/// turn records.
///
/// Both halves of the round-trip live here because they had drifted apart: one reader
/// culture-parsed and another did not, one dropped unparseable entries and another
/// turned them into <c>0</c> and then filtered on <c>&gt; 0</c>, so the same stored
/// string could yield different ids depending on which copy read it.
/// </summary>
public static class IdList
{
    /// <summary>
    /// The ids in <paramref name="csv"/>, in stored order, skipping blank and
    /// unparseable entries. Empty for null or blank input.
    /// </summary>
    public static IEnumerable<int> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                yield return id;
            }
        }
    }

    /// <summary>The stored form of <paramref name="ids"/>, readable back by <see cref="Parse"/>.</summary>
    public static string Join(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }
}
