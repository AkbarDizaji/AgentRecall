using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Mining;

/// <summary>
/// Default <see cref="ILessonMiningService"/>. Scans the event ledger for repeated
/// signals (feedback/PR corrections and build/test/lint failures recorded as
/// <see cref="RecallEventType.MistakeObserved"/>, plus rejected candidates recorded
/// as <see cref="RecallEventType.RuleRejected"/>), clusters them by a deterministic
/// normalized key, and proposes a <see cref="LessonCandidate"/> for any cluster that
/// repeats often enough and is not already covered by an Active/Promoted rule or a
/// previously rejected candidate. Idempotent: re-running updates candidates in place.
/// </summary>
public sealed class LessonMiningService : ILessonMiningService
{
    private static readonly RecallEventType[] SignalTypes =
        [RecallEventType.MistakeObserved, RecallEventType.RuleRejected];

    private readonly IRecallEventRepository _events;
    private readonly IRecallRuleRepository _rules;
    private readonly ILessonCandidateRepository _candidates;
    private readonly IRecallExtractor _extractor;
    private readonly IMemoryWorthinessClassifier _classifier;

    public LessonMiningService(
        IRecallEventRepository events,
        IRecallRuleRepository rules,
        ILessonCandidateRepository candidates,
        IRecallExtractor extractor,
        IMemoryWorthinessClassifier classifier)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public async Task<MiningResult> MineAsync(MiningOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MiningOptions();
        var threshold = Math.Max(2, options.MinOccurrences);

        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);
        var rules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _candidates.ListAsync(cancellationToken).ConfigureAwait(false);

        // Token sets of the in-force corpus, used to suppress already-covered lessons.
        var ruleKeys = rules
            .Where(r => !r.Deprecated && r.Status is RuleStatus.Active or RuleStatus.Promoted)
            .Select(r => Tokenize(LessonTextNormalizer.NormalizeKey(r.RuleText)))
            .Where(t => t.Count > 0)
            .ToList();

