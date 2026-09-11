using System.Reflection;
using System.Text.RegularExpressions;

namespace AgentRecall.Cli.ClaudeCode;

/// <summary>What keeping a project's guidance block up to date did to the file.</summary>
public enum GuidanceOutcome
{
    /// <summary>The file had no AgentRecall block; one was appended.</summary>
    Appended,

    /// <summary>An older block was refreshed in place.</summary>
    Updated,

    /// <summary>The block was already current; the file was left untouched.</summary>
    AlreadyPresent,

    /// <summary>The file did not exist; it was created with the block.</summary>
    Created,
}

/// <summary>
/// The standing guidance AgentRecall keeps in a project's <c>CLAUDE.md</c>: the agent behaviour the
/// hooks cannot express on their own, since a hook can inject context but only the agent can judge
/// a turn, report how a rule fared, or answer a capture question from recorded state.
///
/// The document itself is a Markdown file compiled in as an embedded resource rather than a string
/// literal. It is five hundred lines of prose, and as a literal it sat inside the dev-container
/// scaffolder — a type named for something else entirely — where editing a sentence recompiled the
/// scaffolding logic, every brace in its JSON examples had to survive raw-string interpolation, and
/// no reviewer could read it as the document it is. Only the parts that genuinely vary stay dynamic,
/// as <c>{{token}}</c> placeholders substituted here.
/// </summary>
public static partial class ClaudeMdGuidance
{
    /// <summary>Path, relative to the project root, of the agent guidance file.</summary>
    public const string RelativePath = "CLAUDE.md";

    /// <summary>
    /// Heading that marks the AgentRecall block. It is how the block is found for an in-place
    /// refresh, so the document must open with exactly this line — a test holds the two together.
    /// </summary>
    public const string Heading = "## Memory (AgentRecall)";

    /// <summary>The embedded document's resource name, as the compiler derives it from the path.</summary>
    private const string ResourceName = "AgentRecall.Cli.ClaudeCode.AgentGuidance.md";

    /// <summary>Placeholder for the contract these instructions were written for.</summary>
    private const string ContractToken = "{{contract-marker}}";

    private static readonly Lazy<string> Document = new(Load, isThreadSafe: true);

    /// <summary>The guidance block, ready to write into a project's <c>CLAUDE.md</c>.</summary>
    public static string Text => Document.Value;

    /// <summary>
    /// Ensures <c>CLAUDE.md</c> contains the current guidance block. Appends it when absent, and —
    /// crucially — refreshes an older block <em>in place</em> when its content has drifted, so
    /// re-running init upgrades the behaviour contract without ever duplicating the block.
    /// Idempotent: an up-to-date block (and the rest of the file) is left untouched.
    /// </summary>
    public static GuidanceOutcome Ensure(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var path = Path.Combine(projectRoot, RelativePath);
        var guidance = Text;

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var start = existing.IndexOf(Heading, StringComparison.Ordinal);
            if (start >= 0)
            {
                // The block runs from its heading to the next top-level (## ) heading or end of
                // file. The guidance itself uses only ### subheadings, so the next "## " marks
                // where the user's own content resumes.
                var end = NextTopLevelHeadingIndex(existing, start + Heading.Length);

                var before = existing[..start];
                var after = existing[end..];
                var currentBlock = existing[start..end];

                // Already up to date: leave the whole file byte-for-byte unchanged.
                if (currentBlock.TrimEnd() == guidance.TrimEnd())
                {
                    return GuidanceOutcome.AlreadyPresent;
                }

                // Refresh the block in place, preserving everything around it. The guidance ends
                // with a newline, so the following content stays separated.
                File.WriteAllText(path, before + guidance.TrimEnd() + "\n" + after);
                return GuidanceOutcome.Updated;
            }

            // Separate from prior content with a blank line, without rewriting it.
            var separator = existing.EndsWith('\n') ? "\n" : "\n\n";
            File.AppendAllText(path, separator + guidance);
            return GuidanceOutcome.Appended;
        }

        File.WriteAllText(path, guidance);
        return GuidanceOutcome.Created;
    }

    /// <summary>
    /// Finds where the block ends: the next level-2 heading, or end of file. <c>"\n## "</c> matches
    /// a level-2 heading only, since <c>"\n### "</c> differs at the third character, so the
    /// guidance's own subheadings are correctly skipped.
    /// </summary>
    private static int NextTopLevelHeadingIndex(string text, int from)
    {
        var index = text.IndexOf("\n## ", from, StringComparison.Ordinal);
        return index < 0 ? text.Length : index + 1; // start of the "## …" line
    }

    /// <summary>
    /// Reads the document out of the assembly and fills its placeholders. A missing resource is a
    /// build problem, not a runtime condition, so it throws rather than silently writing a project
    /// a truncated contract.
    /// </summary>
    private static string Load()
    {
        var assembly = typeof(ClaudeMdGuidance).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The guidance document '{ResourceName}' is not embedded in {assembly.GetName().Name}. "
                + "Check the EmbeddedResource item for ClaudeCode/AgentGuidance.md.");

        using var reader = new StreamReader(stream);
        var document = NormalizeLineEndings(reader.ReadToEnd())
            .Replace(ContractToken, Core.AgentContract.Marker, StringComparison.Ordinal);

        // A leftover placeholder would ship prose with braces where a value belongs.
        var leftover = PlaceholderPattern().Match(document);
        if (leftover.Success)
        {
            throw new InvalidOperationException(
                $"The guidance document has an unsubstituted placeholder '{leftover.Value}'.");
        }

        return document;
    }

    /// <summary>
    /// Guards against a checkout with CRLF endings producing a block that never matches the one on
    /// disk, which would make every <see cref="Ensure"/> report an update and rewrite the file.
    /// </summary>
    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex(@"\{\{[a-z0-9-]+\}\}")]
    private static partial Regex PlaceholderPattern();
}
