using System.Text;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// Turns a document title into a filesystem-safe slug for <c>agentrecall document write</c>'s
/// auto-generated filenames. Pure and deterministic — no timestamp of its own; the caller
/// composes the date prefix.
/// </summary>
public static class DocSlug
{
    /// <summary>Used when a title has no alphanumeric content at all.</summary>
    public const string Fallback = "untitled";

    /// <summary>
    /// Lowercases <paramref name="title"/>, collapses every run of non-alphanumeric characters
    /// into a single <c>-</c>, trims leading/trailing dashes, and caps the result at
    /// <paramref name="maxLength"/> characters. Returns <see cref="Fallback"/> when the title
    /// carries no alphanumeric content (e.g. all punctuation or emoji).
    /// </summary>
    public static string Slugify(string? title, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Fallback;
        }

        var sb = new StringBuilder(title.Length);
        var lastWasDash = false;
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? Fallback : slug;
    }
}