        // The latest candidate per key wins (terminal Accepted/Rejected status sticks).
        var candidateByKey = existing
            .GroupBy(c => c.NormalizedKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Id).First(), StringComparer.Ordinal);

        // Build clusters of repeated signals keyed by their normalized form.
        var clusters = new Dictionary<string, List<(int Id, string Text, DateTimeOffset At)>>(StringComparer.Ordinal);
        foreach (var ev in events.Where(e => SignalTypes.Contains(e.Type)))
        {
            var text = ExtractSignal(ev);
            var key = LessonTextNormalizer.NormalizeKey(text);
            if (key.Length == 0)
            {
                continue;
            }

            if (!clusters.TryGetValue(key, out var list))
            {
                clusters[key] = list = [];
            }

            list.Add((ev.Id, text, ev.CreatedAt));
        }

        var created = 0;
        var updated = 0;
        var suppressedByRule = 0;
        var suppressedByRejection = 0;
        var touched = new List<LessonCandidate>();

        // Deterministic order: by key.
        foreach (var (key, signals) in clusters.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (signals.Count < threshold)
            {
                continue;
            }

            var keyTokens = Tokenize(key);
            if (ruleKeys.Any(rk => Covers(rk, keyTokens)))
            {
                suppressedByRule++;
                continue;
            }

            if (candidateByKey.TryGetValue(key, out var prior) &&
                prior.Status is LessonCandidateStatus.Rejected)
            {
                suppressedByRejection++;
                continue;
            }

            if (candidateByKey.TryGetValue(key, out prior) &&
                prior.Status is LessonCandidateStatus.Accepted)
            {
                continue; // already became a rule
            }

            // Representative signal: longest text (most context), tie-break by event id.
            var representative = signals
                .OrderByDescending(s => s.Text.Length)
                .ThenBy(s => s.Id)
                .First().Text;

            var occurrence = signals.Count;
            var supporting = signals.Select(s => s.Id).OrderBy(id => id).ToList();
            var suggestedRule = ToSentence(representative);

            if (candidateByKey.TryGetValue(key, out var current) &&
                current.Status == LessonCandidateStatus.Suggested)
            {
                current.Title = BuildTitle(occurrence, suggestedRule);
                current.SuggestedRule = suggestedRule;
                current.Category = _classifier.Classify(representative).Category;
                current.OccurrenceCount = occurrence;
                current.Confidence = ConfidenceFor(occurrence);
                current.FirstSeenAt = signals.Min(s => s.At);
                current.LastSeenAt = signals.Max(s => s.At);
                current.SupportingEventIds = string.Join(",", supporting);
                current.UpdatedAt = DateTimeOffset.UtcNow;
                touched.Add(await _candidates.UpdateAsync(current, cancellationToken).ConfigureAwait(false));
                updated++;
            }
            else
            {
                var candidate = new LessonCandidate
                {
                    Title = BuildTitle(occurrence, suggestedRule),
                    SuggestedRule = suggestedRule,
                    Category = _classifier.Classify(representative).Category,
                    Status = LessonCandidateStatus.Suggested,
                    OccurrenceCount = occurrence,
                    Confidence = ConfidenceFor(occurrence),
                    FirstSeenAt = signals.Min(s => s.At),
                    LastSeenAt = signals.Max(s => s.At),
                    SupportingEventIds = string.Join(",", supporting),
                    NormalizedKey = key,
                };
                touched.Add(await _candidates.AddAsync(candidate, cancellationToken).ConfigureAwait(false));
                created++;
            }
        }

        var suggested = touched
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.OccurrenceCount)
            .ThenBy(c => c.Id)
            .ToList();

        return new MiningResult
        {
            Suggested = suggested,
            Created = created,
            Updated = updated,
            SuppressedByRule = suppressedByRule,
            SuppressedByRejection = suppressedByRejection,
        };
    }

    public async Task<LessonCandidate?> AcceptAsync(int candidateId, CancellationToken cancellationToken = default)
    {
        var candidate = await _candidates.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        if (candidate.Status == LessonCandidateStatus.Suggested)
        {
            // Build a real rule from the suggestion, then carry the mined category and
            // confidence. The rule is Active because a human explicitly accepted it —
            // mining never reaches this path on its own.
            var rule = _extractor.Extract(new FeedbackInput { Task = string.Empty, Feedback = candidate.SuggestedRule });
            rule.Status = RuleStatus.Active;
            rule.Category = candidate.Category;
            rule.Confidence = candidate.Confidence;
            await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

            candidate.Status = LessonCandidateStatus.Accepted;
            candidate.UpdatedAt = DateTimeOffset.UtcNow;
            await _candidates.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        return candidate;
    }

    public async Task<LessonCandidate?> RejectAsync(int candidateId, string reason, CancellationToken cancellationToken = default)
    {
        var candidate = await _candidates.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        candidate.Status = LessonCandidateStatus.Rejected;
        candidate.RejectedReason = string.IsNullOrWhiteSpace(reason) ? "Rejected." : reason.Trim();
        candidate.UpdatedAt = DateTimeOffset.UtcNow;
        await _candidates.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>Confidence from occurrence count: 3→0.60, 5→0.80, 10+→1.00.</summary>
    public static double ConfidenceFor(int occurrences)
    {
        var score = occurrences <= 5
            ? 0.60 + (occurrences - 3) * 0.10
            : 0.80 + (occurrences - 5) * 0.04;
        return Math.Round(Math.Clamp(score, 0.0, 1.0), 2);
    }

    /// <summary>Pulls the meaningful corrective text out of an event.</summary>
    private static string ExtractSignal(RecallEvent ev)
    {
        var details = ev.Details ?? string.Empty;

        // Feedback/PR events carry "Feedback: <text>" on their own line.
        foreach (var line in details.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Feedback:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Feedback:".Length..].Trim();
            }
        }

        // Failure events ("<kind> failure") carry the message in Details.
        if (!string.IsNullOrWhiteSpace(details))
        {
            var first = details.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !l.StartsWith("Rejected as", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }
        }

        return ev.Trigger ?? string.Empty;
    }

    private static string BuildTitle(int occurrence, string suggestedRule) =>
        $"Repeated lesson (×{occurrence}): {Truncate(suggestedRule, 60)}";

    private static string ToSentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var body = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return body.EndsWith('.') || body.EndsWith('!') || body.EndsWith('?') ? body : body + ".";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static HashSet<string> Tokenize(string normalizedKey) =>
        new(normalizedKey.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>True when a rule's tokens already cover the candidate (subset or ≥0.8 overlap).</summary>
    private static bool Covers(HashSet<string> ruleTokens, HashSet<string> candidateTokens)
    {
        if (candidateTokens.Count == 0)
        {
            return false;
        }

        if (candidateTokens.IsSubsetOf(ruleTokens))
        {
            return true;
        }

        var intersection = candidateTokens.Count(ruleTokens.Contains);
        var union = ruleTokens.Count + candidateTokens.Count - intersection;
        return union > 0 && (double)intersection / union >= 0.8;
    }
}
