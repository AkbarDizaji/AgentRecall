using System.Security.Cryptography;
using System.Text;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Derives a deterministic turn correlation id from the data both hook surfaces share:
/// the working directory and the user's prompt. UserPromptSubmit (which records the rules
/// it used) and Stop/finalize-turn (which records what was captured) both compute the same
/// id independently, so a turn's retrieval and capture activity can be joined into one
/// summary without any shared, mutable, cross-process state.
///
/// When there is no prompt to key on, <see cref="Compute"/> returns <c>null</c> and the
/// summary falls back to a conservative time window instead.
/// </summary>
public static class TurnCorrelation
{
    /// <summary>Hex characters kept from the hash — short but collision-safe for a local log.</summary>
    private const int IdLength = 16;

    /// <summary>
    /// The deterministic turn id for a (cwd, prompt) pair, or <c>null</c> when the prompt is
    /// blank. The cwd is matched case-insensitively and the prompt is whitespace-normalised so
    /// the same logical turn produces the same id from either hook.
    /// </summary>
    public static string? Compute(string? cwd, string? prompt)
    {
        var normalizedPrompt = Normalize(prompt);
        if (normalizedPrompt.Length == 0)
        {
            return null;
        }

        var normalizedCwd = Normalize(cwd).ToLowerInvariant();
        var payload = normalizedCwd + "\n" + normalizedPrompt;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes)[..IdLength].ToLowerInvariant();
    }

    /// <summary>Trims and collapses internal runs of whitespace to a single space.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}
