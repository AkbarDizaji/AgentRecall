using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Services;

/// <summary>
/// Extracts individual review comments from a file. Understands the JSON shapes
/// produced by the GitHub CLI — an array of comment objects (<c>gh api …/comments</c>)
/// or an object with a <c>comments</c> array (<c>gh pr view --json comments</c>) —
/// and falls back to treating blank-line-separated blocks of plain text as
/// comments. Deterministic; no LLM.
/// </summary>
public static class PullRequestCommentParser
{
    public static IReadOnlyList<PullRequestComment> Parse(string? content)
    {
        var text = content?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text[0] is '[' or '{')
        {
            if (TryParseJson(text, out var fromJson))
            {
                return fromJson;
            }
        }

        return ParseText(text);
    }

    private static bool TryParseJson(string text, out IReadOnlyList<PullRequestComment> comments)
    {
        comments = [];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        var items = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["comments"] is JsonArray nested => nested,
            JsonObject single => [single],
            _ => null,
        };

        if (items is null)
        {
            return false;
        }

        var result = new List<PullRequestComment>();
        foreach (var item in items)
        {
            if (item is JsonObject node)
            {
                var body = AsString(node["body"]);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    result.Add(new PullRequestComment
                    {
                        Body = body.Trim(),
                        Author = AsString((node["user"] as JsonObject)?["login"])
                            ?? AsString((node["author"] as JsonObject)?["login"]),
                        Path = AsString(node["path"]),
                    });
                }
            }
            else if (item?.GetValueKind() == JsonValueKind.String)
            {
                var body = item.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    result.Add(new PullRequestComment { Body = body.Trim() });
                }
            }
        }

        comments = result;
        return true;
    }

    // Reads a field only when it is actually a JSON string; a wrong-typed value (a number
    // or bool where a string is expected) is treated as absent rather than throwing.
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IReadOnlyList<PullRequestComment> ParseText(string text)
    {
        // Blocks are separated by a blank line or a "---" rule.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = normalized
            .Split(["\n\n", "\n---\n", "\n---"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return blocks
            .Where(b => b.Length > 0)
            .Select(b => new PullRequestComment { Body = b })
            .ToList();
    }
}
