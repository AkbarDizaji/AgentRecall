using System.Text;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Renders an <see cref="ActivityNotice"/> into the recognizable AgentRecall badge
/// format. The single source of truth for notice styling, so every surface (CLI,
/// hooks, status, MCP) reads identically.
///
/// Two render paths exist on purpose:
/// <list type="bullet">
/// <item><see cref="Render"/> is for human surfaces (CLI/status). At
/// <see cref="NoticeLevel.Verbose"/> it adds detail bullets.</item>
/// <item><see cref="RenderCompact"/> is for model-visible surfaces (hooks, MCP). It
/// is always a single line and never includes detail bullets or full rule text, so
/// it cannot bloat injected context.</item>
/// </list>
/// </summary>
public static class ActivityNoticeRenderer
{
    /// <summary>The badge every AgentRecall notice starts with.</summary>
    public const string Badge = "🧠 **AgentRecall:**";

    /// <summary>
    /// Renders a human-facing notice at the given level. Returns null when the level
    /// is <see cref="NoticeLevel.Silent"/> or there is nothing to say.
    /// </summary>
    public static string? Render(ActivityNotice notice, NoticeLevel level)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (level == NoticeLevel.Silent || string.IsNullOrWhiteSpace(notice.Summary))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append(Badge).Append(' ').Append(notice.Summary);

        if (level == NoticeLevel.Verbose && notice.Details.Count > 0)
        {
            foreach (var detail in notice.Details)
            {
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    sb.Append("\n- ").Append(detail.Trim());
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a compact, single-line notice for model-visible surfaces. Never emits
    /// detail bullets regardless of the notice's detail, so hook/context output stays
    /// small. Returns null when <paramref name="hookLevel"/> is
    /// <see cref="NoticeLevel.Silent"/> or there is nothing to say.
    /// </summary>
    public static string? RenderCompact(ActivityNotice notice, NoticeLevel hookLevel)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (NoticeLevels.ClampForHook(hookLevel) == NoticeLevel.Silent ||
            string.IsNullOrWhiteSpace(notice.Summary))
        {
            return null;
        }

        return $"{Badge} {notice.Summary}";
    }
}
